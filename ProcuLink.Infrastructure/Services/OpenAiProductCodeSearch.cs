#pragma warning disable OPENAI001 // OpenAI Responses API (web_search tool) is marked experimental in 2.10.0.
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// T4 — external web/product-code grounding via the OpenAI Responses <c>web_search</c> tool
/// (already-referenced OpenAI 2.10.0 package; reuses <c>Ai:OpenAI:ApiKey</c>, no second vendor
/// key). Finds a real manufacturer part number for a described product. Provider-neutral behind
/// <see cref="IProductCodeSearch"/> — a future product-data API (Octopart/Icecat/GS1) drops in
/// behind the same seam.
///
/// <para><b>Safe by default.</b> Constructs a live client ONLY when the provider is openai, an
/// API key is set, AND the per-feature flag <c>Ai:OpenAI:ProductSearch:Enabled</c> is on.
/// Otherwise every call is a no-op (returns null, no network, no egress) — so the default deploy
/// is byte-identical and no PO data leaves the environment.</para>
///
/// <para><b>Honest, never authoritative.</b> Web results are plausible, not verified. The caller
/// folds a hit in as a non-catalog candidate that stays <c>NeedsReview</c> and is never
/// auto-applied; provenance is labelled "web product search (unverified)".</para>
///
/// <para><b>Cost.</b> Each call is a billable, seconds-long web search. The per-org monthly
/// token cap is enforced PRE-FLIGHT by the caller (it owns the org id; this contract carries
/// none), together with the feature flag, the no-egress org gate, and a per-line cap. Token
/// usage per call is logged for observability.</para>
/// </summary>
public sealed class OpenAiProductCodeSearch : IProductCodeSearch
{
    private const string DefaultModel = "gpt-5-mini";

    // Bound the model's output: we only need a tiny JSON object back. Web-search reasoning
    // happens server-side and is billed separately; this caps the completion tokens.
    private const int MaxOutputTokens = 600;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ResponsesClient? _client;
    private readonly ILogger<OpenAiProductCodeSearch> _logger;
    private readonly string _model;

    public OpenAiProductCodeSearch(
        IConfiguration configuration,
        ILogger<OpenAiProductCodeSearch> logger)
    {
        _logger = logger;
        _model = configuration["Ai:OpenAI:ProductSearch:Model"]
                 ?? configuration["Ai:OpenAI:MappingModel"]
                 ?? DefaultModel;

        var provider = configuration["Ai:Provider"];
        var apiKey = configuration["Ai:OpenAI:ApiKey"];
        var enabled = configuration.GetValue("Ai:OpenAI:ProductSearch:Enabled", false);

        if (enabled
            && string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new ResponsesClient(apiKey);
        }
    }

    public async Task<ProductCodeMatch?> FindPartNumberAsync(
        string description, string? brandHint, CancellationToken ct = default)
    {
        // No-op when unconfigured (flag off / wrong provider / no key) — the default deploy.
        if (_client is null) return null;
        if (string.IsNullOrWhiteSpace(description)) return null;

        try
        {
            var result = await _client.CreateResponseAsync(BuildOptions(_model, description, brandHint), ct);
            var response = result.Value;

            // Token usage is billable either way; log it for observability (the per-org cap is
            // enforced pre-flight by the caller, which owns the org id this contract lacks).
            var tokens = response.Usage?.TotalTokenCount ?? 0;
            if (tokens > 0)
                _logger.LogInformation("Product web search used {Tokens} tokens for model {Model}.", tokens, _model);

            return ParseMatch(response.GetOutputText());
        }
        catch (Exception ex)
        {
            // Never let an external search failure break ingest — degrade to "no suggestion".
            _logger.LogWarning(ex, "Product web search failed for description '{Description}'.", Truncate(description, 120));
            return null;
        }
    }

    /// <summary>
    /// Builds the Responses request. Pure seam (no network) so the request shape — above all
    /// <c>store:false</c> — is unit-testable.
    ///
    /// <para><b>Storage opt-out.</b> The Responses API persists request + response payloads on
    /// OpenAI's side by default (Chat Completions does not). The prompt carries customer PO line
    /// descriptions, so <see cref="CreateResponseOptions.StoredOutputEnabled"/> ("store" in the
    /// JSON payload) is set false on every call. Nothing here reads a stored response back, so
    /// opting out costs no functionality.</para>
    /// </summary>
    internal static CreateResponseOptions BuildOptions(string model, string description, string? brandHint)
    {
        var options = new CreateResponseOptions(
            model, new[] { ResponseItem.CreateUserMessageItem(BuildPrompt(description, brandHint)) });
        options.Tools.Add(ResponseTool.CreateWebSearchTool());
        options.MaxOutputTokenCount = MaxOutputTokens;
        options.StoredOutputEnabled = false;
        return options;
    }

    private static string BuildPrompt(string description, string? brandHint)
    {
        var brand = string.IsNullOrWhiteSpace(brandHint) ? string.Empty : $" Manufacturer or brand: {brandHint.Trim()}.";
        return
            "Find the exact manufacturer part number (MPN) or SKU for the product below. " +
            "Search the web and read manufacturer / retailer product pages to identify the real code. " +
            $"Product description: {description.Trim()}.{brand} " +
            "Respond with ONLY a compact JSON object and nothing else: " +
            "{\"partNumber\": string, \"title\": string, \"sourceUrl\": string, \"confidence\": number between 0 and 1}. " +
            "partNumber must be the real manufacturer part number you found, copied verbatim from the source. " +
            "If you cannot find a real part number with reasonable confidence, set partNumber to an empty string.";
    }

    /// <summary>
    /// Pure parse seam (no network): extracts the JSON object from the model's text — tolerating
    /// a ```json code fence or surrounding prose — and maps it to a <see cref="ProductCodeMatch"/>.
    /// Returns null when the text has no JSON object, fails to parse, or carries an empty
    /// <c>partNumber</c>. Confidence is clamped to [0,1]. Unit-testable without an API call.
    /// </summary>
    internal static ProductCodeMatch? ParseMatch(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Web-search models often wrap the JSON in a fence or add a sentence; isolate the object.
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = raw.Substring(start, end - start + 1);

        ProductSearchDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProductSearchDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.PartNumber)) return null;

        return new ProductCodeMatch(
            PartNumber: dto.PartNumber.Trim(),
            Title:      string.IsNullOrWhiteSpace(dto.Title) ? null : dto.Title!.Trim(),
            SourceUrl:  string.IsNullOrWhiteSpace(dto.SourceUrl) ? null : dto.SourceUrl!.Trim(),
            Confidence: Math.Clamp(dto.Confidence, 0f, 1f));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private sealed record ProductSearchDto(
        string? PartNumber,
        string? Title,
        string? SourceUrl,
        float Confidence);
}
