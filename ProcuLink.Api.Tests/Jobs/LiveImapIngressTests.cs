using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// LIVE real-endpoint IMAP ingress test-fire. Runs the PRODUCTION EmailPollOrgJob
/// against a REAL IMAP mailbox (env PROCULINK_LIVE_IMAP_*) — connecting via MailKit,
/// searching unseen messages, extracting a CSV attachment, and importing it.
/// Caller seeds an unseen CSV email into the mailbox first.
///
/// Gated behind PROCULINK_LIVE_ENDPOINT_TESTS=1; skipped in the normal suite.
///   dotnet test ProcuLink.Api.Tests --filter "FullyQualifiedName~Live_ImapIngress"
/// </summary>
[Trait("Category", "LiveEndpoint")]
public class LiveImapIngressTests
{
    [Fact]
    public async Task Live_ImapIngress_RealPollImportsCsvAttachment()
    {
        if (Environment.GetEnvironmentVariable("PROCULINK_LIVE_ENDPOINT_TESTS") != "1") return;
        var host = Environment.GetEnvironmentVariable("PROCULINK_LIVE_IMAP_HOST") ?? "";
        if (string.IsNullOrEmpty(host)) return;

        await using var db = new ProcuLinkDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var enc = new DeliveryEncryptionService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
            .Build());

        var cfg = new EmailPollingConfig(
            Enabled: true,
            Host: host,
            Port: int.TryParse(Environment.GetEnvironmentVariable("PROCULINK_LIVE_IMAP_PORT"), out var p) ? p : 993,
            UseSsl: true,
            Username: Environment.GetEnvironmentVariable("PROCULINK_LIVE_IMAP_USER") ?? "",
            Folder: "INBOX",
            DefaultSupplierId: supplierId,
            PasswordCiphertext: enc.Encrypt(Environment.GetEnvironmentVariable("PROCULINK_LIVE_IMAP_PASS") ?? ""),
            LastPolledAt: null,
            UpdatedAt: null);

        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = "live-imap",
            Name = "Live IMAP Org",
            Slug = "live-imap",
            EmailConfigJson = cfg.ToJson(),
        });
        // The configured default supplier must EXIST and be un-deleted, or
        // ResolveDefaultSupplierIdAsync resolves to null and the job imports the attachment
        // through the unrouted path instead — which is not what this test asserts.
        db.Set<Supplier>().Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Live IMAP supplier",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // ── Seed: send a CSV-attachment email into the mailbox via the real SMTP relay ──
        var imapUser = Environment.GetEnvironmentVariable("PROCULINK_LIVE_IMAP_USER") ?? "";
        var smtpGuard = new OutboundRequestGuard(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
            .Build(), NullLogger<OutboundRequestGuard>.Instance);
        var smtp = new SmtpDeliveryDispatcher(NullLogger<SmtpDeliveryDispatcher>.Instance, smtpGuard);
        var smtpConfig = new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "smtp",
            AutoDeliver = false,
            ConfigJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                host = Environment.GetEnvironmentVariable("PROCULINK_LIVE_SMTP_HOST") ?? "smtp.ethereal.email",
                port = 587,
                useSsl = false,
                fromAddress = imapUser,
                toAddresses = new[] { imapUser },
                subjectTemplate = "PO via email {poNumber}",
                timeoutSeconds = 30,
            }),
            EncryptedCredentials = string.Empty,
        };
        var smtpCreds = System.Text.Json.JsonSerializer.Serialize(new
        {
            username = imapUser,
            password = Environment.GetEnvironmentVariable("PROCULINK_LIVE_IMAP_PASS") ?? "",
        });
        var seed = await smtp.DispatchAsync(
            System.Text.Encoding.UTF8.GetBytes("po_number,buyer_name,line_no,item_code,quantity,unit_price\r\nPO-LIVE-IMAP-006,Email Buyer,1,SKU-IMAP-1,5,9.99\r\n"),
            "PO-LIVE-IMAP-006.csv", "text/csv", smtpConfig, smtpCreds, default);
        seed.Success.Should().BeTrue(because: $"seeding the mailbox via SMTP should succeed; error: {seed.ErrorMessage}");
        await Task.Delay(TimeSpan.FromSeconds(3)); // let Ethereal make the message visible over IMAP

        var stub = new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Status = "parsing",
            SourceFileKey = $"{orgId}/{Guid.NewGuid()}/email.csv",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var orders = new Mock<IStubOrderCreator>();
        orders.Setup(o => o.CreateStubAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(stub));
        // Taking the unrouted branch means supplier resolution failed, which this test exists to
        // rule out. Fail with that sentence rather than the NullReferenceException an unstubbed
        // Moq member produces two frames deeper inside the job.
        orders.Setup(o => o.CreateUnroutedStubAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "the poll imported the attachment UNROUTED — the configured default supplier did not resolve"));

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), BillingFeature.EmailIngestion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var emailSettings = new Mock<IEmailSettingsService>();
        emailSettings.Setup(e => e.MarkPolledAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var jobClient = new Mock<IBackgroundJobClient>();
        jobClient.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns(Guid.NewGuid().ToString());

        var job = new EmailPollOrgJob(
            db, enc, orders.Object, jobClient.Object, billing.Object, emailSettings.Object,
            // Allow-private guard so the real (public) IMAP host passes the SSRF range check.
            new OutboundRequestGuard(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
                .Build(), NullLogger<OutboundRequestGuard>.Instance),
            NullLogger<EmailPollOrgJob>.Instance);

        await job.ExecuteAsync(orgId, CancellationToken.None);

        orders.Verify(o => o.CreateStubAsync(
                orgId, supplierId, It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "the real IMAP poll should import at least one CSV/PDF/XLSX attachment from the mailbox");
    }
}
