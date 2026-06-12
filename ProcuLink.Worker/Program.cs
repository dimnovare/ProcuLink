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
using Sentry;
using Sentry.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// ── Sentry error tracking — no-op when DSN is absent ─────────────────────────
// The API wires Sentry on its WebHost (Api/Program.cs:42-48). The Worker is a
// generic host with no WebHost, so background-job exceptions were previously
// invisible. Wire Sentry through the logging pipeline instead: AddSentry routes
// log events >= Error to Sentry, and AddSentry also initialises the global
// SentrySdk hub, so jobs can call SentrySdk.CaptureException/CaptureMessage
// directly (e.g. the alert job) without any extra wiring.
// When Sentry:Dsn is empty the SDK initialises disabled — a safe no-op.
builder.Logging.AddSentry(o =>
{
    o.Dsn = builder.Configuration["Sentry:Dsn"] ?? string.Empty;
    o.MinimumBreadcrumbLevel = LogLevel.Information;
    o.MinimumEventLevel = LogLevel.Error;
});

// ── R2 clock-skew correction ────────────────────────────────────────────────
// R2 returns SignatureDoesNotMatch (not RequestTimeTooSkewed) when a request's
// timestamp is outside tolerance, which defeats the AWS SDK's automatic clock-skew
// correction (it only triggers on RequestTimeTooSkewed). A worker container whose
// clock has drifted therefore fails EVERY SigV4 request to R2 with a signature
// error — observed intermittently in production. We probe R2's Date response header
// once at startup and apply a global manual clock correction when the skew is
// material, so this container's R2 signing always uses R2-relative time.
try
{
    var r2Endpoint = builder.Configuration["Storage:R2Endpoint"];
    if (!string.IsNullOrWhiteSpace(r2Endpoint))
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var probeReq = new HttpRequestMessage(HttpMethod.Head, r2Endpoint);
        var probeResp = probe.Send(probeReq);
        var serverDate = probeResp.Headers.Date;
        if (serverDate.HasValue)
        {
            var offset = serverDate.Value.UtcDateTime - DateTime.UtcNow;
            Console.WriteLine($"[R2-CLOCK] offsetSeconds={offset.TotalSeconds:F1}");
            if (Math.Abs(offset.TotalSeconds) > 5)
            {
                Amazon.AWSConfigs.ManualClockCorrection = offset;
                Console.WriteLine($"[R2-CLOCK] applied ManualClockCorrection={offset.TotalSeconds:F1}s");
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[R2-CLOCK] probe failed (non-fatal): {ex.Message}");
}

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

// Cap the Npgsql pool in code so it applies regardless of the env-injected
// connection string (prod string comes from Railway env). Without an explicit
// ceiling each process would use Npgsql's default Maximum Pool Size of 100, so
// API + Worker = two ~100-conn pools, well past Neon's ~100-connection cap.
// The Worker (Hangfire WorkerCount=10) gets 20 — a head per worker plus margin
// for recurring/polling jobs; the API gets 30. Both the EF DbContext and the
// Hangfire Postgres storage share this single capped string below.
// ConnectionIdleLifetime returns idle pooled connections to Neon so the pool
// doesn't pin its ceiling. Existing settings in the string are preserved.
connectionString = BuildPooledConnectionString(connectionString, maxPoolSize: 20);

static string? BuildPooledConnectionString(string? raw, int maxPoolSize)
{
    if (string.IsNullOrWhiteSpace(raw))
        return raw;

    var b = new Npgsql.NpgsqlConnectionStringBuilder(raw)
    {
        MaxPoolSize = maxPoolSize,
        ConnectionIdleLifetime = 60,   // seconds an idle pooled conn lives before close
        ConnectionPruningInterval = 10 // seconds between idle-pruning sweeps
    };
    return b.ConnectionString;
}

// P2 hardening: same log-only order state-machine observer as the API host — delivery,
// stuck-detection, and job-driven Status writes happen here too. Warnings only; never blocks.
builder.Services.AddSingleton<ProcuLink.Infrastructure.Services.OrderStatusTransitionObserver>();
builder.Services.AddDbContext<ProcuLinkDbContext>((sp, options) =>
    options.UseNpgsql(connectionString)
        .AddInterceptors(sp.GetRequiredService<ProcuLink.Infrastructure.Services.OrderStatusTransitionObserver>()));

builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer(opts =>
{
    // Named queues by workload type — prevents polling bursts from starving parse/delivery.
    // Priority order: Hangfire processes queues left-to-right, pulling from the next only when
    // the higher-priority queue is empty.
    opts.WorkerCount = 10;
    opts.Queues = new[] { "critical", "delivery-retry", "polling", "background", "default" };
});

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
    sp.GetRequiredService<ProcuLink.Infrastructure.Services.Security.OutboundRequestGuard>()
      .CreateGuardedHttpHandler());

if (string.IsNullOrEmpty(builder.Configuration["Storage:R2AccessKeyId"]))
    builder.Services.AddSingleton<IFileStorageService, LocalFileStorageService>();
else
    builder.Services.AddSingleton<IFileStorageService, R2StorageService>();

builder.Services.AddScoped<IItemMappingService, ItemMappingService>();
builder.Services.AddScoped<IOrderExceptionService, OrderExceptionService>();
// Wave 4: IntegrationTriggerService is needed by OrderService and DeliveryService.
// Register it here so Worker DI validation passes (same as API/Program.cs line 198).
builder.Services.AddScoped<IIntegrationTriggerService, IntegrationTriggerService>();
// Analytics (PostHog) — required by StripeBillingService (resolved via IBillingService
// in EmailPollingJob) and ParseOrderJob. No-op when Analytics:PostHog:ApiKey is absent.
// Mirrors API/Program.cs lines 189-190.
builder.Services.Configure<PostHogOptions>(builder.Configuration.GetSection("Analytics:PostHog"));
builder.Services.AddSingleton<IAnalyticsService, PostHogAnalyticsService>();
builder.Services.AddScoped<IOrderService, OrderService>();
// Durable AI-suggestion decision history — an optional OrderService dependency; register it
// so the resolve/accept paths persist history when driven from the Worker too.
builder.Services.AddScoped<IAiSuggestionDecisionService, AiSuggestionDecisionService>();
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
// Launch batch 7 — revision authority: pinned-revision config bundle resolver, consumed by the
// Worker's parse (ParseOrderJob → OrderService), transform (TransformOrderJob), and delivery
// jobs. Flag-gated by Connections:RevisionAuthority (default OFF = live tables, byte-identical).
builder.Services.AddScoped<IEffectiveConnectionConfigResolver, EffectiveConnectionConfigResolver>();
builder.Services.AddScoped<IDeliveryConfigService, DeliveryConfigService>();
builder.Services.AddScoped<IDeliveryService, DeliveryService>();
builder.Services.AddScoped<IDeliverySlaService, DeliverySlaService>();

// ── Wave 4 reliability/observability: worker-health alert sweep + retention ──
// The recurring jobs are scheduled in Worker.cs; these are their DI deps.
// Alert sink is Sentry-backed and a safe no-op when Sentry DSN is unset.
builder.Services.AddScoped<IOpsHealthService, OpsHealthService>();
builder.Services.AddSingleton<IWorkerAlertSink, SentryWorkerAlertSink>();
builder.Services.AddSingleton<WorkerHealthAlertState>(); // singleton: cross-run rate-limit state (no scoped DbContext captured)
builder.Services.AddScoped<IWorkerHealthAlertService, WorkerHealthAlertService>();
builder.Services.AddScoped<WorkerHealthAlertJob>();
builder.Services.AddSingleton(_ =>
{
    var opts = new WorkerHealthAlertOptions();
    builder.Configuration.GetSection(WorkerHealthAlertOptions.SectionName).Bind(opts);
    return opts;
});
// Data-retention sweep: DISABLED by default; tune via the "DataRetention" section.
builder.Services.AddSingleton(_ =>
{
    var opts = new DataRetentionOptions();
    builder.Configuration.GetSection(DataRetentionOptions.SectionName).Bind(opts);
    return opts;
});
builder.Services.AddScoped<IDataRetentionService, DataRetentionService>();
builder.Services.AddScoped<DataRetentionSweepJob>();
// Blob-retention sweep (file blobs only): per-org opt-in via organisations.retention_days
// (NULL = disabled, the default) AND the global Retention:DryRun latch (default TRUE —
// audit-only). Both must be deliberately flipped before anything is actually deleted.
builder.Services.AddSingleton(_ =>
{
    var opts = new BlobRetentionOptions();
    builder.Configuration.GetSection(BlobRetentionOptions.SectionName).Bind(opts);
    return opts;
});
builder.Services.AddScoped<IBlobRetentionService, BlobRetentionService>();
builder.Services.AddScoped<BlobRetentionSweepJob>();

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
builder.Services.AddSingleton<IPurchaseOrderParser, IDocOrders05Parser>(); // SAP IDoc ORDERS05 (.xml, root <ORDERS05>)
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

// ── Catalog import channels: SFTP/FTP(S) pull (plan 2026-06-12) ────────────
// DI foot-gun precedent 4607d6d: ISupplierCatalogService was Api-only — the Worker
// executes CatalogSyncSourceJob, so the sink + pull pipeline must resolve here too.
builder.Services.AddScoped<ISupplierCatalogService, SupplierCatalogService>();
builder.Services.AddSingleton<IFtpFetchClientFactory, FluentFtpFetchClientFactory>();
builder.Services.AddScoped<ProcuLink.Core.Services.Catalog.ICatalogPullService,
                           ProcuLink.Infrastructure.Services.Catalog.CatalogPullService>();
builder.Services.AddScoped<CatalogSyncDispatcherJob>();
builder.Services.AddScoped<CatalogSyncSourceJob>();
// IEmailBodyOrderExtractor intentionally NOT registered here: OpenAiEmailBodyOrderExtractor
// depends on ICurrentTenantService (HttpContext-based) which only exists in the API.
// Email-body NLP runs in the API's InboundEmailController scope (Postmark webhook),
// not in Worker jobs. Registering it here triggers DI validation failure at Host.Build().

// PDF rasterizer for the vision fallback (scanned/no-text PDFs). The Worker runs
// ParseOrderJob, so it needs the same registration as the API.
builder.Services.AddSingleton<IPdfRasterizer, SkiaPdfRasterizer>();
// Primary PDF path: text → LLM structured extraction. The Worker runs ParseOrderJob,
// so it must register the same singleton extractor as the API (orgId is a method
// param + tracker resolved via IServiceScopeFactory — no ICurrentTenantService).
builder.Services.AddSingleton<IStructuredOrderExtractor, OpenAiPdfOrderExtractor>();
// OCR seam. The Worker runs ParseOrderJob, so this is where self-hosted OCR executes.
// Opt-in (RapidOcrNet) only when enabled — otherwise a no-op (no models loaded).
if (builder.Configuration.GetValue<bool>("NoEgressOcr:Enabled"))
    builder.Services.AddSingleton<IDocumentOcrService, RapidOcrDocumentOcrService>();
else
    builder.Services.AddSingleton<IDocumentOcrService, NoOpOcrService>();

// ── Phase 6: smart format auto-detect + HMAC webhook receive ──────────────
// Mirrors API/Program.cs lines 270-272. Currently used only by API controllers,
// but registered here too so future background jobs in this dep graph
// (e.g. retry queue, ACK round-trip) can resolve them without a second DI fix.
// IDistributedCache for HmacWebhookVerifier nonce replay store.
// MemoryDistributedCache is single-instance; swap for Redis when horizontal scaling is needed:
//   builder.Services.AddStackExchangeRedisCache(o => o.Configuration = config["Redis:ConnectionString"]);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddScoped<ProcuLink.Core.Services.Detection.IFormatDetector, ProcuLink.Infrastructure.Services.Detection.FormatDetectorService>();
builder.Services.AddScoped<ProcuLink.Core.Services.Webhooks.IHmacWebhookVerifier, ProcuLink.Infrastructure.Services.Webhooks.HmacWebhookVerifier>();

builder.Services.AddScoped<EmailPollingJob>();
builder.Services.AddScoped<EmailPollOrgJob>();
builder.Services.AddScoped<SftpPollingJob>();
builder.Services.AddScoped<SftpPollOrgJob>();
builder.Services.AddScoped<S3PollingJob>();
builder.Services.AddScoped<S3PollOrgJob>();
// P0 reliability: stuck-order detection sweep + operator retry job.
builder.Services.AddScoped<IStuckOrderDetectionService, StuckOrderDetectionService>();
builder.Services.AddScoped<StuckOrderDetectionJob>();
// StuckOrderDetectionService re-enqueues stuck 'parsing' orders through IParseJobEnqueuer.
// That dependency is optional, so without this registration the Worker would reset stuck
// orders to 'pending_parse' but never enqueue a fresh parse job — nothing would drive them.
// The concrete adapter lives in ProcuLink.Api (alongside Hangfire + ParseOrderJob) and needs
// only IBackgroundJobClient, which this Worker already provides.
builder.Services.AddScoped<IParseJobEnqueuer, ProcuLink.Api.Controllers.HangfireParseJobEnqueuer>();
// Group O reliability: automatic delivery retry queue (scheduled here) + SLA breach sweep.
builder.Services.AddScoped<RetryDeliveryJob>();
builder.Services.AddScoped<DeliverySlaSweepJob>();
// Delivery-path hardening: recover orders stranded in 'delivering' (e.g. a Worker restart /
// download throw / dispatcher hang between the status write and the terminal attempt persist).
// The sweep re-drives them through RetryDeliveryJob via IRetryDeliveryEnqueuer up to a bounded
// cap, then dead-letters — guaranteeing a delivering→terminal transition. The Hangfire adapter
// needs only IBackgroundJobClient, which this Worker already provides.
builder.Services.AddScoped<IRetryDeliveryEnqueuer, ProcuLink.Infrastructure.Jobs.HangfireRetryDeliveryEnqueuer>();
builder.Services.AddScoped<IStuckDeliveryDetectionService, StuckDeliveryDetectionService>();
builder.Services.AddScoped<StuckDeliveryDetectionJob>();
// ParseOrderJob (executed here) records schema fingerprints — register the service it depends on.
builder.Services.AddScoped<ProcuLink.Core.Services.Detection.ISchemaFingerprintService, ProcuLink.Infrastructure.Services.Detection.SchemaFingerprintService>();
// ParseOrderJob lives in ProcuLink.Api but is enqueued on "default" — Worker executes it.
builder.Services.AddScoped<ParseOrderJob>();
builder.Services.AddHostedService<Worker>();
// Warm the self-hosted OCR models at startup (no-op unless NoEgressOcr:Enabled) so the
// first scanned-PDF OCR after a deploy doesn't pay the model-load cost inside a job.
builder.Services.AddHostedService<ProcuLink.Worker.OcrWarmupHostedService>();

var host = builder.Build();

// ── Analytics graceful flush on shutdown ─────────────────────────────────
// Mirrors API/Program.cs lines 405-409 — drain queued PostHog events on
// SIGTERM before the Hangfire server stops accepting work.
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
{
    var svc = host.Services.GetRequiredService<IAnalyticsService>();
    try { svc.FlushAsync(default).GetAwaiter().GetResult(); } catch { /* swallow */ }
    // Drain any pending Sentry events before the process exits (no-op when Sentry is disabled).
    try { SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch { /* swallow */ }
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
