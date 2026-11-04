using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services.Documents;

/// <summary>
/// What an invoice must pass to go out without a person looking at it. Pure: reads the document,
/// returns plain-language problems, empty when clean. Signs are not checked, so a credit note
/// (every amount negative) balances exactly like an invoice. The duplicate check needs the
/// database and sits beside the one caller that combines both
/// (<c>OrderIngestionService.CollectInvoiceProblemsAsync</c>).
/// </summary>
public static class InvoiceChecks
{
    private const decimal Tolerance = 0.02m;

    public static IReadOnlyList<string> Run(PurchaseOrderEntity doc)
    {
        var problems = new List<string>();

        if (doc.GrandTotal is not { } total)
            problems.Add("Total not found on the invoice.");
        else if (doc.SubTotal is { } net && doc.TaxTotal is { } tax && Math.Abs(net + tax - total) > Tolerance)
            problems.Add("Net plus VAT does not equal the total.");

        if (doc.Lines.Any(l => l.LineAmount is not null || l.UnitPrice != 0) && doc.SubTotal is { } subTotal
            && Math.Abs(doc.Lines.Sum(l => l.LineAmount ?? l.Quantity * l.UnitPrice) - subTotal) > Tolerance)
            problems.Add("Line amounts do not add up to the net total.");

        foreach (var line in doc.Lines.Where(l => !string.IsNullOrWhiteSpace(l.ReviewReason)).OrderBy(l => l.LineNumber))
            problems.Add($"Line {line.LineNumber}: {line.ReviewReason}");

        if (!string.IsNullOrEmpty(doc.PaymentIban) && !IsValidIban(doc.PaymentIban))
            problems.Add("The bank account (IBAN) is not valid.");

        return problems;
    }

    /// <summary>ISO 13616: country letters, check digits, then mod-97 of the rearranged digits is 1.</summary>
    private static bool IsValidIban(string iban)
    {
        if (iban.Length is < 15 or > 34 || !char.IsAsciiLetter(iban[0]) || !char.IsAsciiLetter(iban[1])
            || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]) || !iban.All(char.IsAsciiLetterOrDigit))
            return false;

        var remainder = 0;
        foreach (var c in iban[4..] + iban[..4])
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : char.ToUpperInvariant(c) - 'A' + 10;
            remainder = (remainder * (value > 9 ? 100 : 10) + value) % 97;
        }
        return remainder == 1;
    }
}
