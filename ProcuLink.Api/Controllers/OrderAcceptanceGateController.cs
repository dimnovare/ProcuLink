using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// WP-17 — the operator-facing surface of the server-side acceptance gate.
///
/// <list type="bullet">
///   <item><c>GET  /api/orders/{id}/acceptance-gate</c> — will this order send, and if not, why not
///   (plain language), plus whether an override is already recorded and by whom.</item>
///   <item><c>POST /api/orders/{id}/acceptance-gate/override</c> — an operator authorises sending an
///   order the supplier's rules refuse, with a stated reason. Recorded in the audit trail.</item>
/// </list>
///
/// <para>Its own controller on purpose (the precedent is <see cref="MapperEnrichmentController"/>):
/// <see cref="OrdersController"/>'s constructor is referenced positionally by roughly two dozen test
/// files, and a new REQUIRED dependency there is a large, risky diff for two thin endpoints — while
/// a new OPTIONAL one is precisely the silently-null dependency this work package exists to
/// eliminate.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/orders")]
public sealed class OrderAcceptanceGateController : ControllerBase
{
    private readonly IAcceptanceGate       _gate;
    private readonly ICurrentTenantService _tenant;

    public OrderAcceptanceGateController(IAcceptanceGate gate, ICurrentTenantService tenant)
    {
        _gate   = gate;
        _tenant = tenant;
    }

    /// <summary>One blocking rule, as the UI shows it.</summary>
    public sealed record AcceptanceBlockerDto(string Code, int? LineNumber, string Message);

    /// <summary>Whether this order will send, and what is standing in the way.</summary>
    public sealed record AcceptanceGateDto(
        bool                              Blocked,
        string?                           Reason,
        bool                              Overridden,
        string?                           OverriddenBy,
        string?                           OverrideReason,
        IReadOnlyList<AcceptanceBlockerDto> Blockers);

    public sealed record AcceptanceOverrideRequest(string? Reason);

    // ── GET /api/orders/{id}/acceptance-gate ──────────────────────────────────

    /// <summary>
    /// The gate's current answer for this order. Read-only — it mutates nothing and persists
    /// nothing, so the UI may poll it freely.
    /// </summary>
    [HttpGet("{id:guid}/acceptance-gate")]
    [ProducesResponseType(typeof(AcceptanceGateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var decision = await _gate.EvaluateAsync(_tenant.OrganisationId, id, ct);
        if (decision is null) return NotFound();
        return Ok(ToDto(decision));
    }

    // ── POST /api/orders/{id}/acceptance-gate/override ────────────────────────

    /// <summary>
    /// Authorises sending this order despite the supplier's blocking rules. The reason is required
    /// and the caller's identity is recorded; the override covers exactly the failures present when
    /// it is granted, so a rule that starts failing afterwards blocks again.
    /// </summary>
    [HttpPost("{id:guid}/acceptance-gate/override")]
    [ProducesResponseType(typeof(AcceptanceGateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Override(
        Guid id, [FromBody] AcceptanceOverrideRequest? request, CancellationToken ct)
    {
        var orgId  = _tenant.OrganisationId;
        var reason = request?.Reason ?? string.Empty;

        var result = await _gate.RecordOverrideAsync(orgId, id, _tenant.ClerkUserId, reason, ct);

        // Branch on the TYPED refusal, never on the message text: the message is written for a
        // human and will be reworded, and a status code that silently depends on its wording is a
        // trap for whoever rewords it.
        switch (result.Refusal)
        {
            case AcceptanceOverrideRefusal.None:
                break;
            case AcceptanceOverrideRefusal.OrderNotFound:
                return NotFound();
            case AcceptanceOverrideRefusal.NotBlocked:
                return Conflict(new { error = result.Error });
            default:
                return BadRequest(new { error = result.Error });
        }

        // Answer with the gate's NEW state so the caller can act on one round trip: the order should
        // now read Blocked=false, Overridden=true.
        var decision = await _gate.EvaluateAsync(orgId, id, ct);
        return decision is null ? NotFound() : Ok(ToDto(decision));
    }

    private static AcceptanceGateDto ToDto(AcceptanceGateDecision d) => new(
        d.Blocked, d.Reason, d.Overridden, d.OverriddenBy, d.OverrideReason,
        d.Blockers.Select(b => new AcceptanceBlockerDto(b.Code, b.LineNumber, b.Message)).ToList());
}
