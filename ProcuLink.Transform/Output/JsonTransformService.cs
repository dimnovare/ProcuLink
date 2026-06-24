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
        // B1: same price>0 / qty>0 output invariant as the fixed CSV/XML transforms — for a fixed
        // JSON transform the entity's canonical columns ARE the emitted bytes (override/template/
        // OutputNode paths emit elsewhere and are intentionally not guarded here).
        OutputFieldValidator.ValidateEntity(order, format);

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

        var buyerName = OrderHeaderReader.ExtractBuyerName(order);

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
