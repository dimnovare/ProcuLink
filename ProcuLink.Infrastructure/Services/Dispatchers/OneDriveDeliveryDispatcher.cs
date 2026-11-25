using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure.Services.Microsoft;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// OneDrive folder delivery through a linked Microsoft account (plan 30 Part B). One PUT by file name with
/// <c>conflictBehavior=replace</c>, so a re-send overwrites the same file (ResendSafety.Safe); a 200/201 is "accepted".
/// It only ever adds or replaces a file by its name in the chosen folder. Never throws.
/// </summary>
public sealed class OneDriveDeliveryDispatcher(
    IHttpClientFactory http, ILinkedAccountTokens tokens, ILogger<OneDriveDeliveryDispatcher> logger) : IDeliveryDispatcher
{
    private const string FolderGone = "The OneDrive or SharePoint folder is gone or ProcuLink can no longer reach it. Paste the folder's link again.";

    public string Protocol => DeliveryProtocolConstants.MicrosoftOneDrive;
    public ResendSafety ResendSafety => ResendSafety.Safe;
    public bool CapturesSupplierResponseBody => false;

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content, string fileName, string contentType, SupplierDeliveryConfig config,
        string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
    {
        try
        {
            var cfg = JsonNode.Parse(config.ConfigJson) as JsonObject;
            var driveId = Text(cfg, "driveId");
            var itemId = Text(cfg, "itemId");
            if (!Guid.TryParse(Text(cfg, "accountId"), out var accountId) || driveId is null || itemId is null) return Permanent(FolderGone);

            var token = await tokens.GetAccessTokenAsync(config.OrgId, accountId, LinkedAccountProviders.Microsoft, ct);
            if (token.Failure is { } failure) return failure;

            using var client = http.CreateClient(MicrosoftOAuthClient.HttpClientName);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(SftpDeliveryDispatcher.DefaultTimeoutSeconds));

            // Graph's ids go in as issued; only the name is escaped. driveitem-put-content takes up to 250 MB.
            var request = new HttpRequestMessage(HttpMethod.Put,
                $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{itemId}:/{Uri.EscapeDataString(SafeName(fileName))}:/content?@microsoft.graph.conflictBehavior=replace")
            {
                Content = new ByteArrayContent(content),
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var parsed) ? parsed : new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var put = await client.SendAsync(request, deadline.Token);

            var status = (int)put.StatusCode;
            if (put.IsSuccessStatusCode) return new DeliveryResult(true, null, status);
            logger.LogWarning("OneDrive delivery refused: HTTP {Status}", status);
            return status switch
            {
                403 or 404 => Permanent(FolderGone, status),
                507 => Permanent("There is no space left in this OneDrive or SharePoint library.", status),
                413 => Permanent("The file is larger than OneDrive accepts in one upload (250 MB).", status),
                408 or 423 or 429 or >= 500 => new DeliveryResult(false, $"OneDrive is not accepting uploads right now (HTTP {status}).", status,
                    RetryAfter: RetryAfterHeader.Read(put.Headers, DateTimeOffset.UtcNow)),
                _ => Permanent($"OneDrive refused the file (HTTP {status}).", status),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Timeouts, DNS and connection faults: transient.
            logger.LogWarning(ex, "OneDrive delivery failed");
            return new DeliveryResult(false, "OneDrive could not be reached.");
        }
    }

    /// <summary>OneDrive refuses <c>" * : &lt; &gt; ? / \ |</c>, control characters and a trailing dot or space in a name.</summary>
    private static string SafeName(string fileName)
    {
        var name = new string(fileName.Select(c => char.IsControl(c) || "\"*:<>?/\\|".Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return name.Length == 0 ? "document" : name;
    }

    private static DeliveryResult Permanent(string message, int? status = null) =>
        new(false, message, status, Outcome: DeliveryOutcome.NotRetryable);

    private static string? Text(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
