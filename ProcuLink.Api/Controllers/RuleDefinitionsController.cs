using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Group V4 — read API for the unified validation model. Lists the org's reusable rule definitions
/// (the catalog) and, for a supplier, its executable rule bindings (active acceptance rules joined to
/// their definitions). Read-only: authoring acceptance rules stays in
/// <see cref="SupplierAcceptanceController"/> and evaluation stays in the acceptance service. Org-scoped
/// via <see cref="ICurrentTenantService"/>.
/// </summary>
[Authorize]
[ApiController]
[Route("api/rule-definitions")]
public sealed class RuleDefinitionsController : ControllerBase
{
    private readonly IRuleDefinitionService _service;
    private readonly ICurrentTenantService  _tenant;

    public RuleDefinitionsController(IRuleDefinitionService service, ICurrentTenantService tenant)
    {
        _service = service;
        _tenant  = tenant;
    }

    private Guid OrgId => _tenant.OrganisationId;

    /// <summary>List the org's reusable rule definitions (the catalog).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RuleDefinitionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var defs = await _service.ListDefinitionsAsync(OrgId, ct);
        return Ok(defs.Select(ToDto));
    }

    /// <summary>A single rule definition by id.</summary>
    [HttpGet("{definitionId:guid}")]
    [ProducesResponseType(typeof(RuleDefinitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid definitionId, CancellationToken ct)
    {
        var def = await _service.GetDefinitionAsync(OrgId, definitionId, ct);
        return def is null ? NotFound() : Ok(ToDto(def));
    }

    /// <summary>A supplier's executable rule bindings (active acceptance rules + their definitions).</summary>
    [HttpGet("/api/suppliers/{supplierId:guid}/rule-bindings")]
    [ProducesResponseType(typeof(IReadOnlyList<SupplierRuleBindingDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSupplierBindings(Guid supplierId, CancellationToken ct)
    {
        var bindings = await _service.ListSupplierBindingsAsync(OrgId, supplierId, ct);
        return Ok(bindings.Select(ToBindingDto));
    }

    // ── mappers ──────────────────────────────────────────────────────────────
    private static RuleDefinitionDto ToDto(RuleDefinition d) => new(
        d.Id, d.Code, d.Title, d.Description, d.Scope, d.FieldPath, d.Operator,
        d.DefaultSeverity, d.DefaultExpectedValue, d.ParamHint,
        d.UblRef, d.EdifactRef, d.X12Ref, d.CxmlRef, d.IsSystem, d.CreatedAt);

    private static SupplierRuleBindingDto ToBindingDto(SupplierRuleBinding b) => new(
        b.Rule.Id, b.Rule.RuleDefinitionId, b.Rule.RuleCode,
        b.Rule.Scope, b.Rule.FieldPath, b.Rule.Operator, b.Rule.ExpectedValue,
        b.Rule.Severity, b.Rule.BlockOnFail,
        b.Definition is null ? null : ToDto(b.Definition));
}
