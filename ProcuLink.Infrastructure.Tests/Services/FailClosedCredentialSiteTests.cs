using Amazon.S3;
using FluentAssertions;
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

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Task 9 (credential-AAD-binding plan): pins that a MIS-BOUND credential never escapes one of the
/// fail-closed sites as an exception.
///
/// <para><b>Why this exists.</b> Tasks 3, 4, and 6 each converted <c>if (x is null) return
/// Fail(msg)</c> into <c>try</c>/<c>catch (CredentialUnbindableException)</c>, because
/// <see cref="DeliveryEncryptionService.Decrypt"/> now THROWS instead of returning null on a
/// binding mismatch. Two things can silently regress in that conversion: the message can change, or
/// the catch can be dropped so the exception escapes. The second is the dangerous one for the two
/// pollers below — an escaping <see cref="CredentialUnbindableException"/> fails the WHOLE Hangfire
/// polling job, so one organisation with a bad credential stops every OTHER organisation from being
/// polled, instead of just skipping the one org.</para>
///
/// <para><b>Why this is cheap to test.</b> At both pollers here the decrypt happens BEFORE the SSRF
/// guard and before any network connect — <c>SftpIngressService.cs</c>, <c>S3IngressService.cs</c>
/// (verified current as of this task; see the "must never connect" assertions below, which double as
/// a guard against that ordering silently drifting). A credential that will not decrypt therefore
/// short-circuits with NO network access at all, so neither test here needs an SSH stub.</para>
///
/// <para><b>Where the third poller's test lives.</b> The same contract holds for the IMAP email
/// poller, but that test is NOT in this file: <c>EmailPollOrgJob</c> is defined in
/// <c>ProcuLink.Worker</c>, and this project (<c>ProcuLink.Infrastructure.Tests</c>) is deliberately
/// scoped to reference only <c>ProcuLink.Infrastructure</c> + <c>ProcuLink.Core</c> — that narrow
/// dependency graph is WHY <c>dotnet test ProcuLink.Infrastructure.Tests</c> can be the fast, scoped
/// check nearly every task brief in this plan relies on. Pulling in <c>ProcuLink.Worker</c> would
/// transitively pull in <c>ProcuLink.Api</c> and the Worker's own package set, compiling the entire
/// application graph just to run this one project's tests. <c>ProcuLink.Api.Tests</c> already carries
/// Api + Infrastructure + Core + Worker and is the designated home for cross-layer tests — see
/// <c>ProcuLink.Api.Tests/Jobs/EmailPollOrgJobFailClosedCredentialTests.cs</c>, right next to
/// <c>EmailPollOrgJobSsrfTests.cs</c>, which tests the same job for the same reason.</para>
///
/// <para><b>Known adjacent issue (NOT fixed here, logged for the final review):</b>
/// <see cref="CredentialScope.ToAssociatedData"/> throws a plain <see cref="ArgumentException"/>
/// (not <see cref="CredentialUnbindableException"/>) when <c>OrgId</c> is <c>Guid.Empty</c> or the
/// purpose string is malformed. None of the catches this task covers (here or in the companion
/// Api.Tests file) handle that. It is unreachable at any of these sites because the org id always
/// comes from a real DB row and the purpose is always a compile-time
/// <see cref="CredentialPurpose"/> constant — but it is a real gap in the fail-closed contract if a
/// future call site ever passes a caller-supplied scope. Do not widen any catch here to
/// <c>catch (Exception)</c> to "cover" it — that would defeat the point of this test file, which is
/// proving each site fails closed on the SPECIFIC exception its production code actually catches.
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

    // ── 3. Delivery test-fire (Step 4: confirm the honest message survives) ──────────────────
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
    /// Never actually exercised by these tests: the decrypt failure returns before either poller
    /// (or DeliveryService) ever reaches its SSRF guard call.
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
