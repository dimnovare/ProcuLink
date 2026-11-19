using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Documents;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StrictOrderX12Tests
{
    [Fact]
    public async Task The_850_carries_every_read_value_in_heading_order()
    {
        var text = await StrictOrderFixture.Render("x12-850");
        new ConformanceService().Check(text, OutputFormat.X12).ErrorCount.Should().Be(0);
        text.IndexOf('~').Should().Be(105, "ISA is fixed-width: 105 characters before its terminator");

        var segs = text.Split('~', StringSplitOptions.RemoveEmptyEntries);
        var tx = segs.SkipWhile(s => !s.StartsWith("ST*")).TakeWhile(s => !s.StartsWith("GE*")).ToArray();
        tx.Should().Equal(
            "ST*850*0001", "BEG*00*NE*PO-2026-0042**20261118", "CUR*BY*EUR", "ITD" + new string('*', 12) + "30 days net", "DTM*002*20261215",
            "N1*ST*Example Buyer Ltd Warehouse", "N3*Warehouse Road 3", "N4*Hamburg**20095*DE",
            "N1*BT*Example Buyer Ltd", "N3*Example Street 1", "N4*Berlin**10115*DE",
            "N1*BY*Example Buyer Ltd", "REF*VX*DE000000000", "PER*BD**EM*orders@example.com",
            "N1*VN*Example Supplier OU", "N3*Example Road 2", "N4*Tallinn**10111*EE", "REF*VX*EE000000000",
            "PO1*1*100*PC*0.25*PE*VP*A-100", "PID*F****Hex bolt M8",
            "PO1*2*200*PC*0.05*PE*VP*B-200", "PID*F****Washer M8",
            "PO1*3*20*PC*1.25*PE*VP*C-300", "PID*F****Compression spring",
            "CTT*3", "AMT*TT*71.40", "SE*27*0001");
    }

    [Fact]
    public async Task Order_reference_buyer_codes_and_ids_have_their_homes()
    {
        var segs = (await StrictOrderFixture.Render("x12-850", StrictOrderFixture.WithEveryValue())).Split('~');
        segs.SkipWhile(s => s != "CUR*BY*EUR").Take(3).Should().Equal("CUR*BY*EUR", "REF*PO*FRAME-2026-01", "ITD" + new string('*', 12) + "30 days net");
        segs.Should().Contain(["N1*VN*Example Supplier OU*UL*0000000000000", "N1*BY*Example Buyer Ltd", "PO1*1*100*PC*0.25*PE*BP*BUY-1*VP*A-100"]);
    }

    [Fact]
    public async Task A_delivery_address_read_without_a_name_is_sent_under_the_buyers_name()
    {
        var segs = (await StrictOrderFixture.Render("x12-850", StrictOrderFixture.Read(without: [FieldMeanings.DeliveryName]))).Split('~');
        segs.SkipWhile(s => !s.StartsWith("N1*ST")).Take(3).Should().Equal("N1*ST*Example Buyer Ltd", "N3*Warehouse Road 3", "N4*Hamburg**20095*DE");
        segs.Should().Contain("SE*27*0001");
    }

    [Fact]
    public async Task An_address_without_a_country_writes_no_trailing_separator()
    {
        var segs = (await StrictOrderFixture.Render("x12-850", StrictOrderFixture.Read(without: [FieldMeanings.DeliveryCountry]))).Split('~');
        segs.Should().Contain("N4*Hamburg**20095").And.NotContain(s => s.EndsWith('*'));
    }

    [Fact]
    public async Task No_totals_read_means_no_total_segments() =>
        (await StrictOrderFixture.Render("x12-850", StrictOrderFixture.Read(without: [FieldMeanings.TotalNet, FieldMeanings.TotalTax, FieldMeanings.TotalGross])))
            .Should().NotContain("AMT*TT");

    [Fact]
    public async Task Over_long_street_is_split_over_N301_and_N302()
    {
        var s = string.Concat(Enumerable.Repeat("Example Street ", 5)).Trim();
        var segs = (await StrictOrderFixture.Render("x12-850", StrictOrderFixture.With(FieldMeanings.SenderStreet, s))).Split('~');
        segs.SkipWhile(x => x != "N1*BT*Example Buyer Ltd").ElementAt(1).Should().Be($"N3*{s[..55]}*{s[55..]}");
    }

    [Fact]
    public async Task An_over_long_order_number_is_refused_never_cut()
    {
        var po = "PO-2026-0042-" + new string('0', 10);
        var read = StrictOrderFixture.With(FieldMeanings.DocumentNumber, po);
        var x12 = await StandardOutputs.RenderAsync("x12-850", read, FlowFieldList.Empty, "lines", Guid.Empty, CancellationToken.None);
        (x12.Ok, x12.Error, x12.Detail).Should().Be((false, "output_invalid", "The order number is longer than this standard allows (22 characters)."));
        (await StrictOrderFixture.Render("edifact-orders", read)).Should().Contain($"BGM+220+{po}+9");
    }
}
