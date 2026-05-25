using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

public class HttpDeliveryDispatcher : IDeliveryDispatcher
{
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Protocol => "http";

    public HttpDeliveryDispatcher(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
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
            var httpCfg = JsonSerializer.Deserialize<HttpConfig>(config.ConfigJson, JsonOpts)
                          ?? throw new InvalidOperationException("Invalid HTTP config JSON.");

            var creds = string.IsNullOrEmpty(decryptedCredentials)
                ? default
                : JsonSerializer.Deserialize<JsonElement>(decryptedCredentials, JsonOpts);

            var client  = _httpClientFactory.CreateClient("delivery");
            var request = new HttpRequestMessage(
                new HttpMethod(httpCfg.Method ?? "POST"),
                httpCfg.Url);

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

            var response = await client.SendAsync(request, ct);
            var body     = await response.Content.ReadAsStringAsync(ct);
            var code     = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? new DeliveryResult(true, null, code)
                : new DeliveryResult(false, $"HTTP {code}: {body[..Math.Min(200, body.Length)]}", code);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(false, ex.Message);
        }
    }

    private static void ApplyAuth(HttpRequestMessage request, JsonElement creds)
    {
        if (creds.ValueKind == JsonValueKind.Undefined) return;

        var type = creds.TryGetProperty("type", out var t) ? t.GetString() : "none";
        switch (type)
        {
            case "apikey":
                var header = creds.GetProperty("header").GetString()!;
                var value  = creds.GetProperty("value").GetString()!;
                request.Headers.TryAddWithoutValidation(header, value);
                break;

            case "bearer":
                var token = creds.GetProperty("token").GetString()!;
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
                break;

            case "basic":
                var user    = creds.GetProperty("username").GetString()!;
                var pass    = creds.GetProperty("password").GetString()!;
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Basic", encoded);
                break;
        }
    }

    // ── Private config POCO ───────────────────────────────────────────────────

    private record HttpConfig(
        string Url,
        string? Method,
        Dictionary<string, string>? Headers,
        int? TimeoutSeconds);
}
