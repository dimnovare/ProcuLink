using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// WS-12 characterization tests: a per-connection <see cref="EnvelopeConfig"/> drives the X12
/// ISA/GS envelope identity and the cXML From/To/Sender party identity. The HARD invariant is the
/// null-fallback: when no envelope is supplied (the overwhelming common case today) the output must
/// be BYTE-FOR-BYTE identical to the pre-WS-12 transform (X12 <c>ISA06=PROCULINK</c>/<c>ISA08=SUPPLIER</c>,
/// cXML legacy <c>OrgId</c>/<c>SupplierId</c> GUID identities). When an envelope IS supplied the
/// configured ids replace the baked constants — and the constants must NOT leak into the output.
/// </summary>
public class EnvelopeConfigTransformTests
{
    private static readonly Guid OrgId      = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupplierId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PurchaseOrderEntity BuildOrder() => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = OrgId,
        SupplierId = SupplierId,
        PoNumber   = "PO-ENV-1",
        OrderDate  = new DateOnly(2026, 6, 15),
        Currency   = "USD",
        Status     = "ready",
        Lines =
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 2m, Unit = "EA", UnitPrice = 12.5m,
                NeedsReview = false, Confidence = 1.0f,
            },
        },
    };

    private static async Task<string> ReadAsString(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content, Encoding.UTF8, leaveOpen: true);
        var s = await reader.ReadToEndAsync();
        result.Content.Position = 0;
        return s;
    }

    private static List<string> SplitSegments(string edi) =>
        edi.Split('~', StringSplitOptions.RemoveEmptyEntries)
           .Select(s => s.Trim('\r', '\n', ' ', '\t'))
           .Where(s => s.Length > 0)
           .ToList();

    // ── X12: null envelope → byte-identical to the legacy 4-arg path ───────────

    [Fact]
    public async Task X12_NullEnvelope_IsByteIdenticalToLegacyConstantPath()
    {
        var svc = new X12TransformService();

        // The pre-WS-12 entry point — the ITransformService 4-arg overload (cxmlCredentials only).
        var legacy = await ReadAsString(
            await svc.TransformAsync(BuildOrder(), OutputFormat.X12, CancellationToken.None));

        // The WS-12 overload with a null envelope must produce the same bytes. ISA09/10 carry the
        // UTC date+time so two calls a moment apart can differ ONLY in those positional fields;
        // compare the segment lists after masking those two ISA elements out.
        var withEnvelope = await ReadAsString(
            await svc.TransformAsync(BuildOrder(), OutputFormat.X12, CancellationToken.None, envelope: null));

        MaskIsaTimestamp(legacy).Should().Be(MaskIsaTimestamp(withEnvelope));
    }

    [Fact]
    public async Task X12_NullEnvelope_EmitsLegacyProculinkSupplierIdentity()
    {
        var svc = new X12TransformService();
        var edi = await ReadAsString(
            await svc.TransformAsync(BuildOrder(), OutputFormat.X12, CancellationToken.None, envelope: null));

        var segs = SplitSegments(edi);
        var isa  = segs[0];
        var gs   = segs[1];

        // ISA05=ZZ, ISA06=PROCULINK (padded 15), ISA07=ZZ, ISA08=SUPPLIER (padded 15), ISA12=00401, ISA15=P.
        var isaEls = isa.Split('*');
        isaEls[5].Should().Be("ZZ");
        isaEls[6].Should().Be("PROCULINK".PadRight(15));
        isaEls[7].Should().Be("ZZ");
        isaEls[8].Should().Be("SUPPLIER".PadRight(15));
        isaEls[12].Should().Be("00401");
        isaEls[15].Should().Be("P");

        // GS02=PROCULINK, GS03=SUPPLIER, GS08=004010.
        gs.Should().Be("GS*PO*PROCULINK*SUPPLIER*" + gs.Split('*')[4] + "*" + gs.Split('*')[5] + "*1*X*004010");
    }

    // ── X12: configured envelope → the supplier's real identity, no leaked constants ──

    [Fact]
    public async Task X12_ConfiguredEnvelope_UsesSupplierIdentity_NotBakedConstants()
    {
        var svc = new X12TransformService();
        var envelope = new EnvelopeConfig
        {
            X12 = new X12Envelope
            {
                IsaSenderQualifier   = "01",
                IsaSenderId          = "ACME-BUYER",
                IsaReceiverQualifier = "14",
                IsaReceiverId        = "VENDOR-99",
                Version              = "005010",
                UsageIndicator       = "T",
            },
        };

        var edi  = await ReadAsString(
            await svc.TransformAsync(BuildOrder(), OutputFormat.X12, CancellationToken.None, envelope));
        var segs = SplitSegments(edi);
        var isa  = segs[0];
        var gs   = segs[1];

        var isaEls = isa.Split('*');
        isaEls[5].Should().Be("01");
        isaEls[6].Should().Be("ACME-BUYER".PadRight(15));
        isaEls[7].Should().Be("14");
        isaEls[8].Should().Be("VENDOR-99".PadRight(15));
        isaEls[12].Should().Be("00501");          // ISA12 derived from GS version 005010
        isaEls[15].Should().Be("T");              // test usage indicator

        gs.Should().StartWith("GS*PO*ACME-BUYER*VENDOR-99*");
        gs.Should().EndWith("*X*005010");

        // The baked legacy identity must NOT leak anywhere in the document.
        edi.Should().NotContain("PROCULINK");
        edi.Should().NotContain("SUPPLIER");
    }

    // ── cXML: null envelope → byte-identical legacy GUID identities ────────────

    [Fact]
    public async Task Cxml_NullEnvelope_EmitsLegacyGuidIdentities()
    {
        var svc = new CxmlTransformService();
        var xml = await ReadAsString(
            await svc.TransformAsync(BuildOrder(), OutputFormat.CXml, CancellationToken.None, envelope: null));

        var (fromDomain, fromIdentity) = ReadCredential(xml, "From");
        var (toDomain, toIdentity)     = ReadCredential(xml, "To");
        var (senderDomain, senderId)   = ReadCredential(xml, "Sender");

        fromDomain.Should().Be("OrgId");
        fromIdentity.Should().Be(OrgId.ToString());
        toDomain.Should().Be("SupplierId");
        toIdentity.Should().Be(SupplierId.ToString());
        senderDomain.Should().Be("NetworkUserId");
        senderId.Should().Be("proculink");
    }

    // ── cXML: configured envelope → real network identity, no leaked GUIDs ─────

    [Fact]
    public async Task Cxml_ConfiguredEnvelope_CarriesRealNetworkIds_NotGuids()
    {
        var svc = new CxmlTransformService();
        var envelope = new EnvelopeConfig
        {
            Cxml = new CxmlEnvelope
            {
                FromDomain     = "NetworkId", FromIdentity   = "TESTBUYER_SE",
                ToDomain       = "NetworkId", ToIdentity     = "TESTSUPPLIER_SE",
                SenderDomain   = "NetworkId", SenderIdentity = "TESTBUYER_SE",
            },
        };

        var xml = await ReadAsString(
            await svc.TransformAsync(BuildOrder(), OutputFormat.CXml, CancellationToken.None, envelope));

        ReadCredential(xml, "From").Should().Be(("NetworkId", "TESTBUYER_SE"));
        ReadCredential(xml, "To").Should().Be(("NetworkId", "TESTSUPPLIER_SE"));
        ReadCredential(xml, "Sender").Should().Be(("NetworkId", "TESTBUYER_SE"));

        // The internal GUIDs must NOT leak into the Header.
        var header = XDocument.Parse(xml).Descendants("Header").Single().ToString();
        header.Should().NotContain(OrgId.ToString());
        header.Should().NotContain(SupplierId.ToString());

        // The envelope carries no shared secret (it is a reference, never inline).
        xml.Should().NotContain("SharedSecret");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Replaces the volatile ISA09 date + ISA10 time elements with fixed placeholders so two
    /// emitted interchanges produced a moment apart compare equal on everything but the timestamp.</summary>
    private static string MaskIsaTimestamp(string edi)
    {
        var segs = SplitSegments(edi);
        var isaEls = segs[0].Split('*');
        isaEls[9]  = "######"; // ISA09 yyMMdd
        isaEls[10] = "####";   // ISA10 HHmm
        segs[0] = string.Join('*', isaEls);

        // GS04 (date) + GS05 (time) are also UTC-now; mask them too.
        var gsEls = segs[1].Split('*');
        gsEls[4] = "########"; // GS04 yyyyMMdd
        gsEls[5] = "####";     // GS05 HHmm
        segs[1] = string.Join('*', gsEls);

        return string.Join('~', segs);
    }

    private static (string? Domain, string? Identity) ReadCredential(string xml, string wrapper)
    {
        var block = XDocument.Parse(xml).Descendants(wrapper).Single();
        var cred  = block.Element("Credential")!;
        return (cred.Attribute("domain")?.Value, cred.Element("Identity")?.Value);
    }
}
