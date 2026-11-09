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
using ProcuLink.Transform.Documents;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(
    ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate,
    IFlowDocumentSubmitter submitter, DocumentRechecker rechecker,
    DocumentSender sender, IFileStorageService storage, IBackgroundJobClient jobs, ILogger<DocumentsController> logger) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private static DocumentSummaryDto ToSummary(FlowDocument d, string flowName) => DocumentSummaryDto.From(d, flowName);

    [HttpPost("~/api/flows/{flowId:guid}/documents")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("upload")]
    // +1 MB headroom for multipart overhead; the service enforces the file cap itself, so the client gets the JSON body.
    [RequestSizeLimit(IngressLimits.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload(Guid flowId, IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var orgId = tenant.OrganisationId;
        var result = await submitter.SubmitAsync(orgId, flowId, stream, file.FileName, file.ContentType, "upload", null, ct);
        var flowName = result.Document is null ? ""
            : await db.Flows.Where(f => f.OrgId == orgId && f.Id == flowId).Select(f => f.Name).FirstAsync(ct);
        return IntakeResults.From(this, result, flowName, submitter.AcceptedFormats(orgId));
    }

    internal static DocumentDto ToDto(FlowDocument d, string flowName)
    {
        var read = DocumentJson.Deserialize<ReadResult>(d.ReadJson);
        return new(ToSummary(d, flowName), d.ContentType, read, DocumentJson.Deserialize<List<DocumentCheck>>(d.ChecksJson),
            SenderIdentity.Of(read, d.Source, d.SourceRef)?.Label);
    }

    private Task<FlowDocument?> FindAsync(Guid id, CancellationToken ct) =>
        db.FlowDocuments.FirstOrDefaultAsync(d => d.OrgId == tenant.OrganisationId && d.Id == id, ct);

    private async Task<string> FlowNameAsync(FlowDocument d, CancellationToken ct) =>
        (await db.Flows.FirstAsync(f => f.OrgId == d.OrgId && f.Id == d.FlowId, ct)).Name;

    private static readonly object NotFoundBody = new { error = "document_not_found" };

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
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The row says there is a file and storage disagrees. The storage key is not logged: it carries the uploader's file name.
            logger.LogWarning(e, "Document source refused for {DocumentId}: {Reason}", id, "storage_object_unreadable");
            return NoContent();
        }

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

    /// <summary>A read that has sat in received or reading this long was lost (a dropped enqueue or a stranded job).</summary>
    private static readonly TimeSpan StrandedAfter = TimeSpan.FromMinutes(15);

    [HttpPost("{id:guid}/retry-read")]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> RetryRead(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var doc = await FindAsync(id, ct);
        if (doc is null) return NotFound(NotFoundBody);
        var stranded = doc.Status is DocumentStatus.Received or DocumentStatus.Reading && doc.UpdatedAt < DateTime.UtcNow - StrandedAfter;
        if (doc.Status != DocumentStatus.ReadFailed && !stranded
            && !(doc.Status == DocumentStatus.NeedsReview && doc.StatusReason == "duplicate_file"))
            return Conflict(new { error = "not_retryable" });

        doc.Status = DocumentStatus.Received;
        doc.StatusReason = null;
        doc.ChecksJson = "[]";
        doc.UpdatedAt = DateTime.UtcNow;
        DocumentAudit.Add(db, doc, "document.retry_read", new { }, tenant.ClerkUserId);
        await db.SaveChangesAsync(ct);
        ReadDocumentJob.Enqueue(jobs, doc.Id, doc.OrgId);
        return Ok(ToSummary(doc, await FlowNameAsync(doc, ct)));
    }

    /// <summary>The document when its values may change; otherwise the refusal.</summary>
    private async Task<(FlowDocument? Doc, IActionResult? Refused)> FindEditableAsync(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return (null, refused);
        var doc = await FindAsync(id, ct);
        if (doc is null) return (null, NotFound(NotFoundBody));
        if (doc.Status is DocumentStatus.Sending or DocumentStatus.Sent) return (null, Conflict(new { error = "document_sent" }));
        return DocumentStatus.IsEditable(doc.Status, doc.StatusReason) ? (doc, null) : (null, Conflict(new { error = "not_editable" }));
    }

    [HttpPatch("{id:guid}/values")]
    public async Task<IActionResult> EditValues(Guid id, EditValuesRequest req, CancellationToken ct)
    {
        var (doc, refused) = await FindEditableAsync(id, ct);
        if (doc is null) return refused!;

        var (fieldEdits, cellEdits, adds, removes) = (req.Fields ?? [], req.Cells ?? [], req.AddRowToTables ?? [], req.RemoveRows ?? []);
        if (fieldEdits.Count + cellEdits.Count + adds.Count + removes.Count == 0) return BadRequest(new { error = "no_changes" });
        if (fieldEdits.Any(x => x?.Key is null) || cellEdits.Any(x => x?.Table is null || x.Column is null) || adds.Any(x => x is null) || removes.Any(x => x?.Table is null))
            return BadRequest(new { error = "unknown_target", target = (string?)null });

        var flow = await db.Flows.FirstAsync(f => f.OrgId == doc.OrgId && f.Id == doc.FlowId, ct);
        return await ApplyEditsAsync(doc, flow, DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson), req, null, ct);
    }

    /// <summary>A point-and-say edit: the value at a spot on the original document becomes the field's value, and the flow keeps a hint of where it was for this sender.</summary>
    [HttpPost("{id:guid}/teach")]
    public async Task<IActionResult> Teach(Guid id, TeachRequest req, CancellationToken ct)
    {
        var (doc, refused) = await FindEditableAsync(id, ct);
        if (doc is null) return refused!;

        var flow = await db.Flows.FirstAsync(f => f.OrgId == doc.OrgId && f.Id == doc.FlowId, ct);
        var flowFields = DocumentJson.Deserialize<FlowFieldList>(flow.FieldsJson);
        var label = string.IsNullOrWhiteSpace(req.NewLabel) ? null : req.NewLabel.Trim();
        if (string.IsNullOrWhiteSpace(req.Key) == (label is null) || label?.Length > 100) return BadRequest(new { error = "unknown_target" });
        if (label is null && flowFields.Fields.All(f => f.Key != req.Key)) return BadRequest(new { error = "unknown_target", target = $"field:{req.Key}" });

        if (label is not null && !flow.FieldsConfirmed) return Conflict(new { error = "fields_unconfirmed" });

        var spotGiven = req.Address is not null
            ? req.Page is null && req.Box is null
            : req.Page is > 0 && req.Box is { Width: > 0, Height: > 0 } b && b.X >= 0 && b.Y >= 0 && b.X + b.Width <= 1 && b.Y + b.Height <= 1;
        if (!spotGiven) return BadRequest(new { error = "unknown_spot" });
        byte[] bytes;
        await using (var source = await storage.DownloadAsync(doc.SourceFileKey, ct))
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        var spot = TeachSpots.Read(doc.FileName, bytes, req.Page, req.Box, req.Address, req.Value?.Trim());
        if (spot is null) return BadRequest(new { error = "unknown_spot" });
        var value = spot.Value?.Trim();
        if (string.IsNullOrEmpty(value)) return BadRequest(new { error = "no_text_in_area" });

        var key = req.Key!;
        if (label is not null)
        {
            key = FieldKey.Unique(flowFields.Fields.Select(f => f.Key).Concat(flowFields.Tables.Select(t => t.Key)).ToHashSet(), label);
            flowFields = new FlowFieldList([.. flowFields.Fields, new FlowField(key, label, FieldTypes.Text, null, false)], flowFields.Tables);
            flow.FieldsJson = DocumentJson.Serialize(flowFields);
        }

        var sender = SenderIdentity.Of(DocumentJson.Deserialize<ReadResult>(doc.ReadJson), doc.Source, doc.SourceRef);
        var loc = spot.Location;
        var hint = new FieldHint(key, sender?.Key ?? "", sender?.Label ?? "", Cut(spot.Near, 120), Cut(value, 200)!, loc.Page, loc.Box, loc.Address, DateTime.UtcNow);
        flow.HintsJson = DocumentJson.Serialize(FieldHints.Upsert(DocumentJson.Deserialize<List<FieldHint>>(flow.HintsJson), hint));
        flow.UpdatedAt = DateTime.UtcNow;
        // Saved by the edit below, with the value it sets.
        DocumentAudit.Add(db, doc, "document.value_taught", new { key, sender = sender?.Label }, tenant.ClerkUserId);
        return await ApplyEditsAsync(doc, flow, flowFields, new EditValuesRequest([new FieldEdit(key, value)], null, null, null),
            new Dictionary<string, SourceLocation> { [key] = loc }, ct);
    }

    private static string? Cut(string? s, int max) => s?.Length > max ? s[..max] : s;

    /// <summary>Applies validated edits, rechecks, writes history and saves. A field edit whose key is in <paramref name="locations"/> takes that location.</summary>
    private async Task<IActionResult> ApplyEditsAsync(FlowDocument doc, Flow flow, FlowFieldList flowFields, EditValuesRequest req,
        IReadOnlyDictionary<string, SourceLocation>? locations, CancellationToken ct)
    {
        var (fieldEdits, cellEdits, adds, removes) = (req.Fields ?? [], req.Cells ?? [], req.AddRowToTables ?? [], req.RemoveRows ?? []);
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
            var edited = fields[i] with
            {
                Value = e.Value, Confidence = 1.0, Edited = true, NotOnPage = false,
                Location = locations?.GetValueOrDefault(e.Key) ?? fields[i].Location,
            };
            // Confirming a value as it is still changes its confidence and Edited flag, so it still leaves a history row.
            if (edited != fields[i]) changes.Add(($"field:{e.Key}", fields[i].Value, e.Value));
            fields[i] = edited;
        }

        foreach (var e in cellEdits)
        {
            var target = $"cell:{e.Table}[{e.Row}].{e.Column}";
            if (!rows.TryGetValue(e.Table, out var list) || e.Row < 0 || e.Row >= list.Count
                || tables.First(t => t.Key == e.Table).Columns.All(c => c.Key != e.Column)) return Unknown(target);
            var old = list[e.Row].Cells.GetValueOrDefault(e.Column);
            var cell = new ReadCell(e.Value, 1.0, old?.Location, false, true);
            if (cell != old) changes.Add((target, old?.Value, e.Value));
            list[e.Row] = new ReadRow(new Dictionary<string, ReadCell>(list[e.Row].Cells) { [e.Column] = cell });
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

        var result = new ReadResult(read.KindLabel, fields, tables.Select(t => t with { Rows = rows[t.Key] }).ToList(), read.Truncated);
        doc.ReadJson = DocumentJson.Serialize(result);
        var wasSendFailed = doc.Status == DocumentStatus.SendFailed;
        await rechecker.ApplyAsync(doc, flow, result, ct);
        if (doc.StatusReason == "no_output_matches") doc.StatusReason = null; // the edit may have fixed the field that routed it
        // The undelivered outputs still need sending; an edit does not make the document unsent-ready again.
        if (wasSendFailed) doc.Status = DocumentStatus.SendFailed;
        foreach (var c in changes) DocumentAudit.Add(db, doc, "document.value_edited", new { target = c.Target, old = c.Old, @new = c.New }, tenant.ClerkUserId);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(doc, flow.Name));
    }

    [HttpPost("{id:guid}/send")]
    public async Task<IActionResult> Send(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var doc = await FindAsync(id, ct);
        if (doc is null) return NotFound(NotFoundBody);
        var outcome = await sender.SendAsync(doc, SendMode.Person, tenant.ClerkUserId, ct);
        if (outcome.Ok) return Ok(outcome.Deliveries.OrderBy(d => d.CreatedAt).Select(ToDeliveryDto));
        return outcome.Error switch
        {
            "output_invalid" when outcome.Detail is OutputFailure f => UnprocessableEntity(new { error = outcome.Error, output = f.Output, detail = f.Detail }),
            "output_invalid" => UnprocessableEntity(new { error = outcome.Error }),
            _ => Conflict(new { error = outcome.Error }),
        };
    }

    internal static object ToDeliveryDto(FlowDelivery d) =>
        new { d.Id, d.OutputName, d.Protocol, d.Status, d.Attempts, d.LastError, d.FileName, d.DeliveredAt };

    [HttpGet("{id:guid}/deliveries")]
    public async Task<IActionResult> Deliveries(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        if (await FindAsync(id, ct) is null) return NotFound(NotFoundBody);
        var rows = await db.Set<FlowDelivery>().AsNoTracking()
            .Where(d => d.OrgId == tenant.OrganisationId && d.DocumentId == id).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        return Ok(rows.Select(ToDeliveryDto));
    }

    [HttpGet("{id:guid}/deliveries/{deliveryId:guid}/file")]
    [EnableRateLimiting("signed-url")]
    public async Task<IActionResult> DeliveryFile(Guid id, Guid deliveryId, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var delivery = await db.Set<FlowDelivery>().AsNoTracking()
            .FirstOrDefaultAsync(d => d.OrgId == tenant.OrganisationId && d.DocumentId == id && d.Id == deliveryId, ct);
        if (delivery?.ArtifactKey is null) return NotFound(new { error = "delivery_not_found" });
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(await storage.DownloadAsync(delivery.ArtifactKey, ct), delivery.ContentType, delivery.FileName);
    }
}
