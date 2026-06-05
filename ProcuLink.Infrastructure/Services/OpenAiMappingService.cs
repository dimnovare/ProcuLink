using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Infrastructure.Services;

public sealed class OpenAiMappingService : IAiMappingService
{
    private const string DefaultModel = "gpt-5-mini";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly BinaryData SuggestionSchema = BinaryData.FromBytes("""
        {
          "type": "object",
          "properties": {
            "supplierItemCode": {
              "type": "string",
              "description": "Suggested supplier item code. Empty string when no useful suggestion exists."
            },
            "confidence": {
              "type": "number",
              "minimum": 0,
              "maximum": 1
            },
            "reason": {
              "type": "string",
              "description": "Short operational explanation for a procurement user."
            },
            "provenance": {
              "type": "string",
              "description": "Evidence used, such as existing mapping rows or buyer code/description signals."
            }
          },
          "required": ["supplierItemCode", "confidence", "reason", "provenance"],
          "additionalProperties": false
        }
        """u8.ToArray());

    private static readonly BinaryData BatchSuggestionSchema = BinaryData.FromBytes("""
        {
          "type": "object",
          "properties": {
            "suggestions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "lineNumber": {
                    "type": "integer",
                    "description": "The lineNumber from the input line this suggestion is for."
                  },
                  "supplierItemCode": {
                    "type": "string",
                    "description": "Suggested supplier item code. Empty string when no useful suggestion exists."
                  },
                  "confidence": {
                    "type": "number",
                    "minimum": 0,
                    "maximum": 1
                  },
                  "reason": {
                    "type": "string",
                    "description": "Short operational explanation for a procurement user."
                  },
                  "provenance": {
                    "type": "string",
                    "description": "Evidence used, such as existing mapping rows or buyer code/description signals."
                  }
                },
                "required": ["lineNumber", "supplierItemCode", "confidence", "reason", "provenance"],
                "additionalProperties": false
              }
            }
          },
          "required": ["suggestions"],
          "additionalProperties": false
        }
        """u8.ToArray());

    private static readonly BinaryData FieldMappingSchema = BinaryData.FromBytes("""
        {
          "type": "object",
          "properties": {
            "mappings": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "canonicalField": { "type": "string" },
                  "suggestedColumn": { "type": "string" },
                  "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
                  "reason": { "type": "string" }
                },
                "required": ["canonicalField", "suggestedColumn", "confidence", "reason"],
                "additionalProperties": false
              }
            }
          },
          "required": ["mappings"],
          "additionalProperties": false
        }
        """u8.ToArray());

    private readonly ChatClient? _client;
    private readonly ILogger<OpenAiMappingService> _logger;
    private readonly string _model;
    // The tracker is a scoped EF service; OpenAiMappingService itself is a singleton
    // in the API/Worker DI containers, so we resolve a tracker per-call from the
    // provided scope factory. When no scope factory is wired (tests, no-op path),
    // we fall back to the inner factory instead.
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Func<IAiUsageTracker?>? _trackerFactory;
    // Test seam: lets a unit test force the no-egress short-circuit without a DbContext.
    // In production this stays null and the org flag is read from a per-call scoped DbContext.
    private readonly Func<Guid, CancellationToken, Task<bool>>? _noEgressCheck;

    public OpenAiMappingService(
        IConfiguration configuration,
        ILogger<OpenAiMappingService> logger,
        IServiceScopeFactory? scopeFactory = null)
    {
        _logger = logger;
        _model = configuration["Ai:OpenAI:MappingModel"] ?? DefaultModel;

        var provider = configuration["Ai:Provider"];
        var apiKey = configuration["Ai:OpenAI:ApiKey"];

        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new ChatClient(_model, apiKey);
        }

        _scopeFactory = scopeFactory;
        _trackerFactory = null;
        _noEgressCheck = null;
    }

    /// <summary>
    /// Test-only ctor: lets tests inject a deterministic <see cref="IAiUsageTracker"/>
    /// without spinning up an <see cref="IServiceScopeFactory"/>. Also lets tests
    /// inject a custom <see cref="ChatClient"/> stand-in by overriding the API key
    /// presence check (set <paramref name="overrideClient"/> to a non-null value).
    /// </summary>
    internal OpenAiMappingService(
        IConfiguration configuration,
        ILogger<OpenAiMappingService> logger,
        IAiUsageTracker? tracker,
        ChatClient? overrideClient = null,
        Func<Guid, CancellationToken, Task<bool>>? noEgressCheck = null)
    {
        _logger = logger;
        _model = configuration["Ai:OpenAI:MappingModel"] ?? DefaultModel;

        var provider = configuration["Ai:Provider"];
        var apiKey = configuration["Ai:OpenAI:ApiKey"];

        if (overrideClient is not null)
        {
            _client = overrideClient;
        }
        else if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new ChatClient(_model, apiKey);
        }

        _scopeFactory = null;
        _trackerFactory = () => tracker;
        _noEgressCheck = noEgressCheck;
    }

    /// <summary>
    /// True when the org opted into no-egress (<c>Organisation.SelfHostedOcr</c>) and
    /// must therefore never have its mapping data (line codes/descriptions or source
    /// column headers) sent to OpenAI. This is the single chokepoint that keeps the
    /// no-egress guarantee whole for EVERY <see cref="IAiMappingService"/> caller —
    /// including the "magic auto-map" field suggester. Fails SAFE: if the flag cannot
    /// be read we treat the org as no-egress (skip OpenAI), mirroring the cap-check's
    /// fail-closed behaviour. Returns false for the no-tenant (<see cref="Guid.Empty"/>)
    /// case so the existing no-key / cap tests are unaffected. The flag is read from the
    /// same per-call scope used for the usage tracker (this service is a singleton, so it
    /// cannot hold a scoped DbContext directly).
    /// </summary>
    private async Task<bool> IsNoEgressOrgAsync(
        IServiceProvider? scopedProvider, Guid organisationId, CancellationToken ct)
    {
        if (organisationId == Guid.Empty) return false;
        try
        {
            if (_noEgressCheck is not null) return await _noEgressCheck(organisationId, ct);
            var db = scopedProvider?.GetService<ProcuLinkDbContext>();
            if (db is null) return false;
            return await db.Organisations
                .AsNoTracking()
                .Where(o => o.Id == organisationId)
                .Select(o => o.SelfHostedOcr)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "No-egress check failed for org {OrgId}; skipping OpenAI mapping to be safe.", organisationId);
            return true;
        }
    }

    public async Task<AiMappingSuggestion?> SuggestSupplierItemCodeAsync(
        Guid organisationId,
        Guid supplierId,
        string supplierName,
        AiMappingLineContext line,
        IReadOnlyList<AiMappingCandidate> candidates,
        CancellationToken ct = default)
    {
        if (_client is null)
            return null;

        if (string.IsNullOrWhiteSpace(line.BuyerItemCode)
            && string.IsNullOrWhiteSpace(line.Description))
        {
            return null;
        }

        // ── Per-org monthly token cap ────────────────────────────────────────
        // Resolved per-call so the EF DbContext is short-lived and so test
        // doubles can be injected via the internal ctor.
        await using var trackerScope = _scopeFactory?.CreateAsyncScope();
        var tracker = trackerScope is not null
            ? trackerScope.Value.ServiceProvider.GetService<IAiUsageTracker>()
            : _trackerFactory?.Invoke();

        // No-egress orgs never send line data to OpenAI (single chokepoint for IAiMappingService).
        if (await IsNoEgressOrgAsync(trackerScope?.ServiceProvider, organisationId, ct))
        {
            _logger.LogInformation(
                "OpenAI mapping skipped — org {OrgId} is no-egress (self-hosted OCR).", organisationId);
            return null;
        }

        if (tracker is not null)
        {
            try
            {
                if (await tracker.IsAtOrOverLimitAsync(organisationId, ct))
                {
                    _logger.LogWarning(
                        "OpenAI mapping skipped — org {OrgId} reached monthly token limit {Limit}",
                        organisationId,
                        tracker.MonthlyLimit);
                    return null;
                }
            }
            catch (Exception ex)
            {
                // If the cap check itself fails we must not silently bypass the
                // cap. Treat it as a no-op and warn.
                _logger.LogWarning(
                    ex,
                    "OpenAI cap check failed for org {OrgId}; skipping suggestion to be safe",
                    organisationId);
                return null;
            }
        }

        try
        {
            var promptPayload = new
            {
                supplierName,
                line,
                candidates = candidates.Take(40).ToArray()
            };

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("""
                    You suggest supplier item codes for unresolved B2B purchase-order lines.
                    Use existing candidate mappings when they support the suggestion.
                    If the evidence is weak, return an empty supplierItemCode and confidence 0.
                    Never claim a mapping is confirmed. Suggestions are human review hints only.
                    Keep reason and provenance concise.
                    """),
                new UserChatMessage(JsonSerializer.Serialize(promptPayload, JsonOptions))
            };

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 350,
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "supplier_item_code_suggestion",
                    jsonSchema: SuggestionSchema,
                    jsonSchemaIsStrict: true)
            };

            ChatCompletion completion = await _client.CompleteChatAsync(messages, options, ct);

            // Record token usage regardless of whether the structured output
            // produced a usable suggestion — the call was billable either way.
            if (tracker is not null)
            {
                try
                {
                    var totalTokens = completion.Usage?.TotalTokenCount ?? 0;
                    if (totalTokens > 0)
                        await tracker.IncrementAsync(organisationId, totalTokens, ct);
                }
                catch (Exception incEx)
                {
                    _logger.LogWarning(
                        incEx,
                        "Failed to record AI token usage for org {OrgId}",
                        organisationId);
                }
            }

            var json = completion.Content.FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(json))
                return null;

            var dto = JsonSerializer.Deserialize<OpenAiSuggestionDto>(json, JsonOptions);
            if (dto is null || string.IsNullOrWhiteSpace(dto.SupplierItemCode))
                return null;

            var confidence = Math.Clamp(dto.Confidence, 0f, 1f);
            return new AiMappingSuggestion(
                dto.SupplierItemCode.Trim(),
                confidence,
                dto.Reason?.Trim() ?? string.Empty,
                dto.Provenance?.Trim() ?? "OpenAI structured output");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "OpenAI mapping suggestion failed for org {OrgId}, supplier {SupplierId}, line {LineNumber}",
                organisationId,
                supplierId,
                line.LineNumber);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<int, AiMappingSuggestion>> SuggestSupplierItemCodesAsync(
        Guid organisationId,
        Guid supplierId,
        string supplierName,
        IReadOnlyList<AiMappingLineContext> lines,
        IReadOnlyList<AiMappingCandidate> candidates,
        CancellationToken ct = default)
    {
        var empty = (IReadOnlyDictionary<int, AiMappingSuggestion>)
            new Dictionary<int, AiMappingSuggestion>();

        if (_client is null)
            return empty;

        // Only send lines that carry usable signal — matches the single-line guard.
        var payloadLines = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.BuyerItemCode)
                        || !string.IsNullOrWhiteSpace(l.Description))
            .ToList();

        if (payloadLines.Count == 0)
            return empty;

        // ── Per-org monthly token cap (same pattern as the single-line suggester) ──
        await using var trackerScope = _scopeFactory?.CreateAsyncScope();
        var tracker = trackerScope is not null
            ? trackerScope.Value.ServiceProvider.GetService<IAiUsageTracker>()
            : _trackerFactory?.Invoke();

        // No-egress orgs never send line data to OpenAI (single chokepoint for IAiMappingService).
        if (await IsNoEgressOrgAsync(trackerScope?.ServiceProvider, organisationId, ct))
        {
            _logger.LogInformation(
                "OpenAI batch mapping skipped — org {OrgId} is no-egress (self-hosted OCR).", organisationId);
            return empty;
        }

        if (tracker is not null)
        {
            try
            {
                if (await tracker.IsAtOrOverLimitAsync(organisationId, ct))
                {
                    _logger.LogWarning(
                        "OpenAI batch mapping skipped — org {OrgId} reached monthly token limit {Limit}",
                        organisationId,
                        tracker.MonthlyLimit);
                    return empty;
                }
            }
            catch (Exception ex)
            {
                // If the cap check itself fails we must not silently bypass the cap.
                _logger.LogWarning(
                    ex,
                    "OpenAI cap check failed for org {OrgId}; skipping batch suggestion to be safe",
                    organisationId);
                return empty;
            }
        }

        try
        {
            var promptPayload = new
            {
                supplierName,
                lines = payloadLines,
                candidates = candidates.Take(40).ToArray()
            };

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("""
                    You suggest supplier item codes for unresolved B2B purchase-order lines.
                    The input contains multiple lines; return one suggestion object per line,
                    echoing each line's lineNumber so the caller can match them.
                    Use existing candidate mappings when they support a suggestion.
                    If the evidence for a line is weak, return an empty supplierItemCode and
                    confidence 0 for that line. Never claim a mapping is confirmed. Suggestions
                    are human review hints only. Keep reason and provenance concise.
                    """),
                new UserChatMessage(JsonSerializer.Serialize(promptPayload, JsonOptions))
            };

            // Scale the output budget with the line count, with a sane ceiling so a
            // pathological order can't blow up the request. ~120 tokens/line covers the
            // four short string fields per suggestion.
            var maxTokens = Math.Clamp(payloadLines.Count * 120, 350, 4000);

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = maxTokens,
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "supplier_item_code_suggestions",
                    jsonSchema: BatchSuggestionSchema,
                    jsonSchemaIsStrict: true)
            };

            ChatCompletion completion = await _client.CompleteChatAsync(messages, options, ct);

            // Record token usage regardless of whether the structured output produced a
            // usable suggestion — the call was billable either way.
            if (tracker is not null)
            {
                try
                {
                    var totalTokens = completion.Usage?.TotalTokenCount ?? 0;
                    if (totalTokens > 0)
                        await tracker.IncrementAsync(organisationId, totalTokens, ct);
                }
                catch (Exception incEx)
                {
                    _logger.LogWarning(
                        incEx,
                        "Failed to record AI token usage for org {OrgId}",
                        organisationId);
                }
            }

            var json = completion.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json))
                return empty;

            var dto = JsonSerializer.Deserialize<OpenAiBatchSuggestionEnvelope>(json, JsonOptions);
            if (dto?.Suggestions is null || dto.Suggestions.Count == 0)
                return empty;

            // Only keep suggestions whose lineNumber was actually in the request and that
            // carry a non-empty code. Last write wins if the model echoes a duplicate.
            var requestedLineNumbers = payloadLines.Select(l => l.LineNumber).ToHashSet();
            var result = new Dictionary<int, AiMappingSuggestion>();

            foreach (var s in dto.Suggestions)
            {
                if (!requestedLineNumbers.Contains(s.LineNumber)) continue;
                if (string.IsNullOrWhiteSpace(s.SupplierItemCode)) continue;

                result[s.LineNumber] = new AiMappingSuggestion(
                    s.SupplierItemCode.Trim(),
                    Math.Clamp(s.Confidence, 0f, 1f),
                    s.Reason?.Trim() ?? string.Empty,
                    s.Provenance?.Trim() ?? "OpenAI structured output");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "OpenAI batch mapping suggestion failed for org {OrgId}, supplier {SupplierId}, {LineCount} lines",
                organisationId,
                supplierId,
                payloadLines.Count);
            return empty;
        }
    }

    public async Task<IReadOnlyList<AiFieldMappingSuggestion>> SuggestFieldMappingsAsync(
        Guid organisationId,
        Guid supplierId,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> unresolvedCanonicalFields,
        CancellationToken ct = default)
    {
        if (_client is null)
            return Array.Empty<AiFieldMappingSuggestion>();

        if (columns.Count == 0 || unresolvedCanonicalFields.Count == 0)
            return Array.Empty<AiFieldMappingSuggestion>();

        // ── Per-org monthly token cap (same pattern as the line suggester) ───
        await using var trackerScope = _scopeFactory?.CreateAsyncScope();
        var tracker = trackerScope is not null
            ? trackerScope.Value.ServiceProvider.GetService<IAiUsageTracker>()
            : _trackerFactory?.Invoke();

        // No-egress orgs never send source column headers to OpenAI. This closes the
        // "magic auto-map" field-suggestion touchpoint so the no-egress guarantee is
        // whole; AiAugmentedFieldMappingSuggester degrades to heuristic-only on empty.
        if (await IsNoEgressOrgAsync(trackerScope?.ServiceProvider, organisationId, ct))
        {
            _logger.LogInformation(
                "OpenAI field mapping skipped — org {OrgId} is no-egress (self-hosted OCR).", organisationId);
            return Array.Empty<AiFieldMappingSuggestion>();
        }

        if (tracker is not null)
        {
            try
            {
                if (await tracker.IsAtOrOverLimitAsync(organisationId, ct))
                {
                    _logger.LogWarning(
                        "OpenAI field mapping skipped — org {OrgId} reached monthly token limit {Limit}",
                        organisationId,
                        tracker.MonthlyLimit);
                    return Array.Empty<AiFieldMappingSuggestion>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "OpenAI cap check failed for org {OrgId}; skipping field mapping to be safe",
                    organisationId);
                return Array.Empty<AiFieldMappingSuggestion>();
            }
        }

        try
        {
            var promptPayload = new
            {
                sourceColumns = columns.Take(100).ToArray(),
                canonicalFieldsNeedingMapping = unresolvedCanonicalFields.Take(20).ToArray()
            };

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("""
                    You map a supplier's purchase-order file column headers to ProcuLink's
                    canonical PO fields. For each canonical field that still needs a mapping,
                    pick the single best matching source column from the provided list.
                    Only choose a column when the evidence is reasonable; if no column fits a
                    field, omit that field from the result. Use confidence 0..1.
                    suggestedColumn MUST be one of the provided source columns verbatim.
                    Keep each reason to a short phrase.
                    """),
                new UserChatMessage(JsonSerializer.Serialize(promptPayload, JsonOptions))
            };

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 600,
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "field_mapping_suggestions",
                    jsonSchema: FieldMappingSchema,
                    jsonSchemaIsStrict: true)
            };

            ChatCompletion completion = await _client.CompleteChatAsync(messages, options, ct);

            if (tracker is not null)
            {
                try
                {
                    var totalTokens = completion.Usage?.TotalTokenCount ?? 0;
                    if (totalTokens > 0)
                        await tracker.IncrementAsync(organisationId, totalTokens, ct);
                }
                catch (Exception incEx)
                {
                    _logger.LogWarning(
                        incEx,
                        "Failed to record AI token usage for org {OrgId}",
                        organisationId);
                }
            }

            var json = completion.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<AiFieldMappingSuggestion>();

            var dto = JsonSerializer.Deserialize<OpenAiFieldMappingEnvelope>(json, JsonOptions);
            if (dto?.Mappings is null || dto.Mappings.Count == 0)
                return Array.Empty<AiFieldMappingSuggestion>();

            // Only trust columns the model was actually given (strict schema can't
            // enforce an enum here because columns are dynamic).
            var allowedColumns = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);

            return dto.Mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.CanonicalField)
                            && !string.IsNullOrWhiteSpace(m.SuggestedColumn)
                            && allowedColumns.Contains(m.SuggestedColumn.Trim()))
                .Select(m => new AiFieldMappingSuggestion(
                    m.CanonicalField.Trim(),
                    m.SuggestedColumn.Trim(),
                    Math.Clamp(m.Confidence, 0f, 1f),
                    m.Reason?.Trim() ?? string.Empty))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "OpenAI field mapping suggestion failed for org {OrgId}, supplier {SupplierId}",
                organisationId,
                supplierId);
            return Array.Empty<AiFieldMappingSuggestion>();
        }
    }

    private sealed record OpenAiSuggestionDto(
        string SupplierItemCode,
        float Confidence,
        string? Reason,
        string? Provenance);

    private sealed record OpenAiBatchSuggestionEnvelope(
        List<OpenAiBatchSuggestionDto>? Suggestions);

    private sealed record OpenAiBatchSuggestionDto(
        int LineNumber,
        string SupplierItemCode,
        float Confidence,
        string? Reason,
        string? Provenance);

    private sealed record OpenAiFieldMappingEnvelope(
        List<OpenAiFieldMappingDto>? Mappings);

    private sealed record OpenAiFieldMappingDto(
        string CanonicalField,
        string SuggestedColumn,
        float Confidence,
        string? Reason);
}
