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
    /// <summary>
    /// Feature-flag configuration key. Absent/false is the CODE default (live tables drive
    /// everything) — but it is NOT the deployed value: production sets
    /// <c>Connections__RevisionAuthority = true</c> on both Railway services, so revision authority
    /// is ON where it matters. See <see cref="RevisionAuthorityHosts"/> and
    /// <c>docs/ops/revision-authority-production-smoke.md</c>. Read the effective value from
    /// <c>GET /health/ready</c> (<c>revisionAuthority</c>) or the host's startup log — never infer
    /// it from an appsettings file, which is how the 2026-07-27 audit produced a false P0.
    /// </summary>
    public const string FlagKey = "Connections:RevisionAuthority";

    /// <summary>
    /// The environment-variable spelling of <see cref="FlagKey"/> (ASP.NET's <c>__</c> section
    /// separator) — what a deployer actually types into Railway.
    /// </summary>
    public const string EnvironmentVariableName = "Connections__RevisionAuthority";

    /// <summary>
    /// THE single reader of the flag. Raw read + <c>bool.TryParse</c> (no Binder dependency);
    /// anything but "true"/"True"/"TRUE" — including "1", "yes", blank, or the key being absent —
    /// leaves the flag OFF. Every consumer must call this rather than re-parsing: two consumers of
    /// the same order disagreeing about the flag would route it two ways in the same instant.
    /// </summary>
    public static bool IsEnabled(IConfiguration? configuration) =>
        bool.TryParse(configuration?[FlagKey], out var on) && on;

    private readonly ProcuLinkDbContext _db;
    private readonly bool _enabled;
    private readonly ILogger<EffectiveConnectionConfigResolver>? _logger;

    public EffectiveConnectionConfigResolver(
        ProcuLinkDbContext db,
        IConfiguration configuration,
        ILogger<EffectiveConnectionConfigResolver>? logger = null)
    {
        _db = db;
        _enabled = IsEnabled(configuration);
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
