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
/// Pins that a MIS-BOUND credential never escapes the delivery test-fire as an exception:
/// <see cref="DeliveryEncryptionService.Decrypt"/> THROWS on a binding mismatch, and the test-fire
/// must turn that into its honest message instead.
/// </summary>
public class FailClosedCredentialSiteTests
{
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

}
