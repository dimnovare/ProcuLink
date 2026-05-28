using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/rules")]
public class ValidationRulesController : ControllerBase
{
    private readonly IValidationRuleService _rules;
    private readonly ICurrentTenantService  _tenant;

    public ValidationRulesController(IValidationRuleService rules, ICurrentTenantService tenant)
    {
        _rules  = rules;
        _tenant = tenant;
    }

    // ── GET /api/rules ────────────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var list  = await _rules.ListAsync(orgId, ct);
        return Ok(list.Select(ToDto));
    }

    // ── POST /api/rules ───────────────────────────────────────────────────────

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateRuleRequest body, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var rule  = await _rules.CreateAsync(orgId, body, ct);
        return StatusCode(201, ToDto(rule));
    }

    // ── PUT /api/rules/{id} ───────────────────────────────────────────────────

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateRuleRequest body, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var rule  = await _rules.UpdateAsync(orgId, id, body, ct);
        if (rule is null) return NotFound();
        return Ok(ToDto(rule));
    }

    // ── PATCH /api/rules/{id}/toggle ─────────────────────────────────────────

    [HttpPatch("{id:guid}/toggle")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var rule  = await _rules.ToggleAsync(orgId, id, ct);
        if (rule is null) return NotFound();
        return Ok(ToDto(rule));
    }

    // ── DELETE /api/rules/{id} ────────────────────────────────────────────────

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var found = await _rules.DeleteAsync(orgId, id, ct);
        if (!found) return NotFound();
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RuleDto ToDto(ProcuLink.Core.Entities.ValidationRule r) =>
        new(
            Id:           r.Id.ToString(),
            Name:         r.Name,
            Description:  r.Description,
            Severity:     r.Severity,
            Entity:       r.Entity,
            Enabled:      r.Enabled,
            AutoBlock:    r.AutoBlock,
            TriggerCount: r.TriggerCount,
            LastTriggered: r.LastTriggeredAt.HasValue ? HumanAge(r.LastTriggeredAt.Value) : null,
            CreatedAt:    r.CreatedAt.ToString("o")
        );

    private static string HumanAge(DateTime utc)
    {
        var diff = DateTime.UtcNow - utc;
        if (diff.TotalMinutes < 60)  return $"{(int)diff.TotalMinutes}m";
        if (diff.TotalHours   < 24)  return $"{(int)diff.TotalHours}h";
        if (diff.TotalDays    < 7)   return $"{(int)diff.TotalDays}d";
        return $"{(int)(diff.TotalDays / 7)}w";
    }
}

internal record RuleDto(
    string  Id,
    string  Name,
    string  Description,
    string  Severity,
    string  Entity,
    bool    Enabled,
    bool    AutoBlock,
    int     TriggerCount,
    string? LastTriggered,
    string  CreatedAt);
