namespace ProcuLink.Infrastructure.Services.Security;

/// <summary>
/// One place that says what a 3xx from a tenant-configured endpoint means, so every outbound
/// channel tells the operator the same actionable thing.
///
/// <para>
/// The guarded transport (<see cref="OutboundRequestGuard.CreateGuardedHttpHandler"/>) does not
/// follow redirects. Before that fix a 3xx was followed silently: the redirect target was never
/// re-run through <see cref="OutboundRequestGuard.ValidateAsync"/>, and .NET replays the request
/// body across a 307/308 and a custom header across any redirect — so the order and the
/// credentials reached a host nobody configured. Now the 3xx reaches the caller as the response it
/// is. Every channel already treats it as a non-success; this makes sure it reads as something an
/// operator can fix rather than an opaque error code.
/// </para>
/// </summary>
public static class OutboundRedirectPolicy
{
    /// <summary>True for any 3xx status.</summary>
    public static bool IsRedirect(int statusCode) => statusCode is >= 300 and < 400;

    /// <summary>
    /// What a delivery/ERP endpoint's operator is told when the configured URL redirects.
    /// </summary>
    public static string DescribeRefusal(int statusCode) =>
        $"HTTP {statusCode}: the endpoint redirected the request. Redirects are not followed, " +
        "because the order and its credentials would be replayed to a host that was never " +
        "configured. Update the delivery URL to the endpoint's final address.";

    /// <summary>
    /// The same refusal, worded for the OAuth token endpoint (no order is in flight there — the
    /// client secret is).
    /// </summary>
    public static string DescribeTokenEndpointRefusal(int statusCode) =>
        $"OAuth token request failed: HTTP {statusCode}. The token endpoint redirected the " +
        "request, and redirects are not followed because the client credentials would be replayed " +
        "to a host that was never configured. Update the token URL to its final address.";
}
