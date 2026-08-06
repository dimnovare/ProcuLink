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
    /// The endpoint URL a url-bearing protocol will send to, or null when the protocol is
    /// host-based, the blob is unparseable, or no <c>url</c> key is present.
    ///
    /// <para>The key match is case-insensitive because the dispatchers deserialize with
    /// <c>PropertyNameCaseInsensitive = true</c>: <c>{"URL":...}</c> is delivered, so it must be
    /// found here too.</para>
    /// </summary>
    public static string? ExtractUrl(string? protocol, string? configJson)
    {
        if (string.IsNullOrWhiteSpace(protocol) || !UrlBasedProtocols.Contains(protocol)) return null;
        if (string.IsNullOrWhiteSpace(configJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "url", StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// An operator-facing warning when the stored endpoint is one the policy now refuses, or null
    /// when it is fine. Never quotes the URL: a stored userinfo URL is exactly the case where
    /// echoing it would copy the password into the editor and the logs.
    /// </summary>
    public static string? DescribeInsecureTransport(string? protocol, string? configJson)
    {
        var url = ExtractUrl(protocol, configJson);
        if (string.IsNullOrWhiteSpace(url)) return null;

        var verdict = OutboundUrlPolicy.Inspect(url, "Delivery endpoint");
        return verdict.Allowed ? null : verdict.Message;
    }
}
