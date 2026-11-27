using System.Text.Json;

namespace ProcuLink.Api.Services;

/// <summary>Reads every Clerk organisation's name, for the admin customer list.</summary>
public static class ClerkOrgNames
{
    private const int PageSize = 100;
    private const int MaxOrgs = 10_000;

    /// <summary>Clerk organisation id to name. Best effort: what was read before a failure, never a throw, at most 5 seconds.</summary>
    public static async Task<Dictionary<string, string>> FetchAsync(HttpClient client, string key, ILogger log, CancellationToken ct)
    {
        var names = new Dictionary<string, string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            for (var offset = 0; offset < MaxOrgs; offset += PageSize)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"organizations?limit={PageSize}&offset={offset}");
                request.Headers.Authorization = new("Bearer", key);
                using var response = await client.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode) { log.LogWarning("Clerk organisation names read failed: Clerk answered {Status}", (int)response.StatusCode); break; }

                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                var page = doc.RootElement.GetProperty("data");
                foreach (var o in page.EnumerateArray())
                    if (o.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                        names[o.GetProperty("id").GetString()!] = n.GetString()!.Trim();
                if (page.GetArrayLength() < PageSize) break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Clerk organisation names read failed: {Error}", ex.GetType().Name);
        }
        return names;
    }
}
