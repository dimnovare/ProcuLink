using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// Internal sub-service of <see cref="OrderService"/> owning read-only order queries
/// and signed-download URL generation. Methods moved verbatim from the original
/// God-class (audit W1/B1 decomposition).
/// </summary>
internal sealed class OrderQueryService
{
    private readonly ProcuLinkDbContext  _db;
    private readonly IFileStorageService _fileStorage;

    public OrderQueryService(
        ProcuLinkDbContext  db,
        IFileStorageService fileStorage)
    {
        _db          = db;
        _fileStorage = fileStorage;
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    public async Task<Result<PurchaseOrderEntity>> GetByIdAsync(
        Guid organisationId, Guid orderId, CancellationToken ct)
    {
        var entity = await _db.PurchaseOrders
            .AsNoTracking()
            .Include(x => x.Lines)
            .Include(x => x.Supplier)
            .Include(x => x.OutboundArtifacts)
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<PurchaseOrderEntity>.Fail("Order not found.");

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── ListPagedAsync ────────────────────────────────────────────────────────

    public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount, int SampleCount)>> ListPagedAsync(
        Guid      organisationId,
        int       page,
        int       pageSize,
        string?   status,
        Guid?     supplierId,
        string?   search,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken ct)
        // Page/pageSize is just a window: skip whole pages, take one page. The offset/limit
        // primitive does the real work so both entry points share one code path.
        => ListWindowAsync(
            organisationId,
            skip: (Math.Max(1, page) - 1) * pageSize,
            take: pageSize,
            status, supplierId, search, dateFrom, dateTo, ct);

    // ── ListWindowAsync ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns one window of the org's orders plus TWO counts over the SAME filtered population:
    /// <c>TotalCount</c> (every row this filter matches) and <c>SampleCount</c> (how many of those
    /// are onboarding practice orders).
    ///
    /// <para><b>Why the second count exists.</b> This list deliberately RETURNS practice orders,
    /// but every count the product reports excludes them — billing quota
    /// (<c>StripeBillingService.CountOrdersAsync</c>), the dashboard KPIs and per-status summary
    /// (<c>DashboardController</c>), and the onboarding milestones (<c>OnboardingController</c>)
    /// all filter <c>!o.IsSample</c>. Returning a bare <c>TotalCount</c> therefore described a
    /// DIFFERENT population from every other number on the screen, with nothing saying so: a
    /// first-run org whose only order was the promoted sample read "Received 0" beside a card
    /// saying "1 orders received" and a table listing that order.
    ///
    /// <c>TotalCount - SampleCount</c> is the metered population, so it reconciles with
    /// <c>OrdersSummaryDto.Total</c> by construction, and <c>SampleCount</c> is the label that
    /// lets a caller explain the difference instead of contradicting itself.</para>
    /// </summary>
    public async Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount, int SampleCount)>> ListWindowAsync(
        Guid      organisationId,
        int       skip,
        int       take,
        string?   status,
        Guid?     supplierId,
        string?   search,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken ct)
    {
        // Defensive clamps — the caller normally clamps, but never trust a raw window.
        skip = Math.Max(0, skip);
        take = Math.Max(0, take);

        // ── Step 1: build base query with SQL-filterable predicates ────────────
        // Org scope is mandatory on every query — never omit.
        var baseQuery = _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == organisationId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            // The UI renders all five failure statuses as one red "Failed" pill, so a
            // status=failed filter must match the whole failure bucket — not just the
            // literal "failed" status (which would silently drop transform_failed,
            // delivery_failed, delivery_dead_letter, and rejected_by_supplier). Every
            // other status stays an exact match.
            if (status == OrderStatusConstants.Failed)
            {
                var failureBucket = OrderStatusConstants.FailureBucket.ToArray();
                baseQuery = baseQuery.Where(o => failureBucket.Contains(o.Status));
            }
            else
            {
                baseQuery = baseQuery.Where(o => o.Status == status);
            }
        }

        if (supplierId.HasValue)
            baseQuery = baseQuery.Where(o => o.SupplierId == supplierId.Value);

        if (dateFrom.HasValue)
            baseQuery = baseQuery.Where(o => o.CreatedAt >= dateFrom.Value);

        if (dateTo.HasValue)
            baseQuery = baseQuery.Where(o => o.CreatedAt <= dateTo.Value);

        // ── Step 2 (new): SQL-native search predicate ────────────────────────────
        // SCALE-GATED CONSTRAINT: the LEADING-wildcard ILIKE ('%term%') cannot use a
        // B-tree index, so this search is a sequential scan over the org's orders.
        // It is fine at the ICP scale (≤ a few thousand orders/org) and is already
        // org-scoped + paginated, but it will get slow once an org accumulates large
        // order history. REVISIT past ~50k orders/org: add a pg_trgm GIN index on
        // (po_number, buyer_name) [+ supplier name], or a prefix index if we can drop
        // the leading wildcard. See docs/audit/2026-06-12-scale-gated-constraints.md.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            baseQuery = baseQuery.Where(o =>
                EF.Functions.ILike(o.PoNumber, $"%{trimmedSearch}%") ||
                EF.Functions.ILike(o.Supplier!.Name, $"%{trimmedSearch}%") ||
                (o.BuyerName != null && EF.Functions.ILike(o.BuyerName, $"%{trimmedSearch}%")));
        }

        // ── Step 3: total + practice-order counts from SQL (no full-table load) ─
        // ONE round trip for both numbers: grouping on the bool splits the same filtered
        // population into practice / real, so SampleCount can never be computed over a
        // different set of predicates than TotalCount — which is the whole defect.
        var populationRows = await baseQuery
            .GroupBy(o => o.IsSample)
            .Select(g => new { IsSample = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var totalCount  = populationRows.Sum(r => r.Count);
        var sampleCount = populationRows.Where(r => r.IsSample).Sum(r => r.Count);

        if (totalCount == 0)
            return Result<(IReadOnlyList<PurchaseOrderSummary>, int, int)>.Ok(
                (Array.Empty<PurchaseOrderSummary>(), 0, 0));

        // ── Step 4: paginate in SQL, select minimal columns ────────────────────
        // CreatedAt DESC is the user-visible order, but it is NOT unique: a large bulk API
        // ingest can stamp many orders with the same CreatedAt. SQL gives no ordering
        // guarantee for rows with equal sort keys, so Skip/Take over a CreatedAt-only sort
        // can let adjacent windows overlap and drop rows (plan/scan/concurrency dependent).
        // The Id DESC tiebreaker makes the sort total → every window is disjoint and the
        // union of all windows covers the full set exactly once, deterministically.
        var paged = await baseQuery
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Skip(skip)
            .Take(take)
            .Select(o => new
            {
                o.Id,
                o.PoNumber,
                SupplierName = o.Supplier != null ? o.Supplier.Name : "Unknown Supplier",
                o.BuyerName,
                o.OrderDate,
                o.Status,
                o.CreatedAt,
                o.Currency,
                o.SourceFileKey,
                o.IsSample,
            })
            .ToListAsync(ct);

        // ── Step 5: aggregate line counts for the page subset only ────────────
        var pagedIds = paged.Select(o => o.Id).ToList();

        var lineCounts = await _db.PurchaseOrderLines
            .AsNoTracking()
            .Where(l => pagedIds.Contains(l.OrderId))
            .GroupBy(l => l.OrderId)
            .Select(g => new
            {
                OrderId    = g.Key,
                Total      = g.Count(),
                Unresolved = g.Count(l => l.NeedsReview),
                TotalValue = g.Sum(l => l.Quantity * l.UnitPrice),
            })
            .ToDictionaryAsync(g => g.OrderId, ct);

        // ── Step 6: project to PurchaseOrderSummary ───────────────────────────
        var summaries = paged.Select(o =>
        {
            lineCounts.TryGetValue(o.Id, out var lc);

            string? sourceFormat = null;
            if (!string.IsNullOrEmpty(o.SourceFileKey))
            {
                var ext = System.IO.Path.GetExtension(o.SourceFileKey).TrimStart('.').ToLowerInvariant();
                sourceFormat = ext switch
                {
                    "pdf"           => "pdf",
                    "csv"           => "csv",
                    "xlsx" or "xls" => "xlsx",
                    "xml" or "cxml" => "cxml",
                    "edi" or "x12"  => "edi",
                    _               => null,
                };
            }

            return new PurchaseOrderSummary(
                o.Id,
                o.PoNumber,
                o.SupplierName,
                o.BuyerName,
                o.OrderDate,
                o.Status,
                lc?.Total      ?? 0,
                lc?.Unresolved ?? 0,
                lc?.TotalValue ?? 0m,
                o.Currency,
                sourceFormat,
                o.CreatedAt,
                o.IsSample);
        }).ToList();

        return Result<(IReadOnlyList<PurchaseOrderSummary>, int, int)>.Ok(
            ((IReadOnlyList<PurchaseOrderSummary>)summaries, totalCount, sampleCount));
    }

    // ── GetDownloadUrlAsync ───────────────────────────────────────────────────

    public async Task<Result<DownloadUrl>> GetDownloadUrlAsync(
        Guid organisationId,
        Guid orderId,
        Guid artifactId,
        CancellationToken ct)
    {
        // Scope the lookup to the org via the order — prevents cross-tenant access
        var artifact = await _db.OutboundArtifacts
            .AsNoTracking()
            .Where(a => a.Id == artifactId
                     && a.OrderId == orderId
                     && a.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (artifact is null)
            return Result<DownloadUrl>.Fail("Artifact not found.");

        // Blob-retention honesty: the row survives a retention purge but the blob is gone.
        // Surface the EXACT marker error so the controller maps it to 410 Gone with an
        // honest explanation instead of handing out a signed URL to a missing object.
        if (artifact.BlobPurgedAt is not null)
            return Result<DownloadUrl>.Fail(RetentionConstants.BlobPurgedError);

        var expiry    = TimeSpan.FromMinutes(15);
        var url       = await _fileStorage.GetSignedDownloadUrlAsync(artifact.FileKey, expiry, ct);
        var expiresAt = DateTime.UtcNow + expiry;

        return Result<DownloadUrl>.Ok(new DownloadUrl(url, expiresAt));
    }
}
