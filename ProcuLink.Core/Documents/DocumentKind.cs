namespace ProcuLink.Core.Documents;

/// <summary>A document's kind and sender in the stable form used to sort documents into flows.</summary>
public static class DocumentKind
{
    private const int MaxKind = 80;
    private const int MaxSenderKey = 200;
    private const int MaxFlowName = 80;

    private static readonly HashSet<string> Filler =
    [
        "a", "an", "the", "of", "for", "from", "to", "our", "your", "supplier", "vendor", "customer", "client",
        "document", "copy", "original", "scan", "scanned",
    ];

    /// <summary>
    /// Lower case, letters and digits only, filler words dropped, each word made singular; null when nothing is left.
    /// Synonyms, other languages, word order, abbreviations, misspellings and qualifiers are not merged.
    /// </summary>
    public static string? Normalize(string? label)
    {
        if (label is null) return null;
        var words = string.Concat(label.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' '))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Singular)
            .Where(w => !Filler.Contains(w));
        var kind = string.Join(' ', words);
        if (kind.Length > MaxKind) kind = kind[..MaxKind].TrimEnd();
        return kind.Length == 0 ? null : kind;
    }

    private static string Singular(string w)
    {
        if (w.EndsWith("ies")) return w[..^3] + "y";
        if (w.EndsWith("sses")) return w[..^2];
        if (w.EndsWith("xes") || w.EndsWith("ches") || w.EndsWith("shes") || w.EndsWith("zes")) return w[..^2];
        if (w.EndsWith('s') && w.Length > 3 && !w.EndsWith("ss") && !w.EndsWith("us") && !w.EndsWith("is")) return w[..^1];
        return w;
    }

    /// <summary>The sender's <c>vat:</c> or <c>name:</c> key; null for a sender known only by its email domain, or not at all.</summary>
    public static string? SenderKeyOf(ReadResult read, string source, string? sourceRef) =>
        SenderIdentity.Of(read, source, sourceRef)?.Key is { Length: <= MaxSenderKey } key
        && (key.StartsWith("vat:") || key.StartsWith("name:"))
            ? key
            : null;

    /// <summary>
    /// A flow name for documents of this kind: the label as read, tidied, with no plural and no word dropped
    /// (an all-capitals label in sentence case); "Documents" when no letter or digit is left.
    /// </summary>
    public static string FlowNameFor(string? kindLabel)
    {
        var name = string.Join(' ', string.Concat((kindLabel ?? "").Select(c =>
                char.IsWhiteSpace(c) ? " " : char.IsLetterOrDigit(c) || "-/&.'()".Contains(c) ? c.ToString() : ""))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > MaxFlowName)
        {
            var space = name.LastIndexOf(' ', MaxFlowName);
            name = name[..(space > 40 ? space : MaxFlowName)];
            if (name.Count(c => c == '(') > name.Count(c => c == ')')) name = name[..name.LastIndexOf('(')];
            name = name.TrimEnd();
        }
        if (!name.Any(char.IsLetterOrDigit)) return "Documents";
        var rest = name.Length > 4 && !name.Any(char.IsLower) ? name[1..].ToLowerInvariant() : name[1..];
        return char.ToUpperInvariant(name[0]) + rest;
    }
}
