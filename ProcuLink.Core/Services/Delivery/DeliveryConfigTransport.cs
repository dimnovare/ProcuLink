using System.Text.Json;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Security;

namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Reads the outbound endpoint out of a supplier delivery config blob and reports whether it
/// still satisfies <see cref="OutboundUrlPolicy"/>.
///
/// <para>Shared on purpose. The save path uses it to refuse a new cleartext endpoint; the read
/// path uses it to flag one that was saved before enforcement existed; the dispatchers use it to
/// log a warning every time such a config actually sends. One extraction, so the three cannot
/// disagree about which string is the endpoint.</para>
/// </summary>
public static class DeliveryConfigTransport
{
    private static readonly HashSet<string> UrlBasedProtocols =
        new(DeliveryProtocolConstants.UrlBased, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// EVERY url-keyed string in the blob, in document order — normally one, and deliberately not
    /// "the first one".
    ///
    /// <para><strong>Why all of them.</strong> A JSON object may repeat a key, and System.Text.Json
    /// keeps both: <see cref="JsonDocument"/> enumerates them in order, while
    /// <c>JsonSerializer.Deserialize</c> — what the dispatchers use — binds the LAST. Returning the
    /// first therefore let <c>{"url":"https://ok…","url":"http://evil…"}</c> be validated as the
    /// https endpoint and delivered to the cleartext one: a complete bypass of the transport rule on
    /// every path that shares this extraction. Inspecting all candidates removes the need to bet on
    /// which one the deserializer picks, and stays correct if that ever changes.</para>
    ///
    /// <para>The key match is case-insensitive because the dispatchers deserialize with
    /// <c>PropertyNameCaseInsensitive = true</c>: <c>{"URL":...}</c> is delivered, so it must be
    /// found here too.</para>
    /// </summary>
    public static IReadOnlyList<string> ExtractUrls(string? protocol, string? configJson)
    {
        if (string.IsNullOrWhiteSpace(protocol) || !UrlBasedProtocols.Contains(protocol))
            return Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(configJson)) return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<string>();

            List<string>? found = null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "url", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.String) continue;

                var value = property.Value.GetString();
                if (value is null) continue;

                (found ??= new List<string>()).Add(value);
            }

            return (IReadOnlyList<string>?)found ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The transport verdict for a delivery config: <see cref="OutboundUrlVerdict.Allow"/> when the
    /// protocol carries no URL or every url-keyed value passes, otherwise the FIRST refusal.
    ///
    /// <para>This is the single decision the save paths and the read/dispatch warnings all run, so
    /// "may this config send?" has exactly one answer no matter who asks. A config with no url key
    /// is left alone deliberately — it cannot deliver anything, and failing it would be a different
    /// behaviour change.</para>
    /// </summary>
    public static OutboundUrlVerdict InspectEndpoint(string? protocol, string? configJson)
    {
        foreach (var url in ExtractUrls(protocol, configJson))
        {
            var verdict = OutboundUrlPolicy.Inspect(url, "Delivery endpoint");
            if (!verdict.Allowed) return verdict;
        }

        return OutboundUrlVerdict.Allow();
    }

    /// <summary>
    /// An operator-facing warning when the stored endpoint is one the policy now refuses, or null
    /// when it is fine. Never quotes the URL: a stored userinfo URL is exactly the case where
    /// echoing it would copy the password into the editor and the logs.
    /// </summary>
    public static string? DescribeInsecureTransport(string? protocol, string? configJson)
    {
        var verdict = InspectEndpoint(protocol, configJson);
        return verdict.Allowed ? null : verdict.Message;
    }

    // ── Credential-bearing headers ───────────────────────────────────────────

    /// <summary>
    /// Header names that conventionally carry a credential, matched case-insensitively on the
    /// trimmed name.
    ///
    /// <para>Published rather than private so a test can walk it — an entry added here must not be
    /// addable without being covered — and so a UI could warn inline before a save is attempted.</para>
    /// </summary>
    public static IReadOnlyCollection<string> KnownCredentialHeaderNames => CredentialHeaderNames;

    private static readonly HashSet<string> CredentialHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "authentication",
        "cookie", "set-cookie",
        "x-api-key", "api-key", "apikey", "x-apikey",
        "x-auth-token", "x-authorization", "x-access-token", "x-auth-key",
        "x-amz-security-token", "x-goog-api-key", "x-functions-key",
        "ocp-apim-subscription-key", "private-token", "x-shopify-access-token",
    };

    /// <summary>
    /// Words that make a header name credential-bearing on their own, matched per hyphen/underscore
    /// segment.
    ///
    /// <para>Deliberately excludes bare <c>auth</c> and bare <c>key</c>. Including either would
    /// refuse <c>X-Auth-Email</c> and <c>X-Idempotency-Key</c> — headers real tenants send — and the
    /// delivery editor has no headers field, so a false refusal is a save an operator cannot make
    /// and cannot work around.</para>
    /// </summary>
    private static readonly HashSet<string> CredentialSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "secret", "password", "passwd", "pwd", "credential", "credentials", "apikey",
    };

    /// <summary>
    /// Adjacent segment pairs that are credential-bearing together though neither word is on its
    /// own — the reason bare <c>key</c> does not need to be.
    /// </summary>
    private static readonly HashSet<string> CredentialSegmentPairs = new(StringComparer.OrdinalIgnoreCase)
    {
        "api-key", "access-key", "secret-key", "private-key", "signing-key", "session-key",
    };

    private static readonly char[] HeaderNameSeparators = ['-', '_'];

    /// <summary>
    /// True when this header name conventionally carries a credential, by exact match or by
    /// segment. See <see cref="CredentialSegments"/> for why the segment rule is narrow.
    /// </summary>
    public static bool IsCredentialHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var trimmed = name.Trim();
        if (CredentialHeaderNames.Contains(trimmed)) return true;

        var segments = trimmed.Split(HeaderNameSeparators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (CredentialSegments.Contains(segments[i])) return true;
            if (i + 1 < segments.Length
                && CredentialSegmentPairs.Contains($"{segments[i]}-{segments[i + 1]}"))
                return true;
        }

        return false;
    }
}
