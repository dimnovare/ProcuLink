using System.Globalization;
using System.Xml.Linq;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;

namespace ProcuLink.Transform.Output.Ksef;

/// <summary>
/// The Polish KSeF invoice file, FA(3) 1-0E (namespace http://crd.gov.pl/wzor/2025/06/25/13775/; mandatory in KSeF since 1 Feb 2026).
/// Version 1 writes a plain VAT invoice (RodzajFaktury VAT) in PLN at 23, 8 or 5 % only. Every value comes from the document:
/// nothing is rounded, converted or defaulted, and anything the file cannot prove (a NIP checksum, line arithmetic, the net,
/// VAT and gross totals) is refused with a plain reason. KSeF itself does not check the arithmetic (MF handbook part II), so we do.
/// Finally the file is checked against the official schema (<see cref="Fa3Schema"/>).
/// </summary>
public static class Fa3InvoiceWriter
{
    private static XNamespace Ns => Fa3Schema.Namespace;

    // FA(3) XSD annotations: 23/22 % → P_13_1/P_14_1, 8/7 % → _2, 5 % → _3. Only 23, 8 and 5 are written in version 1.
    private static readonly Dictionary<decimal, string> Bucket = new() { [23m] = "1", [8m] = "2", [5m] = "3" };

    /// <summary>Split payment (MPP, P_18A) may be mandatory from this gross total; we cannot know the goods, so version 1 refuses it.</summary>
    public const decimal SplitPaymentThreshold = 15000.00m;

    private static string N(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    public static (XDocument? Doc, string? Refusal) Build(InvoiceEntity inv, IReadOnlyList<InvoiceLineEntity> lines, PeppolPartyOptions parties, DateTime createdUtc)
    {
        if (inv.Currency != "PLN") return (null, $"The KSeF invoice file is made for invoices in PLN for now. This document is in {inv.Currency}.");
        if (inv.GrandTotal < 0) return (null, "Corrections (faktura korygująca) aren't supported yet. This document's total is negative.");
        if (!PolishNip.IsValid(parties.SellerVatId))
            return (null, $"The seller's tax number {parties.SellerVatId} isn't a valid Polish NIP. Check the seller's NIP on the document.");
        if (parties.SellerCountry != "PL")
            return (null, $"The seller's country must be Poland (PL) for a KSeF invoice. This document says {parties.SellerCountry}.");
        if (BuyerId(parties.BuyerVatId) is not { } buyerId)
            return (null, $"The buyer's tax number {parties.BuyerVatId} isn't a valid Polish NIP or EU VAT number.");

        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (!Bucket.ContainsKey(l.TaxRate))
                return (null, $"The KSeF invoice file supports VAT rates of 23%, 8% and 5% for now. Line {i + 1} has {N(l.TaxRate)}%.");
            var product = Math.Round(l.Quantity * l.UnitPrice, 2, MidpointRounding.AwayFromZero);
            if (product != l.LineTotal)
                return (null, $"Line {i + 1}: {N(l.Quantity)} × {N(l.UnitPrice)} is {N(product)}, but the line amount is {N(l.LineTotal)}. Check the line before making the KSeF file.");
        }
        var lineSum = lines.Sum(l => l.LineTotal);
        if (lineSum != inv.SubTotal)
            return (null, $"The line amounts add up to {N(lineSum)}, but the document's net total is {N(inv.SubTotal)}. Check the lines and totals before making the KSeF file.");

        // One rate: the VAT is the document's own total, which must match the net × rate within a cent per line (per-line rounding).
        // Several rates: each bucket's VAT is worked out, and together they must equal the document's VAT total exactly.
        var groups = lines.GroupBy(l => l.TaxRate).OrderBy(g => Bucket[g.Key])
            .Select(g => (Rate: g.Key, Net: g.Sum(l => l.LineTotal), Vat: Math.Round(g.Sum(l => l.LineTotal) * g.Key / 100, 2, MidpointRounding.AwayFromZero))).ToList();
        if (groups.Count == 1)
        {
            if (Math.Abs(inv.TaxTotal - groups[0].Vat) > 0.01m * lines.Count)
                return (null, $"The VAT worked out from the lines is {N(groups[0].Vat)}, but the document's VAT total is {N(inv.TaxTotal)}.");
            groups[0] = groups[0] with { Vat = inv.TaxTotal };
        }
        else if (groups.Sum(g => g.Vat) is var vat && vat != inv.TaxTotal)
            return (null, $"The VAT worked out from the lines is {N(vat)}, but the document's VAT total is {N(inv.TaxTotal)}.");
        if (inv.SubTotal + inv.TaxTotal != inv.GrandTotal)
            return (null, $"Net {N(inv.SubTotal)} plus VAT {N(inv.TaxTotal)} is {N(inv.SubTotal + inv.TaxTotal)}, but the document's total is {N(inv.GrandTotal)}.");
        if (inv.GrandTotal >= SplitPaymentThreshold)
            return (null, "Invoices of 15,000.00 PLN or more may need the split-payment mark (MPP), which isn't supported yet.");

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(Ns + "Faktura",
            new XElement(Ns + "Naglowek",
                new XElement(Ns + "KodFormularza", new XAttribute("kodSystemowy", "FA (3)"), new XAttribute("wersjaSchemy", "1-0E"), "FA"),
                E("WariantFormularza", "3"),
                E("DataWytworzeniaFa", createdUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                E("SystemInfo", "ProcuLink")),
            new XElement(Ns + "Podmiot1",
                new XElement(Ns + "DaneIdentyfikacyjne", E("NIP", PolishNip.Normalize(parties.SellerVatId)), E("Nazwa", parties.SellerName)),
                Address("PL", parties.SellerStreet, parties.SellerPostcode, parties.SellerCity)),
            new XElement(Ns + "Podmiot2",
                new XElement(Ns + "DaneIdentyfikacyjne", buyerId, E("Nazwa", parties.BuyerName)),
                parties.BuyerCountry is { } bc ? Address(bc, parties.BuyerStreet, parties.BuyerPostcode, parties.BuyerCity) : null,
                // Not a local government unit (JST) or VAT group member (GV): written 2 and shown in the confirm preview.
                E("JST", "2"), E("GV", "2")),
            new XElement(Ns + "Fa",
                E("KodWaluty", inv.Currency), E("P_1", inv.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), E("P_2", inv.InvoiceNumber),
                parties.DeliveryDate is { } sale && sale != inv.IssueDate ? E("P_6", sale.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null,
                groups.SelectMany(g => new[] { E("P_13_" + Bucket[g.Rate], N(g.Net)), E("P_14_" + Bucket[g.Rate], N(g.Vat)) }),
                E("P_15", N(inv.GrandTotal)),
                // A plain invoice: no cash method, self-billing, reverse charge or split payment; not exempt; no new means of transport; no margin.
                new XElement(Ns + "Adnotacje", E("P_16", "2"), E("P_17", "2"), E("P_18", "2"), E("P_18A", "2"),
                    new XElement(Ns + "Zwolnienie", E("P_19N", "1")), new XElement(Ns + "NoweSrodkiTransportu", E("P_22N", "1")),
                    E("P_23", "2"), new XElement(Ns + "PMarzy", E("P_PMarzyN", "1"))),
                E("RodzajFaktury", "VAT"),
                lines.Select((l, i) => new XElement(Ns + "FaWiersz", E("NrWierszaFa", (i + 1).ToString(CultureInfo.InvariantCulture)), E("P_7", l.Description),
                    E("P_8B", N(l.Quantity)), E("P_9A", N(l.UnitPrice)), E("P_11", N(l.LineTotal)), E("P_12", ((int)l.TaxRate).ToString(CultureInfo.InvariantCulture)))))));

        return Fa3Schema.FirstError(doc) is { } element
            ? (null, $"This file didn't pass the Ministry of Finance FA(3) schema check (at {element}). Nothing was made or sent.")
            : (doc, null);
    }

    private static XElement E(string name, string? value) => new(Ns + name, value);

    // TAdres: KodKraju + AdresL1 (+ AdresL2). Only what the document has is written; with no street the postcode and city are line 1.
    private static XElement? Address(string country, string? street, string? postcode, string? city)
    {
        var place = string.Join(" ", new[] { postcode, city }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(street) && place.Length == 0) return null;
        return new XElement(Ns + "Adres", E("KodKraju", country), E("AdresL1", string.IsNullOrWhiteSpace(street) ? place : street),
            !string.IsNullOrWhiteSpace(street) && place.Length > 0 ? E("AdresL2", place) : null);
    }

    // Podmiot2's id: a checksum-valid NIP, or an EU VAT number (country code other than PL + the rest as written). Anything else is refused.
    // KodUE is TKodyKrajowUE (Greece EL, Northern Ireland XI), never PL: a PL number is a NIP.
    private static object[]? BuyerId(string? raw)
    {
        if (PolishNip.IsValid(raw)) return [E("NIP", PolishNip.Normalize(raw))];
        var v = raw?.Trim().Replace(" ", "");
        var prefix = FieldValueParser.CountryFromVat(v) switch { "GR" => "EL", "GB" => "XI", "PL" => null, var c => c };
        return prefix is not null ? [E("KodUE", prefix), E("NrVatUE", v![2..])] : null;
    }
}
