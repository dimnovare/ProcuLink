using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Detection;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using ProcuLink.Transform.Tokenizing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Workshop P0 (Task 2) — proves on REAL Postgres that the SYNC file-ingest path
/// (<c>OrderIngestionService.CreateFromFileAsync</c>) now persists a LOSSLESS
/// <c>SourceCapture</c> (one token per source cell, addressed by the tokenizer's stable id) so the
/// Order Workshop's "received fields" pane can show every field — not just the ones the canonical
/// model promoted. Before this work the sync path wrote NO capture, so an order created through it
/// had zero draggable source fields.
///
/// Postgres-backed (not EF InMemory): InMemory masks FK ordering and the jsonb round-trip the
/// real <c>source_captures.tokens_json</c> column relies on.
/// Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class SourceCaptureLosslessTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_capture_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;

        await using var migrate = new ProcuLinkDbContext(_options);
        await migrate.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    // A 3-column header + 2 data rows = 9 source cells.
    private const string Csv =
        "po_number,buyer_code,qty\r\n" +
        "PO-CAP-1,ACM-BOLT-001,3\r\n" +
        "PO-CAP-1,ACM-NUT-002,5\r\n";

    [DockerRequiredFact]
    public async Task SyncFileIngest_persists_a_lossless_token_set()
    {
        var (orgId, supplierId) = await SeedSupplierAsync();

        await using var db = new ProcuLinkDbContext(_options!);
        var ingestion = BuildIngestion(db);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Csv));
        var result = await ingestion.CreateFromFileAsync(
            orgId, supplierId, stream, "order.csv", "text/csv", CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var orderId = result.Value!.Id;

        // Reload through a fresh context — the capture must come back from Postgres itself.
        await using var verify = new ProcuLinkDbContext(_options!);
        var capture = await verify.SourceCaptures
            .AsNoTracking()
            .SingleAsync(c => c.OrderId == orderId && c.OrgId == orgId);

        var tokens = SourceTokenSerialization.FromTokensJson(capture.TokensJson);

        // Every source cell present: 3 columns × 3 rows (header + 2 data) = 9 tokens.
        Assert.Equal(9, tokens.Count);
        Assert.Contains(tokens, t => t.Value == "ACM-BOLT-001");
        Assert.Contains(tokens, t => t.Value == "ACM-NUT-002");
        // The stable cell id survives the persist round-trip (id-first serialization).
        Assert.Contains(tokens, t => t.Id == "cell:r2c2" && t.Value == "ACM-BOLT-001");
    }

    // ── Task 3: pushed (API ingress) payloads get a lossless capture too ─────────

    [DockerRequiredFact]
    public async Task PushedIngressOrder_persists_a_lossless_token_set_withNoFile()
    {
        var (orgId, supplierId) = await SeedSupplierAsync();

        await using var db = new ProcuLinkDbContext(_options!);
        var orders = BuildOrderService(db);
        var controller = BuildIngressController(db, orgId, "capture-" + orgId.ToString("N"));

        var req = new ProcuLink.Api.Controllers.IngressOrderRequest(
            OrderNumber: "PO-PUSH-1",
            OrderDate:   new DateOnly(2026, 6, 16),
            Currency:    "EUR",
            Notes:       null,
            SupplierId:  supplierId.ToString(),
            Lines: new List<ProcuLink.Api.Controllers.IngressOrderLine>
            {
                new(BuyerItemCode: "BUY-1", Description: "Widget", Quantity: 4m, Unit: "EA", UnitPrice: 5.50m),
                new(BuyerItemCode: "BUY-2", Description: "Gadget", Quantity: 1m, Unit: "EA", UnitPrice: 99m),
            });

        var result = await controller.ReceiveOrder(
            "capture-" + orgId.ToString("N"), req, orders, CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var orderId = (Guid)ok.Value!.GetType().GetProperty("Id")!.GetValue(ok.Value)!;

        // Reload through a fresh context — the capture must come back from Postgres itself.
        await using var verify = new ProcuLinkDbContext(_options!);
        var order = await verify.PurchaseOrders
            .AsNoTracking()
            .SingleAsync(o => o.Id == orderId && o.OrgId == orgId);

        // A pushed order has NO stored source file, yet must still carry draggable fields.
        Assert.True(string.IsNullOrEmpty(order.SourceFileKey));

        var capture = await verify.SourceCaptures
            .AsNoTracking()
            .SingleAsync(c => c.OrderId == orderId && c.OrgId == orgId);

        var tokens = SourceTokenSerialization.FromTokensJson(capture.TokensJson);
        Assert.NotEmpty(tokens);
        // Every pushed line field is present and addressable.
        Assert.Contains(tokens, t => t.Value == "BUY-1");
        Assert.Contains(tokens, t => t.Value == "Widget");
        Assert.Contains(tokens, t => t.Value == "BUY-2");
        Assert.Contains(tokens, t => t.Value == "PO-PUSH-1");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static ProcuLink.Api.Controllers.IngressController BuildIngressController(
        ProcuLinkDbContext db, Guid orgId, string slug)
    {
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Items[CurrentTenantService.Items.OrganisationId] = orgId;
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = httpContext };
        var tenant = new CurrentTenantService(accessor);

        var controller = new ProcuLink.Api.Controllers.IngressController(
            db,
            new ProcuLink.Infrastructure.Services.IdempotencyService(db),
            tenant,
            NullLogger<ProcuLink.Api.Controllers.IngressController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = httpContext },
        };
        return controller;
    }

    private static IOrderService BuildOrderService(ProcuLinkDbContext db)
    {
        var fileStorage = new Mock<IFileStorageService>();

        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>());

        var poMappings = new Mock<IPoMappingService>();
        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        return new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            itemMappings.Object,
            new OrderExceptionService(db),
            poMappings.Object,
            aiMappings.Object,
            Array.Empty<ITransformService>(),
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new FormatDetectorService());
    }

    private async Task<(Guid orgId, Guid supplierId)> SeedSupplierAsync()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_cap_{orgId:N}",
            Name = "Capture Org",
            Slug = $"capture-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Capture Supplier", CreatedAt = now });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    private static OrderIngestionService BuildIngestion(ProcuLinkDbContext db)
    {
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("stored-key");

        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>());

        var poMappings = new Mock<IPoMappingService>();
        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        return new OrderIngestionService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            itemMappings.Object,
            poMappings.Object,
            aiMappings.Object,
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new FormatDetectorService(),
            new SourceTokenizer(),
            structuredExtractor: null,
            new OrderServiceShared(db, new OrderExceptionService(db), NullLogger<OrderService>.Instance),
            catalogRetrieval: null,
            effectiveConfig: null);
    }
}
