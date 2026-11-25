using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ProcuLink.Core.Documents;

/// <summary>
/// A Peppol participant id (<c>scheme:value</c>, scheme <c>iso6523-actorid-upis</c>) and its SML DNS name
/// (Policy for use of identifiers 4.4.0, POLICY 7: Base32 of SHA-256 over the lower-cased <c>scheme:value</c>).
/// </summary>
public sealed partial record PeppolParticipant(string Scheme, string Value)
{
    public const string InvoiceDocType =
        "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2::Invoice##urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0::2.1";
    public const string CreditNoteDocType =
        "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2::CreditNote##urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0::2.1";

    private const string SchemePrefix = "iso6523-actorid-upis::";

    public override string ToString() => $"{Scheme}:{Value}";

    /// <summary>A trimmed <c>scheme:value</c>, split at the first colon (as the invoice adapter does), or null when it is not one.</summary>
    public static PeppolParticipant? Parse(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase)) t = t[SchemePrefix.Length..];
        var at = t.IndexOf(':');
        if (at < 0) return null;
        var scheme = t[..at].Trim();
        var value = t[(at + 1)..].Trim().Replace(" ", "");
        return SchemeShape().IsMatch(scheme) && value.Length is >= 1 and <= 50 && !value.Any(char.IsWhiteSpace)
            ? new PeppolParticipant(scheme, value) : null;
    }

    /// <summary>Belgium only: the enterprise number is the VAT digits, and <c>0208</c> is the scheme Belgium tells senders to use.
    /// Null when the mod-97 check digits do not match.</summary>
    public static PeppolParticipant? FromBelgianVat(string? vatId)
    {
        var m = BelgianVat().Match((vatId ?? "").Replace(" ", "").Replace(".", "").Replace("-", "").ToUpperInvariant());
        if (!m.Success) return null;
        var digits = m.Groups[1].Value.PadLeft(10, '0');
        // The check digits: 97 - (first 8 digits mod 97). A misread number is never looked up, so it can never read as "not on Peppol".
        return int.Parse(digits[8..]) == 97 - int.Parse(digits[..8]) % 97 ? new PeppolParticipant("0208", digits) : null;
    }

    public string SmlHost(string zone) =>
        $"{Base32(SHA256.HashData(Encoding.UTF8.GetBytes(ToString().ToLowerInvariant())))}.iso6523-actorid-upis.{zone}";

    /// <summary>RFC 4648 Base32, upper-case, no padding.</summary>
    private static string Base32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = ((buffer << 8) | b) & 0xFFFF;
            for (bits += 8; bits >= 5; bits -= 5) sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    [GeneratedRegex("^[0-9]{4}$")]
    private static partial Regex SchemeShape();

    [GeneratedRegex("^BE([01]?[0-9]{9})$")]
    private static partial Regex BelgianVat();
}
