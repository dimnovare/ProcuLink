using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using Microsoft.Net.Http.Headers;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Helpers;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Documents;

namespace ProcuLink.Api.Controllers;

/// <summary>The workspace's one way in: its address, and adding a document without choosing a flow.</summary>
[Authorize]
[ApiController]
public sealed class InboxController(
    ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate,
    IFlowDocumentSubmitter submitter, FlowIntakeService intakes, IBillingService billing, DocumentRechecker rechecker,
    IBackgroundJobClient jobs) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private InboxDto ToDto(Guid flowId, FlowIntakeDto? email) =>
        new(flowId, email?.Address, email?.Id, gate.ScansOn(tenant.OrganisationId), gate.FormatsOn(tenant.OrganisationId));

    [HttpGet("api/inbox")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var inbox = await InboxFlow.GetOrCreateAsync(db, orgId, ct);
        var email = (await intakes.ListAsync(orgId, inbox.Id, ct)).FirstOrDefault(i => i.Kind == IntakeKind.Email);
        return Ok(ToDto(inbox.Id, email));
    }

    [HttpPost("api/inbox/new-address")]
    [RequireOrgAdmin]
    public async Task<IActionResult> NewAddress(CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var inbox = await InboxFlow.GetOrCreateAsync(db, orgId, ct);
        var email = (await intakes.ListAsync(orgId, inbox.Id, ct)).First(i => i.Kind == IntakeKind.Email);
        var rotated = await intakes.ToDtoAsync(await intakes.NewAddressAsync(orgId, inbox.Id, email.Id, ct), ct);
        return Ok(ToDto(inbox.Id, rotated));
    }

    [HttpPost("api/documents")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("upload")]
    // +1 MB headroom for multipart overhead; the service enforces the file cap itself, so the client gets the JSON body.
    [RequestSizeLimit(IngressLimits.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var inbox = await InboxFlow.GetOrCreateAsync(db, orgId, ct);
        await using var stream = file.OpenReadStream();
        var result = await submitter.SubmitAsync(orgId, inbox.Id, stream, file.FileName, file.ContentType, "upload", null, tenant.ClerkUserId, ct);
        return IntakeResults.From(this, result, inbox.Name, submitter.AcceptedFormats(orgId));
    }

    private static readonly object DocumentNotFound = new { error = "document_not_found" };

    private Task<FlowDocument?> FindAsync(Guid id, CancellationToken ct) =>
        db.FlowDocuments.FirstOrDefaultAsync(d => d.OrgId == tenant.OrganisationId && d.Id == id, ct);

    /// <summary>The person flows already holding a document of this kind: the choices for sorting it by hand.</summary>
    [HttpGet("api/documents/{id:guid}/sort-flows")]
    public async Task<IActionResult> SortFlows(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var doc = await FindAsync(id, ct);
        if (doc is null) return NotFound(DocumentNotFound);
        if (doc.Kind is null) return Ok(Array.Empty<object>());
        return Ok(await db.Flows.PersonFlows(orgId)
            .Where(f => db.FlowDocuments.Any(d => d.OrgId == orgId && d.FlowId == f.Id && d.Kind == doc.Kind && !d.IsSample))
            .OrderBy(f => f.Name).Select(f => new { f.Id, f.Name }).ToListAsync(ct));
    }

    /// <summary>A person puts the document in a flow: certain, so it is read there and remembered; in its own flow, the sort is confirmed.</summary>
    [HttpPost("api/documents/{id:guid}/move")]
    [EnableRateLimiting("ai")] // queues reads: its own and each document sorted again
    public async Task<IActionResult> Move(Guid id, MoveDocumentRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var doc = await FindAsync(id, ct);
        if (doc is null) return NotFound(DocumentNotFound);
        var flow = await db.Flows.PersonFlows(orgId).FirstOrDefaultAsync(f => f.Id == request.FlowId, ct);
        if (flow is null) return NotFound(new { error = "flow_not_found" });
        if (!DocumentStatus.IsEditable(doc.Status, doc.StatusReason)) return Conflict(new { error = "not_movable" });
        if (!await billing.CanProcessOrdersAsync(orgId, ct)) return Conflict(new { error = "processing_paused" });

        if (doc.FlowId == flow.Id)
        {
            await DocumentSort.ConfirmAsync(db, doc, ct);
            await rechecker.ApplyAsync(doc, flow, DocumentJson.Deserialize<ReadResult>(doc.ReadJson), ct);
            await DocumentSort.SaveRememberedAsync(db, doc, ct);
            return Ok(DocumentSummaryDto.From(doc, flow.Name));
        }
        var mustRead = await DocumentSort.MoveToAsync(db, doc, flow.Id, flow.Name, true, "document.moved", tenant.ClerkUserId, ct);
        await RememberAndResortAsync(doc, ct);
        if (mustRead) ReadDocumentJob.Enqueue(jobs, doc.Id, orgId);
        return Ok(DocumentSummaryDto.From(doc, flow.Name));
    }

    private Task<bool> InInboxAsync(FlowDocument doc, CancellationToken ct) =>
        db.Flows.AnyAsync(f => f.OrgId == doc.OrgId && f.Id == doc.FlowId && f.IsInbox, ct);

    /// <summary>"Do the same every time": a new flow from the document's read, with the document in it, already checked there.</summary>
    [HttpPost("api/documents/{id:guid}/set-up")]
    [EnableRateLimiting("ai")] // queues the reads of each document sorted again
    public async Task<IActionResult> SetUp(Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SetUpDocumentRequest? request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var doc = await FindAsync(id, ct);
        if (doc is null) return NotFound(DocumentNotFound);
        if (!await InInboxAsync(doc, ct)) return Conflict(new { error = "not_in_inbox" });
        if (!DocumentStatus.IsEditable(doc.Status, doc.StatusReason) || doc.ReadAt is null) return Conflict(new { error = "not_movable" });
        if (!await billing.CanProcessOrdersAsync(orgId, ct)) return Conflict(new { error = "processing_paused" });
        if (await FlowCreation.LimitReachedAsync(db, orgId, ct) is { } reached)
            return StatusCode(429, new { error = "flow_limit_reached", plan = reached.Plan, limit = reached.Cap, upgradeUrl = "/settings" });
        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        if (!FlowCreation.TryName(request?.Name ?? DocumentKind.FlowNameFor(read.KindLabel), out var name)) return BadRequest(new { error = "invalid_name" });
        var fields = ReadMerge.KeysOf(read);
        if (FlowCreation.InvalidFields(fields) is { } detail) return UnprocessableEntity(new { error = "invalid_fields", detail });

        var flow = FlowCreation.New(orgId, name, fields, confirmed: true);
        db.Flows.Add(flow);
        db.Set<FlowOutput>().Add(DefaultOutputTemplate.CsvFile(flow, fields));
        // Read in the inbox already: checked against the new flow straight away, never read again.
        await DocumentSort.MoveToAsync(db, doc, flow.Id, flow.Name, true, "document.moved", tenant.ClerkUserId, ct, newFlow: true);
        await rechecker.ApplyAsync(doc, flow, read, ct);
        await db.SaveChangesAsync(ct);
        await RememberAndResortAsync(doc, ct);
        return Created($"/api/flows/{flow.Id}", new SetUpResultDto(flow.Id, flow.Name, DocumentSummaryDto.From(doc, flow.Name)));
    }

    /// <summary>"Just this one": the read as a CSV or Excel file, with nothing set up. Allowed while processing is paused; downloading again is allowed.</summary>
    [HttpPost("api/documents/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, DownloadDocumentRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var doc = await FindAsync(id, ct);
        if (doc is null) return NotFound(DocumentNotFound);
        if (!await InInboxAsync(doc, ct)) return Conflict(new { error = "not_in_inbox" });
        var downloaded = doc.Status == DocumentStatus.Sent && doc.StatusReason == DocumentSort.Downloaded;
        if (doc.ReadAt is null || !(downloaded || doc.Status is DocumentStatus.NeedsReview or DocumentStatus.Ready))
            return Conflict(new { error = "not_read" });
        if (request.Format is not (FlowOutputFormats.Csv or FlowOutputFormats.Xlsx)) return BadRequest(new { error = "invalid_format" });

        var read = DocumentJson.Deserialize<ReadResult>(doc.ReadJson);
        var tableKey = read.Tables.FirstOrDefault()?.Key;
        var render = DocumentOutputRenderer.Render(request.Format, DefaultOutputTemplate.For(ReadMerge.KeysOf(read), request.Format, tableKey), read, tableKey);
        if (!render.Ok) return UnprocessableEntity(new { error = render.Error, detail = render.Detail });
        if (!downloaded)
        {
            DocumentSort.MarkDownloaded(db, doc, request.Format, tenant.ClerkUserId);
            await db.SaveChangesAsync(ct);
        }
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(render.Result!.Content, render.Result.ContentType,
            FileNameSanitiser.Sanitise(Path.GetFileNameWithoutExtension(doc.FileName)) + render.Result.FileExtension);
    }

    /// <summary>Remembers the document's flow, saves, then sorts again the inbox documents of its kind and enqueues their reads.</summary>
    private async Task RememberAndResortAsync(FlowDocument doc, CancellationToken ct)
    {
        await DocumentSort.RememberAsync(db, doc, ct);
        await DocumentSort.SaveRememberedAsync(db, doc, ct);
        if (doc.Kind is null) return;
        var moved = await DocumentSort.ResortWaitingAsync(db, doc.OrgId, doc.Kind, ct);
        await db.SaveChangesAsync(ct);
        foreach (var movedId in moved) ReadDocumentJob.Enqueue(jobs, movedId, doc.OrgId);
    }
}
