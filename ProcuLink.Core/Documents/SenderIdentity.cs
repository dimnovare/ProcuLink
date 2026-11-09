using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Core.Documents;

/// <summary>Who sent a document: a stable <see cref="Key"/> to scope hints by, and a <see cref="Label"/> to show.</summary>
public sealed record SenderIdentity(string Key, string Label)
{
    /// <summary>VAT id, else company name, else the sender's email domain; null when none of them is known.</summary>
    public static SenderIdentity? Of(ReadResult read, string source, string? sourceRef)
    {
        var vat = Value(read, FieldMeanings.SenderVatId);
        var name = Value(read, FieldMeanings.SenderName);

        if (vat is not null && NormaliseVat(vat) is { Length: >= 5 } normalisedVat)
            return new($"vat:{normalisedVat}", name ?? vat);

        if (name is not null && SupplierSuggestionScoring.NormalizeCompanyName(name) is { Length: >= 3 } normalisedName)
            return new($"name:{normalisedName}", name);

        if (EmailDomain(source, sourceRef) is { } domain) return new($"email:{domain}", domain);

        return null;
    }

    /// <summary>True when a hint taught for <paramref name="senderKey"/> may be used on this document: any sender, the same email channel, or the sender's VAT id or name found in the document text.</summary>
    public static bool Matches(string senderKey, string documentText, string source, string? sourceRef)
    {
        if (senderKey == "") return true;
        if (EmailDomain(source, sourceRef) is { } domain && senderKey == $"email:{domain}") return true;
        if (senderKey.StartsWith("vat:")) return NormaliseVat(documentText).Contains(senderKey[4..]);
        if (senderKey.StartsWith("name:"))
        {
            var words = string.Concat(documentText.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' '));
            return $" {string.Join(' ', words.Split(' ', StringSplitOptions.RemoveEmptyEntries))} ".Contains($" {senderKey[5..]} ");
        }
        return false;
    }

    private static string? EmailDomain(string source, string? sourceRef)
    {
        if (source != IntakeKind.Email && source != IntakeKind.Imap || sourceRef is null) return null;
        var at = sourceRef.LastIndexOf('@');
        if (at < 0) return null;
        var domain = sourceRef[(at + 1)..].Trim().TrimEnd('>').ToLowerInvariant();
        return domain.Length > 0 ? domain : null;
    }

    public static string NormaliseVat(string vat) =>
        new(vat.ToUpperInvariant().Where(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')).ToArray());

    private static string? Value(ReadResult read, string meaning) =>
        read.Fields.FirstOrDefault(f => f.Meaning == meaning && !string.IsNullOrWhiteSpace(f.Value))?.Value?.Trim();
}
