using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Auth;
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

    private FlowDto ToDto(Flow f, int documentCount, bool hasChanges = false, int? streak = null) =>
        new(f.Id, f.Name, f.FieldsConfirmed, DocumentJson.Deserialize<FlowFieldList>(f.FieldsJson), documentCount, f.CreatedAt, f.Automation, gate.ScansOn(f.OrgId), gate.FormatsOn(f.OrgId), gate.DocsOutputsOn(f.OrgId), f.PublishedVersionNo, hasChanges, streak);

    private static bool TryName(string? raw, out string name)
    {
        name = raw?.Trim() ?? "";
        return name.Length is >= 1 and <= 120;
    }

    /// <summary>Every single-flow response: the web app caches each one whole, so none may drop a field.</summary>
    private async Task<FlowDto> SingleAsync(Flow flow, Guid orgId, CancellationToken ct) =>
        ToDto(flow, await CountDocs(orgId, flow.Id, ct), await FlowVersions.HasChangesAsync(db, flow, ct), await FlowStreak.CountAsync(db, orgId, flow.Id, ct));

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
        return flow is null ? FlowNotFound() : Ok(await SingleAsync(flow, orgId, ct));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Rename(Guid id, [FromBody] RenameFlowRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == id, ct);
        if (flow is null) return FlowNotFound();
        // Automatic sending is an admin decision; a member may still rename.
        if (request.Automation is not null && !RequireOrgAdminAttribute.IsAdmin(HttpContext)) return RequireOrgAdminAttribute.Refusal();
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
        return Ok(await SingleAsync(flow, orgId, ct));
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

        await rechecker.RecheckPendingAsync(flow, ct);

        await db.SaveChangesAsync(ct);
        return Ok(await SingleAsync(flow, orgId, ct));
    }

    [HttpPost("{id:guid}/publish")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == id, ct);
        if (flow is null) return FlowNotFound();
        var (_, error) = await FlowVersions.PublishAsync(db, flow, tenant.ClerkUserId, ct);
        if (error is not null) return Conflict(new { error });
        // Documents waiting for review are checked against the new version.
        await rechecker.RecheckPendingAsync(flow, ct);
        await db.SaveChangesAsync(ct);
        return Ok(await SingleAsync(flow, orgId, ct));
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<IActionResult> Versions(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == id, ct);
        if (flow is null) return FlowNotFound();
        var versions = await db.Set<FlowVersion>().AsNoTracking().Where(v => v.OrgId == orgId && v.FlowId == id)
            .OrderByDescending(v => v.VersionNo)
            .Select(v => new { v.VersionNo, v.PublishedAt, v.PublishedBy }).ToListAsync(ct);
        var ids = versions.Select(v => v.PublishedBy).OfType<string>().Distinct().ToList();
        var emails = await db.Set<AppUser>().AsNoTracking().Where(u => ids.Contains(u.ClerkUserId))
            .ToDictionaryAsync(u => u.ClerkUserId, u => u.Email, ct);
        return Ok(versions.Select(v => new FlowVersionDto(v.VersionNo, v.PublishedAt,
            v.PublishedBy is { } by ? emails.GetValueOrDefault(by) : null, v.VersionNo == flow.PublishedVersionNo)));
    }

    [HttpPost("{id:guid}/versions/{versionNo:int}/restore")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Restore(Guid id, int versionNo, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == id, ct);
        if (flow is null) return FlowNotFound();
        return await FlowVersions.RestoreAsync(db, flow, versionNo, ct) switch
        {
            null => Ok(await SingleAsync(flow, orgId, ct)),
            "version_not_found" => NotFound(new { error = "version_not_found" }),
            var error => Conflict(new { error }),
        };
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
