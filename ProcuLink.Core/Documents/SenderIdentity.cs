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

        if (vat is not null && NormaliseVat(vat) is { Length: > 0 } normalisedVat)
            return new($"vat:{normalisedVat}", name ?? vat);

        if (name is not null && SupplierSuggestionScoring.NormalizeCompanyName(name) is { } normalisedName)
            return new($"name:{normalisedName}", name);

        if ((source == IntakeKind.Email || source == IntakeKind.Imap) && sourceRef is not null)
        {
            var at = sourceRef.LastIndexOf('@');
            if (at >= 0)
            {
                var domain = sourceRef[(at + 1)..].Trim().TrimEnd('>').ToLowerInvariant();
                if (domain.Length > 0) return new($"email:{domain}", domain);
            }
        }

        return null;
    }

    public static string NormaliseVat(string vat) =>
        new(vat.ToUpperInvariant().Where(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')).ToArray());

    private static string? Value(ReadResult read, string meaning) =>
        read.Fields.FirstOrDefault(f => f.Meaning == meaning && !string.IsNullOrWhiteSpace(f.Value))?.Value?.Trim();
}
