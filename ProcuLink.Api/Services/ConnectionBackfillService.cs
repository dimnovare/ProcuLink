using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// Group V1 backfill (zero behaviour change). For each (org, supplier) that has any existing
/// loose config but no <see cref="SupplierConnection"/> yet, creates one connection + one
/// published "revision 1" snapshotting the supplier's CURRENT config, and sets the connection's
/// active pointer to it. Source tables are left untouched.
///
/// <para>EF-only (no raw SQL, per the project convention). Idempotent — the
/// UNIQUE(org_id, supplier_id) connection guard plus an existence check make re-running a no-op,
/// so this is safe to call on every boot or under deploy/Hangfire retries.</para>
///
/// <para>Launch-batch-7 review fix: the revision's <c>output_mapping_json</c> now snapshots the
/// supplier-promoted Output section (launch batch 4A) of <c>SupplierPoMapping.ConfigJson</c> when
/// a usable one exists, and <see cref="RebackfillPromotedOutputAsync"/> repairs rows created
/// before this fix (fill-null-only, idempotent, per-row skip+warn).</para>
/// </summary>
public sealed class ConnectionBackfillService : IConnectionBackfillService
{
    /// <summary>
    /// Actor stamped on rows this service creates. The promoted-output re-backfill pass only
    /// ever touches rows carrying it — a USER-authored revision with a null output mapping
    /// means "fixed transformer" by choice and must never be rewritten.
    /// </summary>
    public const string BackfillActor = "system:backfill";

    /// <summary>
    /// Deserializer for <c>SupplierPoMapping.ConfigJson</c> — IDENTICAL options to
    /// <c>PoMappingService</c> (camelCase + case-insensitive) so the promoted Output section is
    /// read exactly as the live transform path (launch batch 4A) reads it.
    /// </summary>
    private static readonly JsonSerializerOptions PoMappingJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Serializer for the snapshotted Output section — camelCase, matching BOTH how
    /// <c>PoMappingService</c> persists the promoted Output inside ConfigJson AND how the
    /// revision-pinned transform path reads <c>output_mapping_json</c> back into an
    /// <see cref="OutputMappingConfig"/> (<c>OrderTransformService.RevisionOutputSerializerOptions</c>
    /// / <c>ReplayService.DeserializeOutputConfig</c>). This IS the consumable shape of the
    /// revision output snapshot.
    /// </summary>
    private static readonly JsonSerializerOptions PromotedOutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ProcuLinkDbContext _db;
    public ConnectionBackfillService(ProcuLinkDbContext db) => _db = db;

    public async Task<int> BackfillAllAsync(CancellationToken ct)
    {
        // Candidate (org, supplier) pairs: any supplier that has ANY existing config surface.
        // Union the distinct (org, supplier) keys from each loose-config table.
        var fromPoMappings = _db.SupplierPoMappings.Select(x => new { x.OrgId, x.SupplierId });
        var fromDelivery   = _db.SupplierDeliveryConfigs.Select(x => new { x.OrgId, x.SupplierId });
        var fromItems      = _db.ItemMappings.Select(x => new { x.OrgId, x.SupplierId });
        var fromAcceptance = _db.SupplierAcceptanceProfiles.Select(x => new { x.OrgId, x.SupplierId });
        var fromProducts   = _db.SupplierProducts.Select(x => new { x.OrgId, x.SupplierId });

        var candidates = await fromPoMappings
            .Concat(fromDelivery)
            .Concat(fromItems)
            .Concat(fromAcceptance)
            .Concat(fromProducts)
            .Distinct()
            .ToListAsync(ct);

        var created = 0;
        foreach (var c in candidates)
        {
            // Count only genuinely NEW connections (idempotent re-runs must report 0).
            var alreadyExists = await _db.SupplierConnections
                .AnyAsync(x => x.OrgId == c.OrgId && x.SupplierId == c.SupplierId, ct);
            if (alreadyExists) continue;

            // Isolate per-supplier failures: one bad supplier must not abort the whole
            // backfill. EF tracks per-DbContext, so on a failure detach the half-added
            // graph before continuing.
            try
            {
                var revId = await BackfillSupplierAsync(c.OrgId, c.SupplierId, ct);
                if (revId is not null) created++;
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                Console.Error.WriteLine($"[ConnectionBackfill] supplier {c.SupplierId} (org {c.OrgId}) failed: {ex.Message}");
            }
        }
        return created;
    }

    public async Task<Guid?> BackfillSupplierAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        // Idempotency: if a connection already exists for this (org, supplier), return its active rev.
        var existing = await _db.SupplierConnections
            .FirstOrDefaultAsync(x => x.OrgId == orgId && x.SupplierId == supplierId, ct);
        if (existing is not null)
            return existing.ActiveRevisionId;

        // Gather the supplier's current config (each piece optional).
        var poMapping = await _db.SupplierPoMappings
            .FirstOrDefaultAsync(x => x.OrgId == orgId && x.SupplierId == supplierId, ct);
        var delivery = await _db.SupplierDeliveryConfigs
            .FirstOrDefaultAsync(x => x.OrgId == orgId && x.SupplierId == supplierId, ct);
        var itemMappings = await _db.ItemMappings
            .Where(x => x.OrgId == orgId && x.SupplierId == supplierId)
            .ToListAsync(ct);
        var activeAcceptance = await _db.SupplierAcceptanceProfiles
            .Where(x => x.OrgId == orgId && x.SupplierId == supplierId && x.Status == "active")
            .FirstOrDefaultAsync(ct);
        var hasProducts = await _db.SupplierProducts
            .AnyAsync(x => x.OrgId == orgId && x.SupplierId == supplierId, ct);

        // No config at all → nothing to snapshot (zero-config supplier; orders fall back to live).
        var hasAnyConfig = poMapping is not null || delivery is not null
            || itemMappings.Count > 0 || activeAcceptance is not null || hasProducts;
        if (!hasAnyConfig)
            return null;

        var supplierName = await _db.Suppliers
            .Where(s => s.Id == supplierId && s.OrgId == orgId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct) ?? "Supplier";

        var now = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();

        var revision = new SupplierConnectionRevision
        {
            Id            = revisionId,
            ConnectionId  = connectionId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            VersionNo     = 1,
            Status        = "published",
            EffectiveFrom = now,
            PublishedAt   = now,
            CreatedAt     = now,
            CreatedBy     = BackfillActor,
            PublishedBy   = BackfillActor,
            // Input/parse mapping snapshot (full ConfigJson, byte-identical).
            InputMappingJson = poMapping?.ConfigJson,
            // Output: snapshot the supplier-promoted Output section (launch batch 4A) when the
            // po-mapping carries a usable one, so a flag-ON pinned order keeps rendering through
            // the promoted layout instead of silently reverting to the fixed transformer.
            // Null (no/unusable/malformed Output) = the fixed transformer path, reproducing
            // current behaviour exactly.
            OutputMappingJson = TryExtractPromotedOutputJson(poMapping?.ConfigJson),
            OutputFormat      = delivery?.OutputFormat,
            // Delivery channel snapshot.
            DeliveryProtocol    = delivery?.Protocol,
            DeliveryConfigJson  = delivery?.ConfigJson,
            DeliveryAutoDeliver = delivery?.AutoDeliver ?? false,
            CredentialsRef      = string.IsNullOrEmpty(delivery?.EncryptedCredentials) ? null : delivery!.EncryptedCredentials,
            // Validation binding (bind by id; don't copy).
            AcceptanceProfileId = activeAcceptance?.Id,
            AcceptanceVersionNo = activeAcceptance?.VersionNo,
            // Catalog stays live in V1.
            CatalogMode = "live",
            ItemMappings = itemMappings.Select(m => new ConnectionRevisionItemMapping
            {
                Id               = Guid.NewGuid(),
                RevisionId       = revisionId,
                BuyerItemCode    = m.BuyerItemCode,
                SupplierItemCode = m.SupplierItemCode,
                Confidence       = m.Confidence,
                Source           = m.Source,
            }).ToList(),
        };

        var connection = new SupplierConnection
        {
            Id               = connectionId,
            OrgId            = orgId,
            SupplierId       = supplierId,
            Name             = supplierName,
            // Break the connection<->revision circular FK: insert with NO active pointer
            // first, then set it. Adding both rows (each referencing the other) in ONE
            // SaveChanges throws on PostgreSQL ("cannot determine insert order") — the
            // InMemory test provider does NOT enforce this, which masked it in tests.
            ActiveRevisionId = null,
            CreatedBy        = BackfillActor,
            CreatedAt        = now,
            UpdatedAt        = now,
        };

        _db.SupplierConnections.Add(connection);          // active_revision_id = NULL → no FK to revision yet
        _db.SupplierConnectionRevisions.Add(revision);    // connection_id → the connection above (now insertable)
        await _db.SaveChangesAsync(ct);

        connection.ActiveRevisionId = revisionId;          // now both rows exist → set the pointer
        await _db.SaveChangesAsync(ct);

        return revisionId;
    }

    // ── Launch-batch-7 review fix: promoted-output re-backfill ─────────────────

    public async Task<int> RebackfillPromotedOutputAsync(CancellationToken ct)
    {
        // Candidates: ONLY rows this service created (see BackfillActor) whose output snapshot
        // is still null. Fill-null-only — a non-null snapshot is NEVER overwritten, and a
        // user-authored revision (different CreatedBy) is NEVER touched. Idempotent: once a row
        // is filled it stops matching; rows whose supplier config has no usable promoted Output
        // are simply re-examined (cheaply) and left untouched on every run.
        var candidates = await _db.SupplierConnectionRevisions
            .AsNoTracking()
            .Where(r => r.OutputMappingJson == null && r.CreatedBy == BackfillActor)
            .Select(r => new { r.Id, r.OrgId, r.SupplierId })
            .ToListAsync(ct);

        var updated = 0;
        foreach (var c in candidates)
        {
            try
            {
                var configJson = await _db.SupplierPoMappings
                    .AsNoTracking()
                    .Where(x => x.OrgId == c.OrgId && x.SupplierId == c.SupplierId)
                    .Select(x => x.ConfigJson)
                    .FirstOrDefaultAsync(ct);

                var outputJson = TryExtractPromotedOutputJson(configJson);
                if (outputJson is null) continue; // no usable promoted Output — row left untouched

                // Re-load TRACKED with the null guard re-applied so this pass only ever FILLS a
                // null snapshot — it cannot overwrite a concurrent writer's value.
                var revision = await _db.SupplierConnectionRevisions
                    .FirstOrDefaultAsync(r => r.Id == c.Id && r.OutputMappingJson == null, ct);
                if (revision is null) continue;

                revision.OutputMappingJson = outputJson;
                await _db.SaveChangesAsync(ct);
                updated++;
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation is the caller's signal — never swallow it
            }
            catch (Exception ex)
            {
                // Per-row isolation, deliberately trigger-compatible: this UPDATEs published
                // rows, so if the published-row immutability DB trigger (migration
                // AddReviewReasonAndPublishedRevisionImmutability — its IS-DISTINCT-FROM guard
                // covers output_mapping_json) or anything else rejects it, skip + warn and keep
                // going — the remaining rows still get repaired. DEPLOY ORDER: this pass must
                // boot against a database BEFORE that trigger migration is applied (or the
                // trigger must exempt NULL→value fills of output_mapping_json); otherwise the
                // affected rows stay null (skipped with this warning) and their pinned orders
                // keep using the fixed transformer.
                _db.ChangeTracker.Clear();
                Console.Error.WriteLine(
                    $"[ConnectionBackfill] promoted-output re-backfill skipped revision {c.Id} (org {c.OrgId}): {ex.Message}");
            }
        }
        return updated;
    }

    /// <summary>
    /// Extracts the supplier-promoted Output section of a <c>SupplierPoMapping.ConfigJson</c> as
    /// the revision-consumable snapshot JSON: a camelCase-serialized <see cref="OutputMappingConfig"/>,
    /// exactly what the pinned-revision transform path deserializes from <c>output_mapping_json</c>.
    /// Returns null — meaning "fixed transformer", today's behaviour — when the config is
    /// null/blank, malformed, or carries no usable Output
    /// (<see cref="OrderMappingOverrideReader.HasUsablePromotedOutput"/>). Never throws.
    /// </summary>
    internal static string? TryExtractPromotedOutputJson(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;

        try
        {
            var config = JsonSerializer.Deserialize<PoMappingConfig>(configJson, PoMappingJsonOptions);
            if (!OrderMappingOverrideReader.HasUsablePromotedOutput(config)) return null;
            return JsonSerializer.Serialize(config!.Output, PromotedOutputJsonOptions);
        }
        catch (JsonException)
        {
            // Not a parseable PoMappingConfig (legacy/free-form mapping JSON) — there is no
            // promoted output to snapshot; the fixed transformer stays in control, as today.
            return null;
        }
    }
}
