using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IDataErasureService"/>. Org-scoped per-order hard erase:
/// the sensitive R2 blobs (PO source + transformed artifacts + any order-confirmation
/// source) are deleted first, then every order-tied DB row in a single SaveChanges.
/// R2 deletes are best-effort (idempotent on a missing key; a transient failure is
/// logged, not fatal). Tenant isolation: the parent order is loaded with an OrgId
/// filter, so every child delete is scoped to that verified order.
///
/// FK note: order_confirmation_lines.purchase_order_line_id is ON DELETE RESTRICT, so
/// confirmation lines + confirmations are removed in the same SaveChanges as (and EF
/// orders them before) the purchase_order_lines they reference — otherwise Postgres
/// would abort the erase.
/// </summary>
public sealed class DataErasureService : IDataErasureService
{
    private readonly ProcuLinkDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ILogger<DataErasureService> _logger;

    public DataErasureService(
        ProcuLinkDbContext db,
        IFileStorageService storage,
        ILogger<DataErasureService> logger)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
    }

    public async Task<OrderErasureResult> EraseOrderAsync(Guid organisationId, Guid orderId, CancellationToken ct)
    {
        // Org-scoped load — never erase across tenants. Unknown/already-erased = no-op.
        var order = await _db.PurchaseOrders
            .FirstOrDefaultAsync(o => o.Id == orderId && o.OrgId == organisationId, ct);
        if (order is null)
        {
            _logger.LogInformation(
                "Erase order {OrderId} (org {OrgId}): not found — no-op.", orderId, organisationId);
            return new OrderErasureResult(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var artifacts = await _db.OutboundArtifacts
            .Where(a => a.OrderId == orderId && a.OrgId == organisationId).ToListAsync(ct);
        var lines = await _db.PurchaseOrderLines
            .Where(l => l.OrderId == orderId).ToListAsync(ct);
        var attempts = await _db.DeliveryAttempts
            .Where(a => a.OrderId == orderId && a.OrgId == organisationId).ToListAsync(ct);
        var exceptions = await _db.OrderExceptions
            .Where(e => e.OrderId == orderId && e.OrgId == organisationId).ToListAsync(ct);
        var validations = await _db.OrderValidationResults
            .Where(v => v.OrderId == orderId && v.OrgId == organisationId).ToListAsync(ct);
        var passport = await _db.PoPassportEvents
            .Where(p => p.OrderId == orderId && p.OrgId == organisationId).ToListAsync(ct);
        // The order's audit events are keyed by EntityId == orderId (Guids are unique,
        // so this never matches another entity), org-scoped for defence in depth.
        var audits = await _db.AuditEvents
            .Where(e => e.EntityId == orderId && e.OrgId == organisationId).ToListAsync(ct);
        // Order confirmations (inbound supplier confirmations) + their lines are tied to
        // this order and hold sensitive PO content (item codes/qty/price/notes + their own
        // R2 source). Lines have a RESTRICT FK onto purchase_order_lines, so they MUST go.
        var confirmations = await _db.OrderConfirmations
            .Where(c => c.PurchaseOrderId == orderId && c.OrgId == organisationId).ToListAsync(ct);
        var confirmationIds = confirmations.Select(c => c.Id).ToList();
        var confirmationLines = await _db.OrderConfirmationLines
            .Where(cl => cl.OrgId == organisationId && confirmationIds.Contains(cl.OrderConfirmationId))
            .ToListAsync(ct);

        // ── 1. Delete the sensitive R2 blobs first (idempotent; best-effort) ──────
        var r2Keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(order.SourceFileKey)) r2Keys.Add(order.SourceFileKey);
        r2Keys.AddRange(artifacts.Select(a => a.FileKey).Where(k => !string.IsNullOrWhiteSpace(k)));
        r2Keys.AddRange(confirmations.Where(c => !string.IsNullOrWhiteSpace(c.SourceFileKey)).Select(c => c.SourceFileKey!));

        var r2Deleted = 0;
        foreach (var key in r2Keys.Distinct())
        {
            try
            {
                await _storage.DeleteAsync(key, ct);
                r2Deleted++;
            }
            catch (Exception ex)
            {
                // Don't fail the whole erase on a single storage hiccup — the DB rows
                // still go; surface the orphaned key for manual/sweep cleanup.
                _logger.LogError(ex,
                    "Erase order {OrderId} (org {OrgId}): failed to delete R2 key {Key}.",
                    orderId, organisationId, key);
            }
        }

        // ── 2. Delete DB rows. Confirmation lines/headers commit FIRST: their
        // purchase_order_line_id FK is ON DELETE RESTRICT, so they must be gone
        // before the purchase_order_lines they reference. A separate SaveChanges
        // guarantees that order regardless of how EF models the FK. ───────────────
        _db.OrderConfirmationLines.RemoveRange(confirmationLines);
        _db.OrderConfirmations.RemoveRange(confirmations);
        if (confirmationLines.Count > 0 || confirmations.Count > 0)
            await _db.SaveChangesAsync(ct);

        _db.PurchaseOrderLines.RemoveRange(lines);
        _db.OutboundArtifacts.RemoveRange(artifacts);
        _db.DeliveryAttempts.RemoveRange(attempts);
        _db.OrderExceptions.RemoveRange(exceptions);
        _db.OrderValidationResults.RemoveRange(validations);
        _db.PoPassportEvents.RemoveRange(passport);
        _db.AuditEvents.RemoveRange(audits);
        _db.PurchaseOrders.Remove(order);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Erased order {OrderId} (org {OrgId}): R2={R2} lines={Lines} artifacts={Artifacts} " +
            "attempts={Attempts} exceptions={Exceptions} validations={Validations} passport={Passport} " +
            "audit={Audit} confirmations={Confirmations} confirmationLines={ConfirmationLines}.",
            orderId, organisationId, r2Deleted, lines.Count, artifacts.Count, attempts.Count,
            exceptions.Count, validations.Count, passport.Count, audits.Count,
            confirmations.Count, confirmationLines.Count);

        return new OrderErasureResult(
            Found: true,
            R2ObjectsDeleted: r2Deleted,
            LinesDeleted: lines.Count,
            ArtifactsDeleted: artifacts.Count,
            DeliveryAttemptsDeleted: attempts.Count,
            ExceptionsDeleted: exceptions.Count,
            ValidationResultsDeleted: validations.Count,
            PassportEventsDeleted: passport.Count,
            AuditEventsDeleted: audits.Count,
            ConfirmationsDeleted: confirmations.Count,
            ConfirmationLinesDeleted: confirmationLines.Count);
    }
}
