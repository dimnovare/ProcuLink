using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="IOrderMappingOverrideService"/>. The override is stored
/// under the <c>"mappingOverride"</c> key inside the order's existing <c>canonical_json</c> jsonb
/// (no new table). Writes go through <see cref="CanonicalJsonMerge.SetRawValue"/> so the override
/// sub-document never clobbers sibling keys (buyerName / enrichment provenance). Every query is
/// org-scoped — a cross-tenant order id reads as null / writes as false.
/// </summary>
public sealed class OrderMappingOverrideService : IOrderMappingOverrideService
{
    /// <summary>
    /// Serializer options for WRITING the override sub-document. CamelCase matches the rest of
    /// canonical_json and the frontend contract; nulls are omitted to keep the document tight.
    /// Reads go through <see cref="OrderMappingOverrideReader"/> so decode is identical everywhere.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // OutputNode tree (Phase B): enum node types round-trip as camelCase strings ("object"/"array"/
        // "field"/"attribute") to match the frontend wire contract, not as opaque numbers.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ProcuLinkDbContext _db;

    public OrderMappingOverrideService(ProcuLinkDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<OrderMappingOverride?> GetAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        // Project only canonical_json — org-scoped — so a cross-tenant id simply yields no row.
        var canonical = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(x => x.Id == orderId && x.OrgId == orgId)
            .Select(x => x.CanonicalJson)
            .FirstOrDefaultAsync(ct);

        return OrderMappingOverrideReader.Read(canonical);
    }

    /// <inheritdoc/>
    public async Task<bool> UpsertAsync(
        Guid orgId, Guid orderId, OrderMappingOverride @override, CancellationToken ct)
    {
        // Tracking load — we mutate canonical_json on the entity and SaveChanges.
        var entity = await _db.PurchaseOrders
            .Where(x => x.Id == orderId && x.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        if (entity is null) return false;

        var rawJson = JsonSerializer.Serialize(@override, SerializerOptions);

        // MV-1: did the override CONTENT actually change? The previously-stored override text is
        // re-serialised through the SAME SetRawValue(parse → WriteTo) path that produced `rawJson`,
        // so an ordinal compare is a faithful "changed?" signal (and an UNCHANGED re-save must never
        // trigger a needless re-transform).
        var previousRaw = OrderMappingOverrideReader.ReadRawJson(entity.CanonicalJson);
        var contentChanged = !string.Equals(previousRaw, rawJson, StringComparison.Ordinal);

        // Merge the override under its key, preserving every other canonical_json property verbatim.
        entity.CanonicalJson = CanonicalJsonMerge.SetRawValue(
            entity.CanonicalJson, OrderMappingOverrideReader.CanonicalKey, rawJson);
        entity.UpdatedAt = DateTime.UtcNow;

        // MV-1 — never ship a stale artifact after a mapping edit. If the order has already been
        // transformed (or is mid-transform / delivered) AND the override content changed, the
        // existing artifact no longer reflects what the user designed. Reset the order to 'ready' so
        // the next Send RE-TRANSFORMS (producing a fresh artifact) instead of redelivering the stale
        // one. We do NOT delete the old artifact — audit history is preserved; the re-transform makes
        // a new one that Redeliver/Send then ships. Both mutations land in ONE SaveChangesAsync on the
        // same tracked entity (a single atomic UPDATE), so the override write and the status reset can
        // never half-apply. An UNCHANGED upsert, or an order still upstream of transform
        // (pending_review/ready/etc.), leaves the status untouched — no needless re-transform.
        if (contentChanged && IsPastReady(entity.Status))
            entity.Status = OrderStatusConstants.Ready;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// True for the post-transform states whose persisted artifact a mapping edit would make stale (the
    /// order has already been transformed at least once). <see cref="OrderStatusMachine"/> documents
    /// <c>ready_to_deliver/transforming/delivered → ready</c> as the MV-1 re-transform path. The two
    /// delivery-failure states (<c>delivery_failed</c>, <c>delivery_dead_letter</c>) are included too:
    /// both are post-artifact and re-dispatchable/rescuable — Retry/Redeliver and the ops requeue ship
    /// the LATEST STORED artifact WITHOUT re-transforming, so a mapping edit after either state would
    /// otherwise deliver the pre-edit content invisibly (the MV-1 sibling bug). <c>rejected_by_supplier</c>
    /// is deliberately excluded: it has no outgoing transitions (it never re-dispatches), so its artifact
    /// can never be re-shipped — resetting it would be dead weight, not a fix.
    /// </summary>
    private static bool IsPastReady(string status) =>
        status is OrderStatusConstants.ReadyToDeliver
               or OrderStatusConstants.Transforming
               or OrderStatusConstants.Delivered
               or OrderStatusConstants.DeliveryFailed
               or OrderStatusConstants.DeliveryDeadLetter;
}
