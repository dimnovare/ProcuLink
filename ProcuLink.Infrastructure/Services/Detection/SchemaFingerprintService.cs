using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Infrastructure.Services.Detection;

/// <summary>
/// Org-scoped schema fingerprinting (v1). See <see cref="ISchemaFingerprintService"/>.
///
/// Column headers and the detected format are supplied by the caller (from the parse result),
/// so this service never needs to download the source file itself.
/// </summary>
public sealed class SchemaFingerprintService : ISchemaFingerprintService
{
    private readonly ProcuLinkDbContext _db;
    private readonly ILogger<SchemaFingerprintService> _logger;

    public SchemaFingerprintService(
        ProcuLinkDbContext db,
        ILogger<SchemaFingerprintService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordParseSuccessAsync(
        Guid organisationId,
        Guid orderId,
        IReadOnlyList<string>? columnHeaders,
        string detectedFormat,
        CancellationToken ct)
    {
        var order = await _db.PurchaseOrders
            .Include(o => o.Supplier)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.OrgId == organisationId, ct);

        if (order is null)
        {
            _logger.LogDebug("Fingerprint skipped — order {OrderId} not found for org {OrgId}", orderId, organisationId);
            return;
        }

        // Idempotency guard: a non-null hash means this order was already counted. This survives
        // Hangfire at-least-once delivery — even a crash *after* the previous commit re-enters here
        // and short-circuits, because the hash and the increment were saved in one transaction.
        if (order.SchemaFingerprintHash is not null)
        {
            _logger.LogDebug("Fingerprint skipped — order {OrderId} already fingerprinted", orderId);
            return;
        }

        var hash = SchemaFingerprintHasher.ComputeColumnNameHash(columnHeaders);
        if (hash is null)
        {
            // Header-less format (XML / EDIFACT / PDF) — nothing to fingerprint in v1.
            _logger.LogDebug("Fingerprint skipped — no column headers for order {OrderId} ({Format})", orderId, detectedFormat);
            return;
        }

        var now = DateTime.UtcNow;
        var existing = await _db.SchemaFingerprints
            .FirstOrDefaultAsync(f => f.OrganisationId == organisationId && f.ColumnNameHash == hash, ct);

        if (existing is not null)
        {
            existing.ParseSuccessCount += 1;
            existing.LastSeenAt = now;
        }
        else
        {
            _db.SchemaFingerprints.Add(new SchemaFingerprint
            {
                Id = Guid.NewGuid(),
                OrganisationId = organisationId,
                ColumnNameHash = hash,
                DetectedFormat = detectedFormat,
                SampleSupplierName = order.Supplier?.Name,
                ParseSuccessCount = 1,
                LastSeenAt = now,
                CreatedAt = now,
            });
        }

        // Persist the guard hash atomically with the increment so a retry cannot double-count.
        order.SchemaFingerprintHash = hash;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent Hangfire worker for a different order with the same column layout
            // won the INSERT race (Postgres 23505). Detach the stale Added entity, reload the
            // winner's row, increment its count, and re-save. The order's SchemaFingerprintHash
            // is still marked Modified in the tracker from before the failed save.
            await RecoverFromConcurrentInsertAsync(organisationId, orderId, hash, now, ct);
            return;
        }

        _logger.LogInformation(
            "Recorded schema fingerprint for order {OrderId} (org {OrgId}): hash {Hash} now seen {Count} time(s)",
            orderId, organisationId, hash, existing?.ParseSuccessCount ?? 1);
    }

    private async Task RecoverFromConcurrentInsertAsync(
        Guid organisationId, Guid orderId, string hash, DateTime now, CancellationToken ct)
    {
        var stale = _db.ChangeTracker.Entries<SchemaFingerprint>()
            .FirstOrDefault(e => e.State == EntityState.Added);
        if (stale is not null) stale.State = EntityState.Detached;

        var winner = await _db.SchemaFingerprints
            .FirstOrDefaultAsync(f => f.OrganisationId == organisationId && f.ColumnNameHash == hash, ct);

        if (winner is null)
        {
            _logger.LogWarning(
                "Fingerprint race recovery aborted — no existing row found for org {OrgId} hash {Hash}",
                organisationId, hash);
            return;
        }

        winner.ParseSuccessCount += 1;
        winner.LastSeenAt = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Race recovery: schema fingerprint for order {OrderId} (org {OrgId}): hash {Hash} now seen {Count} time(s)",
            orderId, organisationId, hash, winner.ParseSuccessCount);
    }

    public async Task<SchemaFingerprintMatch?> LookupAsync(
        Guid organisationId, IReadOnlyList<string>? columnHeaders, CancellationToken ct)
    {
        var hash = SchemaFingerprintHasher.ComputeColumnNameHash(columnHeaders);
        if (hash is null) return null;

        var fp = await _db.SchemaFingerprints
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.OrganisationId == organisationId && f.ColumnNameHash == hash, ct);

        return fp is null
            ? null
            : new SchemaFingerprintMatch(fp.ColumnNameHash, fp.ParseSuccessCount, fp.SampleSupplierName, fp.DetectedFormat);
    }
}
