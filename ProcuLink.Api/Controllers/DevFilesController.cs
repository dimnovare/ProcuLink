using Microsoft.AspNetCore.Mvc;
using ProcuLink.Infrastructure.Storage;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Dev-only passthrough that serves files from the local storage directory.
/// Activated only in the Development environment — always 404 in production.
/// No authentication required (mirrors what a real pre-signed URL would do).
/// </summary>
[ApiController]
[Route("api/dev/files")]
public class DevFilesController : ControllerBase
{
    private readonly IWebHostEnvironment _env;

    public DevFilesController(IWebHostEnvironment env)
    {
        _env = env;
    }

    /// <summary>
    /// GET /api/dev/files/{**key}
    /// Serves the file stored at the key by <see cref="LocalFileStorageService"/>.
    /// </summary>
    [HttpGet("{**key}")]
    public IActionResult Get(string key)
    {
        if (!_env.IsDevelopment())
            return NotFound();

        if (string.IsNullOrWhiteSpace(key))
            return NotFound();

        string path;
        try
        {
            // GetFullPath rejects path-traversal keys that escape the storage root.
            path = LocalFileStorageService.GetFullPath(key);
        }
        catch (ArgumentException)
        {
            return NotFound();
        }

        if (!System.IO.File.Exists(path))
            return NotFound($"File not found: {key}");

        var contentType = GetContentType(path);
        var stream      = System.IO.File.OpenRead(path);

        // FileStreamResult disposes the stream on completion.
        return File(stream, contentType, enableRangeProcessing: false);
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xml"  => "application/xml",
            ".csv"  => "text/csv",
            ".pdf"  => "application/pdf",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _       => "application/octet-stream"
        };
}
