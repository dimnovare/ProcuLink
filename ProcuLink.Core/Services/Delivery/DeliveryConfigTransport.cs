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
}
