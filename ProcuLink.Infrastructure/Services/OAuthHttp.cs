using System.Net;
using System.Text.Json;
using ProcuLink.Core.Services.Google;

namespace ProcuLink.Infrastructure.Services;

/// <summary>The HTTP plumbing every linked-account OAuth client shares. Nothing here logs.</summary>
internal static class OAuthHttp
{
    /// <summary>Connect-time calls: every failure, the network included, is an <see cref="OAuthException"/>.</summary>
    internal static async Task<JsonElement> RequireAsync(HttpClient client, HttpRequestMessage request, string who, CancellationToken ct)
    {
        HttpStatusCode status;
        JsonElement body;
        try { (status, body) = await SendAsync(client, request, who, ct); }
        catch (HttpRequestException) { throw new OAuthException(OAuthException.Unavailable); }
        if ((int)status is < 200 or > 299) throw new OAuthException(Str(body, "error") ?? OAuthException.Unavailable);
        return body;
    }

    /// <summary>A timeout surfaces as <see cref="HttpRequestException"/>, like any other network failure.</summary>
    internal static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpClient client, HttpRequestMessage request, string who, CancellationToken ct)
    {
        try
        {
            using (request)
            using (var response = await client.SendAsync(request, ct))
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                JsonElement body = default;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    body = doc.RootElement.Clone();
                }
                catch (JsonException) { }
                return (response.StatusCode, body);
            }
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException($"{who} did not answer in time.", ex);
        }
    }

    internal static string? Str(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
