using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Tenancy;

namespace ProcuLink.Api.Middleware;

/// <summary>
/// Runs after authentication. For authenticated requests that carry a Clerk org id
/// (legacy v1 org_id claim, OR the v2 compact "o" object claim — see InvokeAsync),
/// looks up the internal Organisation UUID and stores it in HttpContext.Items so that
/// CurrentTenantService can serve it synchronously throughout the rest of the pipeline.
///
/// If the org_id is present but no matching DB record exists, the organisation is
/// auto-provisioned on the spot (pilot trial, 14-day window). This covers the first
/// login flow where Clerk creates an org before the back-end has ever seen it.
///
/// Auto-provisioning is throttled per client IP / email-domain by a lightweight
/// in-memory sliding window so a script that mints fresh Clerk identities cannot
/// create unlimited 14-day trials (trial-farming) or hammer the request-hot-path
/// write. A normal first login makes a single provisioning call and is never
/// affected. When the throttle trips the request may still RESOLVE an organisation that
/// already exists — a first page load is a burst, and a sibling request may have created
/// the row moments earlier — but it may never CREATE one. If there is no row to resolve,
/// the request continues without a tenant (downstream [Authorize] / tenant-scoped
/// controllers fail closed).
///
/// Unauthenticated requests (e.g. /health) pass through untouched.
///
/// <para>Database work here is retried on a transient fault and, if it still cannot complete,
/// answered as 503 + Retry-After rather than being left to surface downstream as an
/// authorization error. Production runs on Neon, which suspends when idle, so a cold start is
/// an ordinary event rather than an outage. See <see cref="IsTransientDatabaseFault"/>.</para>
///
/// <para><b>This middleware is where organisation query filters are armed</b> — the single site, for
/// both auth schemes. See <see cref="ApplyOrganisationScope"/>.</para>
/// </summary>
public sealed class TenantResolutionMiddleware
{
    /// <summary>
    /// Why tenant resolution itself reads unfiltered. This is the query that DISCOVERS which
    /// organisation a request belongs to, so by definition it runs before any tenant is known and
    /// cannot be scoped to one.
    /// </summary>
    internal const string BootstrapReason =
        "tenant resolution: looks up (and, on first login, adopts or provisions) the Organisation " +
        "row for a Clerk key. This is the query that discovers which organisation the request " +
        "belongs to, so it necessarily runs before any tenant is known.";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    // Auto-provision throttle: at most this many new orgs may be minted from a single
    // throttle key (IP + email-domain) within the rolling window. A legitimate new
    // user provisions exactly once, so this leaves normal first-login untouched while
    // stopping a script from farming trials / amplifying the hot-path write.
    private const int MaxProvisionsPerWindow = 5;
    private static readonly TimeSpan ProvisionWindow = TimeSpan.FromMinutes(10);

    // Retry-After on the 503 we answer when the database could not be reached. Two seconds is
    // longer than a Neon cold start normally takes and short enough that a retry still reads as a
    // slow page rather than an outage.
    private const string RetryAfterSeconds = "2";

    // Hard upper bound on the number of distinct throttle keys we keep state for. The
    // sliding window only protects against repeats from the SAME key; without this cap a
    // flood of DISTINCT keys (many IPs/email-domains — botnet, NAT churn, spoofed XFF)
    // would grow the dictionary without limit (memory-growth / unbounded-dictionary DoS).
    // When the cap is exceeded we evict the least-recently-touched entries. The cap is
    // far larger than any realistic burst of legitimate concurrent first-logins, so an
    // honest new user is never evicted before they finish their single provision.
    private const int MaxTrackedKeys = 10_000;

    // How often (by call count) to run the opportunistic stale-entry sweep. Sweeping on
    // every call would make the hot path O(n) in the number of tracked keys; gating it
    // keeps the amortised cost negligible while still reclaiming aged-out entries
    // promptly relative to the 10-minute window.
    private const int SweepEveryNCalls = 256;

    // Singleton state (middleware is registered via UseMiddleware<>, so instance
    // fields persist across requests). Keyed by throttle key → recent provision
    // timestamps within the window. Bounded two ways so it can never grow without limit:
    //  (1) a periodic sweep evicts entries whose window has fully aged out, and
    //  (2) a hard MaxTrackedKeys cap evicts the least-recently-touched entries.
    private readonly ConcurrentDictionary<string, ProvisionWindowState> _provisionWindows = new();

    // Drives the periodic sweep cadence; incremented on every reservation attempt.
    private int _reserveCalls;

    // Allows tests to control the clock; defaults to UTC now.
    private readonly Func<DateTime> _utcNow;

    public TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
        : this(next, logger, utcNow: null)
    {
    }

    // Test seam: inject a clock to exercise the sliding window deterministically.
    internal TenantResolutionMiddleware(
        RequestDelegate next,
        ILogger<TenantResolutionMiddleware> logger,
        Func<DateTime>? utcNow)
    {
        _next = next;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <param name="requestDb">
    /// The REQUEST-scoped context — the one the controller and every service it calls will share.
    /// This middleware never queries it, because a DbContext resolves its model on first use and a
    /// context that has already queried can no longer be scoped (see
    /// <see cref="ProcuLinkDbContext.ScopeToOrganisation"/>). Tenant resolution below therefore runs
    /// on its own short-lived context, leaving this one pristine and armable.
    /// </param>
    /// <param name="dbOptions">
    /// Used to construct the short-lived bootstrap context. Registered by <c>AddDbContext</c>
    /// alongside the context itself, and already carries the configured provider and interceptors,
    /// so the bootstrap context is identical to the request one in everything but lifetime.
    /// </param>
    public async Task InvokeAsync(
        HttpContext context,
        ProcuLinkDbContext requestDb,
        IAnalyticsService analytics,
        DbContextOptions<ProcuLinkDbContext> dbOptions)
    {
        // Tenant resolution touches the database, and the database is Neon, which suspends when
        // idle. A transient fault here used to reach the client as whatever the failure happened
        // to look like downstream — most often UnauthorizedAccessException("Organisation not
        // resolved"), which reads as "you are not allowed in" when the truth was "the database
        // was still waking up". Answer honestly instead, and let the caller retry.
        try
        {
            await ResolveTenantAsync(context, analytics, dbOptions);
        }
        catch (Exception ex) when (IsTransientDatabaseFault(ex))
        {
            _logger.LogWarning(
                ex,
                "Tenant resolution could not reach the database after retries; answering 503.");

            // Fail closed and say so. We do NOT call _next: without a resolved tenant the request
            // must not proceed, and 503 + Retry-After is the accurate, retryable answer.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = RetryAfterSeconds;
            return;
        }

        ApplyOrganisationScope(context, requestDb);

        await _next(context);
    }

    /// <summary>
    /// The tenant-resolution work itself, split out of <see cref="InvokeAsync"/> so a transient
    /// database fault anywhere inside it has one place to be caught and turned into an honest 503.
    /// </summary>
    private async Task ResolveTenantAsync(
        HttpContext context,
        IAnalyticsService analytics,
        DbContextOptions<ProcuLinkDbContext> dbOptions)
    {
        var sub = context.User.FindFirst("sub")?.Value;

        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Constructed only for authenticated requests, so /health and other anonymous traffic
            // allocate nothing. EF opens no connection until a query actually runs.
            await using var db = new ProcuLinkDbContext(dbOptions)
                .UseCrossOrganisationScope(BootstrapReason);

            var clerkOrgId = context.User.FindFirst("org_id")?.Value;
            var orgSlug    = context.User.FindFirst("org_slug")?.Value;

            // Clerk v2 session tokens (claim "v":2) carry org info in a compact "o" claim
            // (a JSON object: { "id": "org_…", "rol": "...", "slg": "..." }) instead of the
            // legacy top-level org_id/org_slug. Fall back to it so org resolution works
            // against real prod tokens. (The .NET JWT handler stores an object claim as its
            // JSON string value.)
            if (string.IsNullOrEmpty(clerkOrgId))
            {
                var o = context.User.FindFirst("o")?.Value;
                if (!string.IsNullOrEmpty(o))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(o);
                        var root = doc.RootElement;
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (string.IsNullOrEmpty(clerkOrgId) && root.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                clerkOrgId = idEl.GetString();
                            if (string.IsNullOrEmpty(orgSlug) && root.TryGetProperty("slg", out var slgEl) && slgEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                orgSlug = slgEl.GetString();
                        }
                    }
                    catch (System.Text.Json.JsonException) { /* malformed o claim → treat as no org */ }
                }
            }

            if (!string.IsNullOrEmpty(clerkOrgId)
                && clerkOrgId.StartsWith("org_", StringComparison.Ordinal))
            {
                // A REAL Clerk organisation (org_…).
                var existingId = await ResolveOrgIdByClerkKeyAsync(db, clerkOrgId, context.RequestAborted);
                if (existingId is { } id)
                {
                    // (1a) Already provisioned/adopted → resolve.
                    context.Items[CurrentTenantService.Items.OrganisationId] = id;
                    StampRole(context, ClerkOrgRole.FromClaims(context.User));
                }
                else
                {
                    // (1b) Not found → adopt-or-provision.
                    await AdoptOrProvisionAsync(context, db, analytics, clerkOrgId, orgSlug, sub);
                }
            }
            else if (!string.IsNullOrEmpty(sub))
            {
                // (2) Sub-only login — NO active Clerk org (legacy/transition window).
                // The production base is 100% legacy "sub-keyed" orgs whose
                // clerk_org_id is the user's own Clerk user id ("user_…"). We SOFTEN
                // the resolve so those users keep working (no lockout) instead of a
                // hard cutover that would lock out every existing customer. We resolve
                // ONLY a pre-existing row keyed to this authenticated user's sub; we
                // NEVER provision a new sub-keyed tenant here (fail closed otherwise),
                // so this cannot mint per-user "Personal workspace" tenants.
                var legacyId = await ResolveOrgIdByClerkKeyAsync(db, sub, context.RequestAborted);
                if (legacyId is { } id)
                {
                    context.Items[CurrentTenantService.Items.OrganisationId] = id;

                    // BOOTSTRAP. This organisation's clerk_org_id IS this authenticated user's own
                    // Clerk user id — that is the only way the lookup above could have matched. It
                    // is their personal tenant, they are its sole member, and there is nobody else
                    // who could ever be its administrator. So they are one.
                    //
                    // This is NOT "no role claim → admit". A token with no role that resolves a REAL
                    // Clerk org (branch 1a/1b) is refused; the admission here rests on a matched
                    // clerk_org_id == sub, which is a determined fact about the resolved tenant, not
                    // an absence of information. Without it, adding this gate would lock every
                    // pre-existing customer out of their own billing, delivery config and API keys
                    // on the day it shipped: the production base is entirely sub-keyed orgs whose
                    // tokens carry no organisation, and therefore no role, at all.
                    StampRole(context, OrgRole.Admin);
                }
                // No legacy org for this sub → leave UNRESOLVED (fail closed). The
                // frontend org gate forces real org creation before tenant-scoped calls.
            }
        }
    }

    /// <summary>
    /// Arms the organisation query filters on the request-scoped context, so a query written
    /// WITHOUT an explicit <c>.Where(x =&gt; x.OrgId == orgId)</c> still cannot return another
    /// organisation's rows.
    ///
    /// <para><b>One arming site, both auth schemes.</b> The organisation id is read from
    /// <c>HttpContext.Items</c> rather than from this middleware's own resolution, because the JWT
    /// path writes it above and <c>ApiKeyAuthHandler</c> writes the same key during authentication.
    /// Reading the resolved value covers both without a second arming site to keep in sync — and an
    /// auth scheme added later that publishes the tenant the same way is armed for free.</para>
    ///
    /// <para><b>Requests with no resolved tenant are left unscoped</b>, which is what they were
    /// before: anonymous traffic, a throttled provision, and a sub with no legacy org all fall
    /// through here. None of them reach tenant data — downstream <c>[Authorize]</c> and the
    /// tenant-scoped controllers fail closed on a missing tenant — so arming would change nothing
    /// except to turn a 401 into a confusing empty 200.</para>
    /// </summary>
    private static void ApplyOrganisationScope(HttpContext context, ProcuLinkDbContext requestDb)
    {
        // A deliberately cross-tenant endpoint must NOT be armed, or it silently truncates to the
        // caller's own organisation and reports the result as the whole system's. Endpoint metadata
        // is populated by routing, which WebApplication inserts at the head of the pipeline — ahead
        // of this middleware. That ordering is load-bearing (a null endpoint here would arm the
        // admin surface), so it is proved over the real pipeline by
        // TheAdminSurface_StillSeesEveryOrganisation rather than assumed.
        var crossOrg = context.GetEndpoint()?.Metadata.GetMetadata<CrossOrganisationReadAttribute>();
        if (crossOrg is not null)
        {
            requestDb.UseCrossOrganisationScope(crossOrg.Reason);
            return;
        }

        if (context.Items.TryGetValue(CurrentTenantService.Items.OrganisationId, out var resolved)
            && resolved is Guid orgId
            && orgId != Guid.Empty)
        {
            requestDb.ScopeToOrganisation(orgId);
        }
    }

    /// <summary>
    /// Resolves the internal Organisation UUID for a Clerk key (org_… or, for legacy
    /// rows, the user's user_… sub). Returns null when no row matches. AsNoTracking +
    /// projection: read-only, so the AsNoTracking no-op-mutation trap does not apply.
    /// </summary>
    private static async Task<Guid?> ResolveOrgIdByClerkKeyAsync(
        ProcuLinkDbContext db, string clerkKey, CancellationToken ct)
    {
        // Retried on a transient fault: this is the FIRST query of the request, so it is the one
        // that pays a Neon cold start, and every branch of tenant resolution goes through it.
        var org = await WithTransientRetryAsync(
            () => db.Organisations
                .AsNoTracking()
                .Where(o => o.ClerkOrgId == clerkKey)
                .Select(o => new { o.Id })
                .FirstOrDefaultAsync(ct),
            ct);
        return org?.Id;
    }

    /// <summary>
    /// A real Clerk org id (org_…) with no matching row yet. Two paths:
    ///   ADOPT — if the CURRENTLY authenticated user already owns a legacy personal
    ///   tenant (ClerkOrgId == their own sub), re-key THAT row to the new org_ id
    ///   (same row Id, data, plan, Stripe, slug). This is the production cutover:
    ///   every legacy org is sub-keyed, so a user's first login under a real Clerk
    ///   org adopts their existing data instead of stranding it. Not throttled — it
    ///   only re-keys the user's OWN existing row (not trial-farming).
    ///
    ///   PROVISION — otherwise mint a fresh pilot trial org via the existing throttled
    ///   path (org_created).
    ///
    /// SECURITY: adopt re-keys ONLY the row whose ClerkOrgId == sub of the validated
    /// JWT. A user can therefore only adopt their OWN personal tenant; there is no way
    /// to attach to another tenant by any other field.
    /// </summary>
    private async Task AdoptOrProvisionAsync(
        HttpContext context, ProcuLinkDbContext db, IAnalyticsService analytics,
        string clerkOrgId, string? orgSlug, string? sub)
    {
        var ct = context.RequestAborted;

        // ADOPT: load the authenticated user's OWN legacy personal tenant as a TRACKED
        // entity (NOT AsNoTracking — we are about to mutate + SaveChanges) and re-key it.
        if (!string.IsNullOrEmpty(sub))
        {
            var personal = await db.Organisations
                .FirstOrDefaultAsync(o => o.ClerkOrgId == sub, ct);
            if (personal is not null)
            {
                personal.ClerkOrgId = clerkOrgId;
                try
                {
                    await WithTransientRetryAsync(() => db.SaveChangesAsync(ct), ct);
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    // Race: the new org_ row was created concurrently (unique index on
                    // clerk_org_id). Drop our re-key attempt and resolve the winner.
                    db.Entry(personal).State = EntityState.Detached;
                    var winnerId = await ResolveOrgIdByClerkKeyAsync(db, clerkOrgId, ct);
                    if (winnerId is { } wid)
                    {
                        context.Items[CurrentTenantService.Items.OrganisationId] = wid;
                        StampRole(context, ClerkOrgRole.FromClaims(context.User));
                    }
                    return;
                }

                _logger.LogInformation(
                    "Adopted legacy personal tenant (Sub={Sub}) into Clerk org {ClerkOrgId} (OrgId={OrgId}).",
                    sub, clerkOrgId, personal.Id);

                await analytics.CaptureAsync(
                    organisationId: personal.Id,
                    userId: sub,
                    eventName: "org_adopted",
                    properties: new Dictionary<string, object?>
                    {
                        ["from"] = "personal_workspace",
                    },
                    ct: ct);

                context.Items[CurrentTenantService.Items.OrganisationId] = personal.Id;

                // The adopting user re-keyed their OWN personal tenant into a real Clerk org, so
                // from here on the org's roles are Clerk's to state. Read the claim rather than
                // carrying the personal tenant's implicit ownership forward: they created the org
                // and Clerk makes a creator org:admin, so the claim says the same thing — and if it
                // ever does not, the claim is the answer that stays true after they add members.
                StampRole(context, ClerkOrgRole.FromClaims(context.User));
                return;
            }
        }

        // PROVISION FRESH: throttle so a script minting fresh Clerk identities cannot
        // farm unlimited trials or amplify the hot-path write. Legitimate first login
        // provisions exactly once and is unaffected.
        var throttleKey = BuildThrottleKey(context, clerkOrgId);
        if (!TryReserveProvision(throttleKey))
        {
            // A throttled request must not MINT an organisation. It may still RESOLVE one that
            // already exists, and that distinction is the whole fix here.
            //
            // The lookup at the top of InvokeAsync found no row, which is the only reason we are
            // on this path at all. But a first page load is not one request — it is a burst of
            // them, and on a cold database the winner's INSERT can take seconds to commit. Every
            // sibling in that burst therefore arrives here having also seen "no row", spends a
            // reservation, and the ones past the cap were failed closed. Downstream that surfaced
            // as UnauthorizedAccessException("Organisation not resolved") — an authorization
            // error on the first screen a new customer ever sees, caused by a slow database.
            //
            // So re-read before giving up. If the row exists now, a sibling created it and this
            // request simply belongs to it. Nothing is created on this path, so the
            // anti-trial-farming property the throttle exists for is untouched: a script minting
            // fresh Clerk identities still finds no row to resolve and is still refused.
            var settledId = await ResolveOrgIdByClerkKeyAsync(db, clerkOrgId, ct);
            if (settledId is { } sid)
            {
                context.Items[CurrentTenantService.Items.OrganisationId] = sid;
                StampRole(context, ClerkOrgRole.FromClaims(context.User));
                return;
            }

            _logger.LogWarning(
                "Auto-provision throttled for TenantKey={ClerkOrgId} (ThrottleKey={ThrottleKey}); " +
                "more than {Max} new orgs from this key within {Minutes} min.",
                clerkOrgId, throttleKey, MaxProvisionsPerWindow, ProvisionWindow.TotalMinutes);
            // Fail closed: downstream [Authorize] / tenant-scoped controllers reject it.
            return;
        }

        var now = _utcNow();
        var orgName = orgSlug ?? clerkOrgId;
        var newOrg = new Organisation
        {
            Id             = Guid.NewGuid(),
            ClerkOrgId     = clerkOrgId,
            Name           = orgName,
            Slug           = GenerateSlug(orgName),
            Plan           = "pilot",
            AccountStatus  = "trialing",
            CreatedAt      = now,
            TrialStartedAt = now,
            TrialEndsAt    = now.AddDays(14),
        };

        db.Organisations.Add(newOrg);
        try
        {
            // Retried on a transient fault. Safe to repeat: the entity is still Added, so a retry
            // re-sends the same INSERT, and if the first one actually landed before the connection
            // dropped the retry comes back as a unique violation — which the catch below already
            // knows how to settle by resolving the winning row.
            await WithTransientRetryAsync(() => db.SaveChangesAsync(ct), ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race: a concurrent first-request for the same org_ won the unique index.
            // Drop our insert and resolve the winning row.
            db.Entry(newOrg).State = EntityState.Detached;
            var winnerId = await ResolveOrgIdByClerkKeyAsync(db, clerkOrgId, ct);
            if (winnerId is { } wid)
            {
                context.Items[CurrentTenantService.Items.OrganisationId] = wid;
                StampRole(context, ClerkOrgRole.FromClaims(context.User));
            }
            return;
        }

        _logger.LogInformation(
            "Auto-provisioned organisation '{Name}' (TenantKey={ClerkOrgId}).",
            newOrg.Name, clerkOrgId);

        await analytics.CaptureAsync(
            organisationId: newOrg.Id,
            userId: sub,
            eventName: "org_created",
            properties: new Dictionary<string, object?>
            {
                ["plan"] = "pilot",
                ["created_via"] = "signup_flow",
            },
            ct: ct);

        context.Items[CurrentTenantService.Items.OrganisationId] = newOrg.Id;
        StampRole(context, ClerkOrgRole.FromClaims(context.User));
    }

    /// <summary>
    /// Records the caller's role for this request, for <see cref="RequireOrgAdminAttribute"/> to read.
    ///
    /// <para>Called ONLY where a tenant genuinely resolved. A request that leaves here without a
    /// stamp carries no role at all, which <see cref="OrgRole.Unknown"/> represents and the gate
    /// refuses — so forgetting to stamp closes a door rather than opening one.</para>
    /// </summary>
    private static void StampRole(HttpContext context, OrgRole role) =>
        context.Items[ClerkOrgRole.ItemsKey] = role;

    /// <summary>
    /// True when a <see cref="DbUpdateException"/> was caused by a Postgres unique-index
    /// violation (SQLSTATE 23505) — i.e. a concurrent request won the race for the same
    /// clerk_org_id. We walk the inner-exception chain and duck-type the SqlState property
    /// (Npgsql's <c>PostgresException.SqlState</c>) so this assembly takes no hard Npgsql
    /// dependency. Any other DbUpdateException is genuine and must propagate.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var sqlState = e.GetType()
                .GetProperty("SqlState")?
                .GetValue(e) as string;
            if (sqlState == "23505")
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when an exception is a TRANSIENT database fault — the connection failed, timed out, or
    /// the server was still waking — as opposed to a fault that will fail identically on a retry.
    ///
    /// <para>We run on Neon, whose compute auto-suspends when idle. Low-traffic periods are
    /// therefore normal, and the first request after one pays a cold start that can exceed the
    /// connection timeout. Nothing in this application retried it: there is no
    /// <c>EnableRetryOnFailure</c> execution strategy configured, and one cannot simply be turned
    /// on, because eight production call sites open explicit transactions and EF's retrying
    /// strategy refuses user-initiated transactions. So resilience is applied here, at the one
    /// place a cold start is most likely to be met and most damaging — the first request of a
    /// brand-new organisation.</para>
    ///
    /// <para>Detection duck-types Npgsql rather than referencing it, for the same reason
    /// <see cref="IsUniqueViolation"/> does: this assembly takes no hard Npgsql dependency. We read
    /// <c>NpgsqlException.IsTransient</c>, which Npgsql itself sets for connection-level failures,
    /// and additionally accept SQLSTATE class 08 (connection exception). A unique violation is not
    /// transient by either test, so the race path above keeps its own distinct handling.</para>
    /// </summary>
    private static bool IsTransientDatabaseFault(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is TimeoutException)
                return true;

            var type = e.GetType();

            if (type.GetProperty("IsTransient")?.GetValue(e) is true)
                return true;

            // SQLSTATE class 08 — "connection exception" (08000, 08003, 08006, 08001, 08004).
            if (type.GetProperty("SqlState")?.GetValue(e) is string s
                && s.StartsWith("08", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Runs a database operation, retrying a bounded number of times on a transient fault
    /// (<see cref="IsTransientDatabaseFault"/>) with a short quadratic backoff.
    ///
    /// <para>Three attempts at 150 ms then 600 ms adds at most ~750 ms to a request that would
    /// otherwise have failed outright, which is the right trade for a Neon cold start that
    /// resolves in about a second. A non-transient fault is rethrown on the first attempt, so a
    /// genuine bug still fails fast and loudly rather than being retried into a timeout.</para>
    /// </summary>
    private static async Task<T> WithTransientRetryAsync<T>(
        Func<Task<T>> operation, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex)
                when (attempt < maxAttempts && IsTransientDatabaseFault(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt * attempt), ct);
            }
        }
    }

    /// <summary>
    /// Generates a unique kebab-case slug from the org name.
    /// Appends a 4-char random suffix to ensure uniqueness without a DB round-trip.
    /// </summary>
    private static string GenerateSlug(string name)
    {
        var slug = new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());
        // Collapse consecutive dashes
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "org";
        // 4-char random suffix for uniqueness
        slug += "-" + Guid.NewGuid().ToString("N")[..4];
        return slug;
    }

    /// <summary>
    /// Builds the abuse-throttle key. Primary axis is the client IP (a script minting
    /// Clerk identities still originates from a bounded set of addresses); the email
    /// domain (from an <c>email</c> claim, when present) is folded in so shared NAT/proxy
    /// egress doesn't lump unrelated tenants together too aggressively. Falls back to the
    /// tenant key when no IP is available (e.g. in tests / unusual hosting).
    /// </summary>
    private static string BuildThrottleKey(HttpContext context, string clerkOrgId)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();

        var email = context.User.FindFirst("email")?.Value;
        var domain = string.Empty;
        if (!string.IsNullOrEmpty(email))
        {
            var at = email.LastIndexOf('@');
            if (at >= 0 && at < email.Length - 1)
                domain = email[(at + 1)..].ToLowerInvariant();
        }

        if (!string.IsNullOrEmpty(ip))
            return string.IsNullOrEmpty(domain) ? $"ip:{ip}" : $"ip:{ip}|dom:{domain}";

        // No IP context: fall back to the tenant key so we still bound a single key,
        // without lumping every keyless request into one global bucket.
        return $"tk:{clerkOrgId}";
    }

    /// <summary>
    /// Sliding-window reservation. Returns true and records a provision if the key is
    /// under the limit for the current window; false if it has hit the cap. Thread-safe
    /// and self-pruning so the dictionary stays bounded: a periodic sweep evicts entries
    /// whose window has fully aged out, and a hard size cap evicts the least-recently-
    /// touched entries so distinct-key floods cannot grow the store without limit.
    /// </summary>
    private bool TryReserveProvision(string throttleKey)
    {
        var now = _utcNow();

        // Bound the dictionary BEFORE touching this key, so eviction work is amortised
        // across calls rather than left to grow until memory pressure.
        BoundStore(now);

        var state = _provisionWindows.GetOrAdd(throttleKey, _ => new ProvisionWindowState());

        lock (state.Gate)
        {
            state.LastTouched = now;

            // Drop timestamps that have aged out of the window.
            state.Timestamps.RemoveAll(t => now - t >= ProvisionWindow);

            if (state.Timestamps.Count >= MaxProvisionsPerWindow)
                return false;

            state.Timestamps.Add(now);
            return true;
        }
    }

    /// <summary>
    /// Keeps <see cref="_provisionWindows"/> from growing without limit under a flood of
    /// distinct throttle keys. Two complementary mechanisms:
    ///   1. A periodic sweep (every <see cref="SweepEveryNCalls"/> calls) removes entries
    ///      whose sliding window has fully aged out — these can never throttle again, so
    ///      retaining them only wastes memory.
    ///   2. A hard <see cref="MaxTrackedKeys"/> cap: if the store is still over budget
    ///      after sweeping (many keys active within the window at once), evict the
    ///      least-recently-touched entries down to the cap.
    /// Both are safe to interleave with concurrent reservations: a key removed here is
    /// simply re-created by the next <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/>,
    /// at worst resetting that key's window — acceptable for an abuse throttle.
    /// </summary>
    private void BoundStore(DateTime now)
    {
        var calls = Interlocked.Increment(ref _reserveCalls);
        var dueForSweep = calls % SweepEveryNCalls == 0;
        var overCap = _provisionWindows.Count > MaxTrackedKeys;

        if (!dueForSweep && !overCap)
            return;

        // 1. Evict fully-expired entries (no live timestamps left in the window).
        foreach (var kvp in _provisionWindows)
        {
            var state = kvp.Value;
            bool expired;
            lock (state.Gate)
            {
                state.Timestamps.RemoveAll(t => now - t >= ProvisionWindow);
                expired = state.Timestamps.Count == 0;
            }

            if (expired)
                _provisionWindows.TryRemove(kvp.Key, out _);
        }

        // 2. If still over the hard cap (many keys active simultaneously), evict the
        //    least-recently-touched entries until back within budget.
        var excess = _provisionWindows.Count - MaxTrackedKeys;
        if (excess <= 0)
            return;

        var victims = _provisionWindows
            .OrderBy(kvp =>
            {
                lock (kvp.Value.Gate) { return kvp.Value.LastTouched; }
            })
            .Take(excess)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in victims)
            _provisionWindows.TryRemove(key, out _);
    }

    /// <summary>
    /// Test seam: current number of distinct throttle keys retained in the store. Lets
    /// tests assert the dictionary stays bounded under a flood of distinct keys.
    /// </summary>
    internal int TrackedKeyCount => _provisionWindows.Count;

    private sealed class ProvisionWindowState
    {
        public object Gate { get; } = new();
        public List<DateTime> Timestamps { get; } = new();

        // Last time this key was reserved/swept; drives least-recently-touched eviction.
        public DateTime LastTouched { get; set; }
    }
}
