using System.Text.Json;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Phase 2: rebuild the addressable <see cref="SourceToken"/> set from a persisted
/// <c>SourceCapture.TokensJson</c> document. This is the bridge that lets <c>SourceMapReDerive</c>
/// resolve <c>SourceFieldRule.SourceToken</c> references at transform/preview time WITHOUT
/// re-tokenizing the source file — so mapping still works after the source blob is purged
/// (<c>SourceFilePurgedAt</c>) and the FULL field universe (mapped + unmapped) is addressable.
///
/// <para>The JSON shape mirrors the Phase-1 writer
/// (<c>OrderIngestionService.UpsertSourceCaptureAsync</c> / <c>BuildSourceCapture</c>):</para>
/// <list type="bullet">
///   <item>structured formats (CSV/XLSX/XML/cXML/EDI/X12): <c>{ id, label, value, group }</c>
///         (group is a literal JSON null when the format has no header/line distinction);</item>
///   <item>PDF/email raw_fields: <c>{ label, value }</c> (no id, no group) — we synthesise a
///         deterministic <c>raw:{label}</c> id so those long-tail fields are still wireable
///         by a SourceMap rule, and leave the group null.</item>
/// </list>
///
/// <para>Values are carried VERBATIM — never numeric-parsed here — so locale-formatted prices
/// ("1.234,56") survive intact (the EU-aware parse happens downstream where arithmetic occurs).</para>
/// </summary>
public static class SourceTokenSerialization
{
    public static IReadOnlyList<SourceToken> FromTokensJson(JsonDocument? tokensJson)
    {
        if (tokensJson is null) return Array.Empty<SourceToken>();

        var root = tokensJson.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return Array.Empty<SourceToken>();

        var result = new List<SourceToken>(root.GetArrayLength());
        foreach (var el in root.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            var label = ReadString(el, "label") ?? string.Empty;
            var value = ReadString(el, "value") ?? string.Empty;
            var group = ReadString(el, "group"); // nullable by design (literal null → null)

            // Prefer the explicit id; else a deterministic raw:{label} id for raw_fields so the
            // long-tail PDF/email fields are still addressable by a SourceMap rule.
            var id = ReadString(el, "id");
            if (string.IsNullOrEmpty(id))
                id = $"raw:{label}";

            result.Add(new SourceToken(id, label, value, group));
        }
        return result;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
