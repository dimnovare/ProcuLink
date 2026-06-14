using System.Text.Json;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Phase 2 — proves the SourceCapture.TokensJson reader rebuilds the addressable
/// <c>SourceToken</c> set for BOTH persisted shapes the Phase-1 writer emits
/// (<c>OrderIngestionService.UpsertSourceCaptureAsync</c>):
///   • structured formats: <c>{ id, label, value, group }</c> (group nullable);
///   • PDF/email raw_fields: <c>{ label, value }</c> (no id → synthesised <c>raw:{label}</c>).
/// Values are carried VERBATIM (no numeric parsing) so locale-formatted prices survive intact.
/// </summary>
public class SourceTokenSerializationTests
{
    [Fact]
    public void Deserialize_structured_tokens_round_trips_id_label_value_group()
    {
        using var doc = JsonDocument.Parse(
            """[{"id":"cell:r2c3","label":"Unit Price · row 2","value":"10.50","group":"line"}]""");

        var tokens = SourceTokenSerialization.FromTokensJson(doc);

        var t = Assert.Single(tokens);
        Assert.Equal("cell:r2c3", t.Id);
        Assert.Equal("Unit Price · row 2", t.Label);
        Assert.Equal("10.50", t.Value);
        Assert.Equal("line", t.Group);
    }

    [Fact]
    public void Deserialize_raw_fields_without_id_synthesises_a_stable_id()
    {
        using var doc = JsonDocument.Parse("""[{"label":"EDI id","value":"REDACTED-TAXID"}]""");

        var tokens = SourceTokenSerialization.FromTokensJson(doc);

        var t = Assert.Single(tokens);
        Assert.Equal("REDACTED-TAXID", t.Value);
        Assert.Equal("EDI id", t.Label);
        // No id in the source → a deterministic "raw:{label}" id so SourceMap rules can address it.
        Assert.Equal("raw:EDI id", t.Id);
        Assert.Null(t.Group); // raw_fields carry no group
    }

    [Fact]
    public void Structured_token_with_explicit_null_group_keeps_null_group()
    {
        // The writer serialises Group=null as a literal JSON null for structured tokens.
        using var doc = JsonDocument.Parse(
            """[{"id":"/Order/Header/PoNumber","label":"Order › PoNumber","value":"PO-7","group":null}]""");

        var tokens = SourceTokenSerialization.FromTokensJson(doc);

        var t = Assert.Single(tokens);
        Assert.Equal("/Order/Header/PoNumber", t.Id);
        Assert.Equal("PO-7", t.Value);
        Assert.Null(t.Group);
    }

    [Fact]
    public void Verbatim_values_are_not_numeric_parsed_eu_comma_decimal_survives()
    {
        using var doc = JsonDocument.Parse(
            """[{"id":"cell:r9c4","label":"Net · row 9","value":"1.234,56","group":"line"}]""");

        var t = Assert.Single(SourceTokenSerialization.FromTokensJson(doc));
        Assert.Equal("1.234,56", t.Value); // carried verbatim — no 100x misread
    }

    [Fact]
    public void Null_document_yields_empty_list()
    {
        Assert.Empty(SourceTokenSerialization.FromTokensJson(null));
    }

    [Fact]
    public void Non_array_document_yields_empty_list()
    {
        using var doc = JsonDocument.Parse("""{"not":"an array"}""");
        Assert.Empty(SourceTokenSerialization.FromTokensJson(doc));
    }

    [Fact]
    public void Non_object_array_entries_are_skipped()
    {
        using var doc = JsonDocument.Parse(
            """["loose string", {"label":"Keep","value":"v"}]""");

        var t = Assert.Single(SourceTokenSerialization.FromTokensJson(doc));
        Assert.Equal("Keep", t.Label);
        Assert.Equal("raw:Keep", t.Id);
    }
}
