using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Peppol wedge — Track A. Proves the BIS Billing 3.0 generator emits the right
/// CustomizationID/ProfileID and the mandatory business-term skeleton.
/// </summary>
public class PeppolBisInvoiceTransformServiceTests
{
    private static readonly XNamespace Cbc =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Cac =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    private static InvoiceEntity MakeInvoice() => new()
    {
        Id            = Guid.NewGuid(),
        InvoiceNumber = "INV-2026-001",
        IssueDate     = new DateOnly(2026, 5, 28),
        DueDate       = new DateOnly(2026, 6, 27),
        Currency      = "EUR",
        BuyerRef      = "PO-9001",
        SubTotal      = 100m,
        TaxTotal      = 20m,
        GrandTotal    = 120m,
        Status        = "approved",
    };

    private static InvoiceLineEntity MakeLine(int num) => new()
    {
        Id          = Guid.NewGuid(),
        LineNumber  = num,
        Description = "Steel bracket",
        Quantity    = 10m,
        UnitCode    = "EA",
        UnitPrice   = 10m,
        TaxRate     = 0.20m,
        LineTotal   = 100m,
        SupplierItemCode = "SUP-123",
        BuyerItemCode    = "BUY-999",
    };

    private static PeppolPartyOptions FullParty() => new()
    {
        SellerName           = "Northwind Trading OÜ",
        SellerEndpointId     = "0192:998765432",
        SellerEndpointScheme = "0192",
        SellerVatId          = "EE100123456",
        BuyerName            = "Fabrikam AS",
        BuyerEndpointId      = "0088:1234567890123",
        BuyerEndpointScheme  = "0088",
        BuyerVatId           = "EE100987654",
    };

    private static XDocument Generate(PeppolPartyOptions? opts = null)
    {
        var svc = new PeppolBisInvoiceTransformService(opts);
        return svc.BuildDocument(MakeInvoice(), new[] { MakeLine(1) });
    }

    [Fact]
    public void Format_IsPeppol()
        => new PeppolBisInvoiceTransformService().Format.Should().Be("peppol");

    [Fact]
    public void Document_HasBisCustomizationIdAndProfileId()
    {
        var doc = Generate(FullParty());
        var root = doc.Root!;

        root.Element(Cbc + "CustomizationID")!.Value
            .Should().Be(PeppolBisInvoiceTransformService.CustomizationId)
            .And.Contain("en16931").And.Contain("billing:3.0");
        root.Element(Cbc + "ProfileID")!.Value
            .Should().Be(PeppolBisInvoiceTransformService.ProfileId);
    }

    [Fact]
    public void Document_HasMandatoryHeaderTerms()
    {
        var root = Generate(FullParty()).Root!;

        root.Element(Cbc + "ID")!.Value.Should().Be("INV-2026-001");          // BT-1
        root.Element(Cbc + "IssueDate")!.Value.Should().Be("2026-05-28");     // BT-2
        root.Element(Cbc + "DueDate")!.Value.Should().Be("2026-06-27");       // BT-9
        root.Element(Cbc + "InvoiceTypeCode")!.Value.Should().Be("380");      // BT-3
        root.Element(Cbc + "DocumentCurrencyCode")!.Value.Should().Be("EUR"); // BT-5
        root.Element(Cbc + "BuyerReference")!.Value.Should().Be("PO-9001");   // BT-10
    }

    [Fact]
    public void Document_HasSellerAndBuyerEndpointIds()
    {
        var root = Generate(FullParty()).Root!;

        var seller = root.Element(Cac + "AccountingSupplierParty")!
                         .Element(Cac + "Party")!;
        seller.Element(Cbc + "EndpointID")!.Value.Should().Be("0192:998765432");  // BT-34
        seller.Element(Cbc + "EndpointID")!.Attribute("schemeID")!.Value.Should().Be("0192");

        var buyer = root.Element(Cac + "AccountingCustomerParty")!
                        .Element(Cac + "Party")!;
        buyer.Element(Cbc + "EndpointID")!.Value.Should().Be("0088:1234567890123"); // BT-49
    }

    [Fact]
    public void Document_HasVatBreakdownWithCategoryAndScheme()
    {
        var root = Generate(FullParty()).Root!;
        var category = root.Descendants(Cac + "TaxCategory").First();

        category.Element(Cbc + "ID")!.Value.Should().Be("S");          // BT-118 (standard)
        category.Element(Cbc + "Percent")!.Value.Should().Be("20.00"); // BT-119
        category.Element(Cac + "TaxScheme")!.Element(Cbc + "ID")!.Value.Should().Be("VAT");
    }

    [Fact]
    public void Document_TotalsReconcile()
    {
        var root = Generate(FullParty()).Root!;
        var lmt  = root.Element(Cac + "LegalMonetaryTotal")!;

        lmt.Element(Cbc + "LineExtensionAmount")!.Value.Should().Be("100.00"); // BT-106
        lmt.Element(Cbc + "TaxExclusiveAmount")!.Value.Should().Be("100.00");  // BT-109
        lmt.Element(Cbc + "TaxInclusiveAmount")!.Value.Should().Be("120.00");  // BT-112
        lmt.Element(Cbc + "PayableAmount")!.Value.Should().Be("120.00");       // BT-115
    }

    [Fact]
    public void Document_HasInvoiceLineWithItemAndPrice()
    {
        var root = Generate(FullParty()).Root!;
        var line = root.Elements(Cac + "InvoiceLine").Single();

        line.Element(Cbc + "ID")!.Value.Should().Be("1");                            // BT-126
        line.Element(Cbc + "InvoicedQuantity")!.Attribute("unitCode")!.Value.Should().Be("EA");
        line.Element(Cbc + "LineExtensionAmount")!.Value.Should().Be("100.00");      // BT-131
        line.Element(Cac + "Item")!.Element(Cbc + "Name")!.Value.Should().Be("Steel bracket"); // BT-153
        line.Element(Cac + "Price")!.Element(Cbc + "PriceAmount")!.Value.Should().Be("10.00"); // BT-146
    }

    [Fact]
    public async Task TransformAsync_ProducesParseableUtf8Xml()
    {
        var svc   = new PeppolBisInvoiceTransformService(FullParty());
        var bytes = await svc.TransformAsync(MakeInvoice(), new[] { MakeLine(1) }, default);
        var text  = Encoding.UTF8.GetString(bytes);

        text.Should().Contain("CustomizationID");
        var parsed = XDocument.Parse(text);
        parsed.Root!.Name.LocalName.Should().Be("Invoice");
    }

    [Fact]
    public void Document_NoParty_OmitsEndpointsButStaysWellFormed()
    {
        // No party options → the generator must NOT fabricate identifiers; it
        // simply omits them. The validator (separate test) flags this honestly.
        var root = Generate(opts: null).Root!;

        var seller = root.Element(Cac + "AccountingSupplierParty")!.Element(Cac + "Party")!;
        seller.Element(Cbc + "EndpointID").Should().BeNull();
        // Still a structurally valid UBL Invoice root.
        root.Name.LocalName.Should().Be("Invoice");
    }
}
