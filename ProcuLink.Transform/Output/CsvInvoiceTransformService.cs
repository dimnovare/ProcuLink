using System.Globalization;
using System.Text;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a two-section CSV from an invoice:
/// a header metadata block followed by a separator and line rows.
/// </summary>
public sealed class CsvInvoiceTransformService : IInvoiceTransformService
{
    public string Format => "csv";

    public Task<byte[]> TransformAsync(
        InvoiceEntity invoice,
        IReadOnlyList<InvoiceLineEntity> lines,
        CancellationToken ct)
    {
        var sb = new StringBuilder();

        // ── Header metadata ───────────────────────────────────────────────
        sb.AppendLine("Field,Value");
        sb.AppendLine($"InvoiceNumber,{Escape(invoice.InvoiceNumber)}");
        sb.AppendLine($"IssueDate,{invoice.IssueDate:yyyy-MM-dd}");
        sb.AppendLine($"DueDate,{invoice.DueDate?.ToString("yyyy-MM-dd") ?? ""}");
        sb.AppendLine($"Currency,{Escape(invoice.Currency)}");
        sb.AppendLine($"BuyerRef,{Escape(invoice.BuyerRef ?? "")}");
        sb.AppendLine($"SupplierRef,{Escape(invoice.SupplierRef ?? "")}");
        sb.AppendLine($"PaymentTerms,{Escape(invoice.PaymentTerms ?? "")}");
        sb.AppendLine($"SubTotal,{invoice.SubTotal.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"TaxTotal,{invoice.TaxTotal.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"GrandTotal,{invoice.GrandTotal.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();

        // ── Line items ────────────────────────────────────────────────────
        sb.AppendLine("LineNumber,BuyerItemCode,SupplierItemCode,Description,Quantity,UnitCode,UnitPrice,TaxRate,LineTotal");
        foreach (var l in lines.OrderBy(x => x.LineNumber))
        {
            sb.AppendLine(string.Join(",",
                l.LineNumber.ToString(CultureInfo.InvariantCulture),
                Escape(l.BuyerItemCode    ?? ""),
                Escape(l.SupplierItemCode ?? ""),
                Escape(l.Description),
                l.Quantity.ToString(CultureInfo.InvariantCulture),
                Escape(l.UnitCode),
                l.UnitPrice.ToString(CultureInfo.InvariantCulture),
                l.TaxRate.ToString(CultureInfo.InvariantCulture),
                l.LineTotal.ToString(CultureInfo.InvariantCulture)
            ));
        }

        return Task.FromResult(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string Escape(string v)
    {
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return $"\"{v.Replace("\"", "\"\"")}\"";
        return v;
    }
}
