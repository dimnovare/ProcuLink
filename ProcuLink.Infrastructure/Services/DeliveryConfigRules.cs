using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// The write-time rules for a delivery config. Shared by <see cref="DeliveryConfigService"/> (a
/// supplier's delivery config) and the document-flow destinations, so the two cannot drift.
/// </summary>
public static class DeliveryConfigRules
{
    public static void ValidateConfigJson(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            throw new ArgumentException("Delivery config JSON is required.", nameof(configJson));

        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(configJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ArgumentException("Delivery config JSON must be valid JSON.", nameof(configJson), ex);
        }
    }

    /// <summary>
    /// Refuses a delivery endpoint that would send the purchase order — and the credentials that
    /// authenticate it — over plain http, or that carries a username/password in the URL itself.
    ///
    /// <para>Scoped deliberately to the transport <c>url</c> inside <c>config_json</c>. It must NOT
    /// touch <c>CxmlCredentials.DtdSystemId</c>, which travels on the same request: that is a cXML
    /// DOCTYPE SYSTEM identifier emitted into the output document and never fetched, and its
    /// canonical published value is <c>http://xml.cxml.org/...</c>.</para>
    ///
    /// <para>The key lookup is case-insensitive because the dispatchers deserialize with
    /// <c>PropertyNameCaseInsensitive = true</c> — <c>{"URL":...}</c> is delivered, so it must also
    /// be inspected — and EVERY url-keyed value is judged, not just the first: a repeated key kept
    /// both, and the deserializer binds the last, so inspecting one of them was bypassable.</para>
    /// </summary>
    public static void ValidateTransportSecurity(string protocol, string configJson)
    {
        // Allow when the protocol carries no URL at all: that config cannot deliver anything, and
        // failing it here would be a separate behaviour change from the one this guard is for.
        var verdict = DeliveryConfigTransport.InspectEndpoint(protocol, configJson);
        if (!verdict.Allowed)
            throw new OutboundUrlPolicyException(verdict, nameof(UpsertDeliveryConfigRequest.ConfigJson));
    }

    /// <summary>
    /// Refuses an OAuth token URL — inside the ENCRYPTED credentials blob — that the transport
    /// policy would refuse as a delivery endpoint. The client-credentials exchange POSTs
    /// <c>client_id</c> and <c>client_secret</c> to that URL at send time, so a cleartext
    /// <c>tokenUrl</c> leaks the secret itself. The catalog save path already inspects its own
    /// token URL (<c>SuppliersController</c>); this closes the same gap on the delivery side,
    /// through the same shared walker so the two write paths cannot drift.
    /// </summary>
    public static void ValidateCredentialTokenUrl(string? credentialsJson)
    {
        var verdict = DeliveryConfigTransport.InspectCredentialTokenUrl(credentialsJson);
        if (!verdict.Allowed)
            throw new OutboundUrlPolicyException(verdict, nameof(UpsertDeliveryConfigRequest.CredentialsJson));
    }

    /// <summary>
    /// Refuses a credential written into the delivery config's extra-headers map. Every entry of
    /// that map is applied to the outbound request by <c>HttpDeliveryDispatcher</c>, so
    /// <c>Authorization: Bearer …</c> typed there is a real credential stored in a cleartext column,
    /// returned by GET, and copied into every connection-revision snapshot.
    ///
    /// <para>The revision write path MUST reach this same primitive
    /// (<see cref="DeliveryConfigTransport.FindCredentialHeaders"/>) rather than hand-rolling its own
    /// header check — see <c>SupplierConnectionService.ValidateCredentialHeaders</c>. Two copies of a
    /// security rule is how the transport gap existed in the first place, and #157 exists to stop it
    /// happening twice.</para>
    /// </summary>
    public static void ValidateCredentialHeaders(string configJson, string? storedConfigJson)
    {
        var offending = DeliveryConfigTransport.FindCredentialHeaders(configJson, storedConfigJson);
        if (offending.Count > 0)
            throw new CredentialHeaderInConfigException(offending);
    }
}
