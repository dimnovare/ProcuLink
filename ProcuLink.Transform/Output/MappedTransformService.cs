using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;

namespace ProcuLink.Transform.Output;

/// <summary>
/// The value resolution every output-tree leaf goes through (<see cref="ResolveRule"/>) and the one
/// CSV cell escaper (<see cref="Escape(string, string, bool)"/>), shared by
/// <see cref="OutputTemplateEmitter"/> and the fixed CSV transforms. The order-row builder this class
/// was named for went with the order pipeline (plan 13b, 2026-11-13).
/// </summary>
public static class MappedTransformService
{
    /// <summary>
    /// Resolves one rule. Precedence for the source value:
    /// <list type="number">
    ///   <item><b>Expression</b> (Scriban) when non-blank — evaluated with <c>order</c> (and, when
    ///         <paramref name="lineScope"/> is true, <c>line</c>) in scope. A compile/eval error is
    ///         non-fatal: it falls back to the canonical/fixed value so the transform never crashes.</item>
    ///   <item><b>FixedValue</b>.</item>
    ///   <item><b>CanonicalField</b> looked up in <paramref name="row"/>.</item>
    /// </list>
    /// The chosen value is then run through the manipulator chain.
    /// </summary>
    internal static string? ResolveRule(
        OutputFieldRule rule, IReadOnlyDictionary<string, string> row, bool lineScope)
    {
        string? value = ResolveExpressionOrField(
            rule.Expression,
            fallbackFixedValue: rule.FixedValue,
            fallbackCanonicalField: rule.CanonicalField,
            row: row,
            lineScope: lineScope);

        foreach (var m in rule.FieldManipulators ?? new List<ManipulatorEntry>())
        {
            var manipulator = ManipulatorRegistry.Resolve(m.Type, m.Params);
            value = manipulator.Apply(value, row);
        }

        return value;
    }

    /// <summary>
    /// Shared value-selection helper for the output-rule path. Precedence:
    /// <c>Expression → FixedValue → CanonicalField</c>. Expression wins when present and non-blank;
    /// then the supplied fixed value; then the named field in <paramref name="row"/>. On an expression
    /// compile/eval failure, falls through to the fixed/field value (never throws).
    /// </summary>
    internal static string? ResolveExpressionOrField(
        string? expression,
        string? fallbackFixedValue,
        string? fallbackCanonicalField,
        IReadOnlyDictionary<string, string> row,
        bool lineScope)
    {
        if (!string.IsNullOrWhiteSpace(expression))
        {
            var result = lineScope
                ? ScribanFieldEvaluator.EvaluateLine(expression, row)
                : ScribanFieldEvaluator.EvaluateHeader(expression, row);

            if (result.Ok)
                return result.Value;

            // Expression failed — fall through to the fixed/field value so the transform survives
            // (fail-open is intentional and UNCHANGED). Log the failure so an authoring mistake in a
            // mapping expression is observable instead of silently producing the fallback value.
            TransformDiagnostics.CreateLogger(nameof(MappedTransformService)).LogWarning(
                "Output-field expression failed to evaluate ({Scope}); falling back to the " +
                "fixed/field value. Error: {Error}",
                lineScope ? "line" : "header", result.Error);
        }

        return fallbackFixedValue
            ?? (fallbackCanonicalField is not null && row.TryGetValue(fallbackCanonicalField, out var v) ? v : null);
    }

    /// <summary>
    /// CSV field escaping for an authored dialect (WP-15) — the ONE CSV cell escaper; the fixed
    /// CSV reaches it through <see cref="CsvTransformService.Escape"/>.
    ///
    /// <para><see cref="CsvTransformService.Escape(string)"/> is the one-arg overload, which delegates here
    /// rather than the two drifting apart.</para>
    ///
    /// <para>The delimiter is a STRING, not a char: a dialect may legitimately be a tab or a
    /// multi-character sequence, and a caller holding one should not have to prove it is a single
    /// character before it can escape correctly.</para>
    ///
    /// <para>CSV injection: every cell is formula-neutralised first (<see cref="CsvTransformService.NeutraliseFormula"/>),
    /// and ',' ';' and tab are always quoted whatever the dialect, so a spreadsheet whose locale
    /// splits on a different separator cannot start a new cell that check never saw.</para>
    /// </summary>
    internal static string Escape(string value, string delimiter, bool quoteAlways)
    {
        value = CsvTransformService.NeutraliseFormula(value);
        if (quoteAlways) return $"\"{value.Replace("\"", "\"\"")}\"";

        var needsQuoting =
            (delimiter.Length > 0 && value.Contains(delimiter, StringComparison.Ordinal))
            || value.IndexOfAny([',', ';', '\t', '"', '\n', '\r']) >= 0;

        return needsQuoting ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
