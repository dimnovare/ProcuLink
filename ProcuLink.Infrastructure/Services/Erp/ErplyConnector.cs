using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services.Erp;

namespace ProcuLink.Infrastructure.Services.Erp;

public sealed class ErplyConnector : IErpConnector
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ErplyConnector> _logger;

    public string Protocol => DeliveryProtocolConstants.ErpErply;

    public ErplyConnector(IHttpClientFactory httpClientFactory, ILogger<ErplyConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ErpDeliveryResult> SendAsync(ErpDeliveryRequest request, CancellationToken ct)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<ErplyConfig>(request.Config.ConfigJson, JsonOpts);
            if (cfg is null || string.IsNullOrWhiteSpace(cfg.Url))
                return new ErpDeliveryResult(false, "Erply connector configuration is invalid.");

            if (!Uri.TryCreate(cfg.Url, UriKind.Absolute, out var endpoint))
                return new ErpDeliveryResult(false, "Erply connector endpoint URL is invalid.");

            var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(request.Content)
            };

            message.Content.Headers.ContentType =
                MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
                    ? mediaType
                    : new MediaTypeHeaderValue("application/octet-stream");

            message.Headers.TryAddWithoutValidation("X-ProcuLink-FileName", request.FileName);
            if (!string.IsNullOrWhiteSpace(cfg.ClientCode))
                message.Headers.TryAddWithoutValidation("X-Erply-Client-Code", cfg.ClientCode);

            ApplyAuth(message, request.DecryptedCredentials);

            using var timeoutCts = cfg.TimeoutSeconds is > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds!.Value));
            var requestCt = timeoutCts?.Token ?? ct;

            var response = await _httpClientFactory.CreateClient("delivery").SendAsync(message, requestCt);
            var body = await response.Content.ReadAsStringAsync(requestCt);
            var code = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? new ErpDeliveryResult(true, null, code)
                : new ErpDeliveryResult(false, BuildFailureMessage("Erply", code, body), code);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ErpDeliveryResult(false, "Erply connector timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erply connector failed before a response was received.");
            return new ErpDeliveryResult(false, "Erply connector failed before receiving a response.");
        }
    }

    private static void ApplyAuth(HttpRequestMessage request, string credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
            return;

        using var doc = JsonDocument.Parse(credentialsJson);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "none";

        if (string.Equals(type, "bearer", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("token", out var token)
            && !string.IsNullOrWhiteSpace(token.GetString()))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.GetString());
            return;
        }

        if (string.Equals(type, "apikey", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("header", out var header)
            && root.TryGetProperty("value", out var value)
            && !string.IsNullOrWhiteSpace(header.GetString()))
        {
            request.Headers.TryAddWithoutValidation(header.GetString()!, value.GetString());
        }
    }

    private static string BuildFailureMessage(string connector, int code, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return $"{connector} HTTP {code}: ERP endpoint returned an error.";

        var summary = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (summary.Length > 120)
            summary = summary[..120];

        return $"{connector} HTTP {code}: ERP endpoint returned an error. Response summary: {summary}";
    }

    private sealed record ErplyConfig(string Url, string? ClientCode, int? TimeoutSeconds);
}
