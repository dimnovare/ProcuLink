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

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Proves the claim-first + resume-on-conflict IMAP attachment ingest on REAL Postgres, where the
/// (OrgId, ImapMessageId, AttachmentHash) unique index is actually enforced (EF InMemory ignores
/// it): re-poll creates no second order; concurrent polls create exactly one; a transient stub
/// failure is RESUMED (not lost); an order whose parse-enqueue crashed is not re-created; and a
/// permanently-bad attachment is bounded. Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class EmailPollClaimFirstPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_email_cf");

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

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

    /// <summary>Seeds an organisation so the persisting stub double's purchase-order insert satisfies its FK.</summary>
    private async Task SeedOrgAsync(Guid orgId)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_email_{orgId:N}", Name = "Email CF Org",
            Slug = $"email-cf-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private EmailPollOrgJob NewJob(ProcuLinkDbContext db, CountingOrderService orders, IBackgroundJobClient? jobs = null) =>
        new(db, Enc(), orders, jobs ?? new Mock<IBackgroundJobClient>().Object,
            new Mock<IBillingService>().Object, new Mock<IEmailSettingsService>().Object,
            Guard(), NullLogger<EmailPollOrgJob>.Instance);

    private async Task<int> OrderCountAsync(Guid orgId)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        return await db.PurchaseOrders.CountAsync(o => o.OrgId == orgId);
    }

    [DockerRequiredFact]
    public async Task RePoll_OfSameMessage_CreatesNoSecondOrder()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedOrgAsync(orgId);
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nRETRY,1\r\n");
        var orders = new CountingOrderService(_options!);

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewJob(db1, orders).ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<retry@x>", "po.csv", bytes), CancellationToken.None))
                .Should().BeTrue();

        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewJob(db2, orders).ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<retry@x>", "po.csv", bytes), CancellationToken.None))
                .Should().BeTrue("a re-presented (unseen) message is still handled — but not re-imported");

        orders.TotalCreates.Should().Be(1, "the same attachment must create exactly ONE order stub across re-polls");
        (await OrderCountAsync(orgId)).Should().Be(1, "exactly ONE purchase order exists across re-polls");

        await using var verify = new ProcuLinkDbContext(_options!);
        (await verify.EmailImportRecords.CountAsync(r => r.OrgId == orgId))
            .Should().Be(1, "exactly one ledger row per imported attachment");
    }

    [DockerRequiredFact]
    public async Task ConcurrentPolls_OfSameMessage_CreateExactlyOneOrder()
    {
        const int workers = 8;
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedOrgAsync(orgId);
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nCONC,1\r\n");
        var orders = new CountingOrderService(_options!);   // shared across all polls

        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            await using var db = new ProcuLinkDbContext(_options!);
            // Each poll builds its own MimeMessage (same id + bytes → same attachment hash) to avoid
            // sharing MimeKit content streams across threads.
            return await NewJob(db, orders).ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<conc@x>", "po.csv", bytes), CancellationToken.None);
        });
        await Task.WhenAll(tasks);

        (await OrderCountAsync(orgId)).Should().Be(1,
            "concurrent polls of the same mailbox message must create EXACTLY ONE purchase order");

        await using var verify = new ProcuLinkDbContext(_options!);
        (await verify.EmailImportRecords.CountAsync(r => r.OrgId == orgId))
            .Should().Be(1, "exactly one ledger row survives the concurrent race");
    }

    [DockerRequiredFact]
    public async Task TransientStubFailure_ThenRetry_ImportsOrder_NotLost()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedOrgAsync(orgId);
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nTRANS,1\r\n");

        var attempt = 0;
        var orders = new CountingOrderService(_options!,
            () => Interlocked.Increment(ref attempt) == 1 ? StubBehavior.ThrowTransient : StubBehavior.Succeed);

        await using (var db1 = new ProcuLinkDbContext(_options!))
        {
            var job = NewJob(db1, orders);
            await Assert.ThrowsAsync<InvalidOperationException>(() => job.ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<trans@x>", "po.csv", bytes), CancellationToken.None));
        }

        await using (var afterFail = new ProcuLinkDbContext(_options!))
        {
            var claim = await afterFail.EmailImportRecords.SingleAsync(r => r.OrgId == orgId);
            claim.OrderId.Should().NotBe(Guid.Empty, "the claim carries a real pre-generated order id to resume under");
            (await afterFail.PurchaseOrders.CountAsync(o => o.OrgId == orgId))
                .Should().Be(0, "the transient failure means the order was never created");
        }

        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewJob(db2, orders).ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<trans@x>", "po.csv", bytes), CancellationToken.None))
                .Should().BeTrue("the retry RESUMES the abandoned claim and imports the order (never lost)");

        (await OrderCountAsync(orgId)).Should().Be(1, "the order is eventually created exactly once — not lost");
    }

    [DockerRequiredFact]
    public async Task OrderCommittedButEnqueueCrashed_Retry_CreatesNoSecondOrder()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedOrgAsync(orgId);
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nPOST,1\r\n");
        var orders = new CountingOrderService(_options!);   // always commits the order

        // A background-job client whose enqueue throws — models a crash AFTER the order committed
        // but before the attachment was known complete.
        var throwingJobs = new Mock<IBackgroundJobClient>();
        throwingJobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
                    .Throws(new InvalidOperationException("parse-enqueue failure after order commit (test double)"));

        await using (var db1 = new ProcuLinkDbContext(_options!))
        {
            var job = NewJob(db1, orders, throwingJobs.Object);
            await Assert.ThrowsAsync<InvalidOperationException>(() => job.ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<post@x>", "po.csv", bytes), CancellationToken.None));
        }
        (await OrderCountAsync(orgId)).Should().Be(1, "the order WAS committed before the enqueue crashed");

        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewJob(db2, orders).ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<post@x>", "po.csv", bytes), CancellationToken.None))
                .Should().BeTrue("the retry handles the message but the existing order (by id) makes it a no-op");

        (await OrderCountAsync(orgId)).Should().Be(1, "still exactly ONE order — the retry created no duplicate");
        orders.TotalCreates.Should().Be(1, "the retry must not call stub creation again for the completed order");
    }

    [DockerRequiredFact]
    public async Task PermanentlyBadAttachment_IsBounded_NotRetriedForever()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedOrgAsync(orgId);
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nBAD,1\r\n");
        var orders = new CountingOrderService(_options!, () => StubBehavior.FailPermanent);

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewJob(db1, orders).ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<bad@x>", "po.csv", bytes), CancellationToken.None))
                .Should().BeTrue("a permanently-bad attachment is handled (flagged SEEN) but imports nothing");

        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewJob(db2, orders).ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<bad@x>", "po.csv", bytes), CancellationToken.None))
                .Should().BeTrue();

        orders.TotalCreates.Should().Be(1, "a genuinely-bad attachment is attempted ONCE, then bounded — never retried forever");
        (await OrderCountAsync(orgId)).Should().Be(0, "no order is ever created for a permanently-bad attachment");

        await using var verify = new ProcuLinkDbContext(_options!);
        var claim = await verify.EmailImportRecords.SingleAsync(r => r.OrgId == orgId);
        claim.OrderId.Should().Be(Guid.Empty, "the claim is marked terminal (Guid.Empty) after the permanent failure");
    }
}
