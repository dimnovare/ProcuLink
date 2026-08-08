using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// End-to-end proof of the founder's ask: editing a supplier connection's cXML credentials makes
/// the GENERATED cXML carry those real network identities instead of ProcuLink's internal GUIDs.
/// Drives the real transform path — OrderService → OrderTransformService → CxmlTransformService —
/// with a real <see cref="CxmlCredentialResolver"/> reading the saved delivery-config row.
/// </summary>
public class CxmlCredentialTransformTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryEncryptionService Encryption() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build());

    /// <summary>OrderService wired with the cXML transformer + the real cXML credential resolver, and a byte-capturing storage mock.</summary>
    private static (OrderService Svc, Func<byte[]?> CapturedBytes) Build(ProcuLinkDbContext db, DeliveryEncryptionService encryption)
    {
        byte[]? captured = null;

        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, string, string, CancellationToken>((stream, _, _, _) =>
            {
                using var ms = new MemoryStream();
                stream.Position = 0;
                stream.CopyTo(ms);
                captured = ms.ToArray();
            })
            .ReturnsAsync("artifact-key");

        var svc = new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new PoMappingService(db),
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CxmlTransformService(), new XmlTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            cxmlResolver: new CxmlCredentialResolver(db, encryption));

        return (svc, () => captured);
    }

    private static async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedResolvedOrderAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "cXML Supplier", CreatedAt = DateTime.UtcNow });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-CXML-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 6, 15),
            Currency = "EUR", Status = "ready", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId);
    }

    [Fact]
    public async Task ConfiguredConnection_GeneratedCxmlCarriesRealIdentities_NotGuids()
    {
        await using var db = NewDb();
        var encryption = Encryption();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        // The operator edits the connection's cXML credentials (the real-world Coupa example).
        await new DeliveryConfigService(db, encryption).UpsertAsync(orgId, supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://supplier.example/cxml\"}", null, "cxml",
                new CxmlCredentialsInput(
                    FromDomain: "NetworkId", FromIdentity: "TESTBUYER_SE",
                    ToDomain: "NetworkId", ToIdentity: "TESTSUPPLIER_SE",
                    SenderDomain: "NetworkId", SenderIdentity: "TESTBUYER_SE",
                    SenderSharedSecret: "wire-secret")),
            default);

        var (svc, captured) = Build(db, encryption);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.CXml, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("cxml", result.Value!.Format);

        var cxml = Encoding.UTF8.GetString(captured()!);

        // From / To / Sender now carry the configured NetworkId identities…
        Assert.Contains("<Credential domain=\"NetworkId\">", cxml);
        Assert.Contains("<Identity>TESTBUYER_SE</Identity>", cxml);
        Assert.Contains("<Identity>TESTSUPPLIER_SE</Identity>", cxml);
        Assert.Contains("<SharedSecret>wire-secret</SharedSecret>", cxml);

        // …and the internal GUIDs / legacy domains the founder complained about are GONE.
        Assert.DoesNotContain(orgId.ToString(), cxml);
        Assert.DoesNotContain(supplierId.ToString(), cxml);
        Assert.DoesNotContain("domain=\"OrgId\"", cxml);
        Assert.DoesNotContain("domain=\"SupplierId\"", cxml);
    }

    [Fact]
    public async Task UnconfiguredConnection_GeneratedCxmlKeepsLegacyGuidIdentities()
    {
        await using var db = NewDb();
        var encryption = Encryption();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        // A delivery config exists but with NO cXML credentials → legacy behaviour preserved.
        await new DeliveryConfigService(db, encryption).UpsertAsync(orgId, supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://supplier.example/cxml\"}", null, "cxml"),
            default);

        var (svc, captured) = Build(db, encryption);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.CXml, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var cxml = Encoding.UTF8.GetString(captured()!);

        Assert.Contains("domain=\"OrgId\"", cxml);
        Assert.Contains($"<Identity>{orgId}</Identity>", cxml);
        Assert.Contains("domain=\"SupplierId\"", cxml);
        Assert.DoesNotContain("SharedSecret", cxml);
    }

    // ── Scope-addition: an unbindable secret must fail the transform, not strand it ────
    //
    // Approved scope addition to the credential-AAD-binding plan's Task 3. Before this, an
    // unreadable cXML secret propagated an unhandled CredentialUnbindableException out of
    // TransformAsync: the call sits before the idempotency claim and outside every other
    // try/catch in the method, so the exception unwound through TransformOrderJob (Hangfire
    // retries it identically 3x, since an AAD mismatch is not transient), and
    // StuckOrderDetectionService — which by design never fails a 'transforming' strand —
    // silently recovered the order back to 'ready' with no visible error. This proves the fix:
    // the same scenario now ends in transform_failed with a plain, visible error.

    [Fact]
    public async Task SecretBoundToADifferentSupplier_EndsInTransformFailed_NotStrandedOrSilentlyRecovered()
    {
        await using var db = NewDb();
        var encryption = Encryption();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        // Seed the shared secret encrypted for a DIFFERENT supplier than the one on this order —
        // the exact condition CredentialScope AAD binding exists to refuse. Written directly
        // (not through DeliveryConfigService, which always encrypts for the correct supplier) to
        // simulate the mismatch this task's binding is meant to catch.
        var otherSupplierId = Guid.NewGuid();
        db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = "http",
            CxmlConfigJson = """{"fromDomain":"NetworkId","fromIdentity":"TESTBUYER_SE"}""",
            EncryptedCxmlSharedSecret = encryption.Encrypt(
                "wire-secret",
                CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCxmlSecret, otherSupplierId)),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (svc, _) = Build(db, encryption);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.CXml, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "The supplier's cXML shared secret could not be decrypted, so the order was not transformed.",
            result.Error);

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatusConstants.TransformFailed, order.Status);
        Assert.NotEqual(OrderStatusConstants.Transforming, order.Status);
        Assert.NotEqual(OrderStatusConstants.Ready, order.Status); // not silently recovered either
        Assert.Equal(0, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));

        // Visible the same way every other terminal transform failure is: the audit trail
        // FailTransformAsync writes, which OrdersController reads for the order's errorMessage.
        var audit = await db.AuditEvents.SingleAsync(
            e => e.EntityId == orderId && e.OrgId == orgId && e.Action == "TransformFailed");
        Assert.True(audit.Payload!.RootElement.TryGetProperty("error", out var error));
        Assert.Equal(
            "The supplier's cXML shared secret could not be decrypted, so the order was not transformed.",
            error.GetString());
    }
}
