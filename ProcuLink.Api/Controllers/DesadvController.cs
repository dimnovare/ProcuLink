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
    public IActionResult Upload(IFormFile file, [FromQuery] Guid? supplierId)
    {
        // EDIFACT DESADV parsing is not implemented (it requires a commercial EDI
        // licence — see EdifactDesadvParser, which throws NotImplementedException).
        // 501 is the honest contract: we do NOT accept + silently shelve a file we
        // can never parse (the old 202 implied processing that never happens).
        // ASN/DESADV intake is also hidden in the UI (NEXT_PUBLIC_INBOUND_ENABLED
        // off); this guards a direct API caller.
        _ = file; _ = supplierId;
        return StatusCode(501, new
        {
            error = "ASN / EDIFACT DESADV ingestion is not available yet (it requires a commercial EDI licence). Contact support if you need it.",
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
