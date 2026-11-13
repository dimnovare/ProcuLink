using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Documents;

namespace ProcuLink.Api.Services.Documents;

public sealed class ValidatorOptions
{
    public string? BaseUrl { get; set; }
    public string? Token { get; set; }
}

/// <summary>
/// Rule checker client for the phorm sidecar (phive + the official Peppol rules). Uses its own named
/// client, never "delivery": that one's SSRF guard refuses the private address the sidecar lives on.
/// An outage must not stop sending, so every failure path answers Unavailable.
/// </summary>
public sealed class PhormValidator(IHttpClientFactory clients, IOptions<ValidatorOptions> options, ILogger<PhormValidator> log) : IStandardValidator
{
    public const string HttpClientName = "validator";

    private static readonly ValidationOutcome Down = new(ValidationStatus.Unavailable, []);

    public async Task<ValidationOutcome> ValidateAsync(string vesid, byte[] xml, CancellationToken ct)
    {
        var baseUrl = options.Value.BaseUrl?.Trim();
        if (string.IsNullOrEmpty(baseUrl)) return Unavailable("no validator URL is configured");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/validate/{vesid}")
            {
                Content = new ByteArrayContent(xml)
            };
            // phorm answers 200 "Failed to read the message body as XML" without this content type.
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(options.Value.Token)) request.Headers.Add("X-Token", options.Value.Token);

            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return Unavailable($"validator answered {(int)response.StatusCode}");

            return Parse(await response.Content.ReadAsStringAsync(ct)) ?? Unavailable("validator answer was not understood");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Unavailable(ex.GetType().Name); } // timeout, bad URL, network: never stop sending
    }

    private ValidationOutcome Unavailable(string reason)
    {
        log.LogWarning("Rule checker unavailable: {Reason}", reason);
        return Down;
    }

    /// <summary>Reads only success, and per item errorLevel / errorID / errorText. Null when the body is not a result.</summary>
    private static ValidationOutcome? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var ok)
                || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;

            var issues = new List<ValidationIssue>();
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                foreach (var result in results.EnumerateArray())
                {
                    if (!result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                    var artifact = Str(result, "artifactType");
                    foreach (var item in items.EnumerateArray())
                    {
                        var rule = Str(item, "errorID");
                        issues.Add(new ValidationIssue(rule.Length > 0 ? rule : artifact, Severity(Str(item, "errorLevel")), Str(item, "errorText")));
                    }
                }

            var failed = !ok.GetBoolean() || issues.Any(i => i.Severity == "error");
            return new ValidationOutcome(failed ? ValidationStatus.Failed : ValidationStatus.Passed, issues);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; } // e.g. a non-object item
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Severity(string level) => level.ToUpperInvariant() switch
    {
        "ERROR" or "FATAL_ERROR" => "error",
        "WARN" or "WARNING" => "warning",
        _ => "info"
    };
}
