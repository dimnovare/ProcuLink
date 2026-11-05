using System.Globalization;
using System.Text;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a supplier-ready CSV from a fully-resolved order entity.
/// Columns: PoNumber, OrderDate, Currency, BuyerName, SupplierItemCode, Description, Quantity, Unit,
/// UnitPrice, LineTotal, ShipToName, ShipToStreet, ShipToCity, ShipToPostalCode, ShipToCountry,
/// BillToName, BillToStreet, BillToCity, BillToPostalCode, BillToCountry, ContactName, ContactEmail,
/// ContactPhone.
/// The PO header fields (PoNumber/OrderDate/Currency/BuyerName) are repeated on every
/// line row so the CSV is self-contained and lossless: a supplier can reconcile the
/// delivery to a PO number and currency without out-of-band context. This matches the
/// JSON and XML default transforms, which already carry the order header. (Before
/// 2026-06-14 the default CSV was line-items-only and dropped PO number + currency —
/// a losslessness gap caught in live-prod QA.)
/// <para><b>Address/contact columns (2026-06).</b> The ship-to / bill-to / contact columns are an
/// additive enrichment carrying the canonical address fields the other formats already emit. Unlike
/// the XML/JSON/cXML transforms — which OMIT an absent block to stay byte-identical — CSV is a fixed
/// flat schema, so the columns are ALWAYS present (empty when the order carries no address). This
/// deliberately changes the fixed CSV header for every order; founder-approved ("all canonicals"),
/// and there are no live CSV-delivery suppliers to break.</para>
/// Values containing commas or double-quotes are RFC 4180 escaped.
/// </summary>
public sealed class CsvTransformService : ITransformService
{
    public bool CanTransform(OutputFormat format) => format == OutputFormat.Csv;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct,
        CxmlCredentialConfig? cxmlCredentials = null) // not used: CSV has no cXML Header
    {
        ValidateOrder(order);
        // B1: enforce the price>0 / qty>0 output invariant the X12/UBL/cXML transforms already apply.
        // For a FIXED CSV transform the entity's canonical columns ARE the emitted bytes, so a line
        // with UnitPrice<=0 or Quantity<=0 is held (revert to 'ready', no artifact) rather than
        // delivering a $0 / zero-qty line. NOTE: override / whole-document-template / OutputNode-tree
        // output emits via a different path that can legitimately transform values or drop lines
        // (IncludeWhen), so the guard lives HERE in the fixed transforms — not centrally — to avoid
        // pre-empting those features.
        OutputFieldValidator.ValidateEntity(order, format);

        var sb = new StringBuilder();
        sb.AppendLine(
            "PoNumber,OrderDate,Currency,BuyerName,SupplierItemCode,Description,Quantity,Unit,UnitPrice,LineTotal," +
            "ShipToName,ShipToStreet,ShipToCity,ShipToPostalCode,ShipToCountry," +
            "BillToName,BillToStreet,BillToCity,BillToPostalCode,BillToCountry," +
            "ContactName,ContactEmail,ContactPhone");

        var poNumber  = Cell(order.PoNumber);
        var orderDate = order.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var currency  = Cell(order.Currency);
        var buyerName = Cell(OrderHeaderReader.ExtractBuyerName(order));

        // Address / contact columns are order-level: hoist once, repeated on every row (like the PO
        // header fields above), keeping the CSV self-contained. Empty when the order carries none.
        var shipToName       = Cell(order.ShipToName);
        var shipToStreet     = Cell(order.ShipToStreet);
        var shipToCity       = Cell(order.ShipToCity);
        var shipToPostalCode = Cell(order.ShipToPostalCode);
        var shipToCountry    = Cell(order.ShipToCountry);
        var billToName       = Cell(order.BillToName);
        var billToStreet     = Cell(order.BillToStreet);
        var billToCity       = Cell(order.BillToCity);
        var billToPostalCode = Cell(order.BillToPostalCode);
        var billToCountry    = Cell(order.BillToCountry);
        var contactName      = Cell(order.ContactName);
        var contactEmail     = Cell(order.ContactEmail);
        var contactPhone     = Cell(order.ContactPhone);

        foreach (var l in order.Lines.OrderBy(x => x.LineNumber))
        {
            var lineTotal = l.Quantity * l.UnitPrice;

            sb.AppendLine(string.Join(",",
                poNumber,
                orderDate,
                currency,
                buyerName,
                Cell(l.SupplierItemCode),
                Cell(l.Description),
                l.Quantity.ToString(CultureInfo.InvariantCulture),
                Cell(l.Unit),
                l.UnitPrice.ToString(CultureInfo.InvariantCulture),
                lineTotal.ToString(CultureInfo.InvariantCulture),
                shipToName,
                shipToStreet,
                shipToCity,
                shipToPostalCode,
                shipToCountry,
                billToName,
                billToStreet,
                billToCity,
                billToPostalCode,
                billToCountry,
                contactName,
                contactEmail,
                contactPhone
            ));
        }

        var bytes  = Encoding.UTF8.GetBytes(sb.ToString());
        var stream = new MemoryStream(bytes);

        return Task.FromResult(TransformResult.For(OutputFormat.Csv, stream));
    }

    private static void ValidateOrder(PurchaseOrderEntity order)
    {
        var unresolved = order.Lines
            .Where(l => l.NeedsReview || string.IsNullOrWhiteSpace(l.SupplierItemCode))
            .Select(l => l.LineNumber)
            .OrderBy(n => n)
            .ToList();

        if (unresolved.Count > 0)
            throw new TransformValidationException(unresolved);
    }

    /// <summary>A text cell: null-safe <see cref="Escape"/>.</summary>
    internal static string Cell(string? value) => Escape(value ?? string.Empty);

    /// <summary>
    /// OWASP CSV injection: Excel/Sheets run a cell starting with = + - @ tab or CR as a formula; a
    /// leading ' makes it literal text. A plain signed number — digits with '.' or ',' separators, in
    /// any culture's format (-5, +3.20, -1,234.50, -5,50) — is left alone: it cannot be a formula, and
    /// the untyped mapped/designer CSV emitters cannot otherwise tell it from text.
    /// </summary>
    internal static string NeutraliseFormula(string value) =>
        value.Length > 0 && "=+-@\t\r".Contains(value[0]) && !IsPlainSignedNumber(value) ? "'" + value : value;

    private static bool IsPlainSignedNumber(string v) =>
        v.Length > 1 && v[0] is '+' or '-'
        && v.Skip(1).Any(char.IsAsciiDigit) && v.Skip(1).All(c => char.IsAsciiDigit(c) || c is '.' or ',');

    /// <summary>
    /// Formula-neutralised, RFC 4180-escaped comma CSV cell. The one implementation lives in
    /// <see cref="MappedTransformService.Escape(string, string, bool)"/>, so the fixed, invoice, mapped
    /// and designer CSV cannot drift apart again.
    /// </summary>
    internal static string Escape(string value) => MappedTransformService.Escape(value, ",", quoteAlways: false);
}
