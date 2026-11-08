using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/flows")]
public sealed class FlowsController(
    ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate, DocumentRechecker rechecker) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private static IActionResult FlowNotFound() => new NotFoundObjectResult(new { error = "flow_not_found" });

    private FlowDto ToDto(Flow f, int documentCount) =>
        new(f.Id, f.Name, f.FieldsConfirmed, DocumentJson.Deserialize<FlowFieldList>(f.FieldsJson), documentCount, f.CreatedAt, f.Automation, gate.ScansOn(f.OrgId));

    private static bool TryName(string? raw, out string name)
    {
        name = raw?.Trim() ?? "";
        return name.Length is >= 1 and <= 120;
    }

    private Task<int> CountDocs(Guid orgId, Guid flowId, CancellationToken ct) =>
        db.FlowDocuments.CountAsync(d => d.OrgId == orgId && d.FlowId == flowId, ct);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flows = await db.Flows.Where(f => f.OrgId == orgId).OrderByDescending(f => f.CreatedAt).ToListAsync(ct);
        var counts = await db.FlowDocuments.Where(d => d.OrgId == orgId)
            .GroupBy(d => d.FlowId).Select(g => new { FlowId = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.FlowId, x => x.N, ct);
        return Ok(flows.Select(f => ToDto(f, counts.GetValueOrDefault(f.Id))));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateFlowRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        if (!TryName(request.Name, out var name)) return BadRequest(new { error = "invalid_name" });
        var now = DateTime.UtcNow;
        var flow = new Flow
        {
            Id = Guid.NewGuid(), OrgId = tenant.OrganisationId, Name = name,
            FieldsJson = DocumentJson.Serialize(FlowFieldList.Empty), CreatedAt = now, UpdatedAt = now,
        };
        db.Flows.Add(flow);
        await db.SaveChangesAsync(ct);
        return Created($"/api/flows/{flow.Id}", ToDto(flow, 0));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == id, ct);
        return flow is null ? FlowNotFound() : Ok(ToDto(flow, await CountDocs(orgId, id, ct)));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameFlowRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == id, ct);
        if (flow is null) return FlowNotFound();
        if (request.Name is null && request.Automation is null) return BadRequest(new { error = "invalid_name" });
        if (request.Name is not null)
        {
            if (!TryName(request.Name, out var name)) return BadRequest(new { error = "invalid_name" });
            flow.Name = name;
        }
        if (request.Automation is not null)
        {
            if (!FlowAutomation.All.Contains(request.Automation)) return UnprocessableEntity(new { error = "invalid_automation" });
            flow.Automation = request.Automation;
        }
        flow.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(flow, await CountDocs(orgId, id, ct)));
    }

    [HttpPut("{id:guid}/fields")]
    public async Task<IActionResult> PutFields(Guid id, [FromBody] FlowFieldList fields, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == id, ct);
        if (flow is null) return FlowNotFound();
        if (Invalid(fields) is { } detail) return BadRequest(new { error = "invalid_fields", detail });

        flow.FieldsJson = DocumentJson.Serialize(fields);
        flow.FieldsConfirmed = true;
        flow.UpdatedAt = DateTime.UtcNow;

        var pending = await db.FlowDocuments
            .Where(d => d.OrgId == orgId && d.FlowId == id && d.Status == DocumentStatus.NeedsReview && d.ReadAt != null)
            .ToListAsync(ct);
        foreach (var doc in pending)
            await rechecker.ApplyAsync(doc, flow, DocumentJson.Deserialize<ReadResult>(doc.ReadJson), ct);

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(flow, await CountDocs(orgId, id, ct)));
    }

    /// <summary>The first problem found in a field list, or null when it is valid.</summary>
    private static string? Invalid(FlowFieldList list)
    {
        if (list.Fields.Any(f => f is null) || list.Tables.Any(t => t?.Columns is null || t.Columns.Any(c => c is null)))
            return "Fields, tables and columns must not be null.";
        foreach (var t in list.Tables)
            if (t.Key is null || !FieldKey.Pattern.IsMatch(t.Key)) return $"Table key '{t.Key}' must start with a lowercase letter and use only a-z, 0-9 and _ (max 64 characters).";
        foreach (var f in list.Fields.Concat(list.Tables.SelectMany(t => t.Columns)))
        {
            if (f.Key is null || !FieldKey.Pattern.IsMatch(f.Key))
                return $"Key '{f.Key}' must start with a lowercase letter and use only a-z, 0-9 and _ (max 64 characters).";
            if (f.Type is null || !FieldTypes.All.Contains(f.Type)) return $"Type '{f.Type}' is not supported.";
            if (f.Meaning is not null && !FieldMeanings.All.Contains(f.Meaning)) return $"Meaning '{f.Meaning}' is not supported.";
        }
        var dup = list.Fields.Select(f => f.Key).GroupBy(k => k).FirstOrDefault(g => g.Count() > 1)?.Key
            ?? list.Tables.Select(t => t.Key).GroupBy(k => k).FirstOrDefault(g => g.Count() > 1)?.Key;
        dup ??= list.Tables.SelectMany(t => t.Columns.GroupBy(c => c.Key).Where(g => g.Count() > 1).Select(g => g.Key)).FirstOrDefault();
        return dup is null ? null : $"Key '{dup}' is used more than once.";
    }
}
