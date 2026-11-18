using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Documents;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StrictOrderCxmlTests
{
    [Fact]
    public async Task The_order_request_has_what_the_dtd_requires_in_dtd_order()
    {
        var text = await StrictOrderFixture.Render("cxml-1-2");
        text.Should().Contain("<!DOCTYPE cXML SYSTEM \"http://xml.cxml.org/schemas/cXML/1.2.024/cXML.dtd\">");
        new ConformanceService().Check(text, OutputFormat.CXml).ErrorCount.Should().Be(0);
        var doc = XDocument.Parse(text);
        string Names(XElement e) => string.Join(",", e.Elements().Select(c => c.Name.LocalName));

        var to = doc.Root!.Element("Header")!.Element("To")!.Element("Credential")!;
        ((string)to.Attribute("domain")!, to.Element("Identity")!.Value).Should().Be(("NetworkId", StrictOrderFixture.SupplierVat));

        var header = doc.Descendants("OrderRequestHeader").Single();
        Names(header).Should().Be("Total,ShipTo,BillTo,Contact");   // DTD: Total, ShipTo?, BillTo, …, Contact*
        header.Element("Total")!.Element("Money")!.Value.Should().Be("60.00");
        var billTo = header.Element("BillTo")!.Element("Address")!;
        (billTo.Element("Name")!.Value, billTo.Element("PostalAddress")!.Element("Street")!.Value).Should().Be(("Example Buyer Ltd", "Example Street 1"));
        var country = header.Element("ShipTo")!.Descendants("Country").Single();
        ((string)country.Attribute("isoCountryCode")!, country.Value).Should().Be(("DE", "Germany"));
        header.Element("Contact")!.Element("Name")!.Value.Should().Be("Example Buyer Ltd");

        var item = doc.Descendants("ItemOut").First();
        ((string)item.Attribute("requestedDeliveryDate")!).Should().Be("2026-12-15T00:00:00");
        var detail = item.Element("ItemDetail")!;
        Names(detail).Should().Be("UnitPrice,Description,UnitOfMeasure,Classification");   // DTD: … UnitOfMeasure, PriceBasisQuantity?, Classification+
        (detail.Element("UnitOfMeasure")!.Value, (string)detail.Element("Classification")!.Attribute("domain")!).Should().Be(("H87", "UNSPSC"));
    }

    [Fact]
    public async Task An_incomplete_address_drops_only_its_postal_part_and_no_buyer_is_refused()
    {
        var text = await StrictOrderFixture.Render("cxml-1-2", StrictOrderFixture.Read(without: [FieldMeanings.DeliveryStreet]));
        var shipTo = XDocument.Parse(text).Descendants("ShipTo").Single().Element("Address")!;
        (shipTo.Element("Name")!.Value, shipTo.Element("PostalAddress")).Should().Be(("Example Buyer Ltd Warehouse", (XElement?)null));

        var refused = await StandardOutputs.RenderAsync("cxml-1-2", StrictOrderFixture.Read(without: [FieldMeanings.SenderName]),
            FlowFieldList.Empty, "lines", Guid.Empty, CancellationToken.None);
        (refused.Error, refused.Detail).Should().Be(("output_invalid", "This standard needs Sender name."));
    }
}
