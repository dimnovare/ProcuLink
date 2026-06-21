using System.Text;
using System.Text.Json;
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
/// T7 — end-to-end proof that a configurable cXML <c>&lt;!DOCTYPE&gt;</c> rides the SAME two identity
/// sources, with the SAME precedence, that From/To/Sender already use:
/// <list type="number">
///   <item><description><b>Round-trip</b>: a DTD saved on the supplier delivery-config cXML JSON
///     (<c>dtdSystemId</c>/<c>dtdPublicId</c>, camelCase, written by the frontend) is read back by
///     <see cref="CxmlCredentialResolver"/> and reaches the DELIVERED cXML.</description></item>
///   <item><description><b>Revision pinning</b>: a DTD on the pinned-revision
///     <see cref="CxmlEnvelope"/> survives into the delivered cXML.</description></item>
///   <item><description><b>Merge precedence</b>: when BOTH sources set a DTD the LIVE delivery-config
///     value wins (mirrors From/To/Sender); when only the envelope sets it, the envelope fills the
///     gap.</description></item>
/// </list>
/// HARD invariant: a cXML supplier with NO DTD configured delivers cXML with NO DOCTYPE.
/// </summary>
public class OrderTransformServiceCxmlDtdMergeTests
{
    private const string LiveDtd = "http://xml.cxml.org/schemas/cXML/1.2.014/cXML.dtd";
    private const string EnvDtd  = "http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd";

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

    private static (OrderService Svc, Func<byte[]?> CapturedBytes) Build(
        ProcuLinkDbContext db, DeliveryEncryptionService encryption)
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

    /// <summary>Seeds a resolved single-line order, optionally pinning an EnvelopeConfig (with a
    /// CxmlEnvelope) onto the per-order OutputTree so the live caller reads it on the revision path.</summary>
    private static async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedResolvedOrderAsync(
        ProcuLinkDbContext db, CxmlEnvelope? envelope = null)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "cXML Supplier", CreatedAt = DateTime.UtcNow });

        JsonDocument? canonical = null;
        if (envelope is not null)
        {
            var @override = new OrderMappingOverride
            {
                OutputTree = new OutputNodeTemplate
                {
                    Format   = OutputFormat.CXml,
                    Envelope = new EnvelopeConfig { Cxml = envelope },
                },
            };
            var payload = new Dictionary<string, object?>
            {
                [OrderMappingOverrideReader.CanonicalKey] = @override,
            };
            canonical = JsonSerializer.SerializeToDocument(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        }

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-DTD-WIRE", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 6, 21),
            Currency = "EUR", Status = "ready", CanonicalJson = canonical,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
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

    /// <summary>Saves a delivery-config cXML block with a DTD via the real DeliveryConfigService.</summary>
    private static Task SaveDeliveryDtdAsync(
        ProcuLinkDbContext db, DeliveryEncryptionService enc, Guid orgId, Guid supplierId,
        string? dtdSystemId, string? dtdPublicId) =>
        new DeliveryConfigService(db, enc).UpsertAsync(orgId, supplierId,
            new UpsertDeliveryConfigRequest("http", false, "{\"url\":\"https://supplier.example/cxml\"}", null, "cxml",
                new CxmlCredentialsInput(
                    FromDomain: "NetworkId", FromIdentity: "REDACTED-NETWORK-ID",
                    ToDomain: "NetworkId", ToIdentity: "REDACTED-NETWORK-ID",
                    SenderDomain: "NetworkId", SenderIdentity: "REDACTED-NETWORK-ID",
                    SenderSharedSecret: null,
                    DtdSystemId: dtdSystemId, DtdPublicId: dtdPublicId)),
            default);

    private static async Task<string> TransformAsync(OrderService svc, Func<byte[]?> captured, Guid orgId, Guid orderId)
    {
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.CXml, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error);
        return Encoding.UTF8.GetString(captured()!);
    }

    // ── Round-trip: a DTD saved on the delivery config reaches the delivered cXML ──

    [Fact]
    public async Task SavedDeliveryDtd_ReachesDeliveredCxml_SystemForm()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        await SaveDeliveryDtdAsync(db, enc, orgId, supplierId, dtdSystemId: LiveDtd, dtdPublicId: null);

        var (svc, captured) = Build(db, enc);
        var cxml = await TransformAsync(svc, captured, orgId, orderId);

        Assert.Contains($"<!DOCTYPE cXML SYSTEM \"{LiveDtd}\">", cxml);
    }

    [Fact]
    public async Task SavedDeliveryDtd_PublicForm_ReachesDeliveredCxml()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        await SaveDeliveryDtdAsync(db, enc, orgId, supplierId,
            dtdSystemId: LiveDtd, dtdPublicId: "-//cXML//DTD cXML 1.2.014//EN");

        var (svc, captured) = Build(db, enc);
        var cxml = await TransformAsync(svc, captured, orgId, orderId);

        Assert.Contains($"<!DOCTYPE cXML PUBLIC \"-//cXML//DTD cXML 1.2.014//EN\" \"{LiveDtd}\">", cxml);
    }

    [Fact]
    public async Task NoDeliveryDtd_DeliveredCxmlHasNoDoctype()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var (orgId, supplierId, orderId) = await SeedResolvedOrderAsync(db);

        // cXML credentials saved, but DTD left null → no DOCTYPE (the gate).
        await SaveDeliveryDtdAsync(db, enc, orgId, supplierId, dtdSystemId: null, dtdPublicId: null);

        var (svc, captured) = Build(db, enc);
        var cxml = await TransformAsync(svc, captured, orgId, orderId);

        Assert.DoesNotContain("<!DOCTYPE", cxml);
    }

    // ── Revision pinning: a DTD on the envelope survives into delivery ─────────

    [Fact]
    public async Task PinnedEnvelopeDtd_SurvivesIntoDeliveredCxml()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var (orgId, supplierId, orderId) =
            await SeedResolvedOrderAsync(db, new CxmlEnvelope { DtdSystemId = EnvDtd });

        // No live delivery-config DTD → the pinned envelope supplies it.
        await SaveDeliveryDtdAsync(db, enc, orgId, supplierId, dtdSystemId: null, dtdPublicId: null);

        var (svc, captured) = Build(db, enc);
        var cxml = await TransformAsync(svc, captured, orgId, orderId);

        Assert.Contains($"<!DOCTYPE cXML SYSTEM \"{EnvDtd}\">", cxml);
    }

    // ── Merge precedence: live wins over the envelope (same as From/To/Sender) ──

    [Fact]
    public async Task BothDtdSet_LiveDeliveryDtdWins_OverEnvelope()
    {
        await using var db = NewDb();
        var enc = Encryption();
        var (orgId, supplierId, orderId) =
            await SeedResolvedOrderAsync(db, new CxmlEnvelope { DtdSystemId = EnvDtd });

        // Live delivery-config DTD set as well → the live value is authoritative (mirrors From/To/Sender).
        await SaveDeliveryDtdAsync(db, enc, orgId, supplierId, dtdSystemId: LiveDtd, dtdPublicId: null);

        var (svc, captured) = Build(db, enc);
        var cxml = await TransformAsync(svc, captured, orgId, orderId);

        Assert.Contains($"<!DOCTYPE cXML SYSTEM \"{LiveDtd}\">", cxml);
        Assert.DoesNotContain(EnvDtd, cxml);
    }
}
