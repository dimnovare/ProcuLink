using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// DESADV (Advance Shipping Notice) endpoints.
/// Full EDIFACT DESADV parsing is deferred pending EdiFabric licence.
/// Upload and list work; parsing returns 202 Accepted (file stored, not yet parsed).
/// </summary>
[Authorize]
[ApiController]
[Route("api/asns")]
public sealed class DesadvController : ControllerBase
{
    private readonly IDesadvService        _desadv;
    private readonly ICurrentTenantService _tenant;

    public DesadvController(IDesadvService desadv, ICurrentTenantService tenant)
    {
        _desadv = desadv;
        _tenant = tenant;
    }

    // POST /api/asns/upload
    [HttpPost("upload")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, [FromQuery] Guid? supplierId, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        var orgId = _tenant.OrganisationId;
        await using var stream = file.OpenReadStream();
        var asn = await _desadv.CreateStubAsync(orgId, supplierId, stream, file.FileName, file.ContentType, ct);

        // 202 Accepted — file stored, EDIFACT parsing deferred (EdiFabric licence required)
        return Accepted(new
        {
            asn.Id,
            asn.Status,
            asn.SourceFileName,
            asn.CreatedAt,
            note = "DESADV parsing is not yet active — EdiFabric licence required. File is stored safely.",
        });
    }

    // GET /api/asns
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var asns  = await _desadv.ListAsync(orgId, ct);
        return Ok(asns.Select(a => new
        {
            a.Id, a.ShipmentId, a.Status, a.DespatchDate,
            a.SourceFileName, a.CreatedAt,
        }));
    }

    // GET /api/asns/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var asn   = await _desadv.GetAsync(orgId, id, ct);
        if (asn is null) return NotFound();
        return Ok(asn);
    }
}
