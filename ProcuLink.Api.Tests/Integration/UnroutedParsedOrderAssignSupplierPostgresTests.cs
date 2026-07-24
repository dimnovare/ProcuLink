using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Detection;
using ProcuLink.Transform.Parsing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Closes the loop on the supplier-less email-body-NLP order: it must not only EXIST, it must be
/// RESOLVABLE. An order created by <c>CreateUnroutedStubFromParsedOrderAsync</c> has NO source
/// file — it arrived as prose, not as an attachment — and <c>assign-supplier</c> resolves an
/// unrouted order by flipping it to <c>parsing</c> and re-enqueueing <c>ParseOrderJob</c>, whose
/// <c>ParseStoredFileAsync</c> starts by downloading the source file. Without a file-less branch
/// there, the operator's assignment would leave the order WEDGED in <c>parsing</c> for good: the
/// job throws, burns its three Hangfire retries and lands red, and the order can never be
/// re-assigned because assign-supplier only accepts an <c>unrouted</c> one.
///
/// <para>Real Postgres is required, not incidental: the whole chain under test is Npgsql-only bulk
/// SQL that EF InMemory cannot translate — assign-supplier's atomic status claim
/// (<c>ExecuteUpdateAsync</c>), and the re-resolve's own status claim plus its delete-then-insert
/// of the lines (<c>ExecuteUpdateAsync</c> + <c>ExecuteDeleteAsync</c>) inside one transaction.
/// Each step runs in its OWN DbContext, as a separate HTTP request and Hangfire activation really
/// do. Docker-gated; skips cleanly where Docker is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class UnroutedParsedOrderAssignSupplierPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_unrouted_body_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _pg.StartAsync();

        _connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        await using var migrateDb = new ProcuLinkDbContext(Options());
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    // ── 1. The prose-only order exists, parked and unpinned ─────────────────

    [DockerRequiredFact]
    public async Task BodyNlpOrder_withNoSupplier_parksUnrouted_withNullSupplierAndNoRevision()
    {
        var (orgId, _, _) = await SeedOrgAsync("park");

        Guid orderId;
        await using (var db = new ProcuLinkDbContext(Options()))
        {
            var result = await BuildIngestion(db).CreateStubFromParsedOrderAsync(
                orgId, supplierId: null, BodyOrder(), "email_body_nlp", CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
            orderId = result.Value!.Id;
        }

        await using var verify = new ProcuLinkDbContext(Options());
        var order = await verify.PurchaseOrders.AsNoTracking().Include(o => o.Lines)
            .SingleAsync(o => o.Id == orderId);

        Assert.Null(order.SupplierId);
        Assert.Null(order.ConnectionRevisionId);
        Assert.Equal(OrderStatusConstants.Unrouted, order.Status);
        Assert.Null(order.SourceFileKey);          // prose, not an attachment
        Assert.Equal(2, order.Lines.Count);        // the triage queue can show what arrived
        Assert.All(order.Lines, l => Assert.True(l.NeedsReview));
    }

    // ── 2. The whole point: the operator can resolve it ─────────────────────

    [DockerRequiredFact]
    public async Task AssignSupplier_thenParse_resolvesTheParkedLines_withoutDuplicatingThem()
    {
        var (orgId, supplierId, revisionId) = await SeedOrgAsync("resolve");

        // 1. The webhook parks the prose order.
        Guid orderId;
        await using (var db = new ProcuLinkDbContext(Options()))
        {
            var result = await BuildIngestion(db).CreateStubFromParsedOrderAsync(
                orgId, supplierId: null, BodyOrder(), "email_body_nlp", CancellationToken.None);
            orderId = result.Value!.Id;
        }

        // 2. The operator picks a supplier through the REAL endpoint (atomic claim + revision pin).
        await using (var db = new ProcuLinkDbContext(Options()))
        {
            var response = await BuildController(db, orgId)
                .AssignSupplier(orderId, new AssignSupplierRequest(supplierId), CancellationToken.None);
            Assert.IsType<OkObjectResult>(response);
        }

        await using (var claimed = new ProcuLinkDbContext(Options()))
        {
            var mid = await claimed.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.Equal(OrderStatusConstants.Parsing, mid.Status);
            Assert.Equal(supplierId, mid.SupplierId);
            Assert.Equal(revisionId, mid.ConnectionRevisionId);   // pinned on assignment, not at ingest
        }

        // 3. The re-enqueued ParseOrderJob runs. There is no file to parse — the persisted lines
        //    are the parsed data, and they must now resolve against the chosen supplier.
        await using (var db = new ProcuLinkDbContext(Options()))
        {
            // Must not THROW either: the persist commits before the passport emit, so an exception
            // raised after it would report a failed parse for an order that is actually resolved.
            var result = await BuildIngestion(db).ParseStoredFileAsync(orgId, orderId, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
            Assert.NotEqual(OrderStatusConstants.Failed, result.Value!.Entity.Status);
        }

        await using var verify = new ProcuLinkDbContext(Options());
        var order = await verify.PurchaseOrders.AsNoTracking().Include(o => o.Lines)
            .SingleAsync(o => o.Id == orderId);

        // Resolved against the assigned supplier's item mappings — the order left the hold.
        Assert.Equal(OrderStatusConstants.Ready, order.Status);
        Assert.Equal(supplierId, order.SupplierId);
        Assert.All(order.Lines, l => Assert.False(l.NeedsReview));
        Assert.Contains(order.Lines, l => l.BuyerItemCode == "WIDGET-A" && l.SupplierItemCode == "SUP-A");

        // Two lines in, two lines out. The re-resolve replaces the parked set, it does not append —
        // the same delete-then-insert the file-backed assign-supplier re-parse relies on.
        Assert.Equal(2, order.Lines.Count);

        // The header the extractor produced survives: there is no file to re-read it from, so a
        // re-resolve that rebuilt the header from nothing would blank the PO number and buyer.
        Assert.Equal("PO-BODY-NOSUP", order.PoNumber);
        Assert.Equal("Acme Buyer", order.BuyerName);
        Assert.Null(order.SourceFileKey);
    }

    // ── 3. The same contract for a parked ATTACHMENT (file-backed re-parse) ──

    /// <summary>
    /// Found while closing the file-less gap, and NOT specific to it. An attachment parked
    /// <c>unrouted</c> already carries the lines its first parse persisted, and those rows are
    /// loaded TRACKED by <c>ParseStoredFileAsync</c> (<c>.Include(x =&gt; x.Lines)</c>). The re-parse
    /// then clears them with <c>ExecuteDeleteAsync</c>, which does NOT tell the change tracker —
    /// leaving phantom entries that EF cascades to <c>Deleted</c> and tries to issue SQL for, so
    /// <c>SaveChanges</c> throws <c>DbUpdateConcurrencyException</c> ("expected to affect 1 row,
    /// actually affected 0"). The FIRST parse of a fresh stub has no lines to go stale, which is
    /// why every existing test passes: this only fires on the SECOND parse, i.e. exactly the
    /// operator's assign-supplier resolve.
    /// </summary>
    [DockerRequiredFact]
    public async Task ParkedAttachment_reParse_replacesItsExistingLines_withoutStaleTrackerConflict()
    {
        var (orgId, supplierId, _) = await SeedOrgAsync("attach");
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // An unrouted attachment as its first parse left it: a source file, and the lines that
        // parse persisted, none of them resolvable while there was no supplier.
        await using (var seed = new ProcuLinkDbContext(Options()))
        {
            seed.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-ATTACH-1",
                Currency = "EUR", Status = OrderStatusConstants.Parsing,
                SourceFileKey = $"{orgId}/{orderId}/order.csv",
                OrderDate = DateOnly.FromDateTime(now), CreatedAt = now, UpdatedAt = now,
                Lines =
                {
                    new PurchaseOrderLineEntity
                    {
                        Id = Guid.NewGuid(), LineNumber = 1, BuyerItemCode = "WIDGET-A",
                        Description = "Widget A", Quantity = 3m, Unit = "pcs", UnitPrice = 2.50m,
                        NeedsReview = true,
                    },
                },
            });
            await seed.SaveChangesAsync();
        }

        // Precondition, asserted rather than assumed: without a PERSISTED prior line there is no
        // stale tracker entry to conflict, and the case below would pass vacuously (the re-parse
        // inserts one line of its own, so the final count alone proves nothing).
        await using (var pre = new ProcuLinkDbContext(Options()))
            Assert.Single(await pre.PurchaseOrderLines.AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync());

        await using (var db = new ProcuLinkDbContext(Options()))
        {
            var result = await BuildIngestion(db, CsvStorage()).ParseStoredFileAsync(orgId, orderId, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
        }

        await using var verify = new ProcuLinkDbContext(Options());
        var order = await verify.PurchaseOrders.AsNoTracking().Include(o => o.Lines)
            .SingleAsync(o => o.Id == orderId);

        Assert.NotEqual(OrderStatusConstants.Parsing, order.Status);   // not wedged
        Assert.Single(order.Lines);                                    // replaced, not duplicated
        Assert.Equal("WIDGET-A", order.Lines.Single().BuyerItemCode);
    }

    // ── 4. A genuinely empty file-less order is still a failure ─────────────

    [DockerRequiredFact]
    public async Task FileLessOrder_withNoLines_stillFails_ratherThanReportingAnEmptySuccess()
    {
        var (orgId, supplierId, _) = await SeedOrgAsync("empty");
        var orderId = Guid.NewGuid();

        // No source file AND no lines: nothing to parse and nothing to re-resolve. This is a real
        // defect somewhere upstream, so it must stay loud — the file-less branch must not turn
        // every missing source file into a silent success.
        await using (var seed = new ProcuLinkDbContext(Options()))
        {
            seed.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = supplierId, PoNumber = "PO-EMPTY",
                Currency = "EUR", Status = OrderStatusConstants.Parsing, SourceFileKey = null,
                OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = new ProcuLinkDbContext(Options());
        var result = await BuildIngestion(db).ParseStoredFileAsync(orgId, orderId, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private DbContextOptions<ProcuLinkDbContext> Options() =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(_connectionString!).Options;

    private static ExtractedOrder BodyOrder() => new(
        PoNumber: "PO-BODY-NOSUP",
        OrderDate: new DateTime(2026, 7, 24),
        BuyerName: "Acme Buyer",
        Currency: "EUR",
        Lines: new[]
        {
            new ExtractedOrderLine(1, "WIDGET-A", "Widget A", 3m, "pcs", 2.50m),
            new ExtractedOrderLine(2, "WIDGET-B", "Widget B", 1m, "pcs", 9.00m),
        });

    private async Task<(Guid orgId, Guid supplierId, Guid revisionId)> SeedOrgAsync(string slug)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(Options());
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{slug}_{orgId:N}", Name = slug, Slug = $"{slug}-{orgId:N}",
            Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Chosen Co", CreatedAt = now });

        // A published connection, so the "pinned on assignment, not at ingest" half of the
        // contract is observable rather than vacuously null on both sides.
        var connectionId = Guid.NewGuid();
        var connection = new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "Chosen Co",
            ActiveRevisionId = null, CreatedAt = now, UpdatedAt = now,
        };
        db.SupplierConnections.Add(connection);
        await db.SaveChangesAsync();

        // connection.active_revision_id ⇄ revision.connection_id is a circular FK — the production
        // publish path breaks it the same way, by writing the pointer only after the revision row
        // exists. Adding both in one SaveChanges makes EF throw on the cycle.
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = revisionId, OrgId = orgId, ConnectionId = connectionId, SupplierId = supplierId,
            VersionNo = 1, Status = "published", PublishedAt = now, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        connection.ActiveRevisionId = revisionId;
        await db.SaveChangesAsync();

        return (orgId, supplierId, revisionId);
    }

    private static OrdersController BuildController(ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        return new OrdersController(
            new Mock<IOrderService>().Object,
            tenant.Object,
            new Mock<IBackgroundJobClient>().Object,
            db,
            NullLogger<OrdersController>.Instance,
            new Mock<IBillingService>().Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            new Mock<ISupplierAcceptanceService>().Object,
            new Mock<IOrderMappingOverrideService>().Object,
            new PromoteMappingService(db, new PoMappingService(db)),
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ITransformService>());
    }

    /// <summary>
    /// Real ingestion service. Its <c>CreateStubFromParsedOrderAsync(orgId, supplierId: null, …)</c>
    /// is exactly what <c>OrderService.CreateUnroutedStubFromParsedOrderAsync</c> delegates to — the
    /// public entry point itself is covered by <c>OrderIngestionUnroutedParsedOrderTests</c>.
    ///
    /// <para>The item-mapping double resolves the two buyer codes ONLY for a real supplier id — an
    /// unrouted order resolves against <c>Guid.Empty</c> and gets nothing — so "the lines resolved"
    /// is genuine evidence that the assigned supplier drove the re-resolve, not an artefact of a
    /// mock that answers the same for every caller.</para>
    /// </summary>
    /// <summary>Fresh CSV stream per download, matching the seeded attachment order's single line.</summary>
    private static IFileStorageService CsvStorage()
    {
        var storage = new Mock<IFileStorageService>();
        storage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "po_number,sku,description,qty,price\r\nPO-ATTACH-1,WIDGET-A,Widget A,3,2.50\r\n")));
        return storage.Object;
    }

    private OrderIngestionService BuildIngestion(ProcuLinkDbContext db, IFileStorageService? fileStorage = null)
    {
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.Is<Guid>(g => g != Guid.Empty),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["WIDGET-A"] = "SUP-A",
                ["WIDGET-B"] = "SUP-B",
            });
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), Guid.Empty,
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

        return new OrderIngestionService(
            db,
            fileStorage ?? new Mock<IFileStorageService>().Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            itemMappings.Object,
            poMappings.Object,
            aiMappings.Object,
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new FormatDetectorService(),
            new ProcuLink.Transform.Tokenizing.SourceTokenizer(),
            structuredExtractor: null,
            new OrderServiceShared(db, new OrderExceptionService(db), NullLogger<OrderService>.Instance),
            catalogRetrieval: null,
            effectiveConfig: null);
    }
}
