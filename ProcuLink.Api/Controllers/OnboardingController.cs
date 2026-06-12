using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/onboarding")]
public class OnboardingController : ControllerBase
{
    /// <summary>
    /// Statuses where the order is parked waiting for a HUMAN action (resolve codes,
    /// send, or fix a failed delivery) — the deep-link targets of checklist steps 4/6.
    /// Transient pipeline states (parsing/transforming/delivering) and terminal
    /// successes are excluded; "failed" is excluded because the dead-letter/ops view
    /// owns that recovery path, not the onboarding checklist.
    /// </summary>
    private static readonly string[] ActionableStatuses =
        { "pending_review", "ready", "ready_to_deliver", "delivery_failed" };

    private readonly ProcuLinkDbContext    _db;
    private readonly ICurrentTenantService _tenant;

    public OnboardingController(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    // ── GET /api/onboarding/status ────────────────────────────────────────────

    /// <summary>
    /// Returns whether the org has completed key onboarding milestones.
    /// Used by the dashboard checklist to determine what to show.
    ///
    /// Every flag/count EXCLUDES sample data (IsSample suppliers/orders): running the
    /// sample order must never "complete" onboarding with zero real data. Slight
    /// staleness is fine — the frontend polls this with a ~30s staleTime.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(OnboardingStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        // ── Suppliers (non-sample, non-deleted) ───────────────────────────────
        var supplierCount = await _db.Suppliers
            .CountAsync(s => s.OrgId == orgId && s.DeletedAt == null && !s.IsSample, ct);

        // "First" = oldest CreatedAt (the supplier the user set up first), Id tiebreaker
        // so ties are deterministic (same lesson as the orders-list Skip/Take tiebreaker).
        var firstSupplierId = await _db.Suppliers
            .Where(s => s.OrgId == orgId && s.DeletedAt == null && !s.IsSample)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        // ── Orders (non-sample) ───────────────────────────────────────────────
        var orderCount = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && !o.IsSample, ct);

        var deliveredCount = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && !o.IsSample && o.Status == "delivered", ct);

        // Deterministic choice, documented: the OLDEST non-sample order sitting in a
        // human-actionable status (FIFO — the one an operator should clear first),
        // with an Id tiebreaker for equal CreatedAt (bulk-ingest ties).
        var firstActionableOrderId = await _db.PurchaseOrders
            .Where(o => o.OrgId == orgId && !o.IsSample && ActionableStatuses.Contains(o.Status))
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(ct);

        var hasResolvedMapping = await _db.PurchaseOrderLines
            .AnyAsync(l => l.Order.OrgId == orgId && !l.Order.IsSample
                        && l.SupplierItemCode != null, ct);

        // ── Per-supplier configuration (joined to non-sample, non-deleted suppliers) ──
        var hasCatalog = await _db.SupplierProducts
            .AnyAsync(p => p.OrgId == orgId
                        && p.Supplier.DeletedAt == null && !p.Supplier.IsSample, ct);

        var hasItemMappings = await _db.ItemMappings
            .AnyAsync(m => m.OrgId == orgId
                        && m.Supplier.DeletedAt == null && !m.Supplier.IsSample, ct);

        var hasDeliveryConfig = await _db.SupplierDeliveryConfigs
            .AnyAsync(c => c.OrgId == orgId
                        && c.Supplier.DeletedAt == null && !c.Supplier.IsSample, ct);

        // Test-fire rows are the OrderId == null / Status == "success" convention written
        // by DeliveryService.TestFireAsync — a real DeliveryAttempt against the user's
        // real endpoint, which is exactly what checklist step 5 certifies.
        var hasTestFired = await _db.DeliveryAttempts
            .AnyAsync(a => a.OrgId == orgId && a.OrderId == null && a.Status == "success", ct);

        // ── Sample pointer (the one place samples ARE surfaced) ──────────────
        // Most recent sample order, so the "resume the practice order" CTA targets
        // the freshest run when the user started the sample more than once.
        var sampleOrderId = await _db.PurchaseOrders
            .Where(o => o.OrgId == orgId && o.IsSample)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(ct);

        return Ok(new OnboardingStatusDto(
            HasSupplier:            supplierCount > 0,
            HasUpload:              orderCount > 0,
            HasResolvedMapping:     hasResolvedMapping,
            HasDelivery:            deliveredCount > 0,
            HasCatalog:             hasCatalog,
            HasItemMappings:        hasItemMappings,
            HasDeliveryConfig:      hasDeliveryConfig,
            HasTestFired:           hasTestFired,
            FirstSupplierId:        firstSupplierId,
            FirstActionableOrderId: firstActionableOrderId,
            SupplierCount:          supplierCount,
            OrderCount:             orderCount,
            DeliveredCount:         deliveredCount,
            HasSampleOrder:         sampleOrderId != null,
            SampleOrderId:          sampleOrderId));
    }
}
