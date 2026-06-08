using System.Globalization;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Pure helper that applies the <see cref="SourceFieldRule"/> map in an
/// <see cref="OrderMappingOverride"/> to produce the effective canonical value for each named
/// field, before the output transform runs.
///
/// <para><b>Resolution order for each field:</b></para>
/// <list type="number">
///   <item>If a <see cref="SourceFieldRule"/> is present for the field AND its
///         <see cref="SourceFieldRule.SourceToken"/> is non-null AND that token id exists in
///         <paramref name="tokens"/>, use the token's <c>Value</c> as the raw input.</item>
///   <item>Else if the rule has a non-null <see cref="SourceFieldRule.FixedValue"/>, use it.</item>
///   <item>Else (no rule, or both SourceToken and FixedValue are null), keep the existing parsed
///         value from <paramref name="headerRow"/> / <paramref name="lineRow"/> unchanged.</item>
/// </list>
/// <para>After the value is selected, the rule's <see cref="SourceFieldRule.Manipulators"/> chain
/// is applied via <see cref="ManipulatorRegistry"/>, using <paramref name="headerRow"/> as the
/// sibling-value bag for manipulators like Concat/Fallback — identical semantics to
/// <c>PoMappingEngine.ResolveField</c> and <c>MappedTransformService</c>.</para>
///
/// <para><b>No SourceMap → unchanged.</b> When <paramref name="override"/>'s
/// <see cref="OrderMappingOverride.SourceMap"/> is null or empty, every method returns the row
/// passed in unmodified, so the default (no SourceMap) transform path is byte-for-byte
/// identical to today.</para>
///
/// <para><b>Unresolved-line guard.</b> <see cref="ApplyToLineRow"/> never overwrites the sentinel
/// that tells downstream transforms an order line must not be auto-delivered: if the result of
/// applying a SourceMap to <c>SupplierItemCode</c> would produce an empty string or whitespace,
/// the guard restores <c>string.Empty</c> (so the fixed transforms still flag the line as
/// unresolved). A SourceMap can NEVER bypass the NeedsReview / null-SupplierItemCode check.</para>
/// </summary>
public static class SourceMapReDerive
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the SourceMap to a header-scope field bag, returning a new dictionary with the
    /// effective values. The input dictionary is not mutated.
    /// </summary>
    public static Dictionary<string, string> ApplyToHeaderRow(
        Dictionary<string, string>  headerRow,
        OrderMappingOverride        @override,
        IReadOnlyList<SourceToken>  tokens)
    {
        var sourceMap = @override.SourceMap;
        if (sourceMap is null || sourceMap.Count == 0)
            return headerRow; // fast path — no SourceMap, unchanged

        var result = new Dictionary<string, string>(headerRow, StringComparer.Ordinal);

        // Header-scope canonical field names.
        var headerFields = new[] { "PoNumber", "OrderDate", "BuyerName", "Currency", "SupplierName" };

        foreach (var fieldName in headerFields)
            ApplyRule(result, fieldName, sourceMap, tokens);

        return result;
    }

    /// <summary>
    /// Applies the SourceMap to a line-scope field bag, returning a new dictionary with the
    /// effective values. The input dictionary is not mutated.
    ///
    /// SupplierItemCode is guarded: if the remap would produce empty/whitespace, the empty string
    /// is kept so downstream transform validation still blocks delivery of the unresolved line.
    /// </summary>
    public static Dictionary<string, string> ApplyToLineRow(
        Dictionary<string, string>  lineRow,
        OrderMappingOverride        @override,
        IReadOnlyList<SourceToken>  tokens)
    {
        var sourceMap = @override.SourceMap;
        if (sourceMap is null || sourceMap.Count == 0)
            return lineRow; // fast path — no SourceMap, unchanged

        var result = new Dictionary<string, string>(lineRow, StringComparer.Ordinal);

        var lineFields = new[]
        {
            "LineNumber", "BuyerItemCode", "SupplierItemCode",
            "Description", "Quantity", "Unit", "UnitPrice", "LineTotal",
        };

        foreach (var fieldName in lineFields)
            ApplyRule(result, fieldName, sourceMap, tokens);

        // Unresolved-line guard: SupplierItemCode must never become empty/whitespace via remap.
        // The fixed transform validators check for null/whitespace — preserve the sentinel.
        if (result.TryGetValue("SupplierItemCode", out var sic) && string.IsNullOrWhiteSpace(sic))
            result["SupplierItemCode"] = string.Empty; // keep empty so the guard still fires

        return result;
    }

    // ── Core re-derive logic ──────────────────────────────────────────────────

    private static void ApplyRule(
        Dictionary<string, string>             row,
        string                                 fieldName,
        Dictionary<string, SourceFieldRule>    sourceMap,
        IReadOnlyList<SourceToken>             tokens)
    {
        if (!sourceMap.TryGetValue(fieldName, out var rule))
            return; // no rule for this field → keep existing value, unchanged

        // 1. Pick raw value: SourceToken lookup → FixedValue → existing parsed value
        string? value = null;

        if (rule.SourceToken is not null)
        {
            // Search the token list for a matching id. Comparison is case-sensitive to keep ids stable.
            var token = FindToken(tokens, rule.SourceToken);
            if (token is not null)
                value = token.Value;
        }

        if (value is null && rule.FixedValue is not null)
            value = rule.FixedValue;

        if (value is null && row.TryGetValue(fieldName, out var existing))
            value = existing; // pass-through: keep the parsed canonical value

        value ??= string.Empty;

        // 2. Apply manipulator chain (same semantics as MappedTransformService.ResolveRule).
        foreach (var entry in rule.Manipulators ?? new List<ManipulatorEntry>())
        {
            var manipulator = ManipulatorRegistry.Resolve(entry.Type, entry.Params);
            value = manipulator.Apply(value, row);
        }

        row[fieldName] = value ?? string.Empty;
    }

    private static SourceToken? FindToken(IReadOnlyList<SourceToken> tokens, string id)
    {
        foreach (var t in tokens)
            if (string.Equals(t.Id, id, StringComparison.Ordinal))
                return t;
        return null;
    }

    // ── Integration with MappedTransformService row-building ─────────────────

    /// <summary>
    /// Convenience overload: given a <see cref="PurchaseOrderEntity"/> and an
    /// <see cref="OrderMappingOverride"/> (possibly null), builds the effective header row ready for
    /// output rule resolution. Calls <see cref="ApplyToHeaderRow"/> when a SourceMap is present.
    /// </summary>
    public static Dictionary<string, string> BuildEffectiveHeaderRow(
        PurchaseOrderEntity                order,
        OrderMappingOverride               @override,
        IReadOnlyList<SourceToken>         tokens,
        Func<PurchaseOrderEntity, OrderMappingOverride, Dictionary<string, string>> baseRowBuilder)
    {
        var baseRow = baseRowBuilder(order, @override);
        return ApplyToHeaderRow(baseRow, @override, tokens);
    }

    /// <summary>
    /// Convenience overload for line rows.
    /// </summary>
    public static Dictionary<string, string> BuildEffectiveLineRow(
        PurchaseOrderEntity                order,
        OrderMappingOverride               @override,
        PurchaseOrderLineEntity            line,
        IReadOnlyList<SourceToken>         tokens,
        Func<PurchaseOrderEntity, OrderMappingOverride, PurchaseOrderLineEntity, Dictionary<string, string>> baseRowBuilder)
    {
        var baseRow = baseRowBuilder(order, @override, line);
        return ApplyToLineRow(baseRow, @override, tokens);
    }
}
