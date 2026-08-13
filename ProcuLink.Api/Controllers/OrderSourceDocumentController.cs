using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Controllers;

/// <summary>
/// Serves the ORIGINAL uploaded document for an order, so a reviewer can see the paper the
/// extracted values came from.
///
/// <para>Deliberately its own controller rather than another action on <c>OrdersController</c>:
/// that file is ~2,700 lines with a 21-parameter constructor, and every test that builds it lists
/// those parameters positionally. A separate controller sharing the <c>api/orders</c> route prefix
/// gives this endpoint its own dependencies, its own tests, and a diff that cannot collide with
/// concurrent work on the order lifecycle.</para>
///
/// <para><b>Bytes stream through the API; no signed URL is minted.</b> See
/// <see cref="GetSource"/> for the reasoning.</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/orders")]
public sealed class OrderSourceDocumentController : ControllerBase
{
    /// <summary>
    /// Refuse to buffer more than this. Deliberately well above every ingest cap — uploads and the
    /// pull/push ingress channels both stop at 10 MB — so it never fires for a document that
    /// arrived through a supported path, and only catches an object that should not exist.
    /// </summary>
    private const long MaxServedBytes = 32L * 1024 * 1024;

    private readonly ProcuLinkDbContext _db;
    private readonly ICurrentTenantService _tenant;
    private readonly IFileStorageService _fileStorage;
    private readonly IFormatDetector _formatDetector;
    private readonly ILogger<OrderSourceDocumentController> _logger;

    public OrderSourceDocumentController(
        ProcuLinkDbContext db,
        ICurrentTenantService tenant,
        IFileStorageService fileStorage,
        IFormatDetector formatDetector,
        ILogger<OrderSourceDocumentController> logger)
    {
        _db = db;
        _tenant = tenant;
        _fileStorage = fileStorage;
        _formatDetector = formatDetector;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/orders/{id}/source — the original uploaded document, streamed.
    ///
    /// <para><b>Streamed, not signed — the trade.</b> The sibling artifact endpoint hands out a
    /// 15-minute pre-signed R2 URL. That is the wrong shape for the source document, for four
    /// reasons, and the bandwidth is the cheaper side of the trade:</para>
    /// <list type="number">
    ///   <item>A signed URL is a bearer credential embedded in a URL. It outlives the request,
    ///         survives in history, logs and referrers, and is not organisation-checked again once
    ///         minted — anyone holding the string reads the document. The source file is the
    ///         buyer's own paperwork: counterparty names, addresses, contact names, prices. That is
    ///         a materially worse thing to hand out unauthenticated than a machine-readable output
    ///         artifact.</item>
    ///   <item>The response headers this endpoint owes the browser cannot be set on a signed URL.
    ///         R2 replays the <c>Content-Type</c> recorded at upload — the browser-supplied one,
    ///         never verified — and the presign here carries no response-header overrides, so
    ///         <c>Content-Disposition</c>, <c>Cache-Control: no-store</c> and <c>nosniff</c> are
    ///         simply not available on that path.</item>
    ///   <item><see cref="ProcuLink.Infrastructure.Storage.LocalFileStorageService"/> cannot sign
    ///         at all. Its <c>GetSignedDownloadUrlAsync</c> returns a hardcoded
    ///         <c>http://localhost:5096/api/dev/files/{key}</c> — a port the API does not even
    ///         listen on — pointing at an unauthenticated dev-only passthrough, and it ignores the
    ///         expiry. Only streaming works identically on both live storage backends.</item>
    ///   <item><c>R2StorageService.DownloadAsync</c> deliberately avoids presigning because
    ///         offline-signed URLs 403 with <c>SignatureDoesNotMatch</c> on clock-drifting
    ///         containers — an observed production failure. Streaming inherits the path that was
    ///         fixed; presigning would inherit the bug.</item>
    /// </list>
    /// <para>The cost is API egress for a file capped at 10 MB
    /// (<c>OrdersController.MaxUploadBytes</c>), fetched when a human opens one order for review,
    /// under the shared 60/minute download budget. That is a rounding error against handing out an
    /// unauthenticated URL to a customer's purchase order.</para>
    ///
    /// <para><b>Refusals.</b> 404 for an unknown order AND for another organisation's order — the
    /// two are indistinguishable to the caller by construction, because both are simply "no row".
    /// 410 for a source blob purged under the org's retention policy (a deliberate, explainable
    /// state, and the same contract the artifact download already uses). 204 when there is no
    /// document to show: an order ingested with no stored file, or a stored key whose object is
    /// gone or empty. An absent document is a normal state — a practice/sample order or an
    /// API-pushed order legitimately has none — so it is answered, not raised.</para>
    /// </summary>
    [HttpGet("{id:guid}/source")]
    [EnableRateLimiting("signed-url")]
    // No response TYPE on the 200: the body is opaque bytes and the media type is decided per
    // order from the file's own content, so naming a schema here would document a shape that does
    // not exist. `InvoiceController.Download`, the other binary download, declares none either.
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetSource(Guid id, CancellationToken ct)
    {
        RequireOrganisationScope();

        // No `.Where(o => o.OrgId == ...)`. The organisation query filter armed by
        // TenantResolutionMiddleware (#178/#183) is what scopes this read, and the assertion above
        // is what proves the filter is actually on. Re-typing the predicate here would make the
        // cross-tenant test pass whether or not the filter works, which is the one thing that test
        // exists to distinguish.
        var order = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new
            {
                o.SourceFileKey,
                o.SourceFilePurgedAt,
                // The format the parser DETECTED at ingest, if a capture row was written.
                CapturedFormat = o.SourceCapture != null ? o.SourceCapture.Format : null,
            })
            .FirstOrDefaultAsync(ct);

        if (order is null)
        {
            // Unknown id and another organisation's id land here identically. The log line names
            // only the id the caller already supplied, so the server can count these without the
            // response distinguishing them.
            _logger.LogInformation(
                "Order source document refused for {OrderId}: {Reason}", id, "not_found_in_scope");
            return NotFound();
        }

        if (order.SourceFilePurgedAt is not null)
            return StatusCode(StatusCodes.Status410Gone, new { error = RetentionConstants.BlobPurgedError });

        if (string.IsNullOrWhiteSpace(order.SourceFileKey))
            return NoDocument(id, "no_source_file_key");

        // Ask storage how big it is BEFORE fetching it. This is the only place the size check can
        // do any good on the R2 path: R2StorageService.DownloadAsync copies the whole object into
        // a MemoryStream before it returns, so by the time this action holds a stream the memory is
        // already committed and a cap on the copy below would bound nothing. TryGetSizeAsync is a
        // HEAD on R2 and a FileInfo locally, is contractually non-throwing, and returns null when
        // the backend cannot say — in which case we proceed exactly as before rather than refusing
        // a document over a number nobody produced.
        var storedBytes = await _fileStorage.TryGetSizeAsync(order.SourceFileKey, ct);
        if (storedBytes > MaxServedBytes)
        {
            // Every ingest path caps at 10 MB (OrdersController.MaxUploadBytes,
            // IngressLimits.MaxFileBytes), so this is unreachable unless a cap was bypassed or the
            // bucket was edited by hand. Error level, not Warning: it is a real defect somewhere
            // upstream, and it must not read as the ordinary "no document" it is answered with.
            _logger.LogError(
                "Order source document refused for {OrderId}: {Reason} ({Bytes} bytes exceeds the {Cap}-byte serve cap)",
                id, "storage_object_exceeds_serve_cap", storedBytes, MaxServedBytes);
            return NoContent();
        }

        byte[] bytes;
        try
        {
            await using var stream = await _fileStorage.DownloadAsync(order.SourceFileKey, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Caller went away — not a storage failure, and not ours to swallow.
        }
        catch (Exception ex)
        {
            // The row says there is a file and storage disagrees. Distinguishable in the log,
            // indistinguishable in the response — and an honest "nothing to show" beats a 500 on a
            // review screen. The storage key is NOT logged: its last segment is the uploader's own
            // filename, which is customer data.
            _logger.LogWarning(ex,
                "Order source document refused for {OrderId}: {Reason}", id, "storage_object_unreadable");
            return NoContent();
        }

        if (bytes.Length == 0)
            return NoDocument(id, "storage_object_empty");

        var media = await ResolveMediaTypeAsync(bytes, order.CapturedFormat, ct);

        // Safe to hand to a browser: an honest type from a closed non-scripting allowlist, no
        // sniffing, no shared-cache copy of one tenant's document, and inline only for types that
        // cannot execute.
        var disposition = new ContentDispositionHeaderValue(media.RenderInline ? "inline" : "attachment");
        var fileName = DownloadFileNameFrom(order.SourceFileKey);
        if (fileName is not null)
            disposition.SetHttpFileName(fileName);

        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        // Also set globally in Program.cs; repeated here so the guarantee travels with the
        // endpoint rather than depending on middleware this action never sees.
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        // Two-argument File(): the three-argument overload would overwrite Content-Disposition
        // with `attachment`, discarding the inline decision above.
        return File(bytes, media.ContentType);
    }

    /// <summary>
    /// Refuses the request unless this DbContext is armed to the calling organisation.
    ///
    /// <para>This endpoint has no hand-written organisation predicate, so the query filter is its
    /// only scoping. An unarmed context resolves the UNFILTERED model and would answer from every
    /// organisation's rows while looking, at the call site above, exactly like a scoped one. The
    /// realistic way that happens is somebody adding <c>[CrossOrganisationRead]</c> to this
    /// controller — which is a two-word diff that would silently turn a document-serving endpoint
    /// into a cross-tenant one. Failing loudly is the only acceptable behaviour, so this throws
    /// rather than returning a status the caller could mistake for a normal refusal.</para>
    /// </summary>
    private void RequireOrganisationScope()
    {
        if (_db.ScopedOrganisationId is not { } scoped || scoped != _tenant.OrganisationId)
            throw new InvalidOperationException(
                "The organisation query filters are not armed for this request, so serving the " +
                "source document would read across tenants. This endpoint must never be declared " +
                "[CrossOrganisationRead].");
    }

    /// <summary>
    /// Answers "there is no document here" — a normal state, recorded server-side with a reason so
    /// the causes stay distinguishable in logs even though the response does not distinguish them.
    /// </summary>
    private IActionResult NoDocument(Guid orderId, string reason)
    {
        _logger.LogInformation("Order source document unavailable for {OrderId}: {Reason}", orderId, reason);
        return NoContent();
    }

    /// <summary>
    /// Resolves the media type from the BYTES, falling back to the format the parser recorded at
    /// ingest, and finally to opaque octets.
    ///
    /// <para>The storage key is never consulted. Its extension is whatever the uploader named the
    /// file, and a media type derived from a name is a guess that renders as a fact — the frontend
    /// already shipped that mistake as <c>sourceTypeFromKey()</c>, which splits an order id on
    /// <c>'.'</c> and has therefore returned <c>undefined</c> since the day it was written, without
    /// ever failing. The detector is given a null filename hint precisely so it cannot fall back to
    /// an extension either.</para>
    /// </summary>
    private async Task<SourceDocumentMediaType> ResolveMediaTypeAsync(
        byte[] bytes, string? capturedFormat, CancellationToken ct)
    {
        string? detected = null;
        try
        {
            using var peek = new MemoryStream(bytes, writable: false);
            // fileName: null — sniff the content, never the name.
            detected = (await _formatDetector.DetectAsync(peek, fileName: null, ct)).Format;
        }
        catch (Exception ex)
        {
            // IFormatDetector is contractually non-throwing; a substitute implementation may not be.
            _logger.LogWarning(ex, "Content sniffing failed while serving a source document");
        }

        var fromBytes = SourceDocumentMediaType.For(detected);
        if (fromBytes != SourceDocumentMediaType.Unknown)
            return fromBytes;

        // The bytes were not recognised. source_captures.format is the next most honest answer:
        // it is what the detector concluded at ingest, on this same file, and it covers the LLM
        // PDF/email path where the capture is written from the extractor rather than from a sniff.
        return SourceDocumentMediaType.For(capturedFormat);
    }

    /// <summary>
    /// The download filename, taken from the last segment of the storage key
    /// (<c>{orgId}/{orderId}/{sanitisedFilename}</c>) and re-sanitised to a conservative ASCII set
    /// so nothing user-supplied can shape a response header. Null when nothing usable survives.
    /// </summary>
    private static string? DownloadFileNameFrom(string storageKey)
    {
        var cut = storageKey.LastIndexOfAny(['/', '\\']);
        var last = cut >= 0 ? storageKey[(cut + 1)..] : storageKey;

        var safe = new string(last
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_')
            .ToArray())
            .Trim('.', '_');

        if (safe.Length > 120)
            safe = safe[..120];

        return safe.Length == 0 ? null : safe;
    }
}
