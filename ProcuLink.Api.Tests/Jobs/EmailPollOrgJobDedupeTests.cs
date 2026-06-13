using System.Text;
using FluentAssertions;
using Hangfire;
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
/// Defer-list #1 — proves the IMAP attachment-import dedupe. The poller flags a message SEEN only
/// after the whole loop succeeds, so a crash mid-poll re-presents the same unseen message on the
/// next poll. Without a (OrgId, ImapMessageId, AttachmentHash) ledger + pre-insert check, the same
/// attachment is re-imported as a brand-new order. These tests drive ProcessMessageAsync directly
/// (no live IMAP server) with a hand-built MimeMessage.
/// </summary>
public class EmailPollOrgJobDedupeTests
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
                PoNumber = "PO-X",
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

    [Fact]
    public async Task SameMessageProcessedTwice_ImportsAttachmentOnce()
    {
        await using var db = NewDb();
        var (job, orders, _) = BuildJob(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nDEMO-1,5\r\n");
        var message = MessageWithCsvAttachment("<msg-1@buyer.example.com>", "po.csv", bytes);

        // First poll imports the attachment.
        (await job.ProcessMessageAsync(orgId, supplierId, message, CancellationToken.None)).Should().BeTrue();
        // Second poll (simulating a crash before the message was flagged SEEN) must NOT re-import.
        (await job.ProcessMessageAsync(orgId, supplierId, message, CancellationToken.None)).Should().BeTrue();

        orders.Verify(o => o.CreateStubAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once, "the same attachment must create exactly one order stub across re-polls");

        var records = await db.EmailImportRecords.AsNoTracking().Where(r => r.OrgId == orgId).ToListAsync();
        records.Should().ContainSingle("one ledger row per imported attachment");
        // MimeKit normalises the Message-Id (strips the angle brackets); the dedupe uses that
        // normalised value consistently for both the lookup and the stored row.
        records[0].ImapMessageId.Should().Be("msg-1@buyer.example.com");
    }

    [Fact]
    public async Task DifferentAttachmentContent_SameMessageId_BothImported()
    {
        await using var db = NewDb();
        var (job, orders, _) = BuildJob(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var first = MessageWithCsvAttachment("<msg-2@x>", "a.csv", Encoding.UTF8.GetBytes("po,qty\r\nA,1\r\n"));
        var second = MessageWithCsvAttachment("<msg-2@x>", "b.csv", Encoding.UTF8.GetBytes("po,qty\r\nB,2\r\n"));

        await job.ProcessMessageAsync(orgId, supplierId, first, CancellationToken.None);
        await job.ProcessMessageAsync(orgId, supplierId, second, CancellationToken.None);

        orders.Verify(o => o.CreateStubAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "distinct attachment content is not a duplicate even under the same Message-Id");

        (await db.EmailImportRecords.AsNoTracking().CountAsync(r => r.OrgId == orgId)).Should().Be(2);
    }

    [Fact]
    public async Task SameAttachmentBytes_DifferentOrgs_NotDeduplicatedAcrossTenants()
    {
        await using var db = NewDb();
        var (job, orders, _) = BuildJob(db);
        var supplierId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nSHARED,9\r\n");

        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var msgA = MessageWithCsvAttachment("<shared@x>", "po.csv", bytes);
        var msgB = MessageWithCsvAttachment("<shared@x>", "po.csv", bytes);

        await job.ProcessMessageAsync(orgA, supplierId, msgA, CancellationToken.None);
        await job.ProcessMessageAsync(orgB, supplierId, msgB, CancellationToken.None);

        orders.Verify(o => o.CreateStubAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "the dedupe ledger is org-scoped — two orgs may each import the same bytes");

        (await db.EmailImportRecords.AsNoTracking().CountAsync()).Should().Be(2);
    }
}
