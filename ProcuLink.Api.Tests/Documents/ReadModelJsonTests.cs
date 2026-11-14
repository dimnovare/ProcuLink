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

    [Fact]
    public void An_empty_value_carries_no_confidence_however_it_was_built()
    {
        var blank = new ReadField("iban", "IBAN", "  ", FieldTypes.Text, null, 0.9, null, false, true, false);
        blank.Confidence.Should().Be(0);                                                            // as a reader builds it
        (new ReadCell("7", 0.8, null, false, false) with { Value = null }).Confidence.Should().Be(0);   // cleared afterwards
        new ReadCell("7", 0.8, null, false, false).Confidence.Should().Be(0.8);                      // a value keeps its own
        const string stored = """{"kindLabel":null,"fields":[{"key":"iban","label":"IBAN","value":null,"type":"text","meaning":null,"confidence":1,"location":null,"notOnPage":false,"listed":true,"edited":false}],"tables":[],"truncated":false}""";
        DocumentJson.Deserialize<ReadResult>(stored).Fields[0].Confidence.Should().Be(0);            // a row stored before this change
        System.Text.Json.JsonDocument.Parse(DocumentJson.Serialize(blank)).RootElement.GetProperty("confidence").GetDouble().Should().Be(0);   // and what every API sends
    }
}
