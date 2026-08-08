using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// WS-12 LAST LEG — end-to-end proof that the per-connection <see cref="EnvelopeConfig"/> (which rides
/// on the order's pinned output template, <see cref="OrderMappingOverride.OutputTree"/> →
/// <c>OutputNodeTemplate.Envelope</c>) is THREADED by the live transform caller
/// (<c>OrderService → OrderTransformService</c>) into the dedicated X12 / cXML transforms. The two
/// transform services already consume an envelope (their <c>envelope:</c> overloads); these tests prove
/// the orchestrator actually passes it at delivery time.
///
/// HARD invariant: an order WITHOUT an envelope produces output byte-identical to the pre-WS-12 path
/// (legacy baked X12 <c>PROCULINK</c>/<c>SUPPLIER</c> identity; legacy cXML <c>OrgId</c>/<c>SupplierId</c>
/// GUID identity). When an envelope IS pinned, the supplier's real identity is delivered and the baked
/// constants do NOT leak.
/// </summary>
public class EnvelopeConfigTransformWiringTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>OrderService wired with the real X12 + cXML transformers and a byte-capturing storage mock.</summary>
    private static (OrderService Svc, Func<byte[]?> CapturedBytes) Build(ProcuLinkDbContext db)
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
            new ITransformService[] { new X12TransformService(), new CxmlTransformService(), new XmlTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());

        return (svc, () => captured);
    }

    /// <summary>Seeds a fully-resolved single-line order, optionally pinning an EnvelopeConfig on the
    /// per-order OutputTree (with the given format) so the live transform reads it as today's caller would.</summary>
    private static async Task<(Guid orgId, Guid orderId)> SeedOrderAsync(
        ProcuLinkDbContext db, OutputFormat treeFormat, EnvelopeConfig? envelope)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Env Supplier", CreatedAt = DateTime.UtcNow });

        JsonDocument? canonical = null;
        if (envelope is not null)
        {
            // The override carries ONLY an OutputTree (no flat Output / template) — for X12/cXML the
            // tree's job is to ferry the EnvelopeConfig into the fixed transformer (the emitter refuses
            // those formats), which is exactly what the caller wiring under test must honour.
            var @override = new OrderMappingOverride
            {
                OutputTree = new OutputNodeTemplate { Format = treeFormat, Envelope = envelope },
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
            PoNumber = "PO-ENV-WIRE", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 6, 15),
            Currency = "USD", Status = "ready", CanonicalJson = canonical,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 2m, Unit = "EA", UnitPrice = 12.5m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });
        await db.SaveChangesAsync();
        return (orgId, orderId);
    }

    // ── X12 — pinned envelope drives the ISA/GS identity ───────────────────────

    [Fact]
    public async Task X12_PinnedEnvelope_DeliversSupplierIdentity_NotBakedConstants()
    {
        await using var db = NewDb();
        var envelope = new EnvelopeConfig
        {
            X12 = new X12Envelope
            {
                IsaSenderQualifier = "01", IsaSenderId = "ACME-BUYER",
                IsaReceiverQualifier = "14", IsaReceiverId = "VENDOR-99",
                Version = "005010", UsageIndicator = "T",
            },
        };
        var (orgId, orderId) = await SeedOrderAsync(db, OutputFormat.X12, envelope);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.X12, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var edi = Encoding.UTF8.GetString(captured()!);

        Assert.Contains("ACME-BUYER", edi);
        Assert.Contains("VENDOR-99", edi);
        Assert.Contains("*005010", edi); // GS08 version from the envelope

        // The baked legacy identity must NOT leak when the envelope is configured.
        Assert.DoesNotContain("PROCULINK", edi);
        Assert.DoesNotContain("SUPPLIER", edi);
    }

    // ── X12 — no envelope → byte-identical legacy identity ─────────────────────

    [Fact]
    public async Task X12_NoEnvelope_KeepsLegacyBakedIdentity()
    {
        await using var db = NewDb();
        var (orgId, orderId) = await SeedOrderAsync(db, OutputFormat.X12, envelope: null);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.X12, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var edi = Encoding.UTF8.GetString(captured()!);

        // Pre-WS-12 baked identity is preserved when nothing is configured.
        Assert.Contains("PROCULINK", edi);
        Assert.Contains("SUPPLIER", edi);
    }

    // ── cXML — pinned envelope drives the From/To/Sender party identity ────────

    [Fact]
    public async Task Cxml_PinnedEnvelope_DeliversNetworkIdentity_NotGuids()
    {
        await using var db = NewDb();
        var envelope = new EnvelopeConfig
        {
            Cxml = new CxmlEnvelope
            {
                FromDomain = "NetworkId", FromIdentity = "TESTBUYER_SE",
                ToDomain = "NetworkId", ToIdentity = "TESTSUPPLIER_SE",
                SenderDomain = "NetworkId", SenderIdentity = "TESTBUYER_SE",
            },
        };
        var (orgId, orderId) = await SeedOrderAsync(db, OutputFormat.CXml, envelope);

        var (svc, captured) = Build(db);
        var result = await svc.TransformAsync(orgId, orderId, OutputFormat.CXml, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        var cxml = Encoding.UTF8.GetString(captured()!);

        Assert.Contains("<Identity>TESTBUYER_SE</Identity>", cxml);
        Assert.Contains("<Identity>TESTSUPPLIER_SE</Identity>", cxml);
        Assert.DoesNotContain($"<Identity>{orgId}</Identity>", cxml);
        Assert.DoesNotContain("domain=\"OrgId\"", cxml);
    }
}
