using System.Globalization;
using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// The Testforsikring bug: a PO from a buyer that is NOT Exemple was routed to a cXML supplier whose
/// configured From identity is Exemple's VatNr (FR99000000000). The cXML From/Identity is the
/// BUYER's identity, so a different buyer must NOT inherit the configured From's VatNr. These tests
/// pin:
/// <list type="bullet">
///   <item>buyer tax id present → From/Identity = it, KEEPING the configured From domain.</item>
///   <item>buyer tax id absent → the configured (or legacy) From is byte-identical to today.</item>
///   <item>line tax present → an ItemOut/Tax/Money block (sibling of ItemDetail) is emitted; absent → byte-identical.</item>
/// </list>
/// </summary>
public class CxmlTransformServiceBuyerTaxAndLineTaxTests
{
    private static readonly Guid OrgId      = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SupplierId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PurchaseOrderEntity BuildOrder() => new()
    {
        Id         = Guid.NewGuid(),
        OrgId      = OrgId,
        SupplierId = SupplierId,
        PoNumber   = "REDACTED-ITEM",
        OrderDate  = new DateOnly(2026, 6, 15),
        Currency   = "NOK",
        Status     = "ready",
        Lines =
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Description = "Service", Quantity = 1m, Unit = "EA", UnitPrice = 3337.08m,
                NeedsReview = false, Confidence = 1.0f,
            },
        },
    };

    private static async Task<string> RenderAsync(PurchaseOrderEntity order, CxmlCredentialConfig? creds = null)
    {
        var result = await new CxmlTransformService()
            .TransformAsync(order, OutputFormat.CXml, CancellationToken.None, creds);
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        return await reader.ReadToEndAsync();
    }

    private static (string? Domain, string? Identity) ReadFrom(string xml)
    {
        var cred = XDocument.Parse(xml).Descendants("From").Single().Element("Credential")!;
        return (cred.Attribute("domain")?.Value, cred.Element("Identity")?.Value);
    }

    // ── Dynamic From identity ──────────────────────────────────────────────────

    [Fact]
    public async Task BuyerTaxId_Present_BecomesFromIdentity_KeepingConfiguredFromDomain()
    {
        // Supplier connection configured with Exemple's From identity (the wrong-buyer scenario).
        var creds = new CxmlCredentialConfig(
            FromDomain: "NetworkId", FromIdentity: "FR99000000000",
            ToDomain: "NetworkId", ToIdentity: "REDACTED-NETWORK-ID",
            SenderDomain: "NetworkId", SenderIdentity: "REDACTED-NETWORK-ID",
            SenderSharedSecret: null);

        var order = BuildOrder();
        order.BuyerTaxId = "NO999000000"; // a DIFFERENT buyer (Testforsikring org number).

        var xml = await RenderAsync(order, creds);

        var from = ReadFrom(xml);
        from.Identity.Should().Be("NO999000000", "the From identity is the order's buyer tax id");
        from.Domain.Should().Be("NetworkId", "the configured From domain is preserved");
        xml.Should().NotContain("FR99000000000", "the configured From VatNr must not leak for a different buyer");
        // To / Sender are unaffected.
        XDocument.Parse(xml).Descendants("To").Single().Descendants("Identity").Single().Value.Should().Be("REDACTED-NETWORK-ID");
    }

    [Fact]
    public async Task BuyerTaxId_Present_NoConfiguredFromDomain_UsesNetworkIdDefault()
    {
        // No cXML credentials at all (legacy GUID path), but the order carries a buyer tax id.
        var order = BuildOrder();
        order.BuyerTaxId = "NO999000000";

        var xml = await RenderAsync(order, creds: null);

        var from = ReadFrom(xml);
        from.Identity.Should().Be("NO999000000");
        from.Domain.Should().Be("NetworkId", "a tax-id identity is a NetworkId, not the legacy OrgId domain");
        xml.Should().NotContain(OrgId.ToString(), "the legacy OrgId GUID must not appear once a buyer tax id is set");
    }

    [Fact]
    public async Task BuyerTaxId_Absent_ConfiguredFrom_IsByteIdenticalToToday()
    {
        var creds = new CxmlCredentialConfig(
            FromDomain: "NetworkId", FromIdentity: "FR99000000000",
            ToDomain: "NetworkId", ToIdentity: "REDACTED-NETWORK-ID",
            SenderDomain: "NetworkId", SenderIdentity: "REDACTED-NETWORK-ID",
            SenderSharedSecret: null);

        var orderNoTax = BuildOrder();                 // BuyerTaxId == null
        var orderTaxBlank = BuildOrder();
        orderTaxBlank.BuyerTaxId = "   ";              // blank = unset

        var xmlNull  = await RenderAsync(orderNoTax, creds);
        var xmlBlank = await RenderAsync(orderTaxBlank, creds);

        // With no buyer tax id the configured From identity is emitted, exactly as before.
        ReadFrom(xmlNull).Should().Be(("NetworkId", "FR99000000000"));
        ReadFrom(xmlBlank).Should().Be(("NetworkId", "FR99000000000"), "a blank buyer tax id is treated as unset");
    }

    [Fact]
    public async Task BuyerTaxId_Absent_NoConfig_IsLegacyOrgIdGuid()
    {
        var xml = await RenderAsync(BuildOrder(), creds: null);
        ReadFrom(xml).Should().Be(("OrgId", OrgId.ToString()), "no buyer tax id + no config = legacy byte-identical output");
    }

    // ── Line tax in ItemOut (sibling of ItemDetail) ────────────────────────────

    [Fact]
    public async Task LineTaxAmount_Present_EmitsItemOutTaxMoney()
    {
        var order = BuildOrder();
        order.Lines[0].TaxAmount = 834.27m;
        order.Lines[0].TaxRate   = 25m;

        var xml = await RenderAsync(order);
        var doc = XDocument.Parse(xml);

        // cXML 1.2: <Tax> is a child of <ItemOut> — a SIBLING of <ItemDetail>, not nested inside it.
        var itemOut = doc.Descendants("ItemOut").Single();
        var tax = itemOut.Element("Tax");
        tax.Should().NotBeNull("a captured per-line tax amount must produce a <Tax> block");
        itemOut.Element("ItemDetail")!.Element("Tax")
            .Should().BeNull("<Tax> must NOT be nested in ItemDetail (DTD-invalid)");
        var money = tax!.Element("Money")!;
        money.Attribute("currency")!.Value.Should().Be("NOK");
        money.Value.Should().Be("834.27");
        // Description carries the rate so the supplier sees what tax this is.
        tax.Element("Description")!.Value.Should().Contain("25");
    }

    [Fact]
    public async Task LineTax_RateNotCaptured_DerivesRateFromAmount()
    {
        // The extractor often captures the tax AMOUNT but not the RATE (TaxRate==0 = "not stated").
        // The <Tax> description must then derive the rate from amount ÷ net (834.27/3337.08 = 25%),
        // not print the misleading literal "VAT 0%".
        var order = BuildOrder();
        order.Lines[0].Quantity  = 1m;
        order.Lines[0].UnitPrice = 3337.08m;
        order.Lines[0].TaxAmount = 834.27m;
        order.Lines[0].TaxRate   = 0m;       // not captured

        var xml = await RenderAsync(order);
        var doc = XDocument.Parse(xml);
        var tax = doc.Descendants("ItemOut").Single().Element("Tax")!;
        tax.Element("Description")!.Value.Should().Be("VAT 25%");
        tax.Element("Description")!.Value.Should().NotContain("0%");
    }

    [Fact]
    public async Task LineTaxAmount_Absent_NoTaxBlock_ByteIdentical()
    {
        var order = BuildOrder();          // no TaxAmount on the line
        order.Lines[0].TaxRate = 25m;      // rate alone (no amount) → still no <Tax> (amount is what we emit)

        var xml = await RenderAsync(order);
        xml.Should().NotContain("<Tax>", "with no captured tax AMOUNT the ItemDetail is byte-identical to today");
    }

    [Fact]
    public async Task LineTax_PlacedInItemOut_AfterItemDetail()
    {
        var order = BuildOrder();
        order.Lines[0].TaxAmount = 834.27m;

        var xml = await RenderAsync(order);

        var itemDetailEndIdx = xml.IndexOf("</ItemDetail>", StringComparison.Ordinal);
        var taxIdx           = xml.IndexOf("<Tax>", StringComparison.Ordinal);
        var itemOutEndIdx    = xml.IndexOf("</ItemOut>", StringComparison.Ordinal);

        itemDetailEndIdx.Should().BeGreaterThanOrEqualTo(0);
        // cXML 1.2: <Tax> is a sibling of ItemDetail inside ItemOut → after </ItemDetail>, before </ItemOut>.
        taxIdx.Should().BeGreaterThan(itemDetailEndIdx);
        taxIdx.Should().BeLessThan(itemOutEndIdx);
    }
}
