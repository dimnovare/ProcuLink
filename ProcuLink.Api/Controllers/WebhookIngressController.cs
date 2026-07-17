using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Webhooks;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// HMAC-verified inbound webhook ingress for supplier ERPs / EDI gateways to
/// push acknowledgements + status callbacks back without an API key.
///
/// Required headers on every request:
///   X-ProcuLink-Timestamp  ISO-8601 UTC, within ±300s of receive time
///   X-ProcuLink-Nonce      Client-generated unique value (UUID recommended)
///   X-ProcuLink-Signature  lower-hex(HMAC-SHA256(secret, $"{ts}.{nonce}.{body}"))
///
/// Auth: HMAC alone — no Clerk, no API key. The org's webhook shared secret
/// (set/rotated by org admins) is the sole authenticator.
///
/// All failure paths return 401 with the same generic error message; never
/// distinguish which check failed (slug unknown vs bad signature vs replay).
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("api/webhook-ingress/{slug}")]
// Rate-limited per TENANT (the {slug} route value), not per source IP — many
// suppliers push callbacks for one org from a shared egress IP, so an IP-keyed
// bucket would let one noisy tenant exhaust every other tenant's quota. The
// "webhook" policy (Program.cs) derives its partition key from this slug.
[EnableRateLimiting("webhook")]
public sealed class WebhookIngressController : ControllerBase
{
    private readonly IHmacWebhookVerifier                  _verifier;
    private readonly ProcuLinkDbContext                    _db;
    private readonly IOrderExceptionService                _exceptions;
    private readonly ILogger<WebhookIngressController>     _logger;

    public WebhookIngressController(
        IHmacWebhookVerifier                  verifier,
        ProcuLinkDbContext                    db,
        IOrderExceptionService                exceptions,
        ILogger<WebhookIngressController>     logger)
    {
        _verifier   = verifier;
        _db         = db;
        _exceptions = exceptions;
        _logger     = logger;
    }

    /// <summary>POST /api/webhook-ingress/{slug}/ping — connectivity + auth test.</summary>
    [HttpPost("ping")]
    public async Task<IActionResult> Ping(string slug, CancellationToken ct)
    {
        var (body, ts, nonce, sig) = await ReadHeadersAndBodyAsync(ct);
        var result = await _verifier.VerifyAsync(slug, ts, nonce, sig, body, ct);
        if (!result.Valid)
            return Unauthorized(new { error = result.ErrorMessage });

        return Ok(new { ok = true, slug, timestamp = DateTimeOffset.UtcNow });
    }

    /// <summary>
    /// POST /api/webhook-ingress/{slug}/acknowledge — supplier acknowledges receipt of an order.
    /// Body: { orderId: guid, supplierReference?: string, acknowledgedAt?: iso, notes?: string }
    /// </summary>
    [HttpPost("acknowledge")]
    public async Task<IActionResult> Acknowledge(string slug, CancellationToken ct)
    {
        var (body, ts, nonce, sig) = await ReadHeadersAndBodyAsync(ct);
        var result = await _verifier.VerifyAsync(slug, ts, nonce, sig, body, ct);
        if (!result.Valid)
            return Unauthorized(new { error = result.ErrorMessage });

        var orgId = result.OrganisationId!.Value;

        AcknowledgePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AcknowledgePayload>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid JSON body." });
        }

        if (payload is null || payload.OrderId == Guid.Empty)
            return BadRequest(new { error = "orderId is required." });

        // Confirm order belongs to this org before recording the audit event.
        var orderExists = await _db.PurchaseOrders
                                   .AnyAsync(o => o.OrgId == orgId && o.Id == payload.OrderId, ct);
        if (!orderExists)
            return NotFound(new { error = "Order not found." });

        var auditPayload = JsonSerializer.Serialize(new
        {
            payload.SupplierReference,
            AcknowledgedAt = payload.AcknowledgedAt ?? DateTimeOffset.UtcNow,
            payload.Notes,
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            UserId     = null,
            EntityType = "PurchaseOrder",
            EntityId   = payload.OrderId,
            Action     = "webhook_acknowledge",
            Payload    = JsonDocument.Parse(auditPayload),
            CreatedAt  = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Webhook acknowledge received for order {OrderId} (org {OrgId}).",
            payload.OrderId, orgId);

        return Ok(new { ok = true, orderId = payload.OrderId });
    }

    /// <summary>
    /// POST /api/webhook-ingress/{slug}/status — supplier reports lifecycle status for an order.
    /// Body: { orderId: guid, status: "received|in_progress|rejected|delivered", reason?: string, occurredAt?: iso }
    /// </summary>
    [HttpPost("status")]
    public async Task<IActionResult> Status(string slug, CancellationToken ct)
    {
        var (body, ts, nonce, sig) = await ReadHeadersAndBodyAsync(ct);
        var result = await _verifier.VerifyAsync(slug, ts, nonce, sig, body, ct);
        if (!result.Valid)
            return Unauthorized(new { error = result.ErrorMessage });

        var orgId = result.OrganisationId!.Value;

        StatusPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StatusPayload>(body, JsonOpts);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid JSON body." });
        }

        if (payload is null || payload.OrderId == Guid.Empty)
            return BadRequest(new { error = "orderId is required." });

        var status = (payload.Status ?? string.Empty).Trim().ToLowerInvariant();
        if (!AllowedStatuses.Contains(status))
            return BadRequest(new
            {
                error = $"status must be one of: {string.Join(", ", AllowedStatuses)}.",
            });

        var order = await _db.PurchaseOrders
                             .Where(o => o.OrgId == orgId && o.Id == payload.OrderId)
                             .FirstOrDefaultAsync(ct);
        if (order is null)
            return NotFound(new { error = "Order not found." });

        // A supplier callback may report a terminal outcome ONLY for an order that was genuinely
        // dispatched. Without this, an HMAC-authenticated callback could force 'delivered' onto an
        // order nobody ever sent -- a SILENT LOST ORDER: shipped in the UI, never sent.
        //
        // A rejection writes rejected_by_supplier, never delivery_failed: delivery_failed is a
        // retryable transport state, so StrandedFailedDeliveryDetectionService would sweep the aged
        // order and re-drive it (RetryDeliveryAsync retries from delivery_failed) -- re-sending a PO
        // the supplier explicitly rejected. That sweeper's predicate is justified on exactly this
        // premise (StrandedFailedDeliveryDetectionService.cs:46).
        //
        // 'received'/'in_progress' are pure telemetry: they mutate nothing, so they are NOT guarded
        // (a 409 there would add noise without preventing harm) and stay 200 from any state.
        var target = status switch
        {
            "delivered" => OrderStatusConstants.Delivered,
            "rejected"  => OrderStatusConstants.RejectedBySupplier,
            _           => null,
        };

        // Reported status already matches => idempotent replay, handled by the non-mutating path
        // below. Callback endpoints get retried, and a supplier re-posting a rejection it already
        // delivered must not get a 409 for work that succeeded. This short-circuit is what lets
        // rejected_by_supplier stay OUT of OrderStatusMachine.WebhookReportableFrom.
        if (target is not null && !string.Equals(order.Status, target, StringComparison.Ordinal))
            return await ApplyReportedStatusAsync(orgId, order, target, status, payload, ct);

        AddStatusAudit(orgId, payload.OrderId, status, payload);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Webhook status={Status} received for order {OrderId} (org {OrgId}).",
            status, payload.OrderId, orgId);

        return Ok(new { ok = true, orderId = payload.OrderId, status = order.Status });
    }

    /// <summary>
    /// Applies a terminal supplier callback, or refuses it — the mutating half of
    /// <see cref="Status"/>.
    ///
    /// <para><b>"Was this order ever dispatched?" is not a status question.</b> Membership of
    /// <see cref="OrderStatusMachine.WebhookReportableFrom"/> only proves an order in that state
    /// MAY have been dispatched; two of its members are also reachable with no dispatch at all
    /// (<c>ready_to_deliver</c> is where every transformed order RESTS awaiting a manual Send;
    /// <c>delivery_held</c> is reachable via the PRE-CLAIM billing gate, DeliveryService.cs:822-825).
    /// So the status check is paired with dispatch EVIDENCE.</para>
    ///
    /// <para><b>The EXISTENCE of a DeliveryAttempt row is NOT that evidence.</b> Four pre-dispatch
    /// gates write an order-linked terminal row with nothing sent: missing config
    /// (<c>FailMissingConfigAsync</c>, Channel="missing_config"), no dispatcher registered,
    /// undecryptable credentials, and artifact-download failure (the last three via
    /// <c>FailBeforeDispatchAsync</c>). DeliveryService.cs says it outright: "the pre-dispatch
    /// failure paths (no send happened, so no in-flight marker)". A row is an ATTEMPT AT delivery,
    /// not a delivery.</para>
    ///
    /// <para>The evidence is therefore a per-row MARKER that only the dispatch sequence writes —
    /// <c>IdempotencyKey != null OR ArtifactSha256 != null</c>:</para>
    /// <list type="bullet">
    ///   <item><c>IdempotencyKey</c> is stamped at exactly one production site
    ///     (<c>OpenDispatchAttemptAsync</c>, DeliveryService.cs:392) on the <c>dispatching</c> row it
    ///     COMMITS before the wire send. Present ⇒ the dispatch sequence ran to its pre-send commit.
    ///     It does NOT prove the bytes reached the supplier: a crash between that commit and the send
    ///     leaves the marker with nothing sent. It proves a send was BEGUN — which is precisely the
    ///     state a callback may legitimately report on, and which the four gates above never reach.
    ///     (Test-fire rows carry <c>OrderId = null</c>, so they can never match this order.)</item>
    ///   <item><c>ArtifactSha256</c> is the hash of the bytes ACTUALLY dispatched, passed only after
    ///     <c>DispatchAsync</c> returns (DeliveryService.cs:345). Present ⇒ the send executed.</item>
    /// </list>
    ///
    /// <para><b>Why BOTH, not just the key:</b> migration
    /// <c>20260716083147_AddDeliveryAttemptIdempotencyKey</c> added the column with NO backfill, so
    /// every row written before 2026-07-16 has a NULL key. <c>artifact_sha256</c> shipped 2026-06-11
    /// (<c>20260611095227</c>) and covers that legacy window. Orders dispatched in the ~2 days
    /// between launch (2026-06-09) and 06-11 have neither and are refused — the residual gap fails
    /// CLOSED, which is the safe direction.</para>
    ///
    /// <para>Marking a never-dispatched order 'delivered' would not merely be wrong: it would
    /// DISABLE that order's own safety net. StrandedReadyOrderDetectionService sweeps on
    /// <c>Status == ready_to_deliver</c> and ReleaseBillingHeldOrdersAsync matches
    /// <c>Status == delivery_held</c>; overwrite either and the predicate stops matching, so the
    /// order is permanently lost, displayed as shipped, and billable.</para>
    ///
    /// <para>Both halves are re-verified INSIDE an atomic <c>ExecuteUpdateAsync</c> claim rather
    /// than trusted from the earlier SELECT: purchase_orders carries no concurrency token, so a
    /// concurrent MV-1 mapping edit (ready_to_deliver → ready) or billing hold landing between the
    /// read and the write would otherwise let the callback write onto a no-longer-dispatched order —
    /// the exact outcome this guard exists to prevent. Mirrors the SLA guard's move from SELECT into
    /// the claim (34ee07b / d0d32b3) and DeliveryService.cs:196-237's claim shape.</para>
    /// </summary>
    private async Task<IActionResult> ApplyReportedStatusAsync(
        Guid                orgId,
        PurchaseOrderEntity order,
        string              target,
        string              reportedStatus,
        StatusPayload       payload,
        CancellationToken   ct)
    {
        // Advisory only on the relational path (the claim below is the real decision) — read here so
        // a refusal can explain ITSELF accurately. On the non-relational path it IS the decision.
        //
        // The marker condition is written out here and again inside the claim predicate; EF cannot
        // translate a captured Expression variable into the claim's correlated subquery, so the two
        // are kept literally identical instead. Their equivalence is what makes the InMemory tests
        // meaningful, so it is PINNED on both providers: WebhookIngressControllerTests exercises this
        // read, WebhookStatusClaimPostgresTests exercises the claim, and both assert the same
        // accept/refuse outcomes per shape.
        var hasDispatchEvidence = await _db.DeliveryAttempts
            .AnyAsync(a => a.OrgId == orgId && a.OrderId == order.Id
                        && (a.IdempotencyKey != null || a.ArtifactSha256 != null), ct);

        var updatedAt = DateTime.UtcNow;

        if (_db.Database.IsRelational())
        {
            bool claimed;

            // ExecuteUpdateAsync AUTO-COMMITS immediately, so without an explicit transaction the
            // status write could land while the audit SaveChanges below fails (or vice versa),
            // leaving a status change nobody can explain. One transaction spans both.
            await using (var claimTx = await _db.Database.BeginTransactionAsync(ct))
            {
                var rows = await _db.PurchaseOrders
                    .Where(o => o.Id == order.Id && o.OrgId == orgId
                             && ReportableFromStatuses.Contains(o.Status)
                             && _db.DeliveryAttempts.Any(a => a.OrgId == o.OrgId && a.OrderId == o.Id
                                                           && (a.IdempotencyKey != null || a.ArtifactSha256 != null)))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.Status, target)
                        .SetProperty(o => o.UpdatedAt, updatedAt), ct);

                if (rows == 0)
                {
                    await claimTx.RollbackAsync(ct);
                    claimed = false;
                }
                else
                {
                    // The bulk update bypasses the change tracker, leaving `order` STALE. Sync the
                    // CURRENT value (the 200 body reports order.Status) and the ORIGINAL value, so
                    // the tracked entity matches the row just claimed and the status observer sees
                    // no phantom transition (it compares original against current). Mirrors
                    // DeliveryService.cs:226-237.
                    //
                    // This does NOT promise the audit SaveChanges emits no status UPDATE: setting
                    // OriginalValue does not clear IsModified, so EF may include the same values in
                    // the UPDATE again. That is harmless — identical values, same transaction, and
                    // the row is already claim-locked — and no behaviour here depends on it.
                    order.Status    = target;
                    order.UpdatedAt = updatedAt;
                    var entry = _db.Entry(order);
                    entry.Property(x => x.Status).OriginalValue    = target;
                    entry.Property(x => x.UpdatedAt).OriginalValue = updatedAt;

                    await StampRejectionReasonAsync(orgId, order, target, payload, ct);
                    AddStatusAudit(orgId, order.Id, reportedStatus, payload);
                    await _db.SaveChangesAsync(ct);
                    await claimTx.CommitAsync(ct);
                    claimed = true;
                }
            }

            if (!claimed)
                return await RejectStatusCallbackAsync(orgId, order, hasDispatchEvidence, reportedStatus, payload, ct);
        }
        else
        {
            // The EF InMemory test provider cannot translate ExecuteUpdateAsync or transactions —
            // emulate the same predicate through the change tracker (InMemory tests are
            // single-threaded, so the race the relational claim defends against cannot occur there).
            // The status write and the audit land in ONE SaveChanges, which is atomic enough here.
            if (!OrderStatusMachine.WebhookReportableFrom.Contains(order.Status) || !hasDispatchEvidence)
                return await RejectStatusCallbackAsync(orgId, order, hasDispatchEvidence, reportedStatus, payload, ct);

            order.Status    = target;
            order.UpdatedAt = updatedAt;
            await StampRejectionReasonAsync(orgId, order, target, payload, ct);
            AddStatusAudit(orgId, order.Id, reportedStatus, payload);
            await _db.SaveChangesAsync(ct);
        }

        // Exceptions are DERIVED from the order's status, so a status write that skips reconcile
        // leaves the previous status's exception open forever: an earlier 503 opens "Delivery to the
        // supplier failed.", this callback moves the order to rejected_by_supplier, and nothing
        // re-reconciles it — the operator reads a transport failure on an order the supplier actually
        // REJECTED. The delivered case inverts it (a stale problem on a delivered order). Both
        // statuses have a correct mapping in OrderExceptionService.ProblemFor, so one reconcile fixes
        // both. Mirrors OrderResolutionService.MarkRejectedAsync (:279).
        //
        // Runs only once the status write is COMMITTED (outside the claim transaction), and via the
        // Safe* contract: a reconcile failure must not turn a committed status write into a 500 the
        // supplier retries. Refusals return earlier and never reach here — there is no status change
        // to reconcile against.
        await SafeReconcileExceptionsAsync(orgId, order.Id, ct);

        _logger.LogInformation(
            "Webhook status={Status} received for order {OrderId} (org {OrgId}).",
            reportedStatus, order.Id, orgId);

        return Ok(new { ok = true, orderId = order.Id, status = order.Status });
    }

    /// <summary>
    /// Stamps the supplier's rejection reason where the ORDER can actually read it.
    ///
    /// <para>The AuditEvent is written with EntityType="PurchaseOrder", but OrdersController.Get
    /// filters audits on EntityType=="Order" and otherwise falls back to the latest DeliveryAttempt —
    /// so a reason living only in that audit is unreachable, and the UI shows the supplier rejecting
    /// the PO because of whatever the last transport error happened to be (e.g. a gateway timeout)
    /// instead of the real reason. Mirrors the canonical manual path,
    /// OrderResolutionService.MarkRejectedAsync (:261-269).</para>
    ///
    /// <para>Only a rejection carries a reason; 'delivered' never writes one. The caller owns the
    /// SaveChanges, so this write joins the claim's transaction.</para>
    /// </summary>
    private async Task StampRejectionReasonAsync(
        Guid                orgId,
        PurchaseOrderEntity order,
        string              target,
        StatusPayload       payload,
        CancellationToken   ct)
    {
        if (!string.Equals(target, OrderStatusConstants.RejectedBySupplier, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload.Reason))
            return;

        var latestAttempt = await _db.DeliveryAttempts
            .Where(a => a.OrgId == orgId && a.OrderId == order.Id)
            .OrderByDescending(a => a.AttemptedAt)
            .FirstOrDefaultAsync(ct);

        if (latestAttempt is not null)
            latestAttempt.RejectionReason = payload.Reason;
    }

    /// <summary>Why a terminal callback was refused. Drives both the operator log and the supplier-facing sentence.</summary>
    private enum RefusalReason
    {
        /// <summary>Already rejected_by_supplier — a webhook must not silently un-reject it.</summary>
        AlreadyRejected,

        /// <summary>
        /// No attempt row carries a dispatch marker, so no send was ever begun for this order and no
        /// supplier can be reporting on it. Two windows can ERASE the evidence for a genuinely
        /// dispatched order — OpsController's requeue hard-deletes its attempt rows to reset the cap,
        /// and DataRetentionService prunes attempt rows for terminal statuses (disabled by default,
        /// 180d) — so this reason can be reached for an order that WAS sent long ago. Both fail
        /// CLOSED: the callback is refused, never applied.
        /// </summary>
        NeverDispatched,

        /// <summary>Dispatched at some point, but the order is not currently awaiting a delivery outcome.</summary>
        NotAwaitingOutcome,
    }

    /// <summary>
    /// A terminal status callback the claim refused. Audited (a 409 nobody can see is a silent
    /// ignore with extra steps) and answered 409: the request is well-formed and authentic, it
    /// conflicts with the order's current state. Well-behaved clients treat 4xx as permanent and
    /// stop retrying, which is what we want for a genuine integration error.
    ///
    /// <para>Three shapes are reachable and the sentence MUST branch across them — a single
    /// "this order has not been sent to a supplier yet" is FALSE on two of them (an order rejected
    /// by the supplier WAS sent; an MV-1-reset order WAS sent), and its "check that the orderId
    /// matches an order you received" fix is a dead end there: it DOES match one.</para>
    ///
    /// <para>The order is tracked but left unmodified, so this SaveChanges writes the audit row
    /// only — never a status change.</para>
    /// </summary>
    private async Task<IActionResult> RejectStatusCallbackAsync(
        Guid                   orgId,
        PurchaseOrderEntity    order,
        bool                   hasDispatchEvidence,
        string                 reportedStatus,
        StatusPayload          payload,
        CancellationToken      ct)
    {
        var reason  = ClassifyRefusal(order.Status, hasDispatchEvidence);
        var message = RefusalMessage(reason, order.Status, reportedStatus);

        var auditPayload = JsonSerializer.Serialize(new
        {
            ReportedStatus       = reportedStatus,
            OrderStatusAtReceipt = order.Status,
            HadDispatchEvidence  = hasDispatchEvidence,
            RefusalReason        = reason.ToString(),
            payload.Reason,
            OccurredAt           = payload.OccurredAt ?? DateTimeOffset.UtcNow,
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            UserId     = null,
            EntityType = "PurchaseOrder",
            EntityId   = order.Id,
            Action     = "webhook_status_rejected",
            Payload    = JsonDocument.Parse(auditPayload),
            CreatedAt  = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Webhook status={ReportedStatus} REFUSED for order {OrderId} (org {OrgId}): order status is " +
            "'{OrderStatus}', dispatchEvidence={DispatchEvidence} (an attempt row carries an idempotency " +
            "key or a dispatched-payload hash), refusal={Reason}. " +
            "The order was NOT modified; the callback is recorded as webhook_status_rejected.",
            reportedStatus, order.Id, orgId, order.Status, hasDispatchEvidence, reason);

        return Conflict(new { error = message });
    }

    private static RefusalReason ClassifyRefusal(string orderStatus, bool hasDispatchEvidence) =>
        string.Equals(orderStatus, OrderStatusConstants.RejectedBySupplier, StringComparison.Ordinal)
            ? RefusalReason.AlreadyRejected
            // Covers every state whose attempt rows carry no dispatch marker — the parse/review
            // pipeline, the two reportable-but-idle states (ready_to_deliver awaiting a Send,
            // delivery_held by the pre-send billing gate), AND an order whose only rows came from a
            // pre-dispatch gate (missing config / no dispatcher / bad credentials / download
            // failure). "Never sent" is TRUE for all of them.
            : !hasDispatchEvidence
                ? RefusalReason.NeverDispatched
                : RefusalReason.NotAwaitingOutcome;

    /// <summary>One human sentence: what is actually true, and what the caller can do about it.</summary>
    private static string RefusalMessage(RefusalReason reason, string orderStatus, string reportedStatus) =>
        reason switch
        {
            RefusalReason.AlreadyRejected =>
                $"This order is already recorded as rejected by the supplier, so a '{reportedStatus}' update "
              + "cannot be applied to it. If that rejection was recorded in error, ask for the order to be "
              + "sent again and report the outcome of that new send.",

            RefusalReason.NeverDispatched =>
                $"This order has not been sent to a supplier yet (it is '{orderStatus}'), so a "
              + $"'{reportedStatus}' update cannot be applied to it. Check that the orderId in the callback "
              + "matches an order you received.",

            // NotAwaitingOutcome: it WAS sent, but has since moved on (e.g. MV-1 reset it to 'ready'
            // for re-transform after a mapping edit).
            _ =>
                $"This order is '{orderStatus}' and is not waiting for a delivery outcome, so a "
              + $"'{reportedStatus}' update cannot be applied to it. Report the outcome once the order has "
              + "been sent to you again.",
        };

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>Queues the accepted-callback audit row. The caller owns the SaveChanges (and any transaction).</summary>
    private void AddStatusAudit(Guid orgId, Guid orderId, string reportedStatus, StatusPayload payload)
    {
        var auditPayload = JsonSerializer.Serialize(new
        {
            ReportedStatus = reportedStatus,
            payload.Reason,
            OccurredAt = payload.OccurredAt ?? DateTimeOffset.UtcNow,
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            UserId     = null,
            EntityType = "PurchaseOrder",
            EntityId   = orderId,
            Action     = "webhook_status",
            Payload    = JsonDocument.Parse(auditPayload),
            CreatedAt  = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// <see cref="OrderStatusMachine.WebhookReportableFrom"/> projected once for the claim predicate:
    /// EF translates <c>Enumerable.Contains</c> over a materialised array into a single
    /// <c>IN (…)</c>, whereas <c>IReadOnlySet.Contains</c> is not a translatable call. Projected
    /// from the canonical set (never re-listed) so the two can never drift.
    /// </summary>
    private static readonly string[] ReportableFromStatuses =
        OrderStatusMachine.WebhookReportableFrom.ToArray();

    /// <summary>
    /// Reconcile the order's exceptions, never failing the supplier's callback if it goes wrong.
    /// The status write is already committed by this point, so a reconcile error must not turn a
    /// SUCCESSFUL status update into a 500 the supplier will retry. Same contract (and same reason)
    /// as OrderServiceShared.SafeReconcileExceptionsAsync.
    /// </summary>
    private async Task SafeReconcileExceptionsAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        try
        {
            await _exceptions.ReconcileAsync(orgId, orderId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Webhook status: exception reconcile failed for order {OrderId} (org {OrgId}) — non-fatal, " +
                "the status write is already committed.",
                orderId, orgId);
        }
    }

    private async Task<(string Body, string Ts, string Nonce, string Sig)> ReadHeadersAndBodyAsync(
        CancellationToken ct)
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        var ts    = Request.Headers["X-ProcuLink-Timestamp"].FirstOrDefault() ?? string.Empty;
        var nonce = Request.Headers["X-ProcuLink-Nonce"].FirstOrDefault()     ?? string.Empty;
        var sig   = Request.Headers["X-ProcuLink-Signature"].FirstOrDefault() ?? string.Empty;
        return (body, ts, nonce, sig);
    }

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "received",
        "in_progress",
        "rejected",
        "delivered",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── payload DTOs ─────────────────────────────────────────────────────

    public sealed record AcknowledgePayload(
        Guid             OrderId,
        string?          SupplierReference,
        DateTimeOffset?  AcknowledgedAt,
        string?          Notes);

    public sealed record StatusPayload(
        Guid             OrderId,
        string?          Status,
        string?          Reason,
        DateTimeOffset?  OccurredAt);
}
