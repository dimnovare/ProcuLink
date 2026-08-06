using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Hardening guard for the async parse persist block (<c>OrderIngestionService.ParseStoredFileAsync</c>).
///
/// The parse persists the order across THREE auto-committing bulk statements plus one
/// <c>SaveChangesAsync</c>: the status flip (<c>ExecuteUpdateAsync</c>) commits its own SQL the
/// instant it runs, then the lines/parties/SourceCapture are written by a later
/// <c>SaveChangesAsync</c> (with two more auto-committing <c>ExecuteDeleteAsync</c> calls in
/// between). A process crash anywhere in that window used to leave a <c>ready</c>/<c>pending_review</c>
/// order with NO lines/parties/capture — and the <c>status != "parsing"</c> re-entry guard then
/// blocked any Hangfire retry from backfilling them (a permanently half-written order).
///
/// These tests prove on REAL Postgres (the bulk ops are Npgsql-only and untranslatable on EF
/// InMemory) that the whole persist block is now one atomic transaction: a failure during the
/// child-write rolls the status flip back WITH it, so the order is left exactly <c>parsing</c> and
/// a retry re-parses it cleanly to completion. Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class ParseAtomicityPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const string CsvContent =
        "po_number,sku,description,qty,price\r\n" +
        "PO-ATOM-1,BUY-1,Widget,3,4.25\r\n";

    private string? _databaseConnectionString;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_atomicity");

        _connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    // ── Atomicity: a crash during child-write leaves NO half-written order ──────

    [DockerRequiredFact]
    public async Task ParseStoredFile_failureDuringChildWrite_rollsBackStatusFlip_noHalfWrittenOrder()
    {
        var (orgId, _, orderId) = await SeedParsingOrderAsync();
        var storage = CsvStorage();

        // The throwing context aborts the child-write SaveChangesAsync — i.e. AFTER the status
        // ExecuteUpdateAsync (and the parties/capture ExecuteDeleteAsync) have run, BEFORE the
        // lines/parties/capture are committed. Exactly the crash window the fix closes.
        await using var throwingDb = new ProcuLinkDbContext(Options(new ThrowOnChildWriteInterceptor()));
        var ingestion = BuildIngestion(throwingDb, storage);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            ingestion.ParseStoredFileAsync(orgId, orderId, CancellationToken.None));

        // Fresh context — read straight from Postgres. The status flip must have rolled back
        // together with the child write: the order is untouched, fully re-parseable.
        await using var verify = new ProcuLinkDbContext(Options(interceptor: null));

        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.Parsing, order.Status); // NOT pending_review/ready

        Assert.Empty(await verify.PurchaseOrderLines.AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync());
        Assert.Empty(await verify.OrderParties.AsNoTracking().Where(p => p.OrderId == orderId).ToListAsync());
        Assert.Null(await verify.SourceCaptures.AsNoTracking().SingleOrDefaultAsync(s => s.OrderId == orderId));
    }

    // ── Retry-ability: after a mid-persist crash, a retry completes the parse ───

    [DockerRequiredFact]
    public async Task ParseStoredFile_afterFailedChildWrite_retryReparsesToCompletion()
    {
        var (orgId, _, orderId) = await SeedParsingOrderAsync();
        var storage = CsvStorage();

        // 1. First parse crashes mid-persist.
        await using (var throwingDb = new ProcuLinkDbContext(Options(new ThrowOnChildWriteInterceptor())))
        {
            var crashing = BuildIngestion(throwingDb, storage);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                crashing.ParseStoredFileAsync(orgId, orderId, CancellationToken.None));
        }

        // 2. Because the status stayed "parsing", the re-entry guard lets the retry run for real.
        await using (var cleanDb = new ProcuLinkDbContext(Options(interceptor: null)))
        {
            var retry = BuildIngestion(cleanDb, storage);
            var result = await retry.ParseStoredFileAsync(orgId, orderId, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
        }

        // 3. The order is now fully persisted — advanced past "parsing", with lines + a capture.
        await using var verify = new ProcuLinkDbContext(Options(interceptor: null));

        var order = await verify.PurchaseOrders.AsNoTracking()
            .Include(o => o.Lines)
            .SingleAsync(o => o.Id == orderId);

        Assert.NotEqual(OrderStatusConstants.Parsing, order.Status);
        Assert.NotEmpty(order.Lines);
        Assert.NotNull(await verify.SourceCaptures.AsNoTracking().SingleOrDefaultAsync(s => s.OrderId == orderId));
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private DbContextOptions<ProcuLinkDbContext> Options(IInterceptor? interceptor)
    {
        var builder = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(_connectionString!);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedParsingOrderAsync()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(Options(interceptor: null));
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_atom_{orgId:N}",
            Name = "Atomicity Org",
            Slug = $"atomicity-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Atomicity Supplier", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-ATOM-1",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = OrderStatusConstants.Parsing,
            SourceFileKey = $"{orgId}/{orderId}/order.csv",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId);
    }

    /// <summary>Returns a FRESH CSV stream per download so a parse + its retry can each read it.</summary>
    private static IFileStorageService CsvStorage()
    {
        var storage = new Mock<IFileStorageService>();
        storage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(CsvContent)));
        return storage.Object;
    }

    private static OrderIngestionService BuildIngestion(ProcuLinkDbContext db, IFileStorageService fileStorage)
    {
        var itemMappings = new Mock<IItemMappingService>();
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
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

    /// <summary>
    /// Aborts the SaveChangesAsync that writes the parse child rows (the one carrying added
    /// <see cref="PurchaseOrderLineEntity"/> rows) — a deterministic stand-in for a process crash
    /// AFTER the status <c>ExecuteUpdateAsync</c> but BEFORE the child write commits. Fires only on
    /// SaveChanges (never on the bulk ExecuteUpdate/ExecuteDelete), so the status flip and the
    /// child write are split exactly across the failure boundary the fix must make atomic.
    /// </summary>
    private sealed class ThrowOnChildWriteInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } ctx &&
                ctx.ChangeTracker.Entries<PurchaseOrderLineEntity>().Any(e => e.State == EntityState.Added))
            {
                throw new InvalidOperationException("Simulated crash during parse child-write.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
