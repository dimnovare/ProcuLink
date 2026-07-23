using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services;

public sealed class DeliveryService : IDeliveryService
{
    /// <summary>
    /// How recently a <c>delivering</c> row must have been stamped to count as "actively in
    /// flight" and therefore NOT reclaimable by a concurrent retry. A freshly claimed delivery
    /// updates <c>UpdatedAt</c> to now, so a racing retry that sees the row already
    /// <c>delivering</c> within this window bows out instead of double-dispatching. Comfortably
    /// shorter than <c>StuckDeliveryDetectionService</c>'s minute-scale stuck threshold, so a
    /// genuinely stranded delivery is still recoverable by the sweep — this only closes the
    /// sub-second concurrent-retry race.
    /// </summary>
    private static readonly TimeSpan DeliveringReclaimWindow = TimeSpan.FromMinutes(2);

    private readonly ProcuLinkDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly DeliveryEncryptionService _encryption;
    private readonly IReadOnlyDictionary<string, IDeliveryDispatcher> _dispatchers;
    private readonly IIntegrationTriggerService _integrationTrigger;
    private readonly IAnalyticsService _analytics;
    private readonly IOrderExceptionService _exceptions;
    private readonly DeliveryReliabilityOptions _reliability;
    private readonly ILogger<DeliveryService> _logger;
    private readonly IEffectiveConnectionConfigResolver? _effectiveConfig;
    // Optional re-drive seam: ReleaseBillingHeldOrdersAsync re-enqueues a delivery retry per
    // released order. Registered in BOTH the Api (reactivation webhook) and Worker hosts. Null in
    // older positional test ctors — release still moves orders back to ready_to_deliver (visible +
    // operator-re-drivable), it just doesn't auto-enqueue. Mirrors StuckOrderDetectionService's
    // optional IParseJobEnqueuer pattern.
    private readonly IRetryDeliveryEnqueuer? _retryEnqueuer;
    // A5 — billing gate on the retry path. Null in older positional test ctors / hosts that do not
    // register a billing service behaves exactly like flag-OFF: the retry is NOT billing-gated
    // (pre-A5 behaviour). Both live hosts (Api + Worker) register IBillingService, so production
    // retries ARE gated. Mirrors the optional-seam pattern used for _effectiveConfig / _retryEnqueuer.
    private readonly IBillingService? _billing;

    public DeliveryService(
        ProcuLinkDbContext db,
        IFileStorageService fileStorage,
        DeliveryEncryptionService encryption,
        IEnumerable<IDeliveryDispatcher> dispatchers,
        IIntegrationTriggerService integrationTrigger,
        IAnalyticsService analytics,
        IOrderExceptionService exceptions,
        ILogger<DeliveryService> logger,
        DeliveryReliabilityOptions? reliability = null,
        IEffectiveConnectionConfigResolver? effectiveConfig = null,
        IRetryDeliveryEnqueuer? retryEnqueuer = null,
        IBillingService? billing = null)
    {
        _db = db;
        _fileStorage = fileStorage;
        _encryption = encryption;
        _dispatchers = dispatchers.ToDictionary(x => x.Protocol, StringComparer.OrdinalIgnoreCase);
        _integrationTrigger = integrationTrigger;
        _analytics = analytics;
        _exceptions = exceptions;
        _reliability = reliability ?? new DeliveryReliabilityOptions();
        _logger = logger;
        // Launch batch 7 — revision authority. Null (older positional test ctors / unregistered
        // hosts) behaves exactly like flag-OFF: the live supplier delivery config drives dispatch.
        _effectiveConfig = effectiveConfig;
        _retryEnqueuer = retryEnqueuer;
        _billing = billing;
    }

    /// <summary>
    /// Deterministic per-artifact delivery idempotency key (A3): a stable function of
    /// (orderId, artifactId). Every dispatch of the same artifact — a legitimate backoff retry AND a
    /// crash-recovery re-send after a lost ACK — produces the SAME key, so a channel that honours it
    /// lets the supplier de-duplicate the re-send. A re-transform mints a new artifactId → a new key
    /// → a legitimately new delivery.
    /// </summary>
    internal static string BuildIdempotencyKey(Guid orderId, Guid artifactId)
        => $"plk-dlv-{orderId:N}-{artifactId:N}";

    /// <summary>
    /// Best-effort exception reconciliation: exception generation is operational
    /// observability data and must never fail the parent delivery operation.
    /// </summary>
    private async Task SafeReconcileExceptionsAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        try
        {
            await _exceptions.ReconcileAsync(orgId, orderId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception reconcile failed for order {OrderId} (non-fatal).", orderId);
        }
    }

    public Task<DeliveryResult> DispatchArtifactAsync(
        Guid orgId,
        Guid orderId,
        Guid artifactId,
        bool requireAutoDeliver,
        CancellationToken ct)
        // Public entry point = the FIRST-DELIVER / redeliver / requeue path (DeliverOrderJob). It is
        // NOT yet holding a delivery claim, so it must atomically claim the order before dispatch
        // (see the claim block below). The retry path enters through the internal overload with
        // alreadyClaimed: true because RetryDeliveryAsync already made the claim.
        => DispatchArtifactAsync(orgId, orderId, artifactId, requireAutoDeliver, reconcileFailedAttempt: true, alreadyClaimed: false, ct);

    // Internal overload. <paramref name="reconcileFailedAttempt"/> lets the retry path opt out
    // of reconciling a failed attempt it is about to dead-letter: DeadLetterAsync reconciles
    // once at the end, so reconciling on the failed attempt first would write a transient
    // delivery_failed exception that the dead-letter reconcile immediately resolves. Successful
    // attempts always reconcile regardless of this flag.
    //
    // <paramref name="alreadyClaimed"/> = true ONLY when the caller (RetryDeliveryAsync) has already
    // atomically flipped the order to 'delivering'. A direct call (alreadyClaimed: false) must make
    // the claim itself so two concurrent first-deliver/redeliver/requeue activations can't both
    // dispatch the same PO (D-1).
    private async Task<DeliveryResult> DispatchArtifactAsync(
        Guid orgId,
        Guid orderId,
        Guid artifactId,
        bool requireAutoDeliver,
        bool reconcileFailedAttempt,
        bool alreadyClaimed,
        CancellationToken ct)
    {
        var artifact = await _db.OutboundArtifacts
            .Where(x => x.Id == artifactId && x.OrderId == orderId && x.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        var order = await _db.PurchaseOrders
            .Where(x => x.Id == orderId && x.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        if (artifact is null || order is null)
            return new DeliveryResult(false, "Order artifact not found.", Outcome: DeliveryOutcome.NotRetryable);

        // ── Launch batch 7 — revision authority ────────────────────────────────
        // A pinned order delivers over the CHANNEL its published revision snapshotted
        // (protocol + non-secret config + encrypted credentials), so a later live
        // delivery-config edit can never silently re-route an already-pinned order.
        // Live config when the flag is off / unpinned / orphan pin / blank snapshot.
        var config = await ResolveEffectiveDeliveryConfigAsync(orgId, order, artifact, ct);

        if (config is null)
            return await FailMissingConfigAsync(order, artifact, reconcileFailedAttempt, ct);

        // Auto-deliver is off: a deliberate no-op, never a send.
        if (requireAutoDeliver && !config.AutoDeliver)
            return new DeliveryResult(true, null, Outcome: DeliveryOutcome.NotRetryable);

        if (!_dispatchers.TryGetValue(config.Protocol, out var dispatcher))
            return await FailBeforeDispatchAsync(order, artifact, config, "No dispatcher registered for delivery protocol.", reconcileFailedAttempt, ct);

        var credentials = string.IsNullOrWhiteSpace(config.EncryptedCredentials)
            ? string.Empty
            : _encryption.Decrypt(config.EncryptedCredentials);

        if (credentials is null)
            return await FailBeforeDispatchAsync(order, artifact, config, "Delivery credentials could not be decrypted.", reconcileFailedAttempt, ct);

        // ── Concurrency claim (D-1) ───────────────────────────────────────────────
        // SLA timer: opening a fresh delivery attempt (re)starts the confirmation window
        // and clears any prior breach flag. The SLA sweep flags the order if this deadline
        // passes without a confirmed delivery.
        //
        // The status flip to 'delivering' is ALSO the delivery claim. A direct call
        // (alreadyClaimed: false — the first-deliver/redeliver/requeue path via DeliverOrderJob)
        // must claim ATOMICALLY: two concurrent activations for the SAME order (a double-clicked
        // Redeliver, a Redeliver racing an ops Requeue, or either racing a scheduled
        // RetryDeliveryJob) could each read a deliverable status and BOTH dispatch — the same PO
        // POSTed twice to a real supplier. Mirrors RetryDeliveryAsync's claim verbatim: a single
        // guarded ExecuteUpdateAsync inside one transaction; 0 rows ⇒ another worker already
        // claimed it (or it's already delivered/terminal) ⇒ a BENIGN no-op result, never a throw
        // and never a dispatch. The retry path enters with alreadyClaimed: true (RetryDeliveryAsync
        // already flipped the row to a FRESH 'delivering'), so it skips the claim and only stamps
        // the SLA window.
        //
        // The claim predicate is the CANONICAL DeliveryClaim.ClaimableForDispatch — the idle set
        // (OrderStatusMachine.ClaimableForDispatchFrom / ClaimableForAutomaticDispatchFrom, picked
        // by requireAutoDeliver) plus the shared stale-'delivering' reclaim (crash recovery). A
        // 'delivered'/terminal order is NOT claimable, so a redeliver on an already-delivered order
        // affects 0 rows and no-ops. A status missing from the set fails SILENTLY — the claim
        // matches 0 rows and returns the benign no-op success below, so the job logs success having
        // sent nothing (52c6431) — which is why the sets are named, pinned, and consumed by BOTH
        // provider branches from the one factory.
        //
        // delivery_unconfirmed is claimable ONLY when requireAutoDeliver is false — which is true
        // exactly when a human pressed a button: every requireAutoDeliver:false enqueue is
        // DeliverOrderJob.EnqueueRedeliver (OrdersController.Redeliver — the park's "Send again" —
        // and OpsController.RequeueDelivery), while every automatic activation goes through
        // DeliverOrderJob.Enqueue with true (TransformOrderJob, HangfireDeliveryDispatchEnqueuer /
        // the stranded-ready sweep).
        //
        // That gate exists because unconditional admission re-opened the park to one automatic
        // path the retry queue's park refusal (RetryDeliveryAsync) never sees: a dead Worker's
        // in-flight DeliverOrderJob is REFETCHED by Hangfire ~30 min later (see the Attempts=0
        // note on DeliverOrderJob) and re-runs this claim AFTER the stuck sweep's re-drive has
        // already parked the order. The parked attempt row is terminal ('unconfirmed'), so
        // OpenDispatchAttemptAsync re-adopts nothing, the re-adopt park guard below never fires,
        // and the refetch would open a fresh attempt and SEND the parked PO automatically — the
        // exact duplicate the park exists to prevent. This predicate, not the pre-claim status
        // reads, is the enforcement: the sweep can park the order between those reads and this
        // update. (A refetched EnqueueRedeliver activation can still claim the park its own crashed
        // send produced — that is the one re-execution of the operator's already-accepted send that
        // Hangfire's at-least-once refetch has always implied, not a new automatic path.)
        //
        // Safe against an immediate re-park: the park finalises its attempt row TERMINAL
        // ('unconfirmed'), and OpenDispatchAttemptAsync only re-adopts a 'dispatching' row — so this
        // re-send opens a FRESH attempt (reAdopted: false) and reaches the dispatcher.
        var dispatchStart = DateTime.UtcNow;
        var dueAt = dispatchStart + _reliability.SlaWindow;

        if (!alreadyClaimed)
        {
            var staleBefore = dispatchStart - DeliveringReclaimWindow;

            if (_db.Database.IsRelational())
            {
                await using var claimTx = await _db.Database.BeginTransactionAsync(ct);

                var claimed = await _db.PurchaseOrders
                    .Where(DeliveryClaim.ClaimableForDispatch(orgId, orderId, requireAutoDeliver, staleBefore))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.Status, OrderStatusConstants.Delivering)
                        .SetProperty(o => o.DeliveryDueAt, dueAt)
                        .SetProperty(o => o.SlaBreached, false)
                        .SetProperty(o => o.UpdatedAt, dispatchStart), ct);

                if (claimed == 0)
                {
                    // Another worker already claimed this order for delivery, or it was advanced out
                    // of a claimable state (e.g. already delivered) between our read and this update.
                    // Do NOT double-dispatch — return a BENIGN success no-op (no attempt row, no throw,
                    // no retry scheduled by DeliverOrderJob, which only checks result.Success).
                    await claimTx.RollbackAsync(ct);
                    _logger.LogInformation(
                        "Delivery {OrderId}: not claimed (already delivering/delivered/terminal, or parked "
                        + "awaiting an operator — an automatic activation never claims a park) — skipping dispatch.",
                        orderId);
                    // Success stays TRUE (a benign no-op — DeliverOrderJob returns on Success and must
                    // never log a "delivery failure" for a lost race). ClaimLost only records that
                    // nothing was dispatched; it does not change this path's control flow.
                    return new DeliveryResult(true, null, Outcome: DeliveryOutcome.ClaimLost);
                }

                await claimTx.CommitAsync(ct);

                // Keep the tracked entity consistent with the row just claimed (the bulk update
                // bypasses the change tracker) so PersistAttemptAsync's later status write diffs
                // correctly against the current AND original values.
                order.Status        = OrderStatusConstants.Delivering;
                order.DeliveryDueAt = dueAt;
                order.SlaBreached   = false;
                order.UpdatedAt     = dispatchStart;
                var entry = _db.Entry(order);
                entry.Property(x => x.Status).OriginalValue        = OrderStatusConstants.Delivering;
                entry.Property(x => x.DeliveryDueAt).OriginalValue = dueAt;
                entry.Property(x => x.SlaBreached).OriginalValue   = false;
                entry.Property(x => x.UpdatedAt).OriginalValue     = dispatchStart;
            }
            else
            {
                // EF InMemory test provider cannot translate ExecuteUpdateAsync / transactions —
                // emulate the claim through the change tracker (InMemory tests are single-threaded,
                // so the race the relational claim defends against cannot occur there). A non-claimable
                // status (e.g. already delivered) still no-ops, matching the relational 0-rows branch.
                // THE SAME predicate as the relational claim above, compiled and evaluated against
                // the loaded entity — the two branches drifting is the four-hardcoded-status-list
                // class that bit four times (AutomaticParkClaimPostgresTests pins the relational
                // branch; DeliveryServiceUnconfirmedParkTests pins this one). Deriving it means
                // this branch now enforces the staleness gate too: it previously accepted ANY
                // 'delivering' row, i.e. it was more permissive than production.
                if (!DeliveryClaim.ClaimableForDispatch(orgId, orderId, requireAutoDeliver, staleBefore)
                        .Compile()(order))
                {
                    _logger.LogInformation(
                        "Delivery {OrderId}: not claimed (status '{Status}' not claimable for this activation) — skipping dispatch.",
                        orderId, order.Status);
                    return new DeliveryResult(true, null, Outcome: DeliveryOutcome.ClaimLost);
                }

                order.Status = OrderStatusConstants.Delivering;
                order.DeliveryDueAt = dueAt;
                order.SlaBreached = false;
                order.UpdatedAt = dispatchStart;
                await _db.SaveChangesAsync(ct);
            }
        }
        else
        {
            // The retry path already holds the claim (status is a fresh 'delivering'); just (re)open
            // the SLA window for this attempt, exactly as before.
            order.Status = OrderStatusConstants.Delivering;
            order.DeliveryDueAt = dueAt;
            order.SlaBreached = false;
            order.UpdatedAt = dispatchStart;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Delivery {OrderId}: downloading artifact {FileKey} ({Protocol})",
            orderId, artifact.FileKey, config.Protocol);

        byte[] content;
        try
        {
            await using var stream = await _fileStorage.DownloadAsync(artifact.FileKey, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A thrown storage error (e.g. an R2 clock-skew signing failure) must become a
            // FAILED DeliveryResult, not an unhandled job exception: the persisted failed
            // attempt routes the order through the single RetryDeliveryJob backoff queue,
            // whereas a throw would leave it stranded in 'delivering' with no attempt row.
            _logger.LogWarning(ex,
                "Delivery {OrderId}: artifact download failed ({FileKey}).", orderId, artifact.FileKey);
            return await FailBeforeDispatchAsync(
                order, artifact, config,
                $"Artifact download failed: {ex.Message}", reconcileFailedAttempt, ct);
        }

        _logger.LogInformation(
            "Delivery {OrderId}: artifact downloaded ({Bytes} bytes); dispatching via {Protocol}",
            orderId, content.Length, config.Protocol);

        // Provenance: hash the payload bytes ACTUALLY dispatched (best-effort, never throws) —
        // comparing this to the artifact's stored ArtifactSha256 detects corruption in transit/storage.
        var dispatchedPayloadSha = ProvenanceHash.TrySha256Hex(content);

        // ── A3: attempt-started marker (the universal crash backstop) ─────────────
        // Persist + COMMIT a 'dispatching' attempt row (carrying the deterministic idempotency key)
        // BEFORE the actual send, so a crash AFTER the supplier accepts but BEFORE the terminal
        // outcome commits leaves a detectable in-flight row (the order stays 'delivering'). A later
        // stuck re-drive REUSES that row (matched on the same key) instead of opening a fresh attempt,
        // so the lost-ACK re-send cannot create a second terminal attempt row or silently burn the
        // retry budget — and it carries the SAME idempotency key, which a supporting supplier
        // de-duplicates (HTTP header / email Message-ID; SFTP/FTPS overwrite the deterministic file).
        var idempotencyKey = BuildIdempotencyKey(order.Id, artifact.Id);
        var (attempt, reAdopted) = await OpenDispatchAttemptAsync(order, artifact, config, idempotencyKey, ct);

        // ── The unknown-outcome park ──────────────────────────────────────────────
        // A re-adopted in-flight row means the previous activation ATTEMPTED this artifact and died
        // before learning the outcome. It does NOT prove a send happened: the marker is committed
        // before the network write, so a crash in between — or a cancelled token on shutdown, which
        // escapes the catch below — leaves the same row with nothing sent. The reachable states are
        // therefore "sent and accepted", "sent and rejected" and "never sent", and this process
        // cannot tell them apart: the crash destroyed the only transaction that could have recorded
        // it. On a channel that cannot de-duplicate (ERP: no dedupe signal reaches the endpoint;
        // email: caller-supplied Message-ID dedup is best-effort), re-sending on that ambiguity
        // would risk handing the supplier a duplicate PO. So we do not guess: park the order and let
        // a human — who can ask the supplier — decide. Safe/BestEffort channels re-drive unchanged.
        if (reAdopted && dispatcher.ResendSafety == ResendSafety.Unsafe)
            return await ParkUnconfirmedAsync(order, attempt, config, ct);

        DeliveryResult result;
        try
        {
            result = await dispatcher.DispatchAsync(
                content,
                BuildFileName(order, artifact),
                GetContentType(artifact.Format),
                config,
                credentials,
                ct,
                idempotencyKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Dispatchers are hardened to return failed results, but a throw here must
            // still land in the normal attempt/retry flow rather than escape the job.
            _logger.LogWarning(ex,
                "Delivery {OrderId}: dispatcher threw ({Protocol}).", orderId, config.Protocol);
            result = new DeliveryResult(false, $"Delivery dispatch failed: {ex.Message}");
        }

        _logger.LogInformation(
            "Delivery {OrderId}: dispatch returned success={Success} code={Code}",
            orderId, result.Success, result.ResponseCode);

        await PersistAttemptAsync(
            order, artifact, config, result, ct,
            reconcile: reconcileFailedAttempt,
            dispatchedPayloadSha256: dispatchedPayloadSha,
            existingAttempt: attempt);
        return result;
    }

    /// <summary>
    /// A3 — open (or, on crash recovery, re-adopt) the in-flight <c>dispatching</c> attempt row for a
    /// send, committing it BEFORE the actual dispatch so the send is crash-detectable. If a
    /// <c>dispatching</c> row already exists for this order+idempotency key (a prior activation sent
    /// but died before finalising), it is REUSED — the re-send stays the same logical attempt (same
    /// attempt number, same key), so it neither duplicates a terminal row nor consumes a retry-budget
    /// slot. Otherwise a fresh row is inserted with the next attempt number.
    /// <c>ReAdopted</c> tells the caller which case happened — a re-adopted row on a channel that
    /// cannot de-duplicate a re-send must be PARKED, not re-sent (see the unknown-outcome park below).
    /// </summary>
    private async Task<(DeliveryAttempt Attempt, bool ReAdopted)> OpenDispatchAttemptAsync(
        PurchaseOrderEntity order,
        OutboundArtifact artifact,
        SupplierDeliveryConfig config,
        string idempotencyKey,
        CancellationToken ct)
    {
        var existing = await _db.DeliveryAttempts
            .Where(a => a.OrderId == order.Id && a.OrgId == order.OrgId
                     && a.Status == DeliveryAttempt.StatusDispatching
                     && a.IdempotencyKey == idempotencyKey)
            .OrderByDescending(a => a.AttemptedAt)
            .FirstOrDefaultAsync(ct);

        // Re-adopting an in-flight row IS the "we already sent this artifact, and never learned
        // the outcome" signal — the row was committed before the send, so its survival means the
        // send was attempted and the process died before finalising it.
        if (existing is not null)
            return (existing, true);

        // Next 1-based attempt index. Only TERMINAL attempts count toward the number (a stale
        // 'dispatching' row for a DIFFERENT artifact must not inflate it).
        //
        // Deliberately NOT DeliveryAttempt.CountsAgainstCap: numbering counts attempts EVER, so it
        // must IGNORE CapSupersededAt — an ops requeue grants a fresh BUDGET, not a fresh HISTORY.
        // Filtering superseded rows here would restart the number at 1 after a requeue, which lies
        // in supplier-visible provenance ("attempt 1" for the fourth real send) and breaks
        // OpsController's OrderBy(AttemptNumber) total order. The two predicates agreeing before
        // CapSupersededAt existed was coincidence, not equivalence.
        var attemptNumber = (await _db.DeliveryAttempts
            .CountAsync(a => a.OrderId == order.Id && a.OrgId == order.OrgId
                          && a.Status != DeliveryAttempt.StatusDispatching, ct)) + 1;

        var attempt = new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            OrgId = order.OrgId,
            Channel = config.Protocol,
            Destination = GetDestination(config),
            Status = DeliveryAttempt.StatusDispatching,
            AttemptNumber = attemptNumber,
            AttemptedAt = DateTime.UtcNow,
            IdempotencyKey = idempotencyKey,
            // Provenance stamped up-front so a crashed (never-finalised) row still records which
            // revision/config produced the artifact it was sending.
            ConnectionRevisionId = artifact.ConnectionRevisionId ?? order.ConnectionRevisionId,
            ConfigDigest = artifact.ConfigDigest,
        };
        _db.DeliveryAttempts.Add(attempt);
        // COMMIT before the send — this is the crash-detectable marker.
        await _db.SaveChangesAsync(ct);
        return (attempt, false);
    }

    /// <summary>
    /// The unknown-outcome park: finalise the re-adopted in-flight row as
    /// <c>unconfirmed</c> and stop. NO send occurs. The result is
    /// <see cref="DeliveryOutcome.NotRetryable"/>, which makes NO retry scheduled true too: both
    /// <c>RetryDeliveryJob</c>/<c>DeliverOrderJob</c> stop on that marker before touching the backoff
    /// queue, <c>RetryDeliveryAsync</c>'s cap-edge dead-letter is guarded on it (so a park on the LAST
    /// allowed attempt still parks rather than dead-lettering), and <c>RetryDeliveryAsync</c>
    /// independently refuses this order's new status (<c>delivery_unconfirmed</c>). The order simply
    /// waits for an operator to either send it again or confirm the supplier received it.
    /// <para>
    /// Unlike the other <see cref="DeliveryOutcome.NotRetryable"/> paths, this one DOES advance the
    /// attempt count — the re-adopted row is finalised here, not abandoned. That is consistent: the
    /// marker states who owns restarting the order (a human), not whether a row was written.
    /// </para>
    /// <para>
    /// This method does not itself write <c>DeliveryDueAt</c>/<c>SlaBreached</c> — but they are NOT
    /// left at whatever a prior breach set them to. The claim earlier in this dispatch
    /// (<c>DispatchArtifactAsync</c>'s <c>alreadyClaimed</c> branch, which runs before this park is
    /// reached) already reset them: <c>SlaBreached = false</c> and <c>DeliveryDueAt = now +
    /// SlaWindow</c>. So a stuck order that had already breached its SLA before this crash-recovery
    /// re-drive silently un-breaches and gets a fresh window — deliberately: a parked order SHOULD
    /// keep nagging until a human resolves it, just on a renewed timer rather than the old one.
    /// </para>
    /// </summary>
    private async Task<DeliveryResult> ParkUnconfirmedAsync(
        PurchaseOrderEntity order,
        DeliveryAttempt attempt,
        SupplierDeliveryConfig config,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var message = BuildUnconfirmedMessage(config.Protocol);

        attempt.Status = DeliveryAttempt.StatusUnconfirmed;
        attempt.AttemptedAt = now;
        attempt.ErrorMessage = message;

        order.Status = OrderStatusConstants.DeliveryUnconfirmed;
        order.UpdatedAt = now;

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            orderId = order.Id,
            fromStatus = OrderStatusConstants.Delivering,
            toStatus = OrderStatusConstants.DeliveryUnconfirmed,
            channel = config.Protocol,
            idempotencyKey = attempt.IdempotencyKey,
            parkedAt = now,
            detail = "Crash-recovery re-drive on a channel that cannot de-duplicate a re-send. "
                   + "The artifact may have been sent, but the outcome was never observed; "
                   + "re-sending could duplicate the PO, so the order is parked for an operator decision.",
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OrgId = order.OrgId,
            UserId = null,
            EntityType = "Order",
            EntityId = order.Id,
            Action = "DeliveryUnconfirmed",
            Payload = System.Text.Json.JsonDocument.Parse(payload),
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        // Reconcile like every other terminal delivery status. Two things ride on this: the park
        // gets an exception row (without one a parked order is invisible to the tiles/dashboard —
        // "not all clear" over a grid of zeros), and a 'delivery_failed' exception left open by the
        // attempt that preceded the park is auto-resolved. That second one is the sharp edge: an
        // order asserting both "delivery failed" (error) and "unconfirmed" (warning) invites the
        // operator to believe the red one and re-send — the duplicate PO this park exists to stop.
        await SafeReconcileExceptionsAsync(order.OrgId, order.Id, ct);

        _logger.LogWarning(
            "DeliveryUnconfirmed: order {OrderId} (org {OrgId}) parked — a crash-recovery re-drive "
            + "re-adopted an in-flight send on {Protocol}, which cannot de-duplicate a re-send. "
            + "NOT re-sent; waiting for an operator to send again or mark delivered.",
            order.Id, order.OrgId, config.Protocol);

        return new DeliveryResult(false, message, ResponseCode: null, ResponseBody: null,
            Outcome: DeliveryOutcome.NotRetryable);
    }

    /// <summary>
    /// The operator-facing park sentence. Plain language, one sentence of what happened plus what
    /// to do — never internal vocabulary (no "idempotency", "re-adopt", "dispatching row", "park").
    /// Says only what a re-adopted in-flight row PROVES: the send was ATTEMPTED, not that it
    /// succeeded — a crash between the marker commit and the network write, or a cancelled token on
    /// shutdown, parks with no send at all. Never fabricate an observed outcome.
    /// <para>
    /// A null <paramref name="protocol"/> yields the channel-agnostic wording, for callers that know
    /// the order is parked but not which channel parked it (<c>OrderExceptionService.ProblemFor</c>
    /// works from order status alone). Shared rather than restated: two operator surfaces telling
    /// different stories about the same order is the failure this whole feature guards against.
    /// </para>
    /// </summary>
    internal static string BuildUnconfirmedMessage(string? protocol) =>
        $"Delivery unconfirmed. We may have sent this order, but lost the connection before the "
        + $"supplier confirmed it, and {DescribeChannel(protocol)} cannot tell us whether it arrived. "
        + $"Check with the supplier, then either send it again or mark it delivered.";

    private static string DescribeChannel(string? protocol) => protocol?.ToLowerInvariant() switch
    {
        "email" or "smtp" => "email",
        "erp_erply" => "the Erply connection",
        "erp_directo" => "the Directo connection",
        _ => "this delivery channel",
    };

    /// <summary>
    /// Launch batch 7 — the delivery config that GOVERNS this dispatch. When the
    /// <c>Connections:RevisionAuthority</c> flag is ON and the artifact/order pin resolves to a
    /// revision with a NON-BLANK delivery protocol, a detached (never persisted) config is built
    /// from the revision snapshot: protocol, non-secret config JSON, auto-deliver, and the
    /// VERBATIM-copied encrypted credentials (<c>CredentialsRef</c>) — decrypted by the exact same
    /// <see cref="DeliveryEncryptionService"/> path as a live row. A revision with no delivery
    /// channel (blank protocol — e.g. a backfilled rev-1 for a supplier configured later) falls
    /// back to the LIVE supplier delivery config, logged. Flag off / unpinned / orphan pin →
    /// live config, byte-identical to the pre-batch-7 behaviour.
    /// </summary>
    private async Task<SupplierDeliveryConfig?> ResolveEffectiveDeliveryConfigAsync(
        Guid orgId, PurchaseOrderEntity order, OutboundArtifact artifact, CancellationToken ct)
    {
        if (_effectiveConfig is not null)
        {
            // Prefer the ARTIFACT's pin (stamped at transform from the same order pin) — those are
            // the exact bytes being dispatched; fall back to the order's pin for older artifacts.
            var effective = await _effectiveConfig.ResolveAsync(
                orgId, artifact.ConnectionRevisionId ?? order.ConnectionRevisionId, ct);

            if (effective.IsRevision)
            {
                if (!string.IsNullOrWhiteSpace(effective.DeliveryProtocol))
                {
                    _logger.LogInformation(
                        "Order {OrderId}: delivery channel taken from pinned {Source} (protocol {Protocol}).",
                        order.Id, effective.Source, effective.DeliveryProtocol);

                    // Detached snapshot view — NEVER added to the DbContext, only read by the
                    // dispatcher / attempt-persistence below.
                    return new SupplierDeliveryConfig
                    {
                        Id                   = Guid.Empty,
                        OrgId                = orgId,
                        // An order only reaches delivery once routed, so SupplierId is set here;
                        // coalesce defensively so the delivery path can never NRE on the snapshot view.
                        SupplierId           = order.SupplierId ?? Guid.Empty,
                        Protocol             = effective.DeliveryProtocol!,
                        AutoDeliver          = effective.DeliveryAutoDeliver,
                        ConfigJson           = string.IsNullOrWhiteSpace(effective.DeliveryConfigJson)
                                                   ? "{}"
                                                   : effective.DeliveryConfigJson!,
                        EncryptedCredentials = effective.CredentialsRef ?? string.Empty,
                    };
                }

                _logger.LogInformation(
                    "Order {OrderId}: pinned {Source} has no delivery channel — using the live supplier delivery config.",
                    order.Id, effective.Source);
            }
        }

        return await _db.SupplierDeliveryConfigs
            .Where(x => x.OrgId == orgId && x.SupplierId == order.SupplierId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<DeliveryTestResult> TestFireAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var config = await _db.SupplierDeliveryConfigs
            .Where(x => x.OrgId == orgId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        if (config is null)
            return new DeliveryTestResult(false, "Delivery config not found.", null);

        if (!_dispatchers.TryGetValue(config.Protocol, out var dispatcher))
            return new DeliveryTestResult(false, "No dispatcher registered for delivery protocol.", null);

        var credentials = string.IsNullOrWhiteSpace(config.EncryptedCredentials)
            ? string.Empty
            : _encryption.Decrypt(config.EncryptedCredentials);

        if (credentials is null)
            return new DeliveryTestResult(false, "Delivery credentials could not be decrypted.", null);

        var result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes("test,from\r\nproculink,true\r\n"),
            "proculink-test.csv",
            "text/csv",
            config,
            credentials,
            ct);

        _db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = null,
            OrgId = orgId,
            Channel = config.Protocol,
            Destination = GetDestination(config),
            Status = result.Success ? "success" : "failed",
            AttemptNumber = 0, // test-fire is not part of an order's retry sequence
            AttemptedAt = DateTime.UtcNow,
            ResponseCode = result.ResponseCode,
            ErrorMessage = result.Success ? null : result.ErrorMessage,
        });

        await _db.SaveChangesAsync(ct);

        return new DeliveryTestResult(result.Success, result.ErrorMessage, result.ResponseCode);
    }

    private async Task<DeliveryResult> FailBeforeDispatchAsync(
        PurchaseOrderEntity order,
        OutboundArtifact artifact,
        SupplierDeliveryConfig config,
        string error,
        bool reconcile,
        CancellationToken ct)
    {
        var result = new DeliveryResult(false, error);
        await PersistAttemptAsync(order, artifact, config, result, ct, reconcile: reconcile);
        return result;
    }

    private async Task<DeliveryResult> FailMissingConfigAsync(
        PurchaseOrderEntity order,
        OutboundArtifact artifact,
        bool reconcile,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        const string error = "Supplier delivery config is missing. Add a delivery endpoint before sending this order.";
        var result = new DeliveryResult(false, error);

        order.Status = OrderStatusConstants.DeliveryFailed;
        order.UpdatedAt = now;

        var attemptNumber = (await _db.DeliveryAttempts
            .CountAsync(a => a.OrderId == order.Id && a.OrgId == order.OrgId, ct)) + 1;

        _db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            OrgId = order.OrgId,
            Channel = "missing_config",
            Destination = "supplier delivery config",
            Status = "failed",
            AttemptNumber = attemptNumber,
            AttemptedAt = now,
            ErrorMessage = error,
            // Provenance: copy what we know even on a pre-dispatch failure. No payload was
            // downloaded, so there is honestly no dispatched-payload hash.
            ConnectionRevisionId = artifact.ConnectionRevisionId ?? order.ConnectionRevisionId,
            ConfigDigest = artifact.ConfigDigest,
            ArtifactSha256 = null,
        });

        await _db.SaveChangesAsync(ct);

        if (reconcile)
            await SafeReconcileExceptionsAsync(order.OrgId, order.Id, ct);

        // Awaited (not fire-and-forget): IntegrationTriggerService shares this scoped
        // DbContext, so a detached `_ =` task would race the next query on _db and throw
        // "A second operation was started on this context instance".
        await _integrationTrigger.EnqueueAsync(
            order.OrgId,
            "order.failed",
            new { order_id = order.Id, failed_at = now, error },
            ct);

        _logger.LogWarning(
            "Delivery attempt for order {OrderId}, artifact {ArtifactId} failed: missing supplier delivery config.",
            order.Id,
            artifact.Id);

        return result;
    }

    private async Task PersistAttemptAsync(
        PurchaseOrderEntity order,
        OutboundArtifact artifact,
        SupplierDeliveryConfig config,
        DeliveryResult result,
        CancellationToken ct,
        bool reconcile = true,
        string? dispatchedPayloadSha256 = null,
        DeliveryAttempt? existingAttempt = null)
    {
        var now = DateTime.UtcNow;

        // Distinguish supplier rejection (4xx) from transient failure (5xx / network).
        // A 4xx means the supplier received and explicitly rejected the payload —
        // retrying the exact same payload will not help without a content fix.
        var isSupplierRejection = !result.Success
            && result.ResponseCode.HasValue
            && result.ResponseCode.Value is >= 400 and <= 499;

        order.Status = result.Success
            ? OrderStatusConstants.Delivered
            : isSupplierRejection
                ? OrderStatusConstants.RejectedBySupplier
                : OrderStatusConstants.DeliveryFailed;
        order.UpdatedAt = now;

        // SLA timer: a terminal supplier outcome closes the SLA window — clear the deadline and breach
        // flag so the sweep can never nag a settled order. A confirmed delivery settles it, and so does
        // a 4xx rejection: the supplier received and explicitly refused the payload, so the order is as
        // done as a delivered one (retrying the same bytes will not help). A 5xx / transient failure is
        // NOT terminal — the order still owes a delivery and its window must keep ticking, so it is
        // deliberately excluded here. rejected_by_supplier is likewise excluded from DeliverySlaService's
        // sweep (belt-and-braces for legacy rows), mirroring Delivered and DeliveryDeadLetter.
        if (result.Success || isSupplierRejection)
        {
            order.DeliveryDueAt = null;
            order.SlaBreached = false;
        }

        var status = result.Success ? DeliveryAttempt.StatusSuccess : DeliveryAttempt.StatusFailed;

        if (existingAttempt is not null)
        {
            // A3 finalize path: flip the in-flight 'dispatching' row (opened + committed before the
            // send) to its terminal outcome IN PLACE. Channel/Destination/AttemptNumber/IdempotencyKey
            // and revision provenance were stamped at open; only the outcome fields change here. On a
            // crash-recovery re-send this is the SAME row, so no second terminal attempt is created.
            existingAttempt.Status = status;
            existingAttempt.AttemptedAt = now;
            existingAttempt.ResponseCode = result.ResponseCode;
            existingAttempt.ErrorMessage = result.Success ? null : result.ErrorMessage;
            existingAttempt.RejectionReason = isSupplierRejection ? result.ErrorMessage : null;
            existingAttempt.ResponseBody = TruncateResponseBody(result.ResponseBody);
            existingAttempt.AcknowledgedAt = result.Success ? now : null;
            existingAttempt.ArtifactSha256 = dispatchedPayloadSha256 ?? existingAttempt.ArtifactSha256;
        }
        else
        {
            // Fresh terminal row — the pre-dispatch failure paths (no send happened, so no in-flight
            // marker). 1-based index counts only TERMINAL attempts so a dangling 'dispatching' row
            // never inflates the number. NOT the cap predicate — numbering counts attempts EVER and
            // ignores CapSupersededAt (see OpenDispatchAttemptAsync's numbering comment).
            var attemptNumber = (await _db.DeliveryAttempts
                .CountAsync(a => a.OrderId == order.Id && a.OrgId == order.OrgId
                              && a.Status != DeliveryAttempt.StatusDispatching, ct)) + 1;

            _db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrgId = order.OrgId,
                Channel = config.Protocol,
                Destination = GetDestination(config),
                Status = status,
                AttemptNumber = attemptNumber,
                AttemptedAt = now,
                ResponseCode = result.ResponseCode,
                ErrorMessage = result.Success ? null : result.ErrorMessage,
                RejectionReason = isSupplierRejection ? result.ErrorMessage : null,
                // Rejection capture: persist the supplier's raw NACK body verbatim (bounded).
                ResponseBody = TruncateResponseBody(result.ResponseBody),
                // ACK round-trip: stamp the confirmation time on a successful dispatch.
                AcknowledgedAt = result.Success ? now : null,
                // Provenance: which connection revision/config produced the artifact this attempt
                // dispatched, plus the SHA-256 of the payload bytes actually sent (null when the
                // attempt failed before the payload was downloaded).
                ConnectionRevisionId = artifact.ConnectionRevisionId ?? order.ConnectionRevisionId,
                ConfigDigest = artifact.ConfigDigest,
                ArtifactSha256 = dispatchedPayloadSha256,
            });
        }

        await _db.SaveChangesAsync(ct);

        // Reconcile exceptions against the new order status (delivered clears delivery
        // exceptions; delivery_failed / rejected open the matching exception). Skipped on a
        // failed attempt that the caller will immediately dead-letter: DeadLetterAsync runs
        // its own reconcile, so reconciling here would only write a transient delivery_failed
        // exception that the very next reconcile resolves. A successful attempt always
        // reconciles regardless of the flag.
        if (reconcile || result.Success)
            await SafeReconcileExceptionsAsync(order.OrgId, order.Id, ct);

        // ── Wave 4: fire order.delivered / order.failed triggers ──────────────────
        if (result.Success)
        {
            // Check whether this is the org's FIRST delivered order. The current order's status
            // has just been saved as 'delivered' above; exclude it by id.
            var hadOtherDeliveredOrders = await _db.PurchaseOrders
                .AnyAsync(o => o.OrgId == order.OrgId
                            && o.Id != order.Id
                            && o.Status == OrderStatusConstants.Delivered, ct);

            if (!hadOtherDeliveredOrders)
            {
                await _analytics.CaptureAsync(
                    organisationId: order.OrgId,
                    userId: null,
                    eventName: "first_delivery_succeeded",
                    properties: new Dictionary<string, object?>
                    {
                        ["order_id"] = order.Id,
                        ["protocol"] = config.Protocol,
                    },
                    ct: ct);
            }

            await _integrationTrigger.EnqueueAsync(
                order.OrgId,
                "order.delivered",
                new { order_id = order.Id, delivered_at = now, acknowledged_at = now },
                ct);
        }
        else if (isSupplierRejection)
        {
            await _integrationTrigger.EnqueueAsync(
                order.OrgId,
                "order.rejected",
                new { order_id = order.Id, rejected_at = now, reason = result.ErrorMessage },
                ct);
        }
        else
        {
            await _integrationTrigger.EnqueueAsync(
                order.OrgId,
                "order.failed",
                new { order_id = order.Id, failed_at = now, error = result.ErrorMessage },
                ct);
        }

        _logger.LogInformation(
            "Delivery attempt for order {OrderId}, artifact {ArtifactId}: {Status}",
            order.Id,
            artifact.Id,
            result.Success ? "success" : "failed");
    }

    private static string BuildFileName(PurchaseOrderEntity order, OutboundArtifact artifact)
    {
        var extension = artifact.Format switch
        {
            "xml" => "xml",
            "csv" => "csv",
            "json" => "json",
            _ => "dat",
        };

        return $"{SanitizeFileToken(order.PoNumber)}.{extension}";
    }

    private static string SanitizeFileToken(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "order" : sanitized;
    }

    private static string GetContentType(string format) => format switch
    {
        "xml" => "application/xml",
        "json" => "application/json",
        "csv" => "text/csv",
        _ => "application/octet-stream",
    };

    private static string? TruncateResponseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        return body.Length <= DeliveryAttempt.MaxResponseBodyLength
            ? body
            : body[..DeliveryAttempt.MaxResponseBodyLength];
    }

    private static string GetDestination(SupplierDeliveryConfig config)
    {
        try
        {
            using var doc = JsonDocument.Parse(config.ConfigJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("url", out var url)) return url.GetString() ?? config.Protocol;
            if (root.TryGetProperty("host", out var host)) return host.GetString() ?? config.Protocol;
        }
        catch (JsonException)
        {
            // Config validation happens at save time. Keep attempts safe if old data is malformed.
        }

        return config.Protocol;
    }

    public async Task<DeliveryResult> RetryDeliveryAsync(
        Guid orgId,
        Guid orderId,
        int maxAttempts,
        CancellationToken ct)
    {
        if (maxAttempts < 1) maxAttempts = 1;

        var order = await _db.PurchaseOrders
            .Where(x => x.Id == orderId && x.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        // Every early return below is NotRetryable: no dispatch, no attempt row, and no later retry
        // can change the answer. The frozen attempt count means rescheduling any of them loops
        // forever against an order the retry is powerless to move (see DeliveryOutcome.NotRetryable).
        if (order is null)
            return new DeliveryResult(false, "Order not found.", Outcome: DeliveryOutcome.NotRetryable);

        if (order.Status == OrderStatusConstants.DeliveryDeadLetter)
            return new DeliveryResult(false, "Order is in dead-letter state — retries are exhausted.",
                Outcome: DeliveryOutcome.NotRetryable);

        if (order.Status == OrderStatusConstants.Delivered)
            return new DeliveryResult(true, null, Outcome: DeliveryOutcome.NotRetryable);

        // An order that is ALREADY parked (a retry scheduled before the park, firing after it). The
        // generic status check below would refuse it as NotRetryable anyway — this branch exists only
        // to say WHY in words: its message is what the retry job writes to the log, and "Order status
        // 'delivery_unconfirmed' is not retryable." names an internal status instead of the decision
        // a human still owes. Never re-driven automatically: re-sending an unobserved outcome on a
        // channel that cannot de-duplicate risks a duplicate PO.
        if (order.Status == OrderStatusConstants.DeliveryUnconfirmed)
            return new DeliveryResult(
                false,
                "This order is waiting for someone to decide whether to send it again or mark it delivered.",
                Outcome: DeliveryOutcome.NotRetryable);

        // Not retryable = terminal, or waiting on something only another actor can clear:
        // 'delivery_held' waits on the billing reactivation release (ReleaseBillingHeldOrdersAsync —
        // which re-drives it, or restores a held park to its human), never on a backoff attempt.
        //
        // Derived from the canonical retry set, PLUS 'delivering' — deliberately admitted here
        // WITHOUT the claim's staleness check, because this advisory gate classifies transient vs
        // terminal while the CLAIM below decides ownership. A fresh 'delivering' must fall through
        // to the claim and come back ClaimLost (transient — keep rescheduling), never be refused
        // here as NotRetryable (terminal): the stuck sweep stamps UpdatedAt = now before enqueuing
        // its re-drive, so the re-driven retry ALWAYS meets a fresh row, and refusing it here would
        // turn "delivered one backoff later" into "never delivered". Pinned by
        // CrashedHolderRecoveryCompositionPostgresTests.
        if (!OrderStatusMachine.ClaimableForRetryFrom.Contains(order.Status)
            && order.Status != OrderStatusConstants.Delivering)
            return new DeliveryResult(false, $"Order status '{order.Status}' is not retryable.",
                Outcome: DeliveryOutcome.NotRetryable);

        // Resolve the artifact BEFORE the claim: a missing artifact is a side-effect-free
        // early return, so it must not first flip the order into 'delivering' and strand it.
        var artifact = await _db.OutboundArtifacts
            .Where(a => a.OrderId == orderId && a.OrgId == orgId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (artifact is null)
            return new DeliveryResult(false, "No outbound artifact found. Transform the order before retrying delivery.",
                Outcome: DeliveryOutcome.NotRetryable);

        // ── A5: billing gate on the retry path ────────────────────────────────────
        // A backoff retry can fire LONG after the first delivery attempt — by which time the org may
        // have lapsed to read_only / past_due / cancelled / trial_expired. DeliverOrderJob gates the
        // FIRST delivery on the same check; the retry path must mirror it exactly, or a lapsed org's
        // order would deliver anyway and meter the €0.50 overage. Checked BEFORE the 'delivering'
        // claim so HoldForBillingAsync can move the still-idle order straight to the explicit,
        // auto-releasing 'delivery_held' state (re-driven on reactivation via
        // ReleaseBillingHeldOrdersAsync) — never a silent strand, never a delivery.
        //
        // Returns a BENIGN success no-op (matching the already-delivered / claim-lost convention) so
        // RetryDeliveryJob does NOT schedule another backoff attempt for a held order — the
        // reactivation re-drive owns getting it moving again.
        if (_billing is not null && !await _billing.CanProcessOrdersAsync(orgId, ct))
        {
            var held = await HoldForBillingAsync(orgId, orderId, ct);
            _logger.LogWarning(
                "RetryDeliveryAsync: order {OrderId} (org {OrgId}) NOT delivered — org cannot process orders (billing). Held={Held}; awaiting reactivation re-drive.",
                orderId, orgId, held);
            return new DeliveryResult(true, null, Outcome: DeliveryOutcome.NotRetryable);
        }

        // ── Concurrency claim ──────────────────────────────────────────────────
        // The status/attempt-count reads above are advisory only: two concurrent retries
        // for the SAME order (a duplicated Hangfire activation racing the operator "Retry
        // now" button, or a backoff-scheduled job firing alongside a manual one) could each
        // see a deliverable status + an under-cap count and BOTH proceed to dispatch — a
        // double-delivered PO (or two clobbering terminal statuses). Atomically CLAIM the
        // order by flipping it to 'delivering' in a single guarded statement and reading the
        // resulting attempt count INSIDE the same transaction. Under Postgres READ COMMITTED
        // the second worker's UPDATE blocks on the first's row lock, then re-evaluates the
        // WHERE against the COMMITTED row, so exactly one claim succeeds and the cap check +
        // dispatch decision are made exactly once. (Mirrors OrderTransformService's
        // ready/transforming → transforming claim and the ParseStoredFileAsync
        // single-transaction-around-the-bulk-ops idiom: ExecuteUpdate auto-commits
        // independently of a later SaveChanges, so it must enlist in ONE transaction to be
        // atomic with the count read.)
        //
        // The claim predicate is the CANONICAL DeliveryClaim.ClaimableForRetry —
        // OrderStatusMachine.ClaimableForRetryFrom (an idle, retry-ready order; deliberately
        // excludes the delivery_unconfirmed park, which only a human's redeliver claims) OR a
        // 'delivering' row that has gone STALE (UpdatedAt older than the reclaim window). The
        // staleness gate is what makes the claim mutually exclusive against a CONCURRENT retry:
        // a fresh claim stamps UpdatedAt = now, so the racing worker that unblocks and sees the
        // row already 'delivering' with a just-stamped timestamp is OUTSIDE the window → 0 rows
        // → it bows out instead of double-dispatching. A genuinely STUCK delivering order
        // (StuckDeliveryDetectionService only re-drives rows stuck for minutes — far past this
        // window) is still reclaimable, so crash recovery is unaffected.
        var staleBefore = DateTime.UtcNow - DeliveringReclaimWindow;
        int priorAttempts;
        if (_db.Database.IsRelational())
        {
            await using var claimTx = await _db.Database.BeginTransactionAsync(ct);

            var claimedAt = DateTime.UtcNow;
            var claimed = await _db.PurchaseOrders
                .Where(DeliveryClaim.ClaimableForRetry(orgId, orderId, staleBefore))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatusConstants.Delivering)
                    .SetProperty(o => o.UpdatedAt, claimedAt), ct);

            if (claimed == 0)
            {
                // Another worker already claimed this order for delivery (or it was advanced
                // out of a claimable state) between our read and this update — do NOT
                // double-dispatch.
                //
                // ClaimLost, NOT NotRetryable — the caller MUST keep retrying. The tempting reasoning
                // ("the holder owns this send, so stay quiet") is disproven by
                // CrashedHolderRecoveryCompositionPostgresTests: if the holder DIES,
                // StuckDeliveryDetectionService re-drives the order but bumps UpdatedAt to now first,
                // so the re-driven retry lands right back HERE (fresh 'delivering' ⇒ 0 rows). The
                // scheduled backoff is what carries the order past the reclaim window so a later
                // attempt can claim the now-stale row. Stop rescheduling and a crashed holder's PO is
                // never sent — it just burns the sweep's requeue budget and dead-letters.
                await claimTx.RollbackAsync(ct);
                return new DeliveryResult(false, "Delivery for this order is already in progress.",
                    Outcome: DeliveryOutcome.ClaimLost);
            }

            // Count is read inside the SAME transaction as the claim, so the cap decision is
            // made against the row this worker now exclusively holds. Composes the ONE cap
            // predicate (DeliveryAttempt.CountsAgainstCap): an in-flight 'dispatching' row (a
            // crash-recovery re-adopt of THIS send) must not burn a retry-budget slot, and an
            // ops-requeue-superseded row belongs to a PREVIOUS budget.
            priorAttempts = await _db.DeliveryAttempts
                .Where(a => a.OrderId == orderId && a.OrgId == orgId)
                .Where(DeliveryAttempt.CountsAgainstCap)
                .CountAsync(ct);

            await claimTx.CommitAsync(ct);

            // Keep the tracked entity consistent with the row just claimed (the bulk update
            // bypasses the change tracker) so DeadLetterAsync / downstream status writes diff
            // correctly against current AND original values.
            order.Status    = OrderStatusConstants.Delivering;
            order.UpdatedAt = claimedAt;
            var entry = _db.Entry(order);
            entry.Property(x => x.Status).OriginalValue    = OrderStatusConstants.Delivering;
            entry.Property(x => x.UpdatedAt).OriginalValue = claimedAt;
        }
        else
        {
            // EF InMemory test provider cannot translate ExecuteUpdateAsync / transactions —
            // emulate the claim through the change tracker (InMemory tests are single-threaded,
            // so the race the relational claim defends against cannot occur there). THE SAME
            // predicate as the relational claim above, compiled against the loaded entity. This
            // branch previously flipped UNCONDITIONALLY — no status gate at all — so InMemory
            // could never reproduce a lost claim and was more permissive than production.
            // Mirrors the relational 0-rows return exactly: ClaimLost, never NotRetryable — the
            // reschedule it triggers IS crash recovery (see the relational branch's comment).
            if (!DeliveryClaim.ClaimableForRetry(orgId, orderId, staleBefore).Compile()(order))
                return new DeliveryResult(false, "Delivery for this order is already in progress.",
                    Outcome: DeliveryOutcome.ClaimLost);

            order.Status    = OrderStatusConstants.Delivering;
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            priorAttempts = await _db.DeliveryAttempts
                .Where(a => a.OrderId == orderId && a.OrgId == orgId)
                .Where(DeliveryAttempt.CountsAgainstCap)
                .CountAsync(ct);
        }

        if (priorAttempts >= maxAttempts)
        {
            // NotRetryable: dead-lettered without dispatching. The job's cap guard also stops here
            // (the count IS at the cap), but marking it keeps the contract honest — the retry queue
            // must never resume a dead-lettered order.
            await DeadLetterAsync(order, priorAttempts, lastError: "Maximum delivery attempts reached.", ct);
            return new DeliveryResult(false, "Maximum delivery attempts reached — order moved to dead-letter.",
                Outcome: DeliveryOutcome.NotRetryable);
        }

        // If this attempt is the last one allowed, a failure will dead-letter immediately —
        // DeadLetterAsync runs its own reconcile, so suppress the per-attempt reconcile to
        // avoid writing a transient delivery_failed exception that gets resolved milliseconds
        // later. A successful attempt still reconciles inside DispatchArtifactAsync.
        var willDeadLetterOnFailure = priorAttempts + 1 >= maxAttempts;

        var result = await DispatchArtifactAsync(
            orgId, orderId, artifact.Id,
            requireAutoDeliver: false,
            reconcileFailedAttempt: !willDeadLetterOnFailure,
            // RetryDeliveryAsync already made the atomic 'delivering' claim above — do NOT claim again
            // (a second claim would reject the fresh-'delivering' row it just set and break the retry).
            alreadyClaimed: true,
            ct);

        // Dead-lettering asserts "this attempt failed AND it was the last one allowed". That is false
        // for every NotRetryable result: none of them reached a dispatcher, so no attempt failed here
        // at all. Guarding on the marker rather than on the park alone is therefore the honest rule,
        // and it is what keeps the retry axis single-mechanism — a park-specific re-check of the
        // order status would rebuild the very flag this branch was collapsed out of.
        //
        // The park (ParkUnconfirmedAsync) is why the guard exists: Success=false, but a DEFERRAL to a
        // human, not a failure. Without it, the crashed send being the LAST allowed attempt (this
        // branch) lets DeadLetterAsync fire immediately after the park and overwrite every one of its
        // constraints — the order flips from 'delivery_unconfirmed' to 'delivery_dead_letter',
        // DeliveryDueAt is nulled (killing the SLA nag the park deliberately leaves running), the
        // order becomes permanently non-retryable (blocking the operator's "Send again"), and the
        // audit trail fabricates "retries exhausted" over an attempt row that says 'unconfirmed'.
        //
        // The only other NotRetryable reachable here is the artifact/order vanishing mid-retry (a
        // concurrent erase, racing this method's own lookup). Dead-lettering that would write the same
        // false "retries exhausted" audit — onto a row that was just deleted.
        if (!result.Success && willDeadLetterOnFailure && result.Outcome != DeliveryOutcome.NotRetryable)
            await DeadLetterAsync(order, priorAttempts + 1, lastError: result.ErrorMessage, ct);

        return result;
    }

    public Task<int> CountDeliveryAttemptsAsync(Guid orgId, Guid orderId, CancellationToken ct) =>
        // THE cap count — composes DeliveryAttempt.CountsAgainstCap (the one definition: terminal
        // AND not superseded by an ops requeue), so the retry-queue backoff step + cap detection
        // agree with RetryDeliveryAsync's count and the stranded-failed sweep's subquery.
        _db.DeliveryAttempts
            .Where(a => a.OrderId == orderId && a.OrgId == orgId)
            .Where(DeliveryAttempt.CountsAgainstCap)
            .CountAsync(ct);

    public async Task<bool> HoldForBillingAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        var order = await _db.PurchaseOrders
            .Where(o => o.Id == orderId && o.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        // Holdable = an idle, send-ready order that has NOT yet been claimed for this dispatch:
        //   • ready_to_deliver     — DeliverOrderJob's first-delivery billing gate (transform just done).
        //   • delivery_failed      — RetryDeliveryAsync's billing gate (A5): a backoff retry for an order
        //                            that previously failed, now blocked because the org lapsed.
        //   • delivery_unconfirmed — the same case reached from the park: an operator clicked
        //                            "Send again" for an org that lapsed since the park. The backoff
        //                            queue never lands here (RetryDeliveryAsync refuses this status
        //                            before its own billing gate), but one AUTOMATIC path does: a
        //                            Hangfire-refetched DeliverOrderJob checks billing BEFORE the
        //                            dispatch claim that would refuse its park claim, so a lapsed
        //                            org's refetch holds the park instead of no-oping. That is safe,
        //                            not a leak: holding pauses the nag without sending, and release
        //                            RESTORES a held park (HeldFromStatus below) rather than
        //                            re-driving it. Omitting the status here would hold NOTHING and
        //                            leave the order parked: invisible to ReleaseBillingHeldOrdersAsync
        //                            (it sweeps delivery_held only), so billing settling would never
        //                            rescue it.
        // Any other status (delivering / delivered / dead-letter / already held) is a benign no-op —
        // the billing gate simply returns without holding, and never delivers.
        if (order is null ||
            order.Status is not (OrderStatusConstants.ReadyToDeliver
                              or OrderStatusConstants.DeliveryFailed
                              or OrderStatusConstants.DeliveryUnconfirmed))
            return false;

        var fromStatus = order.Status;
        var now = DateTime.UtcNow;
        order.Status = OrderStatusConstants.DeliveryHeld;
        // The LIVE ROW must remember where the hold came from, not just the audit payload:
        // ReleaseBillingHeldOrdersAsync branches on it — a held PARK is restored to
        // delivery_unconfirmed for its human, every other hold is re-driven. Overwritten on every
        // hold, cleared on release; meaningful only while the status is delivery_held.
        order.HeldFromStatus = fromStatus;
        order.UpdatedAt = now;
        // Pause the SLA window while held so the SLA sweep never flags an order that is
        // deliberately waiting on billing (not a delivery failure).
        order.DeliveryDueAt = null;
        order.SlaBreached = false;

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            reason = "DeliveryHeldForBilling",
            fromStatus,
            toStatus = OrderStatusConstants.DeliveryHeld,
            heldAt = now,
            detail = "Org cannot process orders (billing) at delivery time — delivery paused, artifact intact; auto-released when the org returns to good standing.",
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            UserId = null,
            EntityType = "Order",
            EntityId = orderId,
            Action = "DeliveryHeldForBilling",
            Payload = System.Text.Json.JsonDocument.Parse(payload),
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "DeliveryHeldForBilling: order {OrderId} (org {OrgId}) held in 'delivery_held' — org cannot process orders at delivery time. Will auto-release on reactivation.",
            orderId, orgId);
        return true;
    }

    /// <summary>
    /// Releases every <c>delivery_held</c> order, branching on where the hold came from
    /// (<c>HeldFromStatus</c>, recorded by <see cref="HoldForBillingAsync"/>):
    /// <list type="bullet">
    /// <item>A held PARK (<c>HeldFromStatus == delivery_unconfirmed</c>) is RESTORED to
    /// <c>delivery_unconfirmed</c> and NOT re-driven. The operator's "Send again" that ran into the
    /// billing gate was made against that moment's state, and while held it could not be revisited:
    /// <c>OrdersController.MarkDelivered</c> gates on <c>delivery_unconfirmed</c>, so an operator who
    /// learned meanwhile that the supplier DID receive the PO had no way to close the order. Billing
    /// can recover days later — completing the stale click then would be an AUTOMATIC re-send of an
    /// unknown-outcome PO on a channel that cannot de-duplicate, the exact duplicate the park exists
    /// to prevent. The human re-decides from the restored park, where "Send again" and
    /// "Mark as delivered" both work again.</item>
    /// <item>Every other hold (<c>ready_to_deliver</c> / <c>delivery_failed</c> origin, or a legacy
    /// null recorded before the column existed) keeps the original behavior: released to
    /// <c>ready_to_deliver</c> and re-driven. Those holds interrupted an AUTOMATIC send that owed
    /// nobody a decision — the release completes it. (<c>delivery_failed</c> origin is deliberately
    /// NOT restored verbatim: the A5 hold consumed the backoff attempt that was mid-flight, so a
    /// restore without a re-drive would strand the order until a sweep noticed; the re-drive claim
    /// accepts <c>ready_to_deliver</c>, making it the one release status every re-driven hold
    /// can share.)</item>
    /// </list>
    /// <para>
    /// A restored park also gets its SLA window reopened (<c>DeliveryDueAt = now + SlaWindow</c>) —
    /// <see cref="ParkUnconfirmedAsync"/>'s contract is that a park keeps nagging until a human acts,
    /// on a renewed timer after every event that touches it, and the hold paused that nag
    /// (<see cref="HoldForBillingAsync"/> nulls <c>DeliveryDueAt</c> so the SLA sweep never flags a
    /// deliberate pause).
    /// </para>
    /// <para>
    /// KNOWN WINDOW (pre-existing — the previous unconditional release had the identical
    /// load-then-<c>SaveChanges</c> shape): the rows are read tracked and written back without a
    /// concurrency token, so a supplier status callback that claims one of them via
    /// <c>ExecuteUpdateAsync</c> (<c>WebhookIngressController</c> — <c>delivery_held</c> is
    /// webhook-reportable) in the milliseconds between this SELECT and the save is overwritten
    /// backwards. Closing it means per-row atomic claims — the canonical delivery-claim predicate
    /// work (#36) owns that shape; tracked as a follow-up, not silently.
    /// </para>
    /// </summary>
    public async Task<int> ReleaseBillingHeldOrdersAsync(Guid orgId, CancellationToken ct)
    {
        var held = await _db.PurchaseOrders
            .Where(o => o.OrgId == orgId && o.Status == OrderStatusConstants.DeliveryHeld)
            .ToListAsync(ct);

        if (held.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var toReDrive = new List<PurchaseOrderEntity>();
        foreach (var order in held)
        {
            var isPark = order.HeldFromStatus == OrderStatusConstants.DeliveryUnconfirmed;
            var toStatus = isPark ? OrderStatusConstants.DeliveryUnconfirmed : OrderStatusConstants.ReadyToDeliver;

            order.Status = toStatus;
            order.HeldFromStatus = null;
            order.UpdatedAt = now;
            if (isPark)
            {
                // Resume the park's nag (see the method doc above).
                order.DeliveryDueAt = now + _reliability.SlaWindow;
                order.SlaBreached = false;
            }
            else
            {
                toReDrive.Add(order);
            }

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason = "DeliveryHoldReleased",
                fromStatus = OrderStatusConstants.DeliveryHeld,
                toStatus,
                releasedAt = now,
                detail = isPark
                    ? "Org returned to a processing state — the hold is over, and the order is back "
                      + "where it was: waiting for someone to decide whether to send it again or mark "
                      + "it delivered. It was NOT sent automatically."
                    : "Org returned to a processing state — delivery hold released and re-driven.",
            });

            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                UserId = null,
                EntityType = "Order",
                EntityId = order.Id,
                Action = "DeliveryHoldReleased",
                Payload = System.Text.Json.JsonDocument.Parse(payload),
                CreatedAt = now,
            });
        }

        // Commit the ready_to_deliver resets BEFORE enqueuing re-drives, so a retry job can't
        // start on a still-'delivery_held' row and bow out (RetryDeliveryAsync claims from
        // ready_to_deliver, not delivery_held).
        await _db.SaveChangesAsync(ct);

        if (_retryEnqueuer is null)
        {
            if (toReDrive.Count > 0)
                _logger.LogWarning(
                    "DeliveryHoldReleased: {Count} order(s) reset to 'ready_to_deliver' for org {OrgId} but no IRetryDeliveryEnqueuer is registered in this process — they will re-drive once an enqueuer is available (or via an operator Retry).",
                    toReDrive.Count, orgId);
        }
        else
        {
            foreach (var order in toReDrive)
                await _retryEnqueuer.EnqueueAsync(order.Id, orgId, ct);
        }

        _logger.LogWarning(
            "DeliveryHoldReleased: released {Count} billing-held order(s) for org {OrgId} — {ReDriven} re-driven, {Restored} restored to their park for an operator decision.",
            held.Count, orgId, toReDrive.Count, held.Count - toReDrive.Count);
        return held.Count;
    }

    private async Task DeadLetterAsync(
        PurchaseOrderEntity order,
        int attemptCount,
        string? lastError,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        order.Status = OrderStatusConstants.DeliveryDeadLetter;
        order.UpdatedAt = now;
        // Dead-letter is terminal: close the SLA window so the sweep never flags an
        // order whose retries are already exhausted.
        order.DeliveryDueAt = null;

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            attemptCount,
            lastError,
            deadLetteredAt = now,
        });

        _db.AuditEvents.Add(new Core.Entities.AuditEvent
        {
            Id = Guid.NewGuid(),
            OrgId = order.OrgId,
            UserId = null,
            EntityType = "Order",
            EntityId = order.Id,
            Action = "DeliveryDeadLettered",
            Payload = System.Text.Json.JsonDocument.Parse(payload),
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        // Reconcile exceptions: dead-letter opens the critical dead_letter exception and
        // supersedes any earlier delivery_failed exception for the order.
        await SafeReconcileExceptionsAsync(order.OrgId, order.Id, ct);

        _logger.LogWarning(
            "Order {OrderId} (org {OrgId}) dead-lettered after {Attempts} attempt(s). Last error: {Error}",
            order.Id, order.OrgId, attemptCount, lastError ?? "(none)");
    }
}
