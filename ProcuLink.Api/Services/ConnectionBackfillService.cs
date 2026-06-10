using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
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
/// </summary>
public sealed class ConnectionBackfillService : IConnectionBackfillService
{
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

            var revId = await BackfillSupplierAsync(c.OrgId, c.SupplierId, ct);
            if (revId is not null) created++;
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
            CreatedBy     = "system:backfill",
            PublishedBy   = "system:backfill",
            // Input/parse mapping snapshot.
            InputMappingJson = poMapping?.ConfigJson,
            // Output: no per-supplier template assignment exists today, so leave null
            // (= the fixed transformer path, reproducing current behaviour exactly).
            OutputMappingJson = null,
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
            ActiveRevisionId = revisionId,
            CreatedBy        = "system:backfill",
            CreatedAt        = now,
            UpdatedAt        = now,
        };

        _db.SupplierConnectionRevisions.Add(revision);
        _db.SupplierConnections.Add(connection);
        await _db.SaveChangesAsync(ct);

        return revisionId;
    }
}
