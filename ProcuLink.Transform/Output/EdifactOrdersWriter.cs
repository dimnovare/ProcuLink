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
        if (OrderHeaderReader.ExtractBuyerName(order) is { } buyer && !string.IsNullOrWhiteSpace(buyer)) message.Add($"NAD+BY+++{Esc(buyer)}");
        if (!string.IsNullOrWhiteSpace(order.SupplierName)) message.Add($"NAD+SU+++{Esc(order.SupplierName)}");
        message.Add($"CUX+2:{Esc(currency)}:9");
        foreach (var line in lines)
        {
            message.Add($"LIN+{line.LineNumber.ToString(CultureInfo.InvariantCulture)}++{Esc(line.BuyerItemCode)}:IN");
            if (!string.IsNullOrWhiteSpace(line.Description)) message.Add($"IMD+F++:::{Esc(line.Description)}");
            message.Add($"QTY+21:{Num(line.Quantity)}");
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
