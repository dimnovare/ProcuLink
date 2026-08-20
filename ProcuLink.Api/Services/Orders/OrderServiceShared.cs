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
    ///
    /// <para><b>Scoped fetch (P1 perf):</b> this used to load the ENTIRE active catalog — 14,713
    /// rows for a live supplier — while every consumer only ever PROBES the dictionary with keys
    /// derived from the order's lines (<c>SupplierItemCode</c>, raw
    /// <c>ManufacturerPartNumber</c>, and its <see cref="ProductKeyNormalizer"/> form; see
    /// <c>ScribanOrderModel.BuildLine</c>, <c>MappedTransformService.InjectCatalogRow</c>, the
    /// price-variance guard in <c>OrderResolutionService</c>, and
    /// <c>MapperEnrichmentController.GetCatalogHints</c> — nothing enumerates the lookup). The
    /// interactive mapping-preview endpoint calls this on every keystroke (300–400ms debounce), so
    /// the full fetch was quadratic pain per typing session. <paramref name="probeKeys"/> is the
    /// set of keys the caller can probe (build it with
    /// <see cref="CollectCatalogProbeKeys"/>), and the query fetches only rows one of those keys
    /// can reach — through ANY of the five key columns, case-insensitively, because the dictionary
    /// itself is case-insensitive across all of them (a probe by supplier item code may legally
    /// hit a row's barcode or normalised MPN). The result is IDENTICAL to the full fetch for every
    /// probeable key — pinned, against a verbatim oracle of the old behaviour, by
    /// <c>CatalogLookupScopedFetchTests</c>.</para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, SupplierProduct>> BuildCatalogLookupAsync(
        ProcuLinkDbContext db, Guid organisationId, Guid supplierId,
        IReadOnlyCollection<string> probeKeys, CancellationToken ct)
    {
        // ToLower(), not EF.Functions.ILike, and BOTH .NET lowerings of each key: `.ToLower()`
        // translates to SQL lower() on Npgsql AND runs in C# on the EF InMemory provider the unit
        // tests use (the exact pattern OrderIngestionService.ResolveByManufacturerPartAsync and
        // CatalogRetrievalService already established). Adding ToLowerInvariant() too costs one
        // extra set entry per key and keeps an exotic-culture host from folding a key differently
        // than the invariant rule the dictionary comparer uses.
        var loweredKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in probeKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            loweredKeys.Add(key.ToLower());
            loweredKeys.Add(key.ToLowerInvariant());
        }
        if (loweredKeys.Count == 0)
            return new Dictionary<string, SupplierProduct>(ItemCodeComparison.Comparer);

        // ORDERED, because the dictionary below is case-INSENSITIVE while the unique index on
        // (org_id, supplier_id, code) is case-SENSITIVE: a catalog may legally hold "AB-1" and
        // "ab-1" as two rows, and `TryAdd` is first-wins. Without an ORDER BY the winner was
        // whatever order Postgres happened to return, so the same order could resolve to a
        // different product between two runs — the same class of defect as two resolvers
        // disagreeing, one level down. Id is the final tie-break so the answer never depends on
        // physical row order. (The same applies to a Barcode/MPN/ExternalId on one row colliding
        // with a Code on another.) Scoping the WHERE does not disturb this: every row that could
        // claim a probeable key is still fetched, in the same relative order.
        //
        // The `ManufacturerPartNumberNormalized == null && ManufacturerPartNumber != null` arm
        // exists for legacy rows written before the normalised column: the dictionary computes
        // their normalised key in memory (the `??` fallback below), which no SQL predicate can
        // reproduce, so such rows are fetched unconditionally. Every writer since
        // AddManufacturerPartNumberToCatalog sets both columns together (SupplierCatalogService),
        // so in a healthy catalog this arm matches zero rows.
        var products = await db.SupplierProducts.AsNoTracking()
            .Where(p => p.OrgId == organisationId && p.SupplierId == supplierId && p.IsActive
                && (loweredKeys.Contains(p.Code.ToLower())
                    || (p.Barcode != null && loweredKeys.Contains(p.Barcode.ToLower()))
                    || (p.ManufacturerPartNumber != null && loweredKeys.Contains(p.ManufacturerPartNumber.ToLower()))
                    || (p.ManufacturerPartNumberNormalized != null && loweredKeys.Contains(p.ManufacturerPartNumberNormalized.ToLower()))
                    || (p.ManufacturerPartNumber != null && p.ManufacturerPartNumberNormalized == null)
                    || (p.ExternalId != null && loweredKeys.Contains(p.ExternalId.ToLower()))))
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
    /// The one place the catalog probe-key set is derived from an order's lines, so every
    /// <see cref="BuildCatalogLookupAsync"/> caller scopes its fetch by the SAME rule the
    /// consumers probe by: the line's <c>SupplierItemCode</c>, its raw
    /// <c>ManufacturerPartNumber</c>, and the <see cref="ProductKeyNormalizer"/> form of that
    /// part number (<c>ScribanOrderModel.BuildLine</c> tries exactly these three, in that order).
    /// Adding a probe to a consumer without adding it here would silently un-scope nothing and
    /// MISS matches — so keep this list and the consumers' probe order in the same review.
    /// </summary>
    public static IReadOnlyCollection<string> CollectCatalogProbeKeys(
        IEnumerable<PurchaseOrderLineEntity> lines)
    {
        var keys = new HashSet<string>(ItemCodeComparison.Comparer);
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line.SupplierItemCode))
                keys.Add(line.SupplierItemCode!);
            if (!string.IsNullOrWhiteSpace(line.ManufacturerPartNumber))
            {
                keys.Add(line.ManufacturerPartNumber!);
                var normalised = ProductKeyNormalizer.Normalize(line.ManufacturerPartNumber);
                if (normalised is not null) keys.Add(normalised);
            }
        }
        return keys;
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
