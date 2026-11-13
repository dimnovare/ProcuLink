using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure.Services.Google;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// Google Drive folder delivery through a linked account (plan 9b, R10). Looks the file name up in the
/// folder and updates that file, else creates it, both over a resumable session; a 200/201 on the
/// final PUT is "accepted". The name lookup makes a re-send overwrite instead of duplicating
/// (ResendSafety.Safe). Never throws.
/// </summary>
public sealed class GoogleDriveDeliveryDispatcher(
    IHttpClientFactory http, ILinkedAccountTokens tokens, ILogger<GoogleDriveDeliveryDispatcher> logger) : IDeliveryDispatcher
{
    private const string Api = "https://www.googleapis.com";
    private const string FolderGone = "The Google Drive folder is gone or ProcuLink can no longer see it. Choose the folder again.";

    public string Protocol => DeliveryProtocolConstants.GoogleDrive;
    public ResendSafety ResendSafety => ResendSafety.Safe;
    public bool CapturesSupplierResponseBody => false;

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content, string fileName, string contentType, SupplierDeliveryConfig config,
        string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
    {
        try
        {
            var cfg = JsonNode.Parse(config.ConfigJson) as JsonObject;
            var folderId = Text(cfg, "folderId");
            if (!Guid.TryParse(Text(cfg, "accountId"), out var accountId) || folderId is null)
                return Permanent("Google Drive delivery configuration is invalid: a Google account and a folder are required.");

            var token = await tokens.GetGoogleAccessTokenAsync(config.OrgId, accountId, ct);
            if (token.Failure is { } failure) return failure;

            using var client = http.CreateClient(GoogleOAuthClient.HttpClientName);
            // Bounded like SFTP and S3; the caller's own cancellation still propagates (filter below).
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(SftpDeliveryDispatcher.DefaultTimeoutSeconds));

            var name = fileName.Replace('\\', '/').Split('/').Last();
            var q = $"name = '{Escape(name)}' and '{Escape(folderId)}' in parents and trashed = false";
            using var lookup = await SendAsync(client, token.Token!, HttpMethod.Get,
                $"{Api}/drive/v3/files?q={Uri.EscapeDataString(q)}&supportsAllDrives=true&includeItemsFromAllDrives=true&fields=files(id)",
                null, deadline.Token);
            if (!lookup.IsSuccessStatusCode) return await Refused(lookup, deadline.Token);
            var existingId = JsonNode.Parse(await lookup.Content.ReadAsStringAsync(deadline.Token))?["files"]?.AsArray()
                .FirstOrDefault()?["id"]?.GetValue<string>();

            var (method, url, body) = existingId is null
                ? (HttpMethod.Post, $"{Api}/upload/drive/v3/files?uploadType=resumable&supportsAllDrives=true",
                    new JsonObject { ["name"] = name, ["parents"] = new JsonArray(folderId) })
                : (HttpMethod.Patch, $"{Api}/upload/drive/v3/files/{Uri.EscapeDataString(existingId)}?uploadType=resumable&supportsAllDrives=true",
                    new JsonObject());
            using var open = await SendAsync(client, token.Token!, method, url,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), deadline.Token,
                r => r.Headers.TryAddWithoutValidation("X-Upload-Content-Type", contentType));
            if (!open.IsSuccessStatusCode) return await Refused(open, deadline.Token);
            if (open.Headers.Location is not { } upload)
                return new DeliveryResult(false, "Google Drive did not open an upload session.", (int)open.StatusCode);

            var bytes = new ByteArrayContent(content);
            bytes.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var parsed) ? parsed : new MediaTypeHeaderValue("application/octet-stream");
            using var put = await SendAsync(client, token.Token!, HttpMethod.Put, upload.ToString(), bytes, deadline.Token);
            return put.IsSuccessStatusCode
                ? new DeliveryResult(true, null, (int)put.StatusCode)
                : await Refused(put, deadline.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Timeouts, DNS and connection faults: transient.
            logger.LogWarning(ex, "Google Drive delivery failed");
            return new DeliveryResult(false, "Google Drive could not be reached.");
        }
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, string accessToken, HttpMethod method, string url, HttpContent? body, CancellationToken ct,
        Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        configure?.Invoke(request);
        return client.SendAsync(request, ct);
    }

    private async Task<DeliveryResult> Refused(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string? reason = null;
        try
        {
            reason = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["error"]?["errors"]?.AsArray()
                .FirstOrDefault()?["reason"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // An unreadable body only loses the reason.
        }
        logger.LogWarning("Google Drive delivery refused: HTTP {Status} {Reason}", status, reason);

        if (reason == "storageQuotaExceeded") return Permanent("Google Drive is full.", status);
        if (status is 408 or 429 or >= 500 || reason is "rateLimitExceeded" or "userRateLimitExceeded")
            return new DeliveryResult(false, $"Google Drive is not accepting uploads right now (HTTP {status}).", status,
                RetryAfter: RetryAfterHeader.Read(response.Headers, DateTimeOffset.UtcNow));
        if (status == 404 || (status == 403 && reason is "insufficientFilePermissions" or "forbidden")) return Permanent(FolderGone, status);
        return Permanent($"Google Drive refused the file (HTTP {status}).", status);
    }

    private static DeliveryResult Permanent(string message, int? status = null) =>
        new(false, message, status, Outcome: DeliveryOutcome.NotRetryable);

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string? Text(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
