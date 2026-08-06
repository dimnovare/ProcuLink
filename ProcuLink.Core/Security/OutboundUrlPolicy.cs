using System.Net;

namespace ProcuLink.Core.Security;

/// <summary>
/// Transport-security policy for every tenant-supplied outbound URL: supplier delivery endpoints,
/// ERP connector endpoints (Erply, Directo), catalog pull feeds, OAuth token endpoints, webhook
/// targets, and S3-compatible ingress endpoints.
///
/// <para><strong>Why this exists.</strong> A delivery request carries the purchase order AND the
/// credentials used to authenticate it — Directo, for example, posts <c>user</c>, <c>password</c>
/// and <c>key</c> as form fields. Over plain <c>http://</c> both are readable by anyone on the
/// network path, while <c>/security</c> tells customers their data moves under "TLS 1.2+ in
/// transit". This policy is what makes that claim true for the outbound leg.</para>
///
/// <para><strong>This is NOT the SSRF guard.</strong>
/// <see cref="T:ProcuLink.Infrastructure.Services.Security.OutboundRequestGuard"/> answers a
/// different question — "may we connect to this host at all?" — by rejecting non-web schemes and
/// internal/private address ranges. The two are deliberately separate: an SSRF verdict is about
/// the destination, a TLS verdict is about the wire. Both run; neither replaces the other.</para>
///
/// <para><strong>Loopback.</strong> Plain <c>http://</c> stays allowed on loopback
/// (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>[::1]</c>, the reserved <c>.localhost</c> suffix)
/// because that traffic never leaves the machine, and the local dev loop plus the integration and
/// e2e suites drive delivery against loopback listeners. Private/RFC-1918 and link-local ranges
/// get NO such exemption: the SSRF guard already blocks them outright in production, so refusing
/// them here removes nothing that works today, and "it is a trusted internal network" is not
/// something ProcuLink can verify from a URL string.</para>
///
/// <para>The frontend mirrors <see cref="SecureSchemes"/> / <see cref="LoopbackOnlySchemes"/> and
/// the error codes in <c>src/lib/outboundUrlPolicy.ts</c>. This type is the source of truth; that
/// file is the copy, and a drift test pins them together.</para>
/// </summary>
public static class OutboundUrlPolicy
{
    // ── Error codes (machine-readable; mirrored by the frontend) ─────────────

    /// <summary>No URL was supplied.</summary>
    public const string ErrorUrlRequired = "url_required";

    /// <summary>The value is not parseable as an absolute URL.</summary>
    public const string ErrorNotAbsolute = "url_not_absolute";

    /// <summary>The scheme is neither http nor https (e.g. <c>file:</c>, <c>ftp:</c>).</summary>
    public const string ErrorSchemeNotAllowed = "url_scheme_not_allowed";

    /// <summary>Username/password embedded in the URL's userinfo component.</summary>
    public const string ErrorCredentialsInUrl = "credentials_in_url_not_allowed";

    /// <summary>Plain http to a host that is not loopback.</summary>
    public const string ErrorInsecureTransport = "url_requires_tls";

    // ── The scheme ladder — declared once ────────────────────────────────────

    /// <summary>Schemes that carry transport security and are allowed to any host.</summary>
    public static readonly IReadOnlyList<string> SecureSchemes = new[] { "https" };

    /// <summary>Schemes with no transport security, allowed only against loopback.</summary>
    public static readonly IReadOnlyList<string> LoopbackOnlySchemes = new[] { "http" };

    /// <summary>
    /// Validates a tenant-supplied outbound URL. <paramref name="subject"/> is the noun the caller
    /// wants the operator to read ("Delivery endpoint", "Catalog feed URL", "Webhook target URL"),
    /// so one shared policy still produces a message that fits the screen it appears on.
    /// </summary>
    public static OutboundUrlVerdict Inspect(string? url, string subject = "URL")
    {
        if (string.IsNullOrWhiteSpace(url))
            return OutboundUrlVerdict.Block(
                ErrorUrlRequired, $"{subject} is required.");

        var trimmed = url.Trim();

        // Require an explicit RFC 3986 scheme BEFORE parsing, because Uri.TryCreate is not
        // platform-agnostic here: on Linux "/orders" parses as an ABSOLUTE file: URI (it is a
        // valid Unix path), while on Windows it does not. Without this the same input is
        // "not absolute" on a developer's machine and "scheme not allowed" in CI — refused
        // either way, but a security primitive should not have a platform-dependent verdict,
        // and a path silently becoming file: is exactly what a URL validator must not do.
        // It also makes this agree with the frontend mirror, where `new URL()` demands a scheme.
        if (!HasExplicitScheme(trimmed) || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return OutboundUrlVerdict.Block(
                ErrorNotAbsolute,
                $"{subject} must be a complete address including the scheme, " +
                "for example https://orders.supplier.example/inbound.");

        var scheme = uri.Scheme.ToLowerInvariant();
        var isSecure = Contains(SecureSchemes, scheme);
        var isLoopbackOnly = Contains(LoopbackOnlySchemes, scheme);

        if (!isSecure && !isLoopbackOnly)
            return OutboundUrlVerdict.Block(
                ErrorSchemeNotAllowed,
                $"{subject} uses the '{scheme}' scheme, which is not supported. Use https://.");

        // Checked before the TLS verdict: a userinfo secret is a leak on https too, and the
        // message must never quote the URL back — that would copy the very password we are
        // refusing into the UI, the logs, and Sentry.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return OutboundUrlVerdict.Block(
                ErrorCredentialsInUrl,
                $"{subject} must not contain a username or password before the host. " +
                "Credentials written into a URL are stored and displayed in clear text and are " +
                "copied into every delivery record. Remove them from the address and enter them " +
                "in the credential fields instead.");

        if (isLoopbackOnly && !IsLoopback(uri))
            return OutboundUrlVerdict.Block(
                ErrorInsecureTransport,
                $"{subject} must use https://. ProcuLink will not send purchase orders or " +
                "supplier credentials over plain http, where anyone on the network path can " +
                "read them. Ask the supplier for their https endpoint. Plain http is accepted " +
                "only against localhost, for local development.");

        return OutboundUrlVerdict.Allow();
    }

    /// <summary>
    /// True when the URL is one this policy would refuse today. Used to flag configs that were
    /// saved before enforcement existed, so an operator is warned rather than silently exposed.
    /// </summary>
    public static bool IsRefused(string? url) => !Inspect(url).Allowed;

    /// <summary>
    /// Loopback per RFC 6761 plus the standard address ranges: <c>localhost</c> and any
    /// <c>*.localhost</c> name, <c>127.0.0.0/8</c>, and <c>::1</c>. Uses the framework's own
    /// determination rather than a hand-maintained list of literals.
    /// </summary>
    public static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback) return true;

        // Not redundant with Uri.IsLoopback: on .NET 8 a host written in a different case
        // ("http://LOCALHOST:5223") is not normalised before the loopback flag is computed, so
        // IsLoopback returns false for it. Relying on the flag alone made a plain-cased local
        // dev URL work and an upper-cased one refuse — decided by runtime version, not by policy.
        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;

        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// RFC 3986 <c>scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )</c> followed by ":".
    ///
    /// <para>At least TWO characters, deliberately: a one-letter "scheme" is a Windows drive
    /// letter (<c>C:\orders\out.xml</c>), which .NET happily parses into an absolute <c>file:</c>
    /// URI. No real scheme is one character, so requiring two keeps a local path from being
    /// mistaken for a URL — the same class of confusion as the Unix path case above.</para>
    /// </summary>
    private static bool HasExplicitScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon < 2) return false;
        if (!char.IsAsciiLetter(value[0])) return false;

        for (var i = 1; i < colon; i++)
        {
            var c = value[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.') return false;
        }

        return true;
    }

    private static bool Contains(IReadOnlyList<string> schemes, string scheme)
    {
        for (var i = 0; i < schemes.Count; i++)
            if (string.Equals(schemes[i], scheme, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

/// <summary>Outcome of <see cref="OutboundUrlPolicy.Inspect"/>.</summary>
public readonly record struct OutboundUrlVerdict(bool Allowed, string? ErrorCode, string? Message)
{
    public static OutboundUrlVerdict Allow() => new(true, null, null);

    public static OutboundUrlVerdict Block(string errorCode, string message) =>
        new(false, errorCode, message);
}

/// <summary>
/// Thrown by service-layer write paths when a tenant-supplied URL fails
/// <see cref="OutboundUrlPolicy"/>. Derives from <see cref="ArgumentException"/> so existing
/// controller handlers keep mapping it to 400, while <see cref="ErrorCode"/> lets a handler that
/// cares emit the machine-readable code alongside the operator-facing message.
/// </summary>
public sealed class OutboundUrlPolicyException : ArgumentException
{
    public string ErrorCode { get; }

    /// <summary>
    /// The policy's verdict message on its own. <see cref="Exception.Message"/> is the same text with
    /// ArgumentException's <c>(Parameter '…')</c> suffix appended, which is right for a log and wrong
    /// for a response body an operator reads — so a handler returning this to the UI uses this
    /// property and does not have to strip anything.
    /// </summary>
    public string PolicyMessage { get; }

    public OutboundUrlPolicyException(OutboundUrlVerdict verdict, string? paramName = null)
        : base(verdict.Message, paramName)
    {
        ErrorCode = verdict.ErrorCode ?? OutboundUrlPolicy.ErrorNotAbsolute;
        PolicyMessage = verdict.Message ?? string.Empty;
    }
}
