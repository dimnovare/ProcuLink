using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire background job: parses the source file for a newly created order stub
/// and updates order lines + status.  Idempotent — safe to retry on transient failure.
/// </summary>
public class ParseOrderJob
{
    private readonly IOrderService             _orderService;
    private readonly ILogger<ParseOrderJob>    _logger;
    private readonly ProcuLinkDbContext        _db;
    private readonly IAnalyticsService         _analytics;
    private readonly ISchemaFingerprintService _fingerprints;
    private readonly ISupplierSchemaMappingService _supplierSchemaMappings;

    public ParseOrderJob(
        IOrderService orderService,
        ILogger<ParseOrderJob> logger,
        ProcuLinkDbContext db,
        IAnalyticsService analytics,
        ISchemaFingerprintService fingerprints,
        ISupplierSchemaMappingService supplierSchemaMappings)
    {
        _orderService           = orderService;
        _logger                 = logger;
        _db                     = db;
        _analytics              = analytics;
        _fingerprints           = fingerprints;
        _supplierSchemaMappings = supplierSchemaMappings;
    }

    /// <summary>
    /// Entry point called by Hangfire.
    /// </summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 5, 30, 120 })]
    public async Task ExecuteAsync(Guid orderId, Guid organisationId, CancellationToken ct)
    {
        _logger.LogInformation("ParseOrderJob starting for order {OrderId}", orderId);

        var result = await _orderService.ParseStoredFileAsync(organisationId, orderId, ct);

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "ParseOrderJob failed for order {OrderId}: {Error}",
                orderId, result.Error);

            // Throwing causes Hangfire to retry; once retries are exhausted it
            // moves to the failed queue. The service itself already set status="failed".
            throw new InvalidOperationException($"Parse failed: {result.Error}");
        }

        _logger.LogInformation(
            "ParseOrderJob completed for order {OrderId}, new status={Status}",
            orderId, result.Value!.Entity.Status);

        // ── First-upload-parsed analytics emission ────────────────────────────
        // Check whether any OTHER parsed order existed for this org. The current
        // order is already persisted with its parsed status by OrderService, so
        // we exclude it by id. This also naturally prevents re-firing on
        // Hangfire retries — once another order is parsed, the AnyAsync is true.
        var hadOtherParsedOrders = await _db.PurchaseOrders
            .AsNoTracking()
            .AnyAsync(o => o.OrgId == organisationId
                        && o.Id != orderId
                        && o.Status != OrderStatusConstants.Parsing
                        && o.Status != OrderStatusConstants.PendingParse
                        && o.Status != OrderStatusConstants.Failed, ct);

        if (!hadOtherParsedOrders)
        {
            var order = await _db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == orderId && o.OrgId == organisationId)
                .Select(o => new { o.SourceFileKey })
                .FirstOrDefaultAsync(ct);

            var parser = "unknown";
            if (order?.SourceFileKey is string key && !string.IsNullOrWhiteSpace(key))
            {
                var ext = Path.GetExtension(key).TrimStart('.').ToLowerInvariant();
                if (!string.IsNullOrEmpty(ext))
                {
                    parser = ext;
                }
            }

            await _analytics.CaptureAsync(
                organisationId: organisationId,
                userId: null,
                eventName: "first_upload_parsed",
                properties: new Dictionary<string, object?>
                {
                    ["order_id"] = orderId,
                    ["parser"]   = parser,
                },
                ct: ct);
        }

        // ── Schema fingerprint accumulation (org-scoped moat) ─────────────────
        // Column headers and format were already detected by OrderService while the
        // buffer was in memory — pass them here to avoid a second file download.
        // Idempotent across retries via the order's persisted hash guard.
        // Non-critical: a fingerprint failure must never fail a successful parse.
        try
        {
            await _fingerprints.RecordParseSuccessAsync(
                organisationId,
                orderId,
                result.Value!.ColumnHeaders,
                result.Value!.DetectedFormat,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Schema fingerprint recording failed for order {OrderId} (non-fatal)", orderId);
        }

        // ── Supplier-scoped field-mapping capture (the learning half of the moat) ──
        // Learn the buyer→supplier item-code mapping observed on this file's resolved lines, keyed
        // by (supplier, column layout). A later upload of the same layout for the same supplier
        // replays it to pre-fill suggestions (see OrderService.BuildLineEntityAsync).
        // Non-critical: a capture failure must never fail a successful parse.
        try
        {
            var parsed = result.Value!.Entity;

            // Only the lines that ARE resolved (deterministic match, or a suggestion already
            // accepted) carry a trustworthy supplier code worth learning. Unresolved lines and
            // lines whose code came only from an un-accepted suggestion are excluded.
            var learnedPairs = parsed.Lines
                .Where(l => !l.NeedsReview
                         && !string.IsNullOrWhiteSpace(l.BuyerItemCode)
                         && !string.IsNullOrWhiteSpace(l.SupplierItemCode))
                .GroupBy(l => l.BuyerItemCode.Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().SupplierItemCode!.Trim());

            if (learnedPairs.Count > 0)
            {
                await _supplierSchemaMappings.CaptureAsync(
                    organisationId,
                    parsed.SupplierId,
                    orderId,
                    result.Value!.ColumnHeaders,
                    result.Value!.DetectedFormat,
                    learnedPairs,
                    ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Supplier schema mapping capture failed for order {OrderId} (non-fatal)", orderId);
        }
    }

    // ── Static factory method for clean enqueue syntax ────────────────────────

    public static void Enqueue(IBackgroundJobClient jobs, Guid orderId, Guid organisationId)
    {
        jobs.Enqueue<ParseOrderJob>(j => j.ExecuteAsync(orderId, organisationId, CancellationToken.None));
    }
}
