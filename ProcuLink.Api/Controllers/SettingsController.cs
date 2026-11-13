using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Auth;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
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
    private readonly IOrganisationSettingsService _orgSettings;
    private readonly ProcuLinkDbContext _db;

    public SettingsController(
        ICurrentTenantService tenant,
        IOrganisationSettingsService orgSettings,
        ProcuLinkDbContext db)
    {
        _tenant = tenant;
        _orgSettings = orgSettings;
        _db = db;
    }

    private async Task<bool> SupplierExistsAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        await _db.Suppliers.AsNoTracking()
            .AnyAsync(x => x.OrgId == orgId && x.Id == supplierId && x.DeletedAt == null, ct);

    // ── Organisation settings (order direction, invoice destination) ───────────
    // Both are free per-org settings — no billing gate.

    [HttpGet("organisation")]
    [ProducesResponseType(typeof(OrgSettingsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrganisation(CancellationToken ct)
        => Ok(await _orgSettings.GetAsync(_tenant.OrganisationId, ct));

    /// <summary>
    /// Partial update. The order direction stays open to every member; changing where invoices go
    /// is an admin's (it redirects every invoice), so that part is gated in-method, and audited in
    /// the one save below. (2026-11-13, plan 13b: a new destination no longer re-routes invoices
    /// parked for want of one — that was the order pipeline, and old orders are a read-only archive.)
    /// </summary>
    [HttpPut("organisation")]
    [ProducesResponseType(typeof(OrgSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateOrganisation([FromBody] UpdateOrgSettingsRequest req, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var from = await _db.Organisations.AsNoTracking().Where(o => o.Id == orgId)
            .Select(o => o.InvoiceDestinationSupplierId).FirstOrDefaultAsync(ct);
        var to = req.InvoiceDestinationSupplierId;

        if (req.InvoiceDestinationSet && to != from)
        {
            if (!RequireOrgAdminAttribute.IsAdmin(HttpContext)) return RequireOrgAdminAttribute.Refusal();
            if (to is { } destination && !await SupplierExistsAsync(orgId, destination, ct))
                return BadRequest(new { error = "invoice_destination_not_found" });

            _db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(), OrgId = orgId, EntityType = "Organisation", EntityId = orgId,
                Action = "InvoiceDestinationChanged", CreatedAt = DateTime.UtcNow,
                Payload = JsonSerializer.SerializeToDocument(new { by = _tenant.ClerkUserId, from, to }),
            });
        }

        return Ok(await _orgSettings.UpdateAsync(orgId, req, ct));
    }

    /// <summary>
    /// The mailbox a flow's address is shown as: the same configuration the router parses recipients
    /// with, so what is displayed and what actually resolves cannot drift apart.
    /// </summary>
    internal static string ComposeAddress(string token, string? inboundDomain, string? hostSuffix) =>
        !string.IsNullOrWhiteSpace(inboundDomain)
            ? $"{token}@{inboundDomain}"
            : $"orders@{token}{(string.IsNullOrWhiteSpace(hostSuffix) ? ".proculink.eu" : hostSuffix)}";
}
