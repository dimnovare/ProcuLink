using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// WP-20 D4 — "Send a test now" on a file-drop connection with "replace existing files" off.
///
/// <para>
/// The test fire sends a FIXED document (<see cref="DeliveryTestArtifact"/>) with no order behind
/// it. Once the overwrite setting is enforced, the second test on such a connection fails where it
/// used to succeed — acceptable, that is the setting doing its job — but it read:
/// <i>"That remote path belongs to this order and no other … almost certainly this same purchase
/// order already delivered — check with the supplier before sending it again."</i> Every clause of
/// that is false: there is no order, nothing was delivered, and there is nothing to ask the
/// supplier about. An operator testing a connection would go and ask a customer about a purchase
/// order that does not exist.
/// </para>
/// </summary>
public class DeliveryTestFireRefusalTests
{
    private const string RemoteDir      = "/in";
    private const string TestFilePath   = RemoteDir + "/" + DeliveryTestArtifact.FileName;
    private const string OrderFilePath  = RemoteDir + "/PO-123-a1b2c3d4.xml";

    // ── The two sentences ─────────────────────────────────────────────────────

    [Fact]
    public void TheRefusalForATestFile_ClaimsNoOrder()
    {
        var message = SftpDeliveryDispatcher.RefusedBecauseFileExists(TestFilePath);

        message.Should().NotContain("this order",
            "a test fire has no order — naming one sends the operator to ask a customer about a document that does not exist");
        message.Should().NotContain("purchase order already delivered");
        message.Should().NotContain("check with the supplier",
            "there is nothing for the supplier to confirm: this is our own previous test file");

        message.Should().Contain("earlier test",
            "the operator needs to know what the file at that path actually is");
        message.Should().Contain("replace existing files",
            "and which setting produced the refusal");
        message.Should().Contain("No purchase order is involved",
            "the one reassurance that matters when a delivery screen shows a failure");
    }

    [Fact]
    public void TheRefusalForAnOrder_IsUnchanged()
    {
        // The test-fire branch must not swallow the order wording — that one is the safety-critical
        // sentence (do NOT delete or rename the file there).
        var message = SftpDeliveryDispatcher.RefusedBecauseFileExists(OrderFilePath);

        message.Should().Contain("belongs to this order");
        message.Should().Contain("check with the supplier");
        message.Should().NotContain("Delete that test file");
    }

    [Theory]
    [InlineData("/in/proculink-test.csv")]
    [InlineData("./out/proculink-test.csv")]
    [InlineData("proculink-test.csv")]
    [InlineData("/deep/nested/dir/PROCULINK-TEST.CSV")]
    public void TheTestArtifactIsRecognisedWhereverTheOperatorPutsIt(string remotePath)
    {
        DeliveryTestArtifact.IsAtPath(remotePath).Should().BeTrue();
    }

    [Fact]
    public void NoOrderFilenameCanBeMistakenForTheTestArtifact()
    {
        // A file-drop order name is {sanitised PO}-{8 hex of the order id}.{ext}, so nothing an
        // order produces can collide with the fixed test name — including an order whose PO number
        // is literally "proculink-test".
        var order = new PurchaseOrderEntity { Id = Guid.NewGuid(), PoNumber = "proculink-test" };

        foreach (var format in new[] { "csv", "cxml", "x12" })
        {
            var name = DeliveryService.BuildFileName(
                order, new OutboundArtifact { Format = format }, DeliveryProtocolConstants.Sftp);

            DeliveryTestArtifact.IsAtPath($"{RemoteDir}/{name}").Should().BeFalse(
                "'{0}' must not be read as the platform's own test file", name);
        }

        DeliveryTestArtifact.IsAtPath(null).Should().BeFalse();
        DeliveryTestArtifact.IsAtPath("   ").Should().BeFalse();
        DeliveryTestArtifact.IsAtPath("/in/not-proculink-test.csv").Should().BeFalse();
    }

    // ── End to end, through the real TestFireAsync ────────────────────────────

    [Fact]
    public async Task TestFire_WhenTheEarlierTestFileIsStillThere_ReportsTheTruth()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = DeliveryProtocolConstants.Sftp,
            AutoDeliver = true,
            ConfigJson = $"{{\"host\":\"drop.supplier.example\",\"port\":22,\"remotePath\":\"{RemoteDir}\","
                       + "\"overwriteExisting\":false}",
            EncryptedCredentials = encryption.Encrypt(
                "{\"username\":\"sftp-user\",\"password\":\"secret\"}",
                CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // The state after ONE successful "Send a test now" on this connection.
        var remote = new FakeSftpSession();
        remote.Files[TestFilePath] = "AN-EARLIER-TEST"u8.ToArray();

        var service = CreateService(db, SftpWith(remote), encryption);

        var result = await service.TestFireAsync(orgId, supplierId, default);

        result.Success.Should().BeFalse("the operator asked for no replacements and got none");
        result.ErrorMessage.Should().NotContain("this order");
        result.ErrorMessage.Should().NotContain("purchase order already delivered");
        result.ErrorMessage.Should().Contain("earlier test");

        // And the recorded attempt carries the same honest sentence — a test fire writes an
        // OrderId-less attempt row that shows up in the delivery log.
        var attempt = await db.DeliveryAttempts.SingleAsync();
        attempt.OrderId.Should().BeNull("there is no order — that is the whole point");
        attempt.ErrorMessage.Should().Be(result.ErrorMessage);
    }

    [Fact]
    public async Task TestFire_OnAFreshPath_StillSucceeds()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = DeliveryProtocolConstants.Sftp,
            AutoDeliver = true,
            ConfigJson = $"{{\"host\":\"drop.supplier.example\",\"port\":22,\"remotePath\":\"{RemoteDir}\","
                       + "\"overwriteExisting\":false}",
            EncryptedCredentials = encryption.Encrypt(
                "{\"username\":\"sftp-user\",\"password\":\"secret\"}",
                CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var remote = new FakeSftpSession();
        var service = CreateService(db, SftpWith(remote), encryption);

        var result = await service.TestFireAsync(orgId, supplierId, default);

        result.Success.Should().BeTrue();
        remote.Files.Keys.Should().Contain(TestFilePath,
            "the test artifact's name is the shared constant the dispatchers recognise, not a local literal");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static SftpDeliveryDispatcher SftpWith(FakeSftpSession session) =>
        new(NullLogger<SftpDeliveryDispatcher>.Instance, AllowAllGuard(), _ => session);

    // Fictional hostnames do not resolve; the guard's own behaviour is covered by
    // OutboundRequestGuardTests, so it is waved through here.
    private static OutboundRequestGuard AllowAllGuard()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:AllowPrivateNetworkTargets"] = "true",
            })
            .Build();
        return new OutboundRequestGuard(cfg, NullLogger<OutboundRequestGuard>.Instance);
    }

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryEncryptionService CreateEncryption()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static DeliveryService CreateService(
        ProcuLinkDbContext db, IDeliveryDispatcher dispatcher, DeliveryEncryptionService encryption) =>
        new(
            db,
            new FakeFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    private sealed class FakeSftpSession : SftpDeliveryDispatcher.ISftpUploadSession
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public bool Exists(string path) => Files.ContainsKey(path);
        public void CreateDirectory(string path) { }

        public void UploadFile(Stream input, string path, bool canOverride)
        {
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            Files[path] = ms.ToArray();
        }
    }

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("PAYLOAD"u8.ToArray()));
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }
}
