using System.Globalization;
using ProcuLink.Core.Entities;

namespace ProcuLink.Transform.Output;

/// <summary>Interchange identity. The defaults are the same placeholders the X12 850 uses, a known limit shared with it.</summary>
public sealed record EdifactEnvelope(
    string Sender = X12TransformService.DefaultSenderId,
    string Recipient = X12TransformService.DefaultReceiverId,
    string Reference = X12TransformService.InterchangeControl);

/// <summary>Writes an EDIFACT ORDERS D.96A message (UNOC, default delimiters) from an order. Free text is released with <c>?</c>.</summary>
public static class EdifactOrdersWriter
{
    private static string Esc(string? text) => string.Concat((text ?? "").Select(c => char.IsControl(c) ? " " : ":+?'".Contains(c) ? "?" + c : c.ToString()));

    private static string Num(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary><c>NAD+{q}+++{name}+{street}+{city}++{postcode}+{iso}</c>, trailing empty elements left out.</summary>
    private static string Nad(string qualifier, string name, string? street, string? city, string? postcode, string? country)
    {
        string[] elements = [qualifier, "", "", Esc(name), Esc(street), Esc(city), "", Esc(postcode), StandardCodes.Country(country) ?? ""];
        var count = elements.Length;
        while (elements[count - 1].Length == 0) count--;
        return "NAD+" + string.Join("+", elements.Take(count));
    }

    public static string Write(PurchaseOrderEntity order, EdifactEnvelope envelope)
    {
        var now = DateTime.UtcNow;
        var currency = string.IsNullOrWhiteSpace(order.Currency) ? "EUR" : order.Currency.ToUpperInvariant();
        var lines = order.Lines.OrderBy(l => l.LineNumber).ToList();

        // UNH..UNT, counted below.
        var message = new List<string>
        {
            "UNH+1+ORDERS:D:96A:UN",
            $"BGM+220+{Esc(order.PoNumber)}+9",
            $"DTM+137:{order.OrderDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}:102",
        };
        if (order.RequestedDeliveryDate is { } delivery) message.Add($"DTM+2:{delivery.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}:102");

        // SG2 NAD → SG3 RFF → SG5 CTA/COM, each party's references and contact right after its NAD, all before CUX.
        if (OrderHeaderReader.ExtractBuyerName(order) is { } buyer && !string.IsNullOrWhiteSpace(buyer))
        {
            message.Add(Nad("BY", buyer, order.BillToStreet, order.BillToCity, order.BillToPostalCode, order.BillToCountry));
            if (!string.IsNullOrWhiteSpace(order.BuyerTaxId)) message.Add($"RFF+VA:{Esc(order.BuyerTaxId)}");
            if (!string.IsNullOrWhiteSpace(order.ContactName) || !string.IsNullOrWhiteSpace(order.ContactEmail) || !string.IsNullOrWhiteSpace(order.ContactPhone))
            {
                message.Add(string.IsNullOrWhiteSpace(order.ContactName) ? "CTA+OC" : $"CTA+OC+:{Esc(order.ContactName)}");
                if (!string.IsNullOrWhiteSpace(order.ContactEmail)) message.Add($"COM+{Esc(order.ContactEmail)}:EM");
                if (!string.IsNullOrWhiteSpace(order.ContactPhone)) message.Add($"COM+{Esc(order.ContactPhone)}:TE");
            }
        }
        var supplier = OrderHeaderReader.SupplierParty(order);
        var supplierName = string.IsNullOrWhiteSpace(order.SupplierName) ? supplier?.Name : order.SupplierName;
        if (!string.IsNullOrWhiteSpace(supplierName))
        {
            message.Add(Nad("SU", supplierName, supplier?.Street, supplier?.City, supplier?.PostalCode, supplier?.Country));
            var supplierVat = string.IsNullOrWhiteSpace(order.SellerTaxId) ? supplier?.Vat : order.SellerTaxId;
            if (!string.IsNullOrWhiteSpace(supplierVat)) message.Add($"RFF+VA:{Esc(supplierVat)}");
        }
        if (!string.IsNullOrWhiteSpace(order.ShipToName))
            message.Add(Nad("ST", order.ShipToName, order.ShipToStreet, order.ShipToCity, order.ShipToPostalCode, order.ShipToCountry));
        message.Add($"CUX+2:{Esc(currency)}:9");
        foreach (var line in lines)
        {
            message.Add($"LIN+{line.LineNumber.ToString(CultureInfo.InvariantCulture)}++{Esc(line.SupplierItemCode)}:SA");
            if (!string.IsNullOrWhiteSpace(line.Description)) message.Add($"IMD+F++:::{Esc(line.Description)}");
            message.Add($"QTY+21:{Num(line.Quantity)}:{StandardCodes.Rec20Unit(line.Unit)}");
            message.Add($"PRI+AAA:{Num(line.UnitPrice)}");
        }
        message.Add("UNS+S");
        message.Add($"CNT+2:{lines.Count.ToString(CultureInfo.InvariantCulture)}");
        message.Add($"UNT+{message.Count + 1}+1");

        var segments = new List<string>
        {
            $"UNB+UNOC:3+{Esc(envelope.Sender)}+{Esc(envelope.Recipient)}+{now:yyMMdd}:{now:HHmm}+{Esc(envelope.Reference)}",
        };
        segments.AddRange(message);
        segments.Add($"UNZ+1+{Esc(envelope.Reference)}");
        return "UNA:+.? '" + string.Concat(segments.Select(s => s + "'")) + Environment.NewLine;
    }
}
