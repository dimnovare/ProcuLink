using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Core.Services.Ingress;
using Microsoft.Net.Http.Headers;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(
    ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate,
    IBillingService billing, DocumentIntakeService intake, DocumentRechecker rechecker,
    IFileStorageService storage, IBackgroundJobClient jobs) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private static DocumentSummaryDto ToSummary(FlowDocument d, string flowName) =>
        new(d.Id, d.FlowId, flowName, d.FileName, d.Status, d.StatusReason, d.PageCount, d.BillingUnits,
            DocumentJson.Deserialize<List<DocumentCheck>>(d.ChecksJson).Count, d.CreatedAt);

    [HttpPost("~/api/flows/{flowId:guid}/documents")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("upload")]
    // +1 MB headroom for multipart overhead; the service enforces the file cap itself, so the client gets the JSON body.
    [RequestSizeLimit(IngressLimits.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid flowId, IFormFile file, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.OrgId == orgId && f.Id == flowId, ct);
        if (flow is null) return NotFound(new { error = "flow_not_found" });

        var limit = await billing.CheckOrderLimitAsync(orgId, ct);
        if (!limit.Allowed)
            return StatusCode(429, new
            {
                error = limit.PilotExpired ? "pilot_expired" : "order_limit_reached",
                plan = limit.Plan, limit = limit.Limit, upgradeUrl = "/settings",
            });

        try
        {
            await using var stream = file.OpenReadStream();
            var doc = await intake.AcceptAsync(orgId, flowId, stream, file.FileName, file.ContentType, ct);
            return Created($"/api/documents/{doc.Id}", ToSummary(doc, flow.Name));
        }
        catch (DocumentRejectedException e)
        {
            return e.Code switch
            {
                "unsupported_format" => StatusCode(415, new { error = e.Code, accepted = DocumentIntakeService.AcceptedFormats }),
                "file_too_large" => StatusCode(413, new { error = e.Code }),
                "unreadable_file" => StatusCode(422, new { error = e.Code }),
                _ => throw e,
            };
        }
    }

    private static DocumentDto ToDto(FlowDocument d, string flowName) =>
        new(ToSummary(d, flowName), d.ContentType, DocumentJson.Deserialize<ReadResult>(d.ReadJson),
            DocumentJson.Deserialize<List<DocumentCheck>>(d.ChecksJson));

    private Task<FlowDocument?> FindAsync(Guid id, CancellationToken ct) =>
        db.FlowDocuments.FirstOrDefaultAsync(d => d.OrgId == tenant.OrganisationId && d.Id == id, ct);

    private async Task<string> FlowNameAsync(FlowDocument d, CancellationToken ct) =>
        (await db.Flows.FirstAsync(f => f.OrgId == d.OrgId && f.Id == d.FlowId, ct)).Name;

    private static readonly object NotFoundBody = new { error = "document_not_found" };

    private void Audit(FlowDocument d, string action, object payload) =>
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = d.OrgId, EntityType = "FlowDocument", EntityId = d.Id, Action = action,
            CreatedAt = DateTime.UtcNow, Payload = JsonSerializer.SerializeToDocument(payload),
        });

    [HttpGet]
    public async Task<IActionResult> List(Guid? flowId, string? status, int? take, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var q = db.FlowDocuments.Where(d => d.OrgId == tenant.OrganisationId);
        if (flowId is { } f) q = q.Where(d => d.FlowId == f);
        if (status is not null) q = q.Where(d => d.Status == status);
        var rows = await q.OrderByDescending(d => d.CreatedAt).Take(Math.Clamp(take ?? 50, 1, 200))
            .Select(d => new { Doc = d, FlowName = d.Flow!.Name }).ToListAsync(ct);
        return Ok(rows.Select(r => ToSummary(r.Doc, r.FlowName)));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var doc = await FindAsync(id, ct);
        return doc is null ? NotFound(NotFoundBody) : Ok(ToDto(doc, await FlowNameAsync(doc, ct)));
    }

    /// <summary>The media type comes from the closed allowlist, keyed by the extension intake already validated, never from the client-supplied content type.</summary>
    [HttpGet("{id:guid}/source")]
    [EnableRateLimiting("signed-url")]
    public async Task<IActionResult> GetSource(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var doc = await FindAsync(id, ct);
        if (doc is null) return NotFound(NotFoundBody);
        Stream stream;
        try { stream = await storage.DownloadAsync(doc.SourceFileKey, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return NoContent(); }

        var media = SourceDocumentMediaType.For(Path.GetExtension(doc.FileName).TrimStart('.'));
        var disposition = new ContentDispositionHeaderValue(media.RenderInline ? "inline" : "attachment");
        disposition.SetHttpFileName(doc.FileName);
        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, media.ContentType);
    }

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        if (await FindAsync(id, ct) is null) return NotFound(NotFoundBody);
        var rows = await db.AuditEvents
            .Where(a => a.OrgId == tenant.OrganisationId && a.EntityType == "FlowDocument" && a.EntityId == id)
            .OrderBy(a => a.CreatedAt).ToListAsync(ct);
        return Ok(rows.Select(a =>
        {
            var payload = a.Payload?.RootElement.Clone() ?? JsonDocument.Parse("{}").RootElement;
            var by = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("by", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() : null;
            return new HistoryEntryDto(a.Action, a.CreatedAt, by, payload);
        }));
    }

    [HttpPost("{id:guid}/retry-read")]
    public async Task<IActionResult> RetryRead(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var doc = await FindAsync(id, ct);
        if (doc is null) return NotFound(NotFoundBody);
        if (doc.Status != DocumentStatus.ReadFailed
            && !(doc.Status == DocumentStatus.NeedsReview && doc.StatusReason == "duplicate_file"))
            return Conflict(new { error = "not_retryable" });

        doc.Status = DocumentStatus.Received;
        doc.StatusReason = null;
        doc.UpdatedAt = DateTime.UtcNow;
        Audit(doc, "document.retry_read", new { by = tenant.ClerkUserId });
        await db.SaveChangesAsync(ct);
        ReadDocumentJob.Enqueue(jobs, doc.Id, doc.OrgId);
        return Ok(ToSummary(doc, await FlowNameAsync(doc, ct)));
    }

    [HttpPatch("{id:guid}/values")]
    public async Task<IActionResult> EditValues(Guid id, EditValuesRequest req, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var doc = await FindAsync(id, ct);
        if (doc is null) return NotFound(NotFoundBody);
        if (doc.Status is not (DocumentStatus.NeedsReview or DocumentStatus.Ready)) return Conflict(new { error = "not_editable" });

        var (fieldEdits, cellEdits, adds, removes) = (req.Fields ?? [], req.Cells ?? [], req.AddRowToTables ?? [], req.RemoveRows ?? []);
        if (fieldEdits.Count + cellEdits.Count + adds.Count + removes.Count == 0) return BadRequest(new { error = "no_changes" });
        if (fieldEdits.Any(x => x is null) || cellEdits.Any(x => x is null) || adds.Any(x => x is null) || removes.Any(x => x is null))
            return BadRequest(new { error = "unknown_target", target = (string?)null });

        var flow = await db.Flows.FirstAsync(f => f.OrgId == doc.OrgId && f.Id == doc.FlowId, ct);
        var flowFields = DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson);
        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        var fields = read.Fields.ToList();
        var tables = read.Tables.ToList();
        foreach (var ft in flowFields.Tables.Where(ft => tables.All(t => t.Key != ft.Key)))
            tables.Add(new ReadTable(ft.Key, ft.Label, ft.Columns.Select(c => new ReadColumn(c.Key, c.Label, c.Type, c.Meaning)).ToList(), [], true));
        var rows = tables.ToDictionary(t => t.Key, t => t.Rows.ToList());
        var changes = new List<(string Target, string? Old, string? New)>();
        IActionResult Unknown(string target) => BadRequest(new { error = "unknown_target", target });

        // Working copies only: a 400 below returns before anything is written.
        foreach (var e in fieldEdits)
        {
            var i = fields.FindIndex(f => f.Key == e.Key);
            if (i < 0)
            {
                var ff = flowFields.Fields.FirstOrDefault(f => f.Key == e.Key);
                if (ff is null) return Unknown($"field:{e.Key}");
                fields.Add(new ReadField(ff.Key, ff.Label, null, ff.Type, ff.Meaning, 0, null, false, true, false));
                i = fields.Count - 1;
            }
            if (fields[i].Value != e.Value) changes.Add(($"field:{e.Key}", fields[i].Value, e.Value));
            fields[i] = fields[i] with { Value = e.Value, Confidence = 1.0, Edited = true, NotOnPage = false };
        }

        foreach (var e in cellEdits)
        {
            var target = $"cell:{e.Table}[{e.Row}].{e.Column}";
            if (!rows.TryGetValue(e.Table, out var list) || e.Row < 0 || e.Row >= list.Count
                || tables.First(t => t.Key == e.Table).Columns.All(c => c.Key != e.Column)) return Unknown(target);
            var old = list[e.Row].Cells.GetValueOrDefault(e.Column);
            if (old?.Value != e.Value) changes.Add((target, old?.Value, e.Value));
            list[e.Row] = new ReadRow(new Dictionary<string, ReadCell>(list[e.Row].Cells)
            {
                [e.Column] = new ReadCell(e.Value, 1.0, old?.Location, false, true),
            });
        }

        foreach (var t in adds)
        {
            if (!rows.TryGetValue(t, out var list)) return Unknown($"row:{t}");
            list.Add(new ReadRow(tables.First(x => x.Key == t).Columns.ToDictionary(c => c.Key, _ => new ReadCell(null, 1.0, null, false, true))));
            changes.Add(($"row:{t}[{list.Count - 1}]", null, "row"));
        }

        foreach (var r in removes.Distinct().OrderByDescending(r => r.Row))
        {
            if (!rows.TryGetValue(r.Table, out var list) || r.Row < 0 || r.Row >= list.Count) return Unknown($"row:{r.Table}[{r.Row}]");
            list.RemoveAt(r.Row);
            changes.Add(($"row:{r.Table}[{r.Row}]", "row", null));
        }

        var edited = new ReadResult(read.KindLabel, fields, tables.Select(t => t with { Rows = rows[t.Key] }).ToList(), read.Truncated);
        doc.ReadJson = DocumentJson.Serialize(edited);
        await rechecker.ApplyAsync(doc, flow, edited, ct);
        foreach (var c in changes) Audit(doc, "document.value_edited", new { by = tenant.ClerkUserId, target = c.Target, old = c.Old, @new = c.New });
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(doc, flow.Name));
    }
}
