using FluentAssertions;
using ProcuLink.Core.Documents;

namespace ProcuLink.Api.Tests.Documents;

public class ReadModelJsonTests
{
    [Fact]
    public void ReadResult_round_trips_with_tables_and_locations()
    {
        var loc = new SourceLocation(2, new Box(0.1, 0.2, 0.3, 0.05), null);
        var result = new ReadResult(
            "Invoice",
            [new ReadField("number", "Number", "A-1", FieldTypes.Text, FieldMeanings.DocumentNumber, 0.93, loc, false, true, false)],
            [new ReadTable("lines", "Lines",
                [new ReadColumn("desc", "Description", FieldTypes.Text, null), new ReadColumn("amt", "Amount", FieldTypes.Money, FieldMeanings.LineAmount)],
                [new ReadRow(new Dictionary<string, ReadCell>
                {
                    ["desc"] = new("Bolt", 0.8, new SourceLocation(null, null, "cell:r2c1"), false, false),
                    ["amt"] = new(null, 0, null, true, true),
                })],
                true)],
            true);

        var json = DocumentJson.Serialize(result);
        var back = DocumentJson.Deserialize<ReadResult>(json);

        back.Should().BeEquivalentTo(result);
        json.Should().Contain("\"kindLabel\":\"Invoice\"").And.Contain("\"address\":null");
    }

    [Fact]
    public void Empty_json_object_deserialises_to_empty_result_and_list()
    {
        DocumentJson.Deserialize<ReadResult>("{}").Should().BeEquivalentTo(ReadResult.Empty);
        DocumentJson.Deserialize<FlowFieldList>("{}").Should().BeEquivalentTo(FlowFieldList.Empty);
    }
}
