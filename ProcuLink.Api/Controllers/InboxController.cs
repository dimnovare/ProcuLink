using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>The workspace's one way in: its address, and adding a document without choosing a flow.</summary>
[Authorize]
[ApiController]
public sealed class InboxController(
    ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate,
    IFlowDocumentSubmitter submitter, FlowIntakeService intakes) : ControllerBase
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
}
