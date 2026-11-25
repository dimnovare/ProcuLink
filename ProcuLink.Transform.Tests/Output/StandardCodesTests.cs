using FluentAssertions;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

public class StandardCodesTests
{
    [Theory]
    [InlineData("pcs", "H87", "PC")] [InlineData("PCS.", "H87", "PC")] [InlineData("Pieces", "H87", "PC")] [InlineData("pc", "H87", "PC")]
    [InlineData("ea", "EA", "EA")] [InlineData("Each", "EA", "EA")] [InlineData("units", "C62", "UN")]
    [InlineData("kg", "KGM", "KG")] [InlineData("kgs", "KGM", "KG")] [InlineData("KGM", "KGM", "KG")] [InlineData("g", "GRM", "GR")]
    [InlineData("lb", "LBR", "LB")] [InlineData("l", "LTR", "LT")] [InlineData("ml", "MLT", "ML")] [InlineData("m", "MTR", "MR")]
    [InlineData("cm", "CMT", "CM")] [InlineData("mm", "MMT", "MM")] [InlineData("m2", "MTK", "SM")] [InlineData("box", "XBX", "BX")]
    [InlineData("pack", "XPK", "PK")] [InlineData("pair", "PR", "PR")] [InlineData("dozen", "DZN", "DZ")] [InlineData("hour", "HUR", "HR")]
    [InlineData("days", "DAY", "DA")]
    [InlineData("BX", "XBX", "BX")] [InlineData("PK", "XPK", "PK")] [InlineData("LT", "LTR", "LT")] [InlineData("UN", "C62", "UN")] // X12 codes as written
    [InlineData("tk", "H87", "PC")] [InlineData("Stk.", "H87", "PC")] [InlineData("St.", "H87", "PC")] [InlineData("kpl", "H87", "PC")]
    [InlineData("gab.", "H87", "PC")] [InlineData("vnt.", "H87", "PC")] [InlineData("шт.", "H87", "PC")]
    [InlineData("кг", "KGM", "KG")] [InlineData("л", "LTR", "LT")] [InlineData("jm", "MTR", "MR")] [InlineData("m3", "MTQ", "CR")]
    [InlineData("kmpl", "SET", "ST")] [InlineData("rull", "XRO", "RL")]
    [InlineData("karp", "XBX", "BX")] [InlineData("pakk", "XPK", "PK")] [InlineData("paar", "PR", "PR")] [InlineData("tund", "HUR", "HR")]
    [InlineData("päev", "DAY", "DA")]
    [InlineData("MTQ", "MTQ", "CR")]
    [InlineData("KWH", "KWH", "EA")]          // code-shaped, not in the table: Rec 20 passes it, X12 (2 chars) cannot
    [InlineData("CT", "CT", "CT")]            // code-shaped 2 chars: passes both
    [InlineData("bundle", "EA", "EA")] [InlineData("", "EA", "EA")] [InlineData(null, "EA", "EA")]
    public void Units_map_to_rec20_and_x12(string? written, string rec20, string x12) =>
        (StandardCodes.Rec20Unit(written), StandardCodes.X12Unit(written)).Should().Be((rec20, x12));

    [Theory]
    [InlineData("Germany", "DE")] [InlineData("germany", "DE")] [InlineData("Deutschland", "DE")] [InlineData("DEU", "DE")]
    [InlineData("de", "DE")] [InlineData("Estonia", "EE")] [InlineData("UK", "GB")] [InlineData("United Kingdom", "GB")]
    [InlineData("Turkey", "TR")] [InlineData("Czech Republic", "CZ")] [InlineData("The Netherlands", "NL")] [InlineData("Holland", "NL")]
    [InlineData("South Korea", "KR")] [InlineData("United States of America", "US")] [InlineData("Great Britain", "GB")]
    // Native names, without case or accents; Greece's VAT prefix EL is not ISO.
    [InlineData("Polska", "PL")] [InlineData("POLSKA", "PL")] [InlineData("Ελλάδα", "GR")] [InlineData("ΕΛΛΑΔΑ", "GR")] [InlineData("EL", "GR")]
    [InlineData("Osterreich", "AT")] [InlineData("België", "BE")] [InlineData("Éire", "IE")] [InlineData("Česko", "CZ")] [InlineData("Suisse", "CH")]
    [InlineData("Kosovo", null)] [InlineData("XK", null)]
    [InlineData("Narnia", null)] [InlineData("XX", null)] [InlineData(" ", null)] [InlineData(null, null)]
    public void Countries_become_iso_alpha2(string? written, string? iso) => StandardCodes.Country(written).Should().Be(iso);
}
