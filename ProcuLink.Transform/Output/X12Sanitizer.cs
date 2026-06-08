using ProcuLink.Core.Entities;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Output;

/// <summary>
/// X12 has no release / escape mechanism, so the only safe way to stop a delimiter
/// character (<c>*</c> element, <c>&gt;</c> component, <c>~</c> segment) inside a
/// value from injecting a phantom element / segment is to substitute it with a space.
///
/// <para>
/// For <em>free text</em> (a description) that substitution is acceptable and is the
/// historical behaviour. For an <em>identifier</em> (a buyer / supplier / unit code)
/// the same substitution silently corrupts a structured vendor part number — e.g.
/// <c>ABC*123&gt;REVB</c>, a part hierarchy, becomes the meaningless <c>ABC 123 REVB</c>
/// with no record that it happened. That is exactly the "silently corrupts vendor part
/// hierarchies" failure the pipeline analysis flagged.
/// </para>
///
/// <para>
/// This helper keeps the safe substitution for free text but makes identifier
/// contamination <em>loud</em>: <see cref="GuardCodeFields(ParsedOrder)"/> /
/// <see cref="GuardCodeFields(PurchaseOrderEntity)"/> raise a
/// <see cref="TransformValidationException"/> listing the affected lines, so a
/// delimiter-bearing part code is held for review in <c>/operations/exceptions</c>
/// rather than delivered mangled. A clean order (no delimiters in any code) is a
/// no-op and the emitted bytes are unchanged.
/// </para>
/// </summary>
public static class X12Sanitizer
{
    private const char ElementSep   = '*';
    private const char ComponentSep = '>';
    private const char SegmentSep   = '~';

    /// <summary>True if <paramref name="value"/> contains any X12 delimiter character.</summary>
    public static bool ContainsDelimiter(string? value) =>
        !string.IsNullOrEmpty(value) &&
        (value.IndexOf(ElementSep) >= 0 ||
         value.IndexOf(ComponentSep) >= 0 ||
         value.IndexOf(SegmentSep) >= 0);

    /// <summary>
    /// Substitutes X12 delimiter characters with spaces and trims. Safe for free text
    /// (descriptions). Returns <see cref="string.Empty"/> for null / empty input.
    /// </summary>
    public static string SanitizeFreeText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace(ElementSep, ' ')
            .Replace(ComponentSep, ' ')
            .Replace(SegmentSep, ' ')
            .Trim();
    }

    /// <summary>
    /// Throws <see cref="TransformValidationException"/> if any line's buyer item code
    /// or unit-of-measure contains an X12 delimiter (a substitution there would corrupt
    /// a structured identifier). No-op for the canonical model when every code is clean.
    /// </summary>
    public static void GuardCodeFields(ParsedOrder order)
    {
        var problems = new List<LineProblem>();

        foreach (var line in order.Lines)
        {
            if (ContainsDelimiter(line.BuyerItemCode))
                problems.Add(ContaminationProblem(line.LineNumber, "buyer item code", line.BuyerItemCode!));

            if (ContainsDelimiter(line.Unit))
                problems.Add(ContaminationProblem(line.LineNumber, "unit of measure", line.Unit!));
        }

        Throw(problems);
    }

    /// <summary>
    /// Throws <see cref="TransformValidationException"/> if any line's supplier item
    /// code, buyer item code, or unit-of-measure contains an X12 delimiter. No-op for
    /// the entity model when every code is clean.
    /// </summary>
    public static void GuardCodeFields(PurchaseOrderEntity order)
    {
        var problems = new List<LineProblem>();

        foreach (var line in order.Lines)
        {
            if (ContainsDelimiter(line.SupplierItemCode))
                problems.Add(ContaminationProblem(line.LineNumber, "supplier item code", line.SupplierItemCode!));

            if (ContainsDelimiter(line.BuyerItemCode))
                problems.Add(ContaminationProblem(line.LineNumber, "buyer item code", line.BuyerItemCode));

            if (ContainsDelimiter(line.Unit))
                problems.Add(ContaminationProblem(line.LineNumber, "unit of measure", line.Unit!));
        }

        Throw(problems);
    }

    private static LineProblem ContaminationProblem(int lineNumber, string field, string value) =>
        new(lineNumber, LineProblemKind.Sanitized,
            $"Line {lineNumber}: {field} \"{value}\" contains an X12 delimiter (* > ~); " +
            "X12 has no escape for these, so the value would be silently corrupted. Held for review.");

    private static void Throw(IReadOnlyList<LineProblem> problems)
    {
        if (problems.Count == 0) return;

        var lineNumbers = problems
            .Select(p => p.LineNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        throw new TransformValidationException(lineNumbers, problems);
    }
}
