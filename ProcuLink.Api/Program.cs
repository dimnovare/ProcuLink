using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Sentry;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Middleware;
using ProcuLink.Api.Services;
using ProcuLink.Api.Services.StarterTemplates;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Erp;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Core.Services.Ocr;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ai;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Email;
using ProcuLink.Infrastructure.Services.Erp;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Ocr;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;
using ProcuLink.Infrastructure.Storage;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Scalar.AspNetCore;
using Stripe;

var builder = WebApplication.CreateBuilder(args);

// ── Sentry error tracking (G6) — no-op when DSN is absent ────────────────
builder.WebHost.UseSentry(o =>
{
    o.Dsn = builder.Configuration["Sentry:Dsn"] ?? string.Empty;
    o.TracesSampleRate = 0.1; // 10 % of transactions
    o.MinimumBreadcrumbLevel = Microsoft.Extensions.Logging.LogLevel.Information;
    o.MinimumEventLevel = Microsoft.Extensions.Logging.LogLevel.Error;

    // Scrub the inbound-email webhook's ?token= secret out of any captured
    // request URL / query string before it leaves the process. Postmark can only
    // pass the shared secret in the webhook URL, so a sampled transaction would
    // otherwise persist it to Sentry. (Sentry already redacts the Authorization
    // header used by the Basic-Auth alternative.)
    o.SetBeforeSend((e, _) => { ScrubRequest(e.Request); return e; });
    o.SetBeforeSendTransaction((t, _) => { ScrubRequest(t.Request); return t; });

    static void ScrubRequest(SentryRequest? r)
    {
        if (r is null) return;
        if (!string.IsNullOrEmpty(r.Url)) r.Url = ScrubToken(r.Url);
        if (!string.IsNullOrEmpty(r.QueryString)) r.QueryString = ScrubToken(r.QueryString);
    }

    static string ScrubToken(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"(?i)(token=)[^&\s]*", "$1[redacted]");
});

// ── Stripe SDK ────────────────────────────────────────────────────────────
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"] ?? string.Empty;
// API-version determinism: Stripe.net pins outgoing calls to the API version its
// release was compiled against (this is Stripe.net 51.1.0 — the package version in
// ProcuLink.Api.csproj IS the pin), so outgoing requests do NOT drift with the
// account-default version. We deliberately do NOT set StripeConfiguration.ApiVersion
// to a hand-typed string: forcing a version newer/older than the SDK's models would
// risk webhook/response deserialization drift. The one thing to keep in lock-step is
// the Stripe webhook endpoint's API version in the dashboard — see the Stripe go-live
// runbook (docs/deployment). AppInfo just tags ProcuLink in Stripe's request logs.
StripeConfiguration.AppInfo = new AppInfo { Name = "ProcuLink", Url = "https://proculink.eu" };

// ── Database ───────────────────────────────────────────────────────────────
// Cap the Npgsql pool in code so it applies regardless of the env-injected
// connection string (prod string comes from Railway env). Without an explicit
// ceiling each process would use Npgsql's default Maximum Pool Size of 100, so
// API + Worker = two ~100-conn pools, well past Neon's ~100-connection cap.
// API gets 30 (it serves HTTP + enqueues Hangfire jobs); the Worker (WorkerCount=10)
// gets 20. ConnectionIdleLifetime returns idle pooled connections to Neon so the
// pool doesn't pin its ceiling. Existing settings in the string are preserved.
// P2 hardening: log-only order state-machine observer. Singleton interceptor attached to
// every DbContext, so all tracked Status writes flow through one observation point. It
// only logs warnings on unexpected transitions — it never blocks or mutates a save.
builder.Services.AddSingleton<ProcuLink.Infrastructure.Services.OrderStatusTransitionObserver>();
builder.Services.AddDbContext<ProcuLinkDbContext>((sp, options) =>
    // Read the connection string LAZILY here (per DbContext creation, after the host is
    // built) so WebApplicationFactory integration tests that override
    // ConnectionStrings:DefaultConnection are honoured. An eager read at builder-config
    // time would capture the pre-override (empty) value. The pool ceiling is applied via
    // BuildPooledConnectionString.
    options.UseNpgsql(BuildPooledConnectionString(
            builder.Configuration.GetConnectionString("DefaultConnection"), maxPoolSize: 30))
        .AddInterceptors(sp.GetRequiredService<ProcuLink.Infrastructure.Services.OrderStatusTransitionObserver>()));

static string? BuildPooledConnectionString(string? raw, int maxPoolSize)
{
    if (string.IsNullOrWhiteSpace(raw))
        return raw;

    var b = new Npgsql.NpgsqlConnectionStringBuilder(raw);
    // Only impose a pool ceiling when pooling is actually enabled. If a caller
    // explicitly disabled pooling (e.g. integration tests that run a background
    // MigrateAsync and must avoid connection multiplexing), leave the string as-is —
    // setting Max Pool Size with Pooling=false is meaningless and Npgsql treats the
    // builder as non-pooling, so we must not override the caller's intent.
    if (!b.Pooling)
        return raw;

    b.MaxPoolSize = maxPoolSize;
    b.ConnectionIdleLifetime = 60;   // seconds an idle pooled conn lives before close
    b.ConnectionPruningInterval = 10; // seconds between idle-pruning sweeps
    return b.ConnectionString;
}

// ── DataProtection ─────────────────────────────────────────────────────────
// Persist the key ring to Postgres so keys survive container restarts and are
// shared across multiple API instances (anti-forgery tokens, auth cookies,
// any IDataProtector consumer). Without this Railway logs:
//   "Storing keys in a directory '/root/.aspnet/DataProtection-Keys' that may
//    not be persisted outside of the container."
//
// SetApplicationName isolates this app from any sibling .NET app that might
// share the same DB in the future. Encryption-at-rest is layered on top via
// AesGcmXmlEncryptor when DataProtection:EncryptionKey is configured —
// otherwise the keys persist in clear XML in the DB (acceptable for dev).
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ProcuLinkDbContext>()
    .SetApplicationName("ProcuLink");

if (!string.IsNullOrWhiteSpace(builder.Configuration["DataProtection:EncryptionKey"]))
{
    builder.Services.AddTransient<AesGcmXmlDecryptor>();
    builder.Services.Configure<KeyManagementOptions>(options =>
    {
        options.XmlEncryptor = new AesGcmXmlEncryptor(builder.Configuration);
    });
}

// ── Authentication — Clerk JWT Bearer + API Key ───────────────────────────
// Authorized parties (Clerk `azp`) = this app's frontend origin(s), same source as CORS below.
// Binds session tokens to this application (see ClerkTokenValidation / OnTokenValidated).
var authorizedParties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "http://localhost:3000",   // Next.js default dev port
    "http://localhost:8082",   // ProcuLink frontend dev port
};
foreach (var origin in (builder.Configuration["Frontend:Url"] ?? string.Empty)
             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    authorizedParties.Add(origin);

var enableQaAuthBypass =
    builder.Environment.IsDevelopment()
    && string.Equals(
        builder.Configuration["PROCULINK_QA_BYPASS_AUTH"],
        "true",
        StringComparison.OrdinalIgnoreCase);

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = enableQaAuthBypass
        ? QaBypassAuthenticationHandler.SchemeName
        : JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = options.DefaultAuthenticateScheme;
});

if (enableQaAuthBypass)
{
    authBuilder.AddScheme<AuthenticationSchemeOptions, QaBypassAuthenticationHandler>(
        QaBypassAuthenticationHandler.SchemeName,
        _ => { });
}
else
{
    authBuilder.AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Clerk:Authority"];
        // Disable legacy claim-type mapping. Without this, JwtBearer renames "sub"
        // to ClaimTypes.NameIdentifier before claims reach HttpContext.User, which
        // breaks TenantResolutionMiddleware's `FindFirst("sub")` fallback.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            // Clerk session tokens carry `azp` (authorized party), not a standard `aud`, so
            // ASP.NET audience validation is off and the binding is enforced via `azp` below.
            ValidateAudience = false,
            NameClaimType = "sub",
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var azp = context.Principal?.FindFirst("azp")?.Value;
                if (!ClerkTokenValidation.IsAuthorizedParty(azp, authorizedParties))
                    context.Fail("Token 'azp' is not an authorized party for this application.");
                return Task.CompletedTask;
            },
        };
    });
}

authBuilder.AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>("ApiKey", _ => { });

builder.Services.AddAuthorization();

// ── Platform-admin allowlist (owner/admin cross-tenant surface) ────────────
// Reads Admin:UserIds / Admin:Emails (env Admin__UserIds / Admin__Emails).
// Consumed by [AdminOnly] on AdminController. Fails closed when both are unset.
builder.Services.AddSingleton<ProcuLink.Api.Auth.AdminAllowlist>();

// ── Forwarded headers — restore the real client IP behind the Railway edge ─
// Railway's edge proxy terminates TLS and forwards plain HTTP to this
// container, so without forwarded-header processing EVERY external caller
// shares the proxy's connection IP. That collapses all IP-partitioned
// rate-limit buckets below (webhook 120/min, global 300/min) into one shared
// partition — a single abuser exhausts the bucket for every webhook sender —
// and guts the IP axis of TenantResolutionMiddleware's trial-farming throttle.
//
// TRUST DECISION (documented per the 2026-06-12 audit, item 10): Railway does
// not publish a pinnable edge IP range, so KnownNetworks/KnownProxies cannot
// be populated — we CLEAR both (the loopback-only defaults would otherwise
// make the middleware a silent no-op behind Railway) and compensate with
// ForwardLimit = 1: only the RIGHT-MOST X-Forwarded-For entry — the one the
// Railway edge itself appends with the real client address — is consumed.
// Railway always terminates in front of this container in production, so a
// caller who sends a forged X-Forwarded-For header only pushes their fake
// values further left where they are never read. Even in the hypothetical
// case of a direct-to-container connection, a spoofed header merely lets the
// caller pick a DIFFERENT rate-limit partition for themselves — it cannot
// escape limiting (every partition stays bounded by the same window), cannot
// impersonate an authenticated caller (partitioning prefers the Clerk `sub`
// claim), and no auth, routing, or tenancy decision reads the client IP.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// ── Rate limiting (P2-4) ───────────────────────────────────────────────────
// Named per-user fixed-window policies for expensive / cost-bearing / abusable
// endpoints, PLUS a global fallback limiter so every endpoint has a backstop.
//
// Partition key for MOST policies: Clerk `sub` claim first (so an authenticated
// user is limited as themselves regardless of source IP), then the remote IP
// for unauthenticated-ish callers (ApiKey ingress), then a shared "anonymous"
// bucket as the last resort. Helper below keeps this consistent across policies.
// The "webhook" policy is the exception: it partitions by the TENANT slug on the
// route, so one tenant's HMAC-webhook flood can't exhaust another tenant's quota.
//
// Controllers opt into a named policy with [EnableRateLimiting("<name>")];
// the global limiter applies to everything not otherwise rejected.
builder.Services.AddRateLimiter(options =>
{
    // SCALE-GATED CONSTRAINT: these are PROCESS-LOCAL fixed-window limiters — each API
    // replica holds its own in-memory counters. With one API replica (today's deploy)
    // the published limits are exact. The moment we scale the API to N replicas the
    // effective per-partition ceiling becomes ~N× the configured value (a partition can
    // hit each replica's independent window). The limits below are tuned conservatively
    // so this is acceptable headroom, not a correctness hole, but REVISIT before running
    // multiple API replicas: back the limiters with a distributed store (e.g. Redis) so
    // the window is shared. See docs/audit/2026-06-12-scale-gated-constraints.md.
    //
    // sub → IP → "anonymous": authenticated callers are limited per-user; the
    // unauthenticated ApiKey-ingress surface is limited per source IP. (The webhook
    // surface overrides this to partition per tenant slug — see the "webhook" policy.)
    static string PartitionKey(HttpContext ctx) =>
        ctx.User.FindFirst("sub")?.Value
        ?? ctx.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";

    static RateLimitPartition<string> Window(HttpContext ctx, string prefix, int permit, int seconds) =>
        WindowFor(ctx, prefix, PartitionKey(ctx), permit, seconds);

    static RateLimitPartition<string> WindowFor(HttpContext ctx, string prefix, string key, int permit, int seconds) =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"{prefix}:{key}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromSeconds(seconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0   // reject immediately — no queuing
            });

    // Per-user fixed-window policy for the upload endpoint. Bulk PO upload is a
    // first-class flow — a procurement team legitimately drops a daily batch of
    // dozens of POs at once — so 20/min was too tight (it tripped on a ~24-file
    // drop and the client mislabeled the 429 as "Plan limit reached"). 60/min
    // comfortably covers a real batch while staying a safe abuse ceiling (each
    // upload enqueues one parse job; well under the global 300/min backstop). The
    // client also paces + retries on a throttle, so larger batches self-throttle.
    options.AddPolicy("upload", ctx => Window(ctx, "upload", permit: 60, seconds: 60));

    // Transform is CPU/IO-heavy (parse + standards serialization + enqueue
    // delivery). Cap per-user so a tight client loop can't hammer the worker.
    options.AddPolicy("transform", ctx => Window(ctx, "transform", permit: 30, seconds: 60));

    // AI mapping / schema-inference / suggestion endpoints call paid LLM APIs.
    // Tighter cap protects the OpenAI bill and latency budget.
    options.AddPolicy("ai", ctx => Window(ctx, "ai", permit: 15, seconds: 60));

    // Deterministic mapping-preview reads (GET .../mapping-preview, POST
    // .../mapping-override/preview). These do NO LLM work — they read stored
    // suggestions / run an in-memory dry-run transform — but the live mapping
    // editor polls them repeatedly (debounced as the user types/while parsing).
    // They MUST NOT share the tight "ai" cap, or the editor trips a 429. Generous
    // per-user ceiling so debounced polling never bites, still bounded so a
    // runaway client can't hammer the transform path.
    options.AddPolicy("preview", ctx => Window(ctx, "preview", permit: 120, seconds: 60));

    // Signed-URL / artifact-download generation. Each call mints a pre-signed R2
    // URL; cap so the surface can't be used to bulk-mint download links.
    options.AddPolicy("signed-url", ctx => Window(ctx, "signed-url", permit: 60, seconds: 60));

    // Webhook receivers are unauthenticated (HMAC-only) and chatty, so the ceiling
    // is generous. The PARTITION KEY is the tenant slug carried in the route
    // (/api/webhook-ingress/{slug}/...), NOT the source IP: many suppliers push
    // callbacks for one org from a shared egress IP, and an IP-keyed bucket lets a
    // single noisy tenant exhaust the window for EVERY other tenant. Keying on the
    // slug isolates each org's quota. Fall back to IP / "anonymous" only when no
    // slug is on the route (a malformed request that won't match the controller
    // anyway) so the surface is never left unbounded.
    static string WebhookKey(HttpContext ctx) =>
        ctx.Request.RouteValues.TryGetValue("slug", out var slug)
            && slug?.ToString() is { Length: > 0 } s
                ? $"slug:{s}"
                : ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

    options.AddPolicy("webhook", ctx => WindowFor(ctx, "webhook", WebhookKey(ctx), permit: 120, seconds: 60));

    // The anonymous support contact form feeds an outbound email sender, so it
    // is a spam/amplification + SMTP-cost vector. Nobody legitimately files
    // more than a handful of tickets a minute — keep this tight.
    options.AddPolicy("support", ctx => Window(ctx, "support", permit: 5, seconds: 60));

    // Global backstop: every request (including ones with no named policy) is
    // bounded per partition. Generous so it never bites normal usage, but it
    // closes the "endpoint with no [EnableRateLimiting]" gap.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        Window(ctx, "global", permit: 300, seconds: 60));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Rate limit exceeded. Please slow down and retry shortly." }, ct);
    };
});

// ── Hangfire (C1/C2) ───────────────────────────────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;
builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
// No AddHangfireServer here — the Worker process is the sole Hangfire executor.
// The API only enqueues jobs; running a server here would cause it to try to
// deserialize ProcuLink.Worker types (e.g. EmailPollingJob) that it can't load.

// Register the Hangfire monitoring API so OpsHealthService can read server heartbeats.
// JobStorage is registered as a singleton by AddHangfire; we expose IMonitoringApi
// as a scoped factory wrapper (GetMonitoringApi() is cheap — one lock-free snapshot).
builder.Services.AddScoped<Hangfire.Storage.IMonitoringApi>(sp =>
    sp.GetRequiredService<Hangfire.JobStorage>().GetMonitoringApi());

// ── HTTP client for webhook delivery ──────────────────────────────────────
// SSRF: the "delivery" client sends to tenant-supplied URLs (ErplyConnector /
// DirectoConnector, and any future CreateClient("delivery") user). Attach the
// SAME connect-time-revalidating primary handler the HTTP dispatcher + webhook
// job already build, so a tenant URL pointing at a private/metadata IP (e.g.
// http://169.254.169.254/…) is rejected at TCP connect. The factory resolves the
// singleton OutboundRequestGuard lazily, so DI registration order is irrelevant.
builder.Services.AddHttpClient("delivery", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(sp =>
    sp.GetRequiredService<OutboundRequestGuard>().CreateGuardedHttpHandler());

// ── Tenant service ─────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentTenantService, CurrentTenantService>();

// ── MVC / Controllers ──────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── RFC-7807 ProblemDetails (P1-2) ─────────────────────────────────────────
// Backs the global exception handler below so unhandled exceptions return a
// structured application/problem+json body instead of a raw 500 / HTML page.
// The handler (UseExceptionHandler) never leaks a stack trace in Production —
// see the pipeline section. Sentry's middleware still captures the exception
// because it is installed via UseSentry() before the request reaches the
// exception handler (the handler runs INSIDE Sentry's scope, so Sentry sees
// the throw before we convert it to a ProblemDetails response).
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        // Stable correlation id for support / Sentry triage — safe in every env.
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;

        // Developer-only: surface the exception message + stack as extensions so
        // local debugging is not blind. Gated on Development — NEVER in Production,
        // so no stack trace is ever leaked to clients in prod.
        if (ctx.HttpContext.RequestServices
                .GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            var ex = ctx.HttpContext.Features
                .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            if (ex is not null)
            {
                ctx.ProblemDetails.Detail = ex.Message;
                ctx.ProblemDetails.Extensions["exception"] = ex.GetType().FullName;
                ctx.ProblemDetails.Extensions["stackTrace"] = ex.StackTrace;
            }
        }
    };
});

// ── CORS — Next.js frontend ────────────────────────────────────────────────
// Frontend:Url can be a single URL or a comma-separated EXACT list, e.g.
//   Frontend:Url=https://proculink.eu,https://www.proculink.eu
//
// SECURITY (P1-4): we intentionally DO NOT call
// SetIsOriginAllowedToAllowWildcardSubdomains(). Combining a wildcard-subdomain
// match with AllowCredentials() is dangerous — a future `https://*.vercel.app`
// (or any wildcard) entry in Frontend:Url would let ANY subdomain of that
// domain make CREDENTIALED cross-origin requests. Prod Frontend:Url is the
// exact two-origin list above (no wildcard), so requiring an exact origin
// match does not break prod and removes the credentialed-wildcard footgun.
builder.Services.AddCors(options =>
{
    var defaultOrigins = new List<string>
    {
        "http://localhost:3000",   // Next.js default dev port
        "http://localhost:8082",   // ProcuLink frontend dev port (package.json)
    };

    var configured = (builder.Configuration["Frontend:Url"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var allOrigins = defaultOrigins
        .Concat(configured)
        .Where(u => !string.IsNullOrWhiteSpace(u))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins(allOrigins)   // EXACT origins only — no wildcard subdomains
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// ── Repositories ──────────────────────────────────────────────────────────
// Supplier profiles still flow through the repository; orders and item mappings
// use DbContext directly via their services (legacy order/item-mapping repos removed).
builder.Services.AddScoped<ISupplierProfileRepository, EfSupplierProfileRepository>();

// ── File storage ───────────────────────────────────────────────────────────
// Use LocalFileStorageService in dev when R2 credentials are absent.
// Set Storage:R2AccessKeyId in user-secrets or environment to switch to R2.
if (string.IsNullOrEmpty(builder.Configuration["Storage:R2AccessKeyId"]))
    builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();
else
    builder.Services.AddSingleton<IFileStorageService, R2StorageService>();

// ── Domain services ────────────────────────────────────────────────────────
// ItemMappingService is Scoped (DbContext is Scoped).
// OrderService is Scoped for the same reason.
builder.Services.AddScoped<IItemMappingService, ItemMappingService>();
// Supplier product catalog (ground truth for AI code suggestions) — Scoped (DbContext is Scoped).
builder.Services.AddScoped<ISupplierCatalogService, SupplierCatalogService>();
// V10 — indexed catalog retrieval at scale (exact + pg_trgm trigram). Scoped; OrderIngestionService
// self-constructs one from its DbContext, but register it so other consumers can resolve it.
builder.Services.AddScoped<ICatalogRetrievalService, CatalogRetrievalService>();
// Durable AI-suggestion accept/reject decision history (confidence calibration) — Scoped.
builder.Services.AddScoped<IAiSuggestionDecisionService, AiSuggestionDecisionService>();
// V9 — confidence calibration. Computes a per-org empirical reliability curve from the decision
// history (display-only; never mutates the stored raw confidence). Needs IMemoryCache for the
// short-TTL per-org curve cache; AddMemoryCache is idempotent if registered elsewhere.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IConfidenceCalibrationService, ConfidenceCalibrationService>();
// heart-piece-flex Phase 1: per-order mapping/override stored in canonical_json (no new table).
builder.Services.AddScoped<ProcuLink.Core.Services.Mapping.IOrderMappingOverrideService,
                           ProcuLink.Infrastructure.Services.OrderMappingOverrideService>();
// heart-piece-flex promote: persists a per-order SourceMap into the supplier's reusable inbound mapping.
builder.Services.AddScoped<ProcuLink.Core.Services.Mapping.IPromoteMappingService,
                           ProcuLink.Infrastructure.Services.PromoteMappingService>();
builder.Services.AddScoped<IOrderExceptionService, OrderExceptionService>();
builder.Services.AddScoped<IOpsHealthService, OpsHealthService>();
builder.Services.AddScoped<ISupplierAcceptanceService, SupplierAcceptanceService>();
// Group V4 — unified validation: reusable rule definitions + binding-aware read API + boot backfill.
builder.Services.AddScoped<IRuleDefinitionService, RuleDefinitionService>();
builder.Services.AddScoped<IRuleDefinitionBackfillService, RuleDefinitionBackfillService>();
// Group V1 — versioned Supplier Connection (lifecycle service, ingest-time resolver, backfill).
builder.Services.AddScoped<ISupplierConnectionService, SupplierConnectionService>();
builder.Services.AddScoped<IConnectionResolver, ConnectionResolver>();
builder.Services.AddScoped<IConnectionBackfillService, ConnectionBackfillService>();
// Launch batch 7 — revision authority: pinned-revision config bundle resolver, consumed by
// parse-mapping/item-codes, validation, transform, and delivery. Flag-gated by
// Connections:RevisionAuthority (default OFF = live tables, byte-identical behaviour).
builder.Services.AddScoped<IEffectiveConnectionConfigResolver,
                           ProcuLink.Infrastructure.Services.EffectiveConnectionConfigResolver>();
// Group V2 — replay / impact testing (non-mutating; reuses the transform + acceptance engines).
builder.Services.AddScoped<IReplayService, ReplayService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IPassportService, PassportService>();
builder.Services.AddScoped<IOrderConfirmationService, OrderConfirmationService>();
builder.Services.AddScoped<IBillingService, StripeBillingService>();
builder.Services.AddScoped<ISampleOrderService, SampleOrderService>();

// ── Email API (Postmark over HTTPS) ──────────────────────────────────────
// Sends mail via a managed HTTP API (port 443), so it works on hosts that block outbound SMTP
// (e.g. Railway blocks 25/465/587). Registered ALWAYS so EmailApiDeliveryDispatcher can resolve
// it; IsConfigured=false (no token) → safe no-op. Backs both the support form and email delivery.
builder.Services.AddHttpClient(ProcuLink.Infrastructure.Services.Email.PostmarkEmailApiClient.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<IEmailApiClient, PostmarkEmailApiClient>();

// ── Support contact (Group L Wave 3) ─────────────────────────────────────
// Prefer the HTTP email API (Postmark). Fall back to legacy SMTP only for self-hosted deploys
// that set Smtp:Host; otherwise ConsoleEmailSender logs the email (no-op). Either way the support
// endpoint always 200s.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Email:Postmark:ServerToken"]))
    builder.Services.AddScoped<IEmailSender, PostmarkEmailSender>();
else if (!string.IsNullOrWhiteSpace(builder.Configuration["Smtp:Host"]))
    builder.Services.AddScoped<IEmailSender, MailKitEmailSender>();
else
    builder.Services.AddScoped<IEmailSender, ConsoleEmailSender>();

builder.Services.AddScoped<ISupportContactService, SupportContactService>();

// ── Starter PO mapping templates — static, read-only, cached ─────────────
builder.Services.AddSingleton<IStarterTemplateService, StarterTemplateService>();

// ── Analytics (PostHog) — no-op when key absent ──────────────────────────
builder.Services.Configure<PostHogOptions>(builder.Configuration.GetSection("Analytics:PostHog"));
builder.Services.AddSingleton<IAnalyticsService, PostHogAnalyticsService>();
builder.Services.AddScoped<IEmailSettingsService, EmailSettingsService>();
builder.Services.AddScoped<ProcuLink.Core.Services.Organisation.IOrganisationSettingsService, ProcuLink.Infrastructure.Services.OrganisationSettingsService>();
builder.Services.AddScoped<ProcuLink.Core.Services.Ingress.IPullIngressSettingsService, ProcuLink.Infrastructure.Services.PullIngressSettingsService>();
builder.Services.AddSingleton<IAiMappingService, OpenAiMappingService>();
// T4 — external web/product-code grounding. No-op unless Ai:OpenAI:ProductSearch:Enabled=true
// (default off → byte-identical deploy). Reuses Ai:OpenAI:ApiKey via the Responses web_search tool.
builder.Services.AddSingleton<IProductCodeSearch, OpenAiProductCodeSearch>();
builder.Services.AddScoped<IAiUsageTracker, AiUsageTracker>();
builder.Services.AddScoped<IIdempotencyService, IdempotencyService>();
builder.Services.AddScoped<ISchemaInferencer, OpenAiSchemaInferencer>();
builder.Services.AddScoped<IEmailBodyOrderExtractor, OpenAiEmailBodyOrderExtractor>();
builder.Services.AddScoped<IInboundEmailRouter, InboundEmailRouter>();
builder.Services.AddScoped<IParseJobEnqueuer, ProcuLink.Api.Controllers.HangfireParseJobEnqueuer>();

// ── Wave 2: pull-ingress (SFTP / S3-R2) + OCR fallback ────────────────────
builder.Services.AddSingleton<ISftpClientFactory, RenciSftpClientFactory>();
builder.Services.AddScoped<ISftpIngressService, SftpIngressService>();
builder.Services.AddSingleton<IAmazonS3ClientFactory, AmazonS3ClientFactory>();
builder.Services.AddScoped<IS3IngressService, S3IngressService>();

// ── Catalog import channels: SFTP/FTP(S) pull + settings (plan 2026-06-12) ─
// FluentFTP fetch seam is hardened (PASVEX + mandatory FTPS data encryption + 30 s
// timeouts) inside the factory; CatalogPullService owns guard/deadline/bounded-read.
builder.Services.AddSingleton<ProcuLink.Infrastructure.Services.Ingress.IFtpFetchClientFactory,
                              ProcuLink.Infrastructure.Services.Ingress.FluentFtpFetchClientFactory>();
builder.Services.AddScoped<ProcuLink.Core.Services.Catalog.ICatalogPullService,
                           ProcuLink.Infrastructure.Services.Catalog.CatalogPullService>();
builder.Services.AddScoped<ProcuLink.Core.Services.Catalog.ICatalogSourceSettingsService,
                           ProcuLink.Infrastructure.Services.Catalog.CatalogSourceSettingsService>();

// PDF rasterizer for the vision fallback (scanned/no-text PDFs). Self-contained
// PDFium + SkiaSharp natives — no extra system packages on the Debian base.
builder.Services.AddSingleton<IPdfRasterizer, SkiaPdfRasterizer>();
// Primary PDF path: text → LLM structured extraction (no-op without an OpenAI key,
// so the deterministic PdfOrderParser stays the fallback). Singleton + per-call
// IServiceScopeFactory tracker resolution so the same instance is valid in the Worker.
builder.Services.AddSingleton<IStructuredOrderExtractor, OpenAiPdfOrderExtractor>();
// OCR seam. Opt-in self-hosted no-egress engine (RapidOcrNet) only when enabled —
// otherwise a no-op, so the ~12MB models + ONNX session are never loaded.
if (builder.Configuration.GetValue<bool>("NoEgressOcr:Enabled"))
    builder.Services.AddSingleton<IDocumentOcrService, RapidOcrDocumentOcrService>();
else
    builder.Services.AddSingleton<IDocumentOcrService, NoOpOcrService>();
builder.Services.AddScoped<IPoMappingService, PoMappingService>();
builder.Services.AddScoped<IBuyerService, BuyerService>();
// ── Wave 4: API keys + integration subscriptions ──────────────────────────
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
builder.Services.AddScoped<IIntegrationTriggerService, IntegrationTriggerService>();
builder.Services.AddScoped<IValidationRuleService, ValidationRuleService>();
builder.Services.AddScoped<IOutputTemplateService, OutputTemplateService>();
builder.Services.AddSingleton<DeliveryEncryptionService>();
builder.Services.AddSingleton<OutboundRequestGuard>();
// Group O reliability: retry-queue backoff + SLA window tunables (section Delivery:Reliability).
builder.Services.AddSingleton(sp =>
{
    var opts = new ProcuLink.Core.Services.Delivery.DeliveryReliabilityOptions();
    builder.Configuration
        .GetSection(ProcuLink.Core.Services.Delivery.DeliveryReliabilityOptions.SectionName)
        .Bind(opts);
    return opts;
});
builder.Services.AddScoped<IDeliveryConfigService, DeliveryConfigService>();
// cXML network credentials: resolves a supplier's configured From/To/Sender identities + decrypts
// the Sender SharedSecret for the cXML transform (OrderService → OrderTransformService). Unregistered
// → legacy GUID identities.
builder.Services.AddScoped<ICxmlCredentialResolver, CxmlCredentialResolver>();
builder.Services.AddScoped<IDeliveryService, DeliveryService>();
builder.Services.AddScoped<IDeliverySlaService, DeliverySlaService>();
builder.Services.AddScoped<IErpConnector, ErplyConnector>();
builder.Services.AddScoped<IErpConnector, DirectoConnector>();
builder.Services.AddScoped<IDeliveryDispatcher, HttpDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, SftpDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, FtpsDeliveryDispatcher>();
// Email delivery via HTTP email API (Postmark) — works where outbound SMTP is blocked.
builder.Services.AddScoped<IDeliveryDispatcher, EmailApiDeliveryDispatcher>();
// Legacy raw-SMTP dispatcher: RETIRED from offered channels (blocked on the cloud host). Registered
// only when a self-hosted operator opts in via Delivery:EnableSmtp=true (default off).
if (builder.Configuration.GetValue<bool>("Delivery:EnableSmtp"))
    builder.Services.AddScoped<IDeliveryDispatcher, SmtpDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, ErplyDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, DirectoDeliveryDispatcher>();

// ── Parsing layer (ProcuLink.Transform) ───────────────────────────────────
// Each parser registered individually so DI can inject IEnumerable<IPurchaseOrderParser>
// into OrderParserFactory, which selects by file extension at runtime.
builder.Services.AddSingleton<IPurchaseOrderParser, CsvOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, XlsxOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, PdfOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, CxmlOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, UblOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, IDocOrders05Parser>(); // SAP IDoc ORDERS05 (.xml, root <ORDERS05>)
builder.Services.AddSingleton<IPurchaseOrderParser, EdifactOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, X12OrderParser>(); // Group M — ANSI X12 850
builder.Services.AddSingleton<OrderParserFactory>();

// ── Transform layer (ProcuLink.Transform) ──────────────────────────────────
// Both implementations registered as ITransformService. OrderService resolves
// the correct one at runtime via IEnumerable<ITransformService> + CanTransform().
builder.Services.AddSingleton<ITransformService, XmlTransformService>();
builder.Services.AddSingleton<ITransformService, CsvTransformService>();
builder.Services.AddSingleton<ITransformService, CxmlTransformService>();
builder.Services.AddSingleton<ITransformService, JsonTransformService>();
builder.Services.AddSingleton<ITransformService, UblOrderTransformService>(); // Group M Phase 1 — UBL 2.1 Peppol BIS 3.0
builder.Services.AddSingleton<ITransformService, X12TransformService>(); // Group M — ANSI X12 850

// ── Group V8: standards conformance reports ─────────────────────────────────
// Validates a generated outbound document against its NAMED standard profile
// (cXML 1.2 / UBL 2.1 / X12 850 / EDIFACT ORDERS D.96A / SAP IDoc ORDERS05).
// Pure/stateless — used by OrdersController's GET /api/orders/{id}/conformance.
builder.Services.AddSingleton<ProcuLink.Transform.Conformance.IConformanceService,
    ProcuLink.Transform.Conformance.ConformanceService>();

// ── Wave 3: Invoice parsers ────────────────────────────────────────────────
builder.Services.AddSingleton<IInvoiceParser, UblInvoiceParser>();
builder.Services.AddSingleton<IInvoiceParser, EdifactInvoiceParser>();
builder.Services.AddSingleton<InvoiceParserFactory>();
builder.Services.AddSingleton<IDesadvParser, EdifactDesadvParser>();
builder.Services.AddSingleton<DesadvParserFactory>();

// ── Wave 3: Invoice transform services ────────────────────────────────────
builder.Services.AddSingleton<IInvoiceTransformService, CsvInvoiceTransformService>();
builder.Services.AddSingleton<IInvoiceTransformService, XmlInvoiceTransformService>();
builder.Services.AddSingleton<IInvoiceTransformService, JsonInvoiceTransformService>();

// ── Peppol wedge (Track A): BIS Billing 3.0 UBL invoice generation + validation.
// Format token "peppol" is routed by InvoiceService.ForwardAsync just like the
// other invoice transformers. Party details (endpoint IDs / VAT) come from
// PeppolPartyOptions; the default-constructed singleton produces a structurally
// valid but party-incomplete document, which PeppolBisValidator flags honestly.
// AS4 / Access-Point transport is NOT included (that is Track B partner-wrap).
builder.Services.AddSingleton<IInvoiceTransformService, PeppolBisInvoiceTransformService>();
builder.Services.AddSingleton<PeppolBisValidator>();

// ── Wave 3: Invoice + ASN services ────────────────────────────────────────
builder.Services.AddScoped<IInvoiceService, ProcuLink.Infrastructure.Services.InvoiceService>();
builder.Services.AddScoped<IDesadvService, ProcuLink.Infrastructure.Services.DesadvService>();

// GDPR per-order erasure (admin-triggered): deletes the R2 blobs + every order-tied DB row.
builder.Services.AddScoped<IDataErasureService, ProcuLink.Infrastructure.Services.DataErasureService>();

// ── Phase 6: smart format auto-detect + HMAC webhook receive ──────────────
// IDistributedCache for HmacWebhookVerifier nonce replay store.
// MemoryDistributedCache is single-instance (correct for one API replica). Set
// Redis:ConnectionString to make HMAC-nonce replay protection cross-instance for
// horizontal scaling — no other code change needed (closes P2-3 / W4 at the swap point).
var hmacNonceRedis = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrWhiteSpace(hmacNonceRedis))
{
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = hmacNonceRedis);
}
else
{
    builder.Services.AddDistributedMemoryCache();
}
builder.Services.AddScoped<ProcuLink.Core.Services.Detection.IFormatDetector, ProcuLink.Infrastructure.Services.Detection.FormatDetectorService>();
builder.Services.AddScoped<ProcuLink.Core.Services.Detection.ISchemaFingerprintService, ProcuLink.Infrastructure.Services.Detection.SchemaFingerprintService>();
// Stateless source-column extractor used by the magic-mapping UI (GET /api/suppliers/{id}/mapping/source-columns).
builder.Services.AddSingleton<ProcuLink.Core.Services.Detection.ISourceColumnExtractor, ProcuLink.Transform.Detection.SourceColumnExtractor>();
// SourceMap engine tokenizer: extracts every addressable value from a source file (CSV + XML concrete;
// other formats return an empty list). Singleton — stateless, reused across requests.
builder.Services.AddSingleton<ProcuLink.Transform.Tokenizing.ISourceTokenizer, ProcuLink.Transform.Tokenizing.SourceTokenizer>();
builder.Services.AddScoped<ProcuLink.Core.Services.Webhooks.IHmacWebhookVerifier, ProcuLink.Infrastructure.Services.Webhooks.HmacWebhookVerifier>();

// ── Health checks (G5) — liveness vs readiness ────────────────────────────
// Liveness (/health) is a fast, dependency-free 200 served by HealthController
// so Railway's container probe never gets killed by a transient DB/R2 blip.
// Readiness (/health/ready, mapped below) runs the dependency checks tagged
// "ready": DB reachability, storage reachability, and the background-migration
// readiness flag. Railway/monitoring can probe readiness to see degraded state
// (e.g. migrations failed) without the process going down (liveness stays up).
builder.Services.AddHealthChecks()
    // DB reachable — opens a connection (CanConnectAsync). Implemented with plain
    // EF Core (already referenced) rather than AddDbContextCheck, which would pull
    // in the extra HealthChecks.EntityFrameworkCore package.
    .AddCheck<ProcuLink.Api.Controllers.DatabaseHealthCheck>(
        name: "database",
        failureStatus: HealthStatus.Unhealthy,
        tags: new[] { "ready" })
    // R2 / local storage reachable (degrades gracefully when R2 isn't configured).
    .AddCheck<ProcuLink.Api.Controllers.StorageHealthCheck>(
        name: "storage",
        failureStatus: HealthStatus.Degraded,
        tags: new[] { "ready" })
    // Background auto-migration readiness — Unhealthy once all retries are exhausted.
    .AddCheck<ProcuLink.Api.Controllers.MigrationReadinessHealthCheck>(
        name: "migrations",
        failureStatus: HealthStatus.Unhealthy,
        tags: new[] { "ready" })
    // Worker (Hangfire server) heartbeat freshness — Degraded (keeps /health/ready
    // at HTTP 200) when no worker is registered or its heartbeat is stale. Reads the
    // SHARED Hangfire Postgres storage via IMonitoringApi (registered above) — no new
    // table/job. The structured JSON body carries workerHealthy:false so the external
    // uptime workflow + WorkerHealthAlertJob alert without the API self-evicting on a
    // Worker blip. See WorkerHeartbeatHealthCheck remarks for the severity rationale.
    .AddCheck<ProcuLink.Api.Controllers.WorkerHeartbeatHealthCheck>(
        name: "worker",
        failureStatus: HealthStatus.Degraded,
        tags: new[] { "ready" });

// IMonitoringApi is registered scoped (factory wrapper) above for OpsHealthService.
// The WorkerHeartbeatHealthCheck is resolved as a singleton by the health-check
// system, so it cannot take a scoped IMonitoringApi via the constructor — instead
// its ctor parameter is optional and it falls back to JobStorage.Current at probe
// time (which is the same singleton storage). No extra registration needed.

// ── OpenAPI — Swashbuckle for spec, Scalar for UI ──────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "ProcuLink API",
        Version = "v1",
        Description = "Purchase Order processing API"
    });
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Paste a Clerk session JWT (without 'Bearer ' prefix)."
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Magic-mapping field suggester: deterministic heuristic, optionally AI-augmented (no-op without an AI key).
builder.Services.AddScoped<ProcuLink.Core.Services.Mapping.IFieldMappingSuggester, ProcuLink.Transform.Mapping.AiAugmentedFieldMappingSuggester>();

// ──────────────────────────────────────────────────────────────────────────
var app = builder.Build();
// ──────────────────────────────────────────────────────────────────────────

// ── Forwarded headers — FIRST middleware in the pipeline ─────────────────
// Must run before anything that reads Connection.RemoteIpAddress (the rate
// limiter's partition key, TenantResolutionMiddleware's trial-farming
// throttle) or Request.Scheme. Trust decision + ForwardLimit=1 rationale are
// documented at the ForwardedHeadersOptions registration above.
app.UseForwardedHeaders();

// ── Startup configuration validation ─────────────────────────────────────
// In Production any missing required key throws and lists every gap in one
// shot. In non-production environments missing keys log a warning instead so
// devs can still run the API without every secret wired up.
{
    var startupLogger = app.Services
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("ProcuLink.Startup");
    StartupConfigurationValidator.Validate(
        configuration:    app.Configuration,
        logger:           startupLogger,
        environmentName:  app.Environment.EnvironmentName,
        requiredKeys:     StartupConfigurationValidator.ApiRequiredKeys,
        optionalKeys:     StartupConfigurationValidator.OptionalKeys,
        componentName:    "ProcuLink.Api");
}

// ── Global exception handler (P1-2) ───────────────────────────────────────
// Installed EARLY — before routing/CORS/auth — so it wraps the entire request
// pipeline and converts any unhandled exception into an RFC-7807
// application/problem+json response (via AddProblemDetails above).
//
// Sentry still captures every exception: UseSentry() (configured on the WebHost
// at the very top) installs Sentry's middleware ahead of this one, so the throw
// propagates up through Sentry's scope BEFORE this handler turns it into a
// response — the handler does not swallow it from Sentry's perspective.
//
// No stack trace ever leaks in Production: UseExceptionHandler emits only the
// standard ProblemDetails fields (type/title/status). We attach developer
// exception details (the message + stack) ONLY in Development, surfaced as
// ProblemDetails extensions for local debugging.
app.UseExceptionHandler();

// ── Baseline security response headers (every API response) ───────────────
// nosniff stops content-type sniffing; HSTS instructs browsers that hit the API
// host directly to pin HTTPS (TLS is terminated at Railway, so we send the header
// rather than redirect). Referrer-Policy limits cross-origin referrer leakage.
// Set via OnStarting so they apply regardless of which middleware writes the body.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        return Task.CompletedTask;
    });
    await next();
});

// ── OpenAPI / Scalar UI — dev only ────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    // Swashbuckle generates the spec at /swagger/v1/swagger.json
    app.UseSwagger();

    // Scalar UI at /scalar — replaces Swagger UI
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("ProcuLink API");
        options.WithOpenApiRoutePattern("/swagger/v1/swagger.json");
        options.WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });

    // Hangfire dashboard — local dev only; no auth guard needed in dev
    app.UseHangfireDashboard("/hangfire");
}

// HTTPS redirection is intentionally NOT enabled in any environment:
//  - Production: Railway terminates TLS at the load balancer; the container
//    only serves HTTP, so a 307 from the app makes the Railway health check fail.
//  - Development: the Next.js frontend (http://localhost:8082) calls the API at
//    http://localhost:5223 (NEXT_PUBLIC_API_BASE_URL). A dev http→https 307 to
//    https://localhost:7230 breaks the CORS *preflight* on the first cross-origin
//    call — browsers never follow redirects for an OPTIONS preflight — which
//    surfaced as "/orders/[id] → Order Not Found" on first navigation while the
//    same order loaded fine elsewhere. Both the HTTP and HTTPS Kestrel endpoints
//    are served directly (see Properties/launchSettings.json `https` profile:
//    "https://localhost:7230;http://localhost:5223"), so no redirect is needed.
// If you reintroduce HTTPS redirection it MUST sit after UseCors and must not
// apply to the frontend's HTTP origin, or first-call CORS will break again.
app.UseCors("AllowFrontend");

// Pipeline order: Authenticate → resolve tenant → rate-limit → Authorize → controllers
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

// Liveness (/health) is served by HealthController (fast, dependency-free 200) so
// Railway's container probe is never blocked by a slow dependency. Readiness
// (/health/ready) runs ONLY the "ready"-tagged dependency checks (DB + storage +
// migration flag + worker heartbeat) and reports the aggregate status so
// monitoring can see degraded state without taking the process down.
//
// HTTP status: Healthy/Degraded → 200, Unhealthy → 503 (the default
// MapHealthChecks status-code map). A stale Worker is Degraded → 200, but the JSON
// body carries workerHealthy:false so the external uptime workflow alerts on it.
//
// BODY: a structured JSON payload (instead of the default plain "Healthy" string)
// with per-check status/description/duration + a flattened workerHealthy flag the
// uptime workflow + dashboards can read. ResponseWriter is the only customisation —
// it NEVER includes secrets (each check's Data bag is counts/ages/booleans only).
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = ProcuLink.Api.Controllers.HealthResponseWriter.WriteReadinessJsonAsync,
});

// ── Auto-migrate after server starts ─────────────────────────────────────
// Runs AFTER the HTTP server is listening so the Railway health check
// succeeds immediately. Neon Postgres has a cold-start delay on the
// first connection; retrying with backoff handles that gracefully.
// We use ApplicationStarted so the scope is created inside a running host.
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var migLogger = loggerFactory.CreateLogger("ProcuLink.Migrations");

        // P2: State starts as Pending (the default) so /health/ready is
        // honest during Neon cold-start; it stays NOT-ready until
        // MigrateAsync() completes successfully. No explicit MarkPending()
        // call is needed here — the field initialises to Pending at process
        // start, which is the desired semantics.

        // ── Phantom-migration reconciliation ─────────────────────────────
        // Some Wave 3/4 migrations had their SQL applied to the prod DB
        // out-of-band (or via a previous deploy that crashed mid-migration),
        // but the __EFMigrationsHistory table doesn't record them as applied.
        // If we let MigrateAsync() proceed it would try to re-add the
        // organisations.slug column and Postgres would reject with
        // 42701 "column already exists". For each known phantom-prone
        // migration we check a sentinel DB object — if the object exists
        // AND the migration row is missing, we insert the history row so
        // MigrateAsync() skips re-applying the SQL.
        //
        // P1 kill-switch: set Migrations:ReconcilePhantom=false to bypass
        // once prod __EFMigrationsHistory is confirmed to contain all 5 IDs.
        // Default is true (current behaviour unchanged).
        var reconcilePhantom = app.Configuration.GetValue("Migrations:ReconcilePhantom", defaultValue: true);
        if (reconcilePhantom)
        {
            try
            {
                await ReconcilePhantomMigrationsAsync(db, migLogger);
            }
            catch (Exception ex)
            {
                // Sentinel queries can transiently fail on Neon cold-start.
                // The retry loop below will get another shot; do not abort boot.
                migLogger.LogWarning(
                    "Phantom-migration reconciliation skipped due to error ({Message}). " +
                    "Proceeding to MigrateAsync — retry loop will handle transient failures.",
                    ex.Message);
            }
        }
        else
        {
            migLogger.LogInformation(
                "Phantom-migration reconciliation disabled via Migrations:ReconcilePhantom=false.");
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                migLogger.LogInformation("Database migrations applied (attempt {Attempt}).", attempt);
                // P2: transition to Succeeded — /health/ready becomes Healthy.
                ProcuLink.Api.Controllers.MigrationReadiness.MarkSucceeded();

                // ── Group V1: idempotent connection backfill ─────────────────
                // Turn each supplier's current loose config into a published "revision 1"
                // under a SupplierConnection (zero behaviour change). Idempotent — the
                // UNIQUE(org_id, supplier_id) connection guard makes re-runs a no-op, so
                // it's safe to call on every boot. Best-effort: a backfill failure must
                // NOT keep the app from serving (migrations already succeeded).
                try
                {
                    var backfill = scope.ServiceProvider.GetRequiredService<IConnectionBackfillService>();
                    var createdCount = await backfill.BackfillAllAsync(CancellationToken.None);
                    migLogger.LogInformation(
                        "Group V1 connection backfill complete: {Count} new connection(s) created.", createdCount);
                }
                catch (Exception backfillEx)
                {
                    migLogger.LogError(backfillEx,
                        "Group V1 connection backfill failed (app stays up; orders fall back to live config).");
                    SentrySdk.CaptureException(backfillEx);
                }

                // ── Launch batch 7 review fix: promoted-output re-backfill ───
                // Earlier V1 backfills left output_mapping_json NULL even when the supplier's
                // PoMappingConfig carried a promoted Output section (batch 4A) — flag-ON pinned
                // orders would silently lose the promoted layout. Fills ONLY null snapshots on
                // "system:backfill" rows; idempotent; per-row skip+warn keeps it compatible with
                // the published-row immutability trigger (AddReviewReasonAndPublishedRevision-
                // Immutability — it must run BEFORE that trigger migration is applied, or the
                // trigger must exempt NULL→value output fills, so rows are repaired rather than
                // skipped). Best-effort, like V1.
                try
                {
                    var rebackfill = scope.ServiceProvider.GetRequiredService<IConnectionBackfillService>();
                    var repairedCount = await rebackfill.RebackfillPromotedOutputAsync(CancellationToken.None);
                    migLogger.LogInformation(
                        "Promoted-output re-backfill complete: {Count} backfilled revision(s) repaired.", repairedCount);
                }
                catch (Exception rebackfillEx)
                {
                    migLogger.LogError(rebackfillEx,
                        "Promoted-output re-backfill failed (app stays up; affected pinned orders use the fixed transformer until repaired).");
                    SentrySdk.CaptureException(rebackfillEx);
                }

                // ── Group V4: idempotent rule-definition seed + link ─────────
                // Seed the global rule catalog as org-scoped RuleDefinitions and link existing
                // free-floating acceptance rules to a matching definition. ZERO evaluation change
                // (rule scalar columns are never touched). Idempotent — UNIQUE(org_id, code) +
                // the "RuleDefinitionId is null" link guard make re-runs a no-op. Best-effort:
                // a failure must NOT keep the app from serving (migrations already succeeded).
                try
                {
                    var ruleBackfill = scope.ServiceProvider.GetRequiredService<IRuleDefinitionBackfillService>();
                    var (defs, links) = await ruleBackfill.BackfillAllAsync(CancellationToken.None);
                    migLogger.LogInformation(
                        "Group V4 rule-definition backfill complete: {Defs} definition(s) seeded, {Links} rule(s) linked.",
                        defs, links);
                }
                catch (Exception ruleBackfillEx)
                {
                    migLogger.LogError(ruleBackfillEx,
                        "Group V4 rule-definition backfill failed (app stays up; acceptance evaluation unaffected).");
                    SentrySdk.CaptureException(ruleBackfillEx);
                }

                // ── Billing: idempotent org-plan-history baseline seed ───────
                // Orgs that predate org_plan_history get ONE baseline row from
                // their current plan + order-limit override (effective_from =
                // CreatedAt) so as-of overage metering has a floor; orgs with
                // any history row are skipped, so re-running every boot is a
                // no-op. Best-effort: a seed failure must NOT keep the app from
                // serving — metering falls back to current org values for
                // unseeded orgs (the pre-history behaviour).
                try
                {
                    var seeded = await ProcuLink.Infrastructure.Services.OrgPlanHistorySeeder
                        .SeedMissingBaselinesAsync(db, CancellationToken.None);
                    migLogger.LogInformation(
                        "Org plan-history baseline seed complete: {Count} baseline row(s) inserted.", seeded);
                }
                catch (Exception planHistorySeedEx)
                {
                    migLogger.LogError(planHistorySeedEx,
                        "Org plan-history baseline seed failed (app stays up; unseeded orgs meter with current plan values).");
                    SentrySdk.CaptureException(planHistorySeedEx);
                }
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= 6)
                    break; // Final attempt failed — fall through to fail-loud handling.

                var delay = TimeSpan.FromSeconds(attempt * 3); // 3 s, 6 s, 9 s, 12 s, 15 s
                migLogger.LogWarning(
                    "Migration attempt {Attempt}/6 failed ({Message}). Retrying in {Delay}s…",
                    attempt, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay);
            }
        }

        // ── Fail-loud on final failure ────────────────────────────────────────
        // All retries exhausted. We deliberately do NOT crash the process: the
        // HTTP server (and liveness probe) stays up so partial functionality and
        // diagnostics remain available. But the failure must be VISIBLE:
        //   1. LogError (structured) — shows in Railway logs.
        //   2. SentrySdk.CaptureException — Sentry is wired on the API WebHost, so
        //      this raises an alert with the actual migration exception.
        //   3. MigrationReadiness.MarkFailed() — flips /health/ready to Unhealthy
        //      so Railway/monitoring can detect the stale-schema state.
        migLogger.LogError(
            lastError,
            "All 6 migration attempts failed — app is running but DB schema may be outdated. " +
            "Marking readiness UNHEALTHY (/health/ready) and reporting to Sentry.");

        if (lastError is not null)
            SentrySdk.CaptureException(lastError);
        else
            SentrySdk.CaptureMessage(
                "Database migrations failed after all retry attempts (no exception captured).",
                SentryLevel.Error);

        ProcuLink.Api.Controllers.MigrationReadiness.MarkFailed();
    });
});

// ── Analytics graceful flush on shutdown ─────────────────────────────────
app.Lifetime.ApplicationStopping.Register(() =>
{
    var svc = app.Services.GetRequiredService<IAnalyticsService>();
    try { svc.FlushAsync(default).GetAwaiter().GetResult(); } catch { /* swallow */ }
});

app.Run();

// ── Phantom-migration helpers ────────────────────────────────────────────
// Local functions live after app.Run() so they sit at the end of the
// top-level program; they are still in scope for the lambdas above.
//
// Phantom-prone migrations and their sentinel checks. Each entry pairs a
// migration ID with a SQL boolean expression that is `true` when the
// migration's SQL has clearly already been applied to the DB. If the
// sentinel matches AND __EFMigrationsHistory has no row for the migration,
// we insert one so EF treats it as applied.
static (string Id, string SentinelDescription, string SentinelSql)[] PhantomMigrations() => new[]
{
    ("20260528120215_AddInvoicesAndLines",
     "table 'invoices' exists",
     "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'invoices')"),
    ("20260528120226_AddAdvanceShippingNotices",
     "table 'advance_shipping_notices' exists",
     "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'advance_shipping_notices')"),
    ("20260528120230_AddTenantApiKeysAndOrgSlug",
     "column 'organisations.slug' exists",
     "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'organisations' AND column_name = 'slug')"),
    ("20260528120235_AddIntegrationSubscriptions",
     "table 'integration_subscriptions' exists",
     "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'integration_subscriptions')"),
    ("20260528150709_AddIsSampleFlags",
     "column 'purchase_orders.is_sample' exists",
     "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'purchase_orders' AND column_name = 'is_sample')"),
};

static async Task ReconcilePhantomMigrationsAsync(ProcuLinkDbContext db, ILogger logger)
{
    // P1 — own the connection lifetime: open it explicitly here and close it
    // in a finally so it is returned to the pool before MigrateAsync runs.
    // Previously the connection was opened but never explicitly closed/disposed,
    // relying on EF scope disposal; that is a resource leak.
    var conn = db.Database.GetDbConnection();
    var openedByUs = conn.State != System.Data.ConnectionState.Open;
    if (openedByUs)
        await conn.OpenAsync();

    try
    {
        // Step 1: Does __EFMigrationsHistory exist? If not, MigrateAsync will
        // create it on first run and there is nothing phantom to reconcile.
        bool historyTableExists;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory')";
            var result = await cmd.ExecuteScalarAsync();
            historyTableExists = result is bool b && b;
        }

        if (!historyTableExists)
        {
            logger.LogInformation(
                "__EFMigrationsHistory does not exist — fresh database. Skipping phantom-migration check.");
            return;
        }

        // Step 2: Read an existing ProductVersion to stay consistent, fallback to 8.0.0.
        var productVersion = "8.0.0";
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT \"ProductVersion\" FROM \"__EFMigrationsHistory\" LIMIT 1";
            var result = await cmd.ExecuteScalarAsync();
            if (result is string s && !string.IsNullOrWhiteSpace(s))
                productVersion = s;
        }

        // Step 3: Load applied migration IDs.
        var appliedIds = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                appliedIds.Add(reader.GetString(0));
        }

        // Step 4: For each phantom-prone migration, check sentinel and insert
        // history row if needed.
        foreach (var (id, sentinelDescription, sentinelSql) in PhantomMigrations())
        {
            if (appliedIds.Contains(id))
                continue; // Already recorded — nothing to do.

            bool sentinelExists;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sentinelSql;
                var result = await cmd.ExecuteScalarAsync();
                sentinelExists = result is bool b && b;
            }

            if (!sentinelExists)
                continue; // Genuinely new migration — let MigrateAsync apply it.

            logger.LogWarning(
                "Phantom migration {Id} detected (sentinel: {Sentinel}). Inserting history row.",
                id, sentinelDescription);

            await using var insert = conn.CreateCommand();
            insert.CommandText =
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                "VALUES (@migId, @productVersion)";
            var migIdParam = insert.CreateParameter();
            migIdParam.ParameterName = "@migId";
            migIdParam.Value = id;
            insert.Parameters.Add(migIdParam);
            var pvParam = insert.CreateParameter();
            pvParam.ParameterName = "@productVersion";
            pvParam.Value = productVersion;
            insert.Parameters.Add(pvParam);
            await insert.ExecuteNonQueryAsync();
        }
    }
    finally
    {
        // Close only if we were the ones who opened it; do not close a
        // connection that MigrateAsync (or EF itself) had already opened.
        if (openedByUs && conn.State == System.Data.ConnectionState.Open)
            await conn.CloseAsync();
    }
}
