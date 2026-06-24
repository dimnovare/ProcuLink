using System.Text;
using FluentAssertions;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// B2 regression: the mapping-template CSV parse path
/// (<see cref="OrderIngestionService.ParseWithMappingTemplateAsync"/>) used to read
/// quantity/unit price with <c>NumberStyles.Any</c> + InvariantCulture, which read EU
/// "73,22" as 7322 (a 100× over-charge) and "1.234,56" as null/0 — both SILENTLY. It now
/// routes both numbers through the shared <see cref="NumberParsing.TryParseFlexibleDecimal"/>
/// reader (the same logic the direct CSV parser uses) and flags the line
/// <c>NeedsReview</c> on genuine ambiguity, so a coordinator checks instead of a wrong
/// value shipping. Invariant-formatted numbers stay byte-identical.
/// </summary>
public class MappingTemplateDecimalParsingTests
{
    private static PoMappingConfig Config(string separator = ",") => new()
    {
        HasHeaderRecord = true,
        Separator = separator,
        Lines = new Dictionary<string, FieldMappingEntry>
        {
            ["BuyerItemCode"] = new() { ExternalField = "ITEM" },
            ["Quantity"]      = new() { ExternalField = "QTY" },
            ["UnitPrice"]     = new() { ExternalField = "PRICE" },
        }
    };

    private static async Task<ParsedOrderLine> ParseSingleLineAsync(
        string csv, string separator = ",")
    {
        var buffer = Encoding.UTF8.GetBytes(csv);
        var parsed = await OrderIngestionService.ParseWithMappingTemplateAsync(
            buffer, Config(separator), CancellationToken.None);
        parsed.Lines.Should().HaveCount(1);
        return parsed.Lines[0];
    }

    [Fact]
    public async Task EuComma_decimal_unit_price_is_never_read_as_100x()
    {
        // "73,22" must NEVER become 7322. It is either read as 73.22 or flagged for review —
        // either way the 100× over-charge is impossible. The value is quoted so the ','
        // delimiter does not split the cell — we are testing the NUMBER reader, not CSV
        // field-splitting.
        var line = await ParseSingleLineAsync("ITEM,QTY,PRICE\nACME-1,2,\"73,22\"");

        line.UnitPrice.Should().NotBe(7322m, "a comma must never be treated as a thousands group here");
        (line.UnitPrice == 73.22m || line.NeedsReview)
            .Should().BeTrue("EU '73,22' must read as 73.22 or be flagged for review");
    }

    [Fact]
    public async Task EuComma_decimal_unit_price_reads_as_correct_value()
    {
        // With the ';' delimiter the file is unambiguously European, so the comma is the
        // decimal separator and the value resolves exactly.
        var line = await ParseSingleLineAsync("ITEM;QTY;PRICE\nACME-1;2;73,22", separator: ";");

        line.UnitPrice.Should().Be(73.22m);
        line.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task EuThousandsGrouped_price_is_never_null_or_truncated()
    {
        // "1.234,56" must NEVER become null/0 (old failure) nor 1.23456 (group dropped).
        var line = await ParseSingleLineAsync("ITEM;QTY;PRICE\nACME-1;1;1.234,56", separator: ";");

        line.UnitPrice.Should().Be(1234.56m);
        line.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task EuThousandsGrouped_price_under_comma_delimiter_never_corrupts()
    {
        // Same value under a ',' template separator: regardless of locale inference the
        // result must be the true value or flagged — never null/0 and never 1.23456.
        var line = await ParseSingleLineAsync("ITEM,QTY,PRICE\nACME-1,1,\"1.234,56\"");

        line.UnitPrice.Should().NotBe(1.23456m, "the thousands group must not be silently dropped");
        (line.UnitPrice == 1234.56m || line.NeedsReview)
            .Should().BeTrue("EU '1.234,56' must read as 1234.56 or be flagged for review");
    }

    [Fact]
    public async Task Invariant_decimal_price_is_byte_unchanged()
    {
        // The fix MUST NOT change correctly-formatted invariant numbers.
        var line = await ParseSingleLineAsync("ITEM,QTY,PRICE\nACME-1,2,73.22");

        line.UnitPrice.Should().Be(73.22m);
        line.Quantity.Should().Be(2m);
        line.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Invariant_thousands_and_decimal_price_is_byte_unchanged()
    {
        var line = await ParseSingleLineAsync("ITEM,QTY,PRICE\nACME-1,3,\"1,234.56\"");

        line.UnitPrice.Should().Be(1234.56m);
        line.Quantity.Should().Be(3m);
        line.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Garbage_numeric_token_is_flagged_for_review_not_silently_dropped()
    {
        // Scientific notation is the canonical silent-corruption trap ("1.5e2" → "1.52").
        var line = await ParseSingleLineAsync("ITEM,QTY,PRICE\nACME-1,2,1.5e2");

        line.NeedsReview.Should().BeTrue();
        line.ReviewReason.Should().NotBeNullOrEmpty();
    }
}
