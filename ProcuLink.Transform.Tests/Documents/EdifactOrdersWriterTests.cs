using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class EdifactOrdersWriterTests
{
    private static readonly (string Label, string? Meaning)[] Columns =
    [
        ("Item code", FieldMeanings.LineItemCode), ("Description", FieldMeanings.LineDescription),
        ("Qty", FieldMeanings.LineQuantity), ("Price", FieldMeanings.LineUnitPrice),
    ];

    private static ReadResult Read(string description)
    {
        ReadRow Row(params string[] values) => new(Columns.Select((c, i) => (c, i))
            .ToDictionary(x => FieldKey.Normalise(x.c.Label), x => new ReadCell(values[x.i], 1, null, false, false)));
        ReadField Field(string label, string value, string meaning) =>
            new(FieldKey.Normalise(label), label, value, "text", meaning, 1, null, false, true, false);
        return new ReadResult("Purchase order",
            [Field("PO number", "PO-1", FieldMeanings.DocumentNumber), Field("Order date", "13.11.2026", FieldMeanings.IssueDate),
             Field("Currency", "eur", FieldMeanings.Currency)],
            [new ReadTable("lines", "Lines", Columns.Select(c => new ReadColumn(FieldKey.Normalise(c.Label), c.Label, "text", c.Meaning)).ToList(),
                [Row("A-1", description, "3", "9.99"), Row("B-2", "Nut", "2", "0.50")], true)]);
    }

    private static async Task<string> Render(string description)
    {
        var render = await StandardOutputs.RenderAsync("edifact-orders", Read(description), FlowFieldList.Empty, "lines", Guid.Empty, CancellationToken.None);
        render.Ok.Should().BeTrue(render.Detail);
        return new StreamReader(render.Result!.Content).ReadToEnd();
    }

    private static async Task<ParsedOrder> Parse(string text) =>
        await new EdifactOrderParser().ParseAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), CancellationToken.None);

    [Fact]
    public async Task Written_orders_pass_the_profile_checker_and_parse_back()
    {
        var text = await Render("Bolt");

        new ConformanceService().Check(text, OutputFormat.EdifactOrders).ErrorCount.Should().Be(0);
        var parsed = await Parse(text);
        (parsed.PoNumber, parsed.OrderDate, parsed.Currency).Should().Be(("PO-1", new DateTime(2026, 11, 13), "EUR"));
        parsed.Lines.Select(l => (l.BuyerItemCode, l.Quantity, l.UnitPrice)).Should()
            .Equal(("A-1", 3m, (decimal?)9.99m), ("B-2", 2m, (decimal?)0.50m));
    }

    [Fact]
    public async Task Control_characters_become_spaces()
    {
        var text = await Render("Bolt\nnut");

        text.Should().Contain("Bolt nut").And.NotContain("Bolt\n");
    }

    [Fact]
    public async Task Text_with_delimiters_is_released()
    {
        var text = await Render("Bolt 5' + nut: M?");

        (await Parse(text)).Lines[0].Description.Should().Be("Bolt 5' + nut: M?");
        AssertUntCounts(Segments(text));
    }

    [Fact]
    public async Task Released_delimiters_in_the_new_party_values()
    {
        var read = StrictOrderFixture.Read();
        read = new ReadResult(read.KindLabel,
            [.. read.Fields.Select(f => f.Meaning == FieldMeanings.DeliveryStreet ? f with { Value = "Dock 5+6 'B': gate" } : f),
             new ReadField("contact", "Contact", "O'Neil: Sales+Ops", "text", FieldMeanings.SenderContactName, 1, null, false, true, false)],
            read.Tables);
        var segments = Segments(await StrictOrderFixture.Render("edifact-orders", read));

        segments.Should().Contain("NAD+ST+++Example Buyer Ltd Warehouse+Dock 5?+6 ?'B?'?: gate+Hamburg++20095+DE")
            .And.Contain("CTA+OC+:O?'Neil?: Sales?+Ops");
        AssertUntCounts(segments);
    }

    // The segments, split on terminators not preceded by the release character.
    private static List<string> Segments(string text)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '?' && i + 1 < text.Length) { current.Append(text[i]).Append(text[++i]); continue; }
            if (text[i] == '\'') { segments.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(text[i]);
        }
        return segments;
    }

    // UNT counts UNH..UNT.
    private static void AssertUntCounts(List<string> segments)
    {
        var unh = segments.FindIndex(s => s.StartsWith("UNH"));
        var unt = segments.FindIndex(s => s.StartsWith("UNT"));
        segments[unt].Should().Be($"UNT+{unt - unh + 1}+1");
    }
}
