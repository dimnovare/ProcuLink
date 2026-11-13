using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services;
using ProcuLink.Api.Telemetry;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Alerting;
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
using ProcuLink.Infrastructure.Services.Alerting;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Email;
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
// The API wires Sentry on its WebHost (see the UseSentry block in Api/Program.cs). The Worker is a
// generic host with no WebHost, so background-job exceptions were previously
// invisible. Wire Sentry through the logging pipeline instead: AddSentry routes
// log events >= Error to Sentry, and AddSentry also initialises the global
// SentrySdk hub, so jobs can call SentrySdk.CaptureException/CaptureMessage
// directly (e.g. the alert job) without any extra wiring.
// When Sentry:Dsn is empty the SDK initialises disabled — a safe no-op.
builder.Logging.AddSentry(o =>
{
    o.Dsn = builder.Configuration["Sentry:Dsn"] ?? string.Empty;
    // Breadcrumb level stays at Information ON PURPOSE — see the same note in the API host. It is
    // what makes a captured job exception diagnosable, and raising it would not close the leak
    // (a secret logged at Error ships just as fast). Redaction is the targeted fix.
    o.MinimumBreadcrumbLevel = LogLevel.Information;
    o.MinimumEventLevel = LogLevel.Error;

    // P1 telemetry hygiene: this host had NO BeforeSend of any kind, so nothing was scrubbed at
    // all — while being the host that fires customer webhooks, whose URLs ARE the credential.
    // Same shared registration the API uses, so the two hosts cannot drift.
    o.UseProcuLinkScrubbing();
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

builder.Services.AddDbContext<ProcuLinkDbContext>(options => options.UseNpgsql(connectionString));

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

builder.Services.AddScoped<IOrderExceptionService, OrderExceptionService>();
// Analytics (PostHog) — required by StripeBillingService (resolved via IBillingService
// in ParseOrderJob). No-op when Analytics:PostHog:ApiKey is absent.
// Mirrors API/Program.cs lines 189-190.
builder.Services.Configure<PostHogOptions>(builder.Configuration.GetSection("Analytics:PostHog"));
builder.Services.Configure<ProcuLink.Api.Services.Documents.DocumentEngineOptions>(builder.Configuration.GetSection("DocumentEngine"));
builder.Services.AddSingleton<ProcuLink.Api.Services.Documents.DocumentEngineGate>();
builder.Services.Configure<ProcuLink.Api.Services.Documents.ValidatorOptions>(builder.Configuration.GetSection("Validator"));
builder.Services.AddHttpClient(ProcuLink.Api.Services.Documents.PhormValidator.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<ProcuLink.Core.Documents.IStandardValidator, ProcuLink.Api.Services.Documents.PhormValidator>();
builder.Services.AddScoped<ProcuLink.Api.Services.Documents.DocumentRechecker>();
builder.Services.AddScoped<ProcuLink.Api.Services.Documents.DocumentSender>();
builder.Services.AddScoped<ProcuLink.Api.Services.Documents.DocumentIntakeService>();
builder.Services.AddScoped<ProcuLink.Core.Documents.IFlowDocumentSubmitter>(sp => sp.GetRequiredService<ProcuLink.Api.Services.Documents.DocumentIntakeService>());

// -- Flow intake pickups: SFTP / FTP / FTPS (more kinds register here) --
builder.Services.AddSingleton<ProcuLink.Api.Services.Documents.Intake.IPickupSource, ProcuLink.Api.Services.Documents.Intake.SftpPickupSource>();
builder.Services.AddSingleton<ProcuLink.Api.Services.Documents.Intake.IPickupSource>(sp => new ProcuLink.Api.Services.Documents.Intake.FtpPickupSource(
    ProcuLink.Core.Documents.IntakeKind.Ftp, sp.GetRequiredService<ProcuLink.Infrastructure.Services.Ingress.IFtpFetchClientFactory>(), sp.GetRequiredService<ProcuLink.Infrastructure.Services.Security.OutboundRequestGuard>()));
builder.Services.AddSingleton<ProcuLink.Api.Services.Documents.Intake.IPickupSource>(sp => new ProcuLink.Api.Services.Documents.Intake.FtpPickupSource(
    ProcuLink.Core.Documents.IntakeKind.Ftps, sp.GetRequiredService<ProcuLink.Infrastructure.Services.Ingress.IFtpFetchClientFactory>(), sp.GetRequiredService<ProcuLink.Infrastructure.Services.Security.OutboundRequestGuard>()));
builder.Services.AddSingleton<ProcuLink.Api.Services.Documents.Intake.IPickupSource>(sp => new ProcuLink.Api.Services.Documents.Intake.S3PickupSource(
    sp.GetRequiredService<IAmazonS3ClientFactory>(), sp.GetRequiredService<ProcuLink.Infrastructure.Services.Security.OutboundRequestGuard>()));
builder.Services.AddSingleton<ProcuLink.Api.Services.Documents.Intake.IPickupSource>(sp => new ProcuLink.Api.Services.Documents.Intake.ImapPickupSource(
    sp.GetRequiredService<ProcuLink.Infrastructure.Services.Security.OutboundRequestGuard>(),
    sp.GetRequiredService<ProcuLink.Api.Services.Documents.DocumentEngineGate>()));
builder.Services.AddScoped<ProcuLink.Api.Jobs.FlowPickupJob>();
builder.Services.AddScoped<ProcuLink.Api.Jobs.FlowWebhookJob>();
builder.Services.AddScoped<ProcuLink.Api.Jobs.FlowPickupDispatchJob>();
builder.Services.AddScoped<ProcuLink.Api.Jobs.DocumentNotificationJob>();
builder.Services.AddScoped<ProcuLink.Api.Jobs.NotificationMembersJob>();
builder.Services.AddHttpClient("clerk", c => { c.BaseAddress = new Uri("https://api.clerk.com/v1/"); c.Timeout = TimeSpan.FromSeconds(30); });
// Notification digest email: Postmark, else SMTP, else log-only (same choice as the API).
if (!string.IsNullOrWhiteSpace(builder.Configuration["Email:Postmark:ServerToken"]))
    builder.Services.AddScoped<IEmailSender, PostmarkEmailSender>();
else if (!string.IsNullOrWhiteSpace(builder.Configuration["Smtp:Host"]))
    builder.Services.AddScoped<IEmailSender, MailKitEmailSender>();
else
    builder.Services.AddScoped<IEmailSender, ConsoleEmailSender>();
builder.Services.AddScoped<ProcuLink.Api.Services.Documents.FlowDestinationService>();
builder.Services.AddSingleton<IAnalyticsService, PostHogAnalyticsService>();
builder.Services.AddScoped<IBillingService, StripeBillingService>();
// Billing safety net: reconciles plan/account_status against Stripe as source of truth
// (driven by the daily BillingReconciliationJob). Registered here because the Worker is the
// sole Hangfire executor and Development DI validation constructs the job's dependency graph.
builder.Services.AddScoped<IBillingReconciliationService, StripeSubscriptionReconciliationService>();
builder.Services.AddScoped<IAiUsageTracker, AiUsageTracker>();
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
// jobs. Flag-gated by Connections:RevisionAuthority. The CODE default is OFF (live tables,
// byte-identical), but that is NOT the deployed value: production sets
// Connections__RevisionAuthority = true on the Railway "aware-amazement" service, so revision
// authority is ON for the host that actually runs parse/transform/deliver. This process serves no
// HTTP, so its effective value is announced in the startup log (StartupConfigurationValidator)
// rather than on a health endpoint. See ProcuLink.Infrastructure/Services/RevisionAuthorityHosts.cs
// and docs/ops/revision-authority-production-smoke.md.
builder.Services.AddScoped<IEffectiveConnectionConfigResolver, EffectiveConnectionConfigResolver>();
builder.Services.AddScoped<IDeliveryConfigService, DeliveryConfigService>();

// ── Wave 4 reliability/observability: worker-health alert sweep + retention ──
// The recurring jobs are scheduled in Worker.cs; these are their DI deps.
// Alerts fan out to Sentry AND email; each is a no-op when unconfigured, and each reports that it
// delivered nothing rather than claiming success. StartupConfigurationValidator refuses to boot
// this host in Production with no working destination at all.
builder.Services.AddScoped<IOpsHealthService, OpsHealthService>();
// WP-37 "page the founder" — the whole alerting graph: the recurring sweep, the probe behind its
// three extra conditions, the tunables, and the transports the alerts route to. It lives in
// WorkerAlertingRegistration so the wiring guard can resolve THIS graph instead of pattern-matching
// this file; see that class for why the previous source-regex guard could not see a deleted sink.
builder.Services.AddWorkerAlerting(builder.Configuration);
// Liveness beat: proves the recurring-job dispatcher is actually firing (a log
// line + Sentry breadcrumb every 2 min), complementing Hangfire's server heartbeat.
builder.Services.AddScoped<WorkerHeartbeatJob>();
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
// Erases leftover production-smoke workspaces (empty, old, unsubscribed) through IOrganisationEraser.
builder.Services.AddScoped<IOrganisationEraser, OrganisationEraser>();
builder.Services.AddScoped<DisposableOrgSweepJob>();

builder.Services.AddScoped<IErpConnector, ErplyConnector>();
builder.Services.AddScoped<IErpConnector, DirectoConnector>();
// Email API (Postmark over HTTPS) — the Worker runs TransformOrderJob → DeliveryService, so the
// email delivery dispatcher resolves IEmailApiClient HERE too (DI foot-gun precedent 4607d6d).
builder.Services.AddHttpClient(PostmarkEmailApiClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<IEmailApiClient, PostmarkEmailApiClient>();
builder.Services.AddScoped<IDeliveryDispatcher, HttpDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, SftpDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, FtpsDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, S3DeliveryDispatcher>();
// Email delivery via HTTP email API — works where outbound SMTP is blocked.
builder.Services.AddScoped<IDeliveryDispatcher, EmailApiDeliveryDispatcher>();
// Legacy raw-SMTP dispatcher: retired from offered channels; self-host opt-in only.
if (builder.Configuration.GetValue<bool>("Delivery:EnableSmtp"))
    builder.Services.AddScoped<IDeliveryDispatcher, SmtpDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, ErplyDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, DirectoDeliveryDispatcher>();

// SourceMap engine tokenizer: the Worker's parse job (ParseOrderJob → OrderService →
// OrderIngestionService.ParseStoredFileAsync) needs this to capture the FULL source-token set
// for structured formats into source_captures. Singleton — stateless, mirrors the Api registration.
builder.Services.AddSingleton<ProcuLink.Transform.Tokenizing.ISourceTokenizer, ProcuLink.Transform.Tokenizing.SourceTokenizer>();
// Registered FROM OutputTransformRegistry.All — the same list that derives the buildable-format
// allow-list every output-format write path checks against. See the Api registration for why the
// two must not be typed out separately.
builder.Services.AddOutputTransforms();

// ── Wave 2: pull-ingress (SFTP / S3-R2) + OCR fallback ────────────────────
builder.Services.AddSingleton<ISftpClientFactory, RenciSftpClientFactory>();
builder.Services.AddSingleton<IAmazonS3ClientFactory, AmazonS3ClientFactory>();
// FTP/FTPS fetch seam for the flow pickups (FtpPickupSource).
builder.Services.AddSingleton<IFtpFetchClientFactory, FluentFtpFetchClientFactory>();

// PDF rasterizer for the vision fallback (scanned/no-text PDFs). The Worker runs
// ParseOrderJob, so it needs the same registration as the API.
builder.Services.AddSingleton<IPdfRasterizer, SkiaPdfRasterizer>();
builder.Services.AddSingleton<IDocumentReader, OpenAiDocumentReader>();
// OCR seam. The Worker runs ParseOrderJob, so this is where self-hosted OCR executes.
// Opt-in (RapidOcrNet) only when enabled — otherwise a no-op (no models loaded).
if (builder.Configuration.GetValue<bool>("NoEgressOcr:Enabled"))
    builder.Services.AddSingleton<IDocumentOcrService, RapidOcrDocumentOcrService>();
else
    builder.Services.AddSingleton<IDocumentOcrService, NoOpOcrService>();

builder.Services.AddScoped<BillingReconciliationJob>();

builder.Services.AddScoped<ReadDocumentJob>();
builder.Services.AddScoped<DeliverDocumentJob>();
builder.Services.AddScoped<SendDocumentJob>();
builder.Services.AddScoped<DocumentDeliverySweepJob>();

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
        componentName:   "ProcuLink.Worker",
        // This host runs the operator-alert sweep, so it must be able to deliver an alert. With no
        // destination configured there is nowhere to report that fact at runtime, which is why it
        // is a Production startup failure rather than another log line nobody receives.
        raisesOperatorAlerts: true);
}

host.Run();
