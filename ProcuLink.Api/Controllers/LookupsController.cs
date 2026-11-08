using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

public sealed record LookupRequest(string Name, List<LookupEntry>? Entries, List<LookupCandidate>? Candidates = null);

public sealed record LookupSummaryDto(Guid Id, string Name, int Count, DateTime UpdatedAt, bool HasCandidates, bool SuggestionsOn);

public sealed record LookupDto(Guid Id, string Name, List<LookupEntry> Entries, DateTime UpdatedAt, List<LookupCandidate> Candidates, bool SuggestionsOn);

[Authorize]
[ApiController]
public sealed class LookupsController(ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private static List<LookupEntry> EntriesOf(Lookup l) => DocumentJson.Deserialize<List<LookupEntry>>(l.EntriesJson);

    private LookupDto ToDto(Lookup l) =>
        new(l.Id, l.Name, EntriesOf(l), l.UpdatedAt, DocumentJson.Deserialize<List<LookupCandidate>>(l.CandidatesJson), gate.LookupSuggestionsOn(tenant.OrganisationId));

    private static IActionResult Invalid(string detail) => new UnprocessableEntityObjectResult(new { error = "lookup_invalid", detail });

    private static IActionResult NotFoundResult() => new NotFoundObjectResult(new { error = "lookup_not_found" });

    /// <summary>Copies a valid request onto <paramref name="l"/>; returns the refusal, or null.</summary>
    private static IActionResult? Apply(Lookup l, LookupRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > LookupRules.MaxNameLength) return Invalid($"The name must be 1 to {LookupRules.MaxNameLength} characters.");
        var entries = request.Entries ?? [];
        if (LookupRules.Problem(entries) is { } problem) return Invalid(problem);
        if (request.Candidates is { } candidates)
        {
            if (LookupRules.CandidateProblem(candidates) is { } candidateProblem) return Invalid(candidateProblem);
            l.CandidatesJson = DocumentJson.Serialize(candidates.Select(c =>
                new LookupCandidate(c.Value.Trim(), string.IsNullOrWhiteSpace(c.Description) ? null : c.Description.Trim())).ToList());
        }
        l.Name = name;
        l.EntriesJson = DocumentJson.Serialize(entries.Select(e => new LookupEntry(e.From.Trim(), e.To ?? "")).ToList());
        l.UpdatedAt = DateTime.UtcNow;
        return null;
    }

    [HttpGet("api/lookups")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var suggestionsOn = gate.LookupSuggestionsOn(orgId);
        var rows = await db.Set<Lookup>().AsNoTracking().Where(l => l.OrgId == orgId).OrderBy(l => l.Name).ToListAsync(ct);
        return Ok(rows.Select(l => new LookupSummaryDto(l.Id, l.Name, EntriesOf(l).Count, l.UpdatedAt, l.CandidatesJson != "[]", suggestionsOn)));
    }

    [HttpPost("api/lookups")]
    public async Task<IActionResult> Create([FromBody] LookupRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var lookup = new Lookup { Id = Guid.NewGuid(), OrgId = tenant.OrganisationId, CreatedAt = DateTime.UtcNow };
        if (Apply(lookup, request) is { } rejected) return rejected;
        db.Set<Lookup>().Add(lookup);
        await db.SaveChangesAsync(ct);
        return Created($"/api/lookups/{lookup.Id}", ToDto(lookup));
    }

    [HttpGet("api/lookups/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var lookup = await db.Set<Lookup>().AsNoTracking().FirstOrDefaultAsync(l => l.OrgId == orgId && l.Id == id, ct);
        return lookup is null ? NotFoundResult() : Ok(ToDto(lookup));
    }

    [HttpPut("api/lookups/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] LookupRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var lookup = await db.Set<Lookup>().FirstOrDefaultAsync(l => l.OrgId == orgId && l.Id == id, ct);
        if (lookup is null) return NotFoundResult();
        if (Apply(lookup, request) is { } rejected) return rejected;
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(lookup));
    }

    [HttpDelete("api/lookups/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var lookup = await db.Set<Lookup>().FirstOrDefaultAsync(l => l.OrgId == orgId && l.Id == id, ct);
        if (lookup is null) return NotFoundResult();

        var templates = await db.Set<FlowOutput>().AsNoTracking().Where(o => o.OrgId == orgId).Select(o => o.TemplateJson).ToListAsync(ct);
        if (templates.Any(t => DocumentLookups.UsedBy(DocumentJson.Deserialize<OutputNodeTemplate>(t)).Contains(id)))
            return Conflict(new { error = "lookup_in_use" });

        db.Set<Lookup>().Remove(lookup);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
