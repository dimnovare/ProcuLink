using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Shared canonical-header reader for the output transforms. Consolidates the four byte-identical
/// <c>ExtractBuyerName</c> copies that lived in <see cref="CsvTransformService"/>,
/// <see cref="JsonTransformService"/>, <see cref="MappedTransformService"/>, and
/// <see cref="ScribanOrderModel"/> into one place.
///
/// <para><b>COLUMN-FIRST CONTRACT.</b> The denormalised typed column
/// (<see cref="PurchaseOrderEntity.BuyerName"/>) is the source of truth — it is always populated by
/// the async parse path (<c>ParseStoredFileAsync</c>), which deliberately does NOT write
/// <c>canonical_json</c>. <c>canonical_json</c> is consulted ONLY as a legacy fallback for orders
/// created via the sync (non-file) ingress path. Reading <c>canonical_json</c> first would deliver an
/// empty buyer for every async-parsed order (prod incident 14c340a1, 2026-06-13).</para>
///
/// <para>Behaviour is byte-identical to the four copies it replaces, with one addition: malformed
/// <c>canonical_json</c> is now LOGGED (then ignored, returning empty) rather than swallowed silently,
/// so a corrupt payload is observable. Logging is a no-op until the composition root wires
/// <see cref="TransformDiagnostics.LoggerFactory"/>.</para>
/// </summary>
internal static class OrderHeaderReader
{
    /// <summary>
    /// Resolve the buyer name: the denormalised <see cref="PurchaseOrderEntity.BuyerName"/> column
    /// first, then the <c>buyerName</c> / <c>BuyerName</c> property in <c>CanonicalJson</c>. Returns
    /// <see cref="string.Empty"/> (never null) when neither is present or the JSON is malformed.
    /// </summary>
    public static string ExtractBuyerName(PurchaseOrderEntity order)
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
        catch (Exception ex)
        {
            // Malformed CanonicalJson — keep the original fail-safe (return empty), but log it so a
            // corrupt payload is observable instead of silently producing a blank buyer name.
            TransformDiagnostics.CreateLogger(nameof(OrderHeaderReader)).LogWarning(
                ex,
                "Order {OrderId} (PO {PoNumber}): CanonicalJson is malformed while reading BuyerName; " +
                "treating buyer name as empty.",
                order.Id, order.PoNumber);
        }
        return string.Empty;
    }
}
