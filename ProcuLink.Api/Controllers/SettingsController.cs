using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Organisation;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services.Security;

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
    private readonly OutboundRequestGuard _guard;
    private readonly ProcuLinkDbContext _db;
    private readonly IInboundAddressService _inboundAddresses;
    private readonly IConfiguration _config;

    public SettingsController(
        ICurrentTenantService tenant,
        IEmailSettingsService emailSettings,
        IPullIngressSettingsService pullIngress,
        IOrganisationSettingsService orgSettings,
        IBillingService billing,
        OutboundRequestGuard guard,
        ProcuLinkDbContext db,
        IInboundAddressService inboundAddresses,
        IConfiguration config)
    {
        _tenant = tenant;
        _emailSettings = emailSettings;
        _pullIngress = pullIngress;
        _orgSettings = orgSettings;
        _billing = billing;
        _guard = guard;
        _db = db;
        _inboundAddresses = inboundAddresses;
        _config = config;
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

    // ── Hosted inbound-email addresses ─────────────────────────────────────────
    // The addresses buyers send purchase orders to. Each one is a CREDENTIAL: presenting it is
    // what authorises delivery into this organisation, so these endpoints are how an operator
    // rotates and revokes without a deploy.

    /// <summary>
    /// Lists this organisation's inbound addresses, minting a primary if it has none yet.
    ///
    /// <para>Open to every member, unlike rotate/revoke below. The address authorises INGEST into
    /// the caller's own organisation, and every member can already ingest — they can upload a
    /// purchase order through the UI. So showing it to a member grants nothing they did not
    /// already have, and withholding it would stop the one person who actually talks to the
    /// supplier from doing their job.</para>
    /// </summary>
    [HttpGet("inbound-email")]
    [ProducesResponseType(typeof(InboundAddressListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInboundAddresses(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        await _inboundAddresses.EnsurePrimaryAsync(orgId, ct);
        return Ok(await BuildInboundResponseAsync(orgId, ct));
    }

    /// <summary>
    /// Mints a NEW primary address. The previous one keeps working until it is explicitly revoked,
    /// so an operator can hand the new address out before retiring the old one — rotation without
    /// a window in which mail bounces.
    ///
    /// <para>Administrators only: rotating is how an organisation's ingest identity changes, and
    /// revoking the old address afterwards silently stops every buyer still using it. That is the
    /// same reasoning that gates the IMAP settings above.</para>
    /// </summary>
    [HttpPost("inbound-email/rotate")]
    [RequireOrgAdmin]
    [ProducesResponseType(typeof(InboundAddressListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RotateInboundAddress(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        await _inboundAddresses.MintPrimaryAsync(orgId, "Primary", ct);
        return Ok(await BuildInboundResponseAsync(orgId, ct));
    }

    /// <summary>
    /// Revokes one address. Takes effect on the next message — a revoked address resolves to
    /// nothing, and the sender's mail is refused rather than delivered somewhere else.
    /// </summary>
    [HttpPost("inbound-email/{id:guid}/revoke")]
    [RequireOrgAdmin]
    [ProducesResponseType(typeof(InboundAddressListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeInboundAddress(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        if (!await _inboundAddresses.RevokeAsync(orgId, id, ct))
            return NotFound(new { error = "No such inbound address." });

        return Ok(await BuildInboundResponseAsync(orgId, ct));
    }

    /// <summary>
    /// Renders address rows as the full mailbox a buyer would send to. The domain comes from the
    /// same configuration the router parses recipients with, so what is displayed here and what
    /// actually resolves cannot drift apart.
    /// </summary>
    private async Task<InboundAddressListResponse> BuildInboundResponseAsync(Guid orgId, CancellationToken ct)
    {
        var rows = await _inboundAddresses.ListAsync(orgId, ct);
        var domain = _config["Inbound:Postmark:InboundDomain"];
        var suffix = _config["Inbound:Postmark:HostSuffix"];

        return new InboundAddressListResponse(rows.Select(r => new InboundAddressDto(
            r.Id,
            r.Kind,
            r.Label,
            // Null token (an unopenable blob) yields a null address rather than a broken one that
            // looks real — the row is still listed so it can be revoked.
            r.Token is null ? null : ComposeAddress(r.Token, domain, suffix),
            r.TokenPrefix,
            r.IsActive,
            r.CreatedAt,
            r.ExpiresAt,
            r.RevokedAt,
            r.LastUsedAt)).ToList());
    }

    private static string ComposeAddress(string token, string? inboundDomain, string? hostSuffix) =>
        !string.IsNullOrWhiteSpace(inboundDomain)
            ? $"{token}@{inboundDomain}"
            : $"orders@{token}{(string.IsNullOrWhiteSpace(hostSuffix) ? ".proculink.eu" : hostSuffix)}";

    [HttpGet("email")]
    [ProducesResponseType(typeof(EmailSettingsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEmail(CancellationToken ct)
    {
        var result = await _emailSettings.GetAsync(_tenant.OrganisationId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Administrators only — the inbound mirror of redirecting deliveries. This stores an IMAP
    /// host, username and password and names the supplier that arriving documents are attributed
    /// to, so a member could repoint the organisation's ingestion at a mailbox they control and
    /// feed it fabricated purchase orders. GET stays open (it never returns the password).
    ///
    /// <para>The existing 403 on this action is the PLAN gate and is unrelated: that one refuses an
    /// org whose plan does not include email ingestion, this one refuses a caller who is not an
    /// administrator. They carry different error codes and both can fire.</para>
    /// </summary>
    [HttpPut("email")]
    [RequireOrgAdmin]
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
                error = BillingGateErrors.RequiresPlan("email_ingestion", BillingFeature.EmailIngestion),
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

            // Save-time SSRF pre-check (SEC-1): reject an internal/metadata IMAP host up
            // front so the operator gets a 400 now instead of a silently-skipped poll later.
            // The poller re-checks at connect time regardless (the authoritative control).
            var hostGuard = await _guard.ValidateHostAsync(request.Host, request.Port, ct);
            if (!hostGuard.Allowed)
                return BadRequest(new { error = "host_not_allowed" });
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

    /// <summary>
    /// Administrators only, for the same reason as the IMAP settings above: host, username and
    /// password for an external server the poller then pulls purchase orders out of.
    /// </summary>
    [HttpPut("sftp")]
    [RequireOrgAdmin]
    [ProducesResponseType(typeof(SftpIngressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSftp([FromBody] UpdateSftpIngressRequest request, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        if (request.Enabled && !await _billing.HasFeatureAsync(orgId, BillingFeature.SftpIngestion, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = BillingGateErrors.RequiresPlan("sftp_ingestion", BillingFeature.SftpIngestion),
                upgradeUrl = "/settings"
            });

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

            // Save-time SSRF pre-check (SEC-1): reject an internal/metadata SFTP host up
            // front. The poller re-checks at connect time regardless (authoritative control).
            var hostGuard = await _guard.ValidateHostAsync(request.Host, request.Port, ct);
            if (!hostGuard.Allowed)
                return BadRequest(new { error = "host_not_allowed" });
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

    /// <summary>
    /// Administrators only, for the same reason as the IMAP and SFTP settings above: bucket,
    /// endpoint and access keys for an external store the poller then pulls purchase orders out of.
    /// </summary>
    [HttpPut("s3")]
    [RequireOrgAdmin]
    [ProducesResponseType(typeof(S3IngressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateS3([FromBody] UpdateS3IngressRequest request, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;

        if (request.Enabled && !await _billing.HasFeatureAsync(orgId, BillingFeature.S3Ingestion, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = BillingGateErrors.RequiresPlan("s3_ingestion", BillingFeature.S3Ingestion),
                upgradeUrl = "/settings"
            });

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

            // Save-time SSRF pre-check (SEC-1): only a custom ServiceUrl (R2/MinIO/arbitrary
            // endpoint) is tenant-controlled and therefore an SSRF vector — the standard AWS
            // endpoint (ServiceUrl null/empty) resolves to public AWS infra. The poller
            // re-checks at connect time regardless (authoritative control).
            if (!string.IsNullOrWhiteSpace(request.ServiceUrl))
            {
                var urlGuard = await _guard.ValidateAsync(request.ServiceUrl, ct);
                if (!urlGuard.Allowed)
                    return BadRequest(new { error = "host_not_allowed" });

                // TLS after the SSRF verdict, so an unreachable internal endpoint still answers
                // host_not_allowed. Over plain http the SigV4-signed request and every purchase
                // order file fetched through it cross the network in the clear.
                var urlPolicy = OutboundUrlPolicy.Inspect(request.ServiceUrl, "S3 endpoint URL");
                if (!urlPolicy.Allowed)
                    return BadRequest(new { error = urlPolicy.ErrorCode, message = urlPolicy.Message });
            }
        }

        if (request.DefaultSupplierId is { } supplierId && supplierId != Guid.Empty &&
            !await SupplierExistsAsync(orgId, supplierId, ct))
            return BadRequest(new { error = "Default supplier was not found." });

        return Ok(await _pullIngress.UpdateS3Async(orgId, request, ct));
    }
}
