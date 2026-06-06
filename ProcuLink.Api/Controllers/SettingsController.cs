using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Organisation;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly ICurrentTenantService _tenant;
    private readonly IEmailSettingsService _emailSettings;
    private readonly IPullIngressSettingsService _pullIngress;
    private readonly IOrganisationSettingsService _orgSettings;
    private readonly IBillingService _billing;
    private readonly ProcuLinkDbContext _db;

    public SettingsController(
        ICurrentTenantService tenant,
        IEmailSettingsService emailSettings,
        IPullIngressSettingsService pullIngress,
        IOrganisationSettingsService orgSettings,
        IBillingService billing,
        ProcuLinkDbContext db)
    {
        _tenant = tenant;
        _emailSettings = emailSettings;
        _pullIngress = pullIngress;
        _orgSettings = orgSettings;
        _billing = billing;
        _db = db;
    }

    private async Task<bool> SupplierExistsAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        await _db.Suppliers.AsNoTracking()
            .AnyAsync(x => x.OrgId == orgId && x.Id == supplierId && x.DeletedAt == null, ct);

    // ── Organisation settings (order direction) ────────────────────────────────
    // Order direction is a free per-org presentation flag — no billing gate.

    [HttpGet("organisation")]
    [ProducesResponseType(typeof(OrgSettingsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrganisation(CancellationToken ct)
        => Ok(await _orgSettings.GetAsync(_tenant.OrganisationId, ct));

    [HttpPut("organisation")]
    [ProducesResponseType(typeof(OrgSettingsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateOrganisation([FromBody] UpdateOrderDirectionRequest req, CancellationToken ct)
        => Ok(await _orgSettings.UpdateDirectionAsync(_tenant.OrganisationId, req, ct));

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

    // ── SFTP pull ─────────────────────────────────────────────────────────────

    [HttpGet("sftp")]
    [ProducesResponseType(typeof(SftpIngressResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSftp(CancellationToken ct)
        => Ok(await _pullIngress.GetSftpAsync(_tenant.OrganisationId, ct));

    [HttpPut("sftp")]
    [ProducesResponseType(typeof(SftpIngressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSftp([FromBody] UpdateSftpIngressRequest request, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        if (request.Enabled && !await _billing.HasFeatureAsync(orgId, BillingFeature.SftpIngestion, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "sftp_ingestion_requires_integration", upgradeUrl = "/settings" });

        if (request.Enabled)
        {
            if (string.IsNullOrWhiteSpace(request.Host))
                return BadRequest(new { error = "SFTP host is required." });
            if (string.IsNullOrWhiteSpace(request.Username))
                return BadRequest(new { error = "SFTP username is required." });
            if (request.DefaultSupplierId is null || request.DefaultSupplierId == Guid.Empty)
                return BadRequest(new { error = "Default supplier is required." });

            var current = await _pullIngress.GetSftpAsync(orgId, ct);
            var hasPassword = !string.IsNullOrWhiteSpace(request.Password) || (request.Password is null && current.HasPassword);
            if (!hasPassword)
                return BadRequest(new { error = "SFTP password is required." });
        }

        if (request.DefaultSupplierId is { } supplierId && supplierId != Guid.Empty &&
            !await SupplierExistsAsync(orgId, supplierId, ct))
            return BadRequest(new { error = "Default supplier was not found." });

        return Ok(await _pullIngress.UpdateSftpAsync(orgId, request, ct));
    }

    // ── S3 / R2 pull ──────────────────────────────────────────────────────────

    [HttpGet("s3")]
    [ProducesResponseType(typeof(S3IngressResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetS3(CancellationToken ct)
        => Ok(await _pullIngress.GetS3Async(_tenant.OrganisationId, ct));

    [HttpPut("s3")]
    [ProducesResponseType(typeof(S3IngressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateS3([FromBody] UpdateS3IngressRequest request, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        if (request.Enabled && !await _billing.HasFeatureAsync(orgId, BillingFeature.S3Ingestion, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "s3_ingestion_requires_integration", upgradeUrl = "/settings" });

        if (request.Enabled)
        {
            if (string.IsNullOrWhiteSpace(request.BucketName))
                return BadRequest(new { error = "Bucket name is required." });
            if (string.IsNullOrWhiteSpace(request.Region))
                return BadRequest(new { error = "Region is required." });
            if (string.IsNullOrWhiteSpace(request.AccessKeyId))
                return BadRequest(new { error = "Access key ID is required." });
            if (request.DefaultSupplierId is null || request.DefaultSupplierId == Guid.Empty)
                return BadRequest(new { error = "Default supplier is required." });

            var current = await _pullIngress.GetS3Async(orgId, ct);
            var hasSecret = !string.IsNullOrWhiteSpace(request.SecretKey) || (request.SecretKey is null && current.HasSecretKey);
            if (!hasSecret)
                return BadRequest(new { error = "Secret access key is required." });
        }

        if (request.DefaultSupplierId is { } supplierId && supplierId != Guid.Empty &&
            !await SupplierExistsAsync(orgId, supplierId, ct))
            return BadRequest(new { error = "Default supplier was not found." });

        return Ok(await _pullIngress.UpdateS3Async(orgId, request, ct));
    }
}
