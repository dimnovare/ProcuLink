using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProcuLink.Api.Helpers;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Build #1 — the one-click setup endpoints (see docs/format-channel-roadmap.md §5).
///
/// Tenant scoping: every call resolves the authenticated org via
/// <see cref="ICurrentTenantService"/>; the inferencer reads the same tenant
/// to enforce per-org token caps.
///
/// Auth + rate-limit match <see cref="OrdersController.Upload"/>.
/// Neither endpoint persists anything — the controller returns the proposal
/// and the existing /api/orders/{id}/resolve endpoint applies user-confirmed
/// mappings.
/// </summary>
[Authorize]
[ApiController]
[Route("api/schema")]
public sealed class SchemaInferenceController : ControllerBase
{
    private readonly ISchemaInferencer                       _inferencer;
    private readonly ICurrentTenantService                   _tenant;
    private readonly ILogger<SchemaInferenceController>      _logger;

    /// <summary>Max accepted sample size. Mirrors OrdersController.MaxUploadBytes.</summary>
    private const long MaxSampleBytes = 10 * 1024 * 1024; // 10 MB

    public SchemaInferenceController(
        ISchemaInferencer                  inferencer,
        ICurrentTenantService              tenant,
        ILogger<SchemaInferenceController> logger)
    {
        _inferencer = inferencer;
        _tenant     = tenant;
        _logger     = logger;
    }

    // ── POST /api/schema/infer ────────────────────────────────────────────

    /// <summary>
    /// Upload a sample file and receive the inferred schema (fields, types,
    /// example values, up to 5 sample rows). Supported formats: CSV, XLSX,
    /// JSON, XML. Returns an empty schema (with FileType="unknown") when the
    /// AI provider is not configured, the org is over its monthly token cap,
    /// or the file cannot be parsed.
    ///
    /// Rate-limited under the "ai" policy (15 requests/minute per authenticated
    /// user) — this endpoint calls the paid LLM provider, so it shares the tighter
    /// AI cap rather than the looser "upload" cap.
    /// </summary>
    [HttpPost("infer")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("ai")]
    [RequestSizeLimit(MaxSampleBytes)]
    [ProducesResponseType(typeof(InferredSchema), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413RequestEntityTooLarge)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Infer(
        IFormFile        file,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "File is required." });

        if (file.Length > MaxSampleBytes)
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge,
                new { error = "File exceeds the 10 MB sample limit." });

        var fileName = FileNameSanitiser.Sanitise(file.FileName);

        _logger.LogInformation(
            "Schema inference requested for '{FileName}' ({Bytes} bytes) by org {OrgId}",
            fileName, file.Length, _tenant.OrganisationId);

        await using var stream = file.OpenReadStream();
        var schema = await _inferencer.InferSchemaAsync(stream, fileName, file.ContentType, ct);
        return Ok(schema);
    }

    // ── POST /api/schema/propose-mapping ──────────────────────────────────

    /// <summary>
    /// Given an inferred schema (typically the response from /infer), return a
    /// proposed mapping from each source field to a canonical PO target with
    /// per-field confidence and a one-line reason. Separate from /infer so the
    /// frontend can re-propose against a different canonical target without
    /// re-uploading the file.
    /// </summary>
    [HttpPost("propose-mapping")]
    [Consumes("application/json")]
    [EnableRateLimiting("ai")]
    [ProducesResponseType(typeof(ProposedMapping), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ProposeMapping(
        [FromBody] InferredSchema schema,
        CancellationToken ct = default)
    {
        if (schema is null)
            return BadRequest(new { error = "Inferred schema body is required." });

        if (schema.Fields is null || schema.Fields.Count == 0)
        {
            // Nothing to map — return an explicit empty proposal rather than 400.
            // The frontend treats this identically to a no-op AI response.
            return Ok(new ProposedMapping(Array.Empty<FieldMapping>()));
        }

        _logger.LogInformation(
            "Mapping proposal requested ({FieldCount} fields, fileType={FileType}) by org {OrgId}",
            schema.Fields.Count, schema.FileType, _tenant.OrganisationId);

        var proposal = await _inferencer.ProposeMappingAsync(schema, ct);
        return Ok(proposal);
    }
}
