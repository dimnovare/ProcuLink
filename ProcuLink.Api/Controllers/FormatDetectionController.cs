using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// "Drop any file and we'll figure out what it is" endpoint. Accepts a single multipart
/// upload and returns the detected format, confidence, suggested parser class name, and
/// best-effort metadata (PO number, supplier, line count) without persisting anything.
///
/// Used by the dual-persona upload wizard (default mode) to preview what we think we
/// received before the user commits to the full parse + validate + transform pipeline.
/// </summary>
[Authorize]
[ApiController]
[Route("api/upload/detect-format")]
public sealed class FormatDetectionController : ControllerBase
{
    private readonly IFormatDetector _detector;
    private readonly ISchemaFingerprintService _fingerprints;
    private readonly ICurrentTenantService _tenant;

    public FormatDetectionController(
        IFormatDetector detector,
        ISchemaFingerprintService fingerprints,
        ICurrentTenantService tenant)
    {
        _detector = detector;
        _fingerprints = fingerprints;
        _tenant = tenant;
    }

    /// <summary>
    /// Inspects an uploaded file and returns a <see cref="DetectedFormat"/> result. The file
    /// is buffered into memory (max 10 MiB) so the detector can sniff the leading window and
    /// rewind. We do <i>not</i> persist or enqueue anything from this endpoint.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MiB cap — sniffing only ever reads the first 1 KiB.
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Detect(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "File is empty." });

        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        ms.Position = 0;

        var detected = await _detector.DetectAsync(ms, file.FileName, ct);

        // Org-scoped schema-fingerprint boost: if this column layout has been parsed before,
        // raise confidence and surface "seen N times". No-op when there is no prior match.
        var match = await _fingerprints.LookupAsync(_tenant.OrganisationId, detected.ColumnHeaders, ct);
        detected = FingerprintBoost.Apply(detected, match);

        return Ok(detected);
    }
}
