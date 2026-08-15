using System.Text.RegularExpressions;

namespace ProcuLink.Core.Security;

/// <summary>
/// Pure, dependency-free redaction of secrets out of anything that is about to leave the process
/// as telemetry — a log line, a Sentry breadcrumb, a Sentry event message, an exception message,
/// a captured request URL.
///
/// <para><b>Why this exists (P1 telemetry-hygiene cluster, 2026-08-14 readiness audit).</b> Both
/// hosts run Sentry with <c>MinimumBreadcrumbLevel = Information</c>, which makes the log surface
/// the Sentry surface: every Information-level log line is attached to the next captured event and
/// shipped to a third party. Several outbound integrations put their entire credential in the URL
/// — a Slack, Teams, Zapier or Discord incoming-webhook URL <i>is</i> the token; there is no
/// separate secret — so logging one whole, at any level, writes a working credential into a log
/// sink and into Sentry. Both ProcuLink repos are public, so a leaked log excerpt is a leaked
/// credential.</para>
///
/// <para><b>Design.</b> Redaction, not silence. Lowering the breadcrumb level would throw away the
/// debugging signal that makes Sentry worth having and would still leak anything logged at Error.
/// This type instead removes the secret and keeps everything an operator can act on: the scheme,
/// the host, and any human-readable path. The two rules that matter:</para>
/// <list type="number">
///   <item><description><b>Known secret-bearing webhook shapes</b> (host and/or path) lose their
///   whole path — <c>https://hooks.slack.com/[redacted]</c>. The vendor is still identifiable.</description></item>
///   <item><description><b>Opaque path segments anywhere else</b> are redacted by shape: long,
///   separator-free, mixed-class or hex. GUIDs and human-readable slugs are deliberately preserved
///   because this codebase routinely puts order ids and PO numbers in URLs and losing those would
///   make the telemetry useless.</description></item>
/// </list>
///
/// <para><b>Deliberate non-goal:</b> this is not a general DLP scanner. It is a last line of defence
/// for telemetry. Code that knows it is handling a secret-bearing URL should log
/// <see cref="SafeDestination"/> (scheme + host) plus the stored config id, and never hand the raw
/// URL to a logger at all.</para>
/// </summary>
public static class TelemetryRedactor
{
    /// <summary>The marker substituted for anything removed. Stable so tests and greps can find it.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>
    /// Minimum length before a URL path segment is even considered opaque. Below this a segment
    /// cannot carry a meaningful secret and is far more likely to be a route element.
    /// </summary>
    private const int MinOpaqueSegmentLength = 24;

    /// <summary>
    /// A path segment with more than this many separators reads as a human-authored slug
    /// (<c>PO-2026-000412-acme-components</c>), not a token. Real webhook tokens are one run of
    /// alphanumerics or base64url with very few separators.
    /// </summary>
    private const int MaxSeparatorsInOpaqueSegment = 2;

    private static readonly RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Any absolute URL embedded in free text. Deliberately greedy up to whitespace/quotes/brackets;
    // trailing sentence punctuation is trimmed back by the caller before parsing.
    private static readonly Regex UrlInText =
        new(@"[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s""'<>\\^`{|}]+", Opts);

    private static readonly Regex GuidLike = new(
        @"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$", Opts);

    private static readonly Regex TokenCharsOnly = new(@"^[A-Za-z0-9._~\-]+$", Opts);
    private static readonly Regex HexOnly = new(@"^[0-9a-fA-F]+$", Opts);

    private static readonly Regex SensitiveParamName = new(
        @"^(?:token|access[_\-]?token|refresh[_\-]?token|id[_\-]?token|auth|authorization|api[_\-]?key|apikey|key|secret|client[_\-]?secret|password|passwd|pwd|sig|signature|sas|credential|x-amz-signature|x-amz-credential|x-amz-security-token)$",
        Opts | RegexOptions.IgnoreCase);

    private static readonly string[] ReadableExtensions =
    {
        ".csv", ".xml", ".json", ".pdf", ".xlsx", ".xls", ".txt", ".edi", ".zip", ".html", ".htm", ".yaml", ".yml",
    };

    /// <summary>
    /// Hosts whose incoming-webhook URL carries the entire credential in the path. Matched as a
    /// domain suffix, so <c>contoso.webhook.office.com</c> matches <c>webhook.office.com</c>.
    /// </summary>
    private static readonly string[] SecretBearingHostSuffixes =
    {
        "hooks.slack.com",
        "hooks.zapier.com",
        "webhook.office.com",
        "logic.azure.com",       // Power Automate / Logic Apps — secret is the ?sig= on a long path
        "chat.googleapis.com",   // Google Chat spaces webhook — ?key=&token=
        "hooks.nylas.com",
        "hook.integromat.com",
        "hook.eu1.make.com",
        "hook.us1.make.com",
    };

    // Free-text secret shapes, applied after URLs have been handled. Each pair is (pattern, replacement).
    private static readonly (Regex Pattern, string Replacement)[] FreeTextSecrets =
    {
        // Preserves the exact prior behaviour of the API's inbound-email ?token= scrub even when the
        // surrounding string is not a parseable URL.
        (new Regex(@"(?i)\btoken=[^&\s""']*", Opts), "token=" + Redacted),

        (new Regex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=\-]{8,}", Opts), "Bearer " + Redacted),
        (new Regex(@"\bsk_(live|test)_[A-Za-z0-9]{6,}", Opts), "sk_$1_" + Redacted),
        (new Regex(@"\bwhsec_[A-Za-z0-9]{6,}", Opts), "whsec_" + Redacted),
        (new Regex(@"\bxox[abeoprs]-[A-Za-z0-9\-]{8,}", Opts), "xox-" + Redacted),
        (new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}", Opts), Redacted),
        (new Regex(@"\bAKIA[0-9A-Z]{16}\b", Opts), Redacted),
        (new Regex(@"\beyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}", Opts), Redacted),
        (new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", Opts), Redacted),

        // name: value / name=value in prose or structured-log output.
        (new Regex(
            @"(?i)\b(api[_\-]?key|apikey|client[_\-]?secret|secret|password|passwd|pwd|access[_\-]?token|authorization)\b\s*[:=]\s*""?([^\s""',;)]{6,})""?",
            Opts), "$1=" + Redacted),
    };

    /// <summary>
    /// Redacts every secret shape this type knows about out of an arbitrary string: embedded URLs
    /// first, then free-text token shapes. Null and empty pass through unchanged. Idempotent —
    /// running it twice yields the same string.
    /// </summary>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = UrlInText.Replace(text, m =>
        {
            var raw   = m.Value;
            var trail = string.Empty;
            // Pull sentence punctuation off the end so "…/TOKEN." parses as a URL. `]` is
            // deliberately NOT in this set: it terminates the "[redacted]" marker, and trimming it
            // would make a second pass emit "[redacted]]" — i.e. break idempotence, which matters
            // because a redacted string can be re-scrubbed (breadcrumb → event).
            while (raw.Length > 0 && ".,;:!?)\"'".IndexOf(raw[^1]) >= 0)
            {
                trail = raw[^1] + trail;
                raw   = raw[..^1];
            }
            return RedactUrl(raw) + trail;
        });

        foreach (var (pattern, replacement) in FreeTextSecrets)
            result = pattern.Replace(result, replacement);

        return result;
    }

    /// <summary>
    /// Redacts one URL. Known secret-bearing webhook shapes keep only scheme + host; every other
    /// URL keeps its readable path and loses only opaque segments and sensitive query values.
    /// An unparseable input is returned unchanged (the free-text pass in <see cref="Redact"/> is
    /// what covers those).
    /// </summary>
    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var origin = OriginOf(uri);

        if (IsSecretBearingWebhook(uri))
            return origin + "/" + Redacted;

        var path = string.Join('/', uri.AbsolutePath.Split('/').Select(RedactSegment));
        return origin + path + RedactQuery(uri.Query) + RedactQuery(uri.Fragment);
    }

    /// <summary>
    /// The one form a caller may deliberately log for an outbound destination: scheme + host
    /// (+ port when non-default) and nothing else. Never carries a path, query or fragment, so it
    /// is safe for a URL that IS a credential. Pair it with the stored config id (subscription /
    /// supplier id) so an operator can look the full target up in the database.
    /// </summary>
    public static string SafeDestination(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(no destination)";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? OriginOf(uri)
            : "(unparseable destination)";
    }

    private static string OriginOf(Uri uri) =>
        uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";

    private static bool IsSecretBearingWebhook(Uri uri)
    {
        var host = uri.Host;
        var path = uri.AbsolutePath;

        foreach (var suffix in SecretBearingHostSuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Microsoft Teams connector, on any of the several hosts Microsoft has used for it.
        if (path.Contains("/IncomingWebhook/", StringComparison.OrdinalIgnoreCase)) return true;

        // Slack on a non-hooks host, and Slack-compatible relays.
        if (HostEndsWith(host, "slack.com") &&
            (path.StartsWith("/services/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/workflows/", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Discord / Guilded style: /api/webhooks/<id>/<token>
        if ((HostEndsWith(host, "discord.com") || HostEndsWith(host, "discordapp.com")) &&
            path.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Zapier catch hooks, including custom domains.
        if (path.StartsWith("/hooks/catch/", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static bool HostEndsWith(string host, string suffix) =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Redacts a single path segment if — and only if — it looks like an opaque token rather than
    /// a route element. Preserving GUIDs and slugs is intentional: order ids and PO numbers in
    /// URLs are the whole reason the telemetry is useful.
    /// </summary>
    private static string RedactSegment(string segment)
    {
        if (segment.Length < MinOpaqueSegmentLength) return segment;
        if (GuidLike.IsMatch(segment)) return segment;
        if (!TokenCharsOnly.IsMatch(segment)) return segment;

        foreach (var ext in ReadableExtensions)
            if (segment.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return segment;

        var separators = segment.Count(c => c is '-' or '_' or '.');
        if (separators > MaxSeparatorsInOpaqueSegment) return segment;

        var classes = 0;
        if (segment.Any(char.IsLower)) classes++;
        if (segment.Any(char.IsUpper)) classes++;
        if (segment.Any(char.IsDigit)) classes++;

        if (classes >= 3) return Redacted;                                    // mixed-class opaque token
        if (segment.Length >= 32 && HexOnly.IsMatch(segment)) return Redacted; // hex digest
        if (segment.Length >= 32 && classes >= 2) return Redacted;             // long base64url-ish

        return segment;
    }

    /// <summary>
    /// Redacts the values of sensitively-named parameters in a <c>?a=b&amp;c=d</c> (or <c>#…</c>)
    /// string, preserving the leading delimiter, the parameter names and the ordering so the URL
    /// stays diagnosable.
    /// </summary>
    private static string RedactQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) return query;

        var lead = query[0] is '?' or '#' ? query[..1] : string.Empty;
        var body = query[lead.Length..];
        if (body.Length == 0) return query;

        var parts = body.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            var name = parts[i][..eq];
            if (SensitiveParamName.IsMatch(name))
                parts[i] = name + "=" + Redacted;
        }

        return lead + string.Join('&', parts);
    }
}
