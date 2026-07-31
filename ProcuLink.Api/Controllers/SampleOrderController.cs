using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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

    /// <param name="body">
    /// Optional. <c>deliverTo</c> is where the finished supplier-ready file should be emailed when
    /// the user presses send — normally their own address. Supplying it seeds an <c>email</c>
    /// delivery setup on the sample supplier, which is what lets a brand-new account reach a
    /// DELIVERED file on day one without any cooperation from a real supplier.
    /// <para>
    /// The body is optional so pre-WP-27 callers (and a plain <c>POST</c> with no content-type)
    /// keep working unchanged: they get the practice order with no delivery setup, exactly as
    /// before, and <c>deliveryConfigured: false</c> tells them so.
    /// </para>
    /// </param>
    [HttpPost]
    [EnableRateLimiting("sample-order")]
    public async Task<IActionResult> Create([FromBody] SampleOrderRequest? body, CancellationToken ct)
    {
        var result = await _samples.CreateAndEnqueueAsync(
            _tenant.OrganisationId, _tenant.ClerkUserId, body?.DeliverTo, ct);

        return Ok(new
        {
            orderId  = result.OrderId,
            isSample = true,
            // The caller MUST NOT promise a delivery this deployment cannot make: false when no
            // usable address was supplied or no email provider is configured here.
            deliveryConfigured = result.DeliveryConfigured,
        });
    }

    /// <summary>Optional request body for <c>POST /api/onboarding/sample-order</c>.</summary>
    public sealed class SampleOrderRequest
    {
        /// <summary>Single email address to deliver the practice order to. Null/blank/invalid is
        /// tolerated and simply skips the delivery seeding.</summary>
        public string? DeliverTo { get; set; }
    }
}
