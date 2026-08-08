using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Detection;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// F2 — the schema fingerprint must learn the operator's supplier on the path that actually
/// carries one: a FILE-BACKED unrouted order.
///
/// <para><see cref="SchemaFingerprintLearnsOnAssignPostgresTests"/> already pins the learn itself,
/// but it calls the recorder with a fresh <see cref="ProcuLinkDbContext"/> of its own, so it never
/// meets the change tracker <see cref="ParseOrderJob"/> hands it. Those two services share ONE
/// scoped DbContext in production, and the parse runs first: whatever that parse leaves in the
/// tracker, the fingerprint's <c>SaveChanges</c> inherits.</para>
///
/// <para>What it leaves is phantoms. The file-backed re-parse clears the prior lines with
/// <c>ExecuteDeleteAsync</c>, which removes the ROWS and tells the change tracker nothing, so the
/// lines <c>ParseStoredFileAsync</c> loaded tracked (<c>.Include(x =&gt; x.Lines)</c>) survive as
/// entries pointing at rows that are gone. Reflecting the new set onto <c>entity.Lines</c> after
/// the commit severs them from their required parent and EF cascades them to <c>Deleted</c> — so
/// the next <c>SaveChanges</c> in the scope issues DELETEs matching 0 rows and throws
/// <c>DbUpdateConcurrencyException</c>. PR #60 fixed exactly this on the FILE-LESS branch and
/// correctly refuted it for the file-backed one's own passport emit (which runs BEFORE the
/// reflection); the writers that run AFTER it — exception reconcile, then the fingerprint — were
/// never checked. Every real unrouted order arrived as a file, so this is the normal path.</para>
///
/// <para>Real Postgres is required, not incidental: the whole chain is Npgsql-only bulk SQL EF
/// InMemory cannot translate — assign-supplier's atomic claim, the parse's own claim and line
/// delete-then-insert, and the fingerprint recorder's atomic count bump. Docker-gated; skips
/// cleanly where Docker is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class FingerprintSurvivesFileBackedReParsePostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const string CsvBody =
        "po_number,sku,description,qty,price\r\nPO-ATTACH-1,WIDGET-A,Widget A,3,2.50\r\n";

    private static readonly string[] LayoutHeaders = { "po_number", "sku", "description", "qty", "price" };

    private string? _databaseConnectionString;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_fpfileback");

        _connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    // ── 1. The headline: the operator's correction reaches the layout ────────

    [DockerRequiredFact]
    public async Task AssignSupplier_thenFileBackedReParseInOneJobScope_LearnsTheOperatorsSupplier()
    {
        var (orgId, supplierId, orderId) = await SeedUnroutedAttachmentAsync("fplearn");

        // 1. First parse: the attachment arrives with no supplier and parks unrouted. The layout is
        //    recorded with nothing bound — correct, there was no supplier to bind.
        await using (var db = new ProcuLinkDbContext(Options()))
            await NewJob(db, orgId, orderId).ExecuteAsync(orderId, orgId, CancellationToken.None);

        // Preconditions, asserted rather than assumed. Without a PERSISTED prior line there is no
        // stale tracker entry for the re-parse to trip over and the case below passes vacuously;
        // without the guard hash the recorder takes the first-sighting branch, not the learn branch.
        await using (var pre = new ProcuLinkDbContext(Options()))
        {
            var parked = await pre.PurchaseOrders.AsNoTracking().Include(o => o.Lines)
                .SingleAsync(o => o.Id == orderId);
            Assert.Equal(OrderStatusConstants.Unrouted, parked.Status);
            Assert.NotEmpty(parked.Lines);
            Assert.NotNull(parked.SchemaFingerprintHash);

            var fp = await pre.SchemaFingerprints.AsNoTracking().SingleAsync(f => f.OrganisationId == orgId);
            Assert.Equal(string.Empty, fp.SupplierIdsCsv);
            Assert.Equal(1, fp.ParseSuccessCount);
        }

        // 2. The operator resolves the routing through the real endpoint.
        await using (var db = new ProcuLinkDbContext(Options()))
        {
            var response = await BuildController(db, orgId)
                .AssignSupplier(orderId, new AssignSupplierRequest(supplierId), CancellationToken.None);
            Assert.IsType<OkObjectResult>(response);
        }

        // 3. The re-enqueued ParseOrderJob runs — ONE DbContext for the parse and the fingerprint,
        //    exactly as the Hangfire scope resolves them.
        await using (var db = new ProcuLinkDbContext(Options()))
            await NewJob(db, orgId, orderId).ExecuteAsync(orderId, orgId, CancellationToken.None);

        await using var verify = new ProcuLinkDbContext(Options());
        var learned = await verify.SchemaFingerprints.AsNoTracking().SingleAsync(f => f.OrganisationId == orgId);

        // The whole point of BE #54: the human correction is recorded against the layout.
        Assert.Equal(supplierId.ToString(), learned.SupplierIdsCsv);
        Assert.Equal("Corrected Supplier", learned.SampleSupplierName);
        Assert.Equal(1, learned.ParseSuccessCount);   // the same document re-parsed is not a new sighting

        // Visible through the read API a future auto-routing suggestion would consult.
        var match = await new SchemaFingerprintService(verify, NullLogger<SchemaFingerprintService>.Instance)
            .LookupAsync(orgId, LayoutHeaders, CancellationToken.None);
        Assert.True(match!.IsBoundTo(supplierId));

        // And the order itself resolved, so this is not a fingerprint win bought with a broken parse.
        var order = await verify.PurchaseOrders.AsNoTracking().Include(o => o.Lines)
            .SingleAsync(o => o.Id == orderId);
        Assert.Equal(supplierId, order.SupplierId);
        Assert.NotEqual(OrderStatusConstants.Unrouted, order.Status);
        Assert.NotEqual(OrderStatusConstants.Parsing, order.Status);
        Assert.Single(order.Lines);                   // replaced, not duplicated
    }

    // ── 2. The mechanism, named directly ─────────────────────────────────────

    /// <summary>
    /// The same defect stated without the fingerprint: after a file-backed re-parse, the scope's
    /// DbContext must be safe to write through. The headline test above asserts the OUTCOME (the
    /// binding); this one asserts the CAUSE, so a regression reads as "the re-parse left phantoms"
    /// rather than "the fingerprint is empty" — and it also covers the parse's other same-scope
    /// victim, <c>OrderExceptionService.ReconcileAsync</c>, whose SaveChanges runs first and is
    /// swallowed just as quietly.
    /// </summary>
    [DockerRequiredFact]
    public async Task FileBackedReParse_LeavesNoPhantomLineEntries_SoLaterWritesInTheScopeSucceed()
    {
        var (orgId, supplierId, orderId) = await SeedUnroutedAttachmentAsync("fpphantom");

        await using (var db = new ProcuLinkDbContext(Options()))
            await NewJob(db, orgId, orderId).ExecuteAsync(orderId, orgId, CancellationToken.None);

        await using (var pre = new ProcuLinkDbContext(Options()))
            Assert.NotEmpty(await pre.PurchaseOrderLines.AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync());

        await using (var db = new ProcuLinkDbContext(Options()))
        {
            var response = await BuildController(db, orgId)
                .AssignSupplier(orderId, new AssignSupplierRequest(supplierId), CancellationToken.None);
            Assert.IsType<OkObjectResult>(response);
        }

        await using var scope = new ProcuLinkDbContext(Options());
        var parsed = await BuildIngestion(scope, CsvStorage()).ParseStoredFileAsync(orgId, orderId, CancellationToken.None);
        Assert.True(parsed.IsSuccess, parsed.Error);

        Assert.DoesNotContain(
            scope.ChangeTracker.Entries<PurchaseOrderLineEntity>(),
            e => e.State == EntityState.Deleted);

        // Any writer that comes after the parse in this scope — reconcile, the fingerprint, a future
        // one — must be able to save. Unrelated write, so it can only fail on inherited state.
        scope.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(orgId, orderId, "PhantomProbe", new { probe = true }));
        await scope.SaveChangesAsync();
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private DbContextOptions<ProcuLinkDbContext> Options() =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(_connectionString!).Options;

    /// <summary>
    /// The real <see cref="ParseOrderJob"/> — including the try/catch at its tail that makes a
    /// fingerprint failure non-fatal, which is precisely why this defect is silent in production.
    /// The <c>IOrderService</c> double is a pass-through to the REAL ingestion service over the
    /// SAME DbContext: the facade's <c>ParseStoredFileAsync</c> is a one-line delegation, and
    /// sharing the context is the whole point of the test.
    /// </summary>
    private ParseOrderJob NewJob(ProcuLinkDbContext db, Guid orgId, Guid orderId)
    {
        var ingestion = BuildIngestion(db, CsvStorage());
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.ParseStoredFileAsync(orgId, orderId, It.IsAny<CancellationToken>()))
              .Returns((Guid o, Guid i, CancellationToken c) => ingestion.ParseStoredFileAsync(o, i, c));

        return new ParseOrderJob(
            orders.Object,
            NullLogger<ParseOrderJob>.Instance,
            db,
            new FakeAnalyticsService(),
            new SchemaFingerprintService(db, NullLogger<SchemaFingerprintService>.Instance));
    }

    /// <summary>An unrouted ATTACHMENT as the ingest left it: a source file, no supplier, 'parsing'.</summary>
    private async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedUnroutedAttachmentAsync(string slug)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(Options());
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_{slug}_{orgId:N}", Name = slug, Slug = $"{slug}-{orgId:N}",
            Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Corrected Supplier", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = null,          // arrived on a content-routed channel
            PoNumber = "PO-ATTACH-1", Currency = "EUR", Status = OrderStatusConstants.Parsing,
            OrderDate = new DateOnly(2026, 7, 26), SourceFileKey = $"{orgId}/{orderId}/order.csv",
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId);
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

    /// <summary>Fresh CSV stream per download — the same attachment on both parses.</summary>
    private static IFileStorageService CsvStorage()
    {
        var storage = new Mock<IFileStorageService>();
        storage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(CsvBody)));
        return storage.Object;
    }

    /// <summary>
    /// Real ingestion service. The item-mapping double resolves the buyer code ONLY for a real
    /// supplier id — an unrouted order resolves against <c>Guid.Empty</c> and gets nothing — so a
    /// resolved line is genuine evidence the assigned supplier drove the re-parse.
    /// </summary>
    private static OrderIngestionService BuildIngestion(ProcuLinkDbContext db, IFileStorageService fileStorage)
    {
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.Is<Guid>(g => g != Guid.Empty),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
            {
                ["WIDGET-A"] = "SUP-A",
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
            fileStorage,
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
