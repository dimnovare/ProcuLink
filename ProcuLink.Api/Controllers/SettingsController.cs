using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly ICurrentTenantService _tenant;
    private readonly IEmailSettingsService _emailSettings;
    private readonly IBillingService _billing;
    private readonly ProcuLinkDbContext _db;

    public SettingsController(
        ICurrentTenantService tenant,
        IEmailSettingsService emailSettings,
        IBillingService billing,
        ProcuLinkDbContext db)
    {
        _tenant = tenant;
        _emailSettings = emailSettings;
        _billing = billing;
        _db = db;
    }

    [HttpGet("email")]
    [ProducesResponseType(typeof(EmailSettingsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEmail(CancellationToken ct)
    {
        var result = await _emailSettings.GetAsync(_tenant.OrganisationId, ct);
        return Ok(result);
    }

    [HttpPut("email")]
    [ProducesResponseType(typeof(EmailSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateEmail([FromBody] UpdateEmailSettingsRequest request, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        if (request.Enabled && !await _billing.HasFeatureAsync(orgId, BillingFeature.EmailIngestion, ct))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "email_ingestion_requires_integration",
                upgradeUrl = "/settings"
            });
        }

        if (request.Enabled)
        {
            if (string.IsNullOrWhiteSpace(request.Host))
                return BadRequest(new { error = "IMAP host is required." });

            if (string.IsNullOrWhiteSpace(request.Username))
                return BadRequest(new { error = "IMAP username is required." });

            if (request.DefaultSupplierId is null || request.DefaultSupplierId == Guid.Empty)
                return BadRequest(new { error = "Default supplier is required." });

            var current = await _emailSettings.GetAsync(orgId, ct);
            var hasPasswordAfterUpdate = !string.IsNullOrWhiteSpace(request.Password) ||
                (request.Password is null && current.HasPassword);
            if (!hasPasswordAfterUpdate)
                return BadRequest(new { error = "IMAP password is required." });
        }

        if (request.DefaultSupplierId is { } supplierId && supplierId != Guid.Empty)
        {
            var exists = await _db.Suppliers
                .AsNoTracking()
                .AnyAsync(x => x.OrgId == orgId && x.Id == supplierId && x.DeletedAt == null, ct);

            if (!exists)
                return BadRequest(new { error = "Default supplier was not found." });
        }

        var result = await _emailSettings.UpdateAsync(orgId, request, ct);
        return Ok(result);
    }
}
