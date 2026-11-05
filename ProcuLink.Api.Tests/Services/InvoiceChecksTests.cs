using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public sealed class InvoiceChecksTests
{
    private static PurchaseOrderEntity Invoice(decimal line1, decimal line2, decimal? net, decimal? tax, decimal? total) => new()
    {
        PoNumberNormalized = "INV-1", SupplierName = "Example Supplies GmbH",
        SubTotal = net, TaxTotal = tax, GrandTotal = total, PaymentIban = "DE89370400440532013000",
        Lines =
        {
            new PurchaseOrderLineEntity { LineNumber = 1, Quantity = 1m, UnitPrice = line1, LineAmount = line1 },
            new PurchaseOrderLineEntity { LineNumber = 2, Quantity = 2m, UnitPrice = line2 / 2m },
        },
    };

    /// <summary>A balanced credit note at zero VAT: signs are not checked, and 0 VAT is a value read.</summary>
    [Fact]
    public void BalancedCreditNote_AtZeroVat_HasNoProblems() =>
        InvoiceChecks.Run(Invoice(-60m, -40m, -100m, 0m, -100m)).Should().BeEmpty();

    [Theory]
    [InlineData("lines", "Line amounts do not add up to the net total.")]
    [InlineData("iban", "The bank account (IBAN) is not valid.")]
    [InlineData("number", "Invoice number not found on the invoice.")]
    [InlineData("date", "Invoice date not found on the invoice.")]
    [InlineData("seller", "Seller not found on the invoice.")]
    [InlineData("net", "Net amount not found on the invoice.")]
    [InlineData("vat", "VAT amount not found on the invoice.")]
    public void OneFault_ReportsExactlyIt(string fault, string problem)
    {
        var doc = Invoice(fault == "lines" ? 50m : 60m, 40m, 100m, 21m, 121m);
        if (fault == "iban") doc.PaymentIban = "DE89370400440532013001";
        if (fault == "number") doc.PoNumberNormalized = null;
        if (fault == "seller") doc.SupplierName = " ";
        if (fault == "net") doc.SubTotal = null;
        if (fault == "vat") doc.TaxTotal = null;

        InvoiceChecks.Run(doc, dateRead: fault != "date").Should().Equal(problem);
    }
}
