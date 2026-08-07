using Amazon.S3;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Infrastructure.Tests.TestDoubles;
using ProcuLink.Worker.Jobs;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Task 9 (credential-AAD-binding plan): pins that a MIS-BOUND credential never escapes one of the
/// fail-closed sites as an exception.
///
/// <para><b>Why this exists.</b> Tasks 3, 4, and 6 each converted <c>if (x is null) return
/// Fail(msg)</c> into <c>try</c>/<c>catch (CredentialUnbindableException)</c>, because
/// <see cref="DeliveryEncryptionService.Decrypt"/> now THROWS instead of returning null on a
/// binding mismatch. Two things can silently regress in that conversion: the message can change, or
/// the catch can be dropped so the exception escapes. The second is the dangerous one at the three
/// pollers below — an escaping <see cref="CredentialUnbindableException"/> fails the WHOLE Hangfire
/// polling job, so one organisation with a bad credential stops every OTHER organisation from being
/// polled, instead of just skipping the one org.</para>
///
/// <para><b>Why this is cheap to test.</b> At all three pollers the decrypt happens BEFORE the SSRF
/// guard and before any network connect — <c>SftpIngressService.cs</c>, <c>S3IngressService.cs</c>,
/// <c>EmailPollOrgJob.cs</c> (verified current as of this task; see the "must never connect"
/// assertions below, which double as a guard against that ordering silently drifting). A credential
/// that will not decrypt therefore short-circuits with NO network access at all, so none of these
/// tests need an SSH or IMAP stub.</para>
///
/// <para><b>Known adjacent issue (NOT fixed here, logged for the final review):</b>
/// <see cref="CredentialScope.ToAssociatedData"/> throws a plain <see cref="ArgumentException"/>
/// (not <see cref="CredentialUnbindableException"/>) when <c>OrgId</c> is <c>Guid.Empty</c> or the
/// purpose string is malformed. None of the catches below cover that. It is unreachable at these
/// four sites because the org id always comes from a real DB row and the purpose is always a
/// compile-time <see cref="CredentialPurpose"/> constant — but it is a real gap in the fail-closed
/// contract if a future call site ever passes a caller-supplied scope. Do not widen any catch here
/// to <c>catch (Exception)</c> to "cover" it — that would defeat the point of this test file, which
/// is proving each site fails closed on the SPECIFIC exception its production code actually catches.
/// </para>
/// </summary>
public class FailClosedCredentialSiteTests
{
    // ── 1. SFTP ingress poller ────────────────────────────────────────────────

    [Fact]
    public async Task SftpIngress_MisboundPassword_ReturnsZeroWithoutThrowing()
    {
        await using var db = CreateDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var encryption = MakeEncryption();

        db.Set<SftpIngressConfig>().Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgA,
            Host = "sftp.example.com",
            Port = 22,
            Username = "testuser",
            // Bound to ORG B — org A's own scope can never decrypt this. Guarantees
            // AesGcm authentication failure, i.e. CredentialFailureReason.AuthenticationFailed.
            EncryptedPassword = encryption.Encrypt(
                "hunter2", CredentialScope.ForOrg(orgB, CredentialPurpose.OrgIngressSftpPassword)),
            RemoteDirectory = "/incoming",
            DefaultSupplierId = null,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var sftpFactory = new RecordingSftpClientFactory();
        var orders = new Mock<IStubOrderCreator>(MockBehavior.Strict);
        var enqueuer = new Mock<IParseJobEnqueuer>(MockBehavior.Strict);

        var service = new SftpIngressService(
            db,
            orders.Object,
            enqueuer.Object,
            encryption,
            sftpFactory,
            AllowPrivateGuard(),
            NullLogger<SftpIngressService>.Instance);

        // Each poller must SKIP the org and keep going. An escaping exception would fail the
        // whole Hangfire job and stop every other organisation from being polled.
        var act = async () => await service.PollAsync(orgA, default);

        await act.Should().NotThrowAsync(
            "a credential that cannot be decrypted must be skipped, not allowed to fail the whole poll job");
        (await service.PollAsync(orgA, default)).Should().Be(0);

        sftpFactory.ConnectCalls.Should().Be(0,
            "decrypt happens before the SSRF guard and before any connect — a mis-bound credential " +
            "must short-circuit with no SFTP connection ever attempted");
    }

    // ── 2. S3/R2 ingress poller ───────────────────────────────────────────────

    [Fact]
    public async Task S3Ingress_MisboundSecretKey_ReturnsZeroWithoutThrowing()
    {
        await using var db = CreateDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var encryption = MakeEncryption();

        db.Set<S3IngressConfig>().Add(new S3IngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgA,
            BucketName = "test-bucket",
            KeyPrefix = string.Empty,
            Region = "eu-west-1",
            ServiceUrl = null,
            AccessKeyId = "AKIAFAKE",
            // Bound to ORG B — org A's own scope can never decrypt this.
            EncryptedSecretKey = encryption.Encrypt(
                "fake-secret", CredentialScope.ForOrg(orgB, CredentialPurpose.OrgIngressS3SecretKey)),
            DefaultSupplierId = null,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Strict with zero setups: ANY call (list/get/etc.) fails the test — proves the poller
        // never reaches the S3 client when the secret key cannot be decrypted.
        var s3 = new Mock<IAmazonS3>(MockBehavior.Strict);
        var orders = new Mock<IStubOrderCreator>(MockBehavior.Strict);
        var enqueuer = new Mock<IParseJobEnqueuer>(MockBehavior.Strict);

        var service = new S3IngressService(
            db,
            orders.Object,
            enqueuer.Object,
            encryption,
            new FakeAmazonS3ClientFactory(s3.Object),
            AllowPrivateGuard(),
            NullLogger<S3IngressService>.Instance);

        var act = async () => await service.PollAsync(orgA, default);

        await act.Should().NotThrowAsync(
            "a credential that cannot be decrypted must be skipped, not allowed to fail the whole poll job");
        (await service.PollAsync(orgA, default)).Should().Be(0);
    }

    // ── 3. IMAP email poller ──────────────────────────────────────────────────

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
            // Bound to ORG B — org A's own scope can never decrypt this.
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

        // ExecuteAsync returns plain Task (no import count to assert) — the required content here
        // is that it returns cleanly instead of throwing CredentialUnbindableException.
        var act = async () => await job.ExecuteAsync(orgA, default);

        await act.Should().NotThrowAsync(
            "a credential that cannot be decrypted must be skipped, not allowed to fail the whole poll job");
    }

    // ── 4. Delivery test-fire (Step 4: confirm the honest message survives) ──────────────────
    // No prior test asserted "Delivery credentials could not be decrypted." verbatim (confirmed by
    // `git grep -rn "Delivery credentials could not be decrypted" -- '*Tests*'` returning no hits
    // before this test was added). TestFireAsync needs no order fixture — the fixed test document
    // has no order behind it — so this is the cheapest site to pin the exact wording at.

    [Fact]
    public async Task DeliveryTestFire_MisboundCredentials_ReturnsHonestMessage()
    {
        await using var db = CreateDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var encryption = MakeEncryption();

        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgA,
            SupplierId = supplierId,
            Protocol = DeliveryProtocolConstants.Http,
            // Bound to ORG B — org A's own scope can never decrypt this.
            EncryptedCredentials = encryption.Encrypt(
                "{\"apiKey\":\"secret\"}",
                CredentialScope.ForSupplier(orgB, CredentialPurpose.SupplierDeliveryCredentials, supplierId)),
            ConfigJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Strict — DispatchAsync must never be reached: the credential fails to decrypt first.
        // Only Protocol is set up because that is the only member DeliveryService touches before
        // the decrypt short-circuit (dispatchers.ToDictionary(x => x.Protocol, ...) at construction).
        var dispatcher = new Mock<IDeliveryDispatcher>(MockBehavior.Strict);
        dispatcher.SetupGet(d => d.Protocol).Returns(DeliveryProtocolConstants.Http);

        var service = new DeliveryService(
            db,
            new Mock<IFileStorageService>(MockBehavior.Strict).Object,
            encryption,
            new[] { dispatcher.Object },
            new Mock<IIntegrationTriggerService>(MockBehavior.Strict).Object,
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

        var act = async () => await service.TestFireAsync(orgA, supplierId, default);
        await act.Should().NotThrowAsync(
            "a credential that cannot be decrypted must produce an honest DeliveryTestResult, not an exception");

        var result = await service.TestFireAsync(orgA, supplierId, default);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(
            "Delivery credentials could not be decrypted.",
            "the message must stay byte-identical to what it was before the try/catch conversion");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
    /// Never actually exercised by these tests: the decrypt failure returns before any of the three
    /// pollers (or DeliveryService) ever reach their SSRF guard call.
    /// </summary>
    private static OutboundRequestGuard AllowPrivateGuard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    /// <summary>Counts Connect() calls; never throws itself — the assertion is ConnectCalls == 0.</summary>
    private sealed class RecordingSftpClientFactory : ISftpClientFactory
    {
        public int ConnectCalls { get; private set; }

        public ISftpSession Connect(
            string host, int port, string username, string password, SshHostKeyVerifier verifier)
        {
            ConnectCalls++;
            return new EmptySftpSession();
        }

        private sealed class EmptySftpSession : ISftpSession
        {
            public IEnumerable<string> ListFileNames(string remoteDirectory) => Enumerable.Empty<string>();
            public MemoryStream DownloadFile(string remotePath) => new();
            public Stream OpenRead(string remotePath) => new MemoryStream();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Test double for <see cref="IAmazonS3ClientFactory"/> that returns a pre-built
    /// <see cref="IAmazonS3"/> regardless of the (decrypted) credentials passed in.
    /// </summary>
    private sealed class FakeAmazonS3ClientFactory : IAmazonS3ClientFactory
    {
        private readonly IAmazonS3 _client;

        public FakeAmazonS3ClientFactory(IAmazonS3 client) => _client = client;

        public IAmazonS3 Create(string accessKeyId, string secretAccessKey, string region, string? serviceUrl)
            => _client;
    }
}
