using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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
});

// ── Stripe SDK ────────────────────────────────────────────────────────────
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"] ?? string.Empty;

// ── Database ───────────────────────────────────────────────────────────────
builder.Services.AddDbContext<ProcuLinkDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

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

// ── Rate limiting — 20 uploads/min per authenticated user ──────────────────
builder.Services.AddRateLimiter(options =>
{
    // Per-user fixed-window policy for the upload endpoint.
    // Key: Clerk sub claim; falls back to IP for unauthenticated callers.
    options.AddPolicy("upload", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("sub")?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0   // reject immediately — no queuing
            }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Upload rate limit exceeded. Maximum 20 uploads per minute." }, ct);
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

// ── HTTP client for webhook delivery ──────────────────────────────────────
builder.Services.AddHttpClient("delivery", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

// ── Tenant service ─────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentTenantService, CurrentTenantService>();

// ── MVC / Controllers ──────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── CORS — Next.js frontend ────────────────────────────────────────────────
// Frontend:Url can be a single URL or a comma-separated list. Supports Vercel
// preview-deploy wildcard subdomains when configured as e.g.
//   Frontend:Url=https://proculink.eu,https://*.vercel.app
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
        policy.WithOrigins(allOrigins)
              .SetIsOriginAllowedToAllowWildcardSubdomains()
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
builder.Services.AddScoped<IOrderExceptionService, OrderExceptionService>();
builder.Services.AddScoped<ISupplierAcceptanceService, SupplierAcceptanceService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IPassportService, PassportService>();
builder.Services.AddScoped<IOrderConfirmationService, OrderConfirmationService>();
builder.Services.AddScoped<IBillingService, StripeBillingService>();
builder.Services.AddScoped<ISampleOrderService, SampleOrderService>();

// ── Support contact (Group L Wave 3) ─────────────────────────────────────
// MailKitEmailSender when SMTP host configured; otherwise ConsoleEmailSender
// logs the email (no-op delivery). Either way the support endpoint always 200s.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Smtp:Host"]))
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
builder.Services.AddSingleton<IAiMappingService, OpenAiMappingService>();
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

// OCR fallback — opt-in via Ocr:Azure:Endpoint + Ocr:Azure:ApiKey.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Ocr:Azure:Endpoint"])
    && !string.IsNullOrWhiteSpace(builder.Configuration["Ocr:Azure:ApiKey"]))
{
    builder.Services.AddSingleton<IDocumentOcrService, AzureDocumentIntelligenceOcrService>();
}
else
{
    builder.Services.AddSingleton<IDocumentOcrService, NoOpOcrService>();
}
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
builder.Services.AddScoped<IDeliveryService, DeliveryService>();
builder.Services.AddScoped<IDeliverySlaService, DeliverySlaService>();
builder.Services.AddScoped<IErpConnector, ErplyConnector>();
builder.Services.AddScoped<IErpConnector, DirectoConnector>();
builder.Services.AddScoped<IDeliveryDispatcher, HttpDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, SftpDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, FtpsDeliveryDispatcher>();
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

// ── Canonical-model output transforms (ProcuLink.Transform) ─────────────────
// Serialize a ParsedOrder (the pre-resolution canonical model) directly to a
// standards document. Selected at runtime by ParsedOrderTransformFactory via
// IEnumerable<IParsedOrderTransform> + CanTransform().
builder.Services.AddSingleton<IParsedOrderTransform, UblParsedOrderTransform>();     // UBL 2.1 Order
builder.Services.AddSingleton<IParsedOrderTransform, X12ParsedOrderTransform>();     // ANSI X12 850
builder.Services.AddSingleton<IParsedOrderTransform, EdifactParsedOrderTransform>(); // UN/EDIFACT ORDERS D.96A
builder.Services.AddSingleton<ParsedOrderTransformFactory>();

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

// ── Wave 3: Invoice + ASN services ────────────────────────────────────────
builder.Services.AddScoped<IInvoiceService, ProcuLink.Infrastructure.Services.InvoiceService>();
builder.Services.AddScoped<IDesadvService, ProcuLink.Infrastructure.Services.DesadvService>();

// ── Phase 6: smart format auto-detect + HMAC webhook receive ──────────────
// IDistributedCache for HmacWebhookVerifier nonce replay store.
// MemoryDistributedCache is single-instance; swap for Redis when horizontal scaling is needed:
//   builder.Services.AddStackExchangeRedisCache(o => o.Configuration = config["Redis:ConnectionString"]);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddScoped<ProcuLink.Core.Services.Detection.IFormatDetector, ProcuLink.Infrastructure.Services.Detection.FormatDetectorService>();
builder.Services.AddScoped<ProcuLink.Core.Services.Detection.ISchemaFingerprintService, ProcuLink.Infrastructure.Services.Detection.SchemaFingerprintService>();
// Stateless source-column extractor used by the magic-mapping UI (GET /api/suppliers/{id}/mapping/source-columns).
builder.Services.AddSingleton<ProcuLink.Core.Services.Detection.ISourceColumnExtractor, ProcuLink.Transform.Detection.SourceColumnExtractor>();
builder.Services.AddScoped<ProcuLink.Core.Services.Webhooks.IHmacWebhookVerifier, ProcuLink.Infrastructure.Services.Webhooks.HmacWebhookVerifier>();

// ── Health check (G5) ─────────────────────────────────────────────────────
builder.Services.AddHealthChecks();

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
app.MapHealthChecks("/health");

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

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                migLogger.LogInformation("Database migrations applied (attempt {Attempt}).", attempt);
                return;
            }
            catch (Exception ex) when (attempt < 6)
            {
                var delay = TimeSpan.FromSeconds(attempt * 3); // 3 s, 6 s, 9 s, 12 s, 15 s
                migLogger.LogWarning(
                    "Migration attempt {Attempt}/6 failed ({Message}). Retrying in {Delay}s…",
                    attempt, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay);
            }
        }

        migLogger.LogError("All 6 migration attempts failed — app is running but DB schema may be outdated.");
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
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open)
        await conn.OpenAsync();

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
