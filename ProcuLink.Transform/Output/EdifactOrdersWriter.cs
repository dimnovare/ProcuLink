using System.Globalization;
using System.Text;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Output;

/// <summary>Writes an EDIFACT ORDERS D.96A message (UNOC, default delimiters) from an order. Free text is released with <c>?</c>.</summary>
public static class EdifactOrdersWriter
{
    private static string Esc(string? text) => string.Concat((text ?? "").Select(c => char.IsControl(c) ? " " : ":+?'".Contains(c) ? "?" + c : c.ToString()));

    private const string Cyrillic = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
    private static readonly string[] CyrillicLatin =
        ["a", "b", "v", "g", "d", "e", "e", "zh", "z", "i", "i", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "kh", "ts", "ch", "sh", "shch", "", "y", "", "e", "iu", "ia"];

    /// <summary>
    /// UNOC is ISO 8859-1: a Latin-1 character is kept, Cyrillic is written in Latin letters (ICAO 9303), any other letter
    /// loses its diacritic when its base is Latin-1, and anything else becomes <c>?</c>. A control character becomes a space.
    /// </summary>
    private static string Fold(string? text) => string.Concat((text ?? "").Select(c =>
        char.IsControl(c) ? " "
        : c <= 'ÿ' ? c.ToString()
        : char.IsLowSurrogate(c) ? ""
        : char.IsHighSurrogate(c) ? "?"
        : Cyrillic.IndexOf(char.ToLowerInvariant(c)) is var i and >= 0
            ? char.IsUpper(c) && CyrillicLatin[i].Length > 0 ? char.ToUpperInvariant(CyrillicLatin[i][0]) + CyrillicLatin[i][1..] : CyrillicLatin[i]
        : c.ToString().Normalize(NormalizationForm.FormD)[0] is var b and <= 'ÿ' ? b.ToString() : "?"));

    /// <summary>Free text folded, split over its <paramref name="repeats"/> components of <paramref name="max"/> (counted before release), the rest cut.</summary>
    private static string Text(string? text, int max, int repeats = 1) => string.Join(":", StandardCodes.Cut(Fold(text), max, repeats).Select(Esc));

    /// <summary>An identifier as read, refused when longer than <paramref name="max"/> (counted before release) or when UNOC can't carry it: never cut, never folded.</summary>
    private static string Id(string? value, int max, string what) =>
        Fold(value) == (value ?? "") ? Esc(StandardCodes.Id(value, max, what)) : throw ValueTooLongException.NotCarried(what);

    // Full read precision, as the X12 850 writes it.
    private static string Num(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>NAD+{q}+[{gln}::9]++{name}+{street}+{city}++{postcode}+{iso}</c>, trailing empty elements left out. 9 = GS1, the one Peppol scheme EDIFACT has an agency code for.</summary>
    private static string Nad(string qualifier, string? gln, string name, string? street, string? city, string? postcode, string? country)
    {
        string[] elements = [qualifier, gln is null ? "" : Id(gln, 35, "Peppol ID") + "::9", "", Text(name, 35, 5), Text(street, 35, 3), Text(city, 35), "", Text(postcode, 9), StandardCodes.Country(country) ?? ""];
        var count = elements.Length;
        while (elements[count - 1].Length == 0) count--;
        return "NAD+" + string.Join("+", elements.Take(count));
    }

    /// <summary>A UNB party: the ID (blank → the X12 placeholder, a known limit shared with it), then <c>:qualifier</c> only when one is given.</summary>
    private static string Party(string? id, string? qualifier, string fallback) =>
        Text(string.IsNullOrWhiteSpace(id) ? fallback : id.Trim(), 35) + (string.IsNullOrWhiteSpace(qualifier) ? "" : ":" + Text(qualifier.Trim(), 4));

    public static string Write(PurchaseOrderEntity order, EdifactEnvelope? envelope = null)
    {
        var now = DateTime.UtcNow;
        var currency = string.IsNullOrWhiteSpace(order.Currency) ? "EUR" : order.Currency.ToUpperInvariant();
        var lines = order.Lines.OrderBy(l => l.LineNumber).ToList();

        // UNH..UNT, counted below.
        var message = new List<string>
        {
            "UNH+1+ORDERS:D:96A:UN",
            $"BGM+220+{Id(order.PoNumber, 35, "order number")}+9",
            $"DTM+137:{order.OrderDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}:102",
        };
        if (order.RequestedDeliveryDate is { } delivery) message.Add($"DTM+2:{delivery.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}:102");
        if (!string.IsNullOrWhiteSpace(order.PaymentTerms)) message.Add($"FTX+AAB+++{Text(order.PaymentTerms, 70, 5)}");
        // SG1 RFF: the order this one refers to.
        if (!string.IsNullOrWhiteSpace(order.BuyerOrderRef)) message.Add($"RFF+ON:{Id(order.BuyerOrderRef, 35, "order reference")}");

        // SG2 NAD → SG3 RFF → SG5 CTA/COM, each party's references and contact right after its NAD, all before CUX.
        if (OrderHeaderReader.ExtractBuyerName(order) is { } buyer && !string.IsNullOrWhiteSpace(buyer))
        {
            message.Add(Nad("BY", StandardCodes.Gln(OrderHeaderReader.BuyerParty(order)?.EdiCode), buyer, order.BillToStreet, order.BillToCity, order.BillToPostalCode, order.BillToCountry));
            if (!string.IsNullOrWhiteSpace(order.BuyerTaxId)) message.Add($"RFF+VA:{Id(order.BuyerTaxId, 35, "VAT number")}");
            if (!string.IsNullOrWhiteSpace(order.ContactName) || !string.IsNullOrWhiteSpace(order.ContactEmail) || !string.IsNullOrWhiteSpace(order.ContactPhone))
            {
                message.Add(string.IsNullOrWhiteSpace(order.ContactName) ? "CTA+OC" : $"CTA+OC+:{Text(order.ContactName, 35)}");
                if (!string.IsNullOrWhiteSpace(order.ContactEmail)) message.Add($"COM+{Text(order.ContactEmail, 512)}:EM");
                if (!string.IsNullOrWhiteSpace(order.ContactPhone)) message.Add($"COM+{Text(order.ContactPhone, 512)}:TE");
            }
        }
        var supplier = OrderHeaderReader.SupplierParty(order);
        var supplierName = string.IsNullOrWhiteSpace(order.SupplierName) ? supplier?.Name : order.SupplierName;
        if (!string.IsNullOrWhiteSpace(supplierName))
        {
            message.Add(Nad("SU", StandardCodes.Gln(supplier?.EdiCode), supplierName, supplier?.Street, supplier?.City, supplier?.PostalCode, supplier?.Country));
            var supplierVat = string.IsNullOrWhiteSpace(order.SellerTaxId) ? supplier?.Vat : order.SellerTaxId;
            if (!string.IsNullOrWhiteSpace(supplierVat)) message.Add($"RFF+VA:{Id(supplierVat, 35, "VAT number")}");
        }
        if (OrderHeaderReader.ShipToName(order) is { } shipTo)
            message.Add(Nad("ST", null, shipTo, order.ShipToStreet, order.ShipToCity, order.ShipToPostalCode, order.ShipToCountry));
        message.Add($"CUX+2:{Esc(currency)}:9");
        foreach (var line in lines)
        {
            message.Add($"LIN+{line.LineNumber.ToString(CultureInfo.InvariantCulture)}++{Id(line.SupplierItemCode, 35, "item code")}:SA");
            if (!string.IsNullOrWhiteSpace(line.BuyerItemCode)) message.Add($"PIA+1+{Id(line.BuyerItemCode, 35, "buyer's item code")}:BP");
            if (!string.IsNullOrWhiteSpace(line.Description)) message.Add($"IMD+F++:::{Text(line.Description, 35, 2)}");
            message.Add($"QTY+21:{Num(line.Quantity)}:{StandardCodes.Rec20Unit(line.Unit)}");
            if (line.LineAmount is { } amount) message.Add($"MOA+203:{amount.ToString("F2", CultureInfo.InvariantCulture)}");
            message.Add($"PRI+AAA:{StandardCodes.Price(line.UnitPrice)}");
            if (line.TaxRate is { } rate) message.Add($"TAX+7+VAT+++:::{rate.ToString("0.##", CultureInfo.InvariantCulture)}");
        }
        message.Add("UNS+S");
        // Summary MOA, each only when read: 125 taxable amount (total_net), 124 tax amount, 86 message total.
        foreach (var (qualifier, amount) in new[] { ("125", order.SubTotal), ("124", order.TaxTotal), ("86", order.GrandTotal) })
            if (amount is { } a) message.Add($"MOA+{qualifier}:{a.ToString("F2", CultureInfo.InvariantCulture)}");
        message.Add($"CNT+2:{lines.Count.ToString(CultureInfo.InvariantCulture)}");
        message.Add($"UNT+{message.Count + 1}+1");

        var segments = new List<string>
        {
            $"UNB+UNOC:3+{Party(envelope?.SenderId, envelope?.SenderQualifier, X12TransformService.DefaultSenderId)}+{Party(envelope?.RecipientId, envelope?.RecipientQualifier, X12TransformService.DefaultReceiverId)}+{now:yyMMdd}:{now:HHmm}+{X12TransformService.InterchangeControl}",
        };
        segments.AddRange(message);
        segments.Add($"UNZ+1+{X12TransformService.InterchangeControl}");
        return "UNA:+.? '" + string.Concat(segments.Select(s => s + "'")) + Environment.NewLine;
    }
}
