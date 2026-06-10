using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// Group V1 lifecycle service for the versioned Supplier Connection. Generalises the
/// <see cref="SupplierAcceptanceService"/> versioning precedent (version_no + status +
/// effective_from/to, archive-prior-on-activate) to the whole connection bundle, and adds
/// the connection-level <c>active_revision_id</c> pointer the acceptance precedent lacked.
/// All queries are org-scoped.
/// </summary>
public sealed class SupplierConnectionService : ISupplierConnectionService
{
    private readonly ProcuLinkDbContext _db;
    public SupplierConnectionService(ProcuLinkDbContext db) => _db = db;

    public async Task<IReadOnlyList<SupplierConnection>> ListAsync(Guid orgId, CancellationToken ct) =>
        await _db.SupplierConnections
            .Where(c => c.OrgId == orgId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    public async Task<SupplierConnection?> GetAsync(Guid orgId, Guid connectionId, CancellationToken ct) =>
        await _db.SupplierConnections
            .Include(c => c.Revisions)
            .Where(c => c.OrgId == orgId && c.Id == connectionId)
            .FirstOrDefaultAsync(ct);

    public async Task<SupplierConnectionRevision?> GetRevisionAsync(
        Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct) =>
        await _db.SupplierConnectionRevisions
            .Include(r => r.ItemMappings)
            .Include(r => r.TestCases)
            .Where(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == revisionId)
            .FirstOrDefaultAsync(ct);

    public async Task<SupplierConnection?> EnsureConnectionAsync(
        Guid orgId, Guid supplierId, string? createdBy, CancellationToken ct)
    {
        var existing = await _db.SupplierConnections
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.SupplierId == supplierId, ct);
        if (existing is not null) return existing;

        // Supplier must belong to the org (cross-tenant guard).
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId && s.OrgId == orgId, ct);
        if (supplier is null) return null;

        var now = DateTime.UtcNow;
        var connection = new SupplierConnection
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            SupplierId = supplierId,
            Name       = supplier.Name,
            CreatedBy  = createdBy,
            CreatedAt  = now,
            UpdatedAt  = now,
        };
        _db.SupplierConnections.Add(connection);
        await _db.SaveChangesAsync(ct);
        return connection;
    }

    public async Task<SupplierConnectionRevision?> CreateDraftAsync(
        Guid orgId, Guid connectionId, ConnectionRevisionDraftInput? input,
        bool cloneFromActive, string? createdBy, CancellationToken ct)
    {
        var connection = await _db.SupplierConnections
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == connectionId, ct);
        if (connection is null) return null;

        var maxVersion = await _db.SupplierConnectionRevisions
            .Where(r => r.ConnectionId == connectionId)
            .Select(r => (int?)r.VersionNo)
            .MaxAsync(ct);
        var nextVersion = (maxVersion ?? 0) + 1;

        var now = DateTime.UtcNow;
        var revisionId = Guid.NewGuid();
        var draft = new SupplierConnectionRevision
        {
            Id           = revisionId,
            ConnectionId = connectionId,
            OrgId        = orgId,
            SupplierId   = connection.SupplierId,
            VersionNo    = nextVersion,
            Status       = "draft",
            CreatedAt    = now,
            CreatedBy    = createdBy,
            CatalogMode  = "live",
        };

        // Clone-from-active takes precedence: snapshot the published revision's bundle into the draft.
        SupplierConnectionRevision? source = null;
        if (cloneFromActive && connection.ActiveRevisionId is not null)
        {
            source = await _db.SupplierConnectionRevisions
                .Include(r => r.ItemMappings)
                .FirstOrDefaultAsync(r => r.Id == connection.ActiveRevisionId, ct);
        }

        if (source is not null)
        {
            draft.InputMappingJson    = source.InputMappingJson;
            draft.OutputMappingJson   = source.OutputMappingJson;
            draft.OutputFormat        = source.OutputFormat;
            draft.DeliveryProtocol    = source.DeliveryProtocol;
            draft.DeliveryConfigJson  = source.DeliveryConfigJson;
            draft.DeliveryAutoDeliver = source.DeliveryAutoDeliver;
            draft.CredentialsRef      = source.CredentialsRef;
            draft.AcceptanceProfileId = source.AcceptanceProfileId;
            draft.AcceptanceVersionNo = source.AcceptanceVersionNo;
            draft.CatalogMode         = source.CatalogMode;
            draft.ItemMappings        = source.ItemMappings.Select(m => CloneMapping(revisionId, m)).ToList();
        }
        else if (input is not null)
        {
            ApplyInput(draft, revisionId, input);
        }

        _db.SupplierConnectionRevisions.Add(draft);
        await _db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task<bool?> UpdateDraftAsync(
        Guid orgId, Guid connectionId, Guid revisionId, ConnectionRevisionDraftInput input, CancellationToken ct)
    {
        var rev = await _db.SupplierConnectionRevisions
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == revisionId, ct);
        if (rev is null) return null;

        // Immutability: only draft/test revisions may be edited; publish is the freeze line.
        if (rev.Status is not ("draft" or "test")) return false;

        ApplyScalars(rev, input);

        // Replace child item mappings via the DbSet directly (no Include navigation). Delete the
        // old rows in their own SaveChanges first, then insert the new ones — separate units of
        // work avoid the InMemory "update/delete an entity that does not exist" concurrency error
        // a combined remove+reinsert can trigger.
        var oldMappings = await _db.ConnectionRevisionItemMappings
            .Where(m => m.RevisionId == revisionId)
            .ToListAsync(ct);
        if (oldMappings.Count > 0)
        {
            _db.ConnectionRevisionItemMappings.RemoveRange(oldMappings);
            await _db.SaveChangesAsync(ct);
        }
        var newMappings = (input.ItemMappings ?? Array.Empty<ConnectionItemMappingInput>())
            .Select(m => NewMapping(revisionId, m))
            .ToList();
        if (newMappings.Count > 0)
            _db.ConnectionRevisionItemMappings.AddRange(newMappings);

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool?> MarkTestAsync(Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var rev = await _db.SupplierConnectionRevisions
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == revisionId, ct);
        if (rev is null) return null;
        if (rev.Status is not ("draft" or "test")) return false;

        rev.Status = "test";
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool?> PublishAsync(
        Guid orgId, Guid connectionId, Guid revisionId, string? publishedBy, CancellationToken ct)
    {
        var connection = await _db.SupplierConnections
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == connectionId, ct);
        if (connection is null) return null;

        var revisions = await _db.SupplierConnectionRevisions
            .Where(r => r.ConnectionId == connectionId)
            .ToListAsync(ct);
        var target = revisions.FirstOrDefault(r => r.Id == revisionId);
        if (target is null) return null;

        // Only draft/test may be published; published/archived are frozen.
        if (target.Status is not ("draft" or "test")) return false;

        var now = DateTime.UtcNow;
        // Archive the prior published revision (one published per connection — acceptance precedent).
        foreach (var r in revisions)
        {
            if (r.Status == "published" && r.Id != target.Id)
            {
                r.Status = "archived";
                r.EffectiveTo = now;
            }
        }

        target.Status = "published";
        target.EffectiveFrom = now;
        target.EffectiveTo = null;
        target.PublishedAt = now;
        target.PublishedBy = publishedBy;

        connection.ActiveRevisionId = target.Id;
        connection.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool?> ArchiveAsync(Guid orgId, Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var connection = await _db.SupplierConnections
            .FirstOrDefaultAsync(c => c.OrgId == orgId && c.Id == connectionId, ct);
        if (connection is null) return null;

        var rev = await _db.SupplierConnectionRevisions
            .FirstOrDefaultAsync(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == revisionId, ct);
        if (rev is null) return null;

        var now = DateTime.UtcNow;
        rev.Status = "archived";
        rev.EffectiveTo = now;

        if (connection.ActiveRevisionId == rev.Id)
        {
            connection.ActiveRevisionId = null;
            connection.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    // Scalar-only assignment (used by both create and update); item mappings are handled
    // separately so the UPDATE path can mutate the TRACKED collection rather than reassign it
    // (reassigning while old children are marked Deleted trips an InMemory concurrency error).
    private static void ApplyScalars(SupplierConnectionRevision rev, ConnectionRevisionDraftInput input)
    {
        rev.InputMappingJson    = input.InputMappingJson;
        rev.OutputMappingJson   = input.OutputMappingJson;
        rev.OutputFormat        = input.OutputFormat;
        rev.DeliveryProtocol    = input.DeliveryProtocol;
        rev.DeliveryConfigJson  = input.DeliveryConfigJson;
        rev.DeliveryAutoDeliver = input.DeliveryAutoDeliver;
        rev.CredentialsRef      = input.CredentialsRef;
        rev.AcceptanceProfileId = input.AcceptanceProfileId;
        rev.AcceptanceVersionNo = input.AcceptanceVersionNo;
        rev.CatalogMode         = string.IsNullOrWhiteSpace(input.CatalogMode) ? "live" : input.CatalogMode;
    }

    // For a brand-new (untracked) draft entity it's safe to set the navigation collection directly.
    private static void ApplyInput(SupplierConnectionRevision rev, Guid revisionId, ConnectionRevisionDraftInput input)
    {
        ApplyScalars(rev, input);
        rev.ItemMappings = (input.ItemMappings ?? Array.Empty<ConnectionItemMappingInput>())
            .Select(m => NewMapping(revisionId, m)).ToList();
    }

    private static ConnectionRevisionItemMapping NewMapping(Guid revisionId, ConnectionItemMappingInput m) => new()
    {
        Id               = Guid.NewGuid(),
        RevisionId       = revisionId,
        BuyerItemCode    = m.BuyerItemCode,
        SupplierItemCode = m.SupplierItemCode,
        Confidence       = m.Confidence,
        Source           = m.Source,
    };

    private static ConnectionRevisionItemMapping CloneMapping(Guid revisionId, ConnectionRevisionItemMapping m) => new()
    {
        Id               = Guid.NewGuid(),
        RevisionId       = revisionId,
        BuyerItemCode    = m.BuyerItemCode,
        SupplierItemCode = m.SupplierItemCode,
        Confidence       = m.Confidence,
        Source           = m.Source,
    };
}
