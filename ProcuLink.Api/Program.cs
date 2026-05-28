using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ProcuLink.Api.Middleware;
using ProcuLink.Api.Services;
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

// ── Authentication — Clerk JWT Bearer ─────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Clerk:Authority"];
        // Disable legacy claim-type mapping. Without this, JwtBearer renames "sub"
        // to ClaimTypes.NameIdentifier before claims reach HttpContext.User, which
        // breaks TenantResolutionMiddleware's `FindFirst("sub")` fallback.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false,
            NameClaimType = "sub",
        };
    });

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

// ── CORS — React frontend ──────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    // Add VITE_FRONTEND_URL (Railway env var) for production Vercel frontend (G4)
    var frontendUrls = new List<string>
    {
        "http://localhost:8080",
        "http://localhost:8081",
        "http://localhost:5173",
        "http://localhost:8082",
    };
    var vercelUrl = builder.Configuration["Frontend:Url"];
    if (!string.IsNullOrWhiteSpace(vercelUrl))
        frontendUrls.Add(vercelUrl);

    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins(frontendUrls.ToArray())
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

// ── Repositories ──────────────────────────────────────────────────────────
// IOrderRepository / EfOrderRepository kept for SuppliersController (Phase 2 services use DbContext directly).
builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
builder.Services.AddScoped<ISupplierProfileRepository, EfSupplierProfileRepository>();
builder.Services.AddScoped<IItemMappingRepository, EfItemMappingRepository>();

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
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IBillingService, StripeBillingService>();
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
builder.Services.AddSingleton<DeliveryEncryptionService>();
builder.Services.AddScoped<IDeliveryConfigService, DeliveryConfigService>();
builder.Services.AddScoped<IDeliveryService, DeliveryService>();
builder.Services.AddScoped<IErpConnector, ErplyConnector>();
builder.Services.AddScoped<IErpConnector, DirectoConnector>();
builder.Services.AddScoped<IDeliveryDispatcher, HttpDeliveryDispatcher>();
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
builder.Services.AddSingleton<OrderParserFactory>();

// ── Transform layer (ProcuLink.Transform) ──────────────────────────────────
// Both implementations registered as ITransformService. OrderService resolves
// the correct one at runtime via IEnumerable<ITransformService> + CanTransform().
builder.Services.AddSingleton<ITransformService, XmlTransformService>();
builder.Services.AddSingleton<ITransformService, CsvTransformService>();
builder.Services.AddSingleton<ITransformService, CxmlTransformService>();
builder.Services.AddSingleton<ITransformService, JsonTransformService>();

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

// Railway terminates TLS at the load balancer; the container only needs HTTP.
// Unconditional HTTPS redirect causes the Railway healthchecker to receive 307, not 200.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
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

app.Run();
