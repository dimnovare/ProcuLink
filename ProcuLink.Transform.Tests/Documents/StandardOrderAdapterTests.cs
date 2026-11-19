using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StandardOrderAdapterTests
{
    [Fact]
    public void A_purchase_order_read_fills_every_column_the_writers_read()
    {
        var (order, missing) = StandardOrderAdapter.ToOrder(StrictOrderFixture.Read(), FlowFieldList.Empty, "lines",
            StandardOutputs.Find("ubl-2-1-order")!.Needs, Guid.Empty);

        missing.Should().BeEmpty();
        order!.RequestedDeliveryDate.Should().Be(new DateOnly(2026, 12, 15));
        (order.ShipToName, order.ShipToStreet, order.ShipToPostalCode, order.ShipToCity, order.ShipToCountry)
            .Should().Be(("Example Buyer Ltd Warehouse", "Warehouse Road 3", "20095", "Hamburg", "Germany"));
        (order.BillToName, order.BillToStreet, order.BillToPostalCode, order.BillToCity, order.BillToCountry)
            .Should().Be(("Example Buyer Ltd", "Example Street 1", "10115", "Berlin", "Germany"));
        (order.ContactEmail, order.BuyerTaxId, order.SellerTaxId).Should().Be(("orders@example.com", "DE000000000", "EE000000000"));
        (order.SubTotal, order.TaxTotal, order.GrandTotal).Should().Be((60.00m, 11.40m, 71.40m));
        var supplier = OrderHeaderReader.SupplierParty(order)!;
        (supplier.Name, supplier.Street, supplier.PostalCode, supplier.City, supplier.Country, supplier.Vat)
            .Should().Be(("Example Supplier OU", "Example Road 2", "10111", "Tallinn", "Estonia", "EE000000000"));
    }

    [Fact]
    public void Every_read_order_value_reaches_the_order()
    {
        var (order, _) = StandardOrderAdapter.ToOrder(StrictOrderFixture.WithEveryValue(), FlowFieldList.Empty, "lines", [], Guid.Empty);

        (order!.BuyerOrderRef, order.PaymentTerms).Should().Be(("FRAME-2026-01", "30 days net"));
        order.Lines.Select(l => (l.BuyerItemCode, l.LineAmount, l.TaxRate))
            .Should().Equal(("BUY-1", 25.00m, 19m), ("BUY-2", 10.00m, 19m), ("BUY-3", 25.00m, 19m));
        OrderHeaderReader.BuyerParty(order)!.EdiCode.Should().Be("0191:12345678");
        OrderHeaderReader.SupplierParty(order)!.EdiCode.Should().Be("0088:0000000000000");
    }
}
