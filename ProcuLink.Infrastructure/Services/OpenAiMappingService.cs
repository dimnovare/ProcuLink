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

    // A single batch request packs at most this many lines so a large PO (500–1000
    // lines) never truncates the model's JSON output against the per-call token ceiling
    // and never burns the whole monthly cap in one shot. The cap is re-checked before
    // each chunk so we stop gracefully mid-way, leaving the rest for manual review.
    // Small orders (≤ BatchSize lines) are a single chunk → behaviour is unchanged.
    private const int BatchSize = 50;

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
            // Catalog allow-list: when this supplier has a product catalog, the candidates
            // carry the real codes and the model MUST select one of them — any code outside
            // the catalog is rejected after the call so a hallucinated code can never surface.
            // With no catalog the allow-list is empty → behaviour is unchanged (free suggestion).
            var catalog = BuildCatalogAllowList(candidates);

            var promptPayload = new
            {
                supplierName,
                line,
                candidates = candidates.Take(40).ToArray()
            };

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(catalog.HasCatalog ? CatalogSystemPrompt : FreeSystemPrompt),
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
            if (dto is null) return null;

            // Allow-list guard + concrete provenance (shared with the batch path). Returns
            // null when the code is empty OR (when a catalog is present) not a real catalog
            // code — so a hallucinated code can never surface. offer ⇔ works.
            var suggestion = ResolveSuggestion(
                catalog, dto.SupplierItemCode, dto.Confidence, dto.Reason, dto.Provenance);

            if (suggestion is null && catalog.HasCatalog && !string.IsNullOrWhiteSpace(dto.SupplierItemCode))
            {
                _logger.LogInformation(
                    "Rejected non-catalog AI suggestion {Code} for org {OrgId}, supplier {SupplierId}, line {LineNumber}",
                    dto.SupplierItemCode.Trim(), organisationId, supplierId, line.LineNumber);
            }

            return suggestion;
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

        // ── Chunked dispatch ─────────────────────────────────────────────────
        // A 500–1000-line PO is split into fixed batches so no single request
        // truncates against the token ceiling. The cap is re-checked before each
        // chunk; if it trips mid-way we stop and return what we already have,
        // leaving the remaining lines for manual review. A small order is one chunk.
        var merged = new Dictionary<int, AiMappingSuggestion>();
        var candidatePayload = candidates.Take(40).ToArray();

        // Catalog allow-list computed once for the whole order. When the supplier has a
        // product catalog the candidates carry the real codes; the model must select one
        // of them and any code outside it is rejected per chunk. No catalog → unchanged.
        var catalog = BuildCatalogAllowList(candidatePayload);

        foreach (var (offset, chunk) in ChunkLines(payloadLines, BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            if (tracker is not null)
            {
                try
                {
                    if (await tracker.IsAtOrOverLimitAsync(organisationId, ct))
                    {
                        _logger.LogWarning(
                            "OpenAI batch mapping stopped at chunk offset {Offset}/{Total} — " +
                            "org {OrgId} reached monthly token limit {Limit}; remaining lines go to manual review",
                            offset,
                            payloadLines.Count,
                            organisationId,
                            tracker.MonthlyLimit);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    // If the cap check itself fails we must not silently bypass the cap.
                    _logger.LogWarning(
                        ex,
                        "OpenAI cap check failed for org {OrgId}; stopping batch suggestion to be safe",
                        organisationId);
                    break;
                }
            }

            try
            {
                await SuggestChunkAsync(
                    organisationId, supplierId, supplierName, chunk, candidatePayload, catalog, tracker, merged, ct);
            }
            catch (Exception ex)
            {
                // A failed chunk shouldn't lose the suggestions already merged from
                // earlier chunks; log and move on so the remaining lines still get a shot.
                _logger.LogWarning(
                    ex,
                    "OpenAI batch mapping suggestion failed for org {OrgId}, supplier {SupplierId}, "
                        + "chunk offset {Offset} ({ChunkSize} lines)",
                    organisationId,
                    supplierId,
                    offset,
                    chunk.Count);
            }
        }

        return merged.Count == 0 ? empty : merged;
    }

    /// <summary>
    /// Issues one model call for a single chunk of unresolved lines and merges any
    /// usable suggestions into <paramref name="merged"/>. Token usage is recorded per
    /// chunk so the per-org cap stays accurate across a multi-chunk PO. Mirrors the
    /// single-request prompt/response shape, confidence/provenance handling, and
    /// requested-line filtering exactly — chunking only changes how many lines go per call.
    /// </summary>
    private async Task SuggestChunkAsync(
        Guid organisationId,
        Guid supplierId,
        string supplierName,
        IReadOnlyList<AiMappingLineContext> chunk,
        IReadOnlyList<AiMappingCandidate> candidatePayload,
        CatalogAllowList catalog,
        IAiUsageTracker? tracker,
        Dictionary<int, AiMappingSuggestion> merged,
        CancellationToken ct)
    {
        var promptPayload = new
        {
            supplierName,
            lines = chunk,
            candidates = candidatePayload
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(catalog.HasCatalog ? CatalogBatchSystemPrompt : FreeBatchSystemPrompt),
            new UserChatMessage(JsonSerializer.Serialize(promptPayload, JsonOptions))
        };

        // Scale the output budget with the chunk's line count, with a sane ceiling so a
        // single request can't blow up. ~120 tokens/line covers the four short string
        // fields per suggestion; BatchSize keeps this comfortably under the ceiling.
        var maxTokens = Math.Clamp(chunk.Count * 120, 350, 4000);

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = maxTokens,
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "supplier_item_code_suggestions",
                jsonSchema: BatchSuggestionSchema,
                jsonSchemaIsStrict: true)
        };

        ChatCompletion completion = await _client!.CompleteChatAsync(messages, options, ct);

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
            return;

        var dto = JsonSerializer.Deserialize<OpenAiBatchSuggestionEnvelope>(json, JsonOptions);
        if (dto?.Suggestions is null || dto.Suggestions.Count == 0)
            return;

        // Only keep suggestions whose lineNumber was actually in this chunk's request and
        // that carry a non-empty code. Last write wins if the model echoes a duplicate.
        var requestedLineNumbers = chunk.Select(l => l.LineNumber).ToHashSet();

        foreach (var s in dto.Suggestions)
        {
            if (!requestedLineNumbers.Contains(s.LineNumber)) continue;

            // Allow-list guard + concrete provenance (shared with the single-line path):
            // null when the code is empty OR (with a catalog) not a real catalog code.
            var suggestion = ResolveSuggestion(
                catalog, s.SupplierItemCode, s.Confidence, s.Reason, s.Provenance);

            if (suggestion is null)
            {
                if (catalog.HasCatalog && !string.IsNullOrWhiteSpace(s.SupplierItemCode))
                    _logger.LogInformation(
                        "Rejected non-catalog AI suggestion {Code} for org {OrgId}, supplier {SupplierId}, line {LineNumber}",
                        s.SupplierItemCode.Trim(), organisationId, supplierId, s.LineNumber);
                continue;
            }

            // Last write wins if the model echoes a duplicate lineNumber.
            merged[s.LineNumber] = suggestion;
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

    /// <summary>
    /// Partitions <paramref name="lines"/> into fixed-size, in-order batches, each tagged
    /// with its starting offset. Pure and side-effect free so the batching boundaries can
    /// be unit-tested without a model call. A list of ≤ <paramref name="batchSize"/> lines
    /// yields exactly one chunk (offset 0), keeping small-order behaviour unchanged.
    /// </summary>
    internal static IEnumerable<(int Offset, IReadOnlyList<AiMappingLineContext> Chunk)> ChunkLines(
        IReadOnlyList<AiMappingLineContext> lines, int batchSize)
    {
        if (batchSize < 1) batchSize = 1;
        for (var offset = 0; offset < lines.Count; offset += batchSize)
        {
            var take = Math.Min(batchSize, lines.Count - offset);
            var chunk = new List<AiMappingLineContext>(take);
            for (var i = 0; i < take; i++)
                chunk.Add(lines[offset + i]);
            yield return (offset, chunk);
        }
    }

    // ── Catalog grounding ────────────────────────────────────────────────────────
    // System prompts come in two flavours. Without a catalog the model free-forms a code
    // (today's behaviour). WITH a catalog the candidates are the supplier's REAL product
    // rows and the model MUST select one of them (the post-response allow-list guard then
    // rejects anything that slips through), so a hallucinated code can never reach delivery.

    private const string FreeSystemPrompt = """
        You suggest supplier item codes for unresolved B2B purchase-order lines.
        Use existing candidate mappings when they support the suggestion.
        If the evidence is weak, return an empty supplierItemCode and confidence 0.
        Never claim a mapping is confirmed. Suggestions are human review hints only.
        Keep reason and provenance concise.
        """;

    private const string CatalogSystemPrompt = """
        You match an unresolved B2B purchase-order line to the supplier's REAL product catalog.
        The candidates list contains the supplier's actual products (code + name + barcode + unit).
        You MUST set supplierItemCode to one of the supplierItemCode values present in candidates,
        choosing the closest real product to the buyer's line. You may NOT invent or modify a code.
        If no candidate is a reasonable match, return an empty supplierItemCode and confidence 0.
        Never claim a mapping is confirmed. Suggestions are human review hints only.
        Keep reason and provenance concise.
        """;

    private const string FreeBatchSystemPrompt = """
        You suggest supplier item codes for unresolved B2B purchase-order lines.
        The input contains multiple lines; return one suggestion object per line,
        echoing each line's lineNumber so the caller can match them.
        Use existing candidate mappings when they support a suggestion.
        If the evidence for a line is weak, return an empty supplierItemCode and
        confidence 0 for that line. Never claim a mapping is confirmed. Suggestions
        are human review hints only. Keep reason and provenance concise.
        """;

    private const string CatalogBatchSystemPrompt = """
        You match unresolved B2B purchase-order lines to the supplier's REAL product catalog.
        The candidates list contains the supplier's actual products (code + name + barcode + unit).
        The input contains multiple lines; return one suggestion object per line, echoing each
        line's lineNumber so the caller can match them.
        For each line you MUST set supplierItemCode to one of the supplierItemCode values present
        in candidates, choosing the closest real product. You may NOT invent or modify a code.
        If no candidate is a reasonable match for a line, return an empty supplierItemCode and
        confidence 0 for that line. Never claim a mapping is confirmed. Suggestions are human
        review hints only. Keep reason and provenance concise.
        """;

    /// <summary>
    /// The supplier's real catalog codes available for this request, derived from the
    /// catalog-flavoured candidates. <see cref="HasCatalog"/> is true only when at least one
    /// catalog candidate was supplied; that is the switch that activates the allow-list guard
    /// and the constrained prompt. Membership is case-insensitive + trimmed to match the way
    /// codes round-trip through the model. Carries the catalog rows so provenance can name the
    /// matched product (code + name).
    /// </summary>
    private sealed class CatalogAllowList
    {
        public static readonly CatalogAllowList None = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, AiMappingCandidate>(StringComparer.OrdinalIgnoreCase));

        private readonly HashSet<string> _codes;
        private readonly Dictionary<string, AiMappingCandidate> _byCode;

        private CatalogAllowList(HashSet<string> codes, Dictionary<string, AiMappingCandidate> byCode)
        {
            _codes = codes;
            _byCode = byCode;
        }

        public bool HasCatalog => _codes.Count > 0;

        public bool Contains(string? code) =>
            !string.IsNullOrWhiteSpace(code) && _codes.Contains(code.Trim());

        public AiMappingCandidate? Match(string? code) =>
            !string.IsNullOrWhiteSpace(code) && _byCode.TryGetValue(code.Trim(), out var row) ? row : null;

        public static CatalogAllowList From(IEnumerable<AiMappingCandidate> candidates)
        {
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byCode = new Dictionary<string, AiMappingCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates)
            {
                if (!c.IsCatalogProduct || string.IsNullOrWhiteSpace(c.SupplierItemCode)) continue;
                var code = c.SupplierItemCode.Trim();
                if (codes.Add(code)) byCode[code] = c;
            }
            return codes.Count == 0 ? None : new CatalogAllowList(codes, byCode);
        }
    }

    private static CatalogAllowList BuildCatalogAllowList(IEnumerable<AiMappingCandidate> candidates) =>
        CatalogAllowList.From(candidates);

    /// <summary>
    /// Turns one model-returned suggestion into a validated <see cref="AiMappingSuggestion"/>,
    /// or null when it must be dropped. The single decision point shared by the single-line and
    /// batch paths: (1) empty code → null; (2) when a catalog is present and the code is not a
    /// real catalog code → null (allow-list guard, blocks hallucinations); otherwise build the
    /// suggestion with clamped confidence and concrete catalog provenance. Pure + side-effect
    /// free so it is directly unit-testable without a network call.
    /// </summary>
    private static AiMappingSuggestion? ResolveSuggestion(
        CatalogAllowList catalog, string? suggestedCode, float confidence, string? reason, string? provenance)
    {
        if (string.IsNullOrWhiteSpace(suggestedCode)) return null;
        if (catalog.HasCatalog && !catalog.Contains(suggestedCode)) return null;

        return new AiMappingSuggestion(
            suggestedCode.Trim(),
            Math.Clamp(confidence, 0f, 1f),
            reason?.Trim() ?? string.Empty,
            BuildProvenance(catalog, suggestedCode, provenance));
    }

    /// <summary>
    /// Test seam: exercises the catalog allow-list guard + provenance exactly as the live
    /// paths do, building the allow-list from the same public <see cref="AiMappingCandidate"/>
    /// set the ingestion service passes in. Returns null when the suggestion would be dropped.
    /// </summary>
    internal static AiMappingSuggestion? ApplyCatalogGuardForTest(
        IEnumerable<AiMappingCandidate> candidates,
        string? suggestedCode,
        float confidence,
        string? reason,
        string? provenance) =>
        ResolveSuggestion(BuildCatalogAllowList(candidates), suggestedCode, confidence, reason, provenance);

    /// <summary>
    /// Builds a concrete, veteran-grade provenance string. When the suggested code matches a
    /// catalog row, names that row (code + name) instead of the vague model-supplied text —
    /// "Matched catalog product ES-RES-220R 'Resistor 220Ω 0.25W'". Falls back to the model's
    /// own provenance (then a default) when there is no catalog match.
    /// </summary>
    private static string BuildProvenance(CatalogAllowList catalog, string suggestedCode, string? modelProvenance)
    {
        var match = catalog.Match(suggestedCode);
        if (match is not null)
        {
            var code = match.SupplierItemCode.Trim();
            return string.IsNullOrWhiteSpace(match.Name)
                ? $"Matched catalog product {code}"
                : $"Matched catalog product {code} — \"{match.Name!.Trim()}\"";
        }

        var fromModel = modelProvenance?.Trim();
        return string.IsNullOrWhiteSpace(fromModel) ? "OpenAI structured output" : fromModel;
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
