using System.Globalization;
using System.Text;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a supplier-ready CSV from a fully-resolved order entity.
/// Columns: SupplierItemCode, Description, Quantity, Unit, UnitPrice, LineTotal
/// Values containing commas or double-quotes are RFC 4180 escaped.
/// </summary>
public sealed class CsvTransformService : ITransformService
{
    public bool CanTransform(OutputFormat format) => format == OutputFormat.Csv;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct)
    {
        ValidateOrder(order);

        var sb = new StringBuilder();
        sb.AppendLine("SupplierItemCode,Description,Quantity,Unit,UnitPrice,LineTotal");

        foreach (var l in order.Lines.OrderBy(x => x.LineNumber))
        {
            var lineTotal = l.Quantity * l.UnitPrice;

            sb.AppendLine(string.Join(",",
                Escape(l.SupplierItemCode ?? string.Empty),
                Escape(l.Description      ?? string.Empty),
                l.Quantity.ToString(CultureInfo.InvariantCulture),
                Escape(l.Unit             ?? string.Empty),
                l.UnitPrice.ToString(CultureInfo.InvariantCulture),
                lineTotal.ToString(CultureInfo.InvariantCulture)
            ));
        }

        var bytes  = Encoding.UTF8.GetBytes(sb.ToString());
        var stream = new MemoryStream(bytes);

        return Task.FromResult(new TransformResult(
            Content:       stream,
            ContentType:   "text/csv",
            FileExtension: ".csv"
        ));
    }

    private static void ValidateOrder(PurchaseOrderEntity order)
    {
        var unresolved = order.Lines
            .Where(l => l.NeedsReview || string.IsNullOrWhiteSpace(l.SupplierItemCode))
            .Select(l => l.LineNumber)
            .OrderBy(n => n)
            .ToList();

        if (unresolved.Count > 0)
            throw new TransformValidationException(unresolved);
    }

    /// <summary>RFC 4180: wrap in double-quotes if the value contains comma, quote, or newline.</summary>
    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
