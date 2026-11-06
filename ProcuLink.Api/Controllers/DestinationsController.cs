using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/destinations")]
public sealed class DestinationsController(
    ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate, FlowDestinationService destinations) : ControllerBase
{
    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private static IActionResult NotFoundBody() => new NotFoundObjectResult(new { error = "destination_not_found" });

    private static object ToDto(FlowDestination d) =>
        new
        {
            id = d.Id, name = d.Name, protocol = d.Protocol,
            config = JsonDocument.Parse(d.ConfigJson).RootElement.Clone(),
            hasCredentials = !string.IsNullOrEmpty(d.EncryptedCredentials), createdAt = d.CreatedAt,
        };

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var orgId = tenant.OrganisationId;
        var rows = await db.Set<FlowDestination>().Where(d => d.OrgId == orgId).OrderBy(d => d.Name).ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [HttpPost]
    [RequireOrgAdmin]
    public async Task<IActionResult> Create([FromBody] DestinationRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        try
        {
            var saved = await destinations.SaveAsync(tenant.OrganisationId, null, request, ct);
            return Created($"/api/destinations/{saved.Id}", ToDto(saved));
        }
        catch (DestinationException ex)
        {
            return Rejected(ex);
        }
    }

    [HttpPut("{id:guid}")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Update(Guid id, [FromBody] DestinationRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        try
        {
            return Ok(ToDto(await destinations.SaveAsync(tenant.OrganisationId, id, request, ct)));
        }
        catch (DestinationException ex)
        {
            return Rejected(ex);
        }
        catch (KeyNotFoundException)
        {
            return NotFoundBody();
        }
    }

    private IActionResult Rejected(DestinationException ex) =>
        UnprocessableEntity(new { error = ex.Code, detail = ex.Code == "invalid_destination" ? ex.Message : null });

    [HttpDelete("{id:guid}")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        return await destinations.DeleteAsync(tenant.OrganisationId, id, ct) switch
        {
            null => NotFoundBody(),
            false => Conflict(new { error = "destination_in_use" }),
            true => NoContent(),
        };
    }

    [HttpPost("{id:guid}/test")]
    [RequireOrgAdmin]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var result = await destinations.TestAsync(tenant.OrganisationId, id, ct);
        return result is null
            ? NotFoundBody()
            : Ok(new { success = result.Success, message = result.ErrorMessage, responseCode = result.ResponseCode });
    }
}
