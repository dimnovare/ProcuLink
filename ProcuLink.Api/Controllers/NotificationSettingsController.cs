using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/notification-settings")]
public sealed class NotificationSettingsController(
    ProcuLinkDbContext db, ICurrentTenantService tenant, DocumentEngineGate gate) : ControllerBase
{
    public sealed record NotificationSettingsRequest(bool OnReview, bool OnFailure);

    private IActionResult? Refuse() =>
        gate.IsEnabled(tenant.OrganisationId) ? null : StatusCode(403, new { error = "document_engine_not_enabled" });

    private Task<NotificationSetting?> Row(CancellationToken ct) =>
        db.Set<NotificationSetting>().FirstOrDefaultAsync(
            s => s.OrgId == tenant.OrganisationId && s.ClerkUserId == tenant.ClerkUserId, ct);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var row = await Row(ct);
        return Ok(new { email = ClerkClaims.Email(User) ?? row?.Email, onReview = row?.OnReview ?? false, onFailure = row?.OnFailure ?? false });
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] NotificationSettingsRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var row = await Row(ct);
        var email = ClerkClaims.Email(User) ?? row?.Email;
        // The digest needs an address, from the session claim or the daily Clerk membership sync: refuse rather than save a switch that can never send.
        if ((request.OnReview || request.OnFailure) && email is null) return Conflict(new { error = "email_unknown" });
        if (row is null)
            db.Set<NotificationSetting>().Add(row = new NotificationSetting { Id = Guid.NewGuid(), OrgId = tenant.OrganisationId, ClerkUserId = tenant.ClerkUserId });
        // Turning the digest on (both switches were off) starts it from now, so the first email never covers older events.
        if (!row.OnReview && !row.OnFailure && (request.OnReview || request.OnFailure)) row.LastSentAt = DateTime.UtcNow;
        row.OnReview = request.OnReview;
        row.OnFailure = request.OnFailure;
        row.Email = email ?? row.Email;
        await db.SaveChangesAsync(ct);
        return Ok(new { email, onReview = row.OnReview, onFailure = row.OnFailure });
    }
}
