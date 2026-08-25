using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Services;

/// <summary>
/// WP-33 stage 1. Builds the whole auto-send decision and then, instead of sending, writes it down.
/// See <see cref="IAutoSendDryRunEvaluator"/> for why this class exists and what it must never do.
///
/// <para><b>The one invariant.</b> Nothing reachable from here transforms, dispatches, enqueues, or
/// changes an order's status. That is not a promise kept by reading this file — it is pinned from
/// compiled IL by <c>AutoSendDryRunCannotSendTests</c>, which walks every method this class can
/// reach and fails the build if any of them touches a send primitive.</para>
/// </summary>
public sealed class AutoSendDryRunEvaluator : IAutoSendDryRunEvaluator
{
    /// <summary>Field separator for the decision digest. A control character, so no order value can forge one.</summary>
    private const char FieldSeparator = '';

    /// <summary>Record separator between lines in the decision digest.</summary>
    private const char RecordSeparator = '';

    /// <summary>Bumped whenever the digest's inputs change, so old and new digests never compare equal by accident.</summary>
    private const string DigestSchema = "proculink.autosend.v1";

    private readonly ProcuLinkDbContext                  _db;
    private readonly IAcceptanceGate                     _gate;
    private readonly IEffectiveConnectionConfigResolver? _effectiveConfig;
    private readonly ILogger<AutoSendDryRunEvaluator>?   _logger;

    public AutoSendDryRunEvaluator(
        ProcuLinkDbContext db,
        IAcceptanceGate gate,
        IEffectiveConnectionConfigResolver? effectiveConfig = null,
        ILogger<AutoSendDryRunEvaluator>? logger = null)
    {
        _db              = db;
        _gate            = gate;
        _effectiveConfig = effectiveConfig;
        _logger          = logger;
    }

    /// <inheritdoc />
    public async Task<AutoSendDryRunOutcome> EvaluateAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        // Org-scoped, as every read in this codebase is. A projection rather than the entity: this
        // evaluation must not put the order on the change tracker, where an unrelated SaveChanges
        // later in the same scope could persist something it touched.
        var order = await _db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == orderId && o.OrgId == orgId)
            .Select(o => new
            {
                o.SupplierId,
                o.Status,
                o.ConnectionRevisionId,
                o.PoNumber,
                o.OrderDate,
                o.Currency,
                o.CanonicalJson,
                LineCount       = o.Lines.Count,
                UnresolvedLines = o.Lines.Count(l => l.NeedsReview),
                Lines = o.Lines
                    .OrderBy(l => l.LineNumber)
                    .Select(l => new { l.LineNumber, l.BuyerItemCode, l.SupplierItemCode, l.Quantity, l.UnitPrice })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        if (order is null)
            return NotRecorded(AutoSendDecision.OrderNotFound);

        // No supplier means no per-supplier switch to consult. Auto-detect deliberately never
        // assigns one (founder ruling D3 — suggest, never auto-assign), so an unrouted order is
        // unreachable by automation by construction, not by a check that could be removed.
        if (order.SupplierId is null)
            return NotRecorded(AutoSendDecision.NoSupplier);

        var supplierId = order.SupplierId.Value;

        var delivery = await _db.SupplierDeliveryConfigs.AsNoTracking()
            .Where(c => c.OrgId == orgId && c.SupplierId == supplierId)
            .Select(c => new { c.AutoTransform, c.AutoDeliver, c.Protocol, c.OutputFormat })
            .FirstOrDefaultAsync(ct);

        // The opt-in lives on the delivery config, so "no config" and "not opted in" are one state.
        if (delivery is null)
            return NotRecorded(AutoSendDecision.NoDeliveryConfig);

        // The switch. Read live and read FIRST: a supplier nobody opted in for costs two reads and
        // produces no row, so the table stays the opted-in population rather than a log of every
        // order the system has ever parsed.
        if (!delivery.AutoTransform)
            return NotRecorded(AutoSendDecision.AutoTransformOff);

        // ── From here every path records a row ────────────────────────────────

        // The channel is resolved through the SAME revision-pin-aware precedence dispatch uses
        // (DeliveryService.ResolveEffectiveDeliveryConfigAsync): a pinned published revision's
        // snapshotted protocol wins when it has one, otherwise the live supplier row. Naming a
        // channel the real send would not have used would make the whole week's data misleading.
        var effective = _effectiveConfig is null
            ? EffectiveConnectionConfig.Live
            : await _effectiveConfig.ResolveAsync(orgId, order.ConnectionRevisionId, ct);

        var channel = effective.IsRevision && !string.IsNullOrWhiteSpace(effective.DeliveryProtocol)
            ? effective.DeliveryProtocol!.Trim()
            : delivery.Protocol?.Trim();

        var outputFormat = effective.IsRevision && !string.IsNullOrWhiteSpace(effective.OutputFormat)
            ? effective.OutputFormat!.Trim().ToLowerInvariant()
            : delivery.OutputFormat?.Trim().ToLowerInvariant();

        var digest = ComputeDecisionDigest(
            orgId, orderId, supplierId, order.PoNumber, order.OrderDate, order.Currency,
            outputFormat, channel, effective.Source, order.CanonicalJson?.RootElement.GetRawText(),
            order.Lines.Select(l => (l.LineNumber, l.BuyerItemCode, l.SupplierItemCode, l.Quantity, l.UnitPrice)));

        object BaseEvidence(object? extra = null) => new
        {
            status           = order.Status,
            lineCount        = order.LineCount,
            unresolvedLines  = order.UnresolvedLines,
            configSource     = effective.Source,
            // In stage 2 an auto-transformed order still only leaves the building if the supplier's
            // AutoDeliver is on. Recorded so the founder can tell "would have been sent" from
            // "would have produced a document and stopped".
            autoDeliver      = delivery.AutoDeliver,
            detail           = extra,
        };

        async Task<AutoSendDryRunOutcome> Record(
            string decision, bool wouldHaveSent, int blockerCount, object evidence) =>
            await PersistAsync(
                orgId, orderId, supplierId, decision, wouldHaveSent,
                channel, outputFormat, digest, blockerCount, evidence, ct);

        // Only a `ready` order may go. Every other status is either not finished, waiting on a
        // human, already failed, or ALREADY SENT — and re-sending a delivered PO unattended is
        // exactly the failure mode this staging exists to make impossible.
        if (!string.Equals(order.Status, OrderStatusConstants.Ready, StringComparison.Ordinal))
            return await Record(AutoSendDecision.StatusNotReady, false, 0, BaseEvidence());

        // Checked independently of the status rather than inferred from it. The real transform makes
        // the same choice (OrderTransformService re-checks NeedsReview before doing anything), and
        // for the same reason: one write staying in step with another is an assumption, not a fact.
        if (order.UnresolvedLines > 0)
            return await Record(AutoSendDecision.UnresolvedLines, false, 0, BaseEvidence());

        // An open duplicate_po_number warning is a human decision, so auto-send declines it.
        //
        // Detection is advisory by design — OrderExceptionService opens a warning and blocks
        // nothing, because suppliers do legitimately reuse PO numbers and a hard block would refuse
        // real orders. That trade-off holds exactly as long as a person reads the warning before
        // clicking Send, which is what the frontend now puts in front of them at the Send button.
        // Take the person out of the loop and the trade-off inverts: the thing being waved through
        // unattended is a second copy of a purchase order that has already gone to the supplier.
        //
        // Read, not re-derived: this asks OrderExceptionService's own flag rather than running a
        // second duplicate query here, for the same reason the acceptance verdict comes from the
        // gate — two definitions of "duplicate" would eventually disagree, and the day they did,
        // this table would be certifying orders the review screen is warning about.
        //
        // `open` only. Resolved or ignored means an operator already looked and decided; re-asking
        // them would leave a flag that can never be cleared.
        var duplicateFlagged = await _db.OrderExceptions.AsNoTracking()
            .AnyAsync(
                e => e.OrgId == orgId
                  && e.OrderId == orderId
                  && e.Code == OrderExceptionService.DuplicatePoNumberCode
                  && e.State == "open",
                ct);

        if (duplicateFlagged)
            return await Record(AutoSendDecision.PossibleDuplicate, false, 0,
                BaseEvidence(new { duplicatePoNumber = order.PoNumber }));

        if (string.IsNullOrWhiteSpace(channel))
            return await Record(AutoSendDecision.NoDeliveryChannel, false, 0, BaseEvidence());

        // WP-17's gate is the single authority on whether a supplier's rules permit an order. Asking
        // it — rather than re-deriving an answer here — is what keeps this table's verdict and the
        // transform door's verdict from ever disagreeing.
        AcceptanceGateDecision? gate;
        try
        {
            gate = await _gate.EvaluateAsync(orgId, orderId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;                                  // a cancelled run is not a refusal
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Order {OrderId} (org {OrgId}): the supplier acceptance gate could not be evaluated, so auto-send "
              + "treats it as not clean.", orderId, orgId);

            return await Record(AutoSendDecision.AcceptanceGateUnavailable, false, 0,
                BaseEvidence(new { gateError = ex.GetType().Name }));
        }

        if (gate is null)
            return NotRecorded(AutoSendDecision.OrderNotFound);   // vanished mid-evaluation

        if (gate.Blocked)
            return await Record(AutoSendDecision.AcceptanceBlocked, false, gate.Blockers.Count,
                BaseEvidence(new { blockers = DescribeBlockers(gate) }));

        // THE SUBTLE ONE. An override makes the gate answer `Blocked: false` while its blockers
        // remain — a human authorised ONE send, having read what they were waiving. That consent
        // does not extend to a machine sending the next hundred unattended. Tested on the blocker
        // COUNT rather than the Overridden flag, so any future decision shape that clears Blocked
        // while failures remain is refused here too, without anyone having to remember to update it.
        if (gate.Blockers.Count > 0)
            return await Record(AutoSendDecision.AcceptanceOverridden, false, gate.Blockers.Count,
                BaseEvidence(new
                {
                    blockers       = DescribeBlockers(gate),
                    overridden     = gate.Overridden,
                    overriddenBy   = gate.OverriddenBy,
                    overrideReason = gate.OverrideReason,
                }));

        return await Record(AutoSendDecision.Clean, true, 0, BaseEvidence());
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private async Task<AutoSendDryRunOutcome> PersistAsync(
        Guid orgId, Guid orderId, Guid supplierId, string decision, bool wouldHaveSent,
        string? channel, string? outputFormat, string? digest, int blockerCount,
        object evidence, CancellationToken ct)
    {
        // NO "have we already recorded this?" pre-check. There was one, and it was worse than
        // useless: it absorbed every duplicate a test could produce, so the unique index below —
        // the thing that actually holds under concurrency — was never exercised. Deleting the
        // index and disabling the violation handler both left the suite green. One mechanism,
        // always taken, is testable; two, where the cheap one hides the real one, is not.
        var row = new AutoSendDryRun
        {
            Id             = Guid.NewGuid(),
            OrgId          = orgId,
            OrderId        = orderId,
            SupplierId     = supplierId,
            WouldHaveSent  = wouldHaveSent,
            Decision       = decision,
            Channel        = string.IsNullOrWhiteSpace(channel) ? null : channel,
            OutputFormat   = string.IsNullOrWhiteSpace(outputFormat) ? null : outputFormat,
            DecisionDigest = digest,
            BlockerCount   = blockerCount,
            Evidence       = JsonSerializer.Serialize(evidence),
            EvaluatedAt    = DateTime.UtcNow,
        };

        _db.AutoSendDryRuns.Add(row);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A Hangfire refetch, or a genuinely concurrent runner. The unique index is the sole
            // authority and it just refused; that is the whole idempotency guarantee.
            // The failed insert is detached because this DbContext is scoped and shared with the
            // rest of the parse job — leaving it tracked would make the NEXT unrelated SaveChanges
            // in this scope retry the same doomed insert.
            _db.Entry(row).State = EntityState.Detached;

            _logger?.LogInformation(
                "Order {OrderId} (org {OrgId}) had already been evaluated for auto-send — not recording a second row.",
                orderId, orgId);

            return AlreadyEvaluated(decision, wouldHaveSent, channel, outputFormat);
        }

        if (wouldHaveSent)
            _logger?.LogInformation(
                "AUTO-SEND DRY RUN: order {OrderId} (org {OrgId}, supplier {SupplierId}) WOULD have been sent over "
              + "{Channel} as {Format}. Nothing was sent — auto-send is in its dry-run stage.",
                orderId, orgId, supplierId, channel, outputFormat);

        return new AutoSendDryRunOutcome(
            Recorded: true, WouldHaveSent: wouldHaveSent, Decision: decision,
            Channel: channel, OutputFormat: outputFormat);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AutoSendDryRunOutcome NotRecorded(string decision) =>
        new(Recorded: false, WouldHaveSent: false, Decision: decision);

    private static AutoSendDryRunOutcome AlreadyEvaluated(
        string decision, bool wouldHaveSent, string? channel, string? outputFormat) =>
        new(Recorded: false, WouldHaveSent: wouldHaveSent, Decision: decision,
            Channel: channel, OutputFormat: outputFormat, AlreadyEvaluated: true);

    private static object DescribeBlockers(AcceptanceGateDecision gate) =>
        gate.Blockers
            .Select(b => new { code = b.Code, line = b.LineNumber, message = b.Message })
            .ToList();

    /// <summary>
    /// SHA-256 over everything that determines the outgoing document: the order's identity and
    /// content, the resolved format and channel, and which configuration governed the decision.
    ///
    /// <para>It is deliberately NOT the artifact's hash — stage 1 renders no artifact, and calling
    /// the hash of a document that was never produced an "artifact SHA-256" would put a claim in the
    /// audit trail that nothing backs. What it IS good for: two rows with the same digest would have
    /// produced byte-identical documents under identical config (so a week of recurring POs is
    /// visibly recurring), and a digest that differs between the dry run and the eventual real send
    /// says something moved underneath the decision.</para>
    /// </summary>
    private static string? ComputeDecisionDigest(
        Guid orgId, Guid orderId, Guid supplierId, string? poNumber, DateOnly? orderDate,
        string? currency, string? outputFormat, string? channel, string configSource,
        string? canonicalJson,
        IEnumerable<(int LineNumber, string BuyerItemCode, string? SupplierItemCode, decimal Quantity, decimal UnitPrice)> lines)
    {
        var sb = new StringBuilder(DigestSchema);

        void Field(string? value) => sb.Append(FieldSeparator).Append(value ?? string.Empty);

        Field(orgId.ToString("N", CultureInfo.InvariantCulture));
        Field(orderId.ToString("N", CultureInfo.InvariantCulture));
        Field(supplierId.ToString("N", CultureInfo.InvariantCulture));
        Field(poNumber);
        Field(orderDate?.ToString("O", CultureInfo.InvariantCulture));
        Field(currency);
        Field(outputFormat);
        Field(channel);
        Field(configSource);
        Field(canonicalJson);

        foreach (var line in lines)
        {
            sb.Append(RecordSeparator)
              .Append(line.LineNumber.ToString(CultureInfo.InvariantCulture)).Append(FieldSeparator)
              .Append(line.BuyerItemCode).Append(FieldSeparator)
              .Append(line.SupplierItemCode ?? string.Empty).Append(FieldSeparator)
              .Append(line.Quantity.ToString(CultureInfo.InvariantCulture)).Append(FieldSeparator)
              .Append(line.UnitPrice.ToString(CultureInfo.InvariantCulture));
        }

        return ProvenanceHash.TrySha256HexUtf8(sb.ToString());
    }
}
