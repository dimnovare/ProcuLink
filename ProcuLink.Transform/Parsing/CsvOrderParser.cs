using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses CSV purchase order files (comma or semicolon delimited).
/// Column names are normalised to lowercase before matching, so headers like
/// "BuyerItemCode", "buyerItemCode", and "Buyer Item Code" all resolve correctly.
/// Supported column aliases:
///   line number   — linenumber
///   buyer code    — buyeritemcode, itemcode
///   description   — description
///   quantity      — quantity
///   unit          — unit
///   unit price    — unitprice, price
///   po number     — ponumber
///   order date    — orderdate
///   buyer name    — buyername
///   currency      — currency
///   ship-to       — shiptoname, shiptostreet, shiptocity, shiptopostalcode, shiptocountry, …
///   bill-to       — billtoname, billtostreet, billtocity, billtopostalcode, billtocountry, …
/// The ship-to / bill-to columns are read ONLY when a header names them; nothing is
/// inferred from column position (see BuildParties). Full alias list is in RawRowMap.
/// </summary>
public sealed class CsvOrderParser : IPurchaseOrderParser
{
    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".csv", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        // Buffer + NORMALIZE so the parse is byte-deterministic across platforms. The
        // embedded onboarding fixture parsed correctly on Windows but to an EMPTY order on
        // Linux (prod) — same parser, divergent bytes: git stores the CSV LF-normalized, the
        // Windows working copy is CRLF, and the trailing blank line was counted as an extra
        // empty row on one platform but not the other. Decoding UTF-8 explicitly (BOM-safe),
        // unifying line endings to \n, and dropping trailing blank lines removes every
        // OS/encoding-dependent variable before CsvHelper sees the text.
        using var src = new MemoryStream();
        await fileStream.CopyToAsync(src, ct);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        var text = utf8.GetString(src.ToArray());
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];  // strip a UTF-8 BOM
        // Collapse every line ending to \n, drop trailing blank lines, then re-emit CRLF.
        // CRLF is the form proven to parse correctly on BOTH platforms (Windows dev + real
        // customer CSV uploads on Linux prod); the git-normalized LF-only embedded fixture
        // was the only input that parsed to an empty order on Linux, so we never feed bare LF.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n', ' ', '\t').Replace("\n", "\r\n");
        var ms = new MemoryStream(utf8.GetBytes(text));

        var delimiter = DetectDelimiter(ms);
        ms.Position = 0;

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter              = delimiter,
            HeaderValidated        = null!,
            MissingFieldFound      = null!,
            PrepareHeaderForMatch  = args => NormalizeHeader(args.Header)
        };

        using var reader = new StreamReader(ms, utf8);
        using var csv    = new CsvReader(reader, config);

        csv.Context.RegisterClassMap<RawRowMap>();
        var rows = new List<RawRow>();
        try
        {
            await foreach (var row in csv.GetRecordsAsync<RawRow>(ct))
                rows.Add(row);
        }
        catch (CsvHelper.ReaderException)
        {
            // No header column matched any known alias, so CsvHelper has no members to
            // map and raises "No members are mapped for type 'RawRow'". This is a
            // malformed / unrecognised CSV, not an engine fault: degrade to an empty
            // order rather than leaking a third-party exception with an internal type
            // name. The upstream ingestion layer surfaces an empty/no-line order as a
            // reviewable failure ("we couldn't read this file").
            return new ParsedOrder(null, null, null, null, Array.Empty<ParsedOrderLine>());
        }

        // A ';' delimiter is a genuine locale DECLARATION — Excel emits it precisely because
        // the comma is already taken as the decimal separator — so it seeds the numeric
        // convention. It is only a seed, though: it used to be the SOLE signal, which meant a
        // tab- or comma-delimited European file had no locale at all and read "1.000" as 1.0.
        // The document's own numbers now decide first (see BuildParsedOrder).
        return BuildParsedOrder(rows, declared: delimiter == ";"
            ? DecimalConvention.Comma
            : DecimalConvention.Unknown);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string DetectDelimiter(Stream stream)
    {
        using var peekReader = new StreamReader(stream, leaveOpen: true);
        var firstLine = peekReader.ReadLine() ?? string.Empty;
        // Tab-delimited (TSV): a tab in the header with no comma is the reliable signal.
        // Sniffed before ';' so a tab file is never mistaken for a single-column CSV.
        if (firstLine.Contains('\t') && !firstLine.Contains(',')) return "\t";
        // If the header row contains ';' and no ',' then the file is semicolon-delimited
        // (and, being a European convention, the comma is the decimal separator there).
        return firstLine.Contains(';') && !firstLine.Contains(',') ? ";" : ",";
    }

    private static string NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return string.Empty;

        return new string(header
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static ParsedOrder BuildParsedOrder(List<RawRow> rows, DecimalConvention declared)
    {
        // Header-level fields come from the first non-null value across all rows
        var poNumber   = rows.Select(r => r.PoNumber  ).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var buyerName  = rows.Select(r => r.BuyerName ).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var currency   = rows.Select(r => r.Currency  ).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var orderDateRaw = rows.Select(r => r.OrderDate).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var (orderDate, orderDateAmbiguous) = DateParsing.TryParseHeaderDate(orderDateRaw);

        // Decide the decimal convention from the WHOLE FILE before reading any single cell.
        // "1.000" is one thousand in Germany and one in the UK — no cell can answer that
        // alone, but a column containing "73,22" or "1.234,56" settles it for every cell in
        // it. Preference order: this column's own evidence, then the whole document's, then
        // whatever the delimiter declared. Where none of the three decides, the ambiguous
        // cells are flagged for review rather than guessed at.
        var qtyTokens   = rows.Select(r => r.Quantity).ToList();
        var priceTokens = rows.Select(r => r.UnitPrice).ToList();
        var document    = NumberParsing.InferDecimalConvention(qtyTokens.Concat(priceTokens));
        var qtyConvention = NumberParsing.FirstKnown(
            NumberParsing.InferDecimalConvention(qtyTokens), document, declared);
        var priceConvention = NumberParsing.FirstKnown(
            NumberParsing.InferDecimalConvention(priceTokens), document, declared);

        int autoLineNum = 1;
        var lines = new List<ParsedOrderLine>(rows.Count);

        foreach (var raw in rows)
        {
            var lineNumber  = int.TryParse(raw.LineNumber, out var ln) ? ln : autoLineNum;
            var (qtyVal,   qtyAmbiguous)   = NumberParsing.TryParseFlexibleDecimal(raw.Quantity,  qtyConvention);
            var (priceVal, priceAmbiguous) = NumberParsing.TryParseFlexibleDecimal(raw.UnitPrice, priceConvention);
            var buyerCode   = NullIfEmpty(raw.BuyerItemCode ?? raw.ItemCode) ?? string.Empty;

            lines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: buyerCode,
                Description:   NullIfEmpty(raw.Description),
                Quantity:      qtyVal ?? 0m,
                Unit:          NullIfEmpty(raw.Unit),
                UnitPrice:     priceVal,
                // Refuse to deliver a silently-wrong number: a quantity or unit price the
                // parser could not unambiguously read (e.g. scientific notation "1.5e2",
                // letters) flags the line so it surfaces for human review.
                NeedsReview:   qtyAmbiguous || priceAmbiguous,
                ReviewReason:  NumberParsing.BuildAmbiguityReason(qtyAmbiguous, priceAmbiguous)
            ));

            autoLineNum++;
        }

        return new ParsedOrder(poNumber, orderDate, buyerName, currency, lines,
            // Ship-to / bill-to, read ONLY from columns whose header named them. See BuildParties.
            Parties: BuildParties(rows),
            // A CSV declares nothing about its date ordering, so "03/04/2026" is a genuine
            // coin-flip. The day-first reading is emitted, but the order is flagged so a
            // human confirms it rather than the guess shipping silently.
            NeedsReview:  orderDateAmbiguous,
            ReviewReason: DateParsing.BuildAmbiguityReason(orderDateAmbiguous, "order date", orderDateRaw));
    }

    /// <summary>
    /// Ship-to / bill-to parties, or <c>null</c> when the file names neither.
    ///
    /// <para>A CSV is positional: nothing in a row says what a column MEANS. So a party is only
    /// read where a header explicitly named the field (see <c>RawRowMap</c>), and a column is
    /// never inferred from its position — a delivery address invented from layout is worse than
    /// no delivery address, because it is indistinguishable from one the buyer actually stated.
    /// A file with no such header keeps producing <c>Parties == null</c>, exactly as before.</para>
    ///
    /// <para>These are header-level values in a flat per-line CSV, so each is taken from the
    /// first row that states it — the same rule PoNumber/BuyerName/Currency already use.</para>
    /// </summary>
    private static IReadOnlyList<ParsedParty>? BuildParties(List<RawRow> rows)
    {
        string? First(Func<RawRow, string?> selector) =>
            NullIfEmpty(rows.Select(selector).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)));

        var parties = new List<ParsedParty>(2);

        AddIfAnyFieldPresent(parties, "shipTo",
            First(r => r.ShipToName), First(r => r.ShipToStreet), First(r => r.ShipToCity),
            First(r => r.ShipToPostalCode), First(r => r.ShipToCountry),
            First(r => r.ShipToContact), First(r => r.ShipToEmail), First(r => r.ShipToPhone));

        AddIfAnyFieldPresent(parties, "billTo",
            First(r => r.BillToName), First(r => r.BillToStreet), First(r => r.BillToCity),
            First(r => r.BillToPostalCode), First(r => r.BillToCountry),
            First(r => r.BillToContact), First(r => r.BillToEmail), First(r => r.BillToPhone));

        return parties.Count > 0 ? parties : null;
    }

    /// <summary>
    /// Appends a party only when the document stated at least one of its fields. An
    /// all-null party would still denormalise into sixteen NULL columns, but it would
    /// also write an empty <c>order_parties</c> row claiming the document named a
    /// delivery party when it named nothing.
    /// </summary>
    private static void AddIfAnyFieldPresent(
        List<ParsedParty> parties, string role,
        string? name, string? street, string? city, string? postalCode,
        string? country, string? contactName, string? email, string? phone)
    {
        if (name is null && street is null && city is null && postalCode is null
            && country is null && contactName is null && email is null && phone is null)
            return;

        parties.Add(new ParsedParty(role,
            Name: name, Street: street, City: city, PostalCode: postalCode, Country: country,
            ContactName: contactName, Email: email, Phone: phone));
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── Internal CSV row model ─────────────────────────────────────────────

    private sealed class RawRow
    {
        public string? PoNumber      { get; set; }
        public string? OrderDate     { get; set; }
        public string? Currency      { get; set; }
        public string? BuyerName     { get; set; }
        public string? LineNumber    { get; set; }
        // Two separate properties so ClassMap can assign via different column names
        public string? BuyerItemCode { get; set; }
        public string? ItemCode      { get; set; }
        public string? Description   { get; set; }
        public string? Quantity      { get; set; }
        public string? Unit          { get; set; }
        public string? UnitPrice     { get; set; }
        // Party columns. A flat CSV carries these as header-level values repeated on every
        // data row, exactly like PoNumber/BuyerName above.
        public string? ShipToName       { get; set; }
        public string? ShipToContact    { get; set; }
        public string? ShipToStreet     { get; set; }
        public string? ShipToCity       { get; set; }
        public string? ShipToPostalCode { get; set; }
        public string? ShipToCountry    { get; set; }
        public string? ShipToEmail      { get; set; }
        public string? ShipToPhone      { get; set; }
        public string? BillToName       { get; set; }
        public string? BillToContact    { get; set; }
        public string? BillToStreet     { get; set; }
        public string? BillToCity       { get; set; }
        public string? BillToPostalCode { get; set; }
        public string? BillToCountry    { get; set; }
        public string? BillToEmail      { get; set; }
        public string? BillToPhone      { get; set; }
    }

    private sealed class RawRowMap : ClassMap<RawRow>
    {
        public RawRowMap()
        {
            Map(m => m.PoNumber     ).Name("ponumber", "purchaseordernumber", "ordernumber", "po");
            Map(m => m.OrderDate    ).Name("orderdate");
            Map(m => m.Currency     ).Name("currency");
            Map(m => m.BuyerName    ).Name("buyername");
            Map(m => m.LineNumber   ).Name("linenumber", "lineno", "line");
            Map(m => m.BuyerItemCode).Name("buyeritemcode", "buyercode", "sku");
            Map(m => m.ItemCode     ).Name("itemcode", "item");
            Map(m => m.Description  ).Name("description");
            Map(m => m.Quantity     ).Name("quantity", "qty");
            Map(m => m.Unit         ).Name("unit");
            // "price" is an alias for "unitprice"
            Map(m => m.UnitPrice    ).Name("unitprice", "price");

            // ── Delivery / invoice party columns ────────────────────────────────
            // A CSV is positional, so a party may only be read when a HEADER NAMES IT.
            // Every alias below is an explicit ship-to / deliver-to / bill-to label; none
            // is inferred from column position, and a file that names no such column keeps
            // producing no parties at all. Header text is normalised to letters+digits
            // lowercase before matching (NormalizeHeader), so "Ship To Name", "ShipTo_Name"
            // and "shiptoname" are the same alias.
            Map(m => m.ShipToName      ).Name("shiptoname", "shipto", "deliveryname", "deliverto", "delivertoname", "deliveryparty");
            Map(m => m.ShipToContact   ).Name("shiptocontact", "shiptocontactname", "shiptoattention", "deliverycontact");
            Map(m => m.ShipToStreet    ).Name("shiptostreet", "shiptoaddress", "shiptoaddress1", "shiptoaddressline1", "deliveryaddress", "deliverystreet");
            Map(m => m.ShipToCity      ).Name("shiptocity", "deliverycity", "shiptotown");
            Map(m => m.ShipToPostalCode).Name("shiptopostalcode", "shiptopostcode", "shiptozip", "shiptozipcode", "deliverypostalcode", "deliverypostcode", "deliveryzip");
            Map(m => m.ShipToCountry   ).Name("shiptocountry", "deliverycountry");
            Map(m => m.ShipToEmail     ).Name("shiptoemail", "deliveryemail");
            Map(m => m.ShipToPhone     ).Name("shiptophone", "deliveryphone");

            Map(m => m.BillToName      ).Name("billtoname", "billto", "invoiceto", "invoicetoname", "invoicename");
            Map(m => m.BillToContact   ).Name("billtocontact", "billtocontactname", "billtoattention", "invoicecontact");
            Map(m => m.BillToStreet    ).Name("billtostreet", "billtoaddress", "billtoaddress1", "billtoaddressline1", "invoiceaddress");
            Map(m => m.BillToCity      ).Name("billtocity", "invoicecity", "billtotown");
            Map(m => m.BillToPostalCode).Name("billtopostalcode", "billtopostcode", "billtozip", "billtozipcode", "invoicepostalcode", "invoicezip");
            Map(m => m.BillToCountry   ).Name("billtocountry", "invoicecountry");
            Map(m => m.BillToEmail     ).Name("billtoemail", "invoiceemail");
            Map(m => m.BillToPhone     ).Name("billtophone", "invoicephone");
        }
    }
}
