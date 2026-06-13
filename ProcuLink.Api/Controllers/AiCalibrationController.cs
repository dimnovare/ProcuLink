using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// V9 — confidence calibration insight. Exposes the per-org empirical reliability curve computed
/// from the durable AI-suggestion decision history, for a "how reliable are our AI suggestions"
/// UI. Read-only and org-scoped: the curve is derived from THIS org's accept/reject history only.
///
/// <para>The per-suggestion calibrated confidence is attached directly to order lines (see
/// <see cref="OrdersController"/> → <c>AiMappingSuggestionDto</c>); this endpoint is the org-level
/// rollup behind it.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/ai")]
public sealed class AiCalibrationController : ControllerBase
{
    private readonly IConfidenceCalibrationService _calibration;
    private readonly ICurrentTenantService _tenant;

    public AiCalibrationController(
        IConfidenceCalibrationService calibration,
        ICurrentTenantService tenant)
    {
        _calibration = calibration;
        _tenant = tenant;
    }

    // ── GET /api/ai/calibration ───────────────────────────────────────────────

    /// <summary>
    /// Returns the org's confidence-calibration summary: every confidence bucket with its
    /// accepted/rejected counts and smoothed accept rate, whether calibration is currently active,
    /// and the sample thresholds. During cold-start (not enough resolved AI suggestions yet)
    /// <c>IsActive</c> is false and the buckets are mostly empty — the honest "not enough history"
    /// state.
    /// </summary>
    [HttpGet("calibration")]
    [ProducesResponseType(typeof(CalibrationSummaryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CalibrationSummaryDto>> GetCalibration(CancellationToken ct)
    {
        var summary = await _calibration.GetCalibrationSummaryAsync(_tenant.OrganisationId, ct);
        return Ok(CalibrationSummaryDto.From(summary));
    }
}
