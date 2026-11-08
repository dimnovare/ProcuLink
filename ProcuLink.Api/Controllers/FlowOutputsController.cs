using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Documents;

namespace ProcuLink.Api.Controllers;

public sealed record OutputRequest(string Name, string Format, string? TableKey, Guid? DestinationId, OutputNodeTemplate? Template, SendWhen? SendWhen = null);

public sealed record OutputDto(
    Guid Id, Guid FlowId, string Name, string Format, string? TableKey, Guid? DestinationId,
    OutputNodeTemplate Template, DateTime CreatedAt, SendWhen? SendWhen);

public sealed record PreviewRequest(OutputNodeTemplate? Template);

[Authorize]
[ApiController]
public sealed class FlowOutputsController(ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private static OutputDto ToDto(FlowOutput o) =>
        new(o.Id, o.FlowId, o.Name, o.Format, o.TableKey, o.DestinationId,
            DocumentJson.Deserialize<OutputNodeTemplate>(o.TemplateJson), o.CreatedAt,
            o.SendWhenJson is null ? null : DocumentJson.Deserialize<SendWhen>(o.SendWhenJson));

    private static IActionResult Invalid(string detail) =>
        new UnprocessableEntityObjectResult(new { error = "output_invalid", detail });

    private static bool FitsFormat(OutputNodeTemplate t, string format) =>
        t.Format == FlowOutputFormats.TemplateFormat(format) && OutputTreeFormats.IsRenderable(t.Format);

    private static IActionResult WrongFormat(string format) => Invalid($"The template must be a {FlowOutputFormats.TemplateFormat(format)} template.");

    private static IActionResult OutputNotFound() => new NotFoundObjectResult(new { error = "output_not_found" });

    private static IActionResult FlowNotFound() => new NotFoundObjectResult(new { error = "flow_not_found" });

    [HttpGet("api/flows/{flowId:guid}/outputs")]
    public async Task<IActionResult> List(Guid flowId, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        if (!await db.Flows.AnyAsync(f => f.OrgId == orgId && f.Id == flowId, ct)) return FlowNotFound();
        var rows = await db.Set<FlowOutput>().Where(o => o.OrgId == orgId && o.FlowId == flowId).OrderBy(o => o.CreatedAt).ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [HttpPost("api/flows/{flowId:guid}/outputs")]
    public async Task<IActionResult> Create(Guid flowId, [FromBody] OutputRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var output = new FlowOutput { Id = Guid.NewGuid(), OrgId = tenant.OrganisationId, FlowId = flowId, CreatedAt = DateTime.UtcNow };
        if (await Apply(output, request, ct) is { } rejected) return rejected;
        db.Set<FlowOutput>().Add(output);
        await db.SaveChangesAsync(ct);
        return Created($"/api/flows/{flowId}/outputs/{output.Id}", ToDto(output));
    }

    [HttpPut("api/flows/{flowId:guid}/outputs/{id:guid}")]
    public async Task<IActionResult> Update(Guid flowId, Guid id, [FromBody] OutputRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var output = await db.Set<FlowOutput>().FirstOrDefaultAsync(o => o.OrgId == orgId && o.FlowId == flowId && o.Id == id, ct);
        if (output is null) return OutputNotFound();
        if (await Apply(output, request, ct) is { } rejected) return rejected;
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(output));
    }

    [HttpDelete("api/flows/{flowId:guid}/outputs/{id:guid}")]
    public async Task<IActionResult> Delete(Guid flowId, Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var removed = await db.Set<FlowOutput>().Where(o => o.OrgId == orgId && o.FlowId == flowId && o.Id == id).ExecuteDeleteAsync(ct);
        return removed == 0 ? OutputNotFound() : NoContent();
    }

    /// <summary>Renders the document through the output (or the draft template in the body, if given) and returns the text. Nothing is sent or saved.</summary>
    [HttpPost("api/documents/{documentId:guid}/outputs/{outputId:guid}/preview")]
    public async Task<IActionResult> Preview(Guid documentId, Guid outputId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PreviewRequest? body, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var doc = await db.FlowDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.OrgId == orgId && d.Id == documentId, ct);
        if (doc is null) return NotFound(new { error = "document_not_found" });
        var output = await db.Set<FlowOutput>().AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrgId == orgId && o.FlowId == doc.FlowId && o.Id == outputId, ct);
        if (output is null) return OutputNotFound();

        var template = body?.Template ?? DocumentJson.Deserialize<OutputNodeTemplate>(output.TemplateJson);
        if (body?.Template is not null && !FitsFormat(template, output.Format)) return WrongFormat(output.Format);
        // A workbook is binary; its preview is the CSV text of the same grid (xlsx renders from the csv template).
        var format = output.Format == FlowOutputFormats.Xlsx ? FlowOutputFormats.Csv : output.Format;
        var lookups = await DocumentLookups.LoadAsync(db, orgId, [template], ct);
        var render = DocumentOutputRenderer.Render(format, template, DocumentJson.Deserialize<ReadResult>(doc.ReadJson), output.TableKey, lookups);
        if (!render.Ok) return UnprocessableEntity(new { error = render.Error, detail = render.Detail });

        using var reader = new StreamReader(render.Result!.Content, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(ct);
        return Ok(new
        {
            contentType = render.Result.ContentType,
            fileName = Path.GetFileNameWithoutExtension(doc.FileName) + render.Result.FileExtension,
            content,
            lookupMisses = render.Misses ?? [],
        });
    }

    /// <summary>Validates <paramref name="request"/> and copies it onto <paramref name="output"/>; returns the refusal, or null.</summary>
    private async Task<IActionResult?> Apply(FlowOutput output, OutputRequest request, CancellationToken ct)
    {
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == output.FlowId, ct);
        if (flow is null) return FlowNotFound();

        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 120) return BadRequest(new { error = "invalid_name" });
        if (!FlowOutputFormats.All.Contains(request.Format)) return UnprocessableEntity(new { error = "unsupported_format" });
        if (!flow.FieldsConfirmed) return Conflict(new { error = "fields_unconfirmed" });

        var fields = DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson);
        if (request.TableKey is not null && fields.Tables.All(t => t.Key != request.TableKey))
            return Invalid($"The flow has no table '{request.TableKey}'.");
        if (request.DestinationId is { } destinationId
            && !await db.Set<FlowDestination>().AnyAsync(d => d.OrgId == orgId && d.Id == destinationId, ct))
            return Invalid("The destination does not exist.");

        if (request.SendWhen is { } rule
            && (fields.Fields.All(f => f.Key != rule.Field) || !SendWhenOps.All.Contains(rule.Op) || rule.Value?.Trim().Length is not (>= 1 and <= 200)))
            return Invalid("send_when");

        var template = request.Template ?? DefaultOutputTemplate.For(fields, request.Format, request.TableKey);
        if (!FitsFormat(template, request.Format)) return WrongFormat(request.Format);

        var lookups = await DocumentLookups.LoadAsync(db, orgId, [template], ct);
        if (DocumentLookups.Steps(template).Any(s => !Guid.TryParse(s.Params.ElementAtOrDefault(0), out var id) || !lookups.Has(id)
                || s.Params.ElementAtOrDefault(1) is not (null or "keep" or "blank")))
            return Invalid("lookup");

        output.Name = name;
        output.Format = request.Format;
        output.TableKey = request.TableKey;
        output.DestinationId = request.DestinationId;
        output.TemplateJson = DocumentJson.Serialize(template);
        output.SendWhenJson = request.SendWhen is { } sw ? DocumentJson.Serialize(sw with { Value = sw.Value.Trim() }) : null;
        output.UpdatedAt = DateTime.UtcNow;
        return null;
    }
}
