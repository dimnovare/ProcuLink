using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly ProcuLinkDbContext    _db;
    private readonly ICurrentTenantService _tenant;

    public DashboardController(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    // ── GET /api/dashboard/stats ──────────────────────────────────────────────

    [HttpGet("stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var orgId     = _tenant.OrganisationId;
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var totalThisMonth = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && o.CreatedAt >= monthStart, ct);

        var pendingReview = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && o.Status == OrderStatusConstants.PendingReview, ct);

        var delivered = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && o.Status == OrderStatusConstants.Delivered, ct);

        var totalOrders = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId, ct);

        return Ok(new { totalOrdersThisMonth = totalThisMonth, pendingReview, delivered, totalOrders });
    }

    // ── GET /api/orders/summary ───────────────────────────────────────────────
    // Per-status order counts — used by sidebar badge, notifications bell,
    // and dashboard "urgent exceptions" KPI. SQL GROUP BY, no line data loaded.

    [HttpGet("/api/orders/summary")]
    [ProducesResponseType(typeof(OrdersSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        var rows = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == orgId)
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byStatus = rows.ToDictionary(r => r.Status, r => r.Count);
        var total    = rows.Sum(r => r.Count);

        return Ok(new OrdersSummaryDto(byStatus, total));
    }

    // ── GET /api/dashboard/topology ──────────────────────────────────────────
    // Buyer → supplier wire topology derived from all org orders.
    // Buyer name lives in canonical_json (jsonb) — loaded in-memory per the
    // same pattern used by OrderService.ListPagedAsync.

    private static readonly HashSet<string> FailedStatuses = new(StringComparer.Ordinal)
    {
        OrderStatusConstants.Failed, OrderStatusConstants.DeliveryFailed,
        OrderStatusConstants.TransformFailed, OrderStatusConstants.DeliveryDeadLetter,
    };

    private static readonly HashSet<string> ExceptionStatuses = new(StringComparer.Ordinal)
    {
        OrderStatusConstants.PendingReview, OrderStatusConstants.Failed,
        OrderStatusConstants.DeliveryFailed, OrderStatusConstants.TransformFailed,
        OrderStatusConstants.DeliveryDeadLetter, OrderStatusConstants.RejectedBySupplier,
    };

    [HttpGet("topology")]
    [ProducesResponseType(typeof(DashboardTopologyDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTopology(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        // Supplier "health" is presented to the user as an acceptance rate over the
        // LAST 30 DAYS, so the figure must only count orders inside that window —
        // otherwise an all-time average silently contradicts its own label. UTC to
        // match the clock source used elsewhere in this controller (GetStats).
        var supplierHealthCutoff = DateTime.UtcNow.AddDays(-30);

        var rows = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == orgId)
            .Select(o => new
            {
                o.Id,
                o.SupplierId,
                SupplierName = o.Supplier != null ? o.Supplier.Name : "Unknown",
                o.Status,
                o.CreatedAt,
                o.CanonicalJson,
            })
            .ToListAsync(ct);

        // supMap keyed by SupplierId (GUID string) — stable, unique, collision-free
        var supMap  = new Dictionary<string, (string Id, string Name, int Total, int Failed, int Exceptions)>(StringComparer.Ordinal);
        var buyMap  = new Dictionary<string, (string Id, string Name, int Total)>(StringComparer.OrdinalIgnoreCase);
        // wireMap key: "{buyerKey}|||{supplierId}"
        var wireMap = new Dictionary<string, (string BuyerKey, string SupplierKey, int Total, int Failed, int Exceptions)>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var isFailed    = FailedStatuses.Contains(row.Status);
            var isException = ExceptionStatuses.Contains(row.Status);

            // Supplier health is a "last 30 days" acceptance rate — only orders inside
            // that window feed its Total/Failed counts. Buyer + wire aggregations below
            // stay all-time (their labels do not claim a window), so this filter is
            // scoped to supMap only.
            var sk = row.SupplierId.ToString(); // stable GUID string
            if (row.CreatedAt >= supplierHealthCutoff)
            {
                if (!supMap.TryGetValue(sk, out var sa))
                    sa = (sk, row.SupplierName.Trim(), 0, 0, 0);
                supMap[sk] = sa with
                {
                    Total      = sa.Total + 1,
                    Failed     = sa.Failed + (isFailed ? 1 : 0),
                    Exceptions = sa.Exceptions + (isException ? 1 : 0),
                };
            }

            string? buyerName = null;
            if (row.CanonicalJson is not null)
            {
                try
                {
                    var root = row.CanonicalJson.RootElement;
                    if (root.TryGetProperty("buyerName", out var el))
                        buyerName = el.GetString();
                    else if (root.TryGetProperty("BuyerName", out var el2))
                        buyerName = el2.GetString();
                }
                catch { /* malformed json */ }
            }
            if (string.IsNullOrWhiteSpace(buyerName)) continue;

            var bk = buyerName.Trim().ToLowerInvariant();
            if (!buyMap.TryGetValue(bk, out var ba))
                ba = ($"buy-{bk}", buyerName.Trim(), 0);
            buyMap[bk] = ba with { Total = ba.Total + 1 };

            var wk = $"{bk}|||{sk}";
            if (!wireMap.TryGetValue(wk, out var wa))
                wa = (bk, sk, 0, 0, 0);
            wireMap[wk] = wa with
            {
                Total      = wa.Total + 1,
                Failed     = wa.Failed + (isFailed ? 1 : 0),
                Exceptions = wa.Exceptions + (isException ? 1 : 0),
            };
        }

        var buyers = buyMap.Values
            .OrderByDescending(b => b.Total)
            .Select(b => new TopologyBuyerDto(b.Id, b.Name, CodeFor(b.Name), $"{b.Total} ord"))
            .ToList();

        var suppliers = supMap.Values
            .OrderByDescending(s => s.Total)
            .Select(s => new TopologySupplierDto(
                s.Id, s.Name, CodeFor(s.Name), $"{s.Total} ord",
                s.Total == 0 ? 100 : (int)Math.Round(100.0 * (s.Total - s.Failed) / s.Total)))
            .ToList();

        var buyerIdByKey = buyMap.ToDictionary(kv => kv.Key, kv => kv.Value.Id);

        var wires = wireMap.Values
            .Select(w =>
            {
                if (!buyerIdByKey.TryGetValue(w.BuyerKey, out var buyerId)) return null;
                // w.SupplierKey IS the supplierId (GUID string) — use directly
                var supplierId = w.SupplierKey;
                var health = w.Failed > 0 ? "down" : w.Exceptions > 0 ? "risk" : "ok";
                return new TopologyWireDto(
                    buyerId, supplierId, WeightFor(w.Total), health,
                    w.Exceptions > 0 ? w.Exceptions : null);
            })
            .Where(w => w is not null)
            .ToList();

        return Ok(new DashboardTopologyDto(buyers, suppliers, wires!));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string CodeFor(string name)
    {
        var words = System.Text.RegularExpressions.Regex
            .Replace(name, @"[^A-Za-z0-9 ]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "—";
        var initials = string.Concat(words.Select(w => w[0])).ToUpper();
        var code     = initials.Length >= 3 ? initials : string.Concat(words).ToUpper();
        return code[..Math.Min(3, code.Length)];
    }

    private static int WeightFor(int count) => count switch
    {
        <= 1  => 1,
        <= 2  => 2,
        <= 4  => 3,
        <= 8  => 4,
        <= 16 => 5,
        _     => 6,
    };
}
