using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        // Users is global (unique ClerkUserId), so this lookup is legitimately not org-scoped.
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ClerkUserId == tenant.ClerkUserId, ct);
        if (user is null) return Unauthorized();
        var row = await db.Set<NotificationSetting>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrgId == tenant.OrganisationId && s.UserId == user.Id, ct);
        return Ok(new { email = user.Email, onReview = row?.OnReview ?? false, onFailure = row?.OnFailure ?? false });
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] NotificationSettingsRequest request, CancellationToken ct)
    {
        if (Refuse() is { } refused) return refused;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ClerkUserId == tenant.ClerkUserId, ct);
        if (user is null) return Unauthorized();
        var orgId = tenant.OrganisationId;
        var row = await db.Set<NotificationSetting>().FirstOrDefaultAsync(s => s.OrgId == orgId && s.UserId == user.Id, ct);
        if (row is null)
            db.Set<NotificationSetting>().Add(row = new NotificationSetting { Id = Guid.NewGuid(), OrgId = orgId, UserId = user.Id });
        // Turning a switch on starts the digest from now, so the first email never covers older events.
        if ((request.OnReview && !row.OnReview) || (request.OnFailure && !row.OnFailure)) row.LastSentAt = DateTime.UtcNow;
        row.OnReview = request.OnReview;
        row.OnFailure = request.OnFailure;
        await db.SaveChangesAsync(ct);
        return Ok(new { email = user.Email, onReview = row.OnReview, onFailure = row.OnFailure });
    }
}
