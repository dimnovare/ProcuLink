using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/flows/{flowId:guid}/intakes")]
public sealed class FlowIntakesController(
    ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate, FlowIntakeService intakes) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private static IActionResult IntakeNotFound() => new NotFoundObjectResult(new { error = "intake_not_found" });

    /// <summary>The engine gate, then the flow: a flow of another organisation is a 404.</summary>
    private async Task<IActionResult?> Check(Guid flowId, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        return await db.Flows.AnyAsync(f => f.OrgId == orgId && f.Id == flowId, ct)
            ? null
            : new NotFoundObjectResult(new { error = "flow_not_found" });
    }

    private IActionResult Rejected(IntakeException ex) => ex.Code switch
    {
        "intake_exists" or "intake_required" => Conflict(new { error = ex.Code }),
        _ => UnprocessableEntity(new { error = ex.Code, detail = ex.Detail }),
    };

    [HttpGet]
    public async Task<IActionResult> List(Guid flowId, CancellationToken ct) =>
        await Check(flowId, ct) ?? Ok(await intakes.ListAsync(tenant.OrganisationId, flowId, ct));

    [HttpPost]
    [RequireOrgAdmin]
    public async Task<IActionResult> Create(Guid flowId, [FromBody] IntakeRequest request, CancellationToken ct)
    {
        if (await Check(flowId, ct) is { } refused) return refused;
        try
        {
            var saved = await intakes.SaveAsync(tenant.OrganisationId, flowId, null, request, ct);
            return Created($"/api/flows/{flowId}/intakes/{saved.Id}", await intakes.ToDtoAsync(saved, ct));
        }
        catch (IntakeException ex)
        {
            return Rejected(ex);
        }
    }

    [HttpPut("{id:guid}")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Update(Guid flowId, Guid id, [FromBody] IntakeRequest request, CancellationToken ct)
    {
        if (await Check(flowId, ct) is { } refused) return refused;
        try
        {
            return Ok(await intakes.ToDtoAsync(await intakes.SaveAsync(tenant.OrganisationId, flowId, id, request, ct), ct));
        }
        catch (IntakeException ex)
        {
            return Rejected(ex);
        }
        catch (KeyNotFoundException)
        {
            return IntakeNotFound();
        }
    }

    [HttpDelete("{id:guid}")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Delete(Guid flowId, Guid id, CancellationToken ct)
    {
        if (await Check(flowId, ct) is { } refused) return refused;
        try
        {
            return await intakes.DeleteAsync(tenant.OrganisationId, flowId, id, ct) is null ? IntakeNotFound() : NoContent();
        }
        catch (IntakeException ex)
        {
            return Rejected(ex);
        }
    }

    [HttpPost("{id:guid}/new-address")]
    [RequireOrgAdmin]
    public async Task<IActionResult> NewAddress(Guid flowId, Guid id, CancellationToken ct)
    {
        if (await Check(flowId, ct) is { } refused) return refused;
        try
        {
            return Ok(await intakes.ToDtoAsync(await intakes.NewAddressAsync(tenant.OrganisationId, flowId, id, ct), ct));
        }
        catch (IntakeException ex)
        {
            return Rejected(ex);
        }
        catch (KeyNotFoundException)
        {
            return IntakeNotFound();
        }
    }
}
