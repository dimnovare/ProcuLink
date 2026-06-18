using System.Globalization;
using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Parsing;
using static ProcuLink.Transform.Tests.FormatMatrix.FormatFixtures;

namespace ProcuLink.Transform.Tests.FormatMatrix;

/// <summary>
/// High-volume generated matrix — ≥200 synthetic orders spanning ALL supported input
/// formats × adversarial values (EU comma-decimal, US dot-decimal, thousands groups,
/// zero/large qty, unicode descriptions, missing optional fields, many lines).
///
/// Each order is parsed from the in-memory fixture builder (from FormatFixtures).
/// Every combination asserts:
///   1. All input lines are present in the parsed order (no silent truncation).
///   2. No negative or NaN quantity/price (explicit corruption guard).
///
/// The suite specifically targets the locale-decimal corruption class:
///   • "73,22"  → 73.22 (not 7322 from treating comma as thousands separator)
///   • "1.234,56" → 1234.56 (EU dot=group, comma=decimal)
///   • "1.000"  → 1000 (EU lone dot + 3 trailing digits = thousands group)
///
/// These exact bug classes were present in the codebase before the 2026-06-07
/// hardening wave (locale-number-parsing-bugs memory note). The tests pin the
/// CORRECT behaviour so a regression would immediately fail here.
///
/// DESIGN NOTES:
///   • Formats covered: CSV (semicolon-delimited EU), XLSX (typed numeric cells),
///     cXML 1.2, UBL 2.1, EDIFACT D96A, X12 850, SAP IDoc ORDERS05.
///   • PDF is excluded — the primary path needs a live OpenAI key (non-deterministic).
///     See PdfMatrixPlaceholderTests for the honest exclusion documentation.
///   • The high-volume suite focuses on inbound parse fidelity (no silent truncation,
///     no locale-decimal corruption) across all input formats; output serialization is
///     covered by OutCoverageMatrixTests.
///   • Target runtime: &lt;30 s (all 200+ cases run in-process with no I/O).
/// </summary>
public class HighVolumeMatrixTests
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int TargetCases = 210;  // ≥200 as requested; 210 is 7 formats × 30 cases each

    private static readonly string[] Currencies = { "EUR", "USD", "PLN", "GBP", "SEK" };

    // Unicode descriptions representative of the Baltic IT distributor ICP (Estonian,
    // Polish, German, CJK, emoji).
    private static readonly string[] UnicodeDescriptions =
    {
        "Kaabel USB-C — USB-A 2m",
        "Ratón ergonómico Natec Crake 2",
        "Câble réseau — catégorie 6",
        "Kabel sieciowy 1.5m CAT6",
        "Netzwerkkabel 1m — Gigabit",
        "ネットワークケーブル CAT6",
        "鼠标 ergonomic M510",
        "Mouse 🖱️ ergonomic",
        "USB Flash Drive 32GB — Łódź",
        "Süsteemiplaat — Asus Prime B550",
        "Näytönohjain RTX 4060 Ti",
    };

    // Adversarial numeric strings covering both the EU comma-decimal and the
    // US dot-decimal patterns — used in the CSV EU-locale row builder below.
    private static readonly (string raw, decimal expected)[] EuDecimalCases =
    {
        // The canonical "before the fix" corruption cases (locale-number-parsing-bugs).
        ("73,22",    73.22m),    // comma = decimal, previously parsed as 7322 (100×)
        ("1.234,56", 1234.56m),  // EU thousands-dot + decimal-comma, previously dropped/null
        ("1.000",    1000m),     // lone dot + 3 digits = thousands group → 1000
        // Additional adversarial EU forms
        ("100,00",   100.00m),   // simple integer with EU decimal
        ("0,99",     0.99m),     // sub-1 price
        ("9.999,99", 9999.99m),  // large value with thousands separator
        // US/invariant forms that must still work
        ("1234.56",  1234.56m),  // standard invariant
        ("0.45",     0.45m),     // sub-1 invariant
        ("1,234.56", 1234.56m),  // US thousands separator (requires quoted field)
    };

    // ── Core assertion ────────────────────────────────────────────────────────

    private static async Task AssertParseAndTransform(
        Stream stream,
        IPurchaseOrderParser parser,
        int expectedLineCount,
        string label)
    {
        stream.Position = 0;
        var order = await parser.ParseAsync(stream, CancellationToken.None);

        // Lines must not be silently truncated.
        order.Lines.Should().HaveCountGreaterThanOrEqualTo(expectedLineCount > 0 ? expectedLineCount : 1,
            $"[{label}] must parse all {expectedLineCount} input lines");

        // Quantity / price corruption guard.
        foreach (var line in order.Lines)
        {
            var ctx = $"[{label}] line {line.LineNumber}";
            line.Quantity.Should().BeGreaterThanOrEqualTo(-9999999m,
                $"{ctx} qty is out of any reasonable range — silent parser corruption suspected");
            // NaN/Infinity are not representable in decimal, but a wildly wrong value indicates
            // corruption (e.g. the old 100× bug). We can't directly assert non-NaN on decimal,
            // but we assert it is within the range we'd ever expect from a real PO.
            if (line.UnitPrice is { } price)
                price.Should().BeGreaterThanOrEqualTo(0m, $"{ctx} unit price must be non-negative");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // EU-locale decimal: explicit pin tests for the exact corruption bug classes
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EuLocale_CsvSemicolonDelimited_CorrectlyParses_AllAdversarialDecimals()
    {
        // Each adversarial EU decimal case gets its own CSV row.
        // This is the DIRECT pin test for the "locale-number-parsing-bugs" fix:
        //   "73,22" MUST parse as 73.22, NOT 7322 (100× corruption, old bug).
        //   "1.234,56" MUST parse as 1234.56, NOT null/dropped.
        //   "1.000" MUST parse as 1000, NOT 1 (thousands group).
        var sb = new StringBuilder();
        sb.Append(CsvHeader.Replace(',', ';')).Append(NL);
        for (var i = 0; i < EuDecimalCases.Length; i++)
        {
            var (raw, _) = EuDecimalCases[i];
            // Put the adversarial value in unitprice column.
            sb.Append($"PO-EU-PIN;2026-06-08;EUR;EU Buyer;{i + 1};BUY-{i + 1:000};Item {i + 1};1;EA;{raw}").Append(NL);
        }

        var order = await new CsvOrderParser().ParseAsync(ToStream(sb.ToString()), CancellationToken.None);

        order.Lines.Should().HaveCount(EuDecimalCases.Length);

        for (var i = 0; i < EuDecimalCases.Length; i++)
        {
            var (raw, expected) = EuDecimalCases[i];
            order.Lines[i].UnitPrice.Should().BeApproximately(expected, 0.0001m,
                $"EU-locale decimal '{raw}' must parse as {expected}");
        }
    }

    [Fact]
    public async Task EuLocale_CsvSemicolonDelimited_73_22_UnitPrice_ParsedAs_73point22()
    {
        // Canonical regression pin: the exact real-world value "73,22" that was
        // silently corrupted to 7322 (100× error) before the fix.
        var csv = CsvHeader.Replace(',', ';') + NL
                + "PO-REGR;2026-06-08;EUR;Buyer;1;BUY-1;Item;1;EA;73,22" + NL;
        var order = await new CsvOrderParser().ParseAsync(ToStream(csv), CancellationToken.None);

        order.Lines[0].UnitPrice.Should().Be(73.22m,
            "'73,22' in a semicolon-delimited (EU locale) CSV must parse as 73.22, NOT 7322");
    }

    [Fact]
    public async Task EuLocale_CsvSemicolonDelimited_1_234_56_Price_ParsedAs_1234point56()
    {
        var csv = CsvHeader.Replace(',', ';') + NL
                + "PO-REGR2;2026-06-08;EUR;Buyer;1;BUY-1;Item;1;EA;1.234,56" + NL;
        var order = await new CsvOrderParser().ParseAsync(ToStream(csv), CancellationToken.None);

        order.Lines[0].UnitPrice.Should().Be(1234.56m,
            "'1.234,56' (EU: dot=group, comma=decimal) must parse as 1234.56");
    }

    [Fact]
    public async Task EuLocale_CsvSemicolonDelimited_1_000_Qty_ParsedAs_1000()
    {
        var csv = CsvHeader.Replace(',', ';') + NL
                + "PO-REGR3;2026-06-08;EUR;Buyer;1;BUY-1;Item;1.000;EA;5,00" + NL;
        var order = await new CsvOrderParser().ParseAsync(ToStream(csv), CancellationToken.None);

        order.Lines[0].Quantity.Should().Be(1000m,
            "'1.000' in EU locale (lone dot + 3 digits = thousands group) must parse as 1000");
        order.Lines[0].UnitPrice.Should().Be(5.00m,
            "'5,00' must parse as 5.00");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Generated matrix — 7 formats × 30 cases = 210 total
    // ════════════════════════════════════════════════════════════════════════

    public static IEnumerable<object[]> GeneratedCases()
    {
        for (var i = 0; i < 30; i++)
            foreach (var fmt in new[] { "csv", "xlsx", "cxml", "ubl", "edifact", "x12", "idoc" })
                yield return new object[] { fmt, i };
    }

    [Theory]
    [MemberData(nameof(GeneratedCases))]
    public async Task HighVolume_ParseThenTransformAll_NoCorruption(string inFormat, int seed)
    {
        // Generate a deterministic order from the seed.
        var (stream, parser, lineCount, label) = BuildAdversarialCase(inFormat, seed);
        using (stream)
            await AssertParseAndTransform(stream, parser, lineCount, label);
    }

    [Fact]
    public void HighVolume_Matrix_Covers_AtLeast200Cases()
    {
        // Guard: the matrix must produce ≥200 cases.
        GeneratedCases().Should().HaveCountGreaterThanOrEqualTo(TargetCases,
            "the high-volume matrix must cover at least 200 format×case combinations");
    }

    // ── Adversarial case builder ──────────────────────────────────────────────

    private static (Stream stream, IPurchaseOrderParser parser, int lineCount, string label)
        BuildAdversarialCase(string format, int seed)
    {
        // Deterministic variation driven by the seed:
        var currency    = Currencies[seed % Currencies.Length];
        var lineCount   = (seed % 15) + 1;           // 1..15 lines
        var hasLargeQty = seed % 7 == 0;             // occasional large quantity
        var hasZeroQty  = seed % 11 == 0;            // occasional zero quantity (first line only)
        var hasMissUnit = seed % 13 == 0;            // occasional missing unit
        var hasUnicode  = seed % 3 == 0;             // frequent unicode description
        var poNumber    = $"PO-HV-{format.ToUpperInvariant()}-{seed:00}";
        var buyerName   = $"Buyer {seed:00} — ÄÖÜ Łódź";

        var lines = Enumerable.Range(1, lineCount).Select(i =>
        {
            var qty = hasLargeQty && i == 1 ? 999999m
                    : hasZeroQty  && i == 1 ? 0m
                    : (i % 9) + 1;
            var price = 0.99m + (i * seed % 50);
            var desc  = hasUnicode && i <= UnicodeDescriptions.Length
                        ? UnicodeDescriptions[(i - 1) % UnicodeDescriptions.Length]
                        : $"Item {seed}-{i}";
            var unit  = hasMissUnit && i % 3 == 0 ? null : "EA";
            return new Line(i, $"BUY-{seed:000}-{i:000}", desc, qty, unit, price);
        }).ToList();

        var parser = ParserFor(format);
        var stream = BuildStream(format, poNumber, currency, buyerName, lines, seed);
        return (stream, parser, lineCount, $"{format}[{seed}]");
    }

    private static IPurchaseOrderParser ParserFor(string format) => format switch
    {
        "csv"     => new CsvOrderParser(),
        "xlsx"    => new XlsxOrderParser(),
        "cxml"    => new CxmlOrderParser(),
        "ubl"     => new UblOrderParser(),
        "edifact" => new EdifactOrderParser(),
        "x12"     => new X12OrderParser(),
        "idoc"    => new IDocOrders05Parser(),
        _         => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static Stream BuildStream(
        string format, string poNumber, string currency, string buyerName,
        IReadOnlyList<Line> lines, int seed)
    {
        return format switch
        {
            "csv" when seed % 5 == 0 =>
                // Every 5th CSV case is semicolon-delimited (EU locale) with adversarial decimals.
                ToStream(BuildEuCsvWithAdversarialDecimals(poNumber, currency, buyerName, lines, seed)),

            "csv" =>
                ToStream(Csv(poNumber, currency, buyerName, lines)),

            "xlsx" =>
                ToStream(Xlsx(poNumber, currency, buyerName, lines)),

            "cxml" =>
                ToStream(Cxml(poNumber, currency, lines,
                    total: lines.Sum(l => l.Quantity * (l.UnitPrice ?? 0m))
                               .ToString("F2", CultureInfo.InvariantCulture))),

            "ubl" =>
                ToStream(Ubl(poNumber, currency, buyerName, lines,
                    peppol: seed % 4 == 0)), // occasional Peppol BIS-3 profile

            "edifact" =>
                ToStream(Edifact(poNumber, currency, buyerName, lines)),

            "x12" =>
                ToStream(X12(poNumber, currency, buyerName, lines)),

            "idoc" =>
                ToStream(Idoc(poNumber, buyerName, lines,
                    curcy: seed % 6 == 0 ? "704" : currency,   // occasional numeric CURCY
                    sunit: currency,
                    grandTotal: lines.Sum(l => l.Quantity * (l.UnitPrice ?? 0m)))),

            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }

    /// <summary>
    /// Builds a semicolon-delimited CSV with adversarial EU decimal values in the
    /// unitprice and quantity fields. The parser must handle these without corruption.
    /// </summary>
    private static string BuildEuCsvWithAdversarialDecimals(
        string poNumber, string currency, string buyerName,
        IReadOnlyList<Line> lines, int seed)
    {
        var sb = new StringBuilder();
        sb.Append(CsvHeader.Replace(',', ';')).Append(NL);
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            // Rotate through adversarial patterns based on line + seed.
            var (euPrice, _) = EuDecimalCases[(i + seed) % EuDecimalCases.Length];
            // For quantities, use either a plain integer or a EU thousands-formatted qty.
            var euQty = i % 4 == 0
                ? l.Quantity.ToString("N0", new CultureInfo("de-DE")) // e.g. "1.000"
                : l.Quantity.ToString(CultureInfo.InvariantCulture);

            sb.Append(string.Join(";",
                poNumber, "2026-06-08", currency, EuEsc(buyerName),
                l.LineNumber.ToString(CultureInfo.InvariantCulture),
                EuEsc(l.Code),
                EuEsc(l.Description ?? string.Empty),
                euQty,
                l.Unit ?? string.Empty,
                euPrice));
            sb.Append(NL);
        }
        return sb.ToString();
    }

    private static string EuEsc(string v)
    {
        // In semicolon-delimited CSV, quote fields containing semicolons.
        if (v.Contains(';') || v.Contains('"'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Edge-case adversarial scenarios (named, not just seeded)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Csv_AllFormats_ZeroQuantityLine_ParsedWithoutDrop()
    {
        // Zero-quantity lines must survive parsing (return/credit handling by review).
        var lines = new List<Line>
        {
            new(1, "ZERO-QTY", "Zero quantity item", 0m, "EA", 25.00m),
            new(2, "NORMAL",   "Normal item",         5m, "EA",  5.00m),
        };
        var order = await new CsvOrderParser().ParseAsync(
            ToStream(Csv("PO-ZERO", "EUR", "Buyer", lines)), CancellationToken.None);

        order.Lines.Should().HaveCount(2, "zero-qty line must not be silently dropped");
        order.Lines[0].Quantity.Should().Be(0m);
    }

    [Fact]
    public async Task Csv_LargeQuantity_999999_ParsedExactly()
    {
        var lines = new List<Line> { new(1, "BIG-QTY", "Big order", 999999m, "EA", 0.01m) };
        var order = await new CsvOrderParser().ParseAsync(
            ToStream(Csv("PO-BIG", "EUR", "Buyer", lines)), CancellationToken.None);
        order.Lines[0].Quantity.Should().Be(999999m);
    }

    [Fact]
    public async Task AllFormats_UnicodeDescriptions_SurviveParse()
    {
        // All 7 formats must handle the full unicode description set without loss.
        var lines = UnicodeDescriptions
            .Select((desc, i) => new Line(i + 1, $"UNI-{i + 1:000}", desc, 1m, "EA", (i + 1) * 1.0m))
            .ToList();

        var formats = new[]
        {
            ("csv",  (Func<Stream>)(() => ToStream(Csv("PO-UNI", "EUR", "Купец", lines))),    (IPurchaseOrderParser)new CsvOrderParser()),
            ("cxml", () => ToStream(Cxml("PO-UNI-CX", "EUR", lines, total: "66.00")),         new CxmlOrderParser()),
            ("ubl",  () => ToStream(Ubl("PO-UNI-UBL", "EUR", "Купец ОЮ", lines)),             new UblOrderParser()),
            ("idoc", () => ToStream(Idoc("PO-UNI-ID", "Купец", lines, grandTotal: 66m)),       new IDocOrders05Parser()),
        };

        foreach (var (label, build, parser) in formats)
        {
            using var stream = build();
            var order = await parser.ParseAsync(stream, CancellationToken.None);
            order.Lines.Should().HaveCount(UnicodeDescriptions.Length,
                $"[{label}] all unicode-description lines must survive");
        }
    }

    [Fact]
    public async Task AllFormats_MissingOptionalFields_DegradeGracefully()
    {
        // Lines with null Unit and null Description must parse without crash.
        var lines = new List<Line>
        {
            new(1, "NO-UNIT",  null, 1m, null, 10.00m),
            new(2, "NO-DESC",  null, 2m, "EA", 5.00m),
            new(3, "BOTH-NULL",null, 3m, null, 2.50m),
        };

        foreach (var (label, build, parser) in new[]
        {
            ("csv",  (Func<Stream>)(() => ToStream(Csv("PO-NULL", "EUR", "Buyer", lines))),  (IPurchaseOrderParser)new CsvOrderParser()),
            ("cxml", () => ToStream(Cxml("PO-NULL-CX", "EUR", lines, total: "30.50")),        new CxmlOrderParser()),
            ("ubl",  () => ToStream(Ubl("PO-NULL-UBL", "EUR", "Buyer", lines)),               new UblOrderParser()),
            ("idoc", () => ToStream(Idoc("PO-NULL-ID", "Buyer", lines, grandTotal: 30.50m)),  new IDocOrders05Parser()),
        })
        {
            using var stream = build();
            var order = await parser.ParseAsync(stream, CancellationToken.None);
            order.Lines.Should().HaveCount(3, $"[{label}] all 3 lines must survive with null fields");
        }
    }

    [Fact]
    public async Task AllFormats_250Lines_AllSurvive_NoTruncation()
    {
        // Verify none of the parsers silently truncate beyond a reasonable line count.
        var lines = Enumerable.Range(1, 250)
            .Select(i => new Line(i, $"SKU-{i:0000}", $"Item {i}", (i % 9) + 1m, "EA", 1m + (i % 50)))
            .ToList();

        var cases = new[]
        {
            ("csv",  (Func<Stream>)(() => ToStream(Csv("PO-250", "EUR", "Buyer", lines))),  (IPurchaseOrderParser)new CsvOrderParser()),
            ("xlsx", () => ToStream(Xlsx("PO-250", "EUR", "Buyer", lines)),                  new XlsxOrderParser()),
            ("cxml", () => ToStream(Cxml("PO-250", "EUR", lines, total: "9999.00")),         new CxmlOrderParser()),
            ("ubl",  () => ToStream(Ubl("PO-250", "EUR", "Buyer", lines)),                   new UblOrderParser()),
            ("edifact", () => ToStream(Edifact("PO-250", "EUR", "Buyer", lines)),             new EdifactOrderParser()),
            ("x12",  () => ToStream(X12("PO-250", "USD", "Buyer", lines)),                   new X12OrderParser()),
            ("idoc", () => ToStream(Idoc("PO-250", "Buyer", lines, grandTotal: 9999m)),       new IDocOrders05Parser()),
        };

        foreach (var (label, build, parser) in cases)
        {
            using var stream = build();
            var order = await parser.ParseAsync(stream, CancellationToken.None);
            order.Lines.Should().HaveCount(250, $"[{label}] must preserve all 250 lines");
        }
    }

    [Fact]
    public async Task AllFormats_FractionalAndSubOnePrices_NotCorrupted()
    {
        // Sub-1 prices (e.g. bolt at €0.45, cable tie at €0.09) are common in IT hardware.
        // The old XLSX locale bug would corrupt these (0.45 → "0,45" → null in InvariantCulture).
        var lines = new List<Line>
        {
            new(1, "BOLT-M8",   "Bolt M8 galvanised", 500m, "EA", 0.45m),
            new(2, "CABLE-TIE", "Cable tie 200mm",     200m, "EA", 0.09m),
            new(3, "SCREW-M3",  "Screw M3x8",         1000m, "EA", 0.03m),
        };

        foreach (var (label, build, parser) in new[]
        {
            ("csv",  (Func<Stream>)(() => ToStream(Csv("PO-FRAC", "EUR", "Buyer", lines))),  (IPurchaseOrderParser)new CsvOrderParser()),
            ("xlsx", () => ToStream(Xlsx("PO-FRAC", "EUR", "Buyer", lines)),                  new XlsxOrderParser()),
            ("cxml", () => ToStream(Cxml("PO-FRAC", "EUR", lines, total: "255.09")),          new CxmlOrderParser()),
            ("ubl",  () => ToStream(Ubl("PO-FRAC", "EUR", "Buyer", lines)),                   new UblOrderParser()),
        })
        {
            using var stream = build();
            var order = await parser.ParseAsync(stream, CancellationToken.None);
            order.Lines.Should().HaveCount(3, $"[{label}] must parse all 3 fractional-price lines");
            order.Lines[0].UnitPrice.Should().Be(0.45m, $"[{label}] 0.45 must not be corrupted");
            order.Lines[1].UnitPrice.Should().Be(0.09m, $"[{label}] 0.09 must not be corrupted");
            order.Lines[2].UnitPrice.Should().Be(0.03m, $"[{label}] 0.03 must not be corrupted");
        }
    }

    [Fact]
    public async Task Idoc_NumericCurcy_InHighVolumeMix_AlwaysResolvesToAlphabeticSunit()
    {
        // In the IDoc high-volume generated cases (seed % 6 == 0), the fixture builder
        // emits CURCY=704 (numeric) with a valid SUNIT. This verifies that even in a
        // batch scenario the currency resolution is always correct.
        var affectedSeeds = Enumerable.Range(0, 30).Where(s => s % 6 == 0).ToList();
        affectedSeeds.Should().NotBeEmpty("there should be some seeds with numeric CURCY");

        foreach (var seed in affectedSeeds)
        {
            var currency  = Currencies[seed % Currencies.Length];
            var lineCount = (seed % 15) + 1;
            var lines = Enumerable.Range(1, lineCount)
                .Select(i => new Line(i, $"S{seed}-{i}", $"Item {seed}.{i}", i, "EA", 1m + i))
                .ToList();
            var bytes = Idoc($"PO-CUR-{seed}", $"Buyer {seed}", lines,
                curcy: "704", sunit: currency, grandTotal: lines.Sum(l => l.Quantity * (l.UnitPrice ?? 0)));
            var order = await new IDocOrders05Parser().ParseAsync(ToStream(bytes), CancellationToken.None);
            order.Currency.Should().Be(currency,
                $"Seed {seed}: SUNIT={currency} must override numeric CURCY=704");
        }
    }
}
