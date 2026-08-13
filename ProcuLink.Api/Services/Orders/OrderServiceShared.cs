using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Catalog;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// Shared helpers extracted verbatim from the original <c>OrderService</c> during the
/// internal-facade decomposition (audit W1/B1). Behaviour is unchanged — these methods
/// were lifted exactly as they were, only their host type moved.
///
/// Instance members (<see cref="SafeReconcileExceptionsAsync"/>, <see cref="EmitPassportEventAsync"/>)
/// need the scoped <see cref="ProcuLinkDbContext"/> / services, so this is constructed once
/// in the <c>OrderService</c> ctor and passed to the sub-services that use them.
/// Static members (<see cref="BuildAuditEvent"/>, <see cref="ApplyExtractionReviewFlags"/>)
/// are pure.
/// </summary>
internal sealed class OrderServiceShared
{
    private readonly ProcuLinkDbContext     _db;
    private readonly IOrderExceptionService _exceptions;
    private readonly ILogger                _logger;

    public OrderServiceShared(
        ProcuLinkDbContext     db,
        IOrderExceptionService exceptions,
        ILogger                logger)
    {
        _db         = db;
        _exceptions = exceptions;
        _logger     = logger;
    }

    /// <summary>
    /// Best-effort exception reconciliation: exception generation is operational
    /// observability data and must never fail the parent order operation.
    /// </summary>
    public async Task SafeReconcileExceptionsAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        try
        {
            await _exceptions.ReconcileAsync(orgId, orderId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception reconcile failed for order {OrderId} (non-fatal).", orderId);
        }
    }

    public async Task EmitPassportEventAsync(
        Guid orgId, Guid orderId,
        string stage, string eventType,
        string actorType = "system", string? actorId = null,
        object? payload = null,
        CancellationToken ct = default)
    {
        _db.PoPassportEvents.Add(new PoPassportEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            OrderId    = orderId,
            Stage      = stage,
            EventType  = eventType,
            ActorType  = actorType,
            ActorId    = actorId,
            Payload    = payload is null ? null : JsonSerializer.Serialize(payload),
            OccurredAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    public static AuditEvent BuildAuditEvent(Guid orgId, Guid entityId, string action, object payload) =>
        new()
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            EntityType = "Order",
            EntityId   = entityId,
            Action     = action,
            Payload    = JsonDocument.Parse(JsonSerializer.Serialize(payload)),
            CreatedAt  = DateTime.UtcNow
        };

    /// <summary>
    /// Phase 2: batch-load one supplier's ACTIVE catalog ONCE, keyed for the
    /// <c>{{ catalog.* }}</c> Scriban accessor / <c>LoadCatalogProduct</c> manipulator and the
    /// connection price-variance guard. Keyed by <c>Code</c>, <c>Barcode</c>,
    /// <c>ManufacturerPartNumber</c> (raw AND normalised), and <c>ExternalId</c>, so a line
    /// resolves by supplier item code or manufacturer part number without an N+1.
    /// ALWAYS org+supplier scoped — never cross-tenant.
    /// Shared by <see cref="OrderTransformService"/> (template catalog accessor) and
    /// <see cref="OrderResolutionService"/> (variance guard) so the query lives in exactly one place.
    ///
    /// <c>ExternalId</c> stays a key for BACK-COMPAT only: before there was a manufacturer-part
    /// column, the import alias "manufacturer part id" landed in <c>external_id</c>, so catalogs
    /// imported before this change hold their manufacturer part numbers there. It is keyed LAST so
    /// a real manufacturer part number always wins over a stale one parked in external_id.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, SupplierProduct>> BuildCatalogLookupAsync(
        ProcuLinkDbContext db, Guid organisationId, Guid supplierId, CancellationToken ct)
    {
        // ORDERED, because the dictionary below is case-INSENSITIVE while the unique index on
        // (org_id, supplier_id, code) is case-SENSITIVE: a catalog may legally hold "AB-1" and
        // "ab-1" as two rows, and `TryAdd` is first-wins. Without an ORDER BY the winner was
        // whatever order Postgres happened to return, so the same order could resolve to a
        // different product between two runs — the same class of defect as two resolvers
        // disagreeing, one level down. Id is the final tie-break so the answer never depends on
        // physical row order. (The same applies to a Barcode/MPN/ExternalId on one row colliding
        // with a Code on another.)
        var products = await db.SupplierProducts.AsNoTracking()
            .Where(p => p.OrgId == organisationId && p.SupplierId == supplierId && p.IsActive)
            .OrderBy(p => p.Code)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);

        // WP-14: the comparer is named ONCE, in ItemCodeComparison, and referenced from both here
        // and ItemMappingService. Before that, this dictionary was OrdinalIgnoreCase while the
        // learned-mapping resolver used an ordinal `==` — so the same code resolved through the
        // catalog and not through the mapping. Editing one file can no longer split them.
        var dict = new Dictionary<string, SupplierProduct>(ItemCodeComparison.Comparer);
        foreach (var p in products)
        {
            if (!string.IsNullOrWhiteSpace(p.Code))       dict.TryAdd(p.Code, p);
            if (!string.IsNullOrWhiteSpace(p.Barcode))    dict.TryAdd(p.Barcode!, p);
            if (!string.IsNullOrWhiteSpace(p.ManufacturerPartNumber))
            {
                dict.TryAdd(p.ManufacturerPartNumber!, p);
                // The normalised key too, so a line whose part number differs only in separators
                // or case still reaches the product (callers try raw first, then normalised).
                var normalised = p.ManufacturerPartNumberNormalized
                                 ?? ProductKeyNormalizer.Normalize(p.ManufacturerPartNumber);
                if (normalised is not null) dict.TryAdd(normalised, p);
            }
            if (!string.IsNullOrWhiteSpace(p.ExternalId)) dict.TryAdd(p.ExternalId!, p);
        }
        return dict;
    }

    /// <summary>
    /// Forces every line the structured extractor flagged (a number that did not
    /// appear in the source text, or a quantity × unit price that did not reconcile
    /// with the stated amount) to "needs review" — even if its code resolved
    /// deterministically — and caps its confidence so it surfaces for a human.
    /// P2 hardening: also records WHY onto <c>review_reason</c> (the extractor's
    /// per-line reason when provided, else a generic extraction reason), appended
    /// after any reason already written at line-entity creation (e.g. unresolved code).
    /// </summary>
    public static void ApplyExtractionReviewFlags(
        IReadOnlyList<PurchaseOrderLineEntity> lines,
        IReadOnlyCollection<int> reviewLineNumbers,
        IReadOnlyDictionary<int, string>? reviewReasons = null)
    {
        if (reviewLineNumbers.Count == 0) return;

        const string genericExtractionReason =
            "AI extraction flagged this line: a number could not be verified against the source document.";

        var reviewSet = reviewLineNumbers.ToHashSet();
        foreach (var le in lines)
        {
            if (!reviewSet.Contains(le.LineNumber)) continue;
            le.NeedsReview = true;
            // `if (le.Confidence > 0.5f) le.Confidence = 0.5f;` used to sit here — capping a state
            // flag so a flagged line would render red. NeedsReview + ReviewReason (set just below)
            // are what say the line needs looking at; the confidence column now holds a model score
            // or nothing, and a review flag is not evidence about the score's value.

            var reason = reviewReasons is not null && reviewReasons.TryGetValue(le.LineNumber, out var r)
                ? r
                : genericExtractionReason;
            le.ReviewReason = string.IsNullOrWhiteSpace(le.ReviewReason)
                ? reason
                : $"{le.ReviewReason} {reason}";
        }
    }
}
