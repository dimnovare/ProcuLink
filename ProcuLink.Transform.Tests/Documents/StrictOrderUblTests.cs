using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Conformance;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StrictOrderUblTests
{
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static string V(XElement e, params string[] path) => path.Aggregate(e, (x, n) => x.Elements().First(c => c.Name.LocalName == n)).Value;

    [Fact]
    public async Task The_order_carries_every_read_value_and_is_schema_valid()
    {
        var text = await StrictOrderFixture.Render("ubl-2-1-order");
        UblSchemaValidator.Validate(text).Valid.Should().BeTrue(string.Join("; ", UblSchemaValidator.Validate(text).Findings));
        var root = XDocument.Parse(text).Root!;

        var buyer = root.Element(Cac + "BuyerCustomerParty")!.Element(Cac + "Party")!;
        buyer.Elements().Select(e => e.Name.LocalName).Should().Equal("PartyName", "PostalAddress", "PartyTaxScheme", "Contact");
        (V(buyer, "PostalAddress", "StreetName"), V(buyer, "PostalAddress", "CityName"), V(buyer, "PostalAddress", "PostalZone"), V(buyer, "PostalAddress", "Country", "IdentificationCode"))
            .Should().Be(("Example Street 1", "Berlin", "10115", "DE"));
        (V(buyer, "PartyTaxScheme", "CompanyID"), V(buyer, "PartyTaxScheme", "TaxScheme", "ID"), V(buyer, "Contact", "ElectronicMail"))
            .Should().Be(("DE000000000", "VAT", "orders@example.com"));

        var seller = root.Element(Cac + "SellerSupplierParty")!.Element(Cac + "Party")!;
        (V(seller, "PartyName", "Name"), V(seller, "PostalAddress", "CityName"), V(seller, "PostalAddress", "Country", "IdentificationCode"), V(seller, "PartyTaxScheme", "CompanyID"))
            .Should().Be(("Example Supplier OU", "Tallinn", "EE", "EE000000000"));

        var delivery = root.Element(Cac + "Delivery")!;
        delivery.Elements().Select(e => e.Name.LocalName).Should().Equal("DeliveryLocation", "RequestedDeliveryPeriod", "DeliveryParty");
        (V(delivery, "DeliveryLocation", "Address", "StreetName"), V(delivery, "DeliveryLocation", "Address", "Country", "IdentificationCode"),
         V(delivery, "RequestedDeliveryPeriod", "StartDate"), V(delivery, "RequestedDeliveryPeriod", "EndDate"), V(delivery, "DeliveryParty", "PartyName", "Name"))
            .Should().Be(("Warehouse Road 3", "DE", "2026-12-15", "2026-12-15", "Example Buyer Ltd Warehouse"));

        V(root, "TaxTotal", "TaxAmount").Should().Be("11.40");
        var total = root.Element(Cac + "AnticipatedMonetaryTotal")!;
        total.Elements().Select(e => (e.Name.LocalName, e.Value, (string?)e.Attribute("currencyID"))).Should().Equal(
            ("LineExtensionAmount", "60.00", "EUR"), ("TaxExclusiveAmount", "60.00", "EUR"), ("TaxInclusiveAmount", "71.40", "EUR"), ("PayableAmount", "71.40", "EUR"));
        root.Descendants(Cbc + "Quantity").Select(q => (string)q.Attribute("unitCode")!).Should().Equal("H87", "H87", "H87");
    }

    [Fact]
    public async Task A_delivery_date_alone_still_makes_a_delivery_and_no_totals_means_no_total_blocks()
    {
        var read = StrictOrderFixture.Read(without: [FieldMeanings.DeliveryName, FieldMeanings.DeliveryStreet, FieldMeanings.DeliveryCity,
            FieldMeanings.DeliveryPostcode, FieldMeanings.DeliveryCountry, FieldMeanings.TotalNet, FieldMeanings.TotalTax, FieldMeanings.TotalGross]);
        var text = await StrictOrderFixture.Render("ubl-2-1-order", read);

        UblSchemaValidator.Validate(text).Valid.Should().BeTrue();
        var root = XDocument.Parse(text).Root!;
        root.Element(Cac + "Delivery")!.Elements().Select(e => e.Name.LocalName).Should().Equal("RequestedDeliveryPeriod");
        root.Elements().Select(e => e.Name.LocalName).Should().NotContain(["TaxTotal", "AnticipatedMonetaryTotal"]);
    }
}
