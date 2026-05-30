using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Infrastructure.Services.Detection;

/// <summary>
/// Supplier-scoped field-mapping moat. See <see cref="ISupplierSchemaMappingService"/>.
///
/// Column headers and the detected format are supplied by the caller (from the parse result), so
/// this service never needs to download the source file itself.
/// </summary>
public sealed class SupplierSchemaMappingService : ISupplierSchemaMappingService
{
    private readonly ProcuLinkDbContext _db;
    private readonly ILogger<SupplierSchemaMappingService> _logger;

    public SupplierSchemaMappingService(
        ProcuLinkDbContext db,
        ILogger<SupplierSchemaMappingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task CaptureAsync(
        Guid organisationId,
        Guid supplierId,
        Guid? learnedFromOrderId,
        IReadOnlyList<string>? columnHeaders,
        string detectedFormat,
        IReadOnlyDictionary<string, string> fieldMapping,
        CancellationToken ct)
    {
        var hash = SchemaFingerprintHasher.ComputeColumnNameHash(columnHeaders);
        // Header-less format (XML / EDIFACT / PDF) — nothing to key the learned mapping on.
        return UpsertAsync(organisationId, supplierId, learnedFromOrderId, hash, detectedFormat, fieldMapping, ct);
    }

    public Task ReinforceByHashAsync(
        Guid organisationId,
        Guid supplierId,
        Guid? learnedFromOrderId,
        string? columnNameHash,
        string detectedFormat,
        IReadOnlyDictionary<string, string> fieldMapping,
        CancellationToken ct)
        => UpsertAsync(organisationId, supplierId, learnedFromOrderId, columnNameHash, detectedFormat, fieldMapping, ct);

    private async Task UpsertAsync(
        Guid organisationId,
        Guid supplierId,
        Guid? learnedFromOrderId,
        string? hash,
        string detectedFormat,
        IReadOnlyDictionary<string, string> fieldMapping,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            _logger.LogDebug(
                "Supplier schema mapping skipped — no layout hash for org {OrgId} supplier {SupplierId} ({Format})",
                organisationId, supplierId, detectedFormat);
            return;
        }

        var layoutHash = hash; // non-null past the guard above

        var normalised = Normalise(fieldMapping);
        if (normalised.Count == 0)
        {
            // Nothing resolved on this file — no buyer→supplier pairs worth learning.
            _logger.LogDebug(
                "Supplier schema mapping skipped — empty field mapping for org {OrgId} supplier {SupplierId}",
                organisationId, supplierId);
            return;
        }

        var now = DateTime.UtcNow;
        var existing = await _db.SupplierSchemaMappings
            .FirstOrDefaultAsync(
                m => m.OrganisationId == organisationId
                  && m.SupplierId == supplierId
                  && m.ColumnNameHash == layoutHash, ct);

        if (existing is not null)
        {
            // Merge the freshly observed pairs over what we already knew. Newer wins on conflict —
            // a buyer code's supplier mapping can legitimately change over time.
            var merged = Deserialize(existing.FieldMappingJson);
            foreach (var kvp in normalised) merged[kvp.Key] = kvp.Value;

            existing.FieldMappingJson   = Serialize(merged);
            existing.DetectedFormat     = detectedFormat;
            existing.LearnedFromOrderId = learnedFromOrderId;
            existing.ObservationCount  += 1;
            existing.LastLearnedAt      = now;
        }
        else
        {
            _db.SupplierSchemaMappings.Add(new SupplierSchemaMapping
            {
                Id                 = Guid.NewGuid(),
                OrganisationId     = organisationId,
                SupplierId         = supplierId,
                ColumnNameHash     = layoutHash,
                DetectedFormat     = detectedFormat,
                FieldMappingJson   = Serialize(normalised),
                LearnedFromOrderId = learnedFromOrderId,
                ObservationCount   = 1,
                LastLearnedAt      = now,
                CreatedAt          = now,
            });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent worker inserted the same (org, supplier, layout) row first and won the
            // unique-index race (Postgres 23505). Detach the stale Added entity, reload the winner,
            // merge our pairs in, and re-save.
            await RecoverFromConcurrentInsertAsync(
                organisationId, supplierId, layoutHash, detectedFormat, learnedFromOrderId, normalised, now, ct);
            return;
        }

        _logger.LogInformation(
            "Learned supplier schema mapping for org {OrgId} supplier {SupplierId}: hash {Hash}, {Pairs} pair(s), observed {Count} time(s)",
            organisationId, supplierId, layoutHash, normalised.Count, existing?.ObservationCount ?? 1);
    }

    private async Task RecoverFromConcurrentInsertAsync(
        Guid organisationId, Guid supplierId, string hash, string detectedFormat,
        Guid? learnedFromOrderId, IReadOnlyDictionary<string, string> normalised, DateTime now,
        CancellationToken ct)
    {
        var stale = _db.ChangeTracker.Entries<SupplierSchemaMapping>()
            .FirstOrDefault(e => e.State == EntityState.Added);
        if (stale is not null) stale.State = EntityState.Detached;

        var winner = await _db.SupplierSchemaMappings
            .FirstOrDefaultAsync(
                m => m.OrganisationId == organisationId
                  && m.SupplierId == supplierId
                  && m.ColumnNameHash == hash, ct);

        if (winner is null)
        {
            _logger.LogWarning(
                "Supplier schema mapping race recovery aborted — no existing row for org {OrgId} supplier {SupplierId} hash {Hash}",
                organisationId, supplierId, hash);
            return;
        }

        var merged = Deserialize(winner.FieldMappingJson);
        foreach (var kvp in normalised) merged[kvp.Key] = kvp.Value;

        winner.FieldMappingJson   = Serialize(merged);
        winner.DetectedFormat     = detectedFormat;
        winner.LearnedFromOrderId = learnedFromOrderId;
        winner.ObservationCount  += 1;
        winner.LastLearnedAt      = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Race recovery: supplier schema mapping for org {OrgId} supplier {SupplierId} hash {Hash} observed {Count} time(s)",
            organisationId, supplierId, hash, winner.ObservationCount);
    }

    public async Task<SupplierSchemaMappingMatch?> LookupAsync(
        Guid organisationId,
        Guid supplierId,
        IReadOnlyList<string>? columnHeaders,
        CancellationToken ct)
    {
        var hash = SchemaFingerprintHasher.ComputeColumnNameHash(columnHeaders);
        if (hash is null) return null;

        var row = await _db.SupplierSchemaMappings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.OrganisationId == organisationId
                  && m.SupplierId == supplierId
                  && m.ColumnNameHash == hash, ct);

        if (row is null) return null;

        var mapping = Deserialize(row.FieldMappingJson);
        if (mapping.Count == 0) return null;

        return new SupplierSchemaMappingMatch(
            row.ColumnNameHash, row.ObservationCount, row.DetectedFormat, mapping);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalises a raw buyer→supplier map: trims, lower-cases buyer keys (so capture and lookup
    /// agree regardless of source casing), trims supplier values, and drops pairs where either side
    /// is blank. Last write wins on a key collision after normalisation.
    /// </summary>
    private static Dictionary<string, string> Normalise(IReadOnlyDictionary<string, string> raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in raw)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || string.IsNullOrWhiteSpace(kvp.Value)) continue;
            var key = kvp.Key.Trim().ToLowerInvariant();
            result[key] = kvp.Value.Trim();
        }
        return result;
    }

    private static string Serialize(IReadOnlyDictionary<string, string> map) =>
        JsonSerializer.Serialize(map);

    private static Dictionary<string, string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   is { } d
                ? new Dictionary<string, string>(d, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
