using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

/// <summary>
/// Manufacturer-part extraction from real customer purchase orders.
///
/// The two fixtures are a deliberate pair (see the comment header inside each file):
///  • <c>real-cxml-1.2-ariba-punchout-mpn-differs.xml</c> — a PUNCHOUT order where <c>SupplierPartID</c> is the
///    buying network's internal id and resolves against nothing in the supplier's catalog. Here
///    the manufacturer part is the ONLY usable key, and it is a completely different string from
///    the buyer item code.
///  • <c>real-cxml-1.1-mpn-equals-supplier-part.xml</c> — the easy case, where the two are the SAME string.
///    On its own it makes the old blind "echo the MPN back as the supplier code" shortcut look
///    correct, which is exactly why it must never be tested alone.
/// </summary>
public class CxmlOrderParserManufacturerTests
{
    private static async Task<ParsedOrder> ParseFixtureAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        File.Exists(path).Should().BeTrue($"fixture '{fileName}' must be copied to the output directory");

        await using var stream = File.OpenRead(path);
        return await new CxmlOrderParser().ParseAsync(stream, CancellationToken.None);
    }

    [Fact]
    public async Task ParseAsync_KsbAribaPunchout_KeepsBuyerCodeAndManufacturerPartSeparate()
    {
        var result = await ParseFixtureAsync("real-cxml-1.2-ariba-punchout-mpn-differs.xml");

        var line = result.Lines.Should().ContainSingle().Subject;
        line.BuyerItemCode.Should().Be("29954596");                      // SupplierPartID
        line.ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");      // ManufacturerPartID
        line.ManufacturerName.Should().Be("REDACTED-PARTY");                  // ManufacturerName

        line.ManufacturerPartNumber.Should().NotBe(line.BuyerItemCode,
            "this is the punchout case — echoing the MPN back as the supplier code is wrong here");
    }

    [Fact]
    public async Task ParseAsync_MaerskOrder_ManufacturerPartEqualsBuyerCode_AndBrandIsAbsent()
    {
        var result = await ParseFixtureAsync("real-cxml-1.1-mpn-equals-supplier-part.xml");

        var line = result.Lines.Should().ContainSingle().Subject;
        line.BuyerItemCode.Should().Be("REDACTED-ORDER-DATA");
        line.ManufacturerPartNumber.Should().Be("REDACTED-ORDER-DATA");
        line.ManufacturerName.Should().BeNull(
            "this order carries no <ManufacturerName>; an absent brand must stay null, not \"\"");
    }
}
