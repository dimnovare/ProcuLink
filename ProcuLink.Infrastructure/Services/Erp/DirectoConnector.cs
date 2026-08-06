using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Erp;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Services.Erp;

public sealed class DirectoConnector : IErpConnector
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DirectoConnector> _logger;

    public string Protocol => DeliveryProtocolConstants.ErpDirecto;

    public DirectoConnector(IHttpClientFactory httpClientFactory, ILogger<DirectoConnector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ErpDeliveryResult> SendAsync(ErpDeliveryRequest request, CancellationToken ct)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<DirectoConfig>(request.Config.ConfigJson, JsonOpts);
            if (cfg is null || string.IsNullOrWhiteSpace(cfg.Url) || string.IsNullOrWhiteSpace(cfg.Database))
                return new ErpDeliveryResult(false, "Directo connector configuration is invalid.");

            if (!Uri.TryCreate(cfg.Url, UriKind.Absolute, out var endpoint))
                return new ErpDeliveryResult(false, "Directo connector endpoint URL is invalid.");

            // This request posts `user`, `password` and `key` as form fields. A config saved
            // before TLS enforcement keeps delivering rather than stranding the order, but never
            // silently. The URL is not logged whole — it may itself carry credentials.
            if (DeliveryConfigTransport.DescribeInsecureTransport(
                    request.Config.Protocol, request.Config.ConfigJson) is { } insecure)
            {
                _logger.LogWarning(
                    "Directo delivery for supplier {SupplierId} uses a transport that no longer "
                    + "passes policy (scheme '{Scheme}', host '{Host}'); the credentials in this "
                    + "request are exposed on the network path. {Warning}",
                    request.Config.SupplierId, endpoint.Scheme, endpoint.Host, insecure);
            }

            var creds = ParseCredentials(request.DecryptedCredentials);
            var fields = new List<KeyValuePair<string, string>>
            {
                new("database", cfg.Database),
                new("filename", request.FileName),
                new("contentType", request.ContentType),
                new("xmldata", System.Text.Encoding.UTF8.GetString(request.Content))
            };

            if (!string.IsNullOrWhiteSpace(creds.User))
                fields.Add(new("user", creds.User));
            if (!string.IsNullOrWhiteSpace(creds.Password))
                fields.Add(new("password", creds.Password));
            if (!string.IsNullOrWhiteSpace(creds.Key))
                fields.Add(new("key", creds.Key));

            var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(fields)
            };

            using var timeoutCts = cfg.TimeoutSeconds is > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds!.Value));
            var requestCt = timeoutCts?.Token ?? ct;

            var response = await _httpClientFactory.CreateClient("delivery").SendAsync(message, requestCt);
            var body = await response.Content.ReadAsStringAsync(requestCt);
            var code = (int)response.StatusCode;

            // Verbatim body as well as the operator summary — see the sibling comment in
            // ErplyConnector: the classification's 400 split reads the original, not the summary.
            return response.IsSuccessStatusCode
                ? new ErpDeliveryResult(true, null, code)
                : new ErpDeliveryResult(
                    false, BuildFailureMessage(code, body), code,
                    ResponseBody: string.IsNullOrWhiteSpace(body) ? null : body,
                    RetryAfter: RetryAfterHeader.Read(response.Headers, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ErpDeliveryResult(false, "Directo connector timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Directo connector failed before a response was received.");
            return new ErpDeliveryResult(false, "Directo connector failed before receiving a response.");
        }
    }

    private static DirectoCredentials ParseCredentials(string credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
            return new DirectoCredentials(null, null, null);

        using var doc = JsonDocument.Parse(credentialsJson);
        var root = doc.RootElement;

        return new DirectoCredentials(
            root.TryGetProperty("user", out var user) ? user.GetString() : null,
            root.TryGetProperty("password", out var password) ? password.GetString() : null,
            root.TryGetProperty("key", out var key) ? key.GetString() : null);
    }

    private static string BuildFailureMessage(int code, string body)
    {
        // The guarded transport refuses redirects (a 307 would replay this form body — which
        // carries `user` and `password` — to a host nobody configured). Name that, and the fix.
        if (OutboundRedirectPolicy.IsRedirect(code))
            return $"Directo {OutboundRedirectPolicy.DescribeRefusal(code)}";

        if (string.IsNullOrWhiteSpace(body))
            return $"Directo HTTP {code}: ERP endpoint returned an error.";

        var summary = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (summary.Length > 120)
            summary = summary[..120];

        return $"Directo HTTP {code}: ERP endpoint returned an error. Response summary: {summary}";
    }

    private sealed record DirectoConfig(string Url, string Database, int? TimeoutSeconds);
    private sealed record DirectoCredentials(string? User, string? Password, string? Key);
}
