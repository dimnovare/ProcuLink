using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

/// <summary>
/// The server-side acceptance gate (WP-17). See <see cref="IAcceptanceGate"/> for why it exists.
///
/// <para><b>The override is state, and the audit trail IS that state.</b> There is no new column and
/// no new table: an override is an <see cref="AuditEvent"/> with action
/// <see cref="AcceptanceGateAudit.OverriddenAction"/> whose payload carries the operator, the reason,
/// and the exact set of failures being excused. That single row is simultaneously the authorisation
/// the gate reads and the "who and why" a reviewer needs — the two cannot drift apart, because they
/// are the same row.</para>
///
/// <para><b>An override excuses failures, not orders.</b> The excused set is pinned to the blockers
/// that existed when the operator signed off. If the order later fails a DIFFERENT rule — a new rule
/// version, an edited line, a re-parse — that failure is not covered and the gate refuses again.
/// A blanket "this order is forever exempt" flag would quietly hand back the guarantee we are here
/// to keep.</para>
/// </summary>
public sealed class AcceptanceGate : IAcceptanceGate
{
    /// <summary>Shortest reason we accept. An override with no stated reason is not an audit trail.</summary>
    private const int MinReasonLength = 5;

    private readonly ProcuLinkDbContext         _db;
    private readonly ISupplierAcceptanceService _acceptance;
    private readonly ILogger<AcceptanceGate>?   _logger;

    public AcceptanceGate(
        ProcuLinkDbContext db,
        ISupplierAcceptanceService acceptance,
        ILogger<AcceptanceGate>? logger = null)
    {
        _db         = db;
        _acceptance = acceptance;
        _logger     = logger;
    }

    // ── Decision ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AcceptanceGateDecision?> EvaluateAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        var blockers = await _acceptance.GetBlockingFailuresAsync(orgId, orderId, ct);
        if (blockers is null) return null;              // no such order for this org → caller 404s
        if (blockers.Count == 0) return AcceptanceGateDecision.Clear;

        var supplierName = await _db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == orderId && o.OrgId == orgId)
            .Select(o => o.Supplier != null ? o.Supplier.Name : null)
            .FirstOrDefaultAsync(ct);

        var reason = AcceptanceGateMessage.Compose(blockers, supplierName);

        var (excused, actor, overrideReason) = await ReadLatestOverrideAsync(orgId, orderId, ct);
        var covered = excused is not null && blockers.All(b => excused.Contains(b.Key));

        if (covered)
        {
            _logger?.LogWarning(
                "Order {OrderId} (org {OrgId}) fails {Count} blocking supplier acceptance rule(s) but an operator "
              + "override by {Actor} covers all of them — proceeding. Reason given: {Reason}",
                orderId, orgId, blockers.Count, actor, overrideReason);

            return new AcceptanceGateDecision(
                Blocked: false, Blockers: blockers, Reason: reason,
                Overridden: true, OverriddenBy: actor, OverrideReason: overrideReason);
        }

        return new AcceptanceGateDecision(Blocked: true, Blockers: blockers, Reason: reason);
    }

    // ── Override ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<AcceptanceOverrideResult> RecordOverrideAsync(
        Guid orgId, Guid orderId, string actor, string reason, CancellationToken ct)
    {
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length < MinReasonLength)
            return Refused(AcceptanceOverrideRefusal.ReasonMissing,
                "Say why this order should be sent even though the supplier's rules refuse it.");

        var blockers = await _acceptance.GetBlockingFailuresAsync(orgId, orderId, ct);
        if (blockers is null)
            return Refused(AcceptanceOverrideRefusal.OrderNotFound, "Order not found.");

        if (blockers.Count == 0)
            return Refused(AcceptanceOverrideRefusal.NotBlocked,
                "This order isn't blocked by the supplier's rules, so there's nothing to override. Send it as normal.");

        // The excused set is captured HERE, from the failures that exist right now — that is what
        // makes the override specific rather than a standing exemption.
        var excused = blockers.Select(b => b.Key).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();

        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(orgId, orderId, AcceptanceGateAudit.OverriddenAction, new
        {
            reason  = trimmed,
            by      = actor,
            excused,
            // Human-readable copy of exactly what was waved through, so the audit row stands alone
            // even if the rule is later edited or deleted.
            blocked = blockers.Select(b => b.Message).ToList(),
            at      = DateTime.UtcNow,
        }));

        await _db.SaveChangesAsync(ct);

        _logger?.LogWarning(
            "Order {OrderId} (org {OrgId}): {Actor} recorded an acceptance override for {Count} blocking rule(s). Reason: {Reason}",
            orderId, orgId, actor, blockers.Count, trimmed);

        return new AcceptanceOverrideResult(
            Recorded: true, Refusal: AcceptanceOverrideRefusal.None, Error: null, Excused: blockers);
    }

    /// <summary>Records that a transform actually CONSUMED an override — the moment the guarantee
    /// was waived in practice, which is a different (and more interesting) event than granting it.</summary>
    internal static AuditEvent BuildOverrideUsedEvent(
        Guid orgId, Guid orderId, AcceptanceGateDecision decision) =>
        OrderServiceShared.BuildAuditEvent(orgId, orderId, AcceptanceGateAudit.OverrideUsedAction, new
        {
            by       = decision.OverriddenBy,
            reason   = decision.OverrideReason,
            excused  = decision.Blockers.Select(b => b.Key).ToList(),
            blocked  = decision.Blockers.Select(b => b.Message).ToList(),
        });

    // ── Internals ─────────────────────────────────────────────────────────────

    private static AcceptanceOverrideResult Refused(AcceptanceOverrideRefusal refusal, string error) =>
        new(Recorded: false, Refusal: refusal, Error: error, Excused: Array.Empty<AcceptanceBlocker>());

    /// <summary>
    /// The most recent override recorded for this order, as (excused keys, actor, reason). Returns
    /// (null, null, null) when there is none or the payload cannot be read — an unreadable override
    /// FAILS CLOSED (the order stays blocked), because the alternative is shipping a PO the supplier
    /// refuses on the strength of a row we could not parse.
    /// </summary>
    private async Task<(HashSet<string>? Excused, string? Actor, string? Reason)> ReadLatestOverrideAsync(
        Guid orgId, Guid orderId, CancellationToken ct)
    {
        var ev = await _db.AuditEvents.AsNoTracking()
            .Where(a => a.OrgId == orgId
                     && a.EntityId == orderId
                     && a.EntityType == "Order"
                     && a.Action == AcceptanceGateAudit.OverriddenAction)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (ev?.Payload is null) return (null, null, null);

        try
        {
            var root = ev.Payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, null);

            if (!root.TryGetProperty(AcceptanceGateAudit.ExcusedKey, out var excusedEl)
                || excusedEl.ValueKind != JsonValueKind.Array)
                return (null, null, null);

            var excused = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in excusedEl.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { } key)
                    excused.Add(key);

            var actor = root.TryGetProperty(AcceptanceGateAudit.ActorKey, out var byEl)
                     && byEl.ValueKind == JsonValueKind.String ? byEl.GetString() : null;
            var reason = root.TryGetProperty(AcceptanceGateAudit.ReasonKey, out var rEl)
                      && rEl.ValueKind == JsonValueKind.String ? rEl.GetString() : null;

            return (excused, actor, reason);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Order {OrderId}: an acceptance-override audit payload could not be read — the order stays blocked.",
                orderId);
            return (null, null, null);
        }
    }
}
