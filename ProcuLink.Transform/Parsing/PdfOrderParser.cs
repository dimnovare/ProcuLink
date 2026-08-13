using System.Globalization;
using System.Text.RegularExpressions;
using ProcuLink.Core.Services.Ocr;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Parses text-based PDF purchase orders. Falls back to <see cref="IDocumentOcrService"/>
/// when PdfPig extracts no text (image-only / scanned PDFs) and an OCR provider is configured.
///
/// <para>Every header field here is read from a printed LABEL — there is no schema, so a label is
/// the only thing that says what a value means. That constraint is why the ship-to read is
/// narrower than the structured parsers': see <c>BuildParties</c> for what it deliberately
/// refuses to infer.</para>
/// </summary>
public sealed class PdfOrderParser : IPurchaseOrderParser
{
    private readonly IDocumentOcrService? _ocrService;

    public PdfOrderParser() : this(null) { }

    public PdfOrderParser(IDocumentOcrService? ocrService)
    {
        _ocrService = ocrService;
    }

    private static readonly Regex PoNumberRegex = new(
        @"\b(?:po\s*(?:number|no\.?|#)|purchase\s*order)\s*[:#-]?\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OrderDateRegex = new(
        @"\b(?:order\s*date|date)\s*[:#-]?\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BuyerRegex = new(
        @"\b(?:buyer|customer|bill\s*to)\s*[:#-]?\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CurrencyRegex = new(
        @"\bcurrency\s*[:#-]?\s*(?<value>[A-Z]{3})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Ship-to ────────────────────────────────────────────────────────────────
    // A PDF is free text: the ONLY thing that says what a value means is a label printed
    // beside it. So each pattern below requires its own explicit label AND a separator, and
    // reads only the remainder of that same line.
    //
    // The separator is MANDATORY here (`[:#-]`, not the `[:#-]?` the PO-number and buyer
    // patterns use) and that is the whole design. Without it, "Ship To" alone would match
    // the BLOCK form —
    //
    //     Ship To
    //     Contoso Warehouse OY
    //     2 Example Road
    //     Example Town 00001
    //
    // — where the label is on its own line and the address arrives as unlabelled
    // continuation lines. Assigning those to street / city / postcode can only be done by
    // counting lines, i.e. by guessing from layout, and a guessed delivery address is worse
    // than none: nothing downstream can distinguish it from one the buyer actually stated.
    // That form is therefore deliberately NOT read. See the class doc.
    //
    // Ordered specific-before-general at the call site: "Ship To Address:" must reach the
    // street pattern, and does not reach this one because " Address:" is not a separator.
    private static readonly Regex ShipToNameRegex = new(
        @"\b(?:ship|deliver|delivery)\s*to\s*[:#-]\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShipToStreetRegex = new(
        @"\b(?:ship(?:ping)?\s*to\s*address|shipping\s*address|delivery\s*address)\s*[:#-]\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShipToCityRegex = new(
        @"\b(?:ship\s*to\s*city|delivery\s*city)\s*[:#-]\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShipToPostalCodeRegex = new(
        @"\b(?:ship\s*to\s*(?:postal\s*code|postcode|zip(?:\s*code)?)|delivery\s*(?:postal\s*code|postcode|zip(?:\s*code)?))\s*[:#-]\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShipToCountryRegex = new(
        @"\b(?:ship\s*to\s*country|delivery\s*country)\s*[:#-]\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The numeric groups accept REPEATED separators — "1.234,56", not just "1234,56".
    // They used to be `-?\d+(?:[.,]\d+)?`, a single separator only, so a grouped European
    // price failed to match the line at all and the ENTIRE ORDER LINE was dropped on the
    // floor: no value, no error, no review flag, nothing for anyone to notice.
    private const string NumericToken = @"-?\d+(?:[.,]\d+)*";

    private static readonly Regex LineWithPriceRegex = new(
        $@"^\s*(?<line>\d+)\s+(?<code>[A-Z0-9][A-Z0-9._/-]*)\s+(?<desc>.+?)\s+(?<qty>{NumericToken})\s+(?<unit>[A-Za-z]{{1,12}})\s+(?<price>{NumericToken})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LineWithoutPriceRegex = new(
        $@"^\s*(?<line>\d+)\s+(?<code>[A-Z0-9][A-Z0-9._/-]*)\s+(?<desc>.+?)\s+(?<qty>{NumericToken})\s+(?<unit>[A-Za-z]{{1,12}})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        // Buffer up-front so PdfPig and the OCR fallback can each consume a fresh stream
        // over the same bytes — input streams are not guaranteed to be seekable.
        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // Timeout-bounded so a pathological PDF can't hang the parse pipeline indefinitely.
        var textLines = (await PdfTextExtractor.ExtractLinesAsync(bytes, ct)).ToList();

        // OCR fallback for scanned / image-only PDFs (no-op when OCR provider not configured).
        if (textLines.Count == 0 && _ocrService is { IsAvailable: true })
        {
            using var ocrStream = new MemoryStream(bytes);
            var ocrText = await _ocrService.ExtractTextAsync(ocrStream, "application/pdf", ct);
            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                textLines = PdfTextExtractor.NormalizeLines(
                    PdfTextExtractor.SplitPageText(ocrText)).ToList();
            }
        }

        if (textLines.Count == 0)
            return new ParsedOrder(null, null, null, null, Array.Empty<ParsedOrderLine>());

        var orderDateRaw = FindFirstValue(textLines, OrderDateRegex);
        var (orderDate, orderDateAmbiguous) = DateParsing.TryParseHeaderDate(orderDateRaw);

        return new ParsedOrder(
            PoNumber:  FindFirstValue(textLines, PoNumberRegex),
            OrderDate: orderDate,
            BuyerName: FindFirstValue(textLines, BuyerRegex),
            Currency:  FindFirstValue(textLines, CurrencyRegex)?.ToUpperInvariant(),
            Lines:     ParseLines(textLines),
            // Ship-to, read ONLY from inline "<label>: <value>" lines. See BuildParties.
            Parties:   BuildParties(textLines),
            // A printed PDF declares nothing about its date ordering — the same reason this
            // parser cannot assert a decimal locale either. "03/04/2026" is flagged, not guessed.
            NeedsReview:  orderDateAmbiguous,
            ReviewReason: DateParsing.BuildAmbiguityReason(orderDateAmbiguous, "order date", orderDateRaw));
    }

    /// <summary>
    /// The ship-to party, or <c>null</c> when the document prints no inline ship-to label.
    ///
    /// <para><b>What this deliberately does not do.</b> Only the inline
    /// <c>"&lt;label&gt;: &lt;value&gt;"</c> form is read. The block form — a bare "Ship To" line
    /// followed by the address on the lines beneath it — is left unread, because deciding which
    /// continuation line is the street and which is the city can only be done by counting lines.
    /// That is a guess from layout, and a guessed delivery address is worse than an absent one:
    /// once persisted it is indistinguishable from an address the buyer actually stated, and it
    /// is emitted to the supplier with the same confidence.</para>
    ///
    /// <para>Where a document prints everything after one label —
    /// <c>"Ship To: Contoso Warehouse OY, 2 Example Road, Example Town"</c> — the whole remainder
    /// becomes the party NAME rather than being split into street/city. That is verbatim what the
    /// document labelled as the ship-to; splitting it on punctuation would be the same positional
    /// guess in a smaller disguise.</para>
    ///
    /// <para>No bill-to is built here. This parser already reads <c>"Bill To:"</c> as the BUYER
    /// name (<see cref="BuyerRegex"/>, unchanged), so a bill-to party derived from the identical
    /// token would add nothing the canonical model does not already carry — and it would double
    /// the number of places one label writes.</para>
    /// </summary>
    private static IReadOnlyList<ParsedParty>? BuildParties(IReadOnlyList<string> textLines)
    {
        // Specific labels first: "Ship To Address:" is a street, not a name, and must not be
        // consumed by the name pattern. (It cannot be — the name pattern demands a separator
        // directly after "to" — but the ordering keeps that intent readable.)
        var street     = FindFirstValue(textLines, ShipToStreetRegex);
        var city       = FindFirstValue(textLines, ShipToCityRegex);
        var postalCode = FindFirstValue(textLines, ShipToPostalCodeRegex);
        var country    = FindFirstValue(textLines, ShipToCountryRegex);
        var name       = FindFirstValue(textLines, ShipToNameRegex);

        if (name is null && street is null && city is null && postalCode is null && country is null)
            return null;

        return new[]
        {
            new ParsedParty("shipTo",
                Name: name, Street: street, City: city, PostalCode: postalCode, Country: country),
        };
    }

    private static IReadOnlyList<ParsedOrderLine> ParseLines(IEnumerable<string> lines)
    {
        // Match every line FIRST, then read the numbers. A printed PDF declares no locale
        // anywhere, so the only evidence available is the document's own numbers — and that
        // evidence only exists once every line has been matched.
        var matches = new List<Match>();

        foreach (var line in lines)
        {
            if (IsLikelyLineHeader(line))
                continue;

            var match = LineWithPriceRegex.Match(line);
            if (!match.Success)
                match = LineWithoutPriceRegex.Match(line);

            if (match.Success)
                matches.Add(match);
        }

        var qtyTokens   = matches.Select(m => m.Groups["qty"].Value).ToList();
        var priceTokens = matches.Where(m => m.Groups["price"].Success)
                                 .Select(m => m.Groups["price"].Value).ToList();
        var document    = NumberParsing.InferDecimalConvention(qtyTokens.Concat(priceTokens));
        var qtyConvention = NumberParsing.FirstKnown(
            NumberParsing.InferDecimalConvention(qtyTokens), document);
        var priceConvention = NumberParsing.FirstKnown(
            NumberParsing.InferDecimalConvention(priceTokens), document);

        var parsedLines = new List<ParsedOrderLine>(matches.Count);

        foreach (var match in matches)
        {
            var lineNumber = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
            var (quantityVal, qtyAmbiguous) = ParseDecimal(match.Groups["qty"].Value, qtyConvention);
            var quantity = quantityVal ?? 0m;
            var (unitPrice, priceAmbiguous) = match.Groups["price"].Success
                ? ParseDecimal(match.Groups["price"].Value, priceConvention)
                : (null, false);

            parsedLines.Add(new ParsedOrderLine(
                LineNumber:    lineNumber,
                BuyerItemCode: match.Groups["code"].Value.Trim(),
                Description:   NullIfEmpty(match.Groups["desc"].Value),
                Quantity:      quantity,
                Unit:          NullIfEmpty(match.Groups["unit"].Value),
                UnitPrice:     unitPrice,
                // Refuse to deliver a silently-wrong number: a quantity or unit price the parser
                // could not read unambiguously flags the line for human review. Mirrors
                // CsvOrderParser/EdifactOrderParser/X12OrderParser's NeedsReview/ReviewReason contract.
                NeedsReview:   qtyAmbiguous || priceAmbiguous,
                ReviewReason:  NumberParsing.BuildAmbiguityReason(qtyAmbiguous, priceAmbiguous)));
        }

        return parsedLines;
    }

    private static bool IsLikelyLineHeader(string line) =>
        line.Contains("BuyerItemCode", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Item Code", StringComparison.OrdinalIgnoreCase)
        || line.Contains("UnitPrice", StringComparison.OrdinalIgnoreCase);

    private static string? FindFirstValue(IEnumerable<string> lines, Regex regex)
    {
        foreach (var line in lines)
        {
            var match = regex.Match(line);
            if (!match.Success)
                continue;

            return NullIfEmpty(match.Groups["value"].Value);
        }

        return null;
    }

    /// <summary>
    /// Parse a numeric token from the deterministic PDF text fallback via the shared
    /// locale-aware reader, under the <paramref name="convention"/> inferred from the whole
    /// document. Returns <c>(value, ambiguous)</c>; the caller flags the line for review when
    /// the token could not be read unambiguously rather than emitting a silently-wrong number.
    ///
    /// <para>This used to pass a hard-coded <c>european: false</c>. A PDF is free text printed
    /// by a human and carries no locale signal whatsoever, so asserting one was a guess: a
    /// German PO printing "1.000" (one thousand) resolved to 1.0 — a thousandfold under-read —
    /// and reported itself as unambiguous, so nothing was flagged.</para>
    /// </summary>
    private static (decimal? Value, bool Ambiguous) ParseDecimal(string? value, DecimalConvention convention) =>
        NumberParsing.TryParseFlexibleDecimal(value, convention);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
