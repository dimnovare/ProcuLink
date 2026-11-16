namespace ProcuLink.Core.Documents;

/// <summary>A document's kind and sender in the stable form used to sort documents into flows.</summary>
public static class DocumentKind
{
    private const int MaxKind = 80;
    private const int MaxSenderKey = 200;
    private const int MaxFlowName = 120;

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

    /// <summary>A flow name for documents of this kind: the label capitalised and made plural; "Documents" when there is none.</summary>
    public static string FlowNameFor(string? kindLabel)
    {
        var label = kindLabel?.Trim() ?? "";
        if (label.Length == 0) return "Documents";
        label = char.ToUpperInvariant(label[0]) + label[1..];
        var lower = label.ToLowerInvariant();
        var name = lower.EndsWith('s') ? label
            : lower.EndsWith('y') && lower.Length > 1 && !"aeiou".Contains(lower[^2]) ? label[..^1] + "ies"
            : lower.EndsWith('x') || lower.EndsWith("ch") || lower.EndsWith("sh") ? label + "es"
            : label + "s";
        return name.Length > MaxFlowName ? name[..MaxFlowName].TrimEnd() : name;
    }
}
