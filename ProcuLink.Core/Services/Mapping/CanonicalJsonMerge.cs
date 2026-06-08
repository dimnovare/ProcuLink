using System.Text.Json;

namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Single shared helper for merging keys into <c>purchase_orders.canonical_json</c>.
/// Extracted verbatim (behaviour-preserving) from <c>OrderResolutionService.MergeCanonicalJson</c>
/// so that BOTH the resolve path and the new per-order mapping-override write use ONE merge
/// implementation — canonical_json merges must never clobber existing keys such as
/// <c>buyerName</c> / enrichment provenance (the documented denormalisation split, see CLAUDE.md).
/// </summary>
public static class CanonicalJsonMerge
{
    /// <summary>
    /// Returns a new <see cref="JsonDocument"/> equal to <paramref name="existing"/> with the supplied
    /// keys added or overwritten. Existing properties are preserved verbatim; updated keys are written
    /// as STRINGS (header corrections are always string-valued). A null/missing source document yields
    /// a document containing only the updates. Used to keep canonical_json consistent with denormalised
    /// columns after a header edit.
    /// </summary>
    public static JsonDocument MergeStrings(
        JsonDocument? existing,
        IReadOnlyDictionary<string, object?> updates)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // Copy through every existing property except the ones we're overwriting.
            if (existing is not null && existing.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in existing.RootElement.EnumerateObject())
                {
                    if (updates.ContainsKey(prop.Name)) continue; // overwritten below
                    prop.WriteTo(writer);
                }
            }

            // Write (add or overwrite) the corrected keys.
            foreach (var kvp in updates)
            {
                writer.WritePropertyName(kvp.Key);
                if (kvp.Value is null) writer.WriteNullValue();
                else writer.WriteStringValue(kvp.Value.ToString());
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray());
    }

    /// <summary>
    /// Returns a new <see cref="JsonDocument"/> equal to <paramref name="existing"/> with a single key
    /// set to an arbitrary already-serialised JSON value (object/array/etc), preserving every other
    /// existing property verbatim. Passing a null <paramref name="rawJsonValue"/> REMOVES the key.
    /// Used by the mapping-override write so the override sub-document never clobbers buyerName /
    /// enrichment keys that share the same canonical_json document.
    /// </summary>
    public static JsonDocument SetRawValue(
        JsonDocument? existing,
        string key,
        string? rawJsonValue)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            if (existing is not null && existing.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in existing.RootElement.EnumerateObject())
                {
                    if (prop.Name == key) continue; // overwritten / removed below
                    prop.WriteTo(writer);
                }
            }

            if (rawJsonValue is not null)
            {
                writer.WritePropertyName(key);
                using var value = JsonDocument.Parse(rawJsonValue);
                value.RootElement.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray());
    }
}
