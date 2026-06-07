using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProcuLink.Infrastructure.Services.Security;

/// <summary>
/// Guards outbound HTTP requests against SSRF attacks.
///
/// Tenants configure delivery endpoints and webhook URLs. This service validates
/// a URL before any outbound request is made, rejecting targets that could reach
/// internal infrastructure: loopback addresses, private RFC-1918 ranges,
/// link-local addresses (169.254.0.0/16 — cloud metadata endpoint), and
/// unspecified addresses.
///
/// Config override: <c>Delivery:AllowPrivateNetworkTargets</c> (bool, default false).
/// Set to <c>true</c> in development to allow localhost test endpoints. The scheme
/// check is always enforced regardless of this flag.
/// </summary>
public sealed class OutboundRequestGuard
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<OutboundRequestGuard> _logger;

    public OutboundRequestGuard(IConfiguration configuration, ILogger<OutboundRequestGuard> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// When <c>true</c> (config <c>Delivery:AllowPrivateNetworkTargets</c>), all
    /// network-range validation is skipped so local/private test endpoints work.
    /// The connect-time callback honours this flag too.
    /// </summary>
    public bool AllowPrivateNetworkTargets =>
        _configuration.GetValue<bool>("Delivery:AllowPrivateNetworkTargets", false);

    /// <summary>
    /// Validates the given URL for use as an outbound delivery or webhook target.
    /// Returns <c>Allowed = false</c> with a reason when the URL is blocked.
    /// DNS resolution is performed; if resolution fails the URL is rejected.
    /// </summary>
    public async Task<GuardResult> ValidateAsync(string url, CancellationToken ct)
    {
        // ── 1. Parse and scheme check (always enforced) ──────────────────────
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return GuardResult.Block("URL is not a valid absolute URI.");

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return GuardResult.Block(
                $"URL scheme '{uri.Scheme}' is not allowed. Only http and https are permitted.");
        }

        // ── 2. Config override ────────────────────────────────────────────────
        var allowPrivate = _configuration.GetValue<bool>("Delivery:AllowPrivateNetworkTargets", false);

        if (allowPrivate)
        {
            // Scheme check already passed; skip network-range validation.
            return GuardResult.Allow();
        }

        // ── 3. Reject literal "localhost" hostname ────────────────────────────
        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return GuardResult.Block("Requests to 'localhost' are not permitted.");

        // ── 4. DNS resolution + IP-range check ────────────────────────────────
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(
                "SSRF guard: DNS resolution failed for host '{Host}' in URL '{Url}': {Message}",
                host, url, ex.Message);
            return GuardResult.Block($"DNS resolution failed for host '{host}': {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "SSRF guard: unexpected error resolving host '{Host}': {Message}", host, ex.Message);
            return GuardResult.Block($"Could not resolve host '{host}'.");
        }

        if (addresses.Length == 0)
            return GuardResult.Block($"Host '{host}' resolved to no addresses.");

        foreach (var ip in addresses)
        {
            if (IsBlockedAddress(ip))
            {
                _logger.LogWarning(
                    "SSRF guard blocked request to '{Url}': resolved IP {IP} is in a forbidden range.",
                    url, ip);
                return GuardResult.Block(
                    $"Requests to internal/private addresses are not permitted (resolved {ip}).");
            }
        }

        return GuardResult.Allow();
    }

    /// <summary>
    /// Validates a raw hostname and port for use as an outbound SMTP/SFTP/FTPS target.
    /// Uses the same DNS resolution and IP-range logic as <see cref="ValidateAsync"/>.
    /// Returns <c>Allowed = false</c> with a reason when the host is blocked.
    /// </summary>
    public async Task<GuardResult> ValidateHostAsync(string host, int port, CancellationToken ct)
    {
        var allowPrivate = _configuration.GetValue<bool>("Delivery:AllowPrivateNetworkTargets", false);
        if (allowPrivate)
            return GuardResult.Allow();

        if (string.IsNullOrWhiteSpace(host))
            return GuardResult.Block("Host is required.");

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return GuardResult.Block("Connections to 'localhost' are not permitted.");

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(
                "SSRF guard: DNS resolution failed for host '{Host}:{Port}': {Message}",
                host, port, ex.Message);
            return GuardResult.Block($"DNS resolution failed for host '{host}': {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "SSRF guard: unexpected error resolving host '{Host}': {Message}", host, ex.Message);
            return GuardResult.Block($"Could not resolve host '{host}'.");
        }

        if (addresses.Length == 0)
            return GuardResult.Block($"Host '{host}' resolved to no addresses.");

        foreach (var ip in addresses)
        {
            if (IsBlockedAddress(ip))
            {
                _logger.LogWarning(
                    "SSRF guard blocked connection to '{Host}:{Port}': resolved IP {IP} is in a forbidden range.",
                    host, port, ip);
                return GuardResult.Block(
                    $"Connections to internal/private addresses are not permitted (resolved {ip}).");
            }
        }

        return GuardResult.Allow();
    }

    // ── Connect-time re-validation (DNS-rebinding TOCTOU defence) ──────────────

    /// <summary>
    /// Builds a <see cref="SocketsHttpHandler"/> whose <c>ConnectCallback</c> re-resolves
    /// the target host and RE-VALIDATES the resolved IP AT CONNECT TIME, then opens the TCP
    /// connection to that exact validated IP. This closes the DNS-rebinding TOCTOU window:
    /// <see cref="ValidateAsync"/> resolves+validates DNS up front, but a malicious DNS server
    /// can return a public IP at validation time and a private/metadata IP a moment later when
    /// the underlying handler re-resolves to connect. By validating the IP we are actually about
    /// to connect to — and connecting to that pinned IP rather than re-resolving the name a third
    /// time — a public-at-validation / private-at-connect rebind is blocked.
    ///
    /// The original request URI (and therefore the HTTP <c>Host</c> header and the TLS SNI /
    /// certificate hostname) is preserved: only the socket's destination IP is pinned. TLS still
    /// negotiates against the hostname, so certificate validation is unaffected.
    ///
    /// When <see cref="AllowPrivateNetworkTargets"/> is set the callback connects normally
    /// (dev/test escape hatch), mirroring <see cref="ValidateAsync"/>.
    /// </summary>
    public SocketsHttpHandler CreateGuardedHttpHandler()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = GuardedConnectAsync,
        };
        return handler;
    }

    /// <summary>
    /// <c>ConnectCallback</c> implementation: re-resolve the host, reject if every resolved
    /// address is in a forbidden range, otherwise connect to the first allowed address.
    /// Delegates to <see cref="GuardedConnectCoreAsync"/> so the validation logic can be
    /// unit-tested without constructing the framework-internal connection context.
    /// </summary>
    public ValueTask<Stream> GuardedConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
        => GuardedConnectCoreAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);

    /// <summary>
    /// Re-resolves <paramref name="host"/> and validates the resolved IPs at connect time, then
    /// opens a TCP connection to the first allowed address (pinning the validated IP so the socket
    /// never re-resolves the name a third time). Throws <see cref="HttpRequestException"/> when the
    /// host resolves only to forbidden ranges — this is what blocks a DNS-rebind that flipped from
    /// public (at <see cref="ValidateAsync"/> time) to private/metadata (at connect time).
    /// <c>internal</c> so it is unit-testable via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal async ValueTask<Stream> GuardedConnectCoreAsync(string host, int port, CancellationToken ct)
    {
        // Dev/test escape hatch — skip range validation but still connect by hostname.
        if (AllowPrivateNetworkTargets)
            return await OpenSocketAsync(host, port, ct);

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(
                "SSRF guard: connect-time DNS resolution failed for host '{Host}:{Port}': {Message}",
                host, port, ex.Message);
            throw new HttpRequestException(
                $"SSRF guard: DNS resolution failed for host '{host}'.", ex);
        }

        if (addresses.Length == 0)
            throw new HttpRequestException($"SSRF guard: host '{host}' resolved to no addresses.");

        // Connect to the FIRST allowed address. Pinning the IP (rather than letting the socket
        // re-resolve the name) guarantees we connect to an IP we have actually validated.
        IPAddress? allowed = null;
        foreach (var ip in addresses)
        {
            if (IsBlockedAddress(ip))
            {
                _logger.LogWarning(
                    "SSRF guard blocked connect to '{Host}:{Port}': resolved IP {IP} is in a forbidden range.",
                    host, port, ip);
                continue;
            }
            allowed = ip;
            break;
        }

        if (allowed is null)
            throw new HttpRequestException(
                $"SSRF guard: connection to '{host}' blocked — resolved to internal/private addresses only.");

        return await OpenSocketAsync(allowed, port, ct);
    }

    private static async ValueTask<Stream> OpenSocketAsync(IPAddress ip, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async ValueTask<Stream> OpenSocketAsync(string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // ── IP classification ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="ip"/> should be blocked:
    /// loopback, private RFC-1918, CGNAT/shared 100.64.0.0/10 (RFC 6598),
    /// benchmarking 198.18.0.0/15 (RFC 2544), link-local (169.254/16 — cloud
    /// metadata), fc00::/7 (IPv6 unique local), fe80::/10 (IPv6 link-local), and
    /// unspecified (0.0.0.0 / ::). Unmaps IPv4-mapped IPv6 addresses before
    /// checking so ::ffff:127.0.0.1 is correctly identified as loopback.
    ///
    /// Public so it can be unit-tested directly without DNS.
    /// </summary>
    public static bool IsBlockedAddress(IPAddress ip)
    {
        // Unmap IPv4-in-IPv6 (::ffff:a.b.c.d) to its native IPv4 form.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return IsBlockedIpv4(ip);

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return IsBlockedIpv6(ip);

        // Reject any other address family (e.g. Unix domain).
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsBlockedIpv4(IPAddress ip)
    {
        // GetAddressBytes() always returns 4 bytes for an IPv4 address.
        var b = ip.GetAddressBytes();
        var a0 = b[0];
        var a1 = b[1];
        var a2 = b[2];

        // Loopback: 127.0.0.0/8
        if (a0 == 127) return true;

        // Private: 10.0.0.0/8
        if (a0 == 10) return true;

        // Private: 172.16.0.0/12  (172.16.0.0 – 172.31.255.255)
        if (a0 == 172 && a1 >= 16 && a1 <= 31) return true;

        // Private: 192.168.0.0/16
        if (a0 == 192 && a1 == 168) return true;

        // CGNAT / shared address space (RFC 6598): 100.64.0.0/10
        // (100.64.0.0 – 100.127.255.255). Carrier-grade NAT range — reachable
        // internal infrastructure on some hosts; block like RFC-1918.
        if (a0 == 100 && a1 >= 64 && a1 <= 127) return true;

        // Benchmarking (RFC 2544): 198.18.0.0/15  (198.18.0.0 – 198.19.255.255)
        if (a0 == 198 && (a1 == 18 || a1 == 19)) return true;

        // Link-local (includes cloud metadata 169.254.169.254): 169.254.0.0/16
        if (a0 == 169 && a1 == 254) return true;

        // Unspecified: 0.0.0.0/8
        if (a0 == 0) return true;

        // Broadcast: 255.255.255.255
        if (a0 == 255 && a1 == 255 && a2 == 255) return true;

        return false;
    }

    private static bool IsBlockedIpv6(IPAddress ip)
    {
        // Loopback: ::1
        if (ip.Equals(IPAddress.IPv6Loopback)) return true;

        // Unspecified: ::
        if (ip.Equals(IPAddress.IPv6None)) return true;

        var b = ip.GetAddressBytes(); // 16 bytes, big-endian

        // Link-local: fe80::/10 — first byte 0xfe, second byte high 2 bits 0b10 → 0x80..0xbf
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;

        // Unique local: fc00::/7 — first byte 0xfc or 0xfd
        if (b[0] == 0xfc || b[0] == 0xfd) return true;

        return false;
    }
}

/// <summary>Result returned by <see cref="OutboundRequestGuard.ValidateAsync"/>.</summary>
public readonly record struct GuardResult(bool Allowed, string? Reason)
{
    public static GuardResult Allow() => new(true, null);
    public static GuardResult Block(string reason) => new(false, reason);
}
