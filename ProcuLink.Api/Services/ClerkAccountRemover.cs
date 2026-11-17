using System.Net;
using System.Text.Json;
using ProcuLink.Api.Auth;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Services;

/// <summary>
/// Implements <see cref="ISignInAccountRemover"/> against the Clerk Backend API. A member is deleted
/// only when Clerk reports exactly one organisation membership for them and it is this organisation:
/// a person who also works in another workspace keeps their sign-in. Platform staff are never deleted.
/// If the member list cannot be read in full nothing is deleted, not even the organisation, because
/// deleting it would leave its members impossible to find again.
/// </summary>
public sealed class ClerkAccountRemover(IHttpClientFactory http, IConfiguration config, AdminAllowlist staff, ILogger<ClerkAccountRemover> log)
    : ISignInAccountRemover
{
    public async Task<SignInRemoval> RemoveAsync(string clerkOrgId, CancellationToken ct)
    {
        var key = config["Clerk:SecretKey"];
        if (string.IsNullOrWhiteSpace(key)) return new(SignInSteps.SkippedNoKey, 0, []);

        var client = http.CreateClient("clerk");
        var members = string.IsNullOrWhiteSpace(clerkOrgId) ? null : await ClerkMembers.FetchAsync(client, key, clerkOrgId, log, ct);
        if (members is null) return new(SignInSteps.Failed, 0, ["members"]);

        var failed = new List<string>();
        var removed = 0;
        foreach (var (userId, email) in members)
        {
            if (staff.IsAdmin(userId, email)) continue;
            var onlyHere = await BelongsOnlyToAsync(client, key, userId, clerkOrgId, ct);
            if (onlyHere is null) { failed.Add(userId); continue; }
            if (!onlyHere.Value) continue;

            using var deleted = await SendAsync(client, key, HttpMethod.Delete, $"users/{Uri.EscapeDataString(userId)}", ct);
            if (deleted is { IsSuccessStatusCode: true } or { StatusCode: HttpStatusCode.NotFound }) removed++;
            else failed.Add(userId);
        }

        using var org = await SendAsync(client, key, HttpMethod.Delete, $"organizations/{Uri.EscapeDataString(clerkOrgId)}", ct);
        if (org is not { IsSuccessStatusCode: true }) failed.Add("organization");

        if (failed.Count > 0) log.LogWarning("Sign-in removal for {ClerkOrgId}: {Removed} removed, could not remove {Failed}", clerkOrgId, removed, string.Join(", ", failed));
        return new(failed.Count == 0 ? SignInSteps.Done : SignInSteps.Failed, removed, failed);
    }

    /// <summary>
    /// True only when <c>GET users/{id}/organization_memberships</c> answers <c>total_count</c> 1 and its one
    /// <c>data[0].organization.id</c> is <paramref name="clerkOrgId"/> (Clerk Backend API, response
    /// <c>OrganizationMemberships</c>). Any other shape is false, which keeps the user; null means the read failed.
    /// </summary>
    private async Task<bool?> BelongsOnlyToAsync(HttpClient client, string key, string userId, string clerkOrgId, CancellationToken ct)
    {
        using var response = await SendAsync(client, key, HttpMethod.Get, $"users/{Uri.EscapeDataString(userId)}/organization_memberships?limit=1", ct);
        if (response is not { IsSuccessStatusCode: true }) return null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("total_count", out var total) && total.ValueKind == JsonValueKind.Number
                && total.TryGetInt64(out var count) && count == 1
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 1
                && data[0].ValueKind == JsonValueKind.Object
                && data[0].TryGetProperty("organization", out var org) && org.ValueKind == JsonValueKind.Object
                && org.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() == clerkOrgId;
        }
        catch (Exception ex)
        {
            log.LogWarning("Sign-in removal: memberships of {UserId} unreadable: {Error}", userId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>One authorised call; null when it could not be made, so the caller never sees an exception.</summary>
    private async Task<HttpResponseMessage?> SendAsync(HttpClient client, string key, HttpMethod method, string path, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new("Bearer", key);
            return await client.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning("Sign-in removal: {Method} {Path} failed: {Error}", method, path, ex.GetType().Name);
            return null;
        }
    }
}
