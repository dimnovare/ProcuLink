using System.Text;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

public class InvoiceDefaultOutputTests
{
    private static PurchaseOrderEntity Invoice(string description = "Widget") => new()
    {
        PoNumber = "INV-1", OrderDate = new DateOnly(2026, 11, 1), DueDate = new DateOnly(2026, 12, 1),
        SupplierName = "Example Supplies GmbH", SellerTaxId = "DE123456789", BuyerName = "Accounting system",
        Currency = "EUR", SubTotal = 30m, TaxTotal = 5.7m, GrandTotal = 35.7m,
        PaymentIban = "DE89370400440532013000", PaymentReference = "RF-1", DocumentType = "invoice",
        Lines =
        {
            new PurchaseOrderLineEntity { LineNumber = 1, BuyerItemCode = "A-1", Description = description, Quantity = 2m, Unit = "EA", UnitPrice = 10m, TaxRate = 19m },
            new PurchaseOrderLineEntity { LineNumber = 2, BuyerItemCode = "B-2", Description = "Gadget", Quantity = 1m, Unit = "EA", UnitPrice = 10m, TaxRate = 19m },
        },
    };

    private static string[] CsvLines(PurchaseOrderEntity d) =>
        Encoding.UTF8.GetString(InvoiceDefaultOutput.Write(d, "csv").Content).Split('\n',StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Csv_HasHeaderAndOneRowPerLine()
    {
        var lines = CsvLines(Invoice());

        Assert.Equal(
            "InvoiceNumber,InvoiceDate,DueDate,Seller,SellerVatId,Buyer,Currency,NetTotal,VatTotal,Total,Iban,PaymentReference,LineNumber,ItemCode,Description,Quantity,Unit,UnitPrice,VatRate,LineNet",
            lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("INV-1,2026-11-01,2026-12-01,Example Supplies GmbH,DE123456789,Accounting system,EUR,30,5.7,35.7,DE89370400440532013000,RF-1,1,A-1,Widget,2,EA,10,19,20", lines[1]);
    }

    [Fact]
    public void Csv_NeutralisesFormulaPrefix()
    {
        var lines = CsvLines(Invoice("=HYPERLINK(\"x\")"));

        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", lines[1]);
    }

    [Fact]
    public void Json_CarriesHeaderAndLines()
    {
        var root = JsonDocument.Parse(InvoiceDefaultOutput.Write(Invoice(), "json").Content).RootElement;

        Assert.Equal("INV-1", root.GetProperty("invoiceNumber").GetString());
        Assert.Equal("DE89370400440532013000", root.GetProperty("iban").GetString());
        Assert.Equal("2026-12-01", root.GetProperty("dueDate").GetString());
        Assert.Equal(2, root.GetProperty("lines").GetArrayLength());
        Assert.Equal(20m, root.GetProperty("lines")[0].GetProperty("lineNet").GetDecimal());
    }
}
