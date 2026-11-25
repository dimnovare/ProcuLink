using System.Globalization;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProcuLink.Transform.Output;

/// <summary>
/// One clean A4 PDF for any document: a purchase order or an invoice. It does not reproduce the
/// original layout. Text is real text (selectable, searchable). QuestPDF Community licence
/// (free under USD 1M annual revenue).
/// </summary>
public static class DocumentPdfOutput
{
    static DocumentPdfOutput() => QuestPDF.Settings.License = LicenseType.Community;

    public static byte[] Write(PurchaseOrderEntity doc)
    {
        var invoice = doc.DocumentType == "invoice";
        var (from, to) = invoice ? (doc.SupplierName, doc.BuyerName) : (doc.BuyerName, doc.SupplierName);
        var lines = doc.Lines.OrderBy(l => l.LineNumber).ToList();

        var header = new List<(string, string?)>
        {
            ("Reference", doc.PoNumber),
            ("Date", doc.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("Due date", invoice ? doc.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null),
            ("From", from), ("To", to), ("Currency", doc.Currency),
        };
        var totals = new (string, decimal?)[] { ("Net", doc.SubTotal), ("VAT", doc.TaxTotal), ("Total", doc.GrandTotal) };
        var payment = invoice ? new (string, string?)[] { ("IBAN", doc.PaymentIban), ("Payment reference", doc.PaymentReference) } : [];
        var rows = lines.Select(l => new[]
        {
            l.LineNumber.ToString(CultureInfo.InvariantCulture), (invoice ? l.BuyerItemCode : l.SupplierItemCode) ?? "",
            l.Description ?? "", l.Quantity.ToString(CultureInfo.InvariantCulture), l.Unit ?? "",
            l.UnitPrice.ToString("0.00##", CultureInfo.InvariantCulture), l.TaxRate?.ToString(CultureInfo.InvariantCulture) ?? "",
            Money(l.NetAmount ?? l.LineAmount ?? l.Quantity * l.UnitPrice),
        });
        return Render(!invoice ? "Purchase order" : doc.GrandTotal < 0 ? "Credit note" : "Invoice", header, rows, totals, doc.Currency, payment);
    }

    /// <summary>
    /// An invoice model's own page as PDF/A-3B, so a Factur-X / ZUGFeRD file can be attached to it. Drawn from the values the CII writer
    /// wrote (a credit note already made positive), so page and XML agree; the title comes from the standard, never from the sign.
    /// </summary>
    internal static byte[] WriteInvoice(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines, PeppolPartyOptions parties, bool creditNote)
    {
        static string? Day(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        static string Join(string separator, params string?[] parts) => string.Join(separator, parts.Where(x => !string.IsNullOrWhiteSpace(x)));
        static string Address(string? street, string? postcode, string? city, string? country) => Join(", ", street, Join(" ", postcode, city), country);
        var title = creditNote ? "Credit note" : "Invoice";
        var header = new List<(string, string?)>
        {
            ("Invoice number", invoice.InvoiceNumber), ("Issue date", Day(invoice.IssueDate)), ("Due date", Day(invoice.DueDate)), ("Delivery date", Day(parties.DeliveryDate)),
            ("Seller", parties.SellerName), ("Seller address", Address(parties.SellerStreet, parties.SellerPostcode, parties.SellerCity, parties.SellerCountry)),
            ("Seller VAT ID", parties.SellerVatId),
            ("Buyer", parties.BuyerName), ("Buyer address", Address(parties.BuyerStreet, parties.BuyerPostcode, parties.BuyerCity, parties.BuyerCountry)),
            ("Buyer VAT ID", parties.BuyerVatId), ("Buyer reference", invoice.BuyerRef), ("Order reference", parties.OrderReference), ("Currency", invoice.Currency),
        };
        var rows = lines.OrderBy(l => l.LineNumber).Select(l => new[]
        {
            l.LineNumber.ToString(CultureInfo.InvariantCulture), l.SupplierItemCode ?? l.BuyerItemCode ?? "", l.Description, l.Quantity.ToString(CultureInfo.InvariantCulture),
            l.UnitCode, l.UnitPrice.ToString("0.00##", CultureInfo.InvariantCulture), l.TaxRate.ToString(CultureInfo.InvariantCulture), Money(l.LineTotal),
        });
        var totals = new (string, decimal?)[] { ("Net", invoice.SubTotal), ("VAT", invoice.TaxTotal), ("Total", invoice.GrandTotal) };
        var payment = new (string, string?)[] { ("IBAN", parties.PayeeIban), ("Payment reference", parties.PaymentReference) };
        return Render(title, header, rows, totals, invoice.Currency, payment,
            new DocumentSettings { PDFA_Conformance = PDFA_Conformance.PDFA_3B }, new DocumentMetadata { Title = $"{title} {invoice.InvoiceNumber}" });
    }

    private static byte[] Render(string title, IEnumerable<(string, string?)> header, IEnumerable<string[]> rows, IEnumerable<(string, decimal?)> totals,
        string currency, IEnumerable<(string, string?)> payment, DocumentSettings? settings = null, DocumentMetadata? metadata = null)
    {
        var document = Document.Create(c => c.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.Margin(36);
            p.DefaultTextStyle(t => t.FontSize(9));
            p.Header().PaddingBottom(8).Text(title).FontSize(20).SemiBold();
            p.Content().Column(col =>
            {
                col.Spacing(10);
                col.Item().Column(h =>
                {
                    foreach (var (label, value) in header.Where(x => !string.IsNullOrWhiteSpace(x.Item2)))
                        h.Item().Text(t => { t.Span(label + ": ").SemiBold(); t.Span(value); });
                });
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(d =>
                    {
                        d.ConstantColumn(24); d.ConstantColumn(70); d.RelativeColumn(); d.ConstantColumn(36);
                        d.ConstantColumn(30); d.ConstantColumn(48); d.ConstantColumn(34); d.ConstantColumn(52);
                    });
                    t.Header(h =>
                    {
                        foreach (var name in new[] { "No.", "Item code", "Description", "Qty", "Unit", "Unit price", "VAT %", "Net" })
                            h.Cell().BorderBottom(1).PaddingVertical(3).Text(name).SemiBold();
                    });
                    foreach (var row in rows)
                        foreach (var cell in row)
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).Text(cell);
                });
                col.Item().AlignRight().Column(tc =>
                {
                    foreach (var (label, value) in totals.Where(x => x.Item2 is not null))
                        tc.Item().Text(t => { t.Span(label + ": ").SemiBold(); t.Span($"{Money(value)} {currency}"); });
                });
                foreach (var (label, value) in payment.Where(x => !string.IsNullOrWhiteSpace(x.Item2)))
                    col.Item().Text(t => { t.Span(label + ": ").SemiBold(); t.Span(value); });
            });
            p.Footer().AlignCenter().Text(t =>
            {
                t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages();
            });
        }));
        if (settings is not null) document = document.WithSettings(settings);
        if (metadata is not null) document = document.WithMetadata(metadata);
        return document.GeneratePdf();
    }

    private static string Money(decimal? v) => v?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
}
