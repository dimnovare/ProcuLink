using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Auth;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Org members manage their API keys (machine-to-machine access for Zapier, Make.com, etc.)
/// Auth: Clerk JWT Bearer (org members only).
/// </summary>
[Authorize]
[ApiController]
[Route("api/api-keys")]
public sealed class ApiKeyController : ControllerBase
{
    private readonly IApiKeyService        _keys;
    private readonly ICurrentTenantService _tenant;

    public ApiKeyController(IApiKeyService keys, ICurrentTenantService tenant)
    {
        _keys   = keys;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var list  = await _keys.ListAsync(orgId, ct);
        return Ok(list.Select(k => new
        {
            k.Id, k.Label, k.KeyPrefix, k.IsActive,
            k.CreatedAt, k.LastUsedAt, k.ExpiresAt
        }));
    }

    public sealed record CreateKeyRequest(string Label, DateTime? ExpiresAt);

    /// <summary>
    /// Mints a bearer credential with org-wide machine access, shown once and never retrievable.
    /// Administrators only: the key is not attributed to whoever created it, so it is a permanent,
    /// unattributable grant of access to the organisation's data — and today any member could mint
    /// one. Revoking (below) is deliberately left open to every member: spotting a leaked key and
    /// being unable to kill it would be the worse failure.
    /// </summary>
    [HttpPost]
    [RequireOrgAdmin]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateKeyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Label))
            return BadRequest(new { error = "Label is required." });

        var orgId            = _tenant.OrganisationId;
        var (entity, rawKey) = await _keys.CreateAsync(orgId, req.Label, req.ExpiresAt, ct);

        return Ok(new
        {
            entity.Id, entity.Label, entity.KeyPrefix,
            entity.IsActive, entity.CreatedAt, entity.ExpiresAt,
            // Raw key shown ONCE. Cannot be retrieved again.
            RawKey = rawKey,
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var orgId  = _tenant.OrganisationId;
        var result = await _keys.RevokeAsync(orgId, id, ct);
        return result ? NoContent() : NotFound();
    }
}
