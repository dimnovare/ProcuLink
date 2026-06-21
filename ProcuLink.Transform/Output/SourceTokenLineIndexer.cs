using System.Text.RegularExpressions;

namespace ProcuLink.Transform.Output;

/// <summary>
/// F-1 Seam A — PURE helper that maps a <see cref="Tokenizing.SourceToken"/> id to the 1-based LINE
/// ORDINAL it addresses, or <c>null</c> when the id is header-scoped / carries no per-line position.
///
/// <para>The override row bag is built PER LINE. A line-scoped source token has a GLOBAL id
/// (<c>cell:r3c4</c>, <c>/Order/Line[2]/Qty</c>, <c>seg:LIN[2].el1</c>), so injecting every line
/// token into every line's bag would let line 1 read line 2's value. The injector asks this helper
/// "which line does this id address?" and injects the token only into that line's bag. An id with no
/// resolvable ordinal is treated as header-scope (injected into the header bag with its global id), so
/// it is still bindable — just not per-line. Keeping this logic pure + exhaustively unit-tested is the
/// design's one genuinely-new piece of resolution logic (F-1 design §6, repeating-line resolution).</para>
///
/// <para><b>Per-format ordinal scheme</b> (matches <see cref="Tokenizing.SourceTokenizer"/> verbatim):</para>
/// <list type="bullet">
///   <item><b>CSV / XLSX</b> — <c>cell:r{n}c{c}</c>: row 1 is the header, data rows are 2.. so the line
///         ordinal is <c>n-1</c> (e.g. <c>cell:r2c5</c> → line 1, <c>cell:r3c5</c> → line 2). Row 1 → null.</item>
///   <item><b>XML / cXML / UBL / IDoc</b> — an XPath whose repeating line element carries a 1-based
///         <c>[n]</c> position predicate (e.g. <c>/Order/Lines/Line[2]/Qty</c> → line 2). The DEEPEST
///         predicate is the innermost (line) repetition. No predicate → null (a single, non-repeating
///         element is header-scope).</item>
///   <item><b>EDIFACT / X12</b> — <c>seg:{TAG}[{n}].el…</c>: <c>n</c> is the 1-based occurrence of that
///         tag in the message. For the line-anchor segment (<c>LIN</c>/<c>PO1</c>) and the realistic
///         one-segment-per-line layout, occurrence <c>n</c> equals line <c>n</c>, so the ordinal is
///         <c>n</c>. (Honest caveat: an irregular message with several occurrences of the same tag
///         WITHIN one line would skew the per-segment ordinal; the LIN/PO1 anchor — the field users
///         actually bind — is always exact. Such a token simply lands in the wrong line's bag or, when
///         no line matches, falls through to header-scope — it never corrupts another binding.)</item>
///   <item><b>JSON</b> — <c>json:{pointer}</c>: the FIRST 0-based array index in the pointer maps to a
///         1-based ordinal (e.g. <c>json:/lines/0/sku</c> → line 1). No array index → null.</item>
///   <item><b>Everything else</b> (<c>raw:{label}</c>, unknown ids, null/empty) → null (header-scope).</item>
/// </list>
/// </summary>
public static class SourceTokenLineIndexer
{
    // cell:r{n}c{c} — capture the row number.
    private static readonly Regex CellRow = new(@"^cell:r(\d+)c\d+$", RegexOptions.Compiled);

    // seg:{TAG}[{n}]... — capture the tag occurrence number.
    private static readonly Regex SegOccurrence = new(@"^seg:[^\[]+\[(\d+)\]", RegexOptions.Compiled);

    // Any /Name[n] positional predicate in an XPath — captures the n; we take the LAST (deepest) match.
    private static readonly Regex XpathPredicate = new(@"\[(\d+)\]", RegexOptions.Compiled);

    // json:/a/0/b — the FIRST 0-based numeric pointer segment is the array index.
    private static readonly Regex JsonArrayIndex = new(@"^json:.*?/(\d+)(?:/|$)", RegexOptions.Compiled);

    // cell:r{n}c{c} — capture BOTH the row and the column so we can drop the row.
    private static readonly Regex CellRowCol = new(@"^cell:r(\d+)c(\d+)$", RegexOptions.Compiled);

    // seg:{TAG}[{n}]rest — capture the tag, the occurrence, and the trailing element path.
    private static readonly Regex SegRelative = new(@"^(seg:[^\[]+)\[\d+\](.*)$", RegexOptions.Compiled);

    /// <summary>
    /// The 1-based line ordinal the token id addresses, or <c>null</c> for a header-scoped /
    /// non-positional id. Pure; never throws (a malformed/unknown id simply yields null).
    /// </summary>
    public static int? LineOrdinalOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        // CSV / XLSX — cell:r{n}c{c}; data rows are 2.. so the ordinal is n-1; row 1 is the header.
        var cell = CellRow.Match(id);
        if (cell.Success && int.TryParse(cell.Groups[1].Value, out var row))
            return row >= 2 ? row - 1 : (int?)null;

        // EDIFACT / X12 — seg:{TAG}[{n}].el…; the tag occurrence n is the ordinal.
        var seg = SegOccurrence.Match(id);
        if (seg.Success && int.TryParse(seg.Groups[1].Value, out var occ) && occ >= 1)
            return occ;

        // XML family — the DEEPEST [n] predicate is the innermost (line) repetition.
        if (id.StartsWith('/'))
        {
            var matches = XpathPredicate.Matches(id);
            if (matches.Count > 0
                && int.TryParse(matches[^1].Groups[1].Value, out var pos) && pos >= 1)
                return pos;
            return null; // no positional predicate → single occurrence, header-scope
        }

        // JSON — the first array index in the pointer.
        var json = JsonArrayIndex.Match(id);
        if (json.Success && int.TryParse(json.Groups[1].Value, out var idx))
            return idx + 1;

        // raw:{label}, unknown ids → header-scope.
        return null;
    }

    /// <summary>
    /// F-1 Phase 4 — the RELATIVE alias key for a line-scoped token id: the SAME id with its per-line
    /// ordinal stripped, so the key is IDENTICAL across every line. A rule bound to this key resolves to
    /// EACH line's own value (where the absolute id resolves only one specific line). Returns <c>null</c>
    /// for any id <see cref="LineOrdinalOf"/> would treat as header-scoped / non-positional (a header
    /// cell, an unpredicated XML element, a <c>raw:</c> field, null/empty), so the alias is emitted only
    /// when there is a genuine repeating-line address. Pure; never throws.
    ///
    /// <para>This is the ONE source of truth for the relative-id format — the source-tokens DTO and the
    /// override injector both call it, so the FE picker and the BE resolver can never drift.</para>
    ///
    /// <para><b>Per-format strip</b> (mirrors <see cref="LineOrdinalOf"/>'s format detection verbatim):</para>
    /// <list type="bullet">
    ///   <item><b>CSV / XLSX</b> <c>cell:r{n}c{c}</c> → <c>cell:c{c}</c> (drop the row, keep the column).
    ///         Row 1 (the header) → null.</item>
    ///   <item><b>XML / cXML / UBL / IDoc</b> XPath → strip the DEEPEST <c>[n]</c> positional predicate
    ///         ONLY: <c>/Order/Lines/Line[2]/Qty</c> → <c>/Order/Lines/Line/Qty</c> (other predicates
    ///         are left intact). No predicate → null.</item>
    ///   <item><b>EDIFACT / X12</b> <c>seg:{TAG}[{n}].el…</c> → <c>seg:{TAG}.el…</c> (drop the occurrence).</item>
    ///   <item><b>JSON</b> <c>json:/…/{i}/…</c> → replace the FIRST array index with <c>*</c>:
    ///         <c>json:/lines/0/sku</c> → <c>json:/lines/*/sku</c>. No array index → null.</item>
    ///   <item>Everything else (<c>raw:</c>, unknown ids, null/empty) → null.</item>
    /// </list>
    /// </summary>
    public static string? RelativeLineKey(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        // CSV / XLSX — cell:r{n}c{c} → cell:c{c}. Only a data row (n >= 2) addresses a line; row 1 is
        // the header (header-scope, no relative alias) — kept consistent with LineOrdinalOf.
        var cell = CellRowCol.Match(id);
        if (cell.Success)
        {
            if (int.TryParse(cell.Groups[1].Value, out var row) && row >= 2)
                return $"cell:c{cell.Groups[2].Value}";
            return null; // header row
        }

        // EDIFACT / X12 — seg:{TAG}[{n}].el… → seg:{TAG}.el… (drop the occurrence, keep the element path).
        var seg = SegRelative.Match(id);
        if (seg.Success)
            return seg.Groups[1].Value + seg.Groups[2].Value;

        // XML family — strip the DEEPEST [n] predicate only; leave any other predicates intact.
        if (id.StartsWith('/'))
        {
            var matches = XpathPredicate.Matches(id);
            if (matches.Count == 0) return null; // no line predicate → header-scope
            var deepest = matches[^1];
            return id.Remove(deepest.Index, deepest.Length);
        }

        // JSON — replace the FIRST array index with '*' so the key is identical across lines.
        var json = JsonArrayIndex.Match(id);
        if (json.Success)
        {
            var g = json.Groups[1];
            return id.Remove(g.Index, g.Length).Insert(g.Index, "*");
        }

        // raw:{label}, unknown ids → header-scope (no relative alias).
        return null;
    }
}
