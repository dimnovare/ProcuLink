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
            _            => ($"{field} doesn't meet the rule ({@operator} {expected}).", null),
        };

        var title = TitleFor(fieldPath, @operator);
        var lead  = title is null ? string.Empty : $"{title}. ";
        var tail  = fix is null ? string.Empty : $" {fix}";
        return $"{where}{lead}{why}{tail}".Trim();
    }

    /// <summary>The catalog Title for a (field, operator), or null when the pair isn't a known catalog rule.</summary>
    public static string? TitleFor(string fieldPath, string @operator) =>
        TitleForCode(RuleCatalog.CodeFor(fieldPath, @operator));

    /// <summary>The catalog Title for a stored <c>{fieldPath}.{operator}</c> code (null for invariants / ad-hoc rules).</summary>
    public static string? TitleForCode(string? code) =>
        string.IsNullOrEmpty(code) ? null : RuleCatalog.Entries.FirstOrDefault(e => e.Code == code)?.Title;

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
