using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;

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
