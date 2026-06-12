using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[ApiController]
[Route("api/support")]
public sealed class SupportController : ControllerBase
{
    private readonly ISupportContactService _support;
    private readonly ILogger<SupportController> _logger;

    public SupportController(ISupportContactService support, ILogger<SupportController> logger)
    {
        _support = support;
        _logger  = logger;
    }

    /// <summary>
    /// Accepts a support contact form submission from authenticated or anonymous callers.
    /// Org-scoped submissions emit a <c>support_form_submitted</c> analytics event.
    /// Anonymous + feeds an outbound email sender, so the tight "support" rate-limit
    /// policy (5/60s per source) caps the spam/amplification surface.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("support")]
    [HttpPost("contact")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Contact([FromBody] SupportContactRequest req, CancellationToken ct)
    {
        if (req is null)
            return BadRequest(new { error = "request body is required" });

        if (string.IsNullOrWhiteSpace(req.Category) || string.IsNullOrWhiteSpace(req.Message))
            return BadRequest(new { error = "category and message are required" });

        // Resolve auth context without throwing on anonymous callers.
        // ICurrentTenantService.OrganisationId throws UnauthorizedAccessException when no org_id
        // claim is present, so we read HttpContext.Items directly (the same key the middleware sets).
        Guid? orgId = HttpContext.Items["ProcuLink.OrganisationId"] is Guid id ? id : null;
        string? userId = User.FindFirst("sub")?.Value;

        // Capture User-Agent on the server side if the client didn't pass it explicitly.
        var userAgent = req.UserAgent ?? Request.Headers.UserAgent.ToString();

        var enriched = req with { UserAgent = userAgent };

        var result = await _support.SubmitAsync(orgId, userId, enriched, ct);

        _logger.LogInformation(
            "Support contact accepted: org={Org} user={User} category={Category} delivered={Delivered}",
            orgId?.ToString() ?? "(anon)", userId ?? "(none)", req.Category, result.Delivered);

        return Ok(new
        {
            ok           = true,
            delivered    = result.Delivered,
            contactEmail = result.ContactEmail,
        });
    }
}
