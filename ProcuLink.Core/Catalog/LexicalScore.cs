namespace ProcuLink.Core.Catalog;

/// <summary>
/// Dependency-free lexical match score shared by the order path (catalog retrieval) and the
/// lookup-suggestion path: exact code/barcode equality scores highest, then substring
/// containment, then shared-token overlap. Pure and in-memory, so deterministic.
/// </summary>
public static class LexicalScore
{
    private static readonly char[] TokenSeparators =
        { ' ', '\t', '\r', '\n', ',', ';', '.', '/', '\\', '-', '_', '(', ')', '[', ']', '{', '}', ':', '"', '\'', '#', '*', '+', '&' };

    /// <summary>Lowercase, punctuation-split token set; drops tokens shorter than 2 chars as noise.</summary>
    public static HashSet<string> Tokenize(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return set;
        foreach (var raw in text.ToLowerInvariant().Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length >= 2) set.Add(raw);
        }
        return set;
    }

    public static int Score(
        string queryCode, IReadOnlySet<string> queryTokens,
        string candidateCode, string? candidateText, string? candidateBarcode)
    {
        var buyerCode = (queryCode ?? string.Empty).Trim();
        var buyerCodeLower = buyerCode.ToLowerInvariant();
        var code = (candidateCode ?? string.Empty).Trim();
        var codeLower = code.ToLowerInvariant();
        var barcode = (candidateBarcode ?? string.Empty).Trim();
        var name = candidateText ?? string.Empty;

        var score = 0;

        // Strongest signals: exact code or exact barcode match against the query code.
        if (buyerCodeLower.Length > 0 && codeLower == buyerCodeLower) score += 100;
        if (buyerCodeLower.Length > 0 && barcode.Length > 0
            && string.Equals(barcode, buyerCode, StringComparison.OrdinalIgnoreCase)) score += 100;

        // Substring containment in either direction.
        if (buyerCodeLower.Length >= 3 && codeLower.Length >= 3
            && (codeLower.Contains(buyerCodeLower) || buyerCodeLower.Contains(codeLower))) score += 25;

        // Token overlap between the query text and the candidate code+text.
        var candidateTokens = Tokenize($"{code} {name}");
        if (queryTokens.Count > 0 && candidateTokens.Count > 0)
            score += queryTokens.Count(t => candidateTokens.Contains(t)) * 5;

        return score;
    }
}
