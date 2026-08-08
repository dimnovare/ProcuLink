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
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Worker.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Task 9 (credential-AAD-binding plan): pins that a MIS-BOUND IMAP password never escapes
/// <see cref="EmailPollOrgJob.ExecuteAsync"/> as an exception.
///
/// <para><b>Why this exists.</b> Task 6 converted the null-check on the decrypted IMAP password into
/// a <c>try</c>/<c>catch (CredentialUnbindableException)</c>, because
/// <see cref="DeliveryEncryptionService.Decrypt"/> now THROWS instead of returning null on a binding
/// mismatch. If that catch were ever dropped, an escaping exception would fail the WHOLE Hangfire
/// polling job, so one organisation with a bad IMAP password would stop every OTHER organisation from
/// being polled, instead of just skipping the one org.</para>
///
/// <para><b>Why this needs no IMAP stub.</b> The decrypt happens BEFORE the SSRF guard and before any
/// IMAP connect (verified current in <c>EmailPollOrgJob.cs</c> as of this task). A credential that
/// will not decrypt therefore short-circuits with no network access at all.</para>
///
/// <para><b>Why this lives here and not next to the SFTP/S3 versions.</b> The equivalent tests for
/// the SFTP and S3 pull-ingress pollers are in
/// <c>ProcuLink.Infrastructure.Tests/Services/FailClosedCredentialSiteTests.cs</c>. This one is not,
/// because <see cref="EmailPollOrgJob"/> is defined in <c>ProcuLink.Worker</c>, and
/// <c>ProcuLink.Infrastructure.Tests</c> is deliberately scoped to reference only
/// <c>ProcuLink.Infrastructure</c> + <c>ProcuLink.Core</c> — that narrow dependency graph is why
/// <c>dotnet test ProcuLink.Infrastructure.Tests</c> can be the fast, scoped check nearly every task
/// brief in this plan relies on. This project already carries Api + Infrastructure + Core + Worker
/// and is the designated home for cross-layer tests — see <see cref="EmailPollOrgJobSsrfTests"/>
/// immediately below, which tests the same job for the same reason.</para>
/// </summary>
public class EmailPollOrgJobFailClosedCredentialTests
{
    [Fact]
    public async Task EmailPoll_MisboundPassword_ReturnsWithoutThrowing()
    {
        await using var db = CreateDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var encryption = MakeEncryption();

        var cfg = new EmailPollingConfig(
            Enabled: true,
            Host: "imap.example.com",
            Port: 993,
            UseSsl: true,
            Username: "imapuser",
            Folder: "INBOX",
            DefaultSupplierId: null,
            // Bound to ORG B — org A's own scope can never decrypt this. Guarantees AesGcm
            // authentication failure, i.e. CredentialFailureReason.AuthenticationFailed.
            PasswordCiphertext: encryption.Encrypt(
                "hunter2", CredentialScope.ForOrg(orgB, CredentialPurpose.OrgEmailImapPassword)),
            LastPolledAt: null,
            UpdatedAt: null);

        db.Organisations.Add(new Organisation
        {
            Id = orgA,
            ClerkOrgId = "fail-closed-email-org",
            Name = "Fail Closed Email Org",
            Slug = "fail-closed-email-org",
            EmailConfigJson = cfg.ToJson(),
        });
        await db.SaveChangesAsync();

        var orders = new Mock<IStubOrderCreator>(MockBehavior.Strict);

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(orgA, BillingFeature.EmailIngestion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Strict — MarkPolledAsync must NOT be called (it runs only after a real poll completes).
        var emailSettings = new Mock<IEmailSettingsService>(MockBehavior.Strict);

        // Strict — no parse job may be enqueued when nothing was imported.
        var jobClient = new Mock<IBackgroundJobClient>(MockBehavior.Strict);

        var job = new EmailPollOrgJob(
            db,
            encryption,
            orders.Object,
            jobClient.Object,
            billing.Object,
            emailSettings.Object,
            AllowPrivateGuard(),
            NullLogger<EmailPollOrgJob>.Instance);

        // A poller must SKIP the org and keep going. An escaping exception would fail the whole
        // Hangfire job and stop every other organisation from being polled. ExecuteAsync returns
        // plain Task (no import count to assert) — the required content here is that it returns
        // cleanly instead of throwing CredentialUnbindableException.
        var act = async () => await job.ExecuteAsync(orgA, default);

        await act.Should().NotThrowAsync(
            "a credential that cannot be decrypted must be skipped, not allowed to fail the whole poll job");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    // Deliberately duplicated (not shared with FailClosedCredentialSiteTests.cs in
    // ProcuLink.Infrastructure.Tests): the two test projects do not reference each other, and these
    // are small enough that sharing them is not worth a cross-project link.

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Creates a <see cref="DeliveryEncryptionService"/> backed by a known 32-byte key.</summary>
    private static DeliveryEncryptionService MakeEncryption()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }

    /// <summary>
    /// Guard with AllowPrivateNetworkTargets=true — skips range validation (no DNS lookup needed).
    /// Never actually exercised by this test: the decrypt failure returns before the job ever
    /// reaches its SSRF guard call.
    /// </summary>
    private static OutboundRequestGuard AllowPrivateGuard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);
}
