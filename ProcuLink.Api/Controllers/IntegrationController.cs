using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
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

    [HttpPost]
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
        string? encSecret = !string.IsNullOrWhiteSpace(req.Secret)
            ? _enc.Encrypt(req.Secret) : null;

        var sub = new IntegrationSubscription
        {
            Id              = Guid.NewGuid(),
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
