using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;

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
    private readonly IBillingService            _billing;

    public ConnectionsController(
        ISupplierConnectionService service, IReplayService replay, ICurrentTenantService tenant,
        IBillingService billing)
    {
        _service = service;
        _replay  = replay;
        _tenant  = tenant;
        _billing = billing;
    }

    /// <summary>
    /// 403 when the draft bundle selects a delivery channel or output format the org's plan does
    /// not include, else null.
    ///
    /// <para>A revision is what a PINNED order actually delivers through, so this path reaches
    /// the same behaviour as the live delivery-config row. Gating only that row would have left
    /// the whole thing bypassable: save a draft with <c>DeliveryProtocol = "http"</c>, publish,
    /// and a Pilot org delivers by webhook. Both paths share
    /// <see cref="DeliveryCapabilityGate.RequiredFeature"/> so they cannot drift.</para>
    /// </summary>
    private async Task<IActionResult?> GateBundleAsync(ConnectionRevisionBundleDto? bundle, CancellationToken ct)
    {
        if (bundle is null) return null;

        var gated = await DeliveryCapabilityGate.FirstUnmetAsync(
            _billing, OrgId, bundle.DeliveryProtocol, bundle.OutputFormat, ct);
        if (gated is null) return null;

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = BillingGateErrors.RequiresPlan(gated.Value.Capability, gated.Value.Feature),
            upgradeUrl = "/settings",
        });
    }

    /// <summary>
    /// The gate for ACTIVATING an already-stored revision — publish and rollback — as opposed to
    /// authoring one.
    ///
    /// <para><b>Why authoring-time gating is not enough.</b> <see cref="GateBundleAsync"/> runs when
    /// a draft is written, and nothing revokes a stored revision when an org changes plan. Rollback
    /// clones a previously-published, now archived bundle — <c>DeliveryProtocol</c> and
    /// <c>OutputFormat</c> verbatim — into a NEW published revision and moves the connection's
    /// active pointer to it. So an org that held Enterprise, published an <c>erp_erply</c> revision,
    /// moved to a stock channel and then dropped to Growth could press Rollback and be delivering
    /// over ERP again: a capability handed back on request through the one door of three that did
    /// not ask. Publish has the same shape for a draft authored before a downgrade.</para>
    ///
    /// <para>A missing revision returns null rather than a 403 so the service's own
    /// <c>NotFound</c>/<c>Conflict</c> answer still wins — a gate must not tell a caller whether a
    /// revision they cannot see exists.</para>
    /// </summary>
    private async Task<IActionResult?> GateStoredRevisionAsync(
        Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var revision = await _service.GetRevisionAsync(OrgId, connectionId, revisionId, ct);
        if (revision is null) return null;

        var gated = await DeliveryCapabilityGate.FirstUnmetAsync(
            _billing, OrgId, revision.DeliveryProtocol, revision.OutputFormat, ct);
        if (gated is null) return null;

        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = BillingGateErrors.RequiresPlan(gated.Value.Capability, gated.Value.Feature),
            upgradeUrl = "/settings",
        });
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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateDraft(
        Guid connectionId, [FromBody] CreateConnectionRevisionRequest? request, CancellationToken ct)
    {
        request ??= new CreateConnectionRevisionRequest();
        if (await GateBundleAsync(request.Bundle, ct) is { } denied) return denied;

        var input = request.Bundle is null ? null : ToInput(request.Bundle);
        try
        {
            var draft = await _service.CreateDraftAsync(
                OrgId, connectionId, input, request.CloneFromActive, CurrentUser, ct);
            return draft is null ? NotFound() : Ok(ToRevisionDto(draft));
        }
        catch (OutboundUrlPolicyException ex)
        {
            return InsecureEndpoint(ex);
        }
        catch (ClientSuppliedCredentialsRefException ex)
        {
            return RejectedCredentialsRef(ex);
        }
    }

    [HttpPut("{connectionId:guid}/revisions/{revisionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateDraft(
        Guid connectionId, Guid revisionId, [FromBody] UpdateConnectionRevisionRequest request, CancellationToken ct)
    {
        if (await GateBundleAsync(request.Bundle, ct) is { } denied) return denied;

        bool? result;
        try
        {
            result = await _service.UpdateDraftAsync(OrgId, connectionId, revisionId, ToInput(request.Bundle), ct);
        }
        catch (OutboundUrlPolicyException ex)
        {
            return InsecureEndpoint(ex);
        }
        catch (ClientSuppliedCredentialsRefException ex)
        {
            return RejectedCredentialsRef(ex);
        }

        return result switch
        {
            null  => NotFound(),
            false => Conflict("Revision is published/archived and cannot be edited."),
            true  => NoContent(),
        };
    }

    /// <summary>
    /// A delivery endpoint the shared transport policy refuses is the caller's mistake, so it is a
    /// 400 rather than the 500 an unhandled <see cref="OutboundUrlPolicyException"/> would produce.
    /// Both the machine-readable code and the operator-facing message travel, matching the webhook
    /// and catalog endpoints; the message comes from the policy and never quotes the URL back.
    /// </summary>
    private BadRequestObjectResult InsecureEndpoint(OutboundUrlPolicyException ex) =>
        BadRequest(new { error = ex.ErrorCode, message = ex.PolicyMessage });

    /// <summary>
    /// A caller-supplied encrypted-credential reference is refused rather than silently dropped, so
    /// that whatever made a client send it surfaces instead of failing quietly. Same body shape as
    /// <see cref="InsecureEndpoint"/>; the message never quotes the submitted value.
    /// </summary>
    private BadRequestObjectResult RejectedCredentialsRef(ClientSuppliedCredentialsRefException ex) =>
        BadRequest(new { error = ClientSuppliedCredentialsRefException.Code, message = ex.PolicyMessage });

    /// <summary>
    /// Launch batch 3 — runs the REAL test pack (replay over recent orders + conformance check;
    /// never delivers), stores the evidence on the revision, marks it <c>test</c>, and returns
    /// the evidence summary. A FAILED pack still returns 200 with <c>Passed=false</c> — the run
    /// succeeded; the evidence is honest.
    /// </summary>
    [HttpPost("{connectionId:guid}/revisions/{revisionId:guid}/test")]
    [ProducesResponseType(typeof(ConnectionTestEvidenceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MarkTest(Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        var result = await _service.MarkTestAsync(OrgId, connectionId, revisionId, ct);
        return result.Status switch
        {
            ConnectionTestStatus.NotFound      => NotFound(),
            ConnectionTestStatus.InvalidStatus => Conflict("Only draft/test revisions can be marked test."),
            _ => Ok(new ConnectionTestEvidenceDto(
                result.Evidence!.Passed, result.Evidence.TestedAt, result.Evidence.SummaryJson)),
        };
    }

    [HttpPost("{connectionId:guid}/revisions/{revisionId:guid}/publish")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Publish(Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        if (await GateStoredRevisionAsync(connectionId, revisionId, ct) is { } gate) return gate;

        var result = await _service.PublishAsync(OrgId, connectionId, revisionId, CurrentUser, ct);
        return result switch
        {
            ConnectionPublishOutcome.NotFound         => NotFound(),
            ConnectionPublishOutcome.InvalidStatus    => Conflict("Revision is already published/archived."),
            ConnectionPublishOutcome.EvidenceRequired => Conflict("Run tests on this revision before publishing."),
            _ => NoContent(),
        };
    }

    /// <summary>
    /// Launch batch 3 — ROLLBACK: clones a previously-published (now archived) revision's full
    /// bundle into a NEW published revision (next version number, CreatedBy <c>rollback:{user}</c>),
    /// archives the currently published revision, and moves the connection's active pointer.
    /// The target stays archived/immutable; orders already pinned to it are unaffected.
    /// </summary>
    [HttpPost("{connectionId:guid}/revisions/{revisionId:guid}/rollback")]
    [ProducesResponseType(typeof(ConnectionRevisionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Rollback(Guid connectionId, Guid revisionId, CancellationToken ct)
    {
        if (await GateStoredRevisionAsync(connectionId, revisionId, ct) is { } gate) return gate;

        var result = await _service.RollbackAsync(OrgId, connectionId, revisionId, CurrentUser, ct);
        return result.Status switch
        {
            ConnectionRollbackStatus.NotFound      => NotFound(),
            ConnectionRollbackStatus.InvalidTarget => Conflict(
                result.Message ?? "Rollback target must be a previously published (now archived) revision."),
            _ => Ok(ToRevisionDto(result.NewRevision!)),
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

    /// <summary>
    /// WP-35 — ACT on a replay result: re-process ONE historical order under this revision and keep
    /// the output. The re-processed artifact is APPENDED; every artifact the order already held
    /// stays exactly as it was, because the old one is the evidence of what was actually sent.
    ///
    /// <para><b>This does not deliver.</b> Producing the output an operator asked to see is not a
    /// decision to send it — delivery stays a separate, explicit action, and the artifact is stored
    /// outside the deliverable namespace so no send path (redeliver, retry, ops requeue, the
    /// stranded sweep) can pick it up. The order's status and pin are untouched.</para>
    ///
    /// <para>Idempotent: repeating the call — a double submit, or a background retry — returns the
    /// same artifact rather than appending a second copy.</para>
    /// </summary>
    [HttpPost("{connectionId:guid}/revisions/{revisionId:guid}/reprocess")]
    [ProducesResponseType(typeof(ReprocessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reprocess(
        Guid connectionId, Guid revisionId, [FromBody] ReprocessRequest? request, CancellationToken ct)
    {
        if (request is null || request.OrderId == Guid.Empty)
            return BadRequest(new { error = "orderId is required." });

        var outcome = await _replay.ReprocessAsync(
            OrgId, connectionId, revisionId, request.OrderId, CurrentUser, ct);

        return outcome.Status switch
        {
            ReprocessStatus.RevisionNotFound => NotFound(),
            ReprocessStatus.OrderNotFound    => NotFound(),
            // The request was well-formed and the order real; this revision simply cannot produce a
            // document for it. 422 keeps that distinguishable from "no such order".
            ReprocessStatus.RenderFailed => UnprocessableEntity(new { error = outcome.Error }),
            ReprocessStatus.StorageUnavailable => StatusCode(
                StatusCodes.Status503ServiceUnavailable, new { error = outcome.Error }),
            _ => Ok(outcome.Response),
        };
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
            m.BuyerItemCode, m.SupplierItemCode, m.Confidence, m.Source)).ToList(),
        r.TestPassed, r.TestedAt, r.TestResultJson,
        // A revision written before enforcement reached this path keeps delivering, so the editor
        // has to be able to show that its endpoint is one the policy now refuses. Same extraction
        // and same policy as the save path and the dispatch-time log, so the three cannot disagree.
        DeliveryConfigTransport.DescribeInsecureTransport(r.DeliveryProtocol, r.DeliveryConfigJson));

    private static ConnectionRevisionDraftInput ToInput(ConnectionRevisionBundleDto b) => new(
        b.InputMappingJson, b.OutputMappingJson, b.OutputFormat,
        b.DeliveryProtocol, b.DeliveryConfigJson, b.DeliveryAutoDeliver, b.CredentialsRef,
        b.AcceptanceProfileId, b.AcceptanceVersionNo,
        string.IsNullOrWhiteSpace(b.CatalogMode) ? "live" : b.CatalogMode,
        (b.ItemMappings ?? new List<ConnectionItemMappingDto>())
            .Select(m => new ConnectionItemMappingInput(
                m.BuyerItemCode, m.SupplierItemCode, m.Confidence, m.Source)).ToList());
}
