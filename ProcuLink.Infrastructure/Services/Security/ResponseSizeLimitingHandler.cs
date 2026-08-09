using System.Net.Http;

namespace ProcuLink.Infrastructure.Services.Security;

/// <summary>
/// Size limits for OUTBOUND HTTP responses on the guarded transport
/// (<see cref="OutboundRequestGuard.CreateGuardedHttpHandler"/>).
///
/// <para>
/// Nothing bounded these before: <c>MaxResponseContentBufferSize</c> was set nowhere in the
/// solution, so every <c>ReadAsStringAsync</c> fell back to the framework's 2 GB default. The
/// 8,000-character bound on <c>DeliveryAttempt.MaxResponseBodyLength</c> applies at PERSIST time —
/// by then the whole body is already in memory. A supplier streaming a multi-gigabyte response
/// therefore OOMs a worker running ten concurrent delivery jobs.
/// </para>
///
/// <para>
/// The default is deliberately generous rather than tight, because it must not break the one
/// caller that cannot ask for more: the AWS SDK builds its own requests through the guarded
/// transport, so it can never set
/// <see cref="ResponseSizeLimitingHandler.MaxResponseBytesOption"/>. The largest body it
/// legitimately receives is an S3 ingress object, capped at
/// <c>IngressLimits.MaxFileBytes</c> (10 MB). Everything else on the transport is far smaller —
/// delivery and ERP acknowledgements are a few hundred bytes to a few KB, an OAuth token response
/// is a small JSON document, and a vendor catalog page is a couple of MB.
/// </para>
///
/// <para>
/// The one genuine exception is the catalog file download, which is legitimately up to
/// <c>CatalogLimits.MaxCatalogFileBytes</c> (256 MB). That call opts up explicitly, per request.
/// A call site that forgets gets the tight default — the failure mode is a refused oversize
/// response, never an unbounded one.
/// </para>
/// </summary>
public static class OutboundResponseLimits
{
    /// <summary>
    /// Default ceiling for a single outbound response body on the guarded transport, in bytes
    /// (16 MiB). Chosen as the 10 MB S3-object ceiling plus headroom for HTTP framing and for a
    /// paginated listing response. Ten concurrent worker jobs can therefore buffer at most
    /// ~160 MB rather than an unbounded amount.
    /// </summary>
    public const long DefaultMaxResponseBytes = 16L * 1024 * 1024;
}

/// <summary>
/// Thrown when an outbound response exceeds the cap for its request. Derives from
/// <see cref="HttpRequestException"/> so the existing "failed before a response" catch clauses on
/// every channel turn it into a clean, enumerated delivery failure rather than an unhandled crash.
/// </summary>
public sealed class OutboundResponseTooLargeException : HttpRequestException
{
    public OutboundResponseTooLargeException(long maxBytes, long? declaredBytes)
        : base(declaredBytes is { } declared
            ? $"Outbound response declared {declared} bytes, which exceeds the {maxBytes}-byte limit."
            : $"Outbound response exceeded the {maxBytes}-byte limit.")
    {
        MaxBytes = maxBytes;
        DeclaredBytes = declaredBytes;
    }

    public long MaxBytes { get; }

    /// <summary>The <c>Content-Length</c> the server declared, when it declared one.</summary>
    public long? DeclaredBytes { get; }
}

/// <summary>
/// Caps how many response bytes any one guarded outbound call may buffer.
///
/// <para>
/// It sits ABOVE the guarded <see cref="System.Net.Http.SocketsHttpHandler"/> and below
/// <see cref="HttpClient"/>, which is the only layer that works for both completion options:
/// <c>HttpClient</c> buffers the body (for the default <c>ResponseContentRead</c>) AFTER the
/// handler pipeline returns, so the stream this handler substitutes is the stream that gets
/// buffered. A <c>ResponseHeadersRead</c> caller reads through the same limiter.
/// </para>
///
/// <para>Two independent checks, because a server can lie either way:</para>
/// <list type="number">
///   <item>A declared <c>Content-Length</c> over the cap is refused before a single body byte is
///     read.</item>
///   <item>The body stream itself is counted, so a chunked or connection-delimited response with
///     no declared length — or one that declares a small length and then sends forever — trips the
///     cap mid-read and the connection is torn down.</item>
/// </list>
/// </summary>
internal sealed class ResponseSizeLimitingHandler : DelegatingHandler
{
    /// <summary>
    /// Per-request opt-up. Set this on <see cref="HttpRequestMessage.Options"/> for a call that
    /// legitimately receives more than <see cref="OutboundResponseLimits.DefaultMaxResponseBytes"/>
    /// (today: the catalog file download only). Absent, the default applies.
    /// </summary>
    public static readonly HttpRequestOptionsKey<long> MaxResponseBytesOption =
        new("ProcuLink.Outbound.MaxResponseBytes");

    private readonly long _defaultMaxBytes;

    public ResponseSizeLimitingHandler(HttpMessageHandler innerHandler, long defaultMaxBytes)
        : base(innerHandler)
    {
        if (defaultMaxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(defaultMaxBytes), "The response cap must be positive.");
        _defaultMaxBytes = defaultMaxBytes;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var maxBytes = request.Options.TryGetValue(MaxResponseBytesOption, out var requested) && requested > 0
            ? requested
            : _defaultMaxBytes;

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // (1) A declared oversize length is refused without reading the body at all.
        var declared = response.Content.Headers.ContentLength;
        if (declared is { } declaredLength && declaredLength > maxBytes)
        {
            response.Dispose();
            throw new OutboundResponseTooLargeException(maxBytes, declaredLength);
        }

        // (2) Count the bytes that actually arrive. Note this runs AFTER any automatic
        // decompression the inner handler performs, so a compressed bomb is measured by what it
        // expands to, not by what it claimed on the wire.
        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var limited = new LengthLimitingStream(source, maxBytes);
        var replacement = new StreamContent(limited);
        foreach (var header in response.Content.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = replacement;

        return response;
    }

    /// <summary>
    /// Read-only pass-through that throws <see cref="OutboundResponseTooLargeException"/> the
    /// moment cumulative reads pass the cap. At most cap+buffer bytes are ever materialised.
    /// </summary>
    private sealed class LengthLimitingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _totalRead;

        public LengthLimitingStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        private int Count(int read)
        {
            if (read <= 0) return read;
            _totalRead += read;
            if (_totalRead > _maxBytes)
                throw new OutboundResponseTooLargeException(_maxBytes, null);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(_inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _totalRead;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
