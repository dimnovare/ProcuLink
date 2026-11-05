using System.Globalization;
using System.Text;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Default CSV / JSON bytes for an invoice (<c>DocumentType == "invoice"</c>) that has no mapping
/// override or promoted mapping. Header fields are repeated on every CSV row, like the order CSV.
/// </summary>
public static class InvoiceDefaultOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The refusal for an invoice whose output format is not CSV, JSON or PDF.</summary>
    public const string FormatError = "Invoices go out as CSV, JSON or PDF. Change the output format for where invoices go.";

    private const string CsvHeader =
        "InvoiceNumber,InvoiceDate,DueDate,Seller,SellerVatId,Buyer,Currency,NetTotal,VatTotal,Total,Iban,PaymentReference," +
        "LineNumber,ItemCode,Description,Quantity,Unit,UnitPrice,VatRate,LineNet";

    public static (byte[] Content, string ContentType, string Extension) Write(PurchaseOrderEntity doc, string format)
    {
        if (format == "pdf")
        {
            var pdf = DeliveryMediaTypes.For(OutputFormat.Pdf);
            return (DocumentPdfOutput.Write(doc), pdf.ContentType, pdf.FileExtension);
        }
        var (output, text) = format switch
        {
            "csv"  => (OutputFormat.Csv,  Csv(doc)),
            "json" => (OutputFormat.Json, Json(doc)),
            _      => throw new NotSupportedException($"Invoices have no default '{format}' output."),
        };
        var media = DeliveryMediaTypes.For(output);
        return (Encoding.UTF8.GetBytes(text), media.ContentType, media.FileExtension);
    }

    private static decimal LineNet(PurchaseOrderLineEntity l) => l.NetAmount ?? l.LineAmount ?? l.Quantity * l.UnitPrice;

    private static string Num(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Csv(PurchaseOrderEntity d)
    {
        var header = new[]
        {
            Cell(d.PoNumber), d.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            d.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            Cell(d.SupplierName), Cell(d.SellerTaxId), Cell(d.BuyerName), Cell(d.Currency),
            Num(d.SubTotal), Num(d.TaxTotal), Num(d.GrandTotal), Cell(d.PaymentIban), Cell(d.PaymentReference),
        };

        var sb = new StringBuilder(CsvHeader).AppendLine();
        foreach (var l in d.Lines.OrderBy(x => x.LineNumber))
            sb.AppendLine(string.Join(",", header.Concat(new[]
            {
                l.LineNumber.ToString(CultureInfo.InvariantCulture), Cell(l.BuyerItemCode), Cell(l.Description),
                Num(l.Quantity), Cell(l.Unit), Num(l.UnitPrice), Num(l.TaxRate), Num(LineNet(l)),
            })));
        return sb.ToString();
    }

    /// <summary>A text cell: formula-neutralised and RFC 4180 escaped by the one shared escaper.</summary>
    private static string Cell(string? v) => CsvTransformService.Cell(v);

    private static string Json(PurchaseOrderEntity d) => JsonSerializer.Serialize(new
    {
        invoiceNumber = d.PoNumber,
        invoiceDate = d.OrderDate,
        dueDate = d.DueDate,
        seller = d.SupplierName,
        sellerVatId = d.SellerTaxId,
        buyer = d.BuyerName,
        currency = d.Currency,
        netTotal = d.SubTotal,
        vatTotal = d.TaxTotal,
        total = d.GrandTotal,
        iban = d.PaymentIban,
        paymentReference = d.PaymentReference,
        lines = d.Lines.OrderBy(l => l.LineNumber).Select(l => new
        {
            lineNumber = l.LineNumber,
            itemCode = l.BuyerItemCode,
            description = l.Description,
            quantity = l.Quantity,
            unit = l.Unit,
            unitPrice = l.UnitPrice,
            vatRate = l.TaxRate,
            lineNet = LineNet(l),
        }),
    }, JsonOptions);
}
