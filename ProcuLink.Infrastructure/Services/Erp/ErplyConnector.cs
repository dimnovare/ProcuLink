using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Erp;
using ProcuLink.Infrastructure.Services.Security;

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

            // A config saved before TLS enforcement keeps delivering rather than stranding the
            // order, but never silently. The URL is not logged whole — it may carry credentials.
            if (DeliveryConfigTransport.DescribeInsecureTransport(
                    request.Config.Protocol, request.Config.ConfigJson) is { } insecure)
            {
                _logger.LogWarning(
                    "Erply delivery for supplier {SupplierId} uses a transport that no longer "
                    + "passes policy (scheme '{Scheme}', host '{Host}'). {Warning}",
                    request.Config.SupplierId, endpoint.Scheme, endpoint.Host, insecure);
            }

            var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(request.Content)
            };

            message.Content.Headers.ContentType =
                MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
                    ? mediaType
                    : new MediaTypeHeaderValue("application/octet-stream");

            // request.FileName and cfg.ClientCode are tenant-supplied and flow into header
            // VALUES — validate for CR/LF/NUL injection before adding (the header names are
            // fixed constants and always valid).
            if (!HttpHeaderGuard.TryAdd(message.Headers, "X-ProcuLink-FileName", request.FileName))
                _logger.LogWarning("Skipping X-ProcuLink-FileName header (value failed CR/LF validation).");
            if (!string.IsNullOrWhiteSpace(cfg.ClientCode)
                && !HttpHeaderGuard.TryAdd(message.Headers, "X-Erply-Client-Code", cfg.ClientCode))
                _logger.LogWarning("Skipping X-Erply-Client-Code header (value failed CR/LF validation).");

            ApplyAuth(message, request.DecryptedCredentials, _logger);

            using var timeoutCts = cfg.TimeoutSeconds is > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds!.Value));
            var requestCt = timeoutCts?.Token ?? ct;

            var response = await _httpClientFactory.CreateClient("delivery").SendAsync(message, requestCt);
            var body = await response.Content.ReadAsStringAsync(requestCt);
            var code = (int)response.StatusCode;

            // The body is carried VERBATIM as well as summarised: the summary is for the operator,
            // the original is what SupplierResponseClassification reads to tell a refusal OF THE
            // DOCUMENT from a refusal of the request. Dropping it (as this did) made every ERP 400
            // look unexplained, so it was re-dispatched to an endpoint that cannot de-duplicate.
            return response.IsSuccessStatusCode
                ? new ErpDeliveryResult(true, null, code)
                : new ErpDeliveryResult(
                    false, BuildFailureMessage("Erply", code, body), code,
                    ResponseBody: NullIfBlank(body),
                    RetryAfter: RetryAfterHeader.Read(response.Headers, DateTimeOffset.UtcNow));
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

    private static void ApplyAuth(HttpRequestMessage request, string credentialsJson, ILogger logger)
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
            // The apikey header name+value come from tenant-stored credentials; validate for
            // CR/LF/NUL/control-char injection before adding, dropping (and logging) on failure.
            var headerName = header.GetString()!;
            if (!HttpHeaderGuard.TryAdd(request.Headers, headerName, value.GetString()))
                logger.LogWarning(
                    "Skipping invalid Erply apikey auth header name '{HeaderName}' (failed CR/LF/token validation).",
                    headerName);
        }
    }

    private static string? NullIfBlank(string? body) =>
        string.IsNullOrWhiteSpace(body) ? null : body;

    private static string BuildFailureMessage(string connector, int code, string body)
    {
        // The guarded transport refuses redirects — a 307 replays this request body, and any
        // apikey-style credential header, to a host nobody configured. Name that, and the fix.
        if (OutboundRedirectPolicy.IsRedirect(code))
            return $"{connector} {OutboundRedirectPolicy.DescribeRefusal(code)}";

        if (string.IsNullOrWhiteSpace(body))
            return $"{connector} HTTP {code}: ERP endpoint returned an error.";

        var summary = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (summary.Length > 120)
            summary = summary[..120];

        return $"{connector} HTTP {code}: ERP endpoint returned an error. Response summary: {summary}";
    }

    private sealed record ErplyConfig(string Url, string? ClientCode, int? TimeoutSeconds);
}
