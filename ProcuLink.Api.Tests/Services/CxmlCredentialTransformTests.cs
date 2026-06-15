using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
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
                    FromDomain: "NetworkId", FromIdentity: "REDACTED-NETWORK-ID",
                    ToDomain: "NetworkId", ToIdentity: "REDACTED-NETWORK-ID",
                    SenderDomain: "NetworkId", SenderIdentity: "REDACTED-NETWORK-ID",
                    SenderSharedSecret: "wire-secret")),
            default);

        var (svc, captured) = Build(db, encryption);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.CXml, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("cxml", result.Value!.Format);

        var cxml = Encoding.UTF8.GetString(captured()!);

        // From / To / Sender now carry the configured NetworkId identities…
        Assert.Contains("<Credential domain=\"NetworkId\">", cxml);
        Assert.Contains("<Identity>REDACTED-NETWORK-ID</Identity>", cxml);
        Assert.Contains("<Identity>REDACTED-NETWORK-ID</Identity>", cxml);
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
}
