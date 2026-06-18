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
}
