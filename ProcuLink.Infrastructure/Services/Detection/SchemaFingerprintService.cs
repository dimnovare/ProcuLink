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
        //
        // What the guard protects is the COUNT, not the supplier binding. An order fingerprinted
        // while unrouted bound no supplier because there was none; when the operator later resolves
        // the routing, assign-supplier re-enqueues the parse and we re-enter here with a supplier
        // the layout has never seen. Learn it — that human correction is the only way SupplierIdsCsv
        // ever accumulates a supplier that was not already known at ingest, i.e. the only orders that
        // ever needed routing help. Clearing the hash in assign-supplier instead would re-arm the
        // increment and double-count the layout, so the guard is made supplier-aware here instead.
        if (order.SchemaFingerprintHash is not null)
        {
            await LearnSupplierFromCorrectionAsync(organisationId, orderId, order, ct);
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

        // ── Relational path: count bump is ATOMIC, then the per-order idempotency guard ──────────
        // The previous read-modify-write (`existing.ParseSuccessCount += 1`) silently LOST a bump
        // whenever two parse jobs for DIFFERENT orders with the same column layout ran concurrently
        // — both read the same starting count and the second SaveChanges clobbered the first.
        // ExecuteUpdateAsync issues a single `SET parse_success_count = parse_success_count + 1`,
        // so every concurrent bump lands. Ordering note: the atomic count commit happens before the
        // order-hash guard, so a crash in the (tiny) gap re-enters and bumps again on retry — a rare
        // over-count, which is the safe direction for a "seen N times" heuristic (vs. the prior bug
        // that under-counted on EVERY concurrent parse).
        if (_db.Database.IsRelational())
        {
            var bumped = await _db.SchemaFingerprints
                .Where(f => f.OrganisationId == organisationId && f.ColumnNameHash == hash)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.ParseSuccessCount, f => f.ParseSuccessCount + 1)
                    .SetProperty(f => f.LastSeenAt, now), ct);

            if (bumped == 0)
            {
                // First sighting of this layout — insert it. A concurrent worker may win the
                // (OrgId, ColumnNameHash) unique-insert race (Postgres 23505); fall back to the
                // same atomic +1 against the winner's row.
                _db.SchemaFingerprints.Add(NewFingerprint(organisationId, order, hash, detectedFormat, now));
                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    await RecoverFromConcurrentInsertAsync(organisationId, orderId, hash, now, ct);
                }
            }

            // Phase 1: bind THIS order's supplier to the layout (tracked read-modify-write; the COUNT
            // was already handled atomically above). Lost set-updates are the safe direction.
            var fpRow = await _db.SchemaFingerprints
                .FirstOrDefaultAsync(f => f.OrganisationId == organisationId && f.ColumnNameHash == hash, ct);
            if (fpRow is not null) BindSupplier(fpRow, order.SupplierId);

            // Persist the per-order guard hash so a Hangfire retry of THIS order short-circuits.
            order.SchemaFingerprintHash = hash;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Recorded schema fingerprint for order {OrderId} (org {OrgId}): hash {Hash} (atomic)",
                orderId, organisationId, hash);
            return;
        }

        // ── Non-relational (EF InMemory) test provider ───────────────────────────────────────────
        // ExecuteUpdateAsync is not translatable and the tests are single-threaded, so a tracked
        // read-modify-write paired with the order-hash guard in one SaveChanges is correct here.
        // The concurrent-insert recovery is still exercised by the RaceSimulatingDbContext test.
        var existing = await _db.SchemaFingerprints
            .FirstOrDefaultAsync(f => f.OrganisationId == organisationId && f.ColumnNameHash == hash, ct);
        if (existing is not null)
        {
            existing.ParseSuccessCount += 1;
            existing.LastSeenAt = now;
            BindSupplier(existing, order.SupplierId);   // Phase 1: bind this order's supplier (insert path sets it in NewFingerprint)
        }
        else
        {
            _db.SchemaFingerprints.Add(NewFingerprint(organisationId, order, hash, detectedFormat, now));
        }

        order.SchemaFingerprintHash = hash;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent worker won the (OrgId, ColumnNameHash) insert race. Detach the stale
            // Added entity, increment the winner's row, and re-save (order hash is still tracked).
            await RecoverFromConcurrentInsertViaTrackerAsync(organisationId, orderId, hash, now, ct);
        }

        _logger.LogInformation(
            "Recorded schema fingerprint for order {OrderId} (org {OrgId}): hash {Hash} now seen {Count} time(s)",
            orderId, organisationId, hash, existing?.ParseSuccessCount ?? 1);
    }

    /// <summary>
    /// Re-entry path for an order that was already fingerprinted (and therefore already counted):
    /// bind a supplier this layout does not know yet. No-ops when the order still has no supplier,
    /// when the layout row is gone, or when the supplier is already bound. <c>ParseSuccessCount</c>
    /// and <c>LastSeenAt</c> are deliberately left alone — the same document being re-parsed is not
    /// a new sighting of the layout. A lost set-update under concurrency is the safe direction here,
    /// same as the first-parse binding above.
    /// </summary>
    private async Task LearnSupplierFromCorrectionAsync(
        Guid organisationId, Guid orderId, PurchaseOrderEntity order, CancellationToken ct)
    {
        if (order.SupplierId is not { } supplierId || supplierId == Guid.Empty)
        {
            _logger.LogDebug("Fingerprint skipped — order {OrderId} already fingerprinted", orderId);
            return;
        }

        var fpRow = await _db.SchemaFingerprints.FirstOrDefaultAsync(
            f => f.OrganisationId == organisationId && f.ColumnNameHash == order.SchemaFingerprintHash, ct);

        if (fpRow is null || !BindSupplier(fpRow, supplierId))
        {
            _logger.LogDebug("Fingerprint skipped — order {OrderId} already fingerprinted", orderId);
            return;
        }

        // A layout first seen unrouted has no sample name; the correction is what supplies one.
        fpRow.SampleSupplierName ??= order.Supplier?.Name;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Learned supplier {SupplierId} for layout {Hash} from order {OrderId} (org {OrgId}) — count unchanged",
            supplierId, order.SchemaFingerprintHash, orderId, organisationId);
    }

    private static SchemaFingerprint NewFingerprint(
        Guid organisationId, PurchaseOrderEntity order, string hash, string detectedFormat, DateTime now) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrganisationId = organisationId,
            ColumnNameHash = hash,
            DetectedFormat = detectedFormat,
            SampleSupplierName = order.Supplier?.Name,
            SupplierIdsCsv = order.SupplierId is { } sid && sid != Guid.Empty ? sid.ToString() : string.Empty,
            ParseSuccessCount = 1,
            LastSeenAt = now,
            CreatedAt = now,
        };

    /// <summary>Adds <paramref name="supplierId"/> to the tracked row's supplier set; returns true if it
    /// was newly added (caller's SaveChanges persists it). Null/Empty/duplicate → no change (an unrouted
    /// order has no supplier to bind — the layout is still recorded, just without a supplier).</summary>
    private static bool BindSupplier(SchemaFingerprint row, Guid? supplierId)
    {
        if (supplierId is not { } sid || sid == Guid.Empty) return false;
        var ids = ParseSupplierIds(row.SupplierIdsCsv);
        if (ids.Contains(sid)) return false;
        row.SupplierIdsCsv = string.IsNullOrEmpty(row.SupplierIdsCsv)
            ? sid.ToString()
            : $"{row.SupplierIdsCsv},{sid}";
        return true;
    }

    /// <summary>Parse a supplier-id CSV into a de-duplicated, order-preserving list (tolerant of junk).</summary>
    internal static List<Guid> ParseSupplierIds(string? csv)
    {
        var list = new List<Guid>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Guid.TryParse(part, out var g) && !list.Contains(g))
                list.Add(g);
        return list;
    }

    private async Task RecoverFromConcurrentInsertAsync(
        Guid organisationId, Guid orderId, string hash, DateTime now, CancellationToken ct)
    {
        var stale = _db.ChangeTracker.Entries<SchemaFingerprint>()
            .FirstOrDefault(e => e.State == EntityState.Added);
        if (stale is not null) stale.State = EntityState.Detached;

        // Atomic +1 against the winner's row — a plain read-modify-write here could itself lose a
        // third concurrent worker's increment.
        var recovered = await _db.SchemaFingerprints
            .Where(f => f.OrganisationId == organisationId && f.ColumnNameHash == hash)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.ParseSuccessCount, f => f.ParseSuccessCount + 1)
                .SetProperty(f => f.LastSeenAt, now), ct);

        if (recovered == 0)
        {
            _logger.LogWarning(
                "Fingerprint race recovery aborted — no existing row found for org {OrgId} hash {Hash}",
                organisationId, hash);
            return;
        }

        _logger.LogInformation(
            "Race recovery: schema fingerprint for order {OrderId} (org {OrgId}): hash {Hash} (atomic +1)",
            orderId, organisationId, hash);
    }

    /// <summary>
    /// InMemory-provider recovery for the simulated concurrent-insert race: ExecuteUpdateAsync is
    /// not translatable there, so increment the winner's tracked row instead. Single-threaded tests
    /// only — no lost-update concern.
    /// </summary>
    private async Task RecoverFromConcurrentInsertViaTrackerAsync(
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
            "Race recovery (tracked): schema fingerprint for order {OrderId} (org {OrgId}): hash {Hash} now seen {Count} time(s)",
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
            : new SchemaFingerprintMatch(
                fp.ColumnNameHash, fp.ParseSuccessCount, fp.SampleSupplierName, fp.DetectedFormat,
                ParseSupplierIds(fp.SupplierIdsCsv));
    }
}
