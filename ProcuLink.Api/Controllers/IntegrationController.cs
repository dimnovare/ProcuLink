using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/integrations")]
public sealed class IntegrationController : ControllerBase
{
    private readonly ProcuLinkDbContext        _db;
    private readonly ICurrentTenantService     _tenant;
    private readonly DeliveryEncryptionService _enc;

    public IntegrationController(
        ProcuLinkDbContext        db,
        ICurrentTenantService     tenant,
        DeliveryEncryptionService enc)
    {
        _db     = db;
        _tenant = tenant;
        _enc    = enc;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var subs  = await _db.IntegrationSubscriptions
                             .Where(s => s.OrganisationId == orgId)
                             .OrderByDescending(s => s.CreatedAt)
                             .ToListAsync(ct);
        return Ok(subs.Select(s => new
        {
            s.Id, s.Platform, s.EventType, s.TargetUrl,
            s.IsActive, s.FailureCount, s.CreatedAt, s.UpdatedAt,
        }));
    }

    public sealed record CreateSubRequest(
        string Platform, string EventType, string TargetUrl, string? Secret);

    /// <summary>
    /// Administrators only. This stands up an outbound subscription that ships the payload of every
    /// matching order to a URL of the caller's choosing — the most direct way in the product to
    /// silently send an organisation's documents somewhere new, and it needed no approval at all.
    ///
    /// <para>Delete and Toggle below stay open: both only stop an existing subscription, are fully
    /// recoverable by creating it again, and cannot point anything anywhere. The redirection
    /// primitive is Create, so gating Create is what actually closes the hole.</para>
    /// </summary>
    [HttpPost]
    [RequireOrgAdmin]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateSubRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TargetUrl))
            return BadRequest(new { error = "TargetUrl is required." });

        var validEvents = new[] { "order.created", "order.delivered", "order.failed" };
        if (!validEvents.Contains(req.EventType))
            return BadRequest(new { error = $"EventType must be one of: {string.Join(", ", validEvents)}" });

        // Was: absolute-URI parse only. No scheme restriction at all, so file:// and gopher://
        // were stored, and plain http shipped every order payload in the clear.
        var urlPolicy = OutboundUrlPolicy.Inspect(req.TargetUrl, "Webhook target URL");
        if (!urlPolicy.Allowed)
            return BadRequest(new { error = urlPolicy.ErrorCode, message = urlPolicy.Message });

        var orgId = _tenant.OrganisationId;

        // Id first: it is part of the credential's associated data, so it must exist before the
        // secret is encrypted.
        var subscriptionId = Guid.NewGuid();

        string? encSecret = !string.IsNullOrWhiteSpace(req.Secret)
            ? _enc.Encrypt(req.Secret, CredentialScope.ForSupplier(
                orgId, CredentialPurpose.OrgIntegrationWebhookSecret, subscriptionId))
            : null;

        var sub = new IntegrationSubscription
        {
            Id              = subscriptionId,
            OrganisationId  = orgId,
            Platform        = req.Platform ?? "custom",
            EventType       = req.EventType,
            TargetUrl       = req.TargetUrl,
            EncryptedSecret = encSecret,
            IsActive        = true,
            CreatedAt       = DateTime.UtcNow,
            UpdatedAt       = DateTime.UtcNow,
        };

        _db.IntegrationSubscriptions.Add(sub);
        await _db.SaveChangesAsync(ct);
        return Ok(new { sub.Id, sub.Platform, sub.EventType, sub.TargetUrl, sub.IsActive, sub.CreatedAt });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var sub   = await _db.IntegrationSubscriptions
                             .Where(s => s.OrganisationId == orgId && s.Id == id)
                             .FirstOrDefaultAsync(ct);
        if (sub is null) return NotFound();
        _db.IntegrationSubscriptions.Remove(sub);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var sub   = await _db.IntegrationSubscriptions
                             .Where(s => s.OrganisationId == orgId && s.Id == id)
                             .FirstOrDefaultAsync(ct);
        if (sub is null) return NotFound();
        sub.IsActive  = !sub.IsActive;
        sub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { sub.Id, sub.IsActive });
    }
}
