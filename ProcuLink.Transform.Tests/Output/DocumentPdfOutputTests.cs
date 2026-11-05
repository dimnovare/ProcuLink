using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;
using UglyToad.PdfPig;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

public class DocumentPdfOutputTests
{
    private static PurchaseOrderEntity Doc(string type, string supplierCode = "S-1") => new()
    {
        PoNumber = "REF-7731", OrderDate = new DateOnly(2026, 11, 1), DueDate = new DateOnly(2026, 12, 1),
        SupplierName = "Example Supplies GmbH", BuyerName = "Accounting system", Currency = "EUR",
        SubTotal = 30m, TaxTotal = 5.7m, GrandTotal = 35.7m, DocumentType = type,
        PaymentIban = "DE89370400440532013000", PaymentReference = "RF-1",
        Lines =
        {
            new PurchaseOrderLineEntity { LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = supplierCode, Description = "Widget", Quantity = 2m, Unit = "EA", UnitPrice = 10m, TaxRate = 19m },
            new PurchaseOrderLineEntity { LineNumber = 2, BuyerItemCode = "B-2", SupplierItemCode = "S-2", Description = "Gadget", Quantity = 1m, Unit = "EA", UnitPrice = 10m, TaxRate = 19m },
        },
    };

    private static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join(" ", doc.GetPages().Select(p => p.Text));
    }

    [Fact]
    public void Pdf_IsAReadablePdfCarryingTheDocumentText()
    {
        var text = Text(DocumentPdfOutput.Write(Doc("invoice")));

        Assert.Contains("Invoice", text);
        Assert.Contains("REF-7731", text);
        Assert.Contains("DE89370400440532013000", text);
        Assert.Contains("35.70", text);
    }

    [Fact]
    public void Pdf_OrderUsesSupplierItemCode()
    {
        var text = Text(DocumentPdfOutput.Write(Doc("purchase_order")));

        Assert.Contains("S-1", text);
        Assert.Contains("Purchase order", text);
        Assert.DoesNotContain("DE89370400440532013000", text);
    }
}
