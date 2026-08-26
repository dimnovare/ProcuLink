using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// DESADV (Advance Shipping Notice) endpoints. Read-only: this controller lists the ASNs an
/// organisation already holds, and nothing here ingests one.
///
/// <para><b>Why there is no upload.</b> Full EDIFACT DESADV parsing needs a commercial EDI licence
/// (<c>EdifactDesadvParser</c> throws <c>NotImplementedException</c>), so <c>POST /api/asns/upload</c>
/// could only ever refuse — it answered 501 with a licence note, and the ASN page deliberately
/// rendered no control that reached it. <c>GET /api/asns/{id}</c> likewise had no caller in either
/// repository. Both were deleted 2026-08-26: a door that nothing can open is not a smaller feature
/// than a working one, it is a claim the product cannot honour, and the endpoint reachability guard
/// exists to say so.</para>
///
/// <para><b>When the licence lands</b>, the upload endpoint comes back — with a parser behind it,
/// and with cell <c>7a</c> of <c>SupplierRoutingMatrixPostgresTests</c> demanding it answer the
/// supplier-routing question before it can ship.</para>
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

    // GET /api/asns
    //
    // `packageCount` is a field the client has always asked for and never got — AsnDto in
    // project-proculink declares `packageCount: number` and the ASN page renders a Packages
    // column, which read `undefined` because this projection never counted them. It is also now
    // the only reader of the AsnPackages table anywhere in this repo: deleting GET /api/asns/{id}
    // took away its `.Include(a => a.Packages)`, and OrphanGuardTests correctly refused a table
    // that is written and never queried. The remaining AsnDto mismatches (asnNumber, shipDate,
    // supplierName) are a separate pre-existing defect and are deliberately untouched here.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var asns  = await _desadv.ListAsync(orgId, ct);
        return Ok(asns.Select(a => new
        {
            a.Id, a.ShipmentId, a.Status, a.DespatchDate,
            a.SourceFileName, a.CreatedAt, a.PackageCount,
        }));
    }
}
