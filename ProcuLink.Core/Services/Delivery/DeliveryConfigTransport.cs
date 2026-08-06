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

    /// <summary>
    /// Every credential-bearing header name in the blob, in document order, deduped
    /// case-insensitively. Empty means there is nothing to refuse.
    ///
    /// <para><strong>Every <c>headers</c>-keyed object is inspected, not the first.</strong> Same
    /// trap as <see cref="ExtractUrls"/>: a JSON object may repeat a key and System.Text.Json keeps
    /// both — <see cref="JsonDocument"/> enumerates them in document order while
    /// <c>JsonSerializer.Deserialize</c>, what the dispatcher uses, binds the LAST. Inspecting one
    /// of them would validate the clean map and deliver the credential-bearing one.</para>
    ///
    /// <para><strong>Not protocol-scoped.</strong> Only the http connector declares a headers map
    /// today, but a guard scoped to a protocol list goes stale in one direction — a protocol that
    /// later grows one inherits no protection and nothing fails. Inspecting the key wherever it
    /// appears costs nothing and cannot produce a false refusal.</para>
    ///
    /// <para><paramref name="storedConfigJson"/> grandfathers a header whose name AND value are
    /// already persisted, so an unchanged round-trip is not treated as a write of a secret. The
    /// delivery editor has no headers field and carries the stored map through every save untouched;
    /// refusing that echo would lock an operator out of every unrelated edit with no way to remove
    /// the header. Adding one, or rotating its value, is still refused. Pass null — the default —
    /// to grandfather nothing.</para>
    /// </summary>
    public static IReadOnlyList<string> FindCredentialHeaders(
        string? configJson, string? storedConfigJson = null)
    {
        var incoming = ReadHeaderEntries(configJson);
        if (incoming.Count == 0) return Array.Empty<string>();

        var stored = ReadHeaderEntries(storedConfigJson);

        List<string>? offending = null;
        HashSet<string>? seen = null;

        foreach (var (name, value) in incoming)
        {
            if (!IsCredentialHeaderName(name)) continue;
            if (IsAlreadyStored(stored, name, value)) continue;

            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!seen.Add(name.Trim())) continue;

            (offending ??= new List<string>()).Add(name.Trim());
        }

        return (IReadOnlyList<string>?)offending ?? Array.Empty<string>();
    }

    /// <summary>
    /// An operator-facing message naming the credential-bearing headers a stored config carries, or
    /// null when it carries none. Names only — echoing the value is the defect itself.
    /// </summary>
    public static string? DescribeCredentialHeaders(string? configJson)
    {
        var names = FindCredentialHeaders(configJson);
        return names.Count == 0 ? null : BuildCredentialHeaderMessage(names);
    }

    /// <summary>
    /// The one wording, shared by the refusal and the read-path warning so they cannot drift. It
    /// names the destination concretely — the connector manifest really does carry
    /// <c>type</c> + <c>header</c> + <c>value</c> under credentials — so an operator following it
    /// lands on a field that exists.
    /// </summary>
    internal static string BuildCredentialHeaderMessage(IReadOnlyList<string> names)
    {
        var quoted = string.Join(", ", names.Select(n => $"'{n}'"));
        var subject = names.Count == 1
            ? $"Delivery config header {quoted} holds a credential."
            : $"Delivery config headers {quoted} hold credentials.";

        return subject
            + " This config is stored in cleartext, so credentials belong in this supplier's delivery"
            + " credentials — set the auth type there to bearer, basic, apikey or oauth2_client_credentials — where they"
            + " are encrypted. Remove the header and save the token as a credential instead.";
    }

    /// <summary>
    /// Every (name, comparable value) pair under EVERY <c>headers</c>-keyed object in the blob.
    /// An unparseable blob yields nothing: <c>ValidateConfigJson</c> already refuses those on the
    /// save path, and failing here as well would turn a parse error into a security refusal.
    /// </summary>
    private static List<(string Name, string Value)> ReadHeaderEntries(string? configJson)
    {
        var entries = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(configJson)) return entries;

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return entries;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "headers", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.Object) continue;

                foreach (var header in property.Value.EnumerateObject())
                    entries.Add((header.Name, ComparableValue(header.Value)));
            }
        }
        catch (JsonException)
        {
            return entries;
        }

        return entries;
    }

    /// <summary>
    /// The token two blobs are compared by. A JSON string is compared DECODED, so a client that
    /// re-serialises <c>"\u00411"</c> as <c>"A1"</c> has not rotated the secret and is not refused;
    /// anything else is compared by raw text, which is exact for every other value kind.
    /// </summary>
    private static string ComparableValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

    private static bool IsAlreadyStored(
        List<(string Name, string Value)> stored, string name, string value)
    {
        foreach (var (storedName, storedValue) in stored)
            if (string.Equals(storedName.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(storedValue, value, StringComparison.Ordinal))
                return true;

        return false;
    }
}

/// <summary>
/// Thrown by the delivery-config write paths when a caller puts a credential into the extra-headers
/// map of <c>config_json</c>, which is stored in cleartext by design.
///
/// <para>Derives from <see cref="ArgumentException"/> so a handler that is not updated still answers
/// 400 rather than 500, exactly as <see cref="OutboundUrlPolicyException"/> does;
/// <see cref="Code"/> and <see cref="PolicyMessage"/> let one that is updated return the same
/// machine-readable shape the transport refusal already uses. No <c>paramName</c> is passed, because
/// ArgumentException's <c>(Parameter '…')</c> suffix is right for a log and wrong for a body an
/// operator reads.</para>
/// </summary>
public sealed class CredentialHeaderInConfigException : ArgumentException
{
    public const string Code = "credential_header_in_delivery_config";

    /// <summary>The offending header NAMES. Never their values.</summary>
    public IReadOnlyList<string> HeaderNames { get; }

    public string PolicyMessage { get; }

    public CredentialHeaderInConfigException(IReadOnlyList<string> headerNames)
        : this(headerNames, DeliveryConfigTransport.BuildCredentialHeaderMessage(headerNames))
    {
    }

    private CredentialHeaderInConfigException(IReadOnlyList<string> headerNames, string message)
        : base(message)
    {
        HeaderNames = headerNames;
        PolicyMessage = message;
    }
}
