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

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

builder.Services.AddDbContext<ProcuLinkDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer(opts =>
{
    opts.WorkerCount = 2;
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
builder.Services.AddSingleton<OrderParserFactory>();
builder.Services.AddSingleton<ITransformService, XmlTransformService>();
builder.Services.AddSingleton<ITransformService, CsvTransformService>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

RecurringJob.AddOrUpdate<EmailPollingJob>(
    "email-polling",
    job => job.ExecuteAsync(CancellationToken.None),
    "*/5 * * * *");

host.Run();
