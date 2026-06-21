using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Phase 2 (extensible canonical) — connection-scoped CRUD over <see cref="CanonicalFieldDef"/>,
/// the user-defined spine fields the mapper "+ Add field" affordance creates. Closes the
/// offer⇔works gap where the entity/migration/persistence shipped but no route existed, so the
/// frontend "+ Add field" 404'd.
///
/// <para>Org-scoped via <see cref="ICurrentTenantService"/> (mirrors <c>OnboardingController</c>);
/// every query also pins the connection and excludes soft-deleted rows
/// (<c>.Where(x =&gt; x.OrgId == org &amp;&amp; x.ConnectionId == connectionId &amp;&amp; x.DeletedAt == null)</c>).
/// The connection itself is verified to belong to the org before any read/write — a connection
/// from another tenant returns 404, never leaks. Removal is a SOFT delete (stamp
/// <c>DeletedAt</c>) so pinned connection revisions keep a stable view of the field set.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/connections/{connectionId:guid}/canonical-fields")]
public sealed class CanonicalFieldsController : ControllerBase
{
    private readonly ProcuLinkDbContext    _db;
    private readonly ICurrentTenantService _tenant;

    public CanonicalFieldsController(ProcuLinkDbContext db, ICurrentTenantService tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    private Guid OrgId => _tenant.OrganisationId;

    /// <summary>True when the connection exists AND belongs to the caller's org.</summary>
    private Task<bool> ConnectionBelongsToOrgAsync(Guid connectionId, CancellationToken ct) =>
        _db.SupplierConnections.AnyAsync(c => c.Id == connectionId && c.OrgId == OrgId, ct);

    // ── GET — active (non-soft-deleted) defs for (org, connection), ordered by display_order ──
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CanonicalFieldDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(Guid connectionId, CancellationToken ct)
    {
        if (!await ConnectionBelongsToOrgAsync(connectionId, ct)) return NotFound("Connection not found.");

        var defs = await _db.CanonicalFieldDefs
            .Where(x => x.OrgId == OrgId && x.ConnectionId == connectionId && x.DeletedAt == null)
            .OrderBy(x => x.Order).ThenBy(x => x.Key)
            .Select(x => new CanonicalFieldDto(x.Id, x.Key, x.Label, x.Scope, x.Type, x.StandardsRef, x.Order))
            .ToListAsync(ct);

        return Ok(defs);
    }

    // ── POST — create a new connection-scoped def ──
    [HttpPost]
    [ProducesResponseType(typeof(CanonicalFieldDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        Guid connectionId, [FromBody] CreateCanonicalFieldRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Key))
            return BadRequest("A canonical field key is required.");
        if (string.IsNullOrWhiteSpace(request.Label))
            return BadRequest("A canonical field label is required.");
        // F-1 defensive guard: "::" is the RESERVED namespace separator for bound source-token keys
        // (src::{tokenId}). A user-defined field key may never contain it, so the reserved namespace
        // can never be spoofed by a custom/canonical key.
        if (request.Key.Contains("::", StringComparison.Ordinal))
            return BadRequest("A canonical field key may not contain '::' (a reserved namespace separator).");

        if (!await ConnectionBelongsToOrgAsync(connectionId, ct)) return NotFound("Connection not found.");

        var key = request.Key.Trim();

        // Uniqueness is per (org, connection) over the ACTIVE set only — a soft-deleted key may be
        // re-created (the partial unique index was intentionally not added; enforce in code).
        var exists = await _db.CanonicalFieldDefs.AnyAsync(
            x => x.OrgId == OrgId && x.ConnectionId == connectionId
              && x.DeletedAt == null && x.Key == key, ct);
        if (exists)
            return Conflict($"A canonical field with key '{key}' already exists for this connection.");

        // Default the display order to "append after the current max" when the caller omits it.
        var order = request.Order ?? await NextOrderAsync(connectionId, ct);

        var now = DateTime.UtcNow;
        var def = new CanonicalFieldDef
        {
            Id           = Guid.NewGuid(),
            OrgId        = OrgId,
            ConnectionId = connectionId,
            Key          = key,
            Label        = request.Label.Trim(),
            Scope        = NormalizeScope(request.Scope),
            Type         = NormalizeType(request.Type),
            StandardsRef = string.IsNullOrWhiteSpace(request.StandardsRef) ? null : request.StandardsRef.Trim(),
            Order        = order,
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        _db.CanonicalFieldDefs.Add(def);
        await _db.SaveChangesAsync(ct);

        var dto = new CanonicalFieldDto(def.Id, def.Key, def.Label, def.Scope, def.Type, def.StandardsRef, def.Order);
        return CreatedAtAction(nameof(List), new { connectionId }, dto);
    }

    // ── DELETE — idempotent soft-delete by key ──
    [HttpDelete("{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid connectionId, string key, CancellationToken ct)
    {
        if (!await ConnectionBelongsToOrgAsync(connectionId, ct)) return NotFound("Connection not found.");

        var trimmed = (key ?? string.Empty).Trim();
        var def = await _db.CanonicalFieldDefs.FirstOrDefaultAsync(
            x => x.OrgId == OrgId && x.ConnectionId == connectionId
              && x.DeletedAt == null && x.Key == trimmed, ct);

        // Idempotent: an already-deleted (or never-existed) key returns 204 — the desired end
        // state (no active def with this key) already holds.
        if (def is null) return NoContent();

        def.DeletedAt = DateTime.UtcNow;
        def.UpdatedAt = def.DeletedAt.Value;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private async Task<int> NextOrderAsync(Guid connectionId, CancellationToken ct)
    {
        var max = await _db.CanonicalFieldDefs
            .Where(x => x.OrgId == OrgId && x.ConnectionId == connectionId && x.DeletedAt == null)
            .Select(x => (int?)x.Order)
            .MaxAsync(ct);
        return (max ?? 0) + 1;
    }

    private static string NormalizeScope(string? scope) =>
        scope?.Trim().ToLowerInvariant() switch
        {
            "line" => "line",
            _      => "header", // default + any unknown value
        };

    private static string NormalizeType(string? type) =>
        type?.Trim().ToLowerInvariant() switch
        {
            "number" => "number",
            "date"   => "date",
            "bool"   => "bool",
            _        => "string", // default + any unknown value
        };
}
