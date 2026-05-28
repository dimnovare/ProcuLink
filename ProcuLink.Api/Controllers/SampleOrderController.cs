using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Onboarding sample-order endpoint. Creates a hidden sample purchase order from an embedded
/// fixture so a new user can run the full parse → transform → deliver flow without their own data.
/// The created order is flagged <c>IsSample = true</c> and excluded from billing quota.
/// </summary>
[Authorize]
[ApiController]
[Route("api/onboarding/sample-order")]
public class SampleOrderController : ControllerBase
{
    private readonly ISampleOrderService   _samples;
    private readonly ICurrentTenantService _tenant;

    public SampleOrderController(ISampleOrderService samples, ICurrentTenantService tenant)
    {
        _samples = samples;
        _tenant  = tenant;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var orderId = await _samples.CreateAndEnqueueAsync(_tenant.OrganisationId, _tenant.ClerkUserId, ct);
        return Ok(new { orderId, isSample = true });
    }
}
