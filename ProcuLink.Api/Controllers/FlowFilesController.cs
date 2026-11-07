using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Api.Controllers;

/// <summary>The file API: a customer's own system posts a file into a flow with an org API key and fetches the result by id.</summary>
[Authorize(AuthenticationSchemes = "ApiKey")]
[ApiController]
[Route("api/v1")]
public sealed class FlowFilesController(
    ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate, IFlowDocumentSubmitter submitter) : ControllerBase
{
    private const int MaxKeyLength = 200;

    [HttpPost("flows/{flowId:guid}/documents")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("upload")]
    [RequestSizeLimit(IngressLimits.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Post(
        Guid flowId, IFormFile file, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var orgId = tenant.OrganisationId;
        if (!gate.IsEnabled(orgId)) return StatusCode(403, new { error = "document_engine_not_enabled" });
        // Ownership first: the ledger's unique index has no OrgId, so claiming on another org's flow would collide with its keys.
        var flowName = await db.Flows.Where(f => f.OrgId == orgId && f.Id == flowId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        if (flowName is null) return NotFound(new { error = "flow_not_found" });
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;
        if (key is { Length: > MaxKeyLength }) return BadRequest(new { error = "invalid_idempotency_key" });

        FlowIntakeItem? claim = null;
        if (key is not null)
        {
            var itemKey = $"api:{key}";
            claim = await IntakeLedger.ClaimAsync(db, orgId, flowId, itemKey, ct);
            if (claim is null)
            {
                var taken = await db.FlowIntakeItems.AsNoTracking()
                    .FirstOrDefaultAsync(i => i.OrgId == orgId && i.ScopeId == flowId && i.ItemKey == itemKey, ct);
                if (taken?.DocumentId is not { } id || id == IntakeLedger.Refused)
                    return Conflict(new { error = "request_in_progress" });
                var original = await db.FlowDocuments.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.OrgId == orgId && d.Id == id, ct);
                if (original is null) return NotFound(new { error = "document_not_found" });
                Response.Headers["Idempotent-Replay"] = "true";
                return Ok(DocumentSummaryDto.From(original, flowName));
            }
        }

        await using var stream = file.OpenReadStream();
        var result = await submitter.SubmitAsync(orgId, flowId, stream, file.FileName, file.ContentType, "api", null, ct);
        if (claim is not null)
        {
            if (result.Document is null) await IntakeLedger.ReleaseAsync(db, orgId, claim.Id, ct);
            else await db.FlowIntakeItems.Where(i => i.OrgId == orgId && i.Id == claim.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.DocumentId, result.Document.Id), ct);
        }

        return IntakeResults.From(this, result, flowName);
    }

    [HttpGet("documents/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var orgId = tenant.OrganisationId;
        if (!gate.IsEnabled(orgId)) return StatusCode(403, new { error = "document_engine_not_enabled" });
        var doc = await db.FlowDocuments.AsNoTracking().Include(d => d.Flow)
            .FirstOrDefaultAsync(d => d.OrgId == orgId && d.Id == id, ct);
        if (doc is null) return NotFound(new { error = "document_not_found" });
        var deliveries = await db.Set<FlowDelivery>().AsNoTracking()
            .Where(d => d.OrgId == orgId && d.DocumentId == id).OrderBy(d => d.CreatedAt).ToListAsync(ct);
        return Ok(new FlowFileResultDto(DocumentsController.ToDto(doc, doc.Flow!.Name),
            deliveries.Select(DocumentsController.ToDeliveryDto).ToList()));
    }
}
