using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Group V1 — the versioned Supplier Connection API. List/get connections and revisions,
/// create draft revisions, mark test, publish (flip active), and archive. Org-scoped via
/// <see cref="ICurrentTenantService"/>; mirrors <see cref="SupplierAcceptanceController"/>
/// conventions.
/// </summary>
[Authorize]
[ApiController]
[Route("api/connections")]
public sealed class ConnectionsController : ControllerBase
{
    private readonly ISupplierConnectionService _service;
    private readonly IReplayService             _replay;
    private readonly ICurrentTenantService      _tenant;

    public ConnectionsController(
        ISupplierConnectionService service, IReplayService replay, ICurrentTenantService tenant)
    {
        _service = service;
        _replay  = replay;
        _tenant  = tenant;
    }

    private Guid OrgId => _tenant.OrganisationId;
    private string? CurrentUser => User?.FindFirst("sub")?.Value;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ConnectionSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var connections = await _service.ListAsync(OrgId, ct);
        // Resolve active version numbers in one pass (each connection has its revisions for the detail call;
        // for the list we fetch the version no lazily via GetAsync only if needed — keep it light here).
        var dtos = new List<ConnectionSummaryDto>(connections.Count);
        foreach (var c in connections)
        {
            int? activeVersion = null;
            if (c.ActiveRevisionId is not null)
            {
                var rev = await _service.GetRevisionAsync(OrgId, c.Id, c.ActiveRevisionId.Value, ct);
                activeVersion = rev?.VersionNo;
            }
            dtos.Add(new ConnectionSummaryDto(
                c.Id, c.SupplierId, c.Name, c.ActiveRevisionId, activeVersion, c.CreatedAt, c.UpdatedAt));
        }
        return Ok(dtos);
    }

    [HttpGet("{connectionId:guid}")]
    [ProducesResponseType(typeof(ConnectionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid connectionId, CancellationToken ct)
    {
        var c = await _service.GetAsync(OrgId, connectionId, ct);
        if (c is null) return NotFound();
        return Ok(new ConnectionDetailDto(
            c.Id, c.SupplierId, c.Name, c.ActiveRevisionId, c.CreatedAt, c.UpdatedAt,
            c.Revisions.OrderByDescending(r => r.VersionNo).Select(ToRevisionSummary).ToList()));
    }

    /// <summary>Ensure a connection exists for a supplier (idempotent) and return it.</summary>
    [HttpPost("ensure/{supplierId:guid}")]
    [ProducesResponseType(typeof(ConnectionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Ensure(Guid supplierId, CancellationToken ct)
    {
        var c = await _service.EnsureConnectionAsync(OrgId, supplierId, CurrentUser, ct);
        if (c is null) return NotFound("Supplier not found.");
        var full = await _service.GetAsync(OrgId, c.Id, ct);
        return Ok(new ConnectionDetailDto(
            full!.Id, full.SupplierId, full.Name, full.ActiveRevisionId, full.CreatedAt, full.UpdatedAt,
            full.Revisions.OrderByDescending(r => r.VersionNo).Select(ToRevisionSummary).ToList()));
    }

    [HttpGet("{connectionId:guid}/revisions/{revisionId:guid}")]
    [ProducesResponseType(typeof(ConnectionRevisionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRevision(Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var rev = await _service.GetRevisionAsync(OrgId, connectionId, revisionId, ct);
        return rev is null ? NotFound() : Ok(ToRevisionDto(rev));
    }

    [HttpPost("{connectionId:guid}/revisions")]
    [ProducesResponseType(typeof(ConnectionRevisionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateDraft(
        Guid connectionId, [FromBody] CreateConnectionRevisionRequest? request, CancellationToken ct)
    {
        request ??= new CreateConnectionRevisionRequest();
        var input = request.Bundle is null ? null : ToInput(request.Bundle);
        var draft = await _service.CreateDraftAsync(
            OrgId, connectionId, input, request.CloneFromActive, CurrentUser, ct);
        return draft is null ? NotFound() : Ok(ToRevisionDto(draft));
    }

    [HttpPut("{connectionId:guid}/revisions/{revisionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateDraft(
        Guid connectionId, Guid revisionId, [FromBody] UpdateConnectionRevisionRequest request, CancellationToken ct)
    {
        var result = await _service.UpdateDraftAsync(OrgId, connectionId, revisionId, ToInput(request.Bundle), ct);
        return result switch
        {
            null  => NotFound(),
            false => Conflict("Revision is published/archived and cannot be edited."),
            true  => NoContent(),
        };
    }

    [HttpPost("{connectionId:guid}/revisions/{revisionId:guid}/test")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MarkTest(Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var result = await _service.MarkTestAsync(OrgId, connectionId, revisionId, ct);
        return result switch
        {
            null  => NotFound(),
            false => Conflict("Only draft/test revisions can be marked test."),
            true  => NoContent(),
        };
    }

    [HttpPost("{connectionId:guid}/revisions/{revisionId:guid}/publish")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var result = await _service.PublishAsync(OrgId, connectionId, revisionId, CurrentUser, ct);
        return result switch
        {
            null  => NotFound(),
            false => Conflict("Revision is already published/archived."),
            true  => NoContent(),
        };
    }

    [HttpPost("{connectionId:guid}/revisions/{revisionId:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Archive(Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var result = await _service.ArchiveAsync(OrgId, connectionId, revisionId, ct);
        return result is null ? NotFound() : NoContent();
    }

    /// <summary>
    /// Group V2 — REPLAY / impact testing. Runs historical orders through this revision (typically a
    /// DRAFT being evaluated before publish) and returns a per-order DIFF vs. the order's CURRENT
    /// result: output text diff, effective canonical-value changes, and validation pass/fail flips.
    /// NON-MUTATING and NEVER delivers — no order/artifact/validation/connection state is written.
    /// A published/archived revision can also be replayed (read-only). Bounded at
    /// <see cref="ReplayService.MaxOrders"/> orders per call.
    /// </summary>
    [HttpPost("{connectionId:guid}/revisions/{revisionId:guid}/replay")]
    [ProducesResponseType(typeof(ReplayResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Replay(
        Guid connectionId, Guid revisionId, [FromBody] ReplayRequest? request, CancellationToken ct)
    {
        var result = await _replay.ReplayAsync(OrgId, connectionId, revisionId, request ?? new ReplayRequest(), ct);
        return result is null ? NotFound() : Ok(result);
    }

    // ── mappers ──────────────────────────────────────────────────────────────
    private static ConnectionRevisionSummaryDto ToRevisionSummary(SupplierConnectionRevision r) => new(
        r.Id, r.VersionNo, r.Status, r.EffectiveFrom, r.EffectiveTo, r.PublishedAt, r.CreatedAt);

    private static ConnectionRevisionDto ToRevisionDto(SupplierConnectionRevision r) => new(
        r.Id, r.ConnectionId, r.VersionNo, r.Status,
        r.EffectiveFrom, r.EffectiveTo, r.PublishedAt, r.CreatedAt,
        r.InputMappingJson, r.OutputMappingJson, r.OutputFormat,
        r.DeliveryProtocol, r.DeliveryConfigJson, r.DeliveryAutoDeliver,
        !string.IsNullOrEmpty(r.CredentialsRef),
        r.AcceptanceProfileId, r.AcceptanceVersionNo, r.CatalogMode,
        r.ItemMappings.Select(m => new ConnectionItemMappingDto(
            m.BuyerItemCode, m.SupplierItemCode, m.Confidence, m.Source)).ToList());

    private static ConnectionRevisionDraftInput ToInput(ConnectionRevisionBundleDto b) => new(
        b.InputMappingJson, b.OutputMappingJson, b.OutputFormat,
        b.DeliveryProtocol, b.DeliveryConfigJson, b.DeliveryAutoDeliver, b.CredentialsRef,
        b.AcceptanceProfileId, b.AcceptanceVersionNo,
        string.IsNullOrWhiteSpace(b.CatalogMode) ? "live" : b.CatalogMode,
        (b.ItemMappings ?? new List<ConnectionItemMappingDto>())
            .Select(m => new ConnectionItemMappingInput(
                m.BuyerItemCode, m.SupplierItemCode, m.Confidence, m.Source)).ToList());
}
