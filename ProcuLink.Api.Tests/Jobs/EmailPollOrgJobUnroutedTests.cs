using System.Text;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Phase 1b routing for IMAP polling — mirror of the SFTP/S3 pull-ingress change (de4ea0e).
/// An org with no valid default supplier must no longer have its poll skipped (mail piling up
/// unseen forever): attachments are imported UNROUTED via
/// <see cref="IOrderService.CreateUnroutedStubAsync"/> — the parse job parks them 'unrouted'
/// and POST /api/orders/{id}/assign-supplier routes + re-parses later. A valid configured
/// default supplier keeps the routed behavior unchanged. These tests drive
/// <see cref="EmailPollOrgJob.ProcessMessageAsync"/> and
/// <see cref="EmailPollOrgJob.ResolveDefaultSupplierIdAsync"/> directly (no live IMAP server),
/// same pattern as <see cref="EmailPollOrgJobDedupeTests"/>.
/// </summary>
public class EmailPollOrgJobUnroutedTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryEncryptionService Enc()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }

    private static OutboundRequestGuard Guard() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    private static MimeMessage MessageWithCsvAttachment(string messageId, string fileName, byte[] bytes)
    {
        var msg = new MimeMessage { MessageId = messageId };
        msg.From.Add(new MailboxAddress("Sender", "sender@example.com"));
        msg.To.Add(new MailboxAddress("Receiver", "po@buyer.example.com"));
        msg.Subject = "PO attached";

        var body = new TextPart("plain") { Text = "See attached." };
        var attachment = new MimePart("text", "csv")
        {
            Content = new MimeContent(new MemoryStream(bytes)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = fileName,
        };
        msg.Body = new Multipart("mixed") { body, attachment };
        return msg;
    }

    private static (EmailPollOrgJob Job, Mock<IOrderService> Orders, Mock<IBackgroundJobClient> Jobs)
        BuildJob(ProcuLinkDbContext db)
    {
        var orders = new Mock<IOrderService>();
        orders
            .Setup(o => o.CreateStubAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(),
                OrgId = Guid.NewGuid(),
                SupplierId = Guid.NewGuid(),
                PoNumber = "PO-ROUTED",
                Currency = "EUR",
                Status = "parsing",
            }));
        orders
            .Setup(o => o.CreateUnroutedStubAsync(
                It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(),
                OrgId = Guid.NewGuid(),
                SupplierId = null,
                PoNumber = "PO-UNROUTED",
                Currency = "EUR",
                Status = "parsing",
            }));

        var jobs = new Mock<IBackgroundJobClient>();
        var job = new EmailPollOrgJob(
            db, Enc(), orders.Object, jobs.Object,
            new Mock<IBillingService>().Object, new Mock<IEmailSettingsService>().Object,
            Guard(), NullLogger<EmailPollOrgJob>.Instance);
        return (job, orders, jobs);
    }

    // ── 1. Phase 1b: no default supplier → attachment imported UNROUTED, not dropped ──

    [Fact]
    public async Task NoDefaultSupplier_Attachment_ImportedUnrouted_AndParseEnqueued()
    {
        await using var db = NewDb();
        var (job, orders, jobs) = BuildJob(db);
        var orgId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nUNROUTED-1,5\r\n");
        var message = MessageWithCsvAttachment("<unrouted-1@buyer.example.com>", "po.csv", bytes);

        var processed = await job.ProcessMessageAsync(orgId, supplierId: null, message, CancellationToken.None);

        processed.Should().BeTrue("an unrouted import fully handles the message so it can be flagged SEEN");
        orders.Verify(o => o.CreateUnroutedStubAsync(
                orgId, It.IsAny<Stream>(), "po.csv", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once, "with no default supplier the attachment must go through the unrouted hold path");
        orders.Verify(o => o.CreateStubAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "an order must never be routed to a supplier that does not exist");
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Once, "the unrouted order must still get a parse job — the parse parks it 'unrouted'");

        var records = await db.EmailImportRecords.AsNoTracking().Where(r => r.OrgId == orgId).ToListAsync();
        records.Should().ContainSingle("dedupe ledger row must be written for unrouted imports too");
    }

    // ── 2. Phase 1b: unrouted mode still respects the (OrgId, Message-Id, Hash) dedupe ──

    [Fact]
    public async Task NoDefaultSupplier_SameMessageProcessedTwice_ImportsOnce()
    {
        await using var db = NewDb();
        var (job, orders, jobs) = BuildJob(db);
        var orgId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nUNROUTED-DUP,7\r\n");
        var message = MessageWithCsvAttachment("<unrouted-dup@buyer.example.com>", "po.csv", bytes);

        (await job.ProcessMessageAsync(orgId, supplierId: null, message, CancellationToken.None)).Should().BeTrue();
        // Re-poll (crash before SEEN was flagged) must NOT duplicate the unrouted order,
        // and must still report the message as handled so it can finally be flagged SEEN.
        (await job.ProcessMessageAsync(orgId, supplierId: null, message, CancellationToken.None)).Should().BeTrue();

        orders.Verify(o => o.CreateUnroutedStubAsync(
                It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once, "re-polling the same attachment in unrouted mode must not duplicate the order");
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Once, "no second parse job for a deduped attachment");
        (await db.EmailImportRecords.AsNoTracking().CountAsync(r => r.OrgId == orgId)).Should().Be(1);
    }

    // ── 3. Supplier resolution: drives the routed-vs-unrouted decision in ExecuteAsync ──
    //    (tested directly — ExecuteAsync itself needs a live IMAP server)

    [Fact]
    public async Task ResolveDefaultSupplier_NeverConfigured_ReturnsNull()
    {
        await using var db = NewDb();
        var (job, _, _) = BuildJob(db);

        (await job.ResolveDefaultSupplierIdAsync(Guid.NewGuid(), null, CancellationToken.None))
            .Should().BeNull();
        (await job.ResolveDefaultSupplierIdAsync(Guid.NewGuid(), Guid.Empty, CancellationToken.None))
            .Should().BeNull("Guid.Empty is 'never configured', not a real supplier");
    }

    [Fact]
    public async Task ResolveDefaultSupplier_Vanished_ReturnsNull()
    {
        await using var db = NewDb();
        var (job, _, _) = BuildJob(db);

        // Configured id points at a supplier that no longer exists at all.
        (await job.ResolveDefaultSupplierIdAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))
            .Should().BeNull("a vanished supplier degrades to unrouted instead of dropping mail");
    }

    [Fact]
    public async Task ResolveDefaultSupplier_SoftDeleted_ReturnsNull()
    {
        await using var db = NewDb();
        var (job, _, _) = BuildJob(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Deleted supplier",
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            DeletedAt = DateTime.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        (await job.ResolveDefaultSupplierIdAsync(orgId, supplierId, CancellationToken.None))
            .Should().BeNull("an order must never be routed to a soft-deleted supplier");
    }

    [Fact]
    public async Task ResolveDefaultSupplier_WrongOrg_ReturnsNull()
    {
        await using var db = NewDb();
        var (job, _, _) = BuildJob(db);
        var otherOrg = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = otherOrg,
            Name = "Another tenant's supplier",
            CreatedAt = DateTime.UtcNow.AddDays(-30),
        });
        await db.SaveChangesAsync();

        (await job.ResolveDefaultSupplierIdAsync(Guid.NewGuid(), supplierId, CancellationToken.None))
            .Should().BeNull("resolution is org-scoped — a wrong-org id resolves to unrouted, never cross-tenant");
    }

    [Fact]
    public async Task ResolveDefaultSupplier_Valid_ReturnsId_RoutedBehaviorUnchanged()
    {
        await using var db = NewDb();
        var (job, _, _) = BuildJob(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Active supplier",
            CreatedAt = DateTime.UtcNow.AddDays(-30),
        });
        await db.SaveChangesAsync();

        (await job.ResolveDefaultSupplierIdAsync(orgId, supplierId, CancellationToken.None))
            .Should().Be(supplierId, "a valid configured default supplier keeps today's routed behavior");
    }
}
