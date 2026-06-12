using FluentAssertions;
using Hangfire;
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
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// SEC-1 SSRF regression for the EXISTING order-ingress IMAP poller (EmailPollOrgJob).
/// The IMAP host is tenant-configured; before this fix the job connected via MailKit with
/// no SSRF guard, so an org could point it at 169.254.169.254 (cloud metadata) or an
/// RFC-1918 internal host. The shared OutboundRequestGuard now runs IMMEDIATELY before
/// ConnectAsync. This test proves a private host is refused without ever connecting —
/// no MailKit connection (which would otherwise throw/hang against an unreachable IP),
/// no order stub created, and no "polled" watermark written.
/// </summary>
public class EmailPollOrgJobSsrfTests
{
    [Fact]
    public async Task PrivateImapHost_IsBlockedBySsrfGuard_NoConnectOrImport()
    {
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

        // Private literal host (RFC-1918) — the strict guard blocks it without a DNS lookup.
        var cfg = new EmailPollingConfig(
            Enabled: true,
            Host: "10.0.0.5",
            Port: 993,
            UseSsl: true,
            Username: "imapuser",
            Folder: "INBOX",
            DefaultSupplierId: supplierId,
            PasswordCiphertext: enc.Encrypt("hunter2"),
            LastPolledAt: null,
            UpdatedAt: null);

        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = "ssrf-imap",
            Name = "SSRF IMAP Org",
            Slug = "ssrf-imap",
            EmailConfigJson = cfg.ToJson(),
        });
        await db.SaveChangesAsync();

        var orders = new Mock<IOrderService>(MockBehavior.Strict);

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), BillingFeature.EmailIngestion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Strict — MarkPolledAsync must NOT be called (it runs only after a real poll).
        var emailSettings = new Mock<IEmailSettingsService>(MockBehavior.Strict);

        var jobClient = new Mock<IBackgroundJobClient>(MockBehavior.Strict);

        var strictGuard = new OutboundRequestGuard(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:AllowPrivateNetworkTargets"] = "false" })
            .Build(), NullLogger<OutboundRequestGuard>.Instance);

        var job = new EmailPollOrgJob(
            db, enc, orders.Object, jobClient.Object, billing.Object, emailSettings.Object,
            strictGuard, NullLogger<EmailPollOrgJob>.Instance);

        // Must return cleanly (the guard short-circuits before MailKit ever connects).
        await job.ExecuteAsync(orgId, CancellationToken.None);

        orders.Verify(o => o.CreateStubAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        emailSettings.Verify(e => e.MarkPolledAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
