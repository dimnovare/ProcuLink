using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// WP-33 stage 1 — <b>reading the dry run.</b>
///
/// <para>The whole point of stage 1 is that a week of recorded decisions is what earns the right to
/// let a purchase order move unattended. A table nobody can read does not earn anything, so this is
/// the surface that makes the evidence answerable: one call says how many orders WOULD have gone
/// out, over which channels, and what held the rest back.</para>
///
/// <list type="bullet">
///   <item><c>GET /api/auto-send/dry-runs/summary</c> — the aggregate the decision actually turns
///   on: totals by outcome and by channel.</item>
///   <item><c>GET /api/auto-send/dry-runs</c> — the most recent decisions, newest first, for
///   reading the interesting ones after the aggregate narrows them down.</item>
/// </list>
///
/// <para>Its own controller for the same reason <see cref="OrderAcceptanceGateController"/> is:
/// adding a required dependency to <see cref="OrdersController"/> is a large, risky diff for two
/// thin read-only endpoints.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/auto-send/dry-runs")]
public sealed class AutoSendDryRunController : ControllerBase
{
    /// <summary>Hard ceiling on one page. The founder reads hundreds, not the whole table.</summary>
    private const int MaxPageSize = 500;

    private readonly ProcuLinkDbContext    _db;
    private readonly ICurrentTenantService _tenant;

    public AutoSendDryRunController(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    /// <summary>One recorded decision, as a reader sees it.</summary>
    public sealed record AutoSendDryRunDto(
        Guid     OrderId,
        Guid?    SupplierId,
        bool     WouldHaveSent,
        string   Decision,
        string?  Channel,
        string?  OutputFormat,
        string?  DecisionDigest,
        int      BlockerCount,
        string?  Evidence,
        DateTime EvaluatedAt);

    /// <summary>How many decisions came out each way.</summary>
    public sealed record DecisionCountDto(string Decision, int Count);

    /// <summary>How many would-be sends each channel would have carried.</summary>
    public sealed record ChannelCountDto(string Channel, int Count);

    /// <summary>
    /// The stage-2 question in one object: of the orders whose supplier opted in, how many would
    /// have been sent with no human involved, and what stopped the others.
    /// </summary>
    public sealed record AutoSendDryRunSummaryDto(
        int    Evaluated,
        int    WouldHaveSent,
        int    Held,
        /// <summary>Distinct orders that would have been sent — the number that matters, since the
        /// table holds one row per order.</summary>
        int    DistinctDocuments,
        DateTime? FirstEvaluatedAt,
        DateTime? LastEvaluatedAt,
        IReadOnlyList<DecisionCountDto> ByDecision,
        IReadOnlyList<ChannelCountDto>  ByChannel);

    // ── GET /api/auto-send/dry-runs/summary ───────────────────────────────────

    [HttpGet("summary")]
    [ProducesResponseType(typeof(AutoSendDryRunSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var rows  = _db.AutoSendDryRuns.AsNoTracking().Where(r => r.OrgId == orgId);

        // Grouped into anonymous types and shaped afterwards: constructing a DTO inside the
        // GroupBy projection is not translatable on every provider (EF InMemory refuses it), and a
        // read surface that only works on one provider is a read surface that cannot be tested.
        var decisionGroups = await rows
            .GroupBy(r => r.Decision)
            .Select(g => new { Decision = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byDecision = decisionGroups
            .OrderByDescending(d => d.Count)
            .Select(d => new DecisionCountDto(d.Decision, d.Count))
            .ToList();

        var channelGroups = await rows
            .Where(r => r.WouldHaveSent && r.Channel != null)
            .GroupBy(r => r.Channel!)
            .Select(g => new { Channel = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var byChannel = channelGroups
            .OrderByDescending(c => c.Count)
            .Select(c => new ChannelCountDto(c.Channel, c.Count))
            .ToList();

        var evaluated     = await rows.CountAsync(ct);
        var wouldHaveSent = await rows.CountAsync(r => r.WouldHaveSent, ct);

        // Distinct digests among the would-be sends: a hundred rows over three digests is a hundred
        // recurring POs, which is a very different week from a hundred distinct documents.
        var distinctDocuments = await rows
            .Where(r => r.WouldHaveSent && r.DecisionDigest != null)
            .Select(r => r.DecisionDigest!)
            .Distinct()
            .CountAsync(ct);

        var first = await rows.MinAsync(r => (DateTime?)r.EvaluatedAt, ct);
        var last  = await rows.MaxAsync(r => (DateTime?)r.EvaluatedAt, ct);

        return Ok(new AutoSendDryRunSummaryDto(
            Evaluated:         evaluated,
            WouldHaveSent:     wouldHaveSent,
            Held:              evaluated - wouldHaveSent,
            DistinctDocuments: distinctDocuments,
            FirstEvaluatedAt:  first,
            LastEvaluatedAt:   last,
            ByDecision:        byDecision,
            ByChannel:         byChannel));
    }

    // ── GET /api/auto-send/dry-runs ───────────────────────────────────────────

    /// <summary>
    /// Recent decisions, newest first. <paramref name="wouldHaveSent"/> filters to just the orders
    /// that would have moved (or just the ones that were held), which is how a reader gets from the
    /// aggregate to the individual rows worth looking at.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AutoSendDryRunDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        CancellationToken ct,
        [FromQuery] bool? wouldHaveSent = null,
        [FromQuery] string? decision = null,
        [FromQuery] int limit = 100)
    {
        var orgId = _tenant.OrganisationId;
        var query = _db.AutoSendDryRuns.AsNoTracking().Where(r => r.OrgId == orgId);

        if (wouldHaveSent is { } sent)
            query = query.Where(r => r.WouldHaveSent == sent);

        if (!string.IsNullOrWhiteSpace(decision))
        {
            var wanted = decision.Trim();
            query = query.Where(r => r.Decision == wanted);
        }

        var rows = await query
            .OrderByDescending(r => r.EvaluatedAt)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .Select(r => new AutoSendDryRunDto(
                r.OrderId, r.SupplierId, r.WouldHaveSent, r.Decision, r.Channel,
                r.OutputFormat, r.DecisionDigest, r.BlockerCount, r.Evidence, r.EvaluatedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }
}
