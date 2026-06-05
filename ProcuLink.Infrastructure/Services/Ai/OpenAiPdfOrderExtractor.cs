using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Infrastructure.Services.Ai;

/// <summary>
/// Primary PDF parse path: extracts the PDF text layer (PdfPig) and structures it
/// into a canonical <see cref="ExtractedOrder"/> via an OpenAI strict-JSON call.
///
/// Design notes:
///   • Singleton-safe — the organisation id is a method parameter and the scoped
///     <see cref="IAiUsageTracker"/> is resolved per-call from an
///     <see cref="IServiceScopeFactory"/>, so the same instance works in BOTH the
///     API and the background Worker host (mirrors <c>OpenAiMappingService</c>).
///   • No-op (Success=false) when <c>Ai:OpenAI:ApiKey</c> is missing or the
///     provider is not "openai" — the orchestrator then falls back to the
///     deterministic regex <c>PdfOrderParser</c>.
///   • Anti-hallucination validation: every emitted number must appear in the
///     source text and quantity × unit price must reconcile with the stated line
///     amount, otherwise the line is flagged for human review. (This is a
///     defense-in-depth net, not a correctness guarantee — number presence is
///     matched document-wide, so a hallucinated value that coincides with another
///     printed number can pass; the cross-check only runs when a line amount is
///     stated.)
///   • Never throws — all failure paths return Success=false.
/// </summary>
public sealed class OpenAiPdfOrderExtractor : IStructuredOrderExtractor
{
    private const string DefaultModel = "gpt-5-mini";
    internal const double ConfidenceThreshold = 0.6;
    private const int ExtractionMaxTokens = 4000;
    // Cap the text we send so a pathological multi-hundred-page PDF can't blow the
    // per-org monthly token cap in a single call. ~60k chars ≈ ~15k input tokens,
    // comfortably above any real multi-page PO/invoice.
    private const int MaxSourceChars = 60_000;
    private static readonly TimeSpan OpenAiCallTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // OpenAI strict mode requires every property listed in "required" AND
    // additionalProperties:false at every object level. "line_amount" is emitted
    // for arithmetic cross-checking only — it is NOT propagated to ExtractedOrder.
    private static readonly BinaryData ExtractionJsonSchema = BinaryData.FromBytes("""
        {
          "type": "object",
          "properties": {
            "confidence": { "type": "number" },
            "po_number":  { "type": "string" },
            "order_date": { "type": "string" },
            "currency":   { "type": "string" },
            "buyer_name": { "type": "string" },
            "lines": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "line_number":     { "type": "integer" },
                  "buyer_item_code": { "type": "string" },
                  "description":     { "type": "string" },
                  "quantity":        { "type": "number" },
                  "unit":            { "type": "string" },
                  "unit_price":      { "type": "number" },
                  "line_amount":     { "type": "number" }
                },
                "required": ["line_number", "buyer_item_code", "description", "quantity", "unit", "unit_price", "line_amount"],
                "additionalProperties": false
              }
            }
          },
          "required": ["confidence", "po_number", "order_date", "currency", "buyer_name", "lines"],
          "additionalProperties": false
        }
        """u8.ToArray());

    private const string SystemPrompt =
        "You extract a purchase order from the plain text of a supplier PDF. " +
        "Return ONLY structured data matching the schema. Copy numbers and codes " +
        "EXACTLY as printed — never invent, round, or compute a value that is not " +
        "in the text. Use the document's own line/item codes as buyer_item_code. " +
        "Leave a string field empty and a number 0 when the document does not state it. " +
        "Set line_amount to the printed line total (quantity x unit price) when shown, else 0. " +
        "Set confidence 0.0-1.0 based on how clearly the text is a real purchase order.";

    private readonly ChatClient? _client;
    private readonly ILogger<OpenAiPdfOrderExtractor> _logger;
    private readonly string _model;
    // The tracker is a scoped EF service; this extractor is a singleton, so we
    // resolve a tracker per-call from the provided scope factory. Tests inject a
    // tracker directly via the internal ctor instead.
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Func<IAiUsageTracker?>? _trackerFactory;

    public OpenAiPdfOrderExtractor(
        IConfiguration configuration,
        ILogger<OpenAiPdfOrderExtractor> logger,
        IServiceScopeFactory? scopeFactory = null)
    {
        _logger = logger;
        _model = configuration["Ai:OpenAI:ExtractionModel"]
                 ?? configuration["Ai:OpenAI:MappingModel"]
                 ?? DefaultModel;

        var provider = configuration["Ai:Provider"];
        var apiKey = configuration["Ai:OpenAI:ApiKey"];

        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new ChatClient(_model, apiKey);
        }

        _scopeFactory = scopeFactory;
        _trackerFactory = null;
    }

    /// <summary>
    /// Test-only ctor: inject a deterministic <see cref="IAiUsageTracker"/> without
    /// an <see cref="IServiceScopeFactory"/>, and optionally force the "client
    /// present" branch via <paramref name="overrideClient"/> without a real key.
    /// </summary>
    internal OpenAiPdfOrderExtractor(
        IConfiguration configuration,
        ILogger<OpenAiPdfOrderExtractor> logger,
        IAiUsageTracker? tracker,
        ChatClient? overrideClient = null)
    {
        _logger = logger;
        _model = configuration["Ai:OpenAI:ExtractionModel"]
                 ?? configuration["Ai:OpenAI:MappingModel"]
                 ?? DefaultModel;

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
    }

    public bool IsAvailable => _client is not null;

    public async Task<StructuredExtractionResult> ExtractAsync(
        Stream document,
        string contentType,
        Guid organisationId,
        CancellationToken ct)
    {
        if (_client is null)
            return StructuredExtractionResult.Fail("AI provider not configured.");

        // No tenant context → refuse, so the per-org cap can never be bypassed by a
        // caller that forgot to thread the org id (fail closed, not open).
        if (organisationId == Guid.Empty)
            return StructuredExtractionResult.Fail("No organisation context for AI extraction.");

        // Buffer the document so we can extract text (and, in a later phase,
        // rasterise for a vision fallback) over the same bytes.
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            await document.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF extraction: could not read document stream (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("Could not read document.");
        }

        // ── Text layer (Phase 1). No text → fall back to deterministic parsing
        // (a vision fallback for scanned PDFs is a later phase). ──────────────
        string sourceText;
        try
        {
            sourceText = PdfTextExtractor.ExtractText(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF extraction: PdfPig text extraction failed (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("Could not read PDF text layer.");
        }

        if (string.IsNullOrWhiteSpace(sourceText))
            return StructuredExtractionResult.Fail("PDF has no extractable text layer.");

        // Bound the input so one oversized document can't overshoot the token cap.
        if (sourceText.Length > MaxSourceChars)
        {
            _logger.LogWarning(
                "PDF extraction: source text {Len} chars exceeds {Max}; truncating before the LLM call (org {OrgId}).",
                sourceText.Length, MaxSourceChars, organisationId);
            sourceText = sourceText[..MaxSourceChars];
        }

        // ── Per-org monthly token cap ────────────────────────────────────────
        await using var trackerScope = _scopeFactory?.CreateAsyncScope();
        var tracker = trackerScope is not null
            ? trackerScope.Value.ServiceProvider.GetService<IAiUsageTracker>()
            : _trackerFactory?.Invoke();

        if (await IsAtOrOverCapAsync(tracker, organisationId, ct))
            return StructuredExtractionResult.Fail("Organisation AI usage cap reached.");

        // ── OpenAI strict-JSON structured extraction ─────────────────────────
        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(sourceText),
            };

            var completion = await CompleteWithTimeoutAsync(
                messages,
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = ExtractionMaxTokens,
                    ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: "purchase_order_extraction",
                        jsonSchema: ExtractionJsonSchema,
                        jsonSchemaIsStrict: true),
                },
                ct);

            await RecordUsageAsync(tracker, organisationId, completion.Usage?.TotalTokenCount ?? 0, ct);

            var json = completion.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("PDF extraction returned empty content (org {OrgId}).", organisationId);
                return StructuredExtractionResult.Fail("AI returned empty response.");
            }

            var dto = JsonSerializer.Deserialize<ExtractionDto>(json, JsonOptions);
            if (dto is null)
            {
                _logger.LogWarning("PDF extraction response could not be deserialised (org {OrgId}).", organisationId);
                return StructuredExtractionResult.Fail("AI response could not be parsed.");
            }

            return ValidateAndMap(dto, sourceText);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("PDF extraction timed out (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("AI request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PDF extraction failed (org {OrgId}).", organisationId);
            return StructuredExtractionResult.Fail("AI request failed.");
        }
    }

    // ─── Validation + mapping (pure, unit-tested directly) ───────────────────

    /// <summary>
    /// Maps the raw model output onto the canonical <see cref="ExtractedOrder"/> and
    /// applies the anti-hallucination safety net. Returns Success=false when the
    /// order is below the confidence threshold or has no lines (the orchestrator
    /// then falls back to deterministic parsing). When successful, every line whose
    /// emitted numbers do not appear in the source text, or whose
    /// quantity × unit price does not reconcile with the stated line amount, is
    /// reported in <see cref="StructuredExtractionResult.ReviewLineNumbers"/>.
    /// Pure and never throws — malformed numbers (NaN/overflow) flag the line rather
    /// than propagating.
    /// </summary>
    internal static StructuredExtractionResult ValidateAndMap(ExtractionDto dto, string sourceText)
    {
        var confidence = Math.Clamp(dto.Confidence, 0.0, 1.0);
        var rawLines = dto.Lines ?? Array.Empty<ExtractionLineDto>();

        if (confidence < ConfidenceThreshold)
            return StructuredExtractionResult.Fail(
                $"Extraction confidence {confidence:F2} is below the threshold {ConfidenceThreshold:F2}.");

        if (rawLines.Count == 0)
            return StructuredExtractionResult.Fail("No line items were extracted.");

        var sourceNumbers = ExtractSourceNumbers(sourceText);

        var lines = new List<ExtractedOrderLine>(rawLines.Count);
        var reviewLineNumbers = new List<int>();

        for (var idx = 0; idx < rawLines.Count; idx++)
        {
            var l = rawLines[idx];
            // Positional line number — a stable, unique join key for the downstream
            // review overlay and mapping. The model's own line_number is unreliable
            // (it may duplicate or echo a "Pos" column), so we don't trust it here.
            var lineNumber = idx + 1;

            var needsReview = false;

            // Convert numbers safely — a NaN/Infinity/out-of-range value from the
            // model flags the line instead of throwing.
            if (!TryToDecimal(l.Quantity, out var quantity) && l.Quantity is not null)
                needsReview = true;

            decimal? unitPrice = null;
            if (l.UnitPrice is not null)
            {
                if (TryToDecimal(l.UnitPrice, out var up)) unitPrice = up;
                else needsReview = true;
            }

            decimal? lineAmount = null;
            if (l.LineAmount is not null)
            {
                if (TryToDecimal(l.LineAmount, out var la)) lineAmount = la;
                else needsReview = true;
            }

            // Anti-hallucination: every emitted number must appear verbatim in the
            // source text. A zero quantity means "not stated" and is not checked.
            if (quantity != 0m && !NumberAppearsInSource(quantity, sourceNumbers))
                needsReview = true;
            if (unitPrice is { } up2 && up2 != 0m && !NumberAppearsInSource(up2, sourceNumbers))
                needsReview = true;
            if (lineAmount is { } la2 && la2 != 0m && !NumberAppearsInSource(la2, sourceNumbers))
                needsReview = true;

            // Arithmetic: quantity × unit price must reconcile with the stated line amount.
            if (unitPrice is { } u && lineAmount is { } amount && amount != 0m)
            {
                var expected = quantity * u;
                var tolerance = Math.Max(0.02m * Math.Abs(amount), 0.05m);
                if (Math.Abs(expected - amount) > tolerance)
                    needsReview = true;
            }

            if (needsReview)
                reviewLineNumbers.Add(lineNumber);

            lines.Add(new ExtractedOrderLine(
                LineNumber: lineNumber,
                BuyerItemCode: l.BuyerItemCode?.Trim() ?? string.Empty,
                Description: string.IsNullOrWhiteSpace(l.Description) ? null : l.Description.Trim(),
                Quantity: quantity,
                Unit: string.IsNullOrWhiteSpace(l.Unit) ? null : l.Unit.Trim(),
                UnitPrice: unitPrice));
        }

        DateTime? orderDate = null;
        if (!string.IsNullOrWhiteSpace(dto.OrderDate)
            && DateTime.TryParse(dto.OrderDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            orderDate = parsed;
        }

        var order = new ExtractedOrder(
            PoNumber: string.IsNullOrWhiteSpace(dto.PoNumber) ? null : dto.PoNumber.Trim(),
            OrderDate: orderDate,
            BuyerName: string.IsNullOrWhiteSpace(dto.BuyerName) ? null : dto.BuyerName.Trim(),
            Currency: string.IsNullOrWhiteSpace(dto.Currency) ? null : dto.Currency.Trim(),
            Lines: lines);

        return new StructuredExtractionResult(true, confidence, order, null, reviewLineNumbers);
    }

    // ─── Anti-hallucination number matching ──────────────────────────────────

    // Number-like runs: an optional grouped-thousands form (at least one
    // [.,]ddd group) OR a plain integer/decimal. A regular space is NOT a
    // thousands separator here — PdfPig joins distinct words with single spaces,
    // so "4 500" must tokenise as two numbers (4, 500), never one (4500).
    private static readonly Regex NumberToken = new(
        @"\d{1,3}(?:[.,]\d{3})+(?:[.,]\d+)?|\d+(?:[.,]\d+)?",
        RegexOptions.Compiled);

    // A single-separator token with exactly 3 trailing digits (e.g. "1.234",
    // "1,500") is ambiguous: grouped thousands OR a genuine 3-decimal value.
    private static readonly Regex AmbiguousThreeDecimal = new(
        @"^\d{1,3}[.,]\d{3}$", RegexOptions.Compiled);

    private static HashSet<decimal> ExtractSourceNumbers(string sourceText)
    {
        var set = new HashSet<decimal>();
        if (string.IsNullOrEmpty(sourceText)) return set;

        foreach (Match m in NumberToken.Matches(sourceText))
            AddNumberCandidates(m.Value, set);

        return set;
    }

    private static void AddNumberCandidates(string token, HashSet<decimal> set)
    {
        if (TryParseLoose(token, out var primary))
            set.Add(Normalize(primary));

        // For the ambiguous 3-trailing-digit case, ALSO add the decimal reading so a
        // correctly-emitted 1.234 still matches its source even though TryParseLoose
        // read the printed "1.234" as grouped thousands (1234). Membership-only — this
        // only makes matching more lenient, never more strict.
        if (AmbiguousThreeDecimal.IsMatch(token)
            && decimal.TryParse(token.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var asDecimal))
        {
            set.Add(Normalize(asDecimal));
        }
    }

    private static bool NumberAppearsInSource(decimal value, HashSet<decimal> sourceNumbers) =>
        sourceNumbers.Contains(Normalize(value));

    // Round to 4dp so double→decimal noise and trailing zeros don't cause spurious misses.
    private static decimal Normalize(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Safely converts a model-supplied double to decimal. Returns false (rather
    /// than throwing) for null, NaN, Infinity, or out-of-decimal-range values, so
    /// the pure <see cref="ValidateAndMap"/> honours its never-throw contract.
    /// </summary>
    private static bool TryToDecimal(double? d, out decimal value)
    {
        value = 0m;
        if (d is null) return false;
        var x = d.Value;
        if (double.IsNaN(x) || double.IsInfinity(x)) return false;
        if (x is > 7.9e28 or < -7.9e28) return false; // outside decimal's range
        try { value = (decimal)x; return true; }
        catch (OverflowException) { return false; }
    }

    /// <summary>
    /// Parses a printed number token into a decimal, resolving European vs Anglo
    /// thousands/decimal separators. Lenient by design — its only consumer is the
    /// anti-hallucination "does this number appear in the source" check.
    /// </summary>
    private static bool TryParseLoose(string token, out decimal value)
    {
        value = 0m;
        var t = token.Trim();
        if (t.Length == 0) return false;

        var dots = t.Count(c => c == '.');
        var commas = t.Count(c => c == ',');

        string normalized;
        if (dots > 0 && commas > 0)
        {
            // The right-most of the two is the decimal separator; the other is thousands.
            var decimalSep = t.LastIndexOf('.') > t.LastIndexOf(',') ? '.' : ',';
            var thousandsSep = decimalSep == '.' ? ',' : '.';
            t = t.Replace(thousandsSep.ToString(), string.Empty);
            // A single decimal separator is expected; if more remain, treat them all as thousands.
            normalized = t.Count(c => c == decimalSep) == 1
                ? t.Replace(decimalSep, '.')
                : t.Replace(decimalSep.ToString(), string.Empty);
        }
        else if (commas > 0)
        {
            normalized = NormalizeSingleSeparator(t, ',');
        }
        else if (dots > 0)
        {
            normalized = NormalizeSingleSeparator(t, '.');
        }
        else
        {
            normalized = t;
        }

        return decimal.TryParse(
            normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out value);
    }

    private static string NormalizeSingleSeparator(string t, char sep)
    {
        var count = t.Count(c => c == sep);
        if (count == 0) return t;

        // Multiple occurrences → grouped thousands (e.g. "1.234.567").
        if (count > 1) return t.Replace(sep.ToString(), string.Empty);

        // Single occurrence: a 3-digit trailing group is ambiguous and read as
        // thousands ("1.234" / "1,500"); any other length is a decimal fraction.
        // (The decimal reading of the 3-digit case is added separately in
        // AddNumberCandidates so a genuine 3dp value still matches.)
        var trailing = t.Length - t.IndexOf(sep) - 1;
        return trailing == 3
            ? t.Replace(sep.ToString(), string.Empty)
            : t.Replace(sep, '.');
    }

    // ─── Plumbing helpers ────────────────────────────────────────────────────

    private async Task<bool> IsAtOrOverCapAsync(IAiUsageTracker? tracker, Guid orgId, CancellationToken ct)
    {
        if (tracker is null || orgId == Guid.Empty) return false;
        try
        {
            if (await tracker.IsAtOrOverLimitAsync(orgId, ct))
            {
                _logger.LogWarning(
                    "PDF extraction skipped — org {OrgId} reached monthly token limit {Limit}.",
                    orgId, tracker.MonthlyLimit);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Cap-check failure must not silently bypass the cap.
            _logger.LogWarning(ex, "PDF extraction cap check failed for org {OrgId}; skipping to be safe.", orgId);
            return true;
        }
    }

    private async Task RecordUsageAsync(IAiUsageTracker? tracker, Guid orgId, int totalTokens, CancellationToken ct)
    {
        if (tracker is null || orgId == Guid.Empty || totalTokens <= 0) return;
        try
        {
            await tracker.IncrementAsync(orgId, totalTokens, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record AI token usage for org {OrgId}.", orgId);
        }
    }

    private async Task<ChatCompletion> CompleteWithTimeoutAsync(
        IList<ChatMessage> messages,
        ChatCompletionOptions options,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(OpenAiCallTimeout);
        return await _client!.CompleteChatAsync(messages, options, cts.Token);
    }

    // ─── DTOs for OpenAI structured outputs ──────────────────────────────────
    // Snake_case [JsonPropertyName] required: the schema uses snake_case keys and
    // JsonSerializerDefaults.Web would otherwise map to camelCase and bind null.

    internal sealed record ExtractionDto(
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("po_number")] string? PoNumber,
        [property: JsonPropertyName("order_date")] string? OrderDate,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("buyer_name")] string? BuyerName,
        [property: JsonPropertyName("lines")] IReadOnlyList<ExtractionLineDto>? Lines);

    internal sealed record ExtractionLineDto(
        [property: JsonPropertyName("line_number")] int LineNumber,
        [property: JsonPropertyName("buyer_item_code")] string? BuyerItemCode,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("quantity")] double? Quantity,
        [property: JsonPropertyName("unit")] string? Unit,
        [property: JsonPropertyName("unit_price")] double? UnitPrice,
        [property: JsonPropertyName("line_amount")] double? LineAmount);
}
