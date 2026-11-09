using System.Buffers.Binary;
using ProcuLink.Core.Documents;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ProcuLink.Transform.Documents;

/// <summary>A <see cref="DocumentLayout"/> as one A4 PDF with real text: logo and title, fields, table, footer. Styling follows DocumentPdfOutput.</summary>
public static class LayoutPdfWriter
{
    static LayoutPdfWriter() => QuestPDF.Settings.License = LicenseType.Community;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];

    public static bool IsUsableLogo(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith(Png) && !bytes.AsSpan().StartsWith(Jpeg)) return false;
        if (Dimensions(bytes) is not { } d || d.Width > MaxLogoSide || d.Height > MaxLogoSide) return false;
        try { using var _ = Image.FromBinaryData(bytes); return true; }
        catch (Exception) { return false; }
    }

    private const int MaxLogoSide = 4096;

    // Pixel size from the header, so an oversized image is refused before it is decoded. Null when the header is unreadable.
    private static (uint Width, uint Height)? Dimensions(byte[] b)
    {
        if (b.AsSpan().StartsWith(Png))
            return b.Length < 24 ? null : (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20)));
        for (var i = 2; i + 9 <= b.Length;)
        {
            if (b[i] != 0xFF) return null;
            var marker = b[i + 1];
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                return (BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 7)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 5)));
            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 2));
        }
        return null;
    }

    public static byte[] Write(DocumentLayout layout, OutputLayout? settings, byte[]? logo)
    {
        // An undecodable logo is skipped rather than failing the whole output.
        if (logo is not null && !IsUsableLogo(logo)) logo = null;
        var wide = layout.Columns.Count > 8;

        return Document.Create(c => c.Page(p =>
        {
            p.Size(wide ? PageSizes.A4.Landscape() : PageSizes.A4);
            p.Margin(36);
            p.DefaultTextStyle(t => t.FontSize(9));
            p.Header().PaddingBottom(8).Row(r =>
            {
                if (logo is not null) r.ConstantItem(120).MaxHeight(40).Image(logo).FitArea();
                r.RelativeItem().PaddingLeft(logo is null ? 0 : 8).AlignMiddle().Text(settings?.Title ?? "").FontSize(18).SemiBold();
            });
            p.Content().Column(col =>
            {
                col.Spacing(10);
                if (!string.IsNullOrWhiteSpace(settings?.Header)) col.Item().Text(settings.Header);
                col.Item().Column(h =>
                {
                    foreach (var (label, value) in layout.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
                        h.Item().Text(t => { t.Span(label + ": ").SemiBold(); t.Span(value); });
                });
                if (layout.Columns.Count == 0 || layout.Rows.Count == 0) return;
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(d => { foreach (var _ in layout.Columns) d.RelativeColumn(); });
                    t.Header(h =>
                    {
                        foreach (var name in layout.Columns) h.Cell().BorderBottom(1).PaddingVertical(3).Text(name).SemiBold();
                    });
                    foreach (var row in layout.Rows)
                        for (var i = 0; i < layout.Columns.Count; i++)
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).Text(i < row.Count ? row[i] : "");
                });
            });
            p.Footer().Column(f =>
            {
                if (!string.IsNullOrWhiteSpace(settings?.Footer)) f.Item().AlignCenter().Text(settings.Footer);
                f.Item().AlignCenter().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
            });
        })).GeneratePdf();
    }
}
