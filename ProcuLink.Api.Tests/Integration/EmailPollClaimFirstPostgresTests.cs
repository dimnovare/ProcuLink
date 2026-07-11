using System.Text;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Moq;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Worker.Jobs;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Proves the claim-first IMAP attachment ingest on REAL Postgres, where the
/// (OrgId, ImapMessageId, AttachmentHash) unique index is actually enforced (EF InMemory ignores
/// it): (a) a re-poll / retry of the same message creates NO second order; and (b) many concurrent
/// polls of the same mailbox message create EXACTLY ONE order — the losers hit the unique-index
/// claim (23505) and skip. Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class EmailPollClaimFirstPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_email_cf_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _pg.StartAsync();

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
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

    private EmailPollOrgJob NewJob(ProcuLinkDbContext db, CountingOrderService orders) =>
        new(db, Enc(), orders, new Mock<IBackgroundJobClient>().Object,
            new Mock<IBillingService>().Object, new Mock<IEmailSettingsService>().Object,
            Guard(), NullLogger<EmailPollOrgJob>.Instance);

    [DockerRequiredFact]
    public async Task RePoll_OfSameMessage_CreatesNoSecondOrder()
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nRETRY,1\r\n");
        var orders = new CountingOrderService();

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewJob(db1, orders).ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<retry@x>", "po.csv", bytes), CancellationToken.None))
                .Should().BeTrue();

        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewJob(db2, orders).ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<retry@x>", "po.csv", bytes), CancellationToken.None))
                .Should().BeTrue("a re-presented (unseen) message is still handled — but not re-imported");

        orders.TotalCreates.Should().Be(1, "the same attachment must create exactly ONE order stub across re-polls");

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
        var bytes = Encoding.UTF8.GetBytes("po,qty\r\nCONC,1\r\n");
        var orders = new CountingOrderService();   // shared across all polls

        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            await using var db = new ProcuLinkDbContext(_options!);
            // Each poll builds its own MimeMessage (same id + bytes → same attachment hash) to avoid
            // sharing MimeKit content streams across threads.
            return await NewJob(db, orders).ProcessMessageAsync(orgId, supplierId,
                MessageWithCsvAttachment("<conc@x>", "po.csv", bytes), CancellationToken.None);
        });
        await Task.WhenAll(tasks);

        orders.TotalCreates.Should().Be(1,
            "concurrent polls of the same mailbox message must create exactly ONE order stub");

        await using var verify = new ProcuLinkDbContext(_options!);
        (await verify.EmailImportRecords.CountAsync(r => r.OrgId == orgId))
            .Should().Be(1, "exactly one ledger row survives the concurrent race");
    }
}
