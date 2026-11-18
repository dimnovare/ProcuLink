using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.PhoneAlerts;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>Saves or removes this person's browser push subscription for phone alerts in the current workspace.</summary>
[Authorize]
[ApiController]
[Route("api/push-devices")]
public sealed partial class PushDevicesController(ProcuLinkDbContext db, ICurrentTenantService tenant) : ControllerBase
{
    public sealed record PushDeviceRequest(string Endpoint, string P256dh, string Auth);
    public sealed record PushDeviceRemoveRequest(string Endpoint);

    [GeneratedRegex("^[A-Za-z0-9_-]+=*$")]
    private static partial Regex Base64Url();

    private static bool IsKey(string? value) => value is { Length: <= 128 } && Base64Url().IsMatch(value);

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] PushDeviceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenant.ClerkUserId)) return StatusCode(403, new { error = "person_required" });
        if (!PushAddress.IsAllowed(request.Endpoint) || !IsKey(request.P256dh) || !IsKey(request.Auth))
            return BadRequest(new { error = "push_address_not_allowed" });

        var row = await db.Set<PushDevice>().FirstOrDefaultAsync(d => d.OrgId == tenant.OrganisationId && d.Endpoint == request.Endpoint, ct);
        if (row is null) db.Set<PushDevice>().Add(row = new PushDevice { Id = Guid.NewGuid(), OrgId = tenant.OrganisationId, Endpoint = request.Endpoint });
        row.ClerkUserId = tenant.ClerkUserId;
        row.P256dh = request.P256dh;
        row.Auth = request.Auth;
        // Starts from now, so a newly saved device never alerts about older documents.
        row.CreatedAt = row.LastCheckedAt = DateTime.UtcNow;
        row.SummaryOn = null;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Remove([FromBody] PushDeviceRemoveRequest request, CancellationToken ct)
    {
        db.RemoveRange(await db.Set<PushDevice>()
            .Where(d => d.OrgId == tenant.OrganisationId && d.ClerkUserId == tenant.ClerkUserId && d.Endpoint == request.Endpoint)
            .ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
