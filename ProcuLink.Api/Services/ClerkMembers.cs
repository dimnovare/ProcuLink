using System.Text.Json;

namespace ProcuLink.Api.Services;

/// <summary>Reads a Clerk organisation's members, shared by <c>NotificationMembersJob</c> and <see cref="ClerkAccountRemover"/>.</summary>
public static class ClerkMembers
{
    private const int PageSize = 100;
    private const int MaxOffset = 100_000;

    /// <summary>All members as user id to email (null when the identifier is not an address), or null when the read failed or was incomplete.</summary>
    public static async Task<Dictionary<string, string?>?> FetchAsync(HttpClient client, string key, string clerkOrgId, ILogger log, CancellationToken ct)
    {
        var members = new Dictionary<string, string?>();
        try
        {
            for (var offset = 0; offset < MaxOffset; offset += PageSize)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"organizations/{Uri.EscapeDataString(clerkOrgId)}/memberships?limit={PageSize}&offset={offset}");
                request.Headers.Authorization = new("Bearer", key);
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) { log.LogWarning("Clerk member read failed for {ClerkOrgId}: Clerk answered {Status}", clerkOrgId, (int)response.StatusCode); return null; }

                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var page = doc.RootElement.GetProperty("data");
                foreach (var m in page.EnumerateArray())
                {
                    var user = m.GetProperty("public_user_data");
                    var identifier = user.TryGetProperty("identifier", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()?.Trim() : null;
                    members[user.GetProperty("user_id").GetString()!] = identifier?.Contains('@') == true ? identifier : null;
                }
                if (page.GetArrayLength() < PageSize)
                {
                    if (doc.RootElement.TryGetProperty("total_count", out var t) && t.ValueKind == JsonValueKind.Number && members.Count < t.GetInt32())
                    {
                        log.LogWarning("Clerk member read failed for {ClerkOrgId}: read {Count} of {Total} members", clerkOrgId, members.Count, t.GetInt32());
                        return null;
                    }
                    return members;
                }
            }
            log.LogWarning("Clerk member read failed for {ClerkOrgId}: paging passed {Max} members", clerkOrgId, MaxOffset);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentNullException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Clerk member read failed for {ClerkOrgId}: {Error}", clerkOrgId, ex.GetType().Name);
            return null;
        }
    }
}
