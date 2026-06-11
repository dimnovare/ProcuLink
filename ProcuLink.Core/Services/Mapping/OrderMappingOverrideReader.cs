using System.Text.Json;

namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Pure, dependency-free decoder for the per-order mapping override stored under the
/// <c>"mappingOverride"</c> key of an order's <c>canonical_json</c>. Used by BOTH the storage
/// service (Infrastructure) and the override-aware transform branch (Api / Worker) so the override
/// is read identically everywhere. Never throws on a missing / malformed key — returns null.
/// </summary>
public static class OrderMappingOverrideReader
{
    /// <summary>Canonical_json node under which the per-order override lives.</summary>
    public const string CanonicalKey = "mappingOverride";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Extracts the <see cref="OrderMappingOverride"/> from a (possibly null) canonical_json document.
    /// Returns null when the document is null, not an object, has no override key, the key is JSON null,
    /// or the sub-document is malformed.
    /// </summary>
    public static OrderMappingOverride? Read(JsonDocument? canonicalJson)
    {
        if (canonicalJson is null) return null;

        try
        {
            if (canonicalJson.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!canonicalJson.RootElement.TryGetProperty(CanonicalKey, out var node)) return null;
            if (node.ValueKind == JsonValueKind.Null) return null;

            return node.Deserialize<OrderMappingOverride>(SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Raw JSON text of the <c>"mappingOverride"</c> node, exactly as stored in canonical_json
    /// (used for provenance config digests — hashing the stored text, not a re-serialization).
    /// Returns null when the document is null, not an object, has no override key, or the key
    /// is JSON null. Never throws.
    /// </summary>
    public static string? ReadRawJson(JsonDocument? canonicalJson)
    {
        if (canonicalJson is null) return null;

        try
        {
            if (canonicalJson.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!canonicalJson.RootElement.TryGetProperty(CanonicalKey, out var node)) return null;
            if (node.ValueKind == JsonValueKind.Null) return null;

            return node.GetRawText();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True only when an override is present AND actually carries an output mapping with at least one
    /// header or line rule. An override that has only custom fields (no <see cref="OutputMappingConfig"/>),
    /// or an empty output config, must NOT divert the transform — the fixed transformer stays in control
    /// so required fields can never be silently dropped.
    /// </summary>
    public static bool HasUsableOutput(OrderMappingOverride? @override) =>
        @override?.Output is { } output
        && (output.Header.Count > 0 || output.Lines.Count > 0);

    /// <summary>
    /// True only when an override is present AND carries a non-blank whole-document
    /// <see cref="OrderMappingOverride.OutputTemplate"/>. Template mode takes precedence over the
    /// field-by-field <see cref="OrderMappingOverride.Output"/> config: when a usable template is
    /// present, the transform renders the whole document from it. A null/blank template leaves the
    /// existing (field-by-field override, or fixed transformer) path unchanged.
    /// </summary>
    public static bool HasUsableTemplate(OrderMappingOverride? @override) =>
        !string.IsNullOrWhiteSpace(@override?.OutputTemplate);
}
