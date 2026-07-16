using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Regression for the "invoice stuck in parsing forever" bug: ParseInvoiceJob had no
/// try-catch, so any parser exception propagated to Hangfire and the invoice was left
/// in "parsing" indefinitely (no honest terminal state). The job must now set status
/// "failed" before rethrowing, mirroring ParseOrderJob.
/// </summary>
public class ParseInvoiceJobFailureTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static InvoiceEntity SeedInvoice(ProcuLinkDbContext db, Guid orgId, Guid invoiceId, string status)
    {
        var inv = new InvoiceEntity
        {
            Id             = invoiceId,
            OrganisationId = orgId,
            InvoiceNumber  = "PENDING",
            IssueDate      = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency       = "EUR",
            Status         = status,
            SourceFileName = "bad-invoice.edi",
            SourceFileKey  = $"invoices/{orgId}/bad-invoice.edi",
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };
        db.Invoices.Add(inv);
        db.SaveChanges();
        return inv;
    }

    private static (ParseInvoiceJob job, Mock<IInvoiceParser> parser) BuildJob(ProcuLinkDbContext db)
    {
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("not a valid invoice")));

        var parser = new Mock<IInvoiceParser>();
        parser.Setup(p => p.CanParse(It.IsAny<string>(), It.IsAny<string?>())).Returns(true);
        parser.Setup(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvoiceParseException("Malformed invoice document."));

        var factory  = new InvoiceParserFactory(new[] { parser.Object });
        var invoices = new InvoiceService(db, storage.Object, Array.Empty<IInvoiceTransformService>());
        var job = new ParseInvoiceJob(
            invoices, storage.Object, factory, db, NullLogger<ParseInvoiceJob>.Instance);
        return (job, parser);
    }

    [Fact]
    public async Task ExecuteAsync_WhenParserThrows_SetsStatusFailedAndRethrows()
    {
        var orgId     = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        await using var db = NewDb();
        SeedInvoice(db, orgId, invoiceId, "parsing");
        var (job, _) = BuildJob(db);

        // The job must rethrow so Hangfire records the failed attempt …
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.ExecuteAsync(invoiceId, orgId, CancellationToken.None));

        // … but the invoice must NOT be left stranded in "parsing".
        var reloaded = await db.Invoices.AsNoTracking()
            .FirstAsync(i => i.Id == invoiceId);
        Assert.Equal("failed", reloaded.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInvoiceNotInParsing_SkipsWithoutReparsingOrClobbering()
    {
        var orgId     = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        await using var db = NewDb();
        // Simulates a Hangfire retry AFTER the first attempt already marked it "failed":
        // the status guard must short-circuit (no re-parse, no status clobber, no throw).
        SeedInvoice(db, orgId, invoiceId, "failed");
        var (job, parser) = BuildJob(db);

        await job.ExecuteAsync(invoiceId, orgId, CancellationToken.None);

        parser.Verify(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        var reloaded = await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        Assert.Equal("failed", reloaded.Status);
    }

    // ── Poisoned-context regression (finding C1) ────────────────────────────────
    // A transient failure DURING PersistParsedAsync (after AddRange has staged the invoice
    // lines) must NOT let the subsequent SetFailedAsync flush those staged lines. Before the
    // fix, SetFailedAsync reused the SAME scoped DbContext whose tracker still held the Added
    // lines, so its SaveChanges committed the parsed lines under a "failed" invoice — corrupt,
    // and the status guard then blocked any re-parse. The failed-status write must first clear
    // the tracker so only the clean status flip is persisted.

    /// <summary>Latch so "fail exactly once" survives across the two SaveChanges calls.</summary>
    private sealed class FailOnce { public bool Tripped; }

    /// <summary>
    /// Throws on the FIRST SaveChanges that carries Added invoice lines (PersistParsedAsync's
    /// save), simulating a transient DB failure after AddRange. The later SetFailedAsync save
    /// proceeds normally — reproducing the exact poisoned-tracker window.
    /// </summary>
    private sealed class FailLinePersistOnceDbContext : ProcuLinkDbContext
    {
        private readonly FailOnce _fail;
        public FailLinePersistOnceDbContext(DbContextOptions<ProcuLinkDbContext> options, FailOnce fail)
            : base(options) => _fail = fail;

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
        {
            if (!_fail.Tripped &&
                ChangeTracker.Entries<InvoiceLineEntity>().Any(e => e.State == EntityState.Added))
            {
                _fail.Tripped = true;
                throw new DbUpdateException("Simulated transient failure while persisting parsed invoice lines (finding C1).");
            }
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenPersistFailsAfterStagingLines_DoesNotCommitLinesUnderFailedInvoice()
    {
        var orgId     = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var dbName    = Guid.NewGuid().ToString();
        DbContextOptions<ProcuLinkDbContext> Opts() =>
            new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase(dbName).Options;

        using (var seed = new ProcuLinkDbContext(Opts()))
            SeedInvoice(seed, orgId, invoiceId, "parsing");

        var failOnce = new FailOnce();
        await using var db = new FailLinePersistOnceDbContext(Opts(), failOnce);

        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("valid invoice payload")));

        // A parser that SUCCEEDS with real lines, so PersistParsedAsync actually stages them
        // before the (simulated) transient save failure.
        var parser = new Mock<IInvoiceParser>();
        parser.Setup(p => p.CanParse(It.IsAny<string>(), It.IsAny<string?>())).Returns(true);
        parser.Setup(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new ParsedInvoice(
                  InvoiceNumber: "INV-1",
                  IssueDate:     DateOnly.FromDateTime(DateTime.UtcNow),
                  DueDate:       null,
                  Currency:      "EUR",
                  BuyerRef:      null,
                  SupplierRef:   null,
                  PaymentTerms:  null,
                  SubTotal:      100m,
                  TaxTotal:      0m,
                  GrandTotal:    100m,
                  Lines: new[]
                  {
                      new ParsedInvoiceLine(1, "Widget A", 2m, "PCS", 25m, 0m, 50m, null, null),
                      new ParsedInvoiceLine(2, "Widget B", 1m, "PCS", 50m, 0m, 50m, null, null),
                  }));

        var factory  = new InvoiceParserFactory(new[] { parser.Object });
        var invoices = new InvoiceService(db, storage.Object, Array.Empty<IInvoiceTransformService>());
        var job = new ParseInvoiceJob(
            invoices, storage.Object, factory, db, NullLogger<ParseInvoiceJob>.Instance);

        // The transient persist failure surfaces as the job's rethrow (Hangfire records it).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.ExecuteAsync(invoiceId, orgId, CancellationToken.None));

        // Verify against a FRESH context so we read COMMITTED state, not the poisoned tracker.
        await using var verify = new ProcuLinkDbContext(Opts());
        var reloaded = await verify.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        Assert.Equal("failed", reloaded.Status);   // ends failed cleanly (re-parseable via the guard)

        var lineCount = await verify.InvoiceLines.AsNoTracking().CountAsync(l => l.InvoiceId == invoiceId);
        Assert.Equal(0, lineCount);                  // NO parsed lines leaked under the failed invoice
    }
}
