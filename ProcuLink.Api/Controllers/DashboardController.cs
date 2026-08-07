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
            .CountAsync(o => o.OrgId == orgId && !o.IsSample && o.CreatedAt >= monthStart, ct);

        var pendingReview = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && !o.IsSample && o.Status == OrderStatusConstants.PendingReview, ct);

        var delivered = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && !o.IsSample && o.Status == OrderStatusConstants.Delivered, ct);

        var totalOrders = await _db.PurchaseOrders
            .CountAsync(o => o.OrgId == orgId && !o.IsSample, ct);

        return Ok(new { totalOrdersThisMonth = totalThisMonth, pendingReview, delivered, totalOrders });
    }

    // ── GET /api/orders/summary ───────────────────────────────────────────────
    // Per-status order counts — used by sidebar badge, notifications bell,
    // and dashboard "urgent exceptions" KPI. SQL GROUP BY, no line data loaded.
    //
    // ByStatus/Total EXCLUDE practice orders, matching billing quota
    // (StripeBillingService.CountOrdersAsync) and the onboarding milestones. But
    // GET /api/orders RETURNS practice-order rows — the user is sent to one to
    // rehearse the review flow — so reporting only the excluded total left the two
    // numbers describing different populations with nothing saying so: a first-run
    // org whose only order was the promoted sample read "Received 0" beside a table
    // listing that order. SampleTotal is the missing label, grouped in the SAME
    // round trip so it can never be computed over a different set of predicates.

    [HttpGet("/api/orders/summary")]
    [ProducesResponseType(typeof(OrdersSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        var rows = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == orgId)
            .GroupBy(o => new { o.IsSample, o.Status })
            .Select(g => new { g.Key.IsSample, Status = g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var real = rows.Where(r => !r.IsSample).ToList();

        var byStatus    = real.ToDictionary(r => r.Status, r => r.Count);
        var total       = real.Sum(r => r.Count);
        var sampleTotal = rows.Where(r => r.IsSample).Sum(r => r.Count);

        return Ok(new OrdersSummaryDto(byStatus, total, sampleTotal));
    }

    // ── GET /api/dashboard/topology ──────────────────────────────────────────
    // Buyer → supplier wire topology derived from all org orders.
    // Buyer name reads the denormalized, indexed buyer_name column (written by
    // all current ingest paths); buyer/supplier/wire counts are aggregated with
    // SQL GROUP BY so no per-order rows — and no canonical_json jsonb blobs —
    // are loaded on this landing-page hot path. Rows that pre-date the
    // Wave2BuyerNameColumn migration (null column) fall back to canonical_json,
    // fetched separately and capped at LegacyBuyerNameFallbackRows.

    // Cap on how many legacy (null buyer_name column) rows are fetched for the
    // canonical_json fallback. Only rows created before the 2026-05-31
    // Wave2BuyerNameColumn migration can hit this path — all current write paths
    // populate the column — so the cap bounds cost without affecting new data.
    private const int LegacyBuyerNameFallbackRows = 1000;

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

        // Supplier health is a "last 30 days" acceptance rate — only orders inside
        // that window feed its Total/Failed counts. Buyer + wire aggregations below
        // stay all-time (their labels do not claim a window), so this filter is
        // scoped to the supplier query only. Aggregated per supplier in SQL.
        var supplierRows = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == orgId && !o.IsSample && o.CreatedAt >= supplierHealthCutoff)
            .Select(o => new
            {
                o.SupplierId,
                SupplierName = o.Supplier != null ? o.Supplier.Name : "Unknown",
                o.Status,
            })
            .GroupBy(x => new { x.SupplierId, x.SupplierName })
            .Select(g => new
            {
                g.Key.SupplierId,
                g.Key.SupplierName,
                Total      = g.Count(),
                Failed     = g.Count(x => FailedStatuses.Contains(x.Status)),
                Exceptions = g.Count(x => ExceptionStatuses.Contains(x.Status)),
            })
            .ToListAsync(ct);

        // Buyer + wire counts (all-time), aggregated per (buyer_name, supplier) pair
        // in SQL over the indexed buyer_name column — no canonical_json loaded.
        var wireRows = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == orgId && !o.IsSample && o.BuyerName != null)
            .GroupBy(o => new { o.BuyerName, o.SupplierId })
            .Select(g => new
            {
                g.Key.BuyerName,
                g.Key.SupplierId,
                Total      = g.Count(),
                Failed     = g.Count(o => FailedStatuses.Contains(o.Status)),
                Exceptions = g.Count(o => ExceptionStatuses.Contains(o.Status)),
            })
            .ToListAsync(ct);

        // Legacy fallback: rows whose buyer_name column is null may still carry the
        // buyer name in canonical_json (pre-column data). Fetched separately, newest
        // first, capped — so the legacy tail can never reintroduce a full-table
        // jsonb scan on the landing page.
        var legacyRows = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == orgId && !o.IsSample && o.BuyerName == null && o.CanonicalJson != null)
            .OrderByDescending(o => o.CreatedAt)
            .Take(LegacyBuyerNameFallbackRows)
            .Select(o => new { o.SupplierId, o.Status, o.CanonicalJson })
            .ToListAsync(ct);

        var buyMap  = new Dictionary<string, (string Id, string Name, int Total)>(StringComparer.OrdinalIgnoreCase);
        // wireMap key: "{buyerKey}|||{supplierId}"
        var wireMap = new Dictionary<string, (string BuyerKey, string SupplierKey, int Total, int Failed, int Exceptions)>(StringComparer.OrdinalIgnoreCase);

        // Postgres groups buyer names case-sensitively; this merge step recombines
        // case variants under the same case-insensitive key, exactly like the
        // previous in-memory loop did.
        void Accumulate(string buyerName, string supplierKey, int total, int failed, int exceptions)
        {
            var bk = buyerName.Trim().ToLowerInvariant();
            if (!buyMap.TryGetValue(bk, out var ba))
                ba = ($"buy-{bk}", buyerName.Trim(), 0);
            buyMap[bk] = ba with { Total = ba.Total + total };

            var wk = $"{bk}|||{supplierKey}";
            if (!wireMap.TryGetValue(wk, out var wa))
                wa = (bk, supplierKey, 0, 0, 0);
            wireMap[wk] = wa with
            {
                Total      = wa.Total + total,
                Failed     = wa.Failed + failed,
                Exceptions = wa.Exceptions + exceptions,
            };
        }

        foreach (var row in wireRows)
        {
            if (string.IsNullOrWhiteSpace(row.BuyerName)) continue; // defensive: write paths trim + nullify
            Accumulate(row.BuyerName, row.SupplierId?.ToString() ?? string.Empty, row.Total, row.Failed, row.Exceptions);
        }

        foreach (var row in legacyRows)
        {
            var buyerName = ExtractBuyerNameFromJson(row.CanonicalJson);
            if (string.IsNullOrWhiteSpace(buyerName)) continue;
            Accumulate(
                buyerName,
                row.SupplierId?.ToString() ?? string.Empty,
                1,
                FailedStatuses.Contains(row.Status) ? 1 : 0,
                ExceptionStatuses.Contains(row.Status) ? 1 : 0);
        }

        var buyers = buyMap.Values
            .OrderByDescending(b => b.Total)
            .Select(b => new TopologyBuyerDto(b.Id, b.Name, CodeFor(b.Name), $"{b.Total} ord"))
            .ToList();

        var suppliers = supplierRows
            .Select(s => (Id: s.SupplierId?.ToString() ?? string.Empty, Name: s.SupplierName.Trim(), s.Total, s.Failed))
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

    /// <summary>
    /// Legacy buyer-name extraction from canonical_json for rows that pre-date the
    /// denormalized buyer_name column. Keys may be "buyerName" (camelCase from
    /// OrderService) or "BuyerName" (PascalCase from older parsers).
    /// </summary>
    private static string? ExtractBuyerNameFromJson(System.Text.Json.JsonDocument? json)
    {
        if (json is null) return null;
        try
        {
            var root = json.RootElement;
            if (root.TryGetProperty("buyerName", out var el))
                return el.GetString();
            if (root.TryGetProperty("BuyerName", out var el2))
                return el2.GetString();
        }
        catch { /* malformed json */ }
        return null;
    }

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
