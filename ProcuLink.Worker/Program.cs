using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Erp;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Core.Services.Ocr;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ai;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Erp;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Ocr;
using ProcuLink.Infrastructure.Storage;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using ProcuLink.Worker;
using ProcuLink.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);

// In Production we report ALL missing keys in one error after Build(); to
// avoid the connection-string line below pre-empting that consolidated report
// with a single-key error, only fail-fast on the connection string in
// non-Production environments. In Production the StartupConfigurationValidator
// after Build() handles the same gap as part of its combined report.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.Equals(builder.Environment.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase)
    && string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
}
// Use an empty string as a placeholder so DI registration below does not NRE;
// the validator will then throw the real combined error after Build().
connectionString ??= string.Empty;

builder.Services.AddDbContext<ProcuLinkDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer(opts =>
{
    // Worker is the sole Hangfire executor — also processes ParseOrderJob enqueued by the API.
    opts.WorkerCount = 4;
    opts.Queues = new[] { "default" };
});

builder.Services.AddHttpClient("delivery", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

if (string.IsNullOrEmpty(builder.Configuration["Storage:R2AccessKeyId"]))
    builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();
else
    builder.Services.AddSingleton<IFileStorageService, R2StorageService>();

builder.Services.AddScoped<IItemMappingService, ItemMappingService>();
// Wave 4: IntegrationTriggerService is needed by OrderService and DeliveryService.
// Register it here so Worker DI validation passes (same as API/Program.cs line 198).
builder.Services.AddScoped<IIntegrationTriggerService, IntegrationTriggerService>();
// Analytics (PostHog) — required by StripeBillingService (resolved via IBillingService
// in EmailPollingJob) and ParseOrderJob. No-op when Analytics:PostHog:ApiKey is absent.
// Mirrors API/Program.cs lines 189-190.
builder.Services.Configure<PostHogOptions>(builder.Configuration.GetSection("Analytics:PostHog"));
builder.Services.AddSingleton<IAnalyticsService, PostHogAnalyticsService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IBillingService, StripeBillingService>();
builder.Services.AddScoped<IEmailSettingsService, EmailSettingsService>();
builder.Services.AddSingleton<IAiMappingService, OpenAiMappingService>();
builder.Services.AddScoped<IAiUsageTracker, AiUsageTracker>();
builder.Services.AddScoped<IPoMappingService, PoMappingService>();
builder.Services.AddSingleton<DeliveryEncryptionService>();
builder.Services.AddSingleton<ProcuLink.Infrastructure.Services.Security.OutboundRequestGuard>();
// Group O reliability: retry-queue backoff + SLA window tunables (section Delivery:Reliability).
// Mirrors API/Program.cs. The Worker executes the scheduled RetryDeliveryJob and the SLA sweep.
builder.Services.AddSingleton(sp =>
{
    var opts = new DeliveryReliabilityOptions();
    builder.Configuration.GetSection(DeliveryReliabilityOptions.SectionName).Bind(opts);
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

builder.Services.AddSingleton<IPurchaseOrderParser, CsvOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, XlsxOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, PdfOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, CxmlOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, UblOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, EdifactOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, X12OrderParser>(); // Group M — ANSI X12 850
builder.Services.AddSingleton<OrderParserFactory>();
builder.Services.AddSingleton<ITransformService, XmlTransformService>();
builder.Services.AddSingleton<ITransformService, CsvTransformService>();
builder.Services.AddSingleton<ITransformService, CxmlTransformService>();
builder.Services.AddSingleton<ITransformService, JsonTransformService>();
builder.Services.AddSingleton<ITransformService, UblOrderTransformService>(); // Group M Phase 1 — UBL 2.1 Peppol BIS 3.0
builder.Services.AddSingleton<ITransformService, X12TransformService>(); // Group M — ANSI X12 850

// ── Canonical-model output transforms (ParsedOrder → standards document) ────
builder.Services.AddSingleton<IParsedOrderTransform, UblParsedOrderTransform>();     // UBL 2.1 Order
builder.Services.AddSingleton<IParsedOrderTransform, X12ParsedOrderTransform>();     // ANSI X12 850
builder.Services.AddSingleton<IParsedOrderTransform, EdifactParsedOrderTransform>(); // UN/EDIFACT ORDERS D.96A
builder.Services.AddSingleton<ParsedOrderTransformFactory>();

// ── Wave 2: pull-ingress (SFTP / S3-R2) + OCR fallback ────────────────────
builder.Services.AddSingleton<ISftpClientFactory, RenciSftpClientFactory>();
builder.Services.AddScoped<ISftpIngressService, SftpIngressService>();
builder.Services.AddSingleton<IAmazonS3ClientFactory, AmazonS3ClientFactory>();
builder.Services.AddScoped<IS3IngressService, S3IngressService>();
// IEmailBodyOrderExtractor intentionally NOT registered here: OpenAiEmailBodyOrderExtractor
// depends on ICurrentTenantService (HttpContext-based) which only exists in the API.
// Email-body NLP runs in the API's InboundEmailController scope (Postmark webhook),
// not in Worker jobs. Registering it here triggers DI validation failure at Host.Build().

if (!string.IsNullOrWhiteSpace(builder.Configuration["Ocr:Azure:Endpoint"])
    && !string.IsNullOrWhiteSpace(builder.Configuration["Ocr:Azure:ApiKey"]))
{
    builder.Services.AddSingleton<IDocumentOcrService, AzureDocumentIntelligenceOcrService>();
}
else
{
    builder.Services.AddSingleton<IDocumentOcrService, NoOpOcrService>();
}

// ── Phase 6: smart format auto-detect + HMAC webhook receive ──────────────
// Mirrors API/Program.cs lines 270-272. Currently used only by API controllers,
// but registered here too so future background jobs in this dep graph
// (e.g. retry queue, ACK round-trip) can resolve them without a second DI fix.
builder.Services.AddMemoryCache(); // shared cache used by HmacWebhookVerifier nonce replay store
builder.Services.AddScoped<ProcuLink.Core.Services.Detection.IFormatDetector, ProcuLink.Infrastructure.Services.Detection.FormatDetectorService>();
builder.Services.AddScoped<ProcuLink.Core.Services.Webhooks.IHmacWebhookVerifier, ProcuLink.Infrastructure.Services.Webhooks.HmacWebhookVerifier>();

builder.Services.AddScoped<EmailPollingJob>();
builder.Services.AddScoped<SftpPollingJob>();
builder.Services.AddScoped<S3PollingJob>();
// P0 reliability: stuck-order detection sweep + operator retry job.
builder.Services.AddScoped<IStuckOrderDetectionService, StuckOrderDetectionService>();
builder.Services.AddScoped<StuckOrderDetectionJob>();
// Group O reliability: automatic delivery retry queue (scheduled here) + SLA breach sweep.
builder.Services.AddScoped<RetryDeliveryJob>();
builder.Services.AddScoped<DeliverySlaSweepJob>();
// ParseOrderJob (executed here) records schema fingerprints — register the service it depends on.
builder.Services.AddScoped<ProcuLink.Core.Services.Detection.ISchemaFingerprintService, ProcuLink.Infrastructure.Services.Detection.SchemaFingerprintService>();
// ParseOrderJob lives in ProcuLink.Api but is enqueued on "default" — Worker executes it.
builder.Services.AddScoped<ParseOrderJob>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// ── Analytics graceful flush on shutdown ─────────────────────────────────
// Mirrors API/Program.cs lines 405-409 — drain queued PostHog events on
// SIGTERM before the Hangfire server stops accepting work.
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
{
    var svc = host.Services.GetRequiredService<IAnalyticsService>();
    try { svc.FlushAsync(default).GetAwaiter().GetResult(); } catch { /* swallow */ }
});

// ── Startup configuration validation ─────────────────────────────────────
// Fails fast in Production with a single combined error listing every missing
// required key. Non-production environments log warnings instead.
{
    var startupLogger = host.Services
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("ProcuLink.Worker.Startup");
    StartupConfigurationValidator.Validate(
        configuration:   host.Services.GetRequiredService<IConfiguration>(),
        logger:          startupLogger,
        environmentName: host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName,
        requiredKeys:    StartupConfigurationValidator.WorkerRequiredKeys,
        optionalKeys:    StartupConfigurationValidator.OptionalKeys,
        componentName:   "ProcuLink.Worker");
}

host.Run();
