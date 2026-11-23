using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class FieldValueParserNormalTests
{
    [Theory]
    [InlineData("18.11.2026", "2026-11-18")]
    [InlineData("14 Nov 2026", "2026-11-14")]
    [InlineData("2026-11-05", "2026-11-05")]
    [InlineData("21/11/2026", "2026-11-21")]   // day > 12: unambiguous
    [InlineData("11/21/2026", "2026-11-21")]   // US, unambiguous
    [InlineData("05/05/2026", "2026-05-05")]   // same either way
    [InlineData("03/04/2026", null)]           // US or UK: never guessed
    [InlineData("18.11.2026 14:23", null)]     // the time would be lost
    [InlineData("21.11.26", "2026-11-21")]     // dotted short year: day first
    [InlineData("26-11-05", null)]             // year first, or day first?
    [InlineData("soon", null)]
    public void IsoDate_writes_only_what_is_certain(string value, string? expected) =>
        FieldValueParser.IsoDate(value).Should().Be(expected);

    [Theory]
    [InlineData("171,00", ',', "171,00")]
    [InlineData("171,00", '.', "171.00")]
    [InlineData("1 234,56", '.', "1234.56")]
    [InlineData("1,234.50", ',', "1234,50")]
    [InlineData("-12,5", '.', "-12.5")]
    [InlineData("285", ',', "285")]
    [InlineData("1.500", '.', null)]       // one and a half, or fifteen hundred
    [InlineData("EUR 171,00", '.', null)]  // the currency would be lost
    [InlineData("10 pcs", '.', null)]
    [InlineData("1.234.567", ',', "1234567")]
    [InlineData("18.11.2026", '.', null)]  // a date, not eighteen million
    [InlineData("1,5.3", '.', null)]
    [InlineData("0012", '.', null)]        // a code: its zeros matter
    [InlineData("0,50", '.', "0.50")]
    public void PlainNumber_writes_only_what_is_certain(string value, char mark, string? expected) =>
        FieldValueParser.PlainNumber(value, mark).Should().Be(expected);
}
