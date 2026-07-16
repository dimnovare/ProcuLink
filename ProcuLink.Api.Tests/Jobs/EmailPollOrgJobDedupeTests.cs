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
/// Defer-list #1 — proves the IMAP attachment-import dedupe + resume-on-conflict. The poller flags a
/// message SEEN only after the whole loop succeeds, so a crash mid-poll re-presents the same unseen
/// message on the next poll. Without a (OrgId, ImapMessageId, AttachmentHash) ledger carrying a
/// pre-generated order id + an order-existence check, the same attachment is re-imported as a
/// brand-new order. These tests drive ProcessMessageAsync directly (no live IMAP server) with a
/// hand-built MimeMessage and a persisting stub double so the existence check has real orders to see.
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

    /// <summary>
    /// Persisting stub-creator double: self-commits a minimal order under the pre-generated id (find-
    /// or-create on the PK, like the real service) so the ingress's order-existence check works, while
    /// counting calls and recording whether the ledger row was already committed at stub time.
    /// </summary>
    private sealed class PersistingStubCreator : IStubOrderCreator
    {
        private readonly ProcuLinkDbContext _db;
        public PersistingStubCreator(ProcuLinkDbContext db) => _db = db;

        public int Calls { get; private set; }
        public bool LedgerRowPresentAtStubTime { get; private set; }

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Guid orderId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => CreateAsync(organisationId, supplierId, orderId);

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Guid orderId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => CreateAsync(organisationId, null, orderId);

        private Task<Result<PurchaseOrderEntity>> CreateAsync(Guid orgId, Guid? supplierId, Guid orderId)
        {
            Calls++;
            LedgerRowPresentAtStubTime = _db.EmailImportRecords.Any(r => r.OrgId == orgId);
            if (!_db.PurchaseOrders.Any(o => o.Id == orderId))
            {
                _db.PurchaseOrders.Add(new PurchaseOrderEntity
                {
                    Id = orderId, OrgId = orgId, SupplierId = supplierId,
                    PoNumber = "PO-X", Currency = "EUR", Status = "parsing",
                    OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                _db.SaveChanges();
            }
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = supplierId,
                PoNumber = "PO-X", Currency = "EUR", Status = "parsing",
            }));
        }
    }

    private static EmailPollOrgJob BuildJob(ProcuLinkDbContext db, PersistingStubCreator orders) =>
        new(db, Enc(), orders, new Mock<IBackgroundJobClient>().Object,
            new Mock<IBillingService>().Object, new Mock<IEmailSettingsService>().Object,
            Guard(), NullLogger<EmailPollOrgJob>.Instance);

    [Fact]
    public async Task SameMessageProcessedTwice_ImportsAttachmentOnce()
    {
        await using var db = NewDb();
        var orders = new PersistingStubCreator(db);
        var job = BuildJob(db, orders);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nDEMO-1,5\r\n");
        var message = MessageWithCsvAttachment("<msg-1@buyer.example.com>", "po.csv", bytes);

        // First poll imports the attachment.
        (await job.ProcessMessageAsync(orgId, supplierId, message, CancellationToken.None)).Should().BeTrue();
        // Second poll (simulating a crash before the message was flagged SEEN) must NOT re-import.
        (await job.ProcessMessageAsync(orgId, supplierId, message, CancellationToken.None)).Should().BeTrue();

        orders.Calls.Should().Be(1, "the same attachment must create exactly one order stub across re-polls");

        var records = await db.EmailImportRecords.AsNoTracking().Where(r => r.OrgId == orgId).ToListAsync();
        records.Should().ContainSingle("one ledger row per imported attachment");
        // MimeKit normalises the Message-Id (strips the angle brackets); the dedupe uses that
        // normalised value consistently for both the lookup and the stored row.
        records[0].ImapMessageId.Should().Be("msg-1@buyer.example.com");
        (await db.PurchaseOrders.CountAsync(o => o.OrgId == orgId)).Should().Be(1, "exactly one order across re-polls");
    }

    [Fact]
    public async Task ClaimLedgerRowIsCommittedBeforeCreateStub_AndOrderIdMatchesCreatedStub()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nCLAIM,1\r\n");
        var message = MessageWithCsvAttachment("<claim-first@x>", "po.csv", bytes);

        var orders = new PersistingStubCreator(db);
        var job = BuildJob(db, orders);

        (await job.ProcessMessageAsync(orgId, supplierId, message, CancellationToken.None)).Should().BeTrue();

        orders.LedgerRowPresentAtStubTime.Should().BeTrue(
            "claim-first: the (OrgId, ImapMessageId, AttachmentHash) ledger row must be committed BEFORE " +
            "CreateStubAsync so a retry or concurrent poll cannot create a duplicate order");

        var record = await db.EmailImportRecords.AsNoTracking().SingleAsync(r => r.OrgId == orgId);
        record.OrderId.Should().NotBe(Guid.Empty, "the claim carries the pre-generated order id");
        (await db.PurchaseOrders.AnyAsync(o => o.Id == record.OrderId))
            .Should().BeTrue("the created order's primary key is the claim's pre-generated id");
    }

    [Fact]
    public async Task DifferentAttachmentContent_SameMessageId_BothImported()
    {
        await using var db = NewDb();
        var orders = new PersistingStubCreator(db);
        var job = BuildJob(db, orders);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var first = MessageWithCsvAttachment("<msg-2@x>", "a.csv", Encoding.UTF8.GetBytes("po,qty\r\nA,1\r\n"));
        var second = MessageWithCsvAttachment("<msg-2@x>", "b.csv", Encoding.UTF8.GetBytes("po,qty\r\nB,2\r\n"));

        await job.ProcessMessageAsync(orgId, supplierId, first, CancellationToken.None);
        await job.ProcessMessageAsync(orgId, supplierId, second, CancellationToken.None);

        orders.Calls.Should().Be(2, "distinct attachment content is not a duplicate even under the same Message-Id");
        (await db.EmailImportRecords.AsNoTracking().CountAsync(r => r.OrgId == orgId)).Should().Be(2);
    }

    [Fact]
    public async Task SameAttachmentBytes_DifferentOrgs_NotDeduplicatedAcrossTenants()
    {
        await using var db = NewDb();
        var orders = new PersistingStubCreator(db);
        var job = BuildJob(db, orders);
        var supplierId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nSHARED,9\r\n");

        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var msgA = MessageWithCsvAttachment("<shared@x>", "po.csv", bytes);
        var msgB = MessageWithCsvAttachment("<shared@x>", "po.csv", bytes);

        await job.ProcessMessageAsync(orgA, supplierId, msgA, CancellationToken.None);
        await job.ProcessMessageAsync(orgB, supplierId, msgB, CancellationToken.None);

        orders.Calls.Should().Be(2, "the dedupe ledger is org-scoped — two orgs may each import the same bytes");
        (await db.EmailImportRecords.AsNoTracking().CountAsync()).Should().Be(2);
    }
}
