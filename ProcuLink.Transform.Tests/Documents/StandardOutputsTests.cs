using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Documents;
using Xunit;

namespace ProcuLink.Transform.Tests.Documents;

public class StandardOutputsTests
{
    private static ReadField Field(string label, string value, string? meaning) =>
        new(FieldKey.Normalise(label), label, value, "text", meaning, 1, null, false, true, false);

    private static readonly (string Label, string? Meaning)[] Columns =
    [
        ("Item code", FieldMeanings.LineItemCode), ("Description", FieldMeanings.LineDescription), ("Qty", FieldMeanings.LineQuantity),
        ("Unit", FieldMeanings.LineUnit), ("Price", FieldMeanings.LineUnitPrice), ("No", FieldMeanings.LineNumber),
    ];

    private static ReadResult Read(string poNumber = "PO-1", string qty1 = "3", string price1 = "9,99", string code2 = "B-2", string no2 = "20")
    {
        ReadRow Row(params string[] values) => new(Columns.Select((c, i) => (c, i))
            .ToDictionary(x => FieldKey.Normalise(x.c.Label), x => new ReadCell(values[x.i], 1, null, false, false)));
        return new ReadResult("Purchase order",
            [Field("PO number", poNumber, FieldMeanings.DocumentNumber), Field("Order date", "13.11.2026", FieldMeanings.IssueDate),
             Field("Currency", "eur", FieldMeanings.Currency), Field("Buyer", "Buyer Co", FieldMeanings.SenderName),
             Field("Seller", "Seller Co", FieldMeanings.ReceiverName)],
            [new ReadTable("lines", "Lines", Columns.Select(c => new ReadColumn(FieldKey.Normalise(c.Label), c.Label, "text", c.Meaning)).ToList(),
                [Row("A-1", "Bolt", qty1, "EA", price1, "10"), Row(code2, "Nut", "2", "EA", "0,50", no2)], true)]);
    }

    private static Task<DocumentRender> Render(string id, ReadResult read) =>
        StandardOutputs.RenderAsync(id, read, FlowFieldList.Empty, "lines", Guid.Empty, CancellationToken.None);

    private static string Text(DocumentRender r)
    {
        r.Ok.Should().BeTrue(r.Detail);
        return new StreamReader(r.Result!.Content).ReadToEnd();
    }

    [Theory]
    [InlineData("cxml-1-2")]
    [InlineData("ubl-2-1-order")]
    [InlineData("x12-850")]
    public async Task Each_standard_writes_a_conformant_file_from_meanings(string id)
    {
        var render = await Render(id, Read());
        var text = Text(render);
        var writer = StandardOutputs.Find(id)!.Writer;

        text.Should().Contain("PO-1").And.Contain("EUR");
        new ConformanceService().Check(text, writer).ErrorCount.Should().Be(0);
        render.Result!.FileExtension.Should().Be(DeliveryMediaTypes.For(writer).FileExtension);
    }

    [Fact]
    public async Task What_a_document_lacks_is_named_and_nothing_is_written()
    {
        var render = await Render("x12-850", Read(poNumber: " ", qty1: "1.500", code2: ""));
        (render.Result is null, render.Error, render.Detail).Should()
            .Be((true, "output_invalid", "This standard needs PO number, Qty (line 1) and Item code (line 2)."));

        var unknown = await Render("nope", Read());
        (unknown.Error, unknown.Detail).Should().Be(("output_invalid", (string?)null));

        // An amount too large for a writer's arithmetic is a file that cannot be written, not a throw.
        var overflow = await Render("cxml-1-2", Read(qty1: "100000000000000000", price1: "1000000000000"));
        (overflow.Error, overflow.Detail).Should().Be(("output_invalid", (string?)null));
    }

    [Fact]
    public async Task Line_numbers_come_from_the_document_only_when_every_row_has_one()
    {
        Text(await Render("cxml-1-2", Read())).Should().Contain("lineNumber=\"10\"").And.Contain("lineNumber=\"20\"");
        Text(await Render("cxml-1-2", Read(no2: ""))).Should().Contain("lineNumber=\"1\"").And.Contain("lineNumber=\"2\"");
    }
}
