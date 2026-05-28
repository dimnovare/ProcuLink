using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/templates")]
public class OutputTemplatesController : ControllerBase
{
    private readonly IOutputTemplateService _templates;
    private readonly ICurrentTenantService  _tenant;

    public OutputTemplatesController(IOutputTemplateService templates, ICurrentTenantService tenant)
    {
        _templates = templates;
        _tenant    = tenant;
    }

    // ── GET /api/templates ────────────────────────────────────────────────────

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTemplates(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var list  = await _templates.ListAsync(orgId, ct);
        return Ok(list.Select(ToDto));
    }

    // ── POST /api/templates ───────────────────────────────────────────────────

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateTemplate(
        [FromBody] CreateTemplateBody body,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var req   = new CreateTemplateRequest(body.Name, body.Format, body.Version, body.ConfigJson);
        var t     = await _templates.CreateAsync(orgId, req, ct);
        return StatusCode(201, ToDto(t));
    }

    // ── PUT /api/templates/{id} ───────────────────────────────────────────────

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTemplate(
        Guid id,
        [FromBody] CreateTemplateBody body,
        CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var req   = new CreateTemplateRequest(body.Name, body.Format, body.Version, body.ConfigJson);
        var t     = await _templates.UpdateAsync(orgId, id, req, ct);
        if (t is null) return NotFound();
        return Ok(ToDto(t));
    }

    // ── DELETE /api/templates/{id} ────────────────────────────────────────────

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTemplate(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var found = await _templates.DeleteAsync(orgId, id, ct);
        if (!found) return NotFound();
        return NoContent();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static TemplateDto ToDto(ProcuLink.Core.Entities.OutputTemplate t) => new(
        Id:             t.Id.ToString(),
        Name:           t.Name,
        Format:         t.Format,
        Version:        t.Version,
        SuppliersCount: 0,
        LastUsed:       FormatAge(t.UpdatedAt),
        Config:         null
    );

    private static string FormatAge(DateTime dt)
    {
        var diff = DateTime.UtcNow - dt;
        if (diff.TotalMinutes < 60)  return $"{(int)diff.TotalMinutes}m";
        if (diff.TotalHours   < 24)  return $"{(int)diff.TotalHours}h";
        return $"{(int)diff.TotalDays}d";
    }
}

public record CreateTemplateBody(
    string Name,
    string Format,
    string Version,
    System.Text.Json.JsonDocument? ConfigJson);

public record TemplateDto(
    string  Id,
    string  Name,
    string  Format,
    string  Version,
    int     SuppliersCount,
    string  LastUsed,
    object? Config);
