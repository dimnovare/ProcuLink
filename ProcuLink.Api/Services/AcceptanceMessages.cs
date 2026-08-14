using System.Text;
using ProcuLink.Core.Entities;

namespace ProcuLink.Api.Services;

/// <summary>
/// Turns a failed acceptance rule into ONE plain-language sentence a procurement coordinator can act
/// on — replacing the developer template <c>"unitPrice ('100') failed rule max 50000"</c> (the
/// founder's "error messages don't make sense"). It leads with the well-known <see cref="RuleCatalog"/>
/// Title when the rule maps to a catalog entry, states what's wrong in human words, and appends a
/// concrete suggested fix where one is deterministic.
/// </summary>
public static class AcceptanceMessages
{
    /// <summary>A passing rule's (rarely shown) message — the catalog title or humanised field, "— OK".</summary>
    public static string ForPass(string fieldPath, string @operator) =>
        $"{TitleFor(fieldPath, @operator) ?? Capitalize(Humanize(fieldPath))} — OK";

    /// <summary>
    /// A FAILING rule as one human sentence: optional catalog title, the reason (actual vs expected),
    /// and a suggested fix where deterministic. Prefixed with "Line N:" for line-scoped rules.
    /// </summary>
    public static string ForFail(
        string fieldPath, string @operator, string? actualValue, string? expectedValue, int? lineNumber)
    {
        var field    = Humanize(fieldPath);
        var actual   = string.IsNullOrWhiteSpace(actualValue) ? "empty" : $"“{actualValue.Trim()}”";
        var expected = (expectedValue ?? string.Empty).Trim();
        var where    = lineNumber is int ln ? $"Line {ln}: " : string.Empty;

        (string why, string? fix) = @operator switch
        {
            "required"   => ($"{field} is required, but it's missing.", $"Enter {field}."),
            "equals"     => ($"{field} must be {expected} — it's {actual}.", $"Set {field} to {expected}."),
            "not_equals" => ($"{field} must not be {expected}.", null),
            "in"         => ($"{field} must be one of: {expected} — it's {actual}.", $"Set {field} to one of: {expected}."),
            "min" or "greater_than"
                         => ($"{field} must be at least {expected} — it's {actual}.", $"Raise {field} to {expected} or more."),
            "max" or "less_than"
                         => ($"{field} must be at most {expected} — it's {actual}.", $"Lower {field} to {expected} or less."),
            "contains"   => ($"{field} must contain “{expected}” — it's {actual}.", null),
            "max_length" => ($"{field} is too long — keep it to {expected} characters.", null),
            "date_sanity"=> ($"{field} doesn't look like a valid date ({actual}).", $"Check the date in {field}."),
            "not_label"  => ($"{field} still has a placeholder instead of a real value ({actual}).",
                             $"Replace the placeholder in {field} with the real value."),
            "vat_format" => ($"{field} isn't a valid VAT number ({actual}).", $"Enter a valid VAT number for {field}."),
            "line_amount_reconcile"
                         => ("The line amount doesn't add up — quantity × unit price doesn't match the stated total.",
                             "Check the quantity, unit price, and line amount."),
            _            => (expected.Length > 0
                                ? $"{field} doesn't meet the rule ({@operator} {expected})."
                                : $"{field} doesn't meet the rule ({@operator}).", null),
        };

        // The catalog title is returned SEPARATELY (OrderValidationResultDto.Title) and shown as the
        // card headline — so it is NOT repeated here. The message is the explanation + suggested fix.
        var tail = fix is null ? string.Empty : $" {fix}";
        return $"{where}{why}{tail}".Trim();
    }

    /// <summary>
    /// A rule that COULD NOT RUN, as one plain sentence naming what was missing.
    ///
    /// <para>These used to be reported as passes, so a coordinator read "Line amount reconciles with
    /// qty × price — OK" on an order whose document never printed a line amount. The sentence has to
    /// say which input was absent, because "not checked" alone reads like a bug rather than like a
    /// property of the document.</para>
    /// </summary>
    public static string ForNotEvaluated(string fieldPath, string @operator, int? lineNumber)
    {
        var field = Humanize(fieldPath);
        var where = lineNumber is int ln ? $"Line {ln}: " : string.Empty;

        var why = @operator switch
        {
            "line_amount_reconcile"
                        => "not checked — this document didn't state a line amount to reconcile against.",
            "date_sanity" => $"not checked — no printed date was captured for {field}.",
            "not_label"   => $"not checked — {field} is empty.",
            "vat_format"  => $"not checked — no VAT number was captured for {field}.",
            _             => $"not checked — {field} isn't available on this order.",
        };

        return $"{where}{why}";
    }

    /// <summary>The catalog Title for a (field, operator), or null when the pair isn't a known catalog rule.</summary>
    public static string? TitleFor(string fieldPath, string @operator) =>
        TitleForCode(RuleCatalog.CodeFor(fieldPath, @operator));

    /// <summary>The headline title for a stored code — from the rule catalog, or the output-check
    /// titles (zero price / buyer-code / sanitized) routed into the validation list. Null for
    /// invariants / ad-hoc rules (the UI then falls back to the field name).</summary>
    public static string? TitleForCode(string? code) =>
        string.IsNullOrEmpty(code) ? null
            : CodeToTitle.TryGetValue(code, out var t) ? t
            : OutputTitles.TryGetValue(code, out var ot) ? ot
            : null;

    /// <summary>Stable codes + headlines for output-render problems surfaced in the validation list
    /// (see <c>OutputFieldValidator.CollectEntityProblems</c>); their Message is already plain.</summary>
    // Namespaced under "output.*" so they never collide with a catalog acceptance-rule code
    // (e.g. a real "buyerItemCode.required" rule) — both could otherwise validate the same order.
    public const string OutputPriceCode = "output.unitPrice";
    public const string OutputBuyerCodeCode = "output.buyerItemCode";
    public const string OutputSanitizedCode = "output.sanitized";
    private static readonly Dictionary<string, string> OutputTitles = new(StringComparer.Ordinal)
    {
        [OutputPriceCode]     = "Unit price must be positive",
        [OutputBuyerCodeCode] = "Buyer item code required for this format",
        [OutputSanitizedCode] = "Value adjusted for the output",
    };

    /// <summary>O(1) code→title index over the (static, ~18-entry) catalog — built once, not scanned per result.</summary>
    private static readonly Dictionary<string, string> CodeToTitle =
        RuleCatalog.Entries
            .GroupBy(e => e.Code, StringComparer.Ordinal)   // tolerate any dup code (first wins)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);

    /// <summary>camelCase / dotted field path → spaced lower-case label ("unitPrice" → "unit price", "shipToVat" → "ship to vat").</summary>
    internal static string Humanize(string fieldPath)
    {
        if (string.IsNullOrWhiteSpace(fieldPath)) return "this field";
        var last = fieldPath.Split('.').Last();
        var sb = new StringBuilder(last.Length + 4);
        foreach (var c in last)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.Length == 0 ? "this field" : sb.ToString();
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
