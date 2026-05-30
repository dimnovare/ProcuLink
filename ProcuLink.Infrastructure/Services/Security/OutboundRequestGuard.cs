using System.Net;
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

    // ── IP classification ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="ip"/> should be blocked:
    /// loopback, private RFC-1918, link-local (169.254/16 — cloud metadata),
    /// fc00::/7 (IPv6 unique local), fe80::/10 (IPv6 link-local), and
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
