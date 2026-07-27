using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Detection;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Composition;

/// <summary>
/// Resolves <see cref="IOrderService"/> from a real DI container wired the way both composition
/// roots wire it, and asserts that the dependencies the container can supply actually ARRIVE at
/// the ingestion sub-service.
///
/// <para><b>Why this file exists.</b> <see cref="OrderService"/> hand-constructs
/// <c>OrderIngestionService</c> with a positional argument list. Every optional parameter it
/// forgets to forward is silently defaulted to <c>null</c> — the dependency is registered, the DI
/// container hands it to <see cref="OrderService"/>, and it is then dropped on the floor. That is
/// invisible to every test that news up the sub-service directly, which is how supplier
/// auto-detect (BE #70) shipped registered-but-dead in production on 2026-07-27: zero
/// <c>order_supplier_suggestions</c> rows ever written, and no error, because the feature's own
/// tests all constructed <c>OrderIngestionService</c> by hand and passed the scorer themselves.</para>
///
/// <para>A unit test cannot catch this class of bug. Only resolution through the container can.</para>
/// </summary>
public class OrderServiceCompositionRootTests
{
    /// <summary>Records whether the ingestion path actually reached it.</summary>
    private sealed class RecordingSuggestionService : ISupplierSuggestionService
    {
        public int SuggestCalls { get; private set; }
        public SupplierSuggestionInput? LastInput { get; private set; }

        public Task<IReadOnlyList<SupplierSuggestion>> SuggestAsync(SupplierSuggestionInput input, CancellationToken ct)
        {
            SuggestCalls++;
            LastInput = input;
            return Task.FromResult<IReadOnlyList<SupplierSuggestion>>(Array.Empty<SupplierSuggestion>());
        }

        public Task RecordAsync(
            Guid organisationId, Guid orderId, IReadOnlyList<SupplierSuggestion> suggestions, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>
    /// The order-ingestion slice of <c>Program.cs</c>, registered the same way both hosts register
    /// it. Only the three genuinely external dependencies are substituted — object storage, the
    /// OpenAI-backed services, and the outbound integration trigger. Everything the wiring under
    /// test depends on is the real registration.
    /// </summary>
    internal static ServiceProvider BuildContainer(
        Action<IServiceCollection>? customise = null,
        string? postgresConnectionString = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        if (postgresConnectionString is null)
        {
            var name = Guid.NewGuid().ToString();
            services.AddDbContext<ProcuLinkDbContext>(o => o.UseInMemoryDatabase(name));
        }
        else
        {
            services.AddDbContext<ProcuLinkDbContext>(o => o.UseNpgsql(postgresConnectionString));
        }

        // ── Parsers (Program.cs registers each individually so the factory can select) ────
        services.AddSingleton<IPurchaseOrderParser, CsvOrderParser>();
        services.AddSingleton<OrderParserFactory>();

        // ── The real order graph ─────────────────────────────────────────────────────────
        services.AddScoped<IItemMappingService, ItemMappingService>();
        services.AddScoped<IPoMappingService, PoMappingService>();
        services.AddScoped<IOrderExceptionService, OrderExceptionService>();
        services.AddScoped<ICatalogRetrievalService, CatalogRetrievalService>();
        services.AddScoped<IAiSuggestionDecisionService, AiSuggestionDecisionService>();
        services.AddScoped<IEffectiveConnectionConfigResolver, EffectiveConnectionConfigResolver>();
        services.AddScoped<IAiUsageTracker, AiUsageTracker>();
        services.AddScoped<IFormatDetector, FormatDetectorService>();
        services.AddScoped<ISchemaFingerprintService, SchemaFingerprintService>();
        services.AddScoped<ISupplierSuggestionService, SupplierSuggestionService>();
        services.AddSingleton<ProcuLink.Transform.Tokenizing.ISourceTokenizer,
                              ProcuLink.Transform.Tokenizing.SourceTokenizer>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IStubOrderCreator>(sp => (OrderService)sp.GetRequiredService<IOrderService>());

        // ── Substituted: network / secrets only ──────────────────────────────────────────
        services.AddSingleton<IFileStorageService>(new Mock<IFileStorageService>().Object);
        services.AddSingleton<IIntegrationTriggerService>(new Mock<IIntegrationTriggerService>().Object);
        services.AddSingleton<IStructuredOrderExtractor>(new Mock<IStructuredOrderExtractor>().Object);
        services.AddSingleton<IProductCodeSearch>(new Mock<IProductCodeSearch>().Object);
        services.AddSingleton<ICxmlCredentialResolver>(new Mock<ICxmlCredentialResolver>().Object);
        services.AddSingleton<IAiMappingService>(NoOpAiMappings());

        customise?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private static IAiMappingService NoOpAiMappings()
    {
        var m = new Mock<IAiMappingService>();
        m.Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());
        return m.Object;
    }

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{orgId:N}", Name = "Org", Slug = $"org-{orgId:N}",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static ExtractedOrder ProseOrder() => new(
        PoNumber:  "PO-COMPOSITION-1",
        OrderDate: null,
        BuyerName: "Buyer Ltd",
        Currency:  "EUR",
        Lines: new List<ExtractedOrderLine>
        {
            new(LineNumber: 1, BuyerItemCode: "WIDGET-A", Description: "Widget A", Quantity: 2m,
                Unit: "EA", UnitPrice: 5m),
        },
        SupplierName: "Acme GmbH");

    // ── The wiring itself ─────────────────────────────────────────────────────

    [Fact]
    public async Task IOrderService_resolvedFromTheContainer_scoresASupplierLessOrder()
    {
        // The exact production shape: the scorer is registered, and the caller only ever touches
        // IOrderService. If OrderService drops it on the way to OrderIngestionService, the feature
        // is dead in prod while every unit test stays green.
        var scorer = new RecordingSuggestionService();
        await using var sp = BuildContainer(s =>
            s.AddScoped<ISupplierSuggestionService>(_ => scorer));

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
        var orgId = await SeedOrgAsync(db);

        var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var result = await orders.CreateUnroutedStubFromParsedOrderAsync(
            orgId, ProseOrder(), "email_body_nlp", CancellationToken.None,
            inboundSenderDomain: "acme.com");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(OrderStatusConstants.Unrouted, result.Value!.Status);
        Assert.Equal(1, scorer.SuggestCalls);
        Assert.Equal(orgId, scorer.LastInput!.OrganisationId);
    }

    [Fact]
    public void EveryIngestionDependencyTheContainerCanSupply_reachesTheIngestionService()
    {
        // Generalises the bug: ANY optional parameter OrderService forgets to forward is a
        // silently-dead dependency, not a compile error. This walks the ingestion constructor and
        // asserts the container's answer actually landed in the corresponding field.
        using var sp = BuildContainer();
        using var scope = sp.CreateScope();

        var orderService = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var ingestion = PrivateField(orderService, "_ingestion");
        Assert.NotNull(ingestion);

        var ctor = typeof(OrderService).Assembly
            .GetType("ProcuLink.Api.Services.OrderIngestionService", throwOnError: true)!
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single();

        var dead = new List<string>();
        var unregistered = new List<string>();

        foreach (var p in ctor.GetParameters())
        {
            var supplied = scope.ServiceProvider.GetService(p.ParameterType);

            if (supplied is null)
            {
                // An optional parameter the test container cannot answer means this file has
                // drifted from Program.cs — register it here rather than let the check go quiet.
                if (p.HasDefaultValue) unregistered.Add($"{p.ParameterType.Name} {p.Name}");
                continue;
            }

            var field = ingestion!.GetType()
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == p.ParameterType);

            if (field is null) continue;   // not stored (nothing to assert)

            if (field.GetValue(ingestion) is null)
                dead.Add($"{p.ParameterType.Name} {p.Name}");
        }

        Assert.True(unregistered.Count == 0,
            "The test container no longer mirrors Program.cs — register: " + string.Join(", ", unregistered));

        Assert.True(dead.Count == 0,
            "OrderService resolved these dependencies from DI but never forwarded them to "
            + "OrderIngestionService, so they are silently dead in production: "
            + string.Join(", ", dead));
    }

    private static object? PrivateField(object instance, string name) =>
        instance.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(instance);
}
