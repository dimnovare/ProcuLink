using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Helpers;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Output;

namespace ProcuLink.Api.Controllers;

public sealed record OutputRequest(string Name, string Format, string? TableKey, Guid? DestinationId, OutputNodeTemplate? Template, SendWhen? SendWhen = null, OutputLayout? Layout = null, string? Standard = null);

/// <summary><paramref name="Needs"/> lists the meanings a standard output needs; null for a template output.</summary>
public sealed record OutputDto(
    Guid Id, Guid FlowId, string Name, string Format, string? TableKey, Guid? DestinationId,
    OutputNodeTemplate Template, DateTime CreatedAt, SendWhen? SendWhen, OutputLayout? Layout, bool HasLogo,
    string? Standard = null, IReadOnlyList<string>? Needs = null);

public sealed record PreviewRequest(OutputNodeTemplate? Template);

public sealed record TestOutputRequest(string Format, string? TableKey, Guid DestinationId, OutputNodeTemplate Template, OutputLayout? Layout = null);

[Authorize]
[ApiController]
public sealed class FlowOutputsController(ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate, FlowDestinationService destinations) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private static OutputDto ToDto(FlowOutput o) =>
        new(o.Id, o.FlowId, o.Name, o.Format, o.TableKey, o.DestinationId,
            DocumentJson.Deserialize<OutputNodeTemplate>(o.TemplateJson), o.CreatedAt,
            o.SendWhenJson is null ? null : DocumentJson.Deserialize<SendWhen>(o.SendWhenJson),
            LayoutOf(o), o.Logo is not null, o.Standard, StandardOutputs.Find(o.Standard)?.Needs);

    private const int MaxLogoBytes = 262_144;

    private static OutputLayout? LayoutOf(FlowOutput o) => o.LayoutJson is null ? null : DocumentJson.Deserialize<OutputLayout>(o.LayoutJson);

    private static IActionResult Invalid(string detail) =>
        new UnprocessableEntityObjectResult(new { error = "output_invalid", detail });

    private static bool FitsFormat(OutputNodeTemplate t, string format) =>
        t.Format == FlowOutputFormats.TemplateFormat(format) && OutputTreeFormats.IsRenderable(t.Format);

    /// <summary>JSON nulls overwrite the empty defaults; a null node, child list, step or params would crash the walks below.</summary>
    private static bool HasNullHole(OutputNode? n) =>
        n?.Children is null || n.Rule?.FieldManipulators?.Any(m => m?.Params is null) == true || n.Children.Any(HasNullHole);

    private static IActionResult WrongFormat(string format) => Invalid($"The template must be a {FlowOutputFormats.TemplateFormat(format)} template.");

    private static IActionResult OutputNotFound() => new NotFoundObjectResult(new { error = "output_not_found" });

    private static IActionResult FlowNotFound() => new NotFoundObjectResult(new { error = "flow_not_found" });

    [HttpGet("api/flows/{flowId:guid}/outputs")]
    public async Task<IActionResult> List(Guid flowId, CancellationToken ct, [FromQuery] bool published = false)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == flowId, ct);
        if (flow is null) return FlowNotFound();
        if (published) return Ok((await FlowConfigLoader.CurrentAsync(db, flow, ct)).Outputs.Select(ToDto));
        var rows = await db.Set<FlowOutput>().Where(o => o.OrgId == orgId && o.FlowId == flowId).OrderBy(o => o.CreatedAt).ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [RequireOrgAdmin]
    [HttpPost("api/flows/{flowId:guid}/outputs")]
    public async Task<IActionResult> Create(Guid flowId, [FromBody] OutputRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        // The standard is set here only: an update can never turn a standard output into a template output, or back.
        var output = new FlowOutput { Id = Guid.NewGuid(), OrgId = tenant.OrganisationId, FlowId = flowId, CreatedAt = DateTime.UtcNow, Standard = request.Standard };
        if (await Apply(output, request, ct) is { } rejected) return rejected;
        db.Set<FlowOutput>().Add(output);
        await db.SaveChangesAsync(ct);
        return Created($"/api/flows/{flowId}/outputs/{output.Id}", ToDto(output));
    }

    [RequireOrgAdmin]
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

    [RequireOrgAdmin]
    [HttpDelete("api/flows/{flowId:guid}/outputs/{id:guid}")]
    public async Task<IActionResult> Delete(Guid flowId, Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var removed = await db.Set<FlowOutput>().Where(o => o.OrgId == orgId && o.FlowId == flowId && o.Id == id).ExecuteDeleteAsync(ct);
        return removed == 0 ? OutputNotFound() : NoContent();
    }

    [RequireOrgAdmin]
    [HttpPut("api/flows/{flowId:guid}/outputs/{id:guid}/logo")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(300_000)]
    public async Task<IActionResult> SetLogo(Guid flowId, Guid id, IFormFile file, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var output = await db.Set<FlowOutput>().FirstOrDefaultAsync(o => o.OrgId == orgId && o.FlowId == flowId && o.Id == id, ct);
        if (output is null) return OutputNotFound();
        if (file.Length > MaxLogoBytes) return UnprocessableEntity(new { error = "invalid_logo" });
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        if (!LayoutPdfWriter.IsUsableLogo(bytes)) return UnprocessableEntity(new { error = "invalid_logo" });
        output.Logo = bytes;
        output.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [RequireOrgAdmin]
    [HttpDelete("api/flows/{flowId:guid}/outputs/{id:guid}/logo")]
    public async Task<IActionResult> ClearLogo(Guid flowId, Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var output = await db.Set<FlowOutput>().FirstOrDefaultAsync(o => o.OrgId == orgId && o.FlowId == flowId && o.Id == id, ct);
        if (output is null) return OutputNotFound();
        output.Logo = null;
        output.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private const int MaxSampleBytes = 2 * 1024 * 1024;

    /// <summary>Reads a sample file and proposes an output from it: values are matched by name, then by the AI. Nothing is saved.</summary>
    [RequireOrgAdmin]
    [HttpPost("api/flows/{flowId:guid}/outputs/from-sample")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxSampleBytes + 64 * 1024)]
    public async Task<IActionResult> FromSample(Guid flowId, IFormFile file, [FromServices] IOutputDesigner designer, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == flowId, ct);
        if (flow is null) return FlowNotFound();
        if (!flow.FieldsConfirmed) return Conflict(new { error = "fields_unconfirmed" });
        if (file.Length > MaxSampleBytes) return StatusCode(413, new { error = "file_too_large" });
        if (OutputSample.FormatFor(file.FileName) is not { } format) return UnprocessableEntity(new { error = "unsupported_format" });
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (OutputSample.Infer(file.FileName, buffer.ToArray()) is not { } template) return UnprocessableEntity(new { error = "unreadable_file" });

        var fields = DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson);
        var slots = DraftBinder.Slots(template, format, fields);
        var matches = DraftBinder.ByName(slots, fields).Select(m => (m, "name")).ToList();
        matches.AddRange(DraftBinder.ByMeaning(slots, fields).Where(m => matches.All(n => n.m.Path != m.Path)).Select(m => (m, "meaning")).ToList());
        var left = slots.Where(s => matches.All(m => m.m.Path != s.Path)).ToList();
        if (left.Count > 0)
            matches.AddRange((await designer.BindAsync(orgId, left, fields, ct)).Select(m => (m, "ai")));

        var name = Path.GetFileNameWithoutExtension(file.FileName);
        var draft = DraftBinder.Apply(name.Length > 120 ? name[..120] : name, format, template, matches, fields);
        return Ok(new OutputDraftDto(draft.Name, draft.Format, draft.TableKey, draft.Template, draft.Leaves, draft.Notes));
    }

    /// <summary>Reads the receiving system's documentation and proposes a cited output from it. Nothing is saved.</summary>
    [RequireOrgAdmin]
    [HttpPost("api/flows/{flowId:guid}/outputs/from-documentation")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(IngressLimits.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> FromDocumentation(Guid flowId, IFormFile file, [FromServices] IOutputDesigner designer, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        if (!gate.DocsOutputsOn(orgId)) return StatusCode(403, new { error = "docs_outputs_off" });
        var flow = await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == flowId, ct);
        if (flow is null) return FlowNotFound();
        if (!flow.FieldsConfirmed) return Conflict(new { error = "fields_unconfirmed" });
        if (file.Length > IngressLimits.MaxFileBytes) return StatusCode(413, new { error = "file_too_large" });
        if (!DocumentationText.Supports(file.FileName)) return UnprocessableEntity(new { error = "unsupported_format" });
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        if (DocumentationText.Read(file.FileName, buffer.ToArray()) is not { } docs) return UnprocessableEntity(new { error = "unreadable_file" });

        var fields = DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson);
        var result = await designer.ProposeAsync(orgId, docs.Text, fields, ct);
        if (result.Proposal is not { } proposal)
            return result.Failure switch
            {
                "no_egress" => UnprocessableEntity(new { error = "no_egress" }),
                "ai_usage_cap" => StatusCode(429, new { error = "ai_usage_cap" }),
                _ => StatusCode(502, new { error = "ai_error" }),
            };

        var (draft, formatCite, destination) = ProposalDraft.From(proposal, fields, docs);
        var notes = docs.Truncated ? [.. draft.Notes, "truncated"] : draft.Notes;
        return Ok(new OutputDraftDto(draft.Name, draft.Format, draft.TableKey, draft.Template, draft.Leaves, notes, formatCite, destination, docs.Truncated));
    }

    /// <summary>Renders the document through the output (or the draft template in the body, if given) and returns the text. Nothing is sent or saved.</summary>
    [HttpPost("api/documents/{documentId:guid}/outputs/{outputId:guid}/preview")]
    public async Task<IActionResult> Preview(Guid documentId, Guid outputId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PreviewRequest? body, CancellationToken ct, [FromQuery] bool published = false,
        [FromQuery] bool suggest = false, [FromServices] LookupSuggester? suggester = null)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        if (suggest && !gate.LookupSuggestionsOn(orgId)) return StatusCode(403, new { error = "lookup_suggestions_off" });
        var doc = await db.FlowDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.OrgId == orgId && d.Id == documentId, ct);
        if (doc is null) return NotFound(new { error = "document_not_found" });
        var flow = await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == doc.FlowId, ct);
        if (flow is null) return FlowNotFound();
        var config = published ? await FlowConfigLoader.CurrentAsync(db, flow, ct) : await FlowConfigLoader.LiveAsync(db, flow, ct);
        var output = config.Outputs.FirstOrDefault(o => o.Id == outputId);
        if (output is null) return OutputNotFound();

        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        DocumentRender render;
        // A standard is filled by meaning: no template (a draft one included), no lookups.
        if (output.Standard is { } standard)
            render = await StandardOutputs.RenderAsync(standard, read, config.Fields, output.TableKey, orgId, ct);
        else
        {
            var template = body?.Template ?? DocumentJson.Deserialize<OutputNodeTemplate>(output.TemplateJson);
            if (body?.Template is not null && !FitsFormat(template, output.Format)) return WrongFormat(output.Format);
            // A workbook is binary; its preview is the CSV text of the same grid (xlsx renders from the csv template).
            var format = output.Format == FlowOutputFormats.Xlsx ? FlowOutputFormats.Csv : output.Format;
            if (HasNullHole(template.Root)) return Invalid("template");

            var lookups = await DocumentLookups.LoadAsync(db, orgId, [template], ct);
            render = DocumentOutputRenderer.Render(format, template, read, output.TableKey, lookups, LayoutOf(output), output.Logo);
        }
        if (!render.Ok) return UnprocessableEntity(new { error = render.Error, detail = render.Detail });

        var misses = render.Misses ?? [];
        var shown = misses.Select(m => new { lookupId = m.LookupId, value = m.Value }).ToList();
        var fileName = Path.GetFileNameWithoutExtension(doc.FileName) + render.Result!.FileExtension;
        var shownBody = new Dictionary<string, object?> { ["contentType"] = render.Result.ContentType, ["fileName"] = fileName };
        if (output.Format == FlowOutputFormats.Pdf)
        {
            // A pdf is binary: it travels as base64 and has no text to show.
            using var pdf = new MemoryStream();
            await render.Result.Content.CopyToAsync(pdf, ct);
            (shownBody["content"], shownBody["base64"]) = ("", Convert.ToBase64String(pdf.ToArray()));
        }
        else
        {
            using var reader = new StreamReader(render.Result.Content, Encoding.UTF8);
            shownBody["content"] = await reader.ReadToEndAsync(ct);
        }
        shownBody["lookupMisses"] = shown;
        if (!suggest) return Ok(shownBody);

        var found = await suggester!.SuggestAsync(orgId, misses, DocumentRows.Build(read, output.TableKey).Header, ct);
        if (found.CapReached) return StatusCode(429, new { error = "ai_usage_cap" });
        shownBody["suggestions"] = found.Suggestions;
        return Ok(shownBody);
    }

    /// <summary>Renders the document through a draft output (not saved, no logo) and sends it to one of the org's destinations, marked as a test.</summary>
    [HttpPost("api/documents/{documentId:guid}/outputs/test")]
    [RequireOrgAdmin]
    public async Task<IActionResult> TestDraft(Guid documentId, [FromBody] TestOutputRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var doc = await db.FlowDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.OrgId == orgId && d.Id == documentId, ct);
        if (doc is null) return NotFound(new { error = "document_not_found" });
        if (!FlowOutputFormats.All.Contains(request.Format)) return UnprocessableEntity(new { error = "unsupported_format" });
        if (await CheckTemplate(request.Template, request.Format, ct) is { } badTemplate) return badTemplate;

        var lookups = await DocumentLookups.LoadAsync(db, orgId, [request.Template], ct);
        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        var render = DocumentOutputRenderer.Render(request.Format,
            request.Template, read, request.TableKey, lookups, request.Layout);
        if (!render.Ok) return UnprocessableEntity(new { error = render.Error, detail = render.Detail });

        using var content = new MemoryStream();
        await render.Result!.Content.CopyToAsync(content, ct);
        var payload = new TestPayload(content.ToArray(), FileNameSanitiser.Sanitise(Path.GetFileNameWithoutExtension(doc.FileName)) + render.Result.FileExtension, render.Result.ContentType);
        var result = await destinations.TestAsync(orgId, request.DestinationId, ct, payload);
        return result is null
            ? NotFound(new { error = "destination_not_found" })
            : Ok(new { success = result.Success, message = result.ErrorMessage, responseCode = result.ResponseCode });
    }

    /// <summary>Validates <paramref name="request"/> and copies it onto <paramref name="output"/>; returns the refusal, or null.</summary>
    private async Task<IActionResult?> Apply(FlowOutput output, OutputRequest request, CancellationToken ct)
    {
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == output.FlowId, ct);
        if (flow is null) return FlowNotFound();

        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 120) return BadRequest(new { error = "invalid_name" });
        // A standard decides its own format and has no template or layout: the request's are ignored.
        var standard = StandardOutputs.Find(output.Standard);
        var format = output.Standard is null ? request.Format : standard?.Format;
        if (format is null || !FlowOutputFormats.All.Contains(format)) return UnprocessableEntity(new { error = "unsupported_format" });
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

        var templateJson = "{}";
        if (standard is null)
        {
            var template = request.Template ?? DefaultOutputTemplate.For(fields, format, request.TableKey);
            if (await CheckTemplate(template, format, ct) is { } badTemplate) return badTemplate;
            templateJson = DocumentJson.Serialize(template);
        }

        OutputLayout? layout = null;
        if (standard is null && format is FlowOutputFormats.Pdf or FlowOutputFormats.Text && request.Layout is { } l)
        {
            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            layout = new OutputLayout(Clean(l.Title), Clean(l.Header), Clean(l.Footer));
            if (layout.Title?.Length > 120 || layout.Header?.Length > 500 || layout.Footer?.Length > 500) return Invalid("layout");
        }

        output.Name = name;
        output.Format = format;
        output.TableKey = request.TableKey;
        output.DestinationId = request.DestinationId;
        output.TemplateJson = templateJson;
        output.SendWhenJson = request.SendWhen is { } sw ? DocumentJson.Serialize(sw with { Value = sw.Value.Trim() }) : null;
        output.LayoutJson = layout is null ? null : DocumentJson.Serialize(layout);
        output.UpdatedAt = DateTime.UtcNow;
        return null;
    }

    /// <summary>The checks a template must pass for <paramref name="format"/>; returns the refusal, or null.</summary>
    private async Task<IActionResult?> CheckTemplate(OutputNodeTemplate template, string format, CancellationToken ct)
    {
        if (!FitsFormat(template, format)) return WrongFormat(format);
        if (HasNullHole(template.Root)) return Invalid("template");

        var lookups = await DocumentLookups.LoadAsync(db, tenant.OrganisationId, [template], ct);
        if (DocumentLookups.Steps(template).Any(s => !Guid.TryParse(s.Params.ElementAtOrDefault(0), out var id) || !lookups.Has(id)
                || s.Params.ElementAtOrDefault(1) is not (null or "keep" or "blank")))
            return Invalid("lookup");

        // A fixed-width column is only as wide as its rule says; a missing or silly width is refused here, not at send time.
        var (header, line) = OutputTemplateEmitter.CsvColumns(template);
        if (format == FlowOutputFormats.Fixed && header.Concat(line).Any(n => n.Rule?.Width is not (>= 1 and <= 1000))) return Invalid("widths");
        return null;
    }
}
