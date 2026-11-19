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
            "UNH+1+ORDERS:D:96A:UN", "BGM+220+PO-2026-0042+9", "DTM+137:20261118:102", "DTM+2:20261215:102", "FTX+AAB+++30 days net",
            "NAD+BY+++Example Buyer Ltd+Example Street 1+Berlin++10115+DE", "RFF+VA:DE000000000", "CTA+OC", "COM+orders@example.com:EM",
            "NAD+SU+++Example Supplier OU+Example Road 2+Tallinn++10111+EE", "RFF+VA:EE000000000",
            "NAD+ST+++Example Buyer Ltd Warehouse+Warehouse Road 3+Hamburg++20095+DE",
            "CUX+2:EUR:9",
            "LIN+1++A-100:SA", "IMD+F++:::Hex bolt M8", "QTY+21:100:H87", "MOA+203:25.00", "PRI+AAA:0.25",
            "LIN+2++B-200:SA", "IMD+F++:::Washer M8", "QTY+21:200:H87", "MOA+203:10.00", "PRI+AAA:0.05",
            "LIN+3++C-300:SA", "IMD+F++:::Compression spring", "QTY+21:20:H87", "MOA+203:25.00", "PRI+AAA:1.25",
            "UNS+S", "MOA+125:60.00", "MOA+124:11.40", "MOA+86:71.40", "CNT+2:3", "UNT+34+1");

        var parsed = await new EdifactOrderParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);
        // The parser files any LIN item code under BuyerItemCode whatever its 7143 qualifier, so ":SA" reads back here.
        parsed.Lines.Select(l => (l.BuyerItemCode, l.Unit)).Should().Equal(("A-100", "H87"), ("B-200", "H87"), ("C-300", "H87"));
        parsed.RequestedDeliveryDate.Should().Be(new DateOnly(2026, 12, 15));
        parsed.Parties!.Single(p => p.Role == "shipTo").Should().Match<ParsedParty>(p =>
            p.Name == StrictOrderFixture.ShipToName && p.City == StrictOrderFixture.ShipToCity && p.Country == "DE");
    }

    [Fact]
    public async Task Order_reference_buyer_codes_tax_and_ids_have_their_homes()
    {
        var text = await StrictOrderFixture.Render("edifact-orders", StrictOrderFixture.WithEveryValue());
        var segs = text.TrimEnd().Split('\'', StringSplitOptions.RemoveEmptyEntries).ToList();

        segs.SkipWhile(s => !s.StartsWith("FTX")).Take(3).Should().Equal(
            "FTX+AAB+++30 days net", "RFF+ON:FRAME-2026-01", "NAD+BY+++Example Buyer Ltd+Example Street 1+Berlin++10115+DE");
        segs.Should().Contain("NAD+SU+0000000000000::9++Example Supplier OU+Example Road 2+Tallinn++10111+EE");
        segs.SkipWhile(s => s != "LIN+1++A-100:SA").Take(7).Should().Equal(
            "LIN+1++A-100:SA", "PIA+1+BUY-1:BP", "IMD+F++:::Hex bolt M8", "QTY+21:100:H87", "MOA+203:25.00", "PRI+AAA:0.25", "TAX+7+VAT+++:::19");
        new ConformanceService().Check(text, OutputFormat.EdifactOrders).ErrorCount.Should().Be(0);
    }

    [Fact]
    public async Task A_delivery_address_read_without_a_name_is_sent_under_the_buyers_name()
    {
        var text = await StrictOrderFixture.Render("edifact-orders", StrictOrderFixture.Read(without: [FieldMeanings.DeliveryName]));
        text.Should().Contain("'NAD+ST+++Example Buyer Ltd+Warehouse Road 3+Hamburg++20095+DE'");
    }

    [Fact]
    public async Task No_totals_read_means_no_total_segments() =>
        (await StrictOrderFixture.Render("edifact-orders", StrictOrderFixture.Read(without: [FieldMeanings.TotalNet, FieldMeanings.TotalTax, FieldMeanings.TotalGross])))
            .Should().NotContainAny("MOA+125", "MOA+124", "MOA+86");

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
        (await new EdifactOrderParser().ParseAsync(new MemoryStream(bytes.ToArray()), CancellationToken.None)).BuyerName.Should().Be("Example Sokolaad OÜ");
    }

    [Fact]
    public async Task A_unoc_file_sent_as_utf_8_still_reads_right()
    {
        var render = await StandardOutputs.RenderAsync("edifact-orders", StrictOrderFixture.With(FieldMeanings.SenderName, "Example OÜ"),
            FlowFieldList.Empty, "lines", Guid.Empty, CancellationToken.None);
        var latin1 = new MemoryStream();
        await render.Result!.Content.CopyToAsync(latin1);
        var utf8 = Encoding.UTF8.GetBytes(Encoding.Latin1.GetString(latin1.ToArray()));
        (await new EdifactOrderParser().ParseAsync(new MemoryStream(utf8), CancellationToken.None)).BuyerName.Should().Be("Example OÜ");
    }

    [Fact]
    public async Task An_identifier_with_a_letter_outside_latin_1_is_refused_never_folded()
    {
        var render = await StandardOutputs.RenderAsync("edifact-orders", StrictOrderFixture.With(FieldMeanings.DocumentNumber, "PO-文-1"),
            FlowFieldList.Empty, "lines", Guid.Empty, CancellationToken.None);
        (render.Ok, render.Detail).Should().Be((false, "The order number has a character this standard can't carry."));
    }
}
