using System.Xml.Linq;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

public class VatBreakdownTests
{
    private static readonly PeppolPartyOptions Parties = new() { SellerName = "Seller Co", BuyerName = "Buyer Co", SellerCountry = "LV", BuyerCountry = "LV" };

    private static (XDocument Ubl, XDocument Cii) Write(decimal tax, params (decimal Net, decimal Rate)[] lines)
    {
        var inv = new InvoiceEntity { InvoiceNumber = "INV-1", IssueDate = new DateOnly(2026, 11, 1), Currency = "EUR", SubTotal = lines.Sum(l => l.Net), TaxTotal = tax, GrandTotal = lines.Sum(l => l.Net) + tax };
        InvoiceLineEntity[] rows = [.. lines.Select((l, i) => new InvoiceLineEntity { LineNumber = i + 1, Description = "Item", Quantity = 1, UnitPrice = l.Net, LineTotal = l.Net, TaxRate = l.Rate })];
        return (new PeppolBisInvoiceTransformService().BuildDocument(inv, rows, Parties, false), CiiInvoiceWriter.Build(inv, rows, Parties, false));
    }

    private static IEnumerable<string> Leaves(XDocument d, string parent) =>
        d.Descendants().Where(e => e.Name.LocalName == parent && e.Parent!.Name.LocalName is "TaxTotal" or "ApplicableHeaderTradeSettlement")
            .Select(e => string.Join(" ", e.Descendants().Where(x => !x.HasElements).Select(x => x.Value)));

    [Fact]
    public void Each_rate_is_its_own_breakdown_and_a_guess_is_refused()
    {
        // 0.21 is a fraction-style rate: it is normalised to 21 like the line it came from.
        var (ubl, cii) = Write(25.5m, (100m, 0.21m), (50m, 9m));
        Assert.Equal(["100.00 21.00 S 21.00 VAT", "50.00 4.50 S 9.00 VAT"], Leaves(ubl, "TaxSubtotal"));
        Assert.Equal(["21.00 VAT 100.00 S 21.00", "4.50 VAT 50.00 S 9.00"], Leaves(cii, "ApplicableTradeTax"));
        Assert.Contains("21.00", ubl.Descendants().Where(e => e.Name.LocalName == "ClassifiedTaxCategory").Select(e => e.Elements().ElementAt(1).Value));

        // No VAT: zero-rated, exempt and reverse charge are different categories, and nothing says which.
        Assert.Equal(VatBreakdown.NoVatReason, Assert.Throws<VatBreakdownException>(() => Write(0m, (100m, 0m))).Message);
        // Lines read without their own rate take the header's blend (26 / 150 = 17.33 %): no single VAT rate fits.
        Assert.Equal(VatBreakdown.MixedRatesReason, Assert.Throws<VatBreakdownException>(() => Write(26m, (100m, 17.33m), (50m, 17.33m))).Message);
    }
}
