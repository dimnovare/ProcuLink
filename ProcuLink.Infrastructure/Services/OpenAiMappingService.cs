using System.Text.Json;
using Microsoft.Extensions.Configuration;
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

    private readonly ChatClient? _client;
    private readonly ILogger<OpenAiMappingService> _logger;
    private readonly string _model;

    public OpenAiMappingService(IConfiguration configuration, ILogger<OpenAiMappingService> logger)
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

    private sealed record OpenAiSuggestionDto(
        string SupplierItemCode,
        float Confidence,
        string? Reason,
        string? Provenance);
}
