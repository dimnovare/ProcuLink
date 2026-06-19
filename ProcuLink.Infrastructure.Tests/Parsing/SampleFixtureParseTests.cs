using System.Text;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace ProcuLink.Infrastructure.Tests.FixtureParse;

/// <summary>
/// Characterization: the embedded onboarding fixture must parse into a POPULATED order.
/// Live prod showed it parsing to empty lines (qty/price 0, no buyer/currency, 4 lines from
/// 3 rows) — this pins the actual embedded bytes + the CsvOrderParser output so the bug is
/// reproducible (or proven Windows-clean → Linux-only embed issue).
/// </summary>
public class SampleFixtureParseTests
{
    private readonly ITestOutputHelper _out;
    public SampleFixtureParseTests(ITestOutputHelper o) => _out = o;

    private const string ResourceName = "ProcuLink.Infrastructure.Fixtures.sample-order.csv";

    [Fact]
    public void EmbeddedFixture_IsPresent_AndHasExpectedHeader()
    {
        using var s = typeof(SampleOrderService).Assembly.GetManifestResourceStream(ResourceName);
        Assert.NotNull(s);
        using var ms = new MemoryStream();
        s!.CopyTo(ms);
        var bytes = ms.ToArray();
        var text = Encoding.UTF8.GetString(bytes);
        _out.WriteLine($"byte length = {bytes.Length}");
        _out.WriteLine($"first 3 bytes = {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2}");
        _out.WriteLine("CONTENT >>>");
        _out.WriteLine(text.Replace("\r", "\\r").Replace("\n", "\\n\n"));
        _out.WriteLine("<<<");
        Assert.Contains("po_number", text);
        Assert.Contains("DEMO-2026-001", text);
    }

    [Fact]
    public async Task EmbeddedFixture_ParsesToPopulatedOrder()
    {
        using var s = typeof(SampleOrderService).Assembly.GetManifestResourceStream(ResourceName);
        Assert.NotNull(s);
        var parsed = await new CsvOrderParser().ParseAsync(s!, CancellationToken.None);

        _out.WriteLine($"PoNumber={parsed.PoNumber} BuyerName={parsed.BuyerName} Currency={parsed.Currency} Lines={parsed.Lines.Count}");
        foreach (var l in parsed.Lines)
            _out.WriteLine($"  line {l.LineNumber}: code={l.BuyerItemCode} desc={l.Description} qty={l.Quantity} price={l.UnitPrice}");

        Assert.Equal("DEMO-2026-001", parsed.PoNumber);
        Assert.Equal("EUR", parsed.Currency);
        Assert.Equal(3, parsed.Lines.Count);
        Assert.Equal(12m, parsed.Lines[0].Quantity);
        Assert.Equal(4.50m, parsed.Lines[0].UnitPrice);
        Assert.Equal("ACME-WIDGET-A", parsed.Lines[0].BuyerItemCode);
    }
}
