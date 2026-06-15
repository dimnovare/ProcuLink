using System.Globalization;
using System.Text;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a supplier-ready CSV from a fully-resolved order entity.
/// Columns: PoNumber, OrderDate, Currency, BuyerName, SupplierItemCode, Description, Quantity, Unit, UnitPrice, LineTotal
/// The PO header fields (PoNumber/OrderDate/Currency/BuyerName) are repeated on every
/// line row so the CSV is self-contained and lossless: a supplier can reconcile the
/// delivery to a PO number and currency without out-of-band context. This matches the
/// JSON and XML default transforms, which already carry the order header. (Before
/// 2026-06-14 the default CSV was line-items-only and dropped PO number + currency —
/// a losslessness gap caught in live-prod QA.)
/// Values containing commas or double-quotes are RFC 4180 escaped.
/// </summary>
public sealed class CsvTransformService : ITransformService
{
    public bool CanTransform(OutputFormat format) => format == OutputFormat.Csv;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct,
        CxmlCredentialConfig? cxmlCredentials = null) // not used: CSV has no cXML Header
    {
        ValidateOrder(order);

        var sb = new StringBuilder();
        sb.AppendLine("PoNumber,OrderDate,Currency,BuyerName,SupplierItemCode,Description,Quantity,Unit,UnitPrice,LineTotal");

        var poNumber  = Escape(order.PoNumber ?? string.Empty);
        var orderDate = order.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var currency  = Escape(order.Currency ?? string.Empty);
        var buyerName = Escape(ExtractBuyerName(order));

        foreach (var l in order.Lines.OrderBy(x => x.LineNumber))
        {
            var lineTotal = l.Quantity * l.UnitPrice;

            sb.AppendLine(string.Join(",",
                poNumber,
                orderDate,
                currency,
                buyerName,
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

    /// <summary>
    /// Resolve the buyer name, reading the denormalised <see cref="PurchaseOrderEntity.BuyerName"/>
    /// COLUMN first and only falling back to <c>CanonicalJson</c>. The async parse path updates the
    /// column but not CanonicalJson, so reading CanonicalJson alone delivered an empty buyer for
    /// correctly-extracted orders. Mirrors <c>JsonTransformService.ExtractBuyerName</c> /
    /// <c>MappedTransformService.ExtractBuyerName</c> / <c>ScribanOrderModel.ExtractBuyerName</c>
    /// (consolidating these four copies into one shared helper is a worthwhile follow-up).
    /// </summary>
    private static string ExtractBuyerName(PurchaseOrderEntity order)
    {
        if (!string.IsNullOrEmpty(order.BuyerName)) return order.BuyerName;
        if (order.CanonicalJson is null) return string.Empty;
        try
        {
            if (order.CanonicalJson.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (order.CanonicalJson.RootElement.TryGetProperty("buyerName", out var el))
                    return el.GetString() ?? string.Empty;
                if (order.CanonicalJson.RootElement.TryGetProperty("BuyerName", out var el2))
                    return el2.GetString() ?? string.Empty;
            }
        }
        catch { /* malformed JSON — ignore */ }
        return string.Empty;
    }

    /// <summary>RFC 4180: wrap in double-quotes if the value contains comma, quote, or newline.</summary>
    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
