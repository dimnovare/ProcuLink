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
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.HostNameType != UriHostNameType.Dns || uri.UserInfo.Length > 0) return false;
        string host;
        try { host = uri.IdnHost.ToLowerInvariant(); } // the host the socket connects to
        catch (UriFormatException) { return false; } // .NET 8 throws on a host IDN cannot encode (for example U+FF0F)
        return host == "fcm.googleapis.com" || Suffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal));
    }
}
