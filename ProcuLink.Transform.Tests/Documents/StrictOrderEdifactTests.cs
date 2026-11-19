using System.Text;
using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StrictOrderEdifactTests
{
    [Fact]
    public async Task The_orders_message_carries_every_read_value_and_parses_back()
    {
        var text = await StrictOrderFixture.Render("edifact-orders");
        new ConformanceService().Check(text, OutputFormat.EdifactOrders).ErrorCount.Should().Be(0);

        var segs = text.TrimEnd().Split('\'', StringSplitOptions.RemoveEmptyEntries);   // fixture has no release characters
        segs.SkipWhile(s => !s.StartsWith("UNH")).TakeWhile(s => !s.StartsWith("UNZ")).Should().Equal(
            "UNH+1+ORDERS:D:96A:UN", "BGM+220+PO-2026-0042+9", "DTM+137:20261118:102", "DTM+2:20261215:102",
            "NAD+BY+++Example Buyer Ltd+Example Street 1+Berlin++10115+DE", "RFF+VA:DE000000000", "CTA+OC", "COM+orders@example.com:EM",
            "NAD+SU+++Example Supplier OU+Example Road 2+Tallinn++10111+EE", "RFF+VA:EE000000000",
            "NAD+ST+++Example Buyer Ltd Warehouse+Warehouse Road 3+Hamburg++20095+DE",
            "CUX+2:EUR:9",
            "LIN+1++A-100:SA", "IMD+F++:::Hex bolt M8", "QTY+21:100:H87", "PRI+AAA:0.25",
            "LIN+2++B-200:SA", "IMD+F++:::Washer M8", "QTY+21:200:H87", "PRI+AAA:0.05",
            "LIN+3++C-300:SA", "IMD+F++:::Compression spring", "QTY+21:20:H87", "PRI+AAA:1.25",
            "UNS+S", "CNT+2:3", "UNT+27+1");

        var parsed = await new EdifactOrderParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);
        // The parser files any LIN item code under BuyerItemCode whatever its 7143 qualifier, so ":SA" reads back here.
        parsed.Lines.Select(l => (l.BuyerItemCode, l.Unit)).Should().Equal(("A-100", "H87"), ("B-200", "H87"), ("C-300", "H87"));
        parsed.RequestedDeliveryDate.Should().Be(new DateOnly(2026, 12, 15));
        parsed.Parties!.Single(p => p.Role == "shipTo").Should().Match<ParsedParty>(p =>
            p.Name == StrictOrderFixture.ShipToName && p.City == StrictOrderFixture.ShipToCity && p.Country == "DE");
    }

    [Fact]
    public async Task A_delivery_address_read_without_a_name_is_sent_under_the_buyers_name()
    {
        var text = await StrictOrderFixture.Render("edifact-orders", StrictOrderFixture.Read(without: [FieldMeanings.DeliveryName]));
        text.Should().Contain("'NAD+ST+++Example Buyer Ltd+Warehouse Road 3+Hamburg++20095+DE'");
    }

    [Fact]
    public async Task Over_long_text_is_split_over_its_repeats_then_cut()
    {
        var n = string.Concat(Enumerable.Repeat("Example Buyer ", 6)).Trim();
        var d = string.Concat(Enumerable.Repeat("Hex bolt ", 10)).Trim();
        var read = StrictOrderFixture.With(StrictOrderFixture.With(FieldMeanings.SenderName, n), FieldMeanings.LineDescription, d);
        var segs = (await StrictOrderFixture.Render("edifact-orders", read)).Split('\'');
        segs.Should().Contain($"NAD+BY+++{n[..35]}:{n[35..70]}:{n[70..]}+Example Street 1+Berlin++10115+DE")
            .And.Contain($"IMD+F++:::{d[..35]}:{d[35..70]}");
    }

    [Fact]
    public async Task Text_outside_latin_1_is_folded_and_written_as_latin_1()
    {
        var read = StrictOrderFixture.With(StrictOrderFixture.With(FieldMeanings.SenderName, "Example Šokolaad OÜ"), FieldMeanings.ReceiverName, "ООО Пример");
        var render = await StandardOutputs.RenderAsync("edifact-orders", read, FlowFieldList.Empty, "lines", Guid.Empty, CancellationToken.None);
        var bytes = new MemoryStream();
        await render.Result!.Content.CopyToAsync(bytes);
        bytes.ToArray().Should().Contain(0xDC).And.NotContain(0xC3);
        var text = Encoding.Latin1.GetString(bytes.ToArray());
        text.Should().StartWith("UNA:+.? 'UNB+UNOC:3+").And.Contain("'NAD+BY+++Example Sokolaad OÜ+").And.Contain("'NAD+SU+++OOO Primer+");
    }
}
