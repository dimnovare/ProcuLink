using ProcuLink.Core.Entities;

namespace ProcuLink.Transform.Output;

/// <summary>
/// The EN 16931 VAT breakdown (BG-23) both invoice writers write: one standard-rated (S) group per normalised line rate. Nothing is guessed:
/// a document with no VAT is refused, because zero-rated, exempt and reverse charge are different categories and no meaning says which;
/// and totals that fit no rate given to one decimal (a blend of several rates, read without each line's rate) are refused too.
/// </summary>
public static class VatBreakdown
{
    public sealed record Group(decimal Percent, decimal Basis, decimal Tax);

    public const string NoVatReason = "This standard needs to know why there's no VAT: zero-rated, exempt or reverse charge.";
    public const string MixedRatesReason = "This standard needs each line's VAT rate: the totals don't fit one rate.";

    public static IReadOnlyList<Group> For(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines)
    {
        var groups = lines.GroupBy(l => PeppolBisInvoiceTransformService.NormalizePercent(l.TaxRate))
            .Select(g => (Percent: g.Key, Basis: g.Sum(l => l.LineTotal))).OrderByDescending(g => g.Basis).ToList();
        if (groups.Count <= 1)
        {
            // One rate: the header totals are the breakdown, as before; the rate must be one VAT rate, not a blend.
            if (invoice.TaxTotal == 0m || invoice.SubTotal == 0m) throw new VatBreakdownException(NoVatReason);
            var rate = Math.Round(invoice.TaxTotal / invoice.SubTotal * 100m, 1);
            if (Math.Abs(Math.Round(invoice.SubTotal * rate / 100m, 2) - invoice.TaxTotal) > 0.01m) throw new VatBreakdownException(MixedRatesReason);
            return [new(rate, invoice.SubTotal, invoice.TaxTotal)];
        }
        if (groups.Any(g => g.Percent == 0m)) throw new VatBreakdownException(NoVatReason);
        var taxes = groups.Select(g => Math.Round(g.Basis * g.Percent / 100m, 2)).ToArray();
        var rest = invoice.TaxTotal - taxes.Sum();
        if (groups.Sum(g => g.Basis) != invoice.SubTotal || Math.Abs(rest) > 0.01m * groups.Count) throw new VatBreakdownException(MixedRatesReason);
        taxes[0] += rest; // per-line rounding cents land on the largest group, so the groups add up to the VAT total (BR-CO-14)
        return [.. groups.Select((g, i) => new Group(g.Percent, g.Basis, taxes[i]))];
    }

    /// <summary>A line's rate as its breakdown group carries it (a single group may round a read 20.999 to 21.0).</summary>
    public static decimal LineRate(IReadOnlyList<Group> groups, InvoiceLineEntity line) =>
        groups.Count == 1 ? groups[0].Percent : PeppolBisInvoiceTransformService.NormalizePercent(line.TaxRate);
}

/// <summary>The VAT breakdown cannot be written without guessing; the message is the plain sentence shown to the person.</summary>
public sealed class VatBreakdownException(string message) : Exception(message);
