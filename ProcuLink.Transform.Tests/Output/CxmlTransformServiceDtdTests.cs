using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// T7 — configurable cXML <c>&lt;!DOCTYPE&gt;</c>. The founder needs the cXML DTD to be set
/// per-supplier (free-text DTD URI), NOT hardcoded. The HARD invariant (the gate): an UNSET DTD
/// (<see cref="CxmlCredentialConfig.DtdSystemId"/> null/blank) emits NO DocumentType node, so the
/// cXML bytes stay BYTE-IDENTICAL to the pre-feature transform. When a DtdSystemId is set, a
/// <c>&lt;!DOCTYPE cXML …&gt;</c> appears after the <c>&lt;?xml?&gt;</c> declaration and before the
/// <c>&lt;cXML&gt;</c> root (SYSTEM form, or PUBLIC form when DtdPublicId is also set), and the rest
/// of the document is otherwise unchanged.
/// </summary>
public class CxmlTransformServiceDtdTests
{
    private static readonly Guid OrgId      = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupplierId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string DtdUri    = "http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd";
    private const string DtdPublic = "-//cXML//DTD cXML 1.2.024//EN";

    private static PurchaseOrderEntity BuildOrder() => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = OrgId,
        SupplierId = SupplierId,
        PoNumber   = "PO-DTD-1",
        OrderDate  = new DateOnly(2026, 6, 21),
        Currency   = "EUR",
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

    private static async Task<string> RenderAsync(CxmlCredentialConfig? creds)
    {
        var result = await new CxmlTransformService()
            .TransformAsync(BuildOrder(), OutputFormat.CXml, CancellationToken.None, creds);
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Strip the two volatile fields (payloadID GUID + ISO timestamp) so two renders a moment
    /// apart compare equal on everything else — the byte-parity comparison the gate depends on.</summary>
    private static string Normalize(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root!;
        root.Attribute("payloadID")!.Value = "PAYLOAD";
        root.Attribute("timestamp")!.Value = "TIMESTAMP";
        return doc.Declaration + Environment.NewLine + doc.ToString();
    }

    // ── THE GATE: unset DTD → byte-identical to today ──────────────────────────

    [Fact]
    public async Task UnsetDtd_NullConfig_NoDoctype()
    {
        var xml = await RenderAsync(null);
        xml.Should().NotContain("<!DOCTYPE");
    }

    [Fact]
    public async Task UnsetDtd_BlankSystemId_NoDoctype()
    {
        var creds = new CxmlCredentialConfig(null, null, null, null, null, null, null)
        {
            DtdSystemId = "   ",
            DtdPublicId = "anything", // public alone (no system) must NOT emit a DOCTYPE
        };
        var xml = await RenderAsync(creds);
        xml.Should().NotContain("<!DOCTYPE");
    }

    [Fact]
    public async Task UnsetDtd_ProducesByteIdenticalCxml_ToConfigWithoutDtd()
    {
        // The config carries network credentials but NO DTD → identical bytes (sans the two volatile
        // fields) to the exact same config that simply never knew about a DTD. This pins that adding
        // the DTD fields is byte-inert when unused.
        var noDtd = new CxmlCredentialConfig(
            "NetworkId", "REDACTED-NETWORK-ID", "NetworkId", "REDACTED-NETWORK-ID", "NetworkId", "REDACTED-NETWORK-ID", null);

        var withNullDtd = noDtd with { DtdSystemId = null, DtdPublicId = null };

        Normalize(await RenderAsync(noDtd))
            .Should().Be(Normalize(await RenderAsync(withNullDtd)));
    }

    // ── SYSTEM form ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SystemOnly_EmitsSystemDoctype_BeforeRoot()
    {
        var creds = new CxmlCredentialConfig(null, null, null, null, null, null, null)
        {
            DtdSystemId = DtdUri,
        };
        var xml = await RenderAsync(creds);

        xml.Should().Contain($"<!DOCTYPE cXML SYSTEM \"{DtdUri}\">");

        var declIdx    = xml.IndexOf("<?xml", StringComparison.Ordinal);
        var doctypeIdx = xml.IndexOf("<!DOCTYPE", StringComparison.Ordinal);
        var rootIdx    = xml.IndexOf("<cXML", StringComparison.Ordinal);
        doctypeIdx.Should().BeGreaterThan(declIdx);
        rootIdx.Should().BeGreaterThan(doctypeIdx);
    }

    [Fact]
    public async Task SystemDtd_LeavesRootAndHeaderUnchanged()
    {
        // The Header + body of a DTD-configured document must be identical to the un-DTD'd document
        // apart from the prepended DOCTYPE line (and the two volatile fields). Compare the parsed
        // root element trees for equality.
        var withDtd = new CxmlCredentialConfig(null, null, null, null, null, null, null)
        {
            DtdSystemId = DtdUri,
        };

        var withDtdXml = await RenderAsync(withDtd);
        var withoutXml = await RenderAsync(null);

        static string RootOnly(string xml)
        {
            var root = XDocument.Parse(xml).Root!;
            root.Attribute("payloadID")!.Value = "PAYLOAD";
            root.Attribute("timestamp")!.Value = "TIMESTAMP";
            return root.ToString();
        }

        RootOnly(withDtdXml).Should().Be(RootOnly(withoutXml));
    }

    // ── PUBLIC form ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublicAndSystem_EmitsPublicDoctype()
    {
        var creds = new CxmlCredentialConfig(null, null, null, null, null, null, null)
        {
            DtdSystemId = DtdUri,
            DtdPublicId = DtdPublic,
        };
        var xml = await RenderAsync(creds);

        xml.Should().Contain($"<!DOCTYPE cXML PUBLIC \"{DtdPublic}\" \"{DtdUri}\">");
    }

    [Fact]
    public async Task ConfiguredDtd_DocumentStillParses()
    {
        // A DOCTYPE pointing at an external SYSTEM id must not break parsing (DTD is not resolved
        // when ProhibitDtd/DtdProcessing default applies in XDocument.Parse, which permits the node).
        var creds = new CxmlCredentialConfig(
            "NetworkId", "REDACTED-NETWORK-ID", "NetworkId", "REDACTED-NETWORK-ID", "NetworkId", "REDACTED-NETWORK-ID", null)
        {
            DtdSystemId = DtdUri,
        };
        var xml = await RenderAsync(creds);

        var doc = XDocument.Parse(xml);
        doc.DocumentType!.Name.Should().Be("cXML");
        doc.DocumentType.SystemId.Should().Be(DtdUri);
        doc.Root!.Name.LocalName.Should().Be("cXML");
    }

    [Fact]
    public async Task ConfiguredDtd_ValuesAreTrimmed()
    {
        var creds = new CxmlCredentialConfig(null, null, null, null, null, null, null)
        {
            DtdSystemId = "  " + DtdUri + "  ",
            DtdPublicId = "  " + DtdPublic + "  ",
        };
        var xml = await RenderAsync(creds);

        xml.Should().Contain($"<!DOCTYPE cXML PUBLIC \"{DtdPublic}\" \"{DtdUri}\">");
    }
}
