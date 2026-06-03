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

// ── R2 clock-skew diagnostic + correction ──────────────────────────────────
// R2 returns SignatureDoesNotMatch (not RequestTimeTooSkewed) when the request
// timestamp is outside tolerance, which defeats the AWS SDK's automatic clock-skew
// correction (it only triggers on RequestTimeTooSkewed). If this container's clock
// is skewed, every SigV4 request to R2 fails. We probe R2's Date response header
// once at startup, log the offset, and apply a global manual correction when the
// skew is material so all subsequent R2 signing uses the corrected time.
try
{
    var r2Endpoint = builder.Configuration["Storage:R2Endpoint"];
    if (!string.IsNullOrWhiteSpace(r2Endpoint))
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var probeReq = new HttpRequestMessage(HttpMethod.Head, r2Endpoint);
        var probeResp = probe.Send(probeReq);
        var serverDate = probeResp.Headers.Date;
        var localNow = DateTime.UtcNow;
        if (serverDate.HasValue)
        {
            var offset = serverDate.Value.UtcDateTime - localNow;
            Console.WriteLine($"[R2-CLOCK] workerUtcNow={localNow:O} r2ServerDate={serverDate.Value.UtcDateTime:O} offsetSeconds={offset.TotalSeconds:F1}");
            if (Math.Abs(offset.TotalSeconds) > 30)
            {
                Amazon.AWSConfigs.ManualClockCorrection = offset;
                Console.WriteLine($"[R2-CLOCK] applied ManualClockCorrection={offset.TotalSeconds:F1}s");
            }
            else
            {
                Console.WriteLine("[R2-CLOCK] clock within tolerance; no correction applied.");
            }
        }
        else
        {
            Console.WriteLine($"[R2-CLOCK] R2 returned no Date header (status {(int)probeResp.StatusCode}); cannot assess skew.");
        }

        // ── R2 signed-request diagnostic ─────────────────────────────────────
        // Generate a pre-signed GET for a known-existing object and fetch it. On a
        // 403 the R2 body contains the CanonicalRequest/StringToSign it computed,
        // which pinpoints the signing mismatch. Also log the generated URL so it can
        // be diffed against a known-good URL generated off-container.
        try
        {
            var ak  = builder.Configuration["Storage:R2AccessKeyId"]!;
            var sk  = builder.Configuration["Storage:R2SecretAccessKey"]!;
            var bkt = builder.Configuration["Storage:R2BucketName"]!;
            var s3cfg = new Amazon.S3.AmazonS3Config { ServiceURL = r2Endpoint, ForcePathStyle = true, AuthenticationRegion = "auto" };
            using var s3 = new Amazon.S3.AmazonS3Client(ak, sk, s3cfg);
            const string knownKey = "00000000-0000-0000-0000-000000000000/a4d9896f-d015-4173-a4d1-ec9dd167c080/artifacts/bcfef4c7-8108-4a58-90d9-a1821b952e12.xml";
            var signed = s3.GetPreSignedURL(new Amazon.S3.Model.GetPreSignedUrlRequest { BucketName = bkt, Key = knownKey, Verb = Amazon.S3.HttpVerb.GET, Expires = DateTime.UtcNow.AddMinutes(15) });
            Console.WriteLine($"[R2-DIAG] presignedUrl={signed}");
            using var dh = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var dr = dh.GetAsync(signed).GetAwaiter().GetResult();
            var body = dr.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Console.WriteLine($"[R2-DIAG] presigned GET status={(int)dr.StatusCode} bodyLen={body.Length}");
            if (!dr.IsSuccessStatusCode) Console.WriteLine($"[R2-DIAG] body={body.Substring(0, Math.Min(900, body.Length))}");
        }
        catch (Exception dex) { Console.WriteLine($"[R2-DIAG] signed-request probe failed: {dex.Message}"); }
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

builder.Services.AddDbContext<ProcuLinkDbContext>(options =>
    options.UseNpgsql(connectionString));

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

builder.Services.AddHttpClient("delivery", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

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
