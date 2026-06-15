using System.Text;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a canonical JSON payload from a fully-resolved order.
/// Suitable for delivery to REST-webhook suppliers.
/// </summary>
public sealed class JsonTransformService : ITransformService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public bool CanTransform(OutputFormat format) => format == OutputFormat.Json;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct,
        CxmlCredentialConfig? cxmlCredentials = null) // not used: JSON has no cXML Header
    {
        ValidateOrder(order);

        var lines = order.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new
            {
                l.LineNumber,
                supplierItemCode = l.SupplierItemCode ?? string.Empty,
                buyerItemCode    = l.BuyerItemCode    ?? string.Empty,
                description      = l.Description      ?? string.Empty,
                quantity         = l.Quantity,
                unit             = l.Unit             ?? string.Empty,
                unitPrice        = l.UnitPrice,
                lineTotal        = l.Quantity * l.UnitPrice,
            })
            .ToList();

        var totalValue = lines.Sum(l => l.lineTotal);

        var buyerName = ExtractBuyerName(order);

        var payload = new
        {
            poNumber     = order.PoNumber    ?? string.Empty,
            orderDate    = order.OrderDate.ToString("yyyy-MM-dd"),
            currency     = order.Currency,
            buyerName,
            supplierName = order.Supplier?.Name ?? string.Empty,
            lines,
            totalValue,
            generatedAt  = DateTime.UtcNow.ToString("O"),
        };

        var json  = JsonSerializer.Serialize(payload, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        return Task.FromResult(new TransformResult(
            Content:       new MemoryStream(bytes),
            ContentType:   "application/json",
            FileExtension: ".json"
        ));
    }

    /// <summary>
    /// Resolve the buyer name, reading the denormalised <see cref="PurchaseOrderEntity.BuyerName"/>
    /// COLUMN first and only falling back to <c>CanonicalJson</c>. The async parse path updates the
    /// column but not CanonicalJson, so reading CanonicalJson alone delivered an empty buyer for
    /// correctly-extracted orders (prod 14c340a1, 2026-06-13). Mirrors
    /// <c>MappedTransformService.ExtractBuyerName</c> / <c>ScribanOrderModel.ExtractBuyerName</c>.
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
}
