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

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(
    ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate,
    IBillingService billing, DocumentIntakeService intake) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private static DocumentSummaryDto ToSummary(FlowDocument d, string flowName) =>
        new(d.Id, d.FlowId, flowName, d.FileName, d.Status, d.StatusReason, d.PageCount, d.BillingUnits,
            DocumentJson.Deserialize<List<DocumentCheck>>(d.ChecksJson).Count, d.CreatedAt);

    [HttpPost("~/api/flows/{flowId:guid}/documents")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("upload")]
    [RequestSizeLimit(IngressLimits.MaxFileBytes)]
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
        catch (DocumentRejectedException e) when (e.Code == "unsupported_format")
        {
            return StatusCode(415, new { error = "unsupported_format", accepted = DocumentIntakeService.AcceptedFormats });
        }
        catch (DocumentRejectedException)
        {
            return StatusCode(413, new { error = "file_too_large" });
        }
    }
}
