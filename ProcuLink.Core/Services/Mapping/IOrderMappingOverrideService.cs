namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Reads and writes the per-order mapping/override layer (heart-piece-flex Phase 1).
/// The override is persisted INSIDE the existing <c>purchase_orders.canonical_json</c> jsonb
/// under the key <c>"mappingOverride"</c> — there is no new table. All operations are
/// strictly org-scoped (<c>Where(x =&gt; x.Id == orderId &amp;&amp; x.OrgId == orgId)</c>) so a
/// caller can never read or write another tenant's order.
/// </summary>
public interface IOrderMappingOverrideService
{
    /// <summary>
    /// Returns the order's stored <see cref="OrderMappingOverride"/>, or <c>null</c> when the order
    /// exists but has no override, when the canonical_json is absent/malformed, or when the order
    /// does not belong to the org. Never throws for a missing key.
    /// </summary>
    Task<OrderMappingOverride?> GetAsync(Guid orgId, Guid orderId, CancellationToken ct);

    /// <summary>
    /// Upserts the override under the <c>"mappingOverride"</c> canonical_json key, merging it in
    /// without clobbering any other key (buyerName / enrichment provenance) via the shared
    /// canonical-json merge helper. Returns <c>false</c> when the order does not exist for this org
    /// (caller maps that to 404); <c>true</c> on a successful write.
    /// </summary>
    Task<bool> UpsertAsync(Guid orgId, Guid orderId, OrderMappingOverride @override, CancellationToken ct);
}
