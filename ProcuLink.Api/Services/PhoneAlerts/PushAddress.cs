namespace ProcuLink.Api.Services.PhoneAlerts;

/// <summary>
/// Only real push services: the browser hands us the address and the Worker POSTs to it every few minutes,
/// so anything else (an internal URL, another port) would be a request forgery path.
/// </summary>
public static class PushAddress
{
    private static readonly string[] Suffixes = [".push.apple.com", ".push.services.mozilla.com", ".notify.windows.com"];

    public static bool IsAllowed(string? endpoint)
    {
        if (endpoint is null || endpoint.Length > 2048 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        var host = uri.Host.ToLowerInvariant();
        return host == "fcm.googleapis.com" || Suffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal));
    }
}
