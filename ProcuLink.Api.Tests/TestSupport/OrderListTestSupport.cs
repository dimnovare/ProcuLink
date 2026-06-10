using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Tests.TestSupport;

/// <summary>
/// Shared helpers for the order-list / pagination tests. Centralises the two pieces that
/// were copy-pasted across <c>OrderServiceListPagedTests</c>,
/// <c>OrdersControllerListPagingTests</c>, and <c>OrdersListPagingPostgresTests</c>:
/// constructing a real <see cref="OrderService"/> with no-op collaborators, and seeding
/// purchase-order rows. Each test still owns its own DbContext (InMemory vs Postgres) and
/// org/supplier setup — only the duplicated mechanics live here.
/// </summary>
internal static class OrderListTestSupport
{
    /// <summary>
    /// A real <see cref="OrderService"/> over <paramref name="db"/> whose collaborators are
    /// no-ops (empty item/PO/AI mappings, file storage, integration trigger). The list/window
    /// query path does not invoke any of them, so this is safe for every paging test.
    /// </summary>
    public static OrderService BuildOrderService(ProcuLinkDbContext db)
    {
        var fileStorage = new Mock<IFileStorageService>();

        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>());

        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        var integrationTrigger = new Mock<IIntegrationTriggerService>();
        integrationTrigger
            .Setup(s => s.EnqueueAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[]
            {
                new CsvOrderParser(), new XlsxOrderParser(), new PdfOrderParser()
            }),
            itemMappings.Object,
            new ProcuLink.Infrastructure.Services.OrderExceptionService(db),
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            integrationTrigger.Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    /// <summary>
    /// Adds <paramref name="count"/> purchase orders for (<paramref name="orgId"/>,
    /// <paramref name="supplierId"/>) to <paramref name="db"/> WITHOUT saving — the caller saves
    /// (so it can add the parent org/supplier in the same unit of work). Returns the inserted ids
    /// in insertion order (index 0 = oldest).
    /// </summary>
    /// <param name="sharedCreatedAt">
    /// When null (default), each order is staggered one minute apart so <c>CreatedAt</c> is unique
    /// and newest-first ordering is unambiguous. When supplied, EVERY order gets that exact
    /// timestamp — the bulk-ingest tie that exercises the <c>Id</c>-DESC pagination tiebreaker.
    /// </param>
    public static List<Guid> AddOrders(
        ProcuLinkDbContext db,
        Guid orgId,
        Guid supplierId,
        int count,
        string status = "ready",
        DateTime? sharedCreatedAt = null)
    {
        var baseTime = sharedCreatedAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            var createdAt = sharedCreatedAt ?? baseTime.AddMinutes(i);
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id            = id,
                OrgId         = orgId,
                SupplierId    = supplierId,
                PoNumber      = $"PO-{i + 1:D4}",
                OrderDate     = DateOnly.FromDateTime(createdAt),
                Currency      = "EUR",
                Status        = status,
                SourceFileKey = $"{orgId}/{id}/order.csv",
                CreatedAt     = createdAt,
                UpdatedAt     = createdAt,
            });
        }
        return ids;
    }
}
