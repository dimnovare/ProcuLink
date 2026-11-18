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
    [InlineData("MTQ", "MTQ", "EA")]          // code-shaped, not in the table: Rec 20 passes it, X12 (2 chars) cannot
    [InlineData("CT", "CT", "CT")]            // code-shaped 2 chars: passes both
    [InlineData("bundle", "EA", "EA")] [InlineData("", "EA", "EA")] [InlineData(null, "EA", "EA")]
    public void Units_map_to_rec20_and_x12(string? written, string rec20, string x12) =>
        (StandardCodes.Rec20Unit(written), StandardCodes.X12Unit(written)).Should().Be((rec20, x12));

    [Theory]
    [InlineData("Germany", "DE")] [InlineData("germany", "DE")] [InlineData("Deutschland", "DE")] [InlineData("DEU", "DE")]
    [InlineData("de", "DE")] [InlineData("Estonia", "EE")] [InlineData("UK", "GB")] [InlineData("United Kingdom", "GB")]
    [InlineData("Narnia", null)] [InlineData("XX", null)] [InlineData(" ", null)] [InlineData(null, null)]
    public void Countries_become_iso_alpha2(string? written, string? iso) => StandardCodes.Country(written).Should().Be(iso);
}
