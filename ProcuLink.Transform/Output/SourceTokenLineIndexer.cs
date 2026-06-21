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
}
