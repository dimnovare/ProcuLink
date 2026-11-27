using System.Globalization;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Documents;
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
        return Render(!invoice ? "Purchase order" : doc.GrandTotal < 0 ? "Credit note" : "Invoice", header, rows, totals, doc.Currency, payment, DocumentLanguage.English);
    }

    /// <summary>
    /// An invoice model's own page as PDF/A-3B, so a Factur-X / ZUGFeRD file can be attached to it. Drawn from the values the CII writer
    /// wrote (a credit note already made positive), so page and XML agree; the title comes from the standard, never from the sign.
    /// Its words, numbers and dates are in the seller's language (<see cref="DocumentLanguage"/>); the values themselves are the XML's.
    /// </summary>
    internal static byte[] WriteInvoice(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines, PeppolPartyOptions parties, bool creditNote)
    {
        var lang = DocumentLanguage.FromCountry(parties.SellerCountry);
        string L(string english) => DocumentLanguage.Text(lang, english);
        string? Day(DateOnly? d) => DocumentLanguage.Date(lang, d);
        static string Join(string separator, params string?[] parts) => string.Join(separator, parts.Where(x => !string.IsNullOrWhiteSpace(x)));
        static string Address(string? street, string? postcode, string? city, string? country) => Join(", ", street, Join(" ", postcode, city), country);
        var title = L(creditNote ? "Credit note" : "Invoice");
        var header = new List<(string, string?)>
        {
            (L("Invoice number"), invoice.InvoiceNumber), (L("Issue date"), Day(invoice.IssueDate)), (L("Due date"), Day(invoice.DueDate)), (L("Delivery date"), Day(parties.DeliveryDate)),
            (L("Seller"), parties.SellerName), (L("Seller address"), Address(parties.SellerStreet, parties.SellerPostcode, parties.SellerCity, parties.SellerCountry)),
            (L("Seller VAT ID"), parties.SellerVatId),
            (L("Buyer"), parties.BuyerName), (L("Buyer address"), Address(parties.BuyerStreet, parties.BuyerPostcode, parties.BuyerCity, parties.BuyerCountry)),
            (L("Buyer VAT ID"), parties.BuyerVatId), (L("Buyer reference"), invoice.BuyerRef), (L("Order reference"), parties.OrderReference), (L("Currency"), invoice.Currency),
        };
        var rows = lines.OrderBy(l => l.LineNumber).Select(l => new[]
        {
            l.LineNumber.ToString(CultureInfo.InvariantCulture), l.SupplierItemCode ?? l.BuyerItemCode ?? "", l.Description, DocumentLanguage.Number(lang, l.Quantity, null),
            l.UnitCode, DocumentLanguage.Number(lang, l.UnitPrice, "0.00##"), DocumentLanguage.Number(lang, PeppolBisInvoiceTransformService.NormalizePercent(l.TaxRate), "0.##"),
            DocumentLanguage.Money(lang, l.LineTotal),
        });
        var totals = new (string, decimal?)[] { (L("Net"), invoice.SubTotal), (L("VAT"), invoice.TaxTotal), (L("Total"), invoice.GrandTotal) };
        var payment = new (string, string?)[] { ("IBAN", parties.PayeeIban), (L("Payment reference"), parties.PaymentReference) };
        return Render(title, header, rows, totals, invoice.Currency, payment, lang,
            new DocumentSettings { PDFA_Conformance = PDFA_Conformance.PDFA_3B }, new DocumentMetadata { Title = $"{title} {invoice.InvoiceNumber}" });
    }

    private static byte[] Render(string title, IEnumerable<(string, string?)> header, IEnumerable<string[]> rows, IEnumerable<(string, decimal?)> totals,
        string currency, IEnumerable<(string, string?)> payment, string lang, DocumentSettings? settings = null, DocumentMetadata? metadata = null)
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
                            h.Cell().BorderBottom(1).PaddingVertical(3).Text(DocumentLanguage.Text(lang, name)).SemiBold();
                    });
                    foreach (var row in rows)
                        foreach (var cell in row)
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).Text(cell);
                });
                col.Item().AlignRight().Column(tc =>
                {
                    foreach (var (label, value) in totals.Where(x => x.Item2 is not null))
                        tc.Item().Text(t => { t.Span(label + ": ").SemiBold(); t.Span($"{DocumentLanguage.Money(lang, value)} {currency}"); });
                });
                foreach (var (label, value) in payment.Where(x => !string.IsNullOrWhiteSpace(x.Item2)))
                    col.Item().Text(t => { t.Span(label + ": ").SemiBold(); t.Span(value); });
            });
            p.Footer().AlignCenter().Text(t => PageOf(t, lang));
        }));
        if (settings is not null) document = document.WithSettings(settings);
        if (metadata is not null) document = document.WithMetadata(metadata);
        return document.GeneratePdf();
    }

    /// <summary>"Page 1 of 2" in <paramref name="lang"/>: the footer of every page ProcuLink draws.</summary>
    internal static void PageOf(TextDescriptor t, string lang)
    {
        t.Span(DocumentLanguage.Text(lang, "Page") + " "); t.CurrentPageNumber(); t.Span(" " + DocumentLanguage.Text(lang, "of") + " "); t.TotalPages();
    }

    private static string Money(decimal? v) => v?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
}
