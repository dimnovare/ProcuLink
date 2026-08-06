using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProcuLink.Infrastructure.Tests.Services.Security;

/// <summary>
/// A minimal in-process HTTP/1.1 origin server built on a raw <see cref="TcpListener"/>.
///
/// <para>
/// It exists so a test can assert on <b>what actually crossed the wire</b> rather than on a
/// handler property. Two of these stood up side by side — one answering <c>307</c> with a
/// <c>Location</c> pointing at the other — is the only way to prove whether the runtime replays a
/// request body and its headers to a redirect target. A mocked <c>HttpMessageHandler</c> cannot
/// prove that: the redirect logic under test lives <i>inside</i> the handler that a mock replaces.
/// </para>
///
/// <para>
/// Raw TCP rather than <c>HttpListener</c> because <c>HttpListener</c> needs a URL ACL reservation
/// on Windows, and because these tests must be able to send deliberately hostile responses (a body
/// with no <c>Content-Length</c> that never ends, or a <c>Content-Length</c> that lies).
/// </para>
///
/// <para>Request parsing supports <c>Content-Length</c> bodies only — every outbound request
/// ProcuLink makes uses <c>ByteArrayContent</c>, <c>FormUrlEncodedContent</c> or
/// <c>StringContent</c>, all of which set <c>Content-Length</c> rather than chunking.</para>
/// </summary>
internal sealed class StubHttpServer : IDisposable
{
    /// <summary>Writes the raw response bytes for one received request.</summary>
    internal delegate Task Responder(ReceivedRequest request, Stream output, CancellationToken ct);

    private readonly TcpListener _listener;
    private readonly Responder _respond;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ReceivedRequest> _received = new();
    private readonly object _lock = new();

    private StubHttpServer(Responder respond)
    {
        _respond = respond;
        // Dual-mode so the server answers on BOTH ::1 and 127.0.0.1. The redirect tests deliberately
        // address the same socket by two different host strings (127.0.0.1 and "localhost"), and a
        // host where "localhost" resolves to ::1 first must still reach it. Falls back to IPv4-only
        // where IPv6 is unavailable.
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, 0);
            _listener.Server.DualMode = true;
            _listener.Start();
        }
        catch (SocketException)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Base URL using the literal loopback IP (<c>http://127.0.0.1:{port}</c>).</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// Base URL using the <c>localhost</c> name. Same socket, but a DIFFERENT host string — which
    /// is what makes a redirect between the two a genuinely cross-host redirect as far as the
    /// runtime's own "is this the same host?" check is concerned.
    /// </summary>
    public string LocalhostUrl => $"http://localhost:{Port}";

    /// <summary>Every request this server has fully received, in arrival order.</summary>
    public IReadOnlyList<ReceivedRequest> Received
    {
        get { lock (_lock) return _received.ToList(); }
    }

    public static StubHttpServer Start(Responder respond) => new(respond);

    // ── Canned responders ─────────────────────────────────────────────────────

    /// <summary>Answers every request with a redirect to <paramref name="location"/>.</summary>
    public static Responder Redirect(int status, string location) =>
        (_, output, ct) => WriteAsync(output,
            $"HTTP/1.1 {status} Redirect\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            ct);

    /// <summary>Answers every request with a normal <c>200</c> and the given body.</summary>
    public static Responder Ok(string body = "OK", string contentType = "text/plain") =>
        (_, output, ct) =>
        {
            var payload = Encoding.UTF8.GetBytes(body);
            return WriteAsync(output,
                $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n",
                payload, ct);
        };

    /// <summary>
    /// Answers with <c>200</c> and then streams <paramref name="totalBytes"/> of filler with NO
    /// <c>Content-Length</c> (connection-delimited). This is the shape of the hostile response an
    /// unbounded <c>ReadAsStringAsync</c> would buffer in full.
    /// </summary>
    public static Responder Flood(long totalBytes) =>
        async (_, output, ct) =>
        {
            await WriteAsync(output,
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n", ct);

            var chunk = new byte[64 * 1024];
            chunk.AsSpan().Fill((byte)'A');
            long sent = 0;
            while (sent < totalBytes && !ct.IsCancellationRequested)
            {
                var want = (int)Math.Min(chunk.Length, totalBytes - sent);
                try { await output.WriteAsync(chunk.AsMemory(0, want), ct); }
                catch (IOException) { return; }      // client hung up once the cap tripped — expected
                catch (ObjectDisposedException) { return; }
                sent += want;
            }
        };

    /// <summary>
    /// Answers with <c>200</c> and a <c>Content-Length</c> that DECLARES
    /// <paramref name="declaredBytes"/>, then sends far less. Proves the cap can refuse on the
    /// declared length alone, before a single body byte is buffered.
    /// </summary>
    public static Responder DeclaresOversize(long declaredBytes) =>
        async (_, output, ct) =>
        {
            await WriteAsync(output,
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {declaredBytes}\r\nConnection: close\r\n\r\n",
                ct);
            try { await WriteAsync(output, "A", ct); } catch (IOException) { }
        };

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _cts.Token);
                if (request is null) return;

                lock (_lock) _received.Add(request);

                await _respond(request, stream, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // A client that hangs up mid-response (exactly what the size cap makes it do) is
            // normal here; it must not take the listener down or fail the test run.
        }
    }

    private static async Task<ReceivedRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var head = new List<byte>(1024);
        var one = new byte[1];

        // Read until the blank line that terminates the request head.
        while (!EndsWithHeadTerminator(head))
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (read == 0) return null;
            head.Add(one[0]);
            if (head.Count > 64 * 1024) return null;   // runaway head — not something we test
        }

        var text = Encoding.ASCII.GetString(head.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrEmpty(line)) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var lenText) &&
            long.TryParse(lenText, out var length) && length > 0)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, (int)(length - offset)), ct);
                if (read == 0) break;
                offset += read;
            }
            body = buffer[..offset];
        }

        return new ReceivedRequest(requestLine[0], requestLine[1], headers, body);
    }

    private static bool EndsWithHeadTerminator(List<byte> head) =>
        head.Count >= 4 &&
        head[^4] == (byte)'\r' && head[^3] == (byte)'\n' &&
        head[^2] == (byte)'\r' && head[^1] == (byte)'\n';

    private static Task WriteAsync(Stream output, string text, CancellationToken ct) =>
        output.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    private static async Task WriteAsync(Stream output, string head, byte[] payload, CancellationToken ct)
    {
        await WriteAsync(output, head, ct);
        await output.WriteAsync(payload, ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}

/// <summary>One fully received request, captured for assertion.</summary>
internal sealed record ReceivedRequest(
    string Method,
    string Target,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
}
