using System.Globalization;
using System.Text.RegularExpressions;

namespace ProcuLink.Core.Documents;

/// <summary>One extra check a customer adds to a single value of a flow. A field without one has <c>Check: null</c>.</summary>
public sealed record FieldCheck(string Op, IReadOnlyList<string>? Values = null, string? Min = null, string? Max = null, Guid? LookupId = null);

public static class FieldCheckOps
{
    public const string OneOf = "one_of";
    public const string Between = "between";
    public const string InLookup = "in_lookup";
    public const string NotAfterToday = "not_after_today";
    public static readonly string[] All = [OneOf, Between, InLookup, NotAfterToday];
}

/// <summary>A lookup as a check reads it: its name and its trimmed "From" values, compared ignoring case.</summary>
public sealed record CheckLookup(string Name, IReadOnlySet<string> Values);

/// <summary>A failing check marks its value like a hard-to-read one; it never refuses a document.</summary>
public static class FieldChecks
{
    public const int MaxValues = 100;

    private static readonly Regex LimitShape = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>A limit as the customer writes it in our form: invariant "-?digits[.digits]". Empty is no limit.</summary>
    public static bool TryLimit(string? s, out decimal? limit)
    {
        limit = null;
        if (string.IsNullOrWhiteSpace(s)) return true;
        if (!LimitShape.IsMatch(s.Trim()) || !decimal.TryParse(s.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var d)) return false;
        limit = d;
        return true;
    }

    /// <summary>The lookups the flow's checks point at.</summary>
    public static IEnumerable<Guid> LookupIds(FlowFieldList? fields) =>
        (fields?.Fields ?? []).Where(f => f?.Check?.Op == FieldCheckOps.InLookup).Select(f => f.Check!.LookupId).OfType<Guid>().Distinct();

    /// <summary>Failed checks only. Single values only; a value with no text is Required's job, so it is skipped.</summary>
    public static IReadOnlyList<DocumentCheck> Run(ReadResult read, FlowFieldList fields, IReadOnlyDictionary<Guid, CheckLookup> lookups, DateOnly today)
    {
        var failed = new List<DocumentCheck>();
        foreach (var field in fields.Fields.Where(f => f.Check is not null))
        {
            var value = read.Fields.FirstOrDefault(r => r.Key == field.Key)?.Value?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (Fails(field.Label, field.Check!, value, lookups, today) is { } message)
                failed.Add(new DocumentCheck(DocumentChecks.CustomCheck, false, field.Key, message));
        }
        return failed;
    }

    // A value the check cannot read is marked, never passed.
    private static string? Fails(string label, FieldCheck c, string value, IReadOnlyDictionary<Guid, CheckLookup> lookups, DateOnly today)
    {
        switch (c.Op)
        {
            case FieldCheckOps.OneOf:
                var allowed = Tidy(c.Values);
                return allowed.Contains(value, StringComparer.OrdinalIgnoreCase) ? null
                    : $"{label} should be one of: {string.Join(", ", allowed.Take(3))}{(allowed.Count > 3 ? $" or {allowed.Count - 3} more" : "")}.";
            case FieldCheckOps.Between:
                var readable = TryLimit(c.Min, out var min) & TryLimit(c.Max, out var max);
                if (readable && FieldValueParser.Number(value) is { } n && (min is null || n >= min) && (max is null || n <= max)) return null;
                return (Blank(c.Min), Blank(c.Max)) switch
                {
                    ({ } lo, { } hi) => $"{label} should be a number between {lo} and {hi}.",
                    ({ } lo, null) => $"{label} should be a number of at least {lo}.",
                    (null, { } hi) => $"{label} should be a number of at most {hi}.",
                    _ => $"{label} should be a number.",
                };
            case FieldCheckOps.InLookup:
                if (c.LookupId is not { } id || !lookups.TryGetValue(id, out var lookup)) return $"{label} couldn't be checked: its lookup was removed.";
                return lookup.Values.Contains(value) ? null : $"{label} isn't in the lookup \"{lookup.Name}\".";
            case FieldCheckOps.NotAfterToday:
                // The server knows only UTC: an org ahead of UTC reaches "today" early, so UTC tomorrow still passes.
                return FieldValueParser.TryDate(value, out var date) && date <= today.AddDays(1) ? null : $"{label} should be a date no later than today.";
            default:
                return $"{label} has a check this version doesn't know.";
        }
    }

    /// <summary>Trims values and limits, drops blank values and every parameter the op does not use. A field without a check stays as it is.</summary>
    public static FlowFieldList Normalise(FlowFieldList list) => list.Fields.All(f => f?.Check is null) ? list : new(list.Fields.Select(f => f?.Check is not { } c ? f : f with
    {
        Check = c.Op switch
        {
            FieldCheckOps.OneOf => new FieldCheck(c.Op, Values: Tidy(c.Values)),
            FieldCheckOps.Between => new FieldCheck(c.Op, Min: Blank(c.Min), Max: Blank(c.Max)),
            FieldCheckOps.InLookup => new FieldCheck(c.Op, LookupId: c.LookupId),
            FieldCheckOps.NotAfterToday => new FieldCheck(c.Op),
            _ => c,
        },
    }).ToList(), list.Tables);

    /// <summary>Why a check cannot be saved; null when it is fine or there is none.</summary>
    public static string? Problem(FlowField field, bool isColumn)
    {
        if (field.Check is not { } c) return null;
        if (isColumn) return "A check can be added to a single value, not to a table column.";
        if (!FieldCheckOps.All.Contains(c.Op)) return $"Check '{c.Op}' is not supported.";
        var label = field.Label;
        switch (c.Op)
        {
            case FieldCheckOps.OneOf when Tidy(c.Values).Count == 0:
                return $"Add at least one allowed value for {label}.";
            case FieldCheckOps.OneOf when c.Values!.Count > MaxValues || c.Values.Any(v => (v?.Length ?? 0) > LookupRules.MaxValueLength):
                return $"A check allows at most {MaxValues} values, each up to {LookupRules.MaxValueLength} characters.";
            case FieldCheckOps.Between when Blank(c.Min) is null && Blank(c.Max) is null:
                return $"Add a lowest or highest number for {label}.";
            case FieldCheckOps.Between when !TryLimit(c.Min, out _) || !TryLimit(c.Max, out _):
                return $"For {label}, write the numbers with digits only, like 1000 or 12.5.";
            case FieldCheckOps.Between when TryLimit(c.Min, out var min) && TryLimit(c.Max, out var max) && min > max:
                return $"For {label}, the lowest number is above the highest.";
            case FieldCheckOps.InLookup when c.LookupId is null:
                return $"Choose a lookup for {label}.";
            default:
                return null;
        }
    }

    private static List<string> Tidy(IReadOnlyList<string>? values) =>
        (values ?? []).Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
