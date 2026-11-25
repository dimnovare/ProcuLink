namespace ProcuLink.Core.Documents;

/// <summary>The Polish tax number (NIP): ten digits whose last is the weighted checksum of the first nine.</summary>
public static class PolishNip
{
    private static readonly int[] Weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];

    /// <summary>Ten digits, with a leading "PL", spaces and dashes removed; null when anything else is left.</summary>
    public static string? Normalize(string? raw)
    {
        var s = raw?.Trim().Replace(" ", "").Replace("-", "");
        if (s is null) return null;
        if (s.StartsWith("PL", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.Length == 10 && s.All(char.IsAsciiDigit) ? s : null;
    }

    /// <summary>Σ digit × weight mod 11 equals the tenth digit; a remainder of 10 is never valid.</summary>
    public static bool IsValid(string? raw) =>
        Normalize(raw) is { } n && Weights.Select((w, i) => w * (n[i] - '0')).Sum() % 11 == n[9] - '0';
}
