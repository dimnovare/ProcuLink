using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

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
/// affected. When the throttle trips, the request continues without a resolved
/// tenant (downstream [Authorize] / tenant-scoped controllers fail closed).
///
/// Unauthenticated requests (e.g. /health) pass through untouched.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    // Auto-provision throttle: at most this many new orgs may be minted from a single
    // throttle key (IP + email-domain) within the rolling window. A legitimate new
    // user provisions exactly once, so this leaves normal first-login untouched while
    // stopping a script from farming trials / amplifying the hot-path write.
    private const int MaxProvisionsPerWindow = 5;
    private static readonly TimeSpan ProvisionWindow = TimeSpan.FromMinutes(10);

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

    public async Task InvokeAsync(HttpContext context, ProcuLinkDbContext db, IAnalyticsService analytics)
    {
        var sub = context.User.FindFirst("sub")?.Value;

        if (context.User.Identity?.IsAuthenticated == true)
        {
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
                    context.Items[CurrentTenantService.Items.OrganisationId] = id;
                // No legacy org for this sub → leave UNRESOLVED (fail closed). The
                // frontend org gate forces real org creation before tenant-scoped calls.
            }
        }

        await _next(context);
    }

    /// <summary>
    /// Resolves the internal Organisation UUID for a Clerk key (org_… or, for legacy
    /// rows, the user's user_… sub). Returns null when no row matches. AsNoTracking +
    /// projection: read-only, so the AsNoTracking no-op-mutation trap does not apply.
    /// </summary>
    private static async Task<Guid?> ResolveOrgIdByClerkKeyAsync(
        ProcuLinkDbContext db, string clerkKey, CancellationToken ct)
    {
        var org = await db.Organisations
            .AsNoTracking()
            .Where(o => o.ClerkOrgId == clerkKey)
            .Select(o => new { o.Id })
            .FirstOrDefaultAsync(ct);
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
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    // Race: the new org_ row was created concurrently (unique index on
                    // clerk_org_id). Drop our re-key attempt and resolve the winner.
                    db.Entry(personal).State = EntityState.Detached;
                    var winnerId = await ResolveOrgIdByClerkKeyAsync(db, clerkOrgId, ct);
                    if (winnerId is { } wid)
                        context.Items[CurrentTenantService.Items.OrganisationId] = wid;
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
                return;
            }
        }

        // PROVISION FRESH: throttle so a script minting fresh Clerk identities cannot
        // farm unlimited trials or amplify the hot-path write. Legitimate first login
        // provisions exactly once and is unaffected.
        var throttleKey = BuildThrottleKey(context, clerkOrgId);
        if (!TryReserveProvision(throttleKey))
        {
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
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race: a concurrent first-request for the same org_ won the unique index.
            // Drop our insert and resolve the winning row.
            db.Entry(newOrg).State = EntityState.Detached;
            var winnerId = await ResolveOrgIdByClerkKeyAsync(db, clerkOrgId, ct);
            if (winnerId is { } wid)
                context.Items[CurrentTenantService.Items.OrganisationId] = wid;
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
    }

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
