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
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Erp;
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
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IBillingService, StripeBillingService>();
builder.Services.AddScoped<IEmailSettingsService, EmailSettingsService>();
builder.Services.AddSingleton<IAiMappingService, OpenAiMappingService>();
builder.Services.AddScoped<IAiUsageTracker, AiUsageTracker>();
builder.Services.AddScoped<IPoMappingService, PoMappingService>();
builder.Services.AddSingleton<DeliveryEncryptionService>();
builder.Services.AddScoped<IDeliveryConfigService, DeliveryConfigService>();
builder.Services.AddScoped<IDeliveryService, DeliveryService>();
builder.Services.AddScoped<IErpConnector, ErplyConnector>();
builder.Services.AddScoped<IErpConnector, DirectoConnector>();
builder.Services.AddScoped<IDeliveryDispatcher, HttpDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, ErplyDeliveryDispatcher>();
builder.Services.AddScoped<IDeliveryDispatcher, DirectoDeliveryDispatcher>();

builder.Services.AddSingleton<IPurchaseOrderParser, CsvOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, XlsxOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, PdfOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, CxmlOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, UblOrderParser>();
builder.Services.AddSingleton<IPurchaseOrderParser, EdifactOrderParser>();
builder.Services.AddSingleton<OrderParserFactory>();
builder.Services.AddSingleton<ITransformService, XmlTransformService>();
builder.Services.AddSingleton<ITransformService, CsvTransformService>();
builder.Services.AddSingleton<ITransformService, CxmlTransformService>();
builder.Services.AddSingleton<ITransformService, JsonTransformService>();

builder.Services.AddScoped<EmailPollingJob>();
// ParseOrderJob lives in ProcuLink.Api but is enqueued on "default" — Worker executes it.
builder.Services.AddScoped<ParseOrderJob>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

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
