using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

public class HttpDeliveryDispatcher : IDeliveryDispatcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpDeliveryDispatcher> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => "http";

    public HttpDeliveryDispatcher(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpDeliveryDispatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content,
        string fileName,
        string contentType,
        SupplierDeliveryConfig config,
        string decryptedCredentials,
        CancellationToken ct)
    {
        try
        {
            var httpCfg = JsonSerializer.Deserialize<HttpConfig>(config.ConfigJson, JsonOpts);
            if (httpCfg is null || string.IsNullOrWhiteSpace(httpCfg.Url))
                return new DeliveryResult(false, "HTTP delivery configuration is invalid.");

            if (!Uri.TryCreate(httpCfg.Url, UriKind.Absolute, out var endpoint))
                return new DeliveryResult(false, "HTTP delivery endpoint URL is invalid.");

            var creds = string.IsNullOrEmpty(decryptedCredentials)
                ? default
                : JsonSerializer.Deserialize<JsonElement>(decryptedCredentials, JsonOpts);

            var client  = _httpClientFactory.CreateClient("delivery");
            var request = new HttpRequestMessage(
                new HttpMethod(string.IsNullOrWhiteSpace(httpCfg.Method) ? "POST" : httpCfg.Method),
                endpoint);

            // Apply auth
            ApplyAuth(request, creds);

            // Apply extra headers
            if (httpCfg.Headers is not null)
                foreach (var (k, v) in httpCfg.Headers)
                    request.Headers.TryAddWithoutValidation(k, v);

            // Body
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType =
                MediaTypeHeaderValue.TryParse(contentType, out var mt) ? mt : new MediaTypeHeaderValue("application/octet-stream");

            using var timeoutCts = httpCfg.TimeoutSeconds is > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(httpCfg.TimeoutSeconds!.Value));
            var requestCt = timeoutCts?.Token ?? ct;

            var response = await client.SendAsync(request, requestCt);
            var body     = await response.Content.ReadAsStringAsync(requestCt);
            var code     = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? new DeliveryResult(true, null, code)
                : new DeliveryResult(false, BuildFailureMessage(code, body), code);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeliveryResult(false, "HTTP delivery timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP delivery failed before a response was received.");
            return new DeliveryResult(false, "HTTP delivery failed before receiving a response.");
        }
    }

    private static void ApplyAuth(HttpRequestMessage request, JsonElement creds)
    {
        if (creds.ValueKind == JsonValueKind.Undefined) return;

        var type = creds.TryGetProperty("type", out var t) ? t.GetString() : "none";
        switch (type)
        {
            case "apikey":
                if (creds.TryGetProperty("header", out var h) &&
                    creds.TryGetProperty("value", out var v) &&
                    !string.IsNullOrWhiteSpace(h.GetString()))
                {
                    var headerName = h.GetString()!;
                    request.Headers.TryAddWithoutValidation(headerName, v.GetString());
                }
                break;

            case "bearer":
                if (creds.TryGetProperty("token", out var token) &&
                    !string.IsNullOrWhiteSpace(token.GetString()))
                {
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", token.GetString());
                }
                break;

            case "basic":
                if (creds.TryGetProperty("username", out var username) &&
                    creds.TryGetProperty("password", out var password))
                {
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username.GetString()}:{password.GetString()}"));
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Basic", encoded);
                }
                break;
        }
    }

    private static string BuildFailureMessage(int code, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return $"HTTP {code}: supplier endpoint returned an error.";

        var summary = body
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (summary.Length > 120)
            summary = summary[..120];

        return $"HTTP {code}: supplier endpoint returned an error. Response summary: {summary}";
    }

    // ── Private config POCO ───────────────────────────────────────────────────

    private record HttpConfig(
        string Url,
        string? Method,
        Dictionary<string, string>? Headers,
        int? TimeoutSeconds);
}
