using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Launch batch 7 — revision authority resolver. Loads a pinned PUBLISHED revision's bundle ONCE
/// (org-scoped, no-tracking, item mappings included) when the <c>Connections:RevisionAuthority</c>
/// flag is ON; every miss (flag off, unpinned order, pin not found in this org, lookup failure)
/// returns <see cref="EffectiveConnectionConfig.Live"/> so the caller keeps reading the live
/// mutable tables — byte-for-byte the pre-batch-7 behaviour. Never throws (cancellation excepted).
/// </summary>
public sealed class EffectiveConnectionConfigResolver : IEffectiveConnectionConfigResolver
{
    /// <summary>Feature-flag configuration key. Absent/false (the default) = live tables drive everything.</summary>
    public const string FlagKey = "Connections:RevisionAuthority";

    private readonly ProcuLinkDbContext _db;
    private readonly bool _enabled;
    private readonly ILogger<EffectiveConnectionConfigResolver>? _logger;

    public EffectiveConnectionConfigResolver(
        ProcuLinkDbContext db,
        IConfiguration configuration,
        ILogger<EffectiveConnectionConfigResolver>? logger = null)
    {
        _db = db;
        // Raw read + TryParse (no Binder dependency); anything but "true" — including the key
        // being absent, the safe production default — leaves the flag OFF.
        _enabled = bool.TryParse(configuration[FlagKey], out var on) && on;
        _logger = logger;
    }

    public async Task<EffectiveConnectionConfig> ResolveAsync(
        Guid orgId, Guid? connectionRevisionId, CancellationToken ct)
    {
        if (!_enabled || connectionRevisionId is null)
            return EffectiveConnectionConfig.Live;

        try
        {
            var revision = await _db.SupplierConnectionRevisions
                .AsNoTracking()
                .Include(r => r.ItemMappings)
                .Where(r => r.OrgId == orgId && r.Id == connectionRevisionId.Value)
                .FirstOrDefaultAsync(ct);

            if (revision is null)
            {
                // Orphan pin (revision deleted / cross-org id) — fall back to live, never throw.
                _logger?.LogInformation(
                    "Revision authority: pinned revision {RevisionId} not found for org {OrgId} — using live config.",
                    connectionRevisionId, orgId);
                return EffectiveConnectionConfig.Live;
            }

            return EffectiveConnectionConfig.FromRevision(revision);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is the caller's signal — never swallow it
        }
        catch (Exception ex)
        {
            // Defensive: a resolver failure must never brick parse/validate/transform/deliver.
            // Falling back to live keeps the order flowing (the provenance pin on the order is
            // untouched, so a later replay can still reproduce the revision-true result).
            _logger?.LogWarning(ex,
                "Revision authority: failed to load pinned revision {RevisionId} for org {OrgId} — using live config.",
                connectionRevisionId, orgId);
            return EffectiveConnectionConfig.Live;
        }
    }
}
