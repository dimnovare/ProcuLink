namespace ProcuLink.Core.Documents;

/// <summary>A condition on one header field that decides whether an output sends. No condition means always.</summary>
public sealed record SendWhen(string Field, string Op, string Value);

public static class SendWhenOps
{
    public const string Is = "is", IsNot = "is_not", Contains = "contains";

    public static readonly string[] All = [Is, IsNot, Contains];

    /// <summary>Trimmed, case-insensitive. A missing field reads as empty; an unknown op matches nothing, so it holds rather than sends.</summary>
    public static bool Matches(SendWhen? rule, IReadOnlyDictionary<string, string> header)
    {
        if (rule is null) return true;
        var actual = header.GetValueOrDefault(rule.Field, "").Trim();
        var wanted = rule.Value.Trim();
        return rule.Op switch
        {
            Is => actual.Equals(wanted, StringComparison.OrdinalIgnoreCase),
            IsNot => !actual.Equals(wanted, StringComparison.OrdinalIgnoreCase),
            Contains => wanted.Length > 0 && actual.Contains(wanted, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
