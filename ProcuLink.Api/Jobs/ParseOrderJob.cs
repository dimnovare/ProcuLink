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
    private readonly IAutoSendDryRunEvaluator? _autoSend;

    public ParseOrderJob(
        IOrderService orderService,
        ILogger<ParseOrderJob> logger,
        ProcuLinkDbContext db,
        IAnalyticsService analytics,
        ISchemaFingerprintService fingerprints,
        IAutoSendDryRunEvaluator? autoSend = null)
    {
        _orderService = orderService;
        _logger       = logger;
        _db           = db;
        _analytics    = analytics;
        _fingerprints = fingerprints;
        _autoSend     = autoSend;
    }

    /// <summary>
    /// Entry point called by Hangfire.
    /// </summary>
    [Queue("critical")]
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

        // ── Terminal-failure guard ────────────────────────────────────────────
        // A parse failure sets status='failed' and returns Fail, and we throw above. Hangfire then
        // RETRIES, and the retry re-enters ParseStoredFileAsync, whose status!='parsing' re-entry
        // guard sees the now-'failed' order, treats it as an already-processed SKIP and returns Ok.
        // Reporting that as success marked the whole job Succeeded and hid every terminal parse
        // failure from Hangfire's Failed queue. Throw instead: the remaining retries burn out on a
        // cheap read and the job lands red where ops can see it. Attempt 1's real exception stays in
        // the job history. This also short-circuits the analytics block below, which would otherwise
        // fire first_upload_parsed for a FAILED order.
        if (result.Value!.Entity.Status == OrderStatusConstants.Failed)
        {
            _logger.LogError(
                "ParseOrderJob: order {OrderId} is in terminal status '{Status}' — surfacing as a failed job rather than reporting success.",
                orderId, result.Value!.Entity.Status);
            throw new InvalidOperationException(
                $"Parse failed: order {orderId} is in terminal status '{OrderStatusConstants.Failed}'.");
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

        // Re-parse guard: the org-level check above does NOT stop an org's ONLY order from firing
        // this twice. Routing's assign-supplier flips an 'unrouted' order back to 'parsing' and
        // re-parses it; on that second parse the AnyAsync still finds no OTHER parsed order, so the
        // event re-fired — a deterministic double-count. first_upload_parsed is a once-per-order
        // milestone, not a per-parse event. ParseStoredFileAsync writes exactly one 'Parsed' audit
        // event per parse, so more than one for this order means we have been here before.
        var parseCount = await _db.AuditEvents
            .AsNoTracking()
            .CountAsync(e => e.OrgId == organisationId
                          && e.EntityType == "Order"
                          && e.EntityId == orderId
                          && e.Action == "Parsed", ct);
        var isReParse = parseCount > 1;

        if (!hadOtherParsedOrders && !isReParse)
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

        // ── WP-33 stage 1 — auto-send, in dry run ─────────────────────────────
        // Parse completion is where an order first becomes sendable, so it is where an automatic
        // send would begin. Stage 1 makes that whole decision and records it WITHOUT sending: the
        // evaluator cannot reach a transform or a dispatch, which is pinned from IL by
        // AutoSendDryRunCannotSendTests rather than promised here.
        //
        // Non-fatal, exactly like the fingerprint block above. This job throws to signal a failed
        // PARSE — letting a dry-run bookkeeping failure throw would fail a parse that succeeded and
        // send Hangfire round the retry loop for a reason that has nothing to do with parsing.
        // Idempotent across those retries and across a Hangfire refetch: the unique index on
        // (org_id, order_id) refuses a second row.
        try
        {
            if (_autoSend is not null)
                await _autoSend.EvaluateAsync(organisationId, orderId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-send dry-run evaluation failed for order {OrderId} (non-fatal)", orderId);
        }
    }

    // ── Static factory method for clean enqueue syntax ────────────────────────

    public static void Enqueue(IBackgroundJobClient jobs, Guid orderId, Guid organisationId)
    {
        jobs.Enqueue<ParseOrderJob>(j => j.ExecuteAsync(orderId, organisationId, CancellationToken.None));
    }
}
