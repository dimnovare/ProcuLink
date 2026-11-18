using System.Text.RegularExpressions;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services.PhoneAlerts;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>Saves or removes this person's browser push subscription for phone alerts in the current workspace.</summary>
[Authorize]
[ApiController]
[Route("api/push-devices")]
public sealed partial class PushDevicesController(ProcuLinkDbContext db, ICurrentTenantService tenant, IBackgroundJobClient jobs) : ControllerBase
{
    public sealed record PushDeviceRequest(string Endpoint, string P256dh, string Auth);
    public sealed record PushDeviceRemoveRequest(string Endpoint);

    [GeneratedRegex("^[A-Za-z0-9_-]+=*$")]
    private static partial Regex Base64Url();

    /// <summary>The key's bytes, or null when it is blank, too long or not base64url.</summary>
    private static byte[]? Key(string? value)
    {
        if (value is not { Length: <= 128 } || !Base64Url().IsMatch(value)) return null;
        try { return WebEncoders.Base64UrlDecode(value.TrimEnd('=')); }
        catch (FormatException) { return null; }
    }

    // An uncompressed P-256 point and a 16-byte secret: anything else could never be encrypted to, so it is refused, not kept forever.
    private static bool AreKeys(string? p256dh, string? auth) => Key(p256dh) is { Length: 65 } point && point[0] == 0x04 && Key(auth) is { Length: 16 };

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] PushDeviceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenant.ClerkUserId)) return StatusCode(403, new { error = "person_required" });
        if (!PushAddress.IsAllowed(request.Endpoint) || !AreKeys(request.P256dh, request.Auth))
            return BadRequest(new { error = "push_address_not_allowed" });

        var row = await db.Set<PushDevice>().FirstOrDefaultAsync(d => d.OrgId == tenant.OrganisationId && d.Endpoint == request.Endpoint, ct);
        var added = row is null;
        if (row is null) db.Set<PushDevice>().Add(row = new PushDevice { Id = Guid.NewGuid(), OrgId = tenant.OrganisationId, Endpoint = request.Endpoint });
        row.ClerkUserId = tenant.ClerkUserId;
        row.P256dh = request.P256dh;
        row.Auth = request.Auth;
        // Starts from now, so a newly saved device never alerts about older documents.
        row.CreatedAt = row.LastCheckedAt = DateTime.UtcNow;
        row.SummaryOn = null;
        // Two saves of a new device at once: the unique index refuses the second, and the first already wrote the same row.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (added) { }
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Remove([FromBody] PushDeviceRemoveRequest request, CancellationToken ct)
    {
        db.RemoveRange(await Mine(request.Endpoint).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Whether this device is still saved: a push service that refused it has had it removed, while the browser still thinks alerts are on.</summary>
    [HttpPost("check")]
    public async Task<IActionResult> Check([FromBody] PushDeviceRemoveRequest request, CancellationToken ct) =>
        Ok(new { saved = await Mine(request.Endpoint).AnyAsync(ct) });

    /// <summary>Queues one test alert to this device; the Worker, which holds the keys, sends it.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] PushDeviceRemoveRequest request, CancellationToken ct)
    {
        var id = await Mine(request.Endpoint).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        if (id is null) return NotFound(new { error = "push_device_not_found" });
        var orgId = tenant.OrganisationId;
        jobs.Enqueue<PhoneAlertJob>(j => j.SendTestAsync(orgId, id.Value, CancellationToken.None));
        return NoContent();
    }

    private IQueryable<PushDevice> Mine(string? endpoint) =>
        db.Set<PushDevice>().Where(d => d.OrgId == tenant.OrganisationId && d.ClerkUserId == tenant.ClerkUserId && d.Endpoint == endpoint);
}
