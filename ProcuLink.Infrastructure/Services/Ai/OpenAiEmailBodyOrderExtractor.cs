using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;

namespace ProcuLink.Infrastructure.Services.Ai;

/// <summary>
/// OpenAI-backed implementation of <see cref="IEmailBodyOrderExtractor"/>.
///
/// Mirrors <see cref="OpenAiSchemaInferencer"/>:
///   • No-op (Success=false) when <c>Ai:OpenAI:ApiKey</c> is missing or provider ≠ "openai".
///   • Uses OpenAI structured outputs (JSON Schema, strict mode) for extraction.
///   • Enforces the per-org monthly token cap via <see cref="IAiUsageTracker"/>.
///   • 30-second hard timeout per OpenAI call.
///   • Returns Success=false when model-reported confidence &lt; 0.6.
/// </summary>
public sealed class OpenAiEmailBodyOrderExtractor : IEmailBodyOrderExtractor
{
    private const string DefaultModel = "gpt-5-mini";
    private const double ConfidenceThreshold = 0.6;
    private const int ExtractionMaxTokens = 1500;
    private static readonly TimeSpan OpenAiCallTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    // JSON schema for the structured output response.
    // OpenAI strict mode requires every property to be listed in "required" AND
    // additionalProperties: false at every object level.
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
                  "line_number":      { "type": "integer" },
                  "buyer_item_code":  { "type": "string" },
                  "description":      { "type": "string" },
                  "quantity":         { "type": "number" },
                  "unit":             { "type": "string" },
                  "unit_price":       { "type": "number" }
                },
                "required": ["line_number", "buyer_item_code", "description", "quantity", "unit", "unit_price"],
                "additionalProperties": false
              }
            }
          },
          "required": ["confidence", "po_number", "order_date", "currency", "buyer_name", "lines"],
          "additionalProperties": false
        }
        """u8.ToArray());

    private readonly ChatClient?                            _client;
    private readonly ILogger<OpenAiEmailBodyOrderExtractor> _logger;
    private readonly string                                 _model;
    private readonly IAiUsageTracker?                       _tracker;
    private readonly ICurrentTenantService?                 _tenant;
    private readonly Func<Guid>?                            _testOrgIdProvider;

    public OpenAiEmailBodyOrderExtractor(
        IConfiguration                             configuration,
        IAiUsageTracker                            usageTracker,
        ICurrentTenantService                      tenantService,
        ILogger<OpenAiEmailBodyOrderExtractor>     logger)
    {
        _logger  = logger;
        _model   = configuration["Ai:OpenAI:ExtractionModel"]
                   ?? configuration["Ai:OpenAI:MappingModel"]
                   ?? DefaultModel;
        _tracker = usageTracker;
        _tenant  = tenantService;

        var provider = configuration["Ai:Provider"];
        var apiKey   = configuration["Ai:OpenAI:ApiKey"];

        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new ChatClient(_model, apiKey);
        }
    }

    /// <summary>
    /// Test-only ctor: lets tests inject a deterministic
    /// <see cref="IAiUsageTracker"/> and bypass the
    /// <see cref="ICurrentTenantService"/> chain entirely. Set
    /// <paramref name="overrideClient"/> to a non-null value to force the
    /// "client present" branch without a real API key — used by tests that
    /// want to verify the cap-check short-circuit without ever calling
    /// OpenAI for real.
    /// </summary>
    internal OpenAiEmailBodyOrderExtractor(
        IConfiguration                             configuration,
        ILogger<OpenAiEmailBodyOrderExtractor>     logger,
        IAiUsageTracker?                           tracker,
        Func<Guid>                                 orgIdProvider,
        ChatClient?                                overrideClient = null)
    {
        _logger  = logger;
        _model   = configuration["Ai:OpenAI:ExtractionModel"]
                   ?? configuration["Ai:OpenAI:MappingModel"]
                   ?? DefaultModel;
        _tracker = tracker;
        _tenant  = null;
        _testOrgIdProvider = orgIdProvider;

        var provider = configuration["Ai:Provider"];
        var apiKey   = configuration["Ai:OpenAI:ApiKey"];

        if (overrideClient is not null)
        {
            _client = overrideClient;
        }
        else if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(apiKey))
        {
            _client = new ChatClient(_model, apiKey);
        }
    }

    // ─── Public API ─────────────────────────────────────────────────────────

    public async Task<EmailBodyExtractionResult> ExtractAsync(
        string            emailBody,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(emailBody))
        {
            _logger.LogDebug("IEmailBodyOrderExtractor.ExtractAsync: empty body — skipping.");
            return Failure("Email body is empty.");
        }

        if (_client is null)
        {
            _logger.LogDebug("IEmailBodyOrderExtractor.ExtractAsync: AI provider not configured — returning failure.");
            return Failure("AI provider not configured.");
        }

        var orgId = ResolveOrgIdOrEmpty();

        // Per-org monthly token cap — never bypass when the check throws.
        if (await IsAtOrOverCapAsync(orgId, ct))
            return Failure("Organisation AI usage cap reached.");

        // ── OpenAI structured output call ────────────────────────────────────
        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    "Extract a purchase order from this email body. " +
                    "Return only structured data matching the schema. " +
                    "Set confidence to 0.0–1.0 based on how clearly the email contains a real purchase order."),
                new UserChatMessage(emailBody),
            };

            var completion = await CompleteWithTimeoutAsync(
                messages,
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = ExtractionMaxTokens,
                    ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                        jsonSchemaFormatName: "email_order_extraction",
                        jsonSchema: ExtractionJsonSchema,
                        jsonSchemaIsStrict: true),
                },
                ct);

            await RecordUsageAsync(orgId, completion.Usage?.TotalTokenCount ?? 0, ct);

            var json = completion.Content.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("OpenAI email extraction returned empty content (org {OrgId}).", orgId);
                return Failure("AI returned empty response.");
            }

            var dto = JsonSerializer.Deserialize<ExtractionDto>(json, JsonOptions);
            if (dto is null)
            {
                _logger.LogWarning("OpenAI email extraction response could not be deserialized (org {OrgId}).", orgId);
                return Failure("AI response could not be parsed.");
            }

            var confidence = Math.Clamp(dto.Confidence, 0.0, 1.0);

            if (confidence < ConfidenceThreshold)
            {
                _logger.LogDebug(
                    "OpenAI email extraction confidence {Confidence:F2} < {Threshold:F2} — treating as not a PO (org {OrgId}).",
                    confidence, ConfidenceThreshold, orgId);
                return new EmailBodyExtractionResult(
                    false,
                    confidence,
                    null,
                    $"Confidence {confidence:F2} is below the threshold {ConfidenceThreshold:F2}.");
            }

            var order = MapToOrder(dto);
            return new EmailBodyExtractionResult(true, confidence, order, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("OpenAI email extraction timed out (org {OrgId}).", orgId);
            return Failure("AI request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI email extraction failed (org {OrgId}).", orgId);
            return Failure("AI request failed.");
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static EmailBodyExtractionResult Failure(string reason) =>
        new(false, 0.0, null, reason);

    private Guid ResolveOrgIdOrEmpty()
    {
        if (_testOrgIdProvider is not null) return _testOrgIdProvider();
        try { return _tenant?.OrganisationId ?? Guid.Empty; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ICurrentTenantService.OrganisationId unavailable — treating as no-tenant.");
            return Guid.Empty;
        }
    }

    private async Task<bool> IsAtOrOverCapAsync(Guid orgId, CancellationToken ct)
    {
        if (_tracker is null || orgId == Guid.Empty) return false;
        try
        {
            if (await _tracker.IsAtOrOverLimitAsync(orgId, ct))
            {
                _logger.LogWarning(
                    "OpenAI email extraction skipped — org {OrgId} reached monthly token limit {Limit}.",
                    orgId, _tracker.MonthlyLimit);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Cap check failure must not silently bypass the cap.
            _logger.LogWarning(ex, "OpenAI cap check failed for org {OrgId}; skipping extraction to be safe.", orgId);
            return true;
        }
    }

    private async Task RecordUsageAsync(Guid orgId, int totalTokens, CancellationToken ct)
    {
        if (_tracker is null || orgId == Guid.Empty || totalTokens <= 0) return;
        try
        {
            await _tracker.IncrementAsync(orgId, totalTokens, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record AI token usage for org {OrgId}.", orgId);
        }
    }

    private async Task<ChatCompletion> CompleteWithTimeoutAsync(
        IList<ChatMessage>    messages,
        ChatCompletionOptions options,
        CancellationToken     ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(OpenAiCallTimeout);
        return await _client!.CompleteChatAsync(messages, options, cts.Token);
    }

    // ─── Mapping DTO → ExtractedOrder ───────────────────────────────────────

    private static ExtractedOrder MapToOrder(ExtractionDto dto)
    {
        DateTime? orderDate = null;
        if (!string.IsNullOrWhiteSpace(dto.OrderDate)
            && DateTime.TryParse(dto.OrderDate, out var parsed))
        {
            orderDate = parsed;
        }

        var lines = (dto.Lines ?? Array.Empty<ExtractionLineDto>())
            .Select((l, idx) => new ExtractedOrderLine(
                LineNumber:    l.LineNumber > 0 ? l.LineNumber : idx + 1,
                BuyerItemCode: l.BuyerItemCode ?? string.Empty,
                Description:   l.Description,
                Quantity:      (decimal)(l.Quantity ?? 0),
                Unit:          string.IsNullOrWhiteSpace(l.Unit) ? null : l.Unit,
                UnitPrice:     l.UnitPrice.HasValue ? (decimal?)l.UnitPrice.Value : null))
            .ToList();

        return new ExtractedOrder(
            PoNumber:  dto.PoNumber,
            OrderDate: orderDate,
            BuyerName: dto.BuyerName,
            Currency:  dto.Currency,
            Lines:     lines);
    }

    // ─── DTOs for OpenAI structured outputs ─────────────────────────────────

    private sealed record ExtractionDto(
        double                            Confidence,
        string?                           PoNumber,
        string?                           OrderDate,
        string?                           Currency,
        string?                           BuyerName,
        IReadOnlyList<ExtractionLineDto>? Lines);

    private sealed record ExtractionLineDto(
        int     LineNumber,
        string? BuyerItemCode,
        string? Description,
        double? Quantity,
        string? Unit,
        double? UnitPrice);
}
