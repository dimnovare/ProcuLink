using FluentAssertions;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Pins the empty-template guard: a saved PO mapping with NO header rules and NO line
/// rules must be treated as "no mapping" by the parse routing, so the generic CSV parser
/// takes over. Without the guard, template-parsing under an empty config silently blanks
/// every field of every CSV upload for that supplier (found live 2026-06-12: an empty
/// mapping saved on the sample supplier blanked all sample-order lines).
/// </summary>
public class EmptyPoMappingTemplateTests
{
    [Fact]
    public void Null_mapping_is_not_empty_template()
        => OrderIngestionService.IsEmptyTemplate(null).Should().BeFalse();

    [Fact]
    public void Mapping_with_no_header_and_no_line_rules_is_empty()
        => OrderIngestionService.IsEmptyTemplate(new PoMappingConfig()).Should().BeTrue();

    [Fact]
    public void Mapping_with_line_rules_is_usable()
    {
        var cfg = new PoMappingConfig
        {
            Lines = new Dictionary<string, FieldMappingEntry>
            {
                ["BuyerItemCode"] = new() { ExternalField = "sku" },
            },
        };
        OrderIngestionService.IsEmptyTemplate(cfg).Should().BeFalse();
    }

    [Fact]
    public void Mapping_with_only_header_rules_is_usable()
    {
        var cfg = new PoMappingConfig
        {
            Header = new Dictionary<string, FieldMappingEntry>
            {
                ["PoNumber"] = new() { ExternalField = "po" },
            },
        };
        OrderIngestionService.IsEmptyTemplate(cfg).Should().BeFalse();
    }

    // ── IsDegenerateParse ─────────────────────────────────────────────────────
    // The empty-template guard above only catches a template with NO rules. A
    // NON-empty but FORMAT-MISMATCHED template (e.g. a cXML/XPath mapping saved on a
    // supplier whose upload is a flat CSV) passes IsEmptyTemplate, then resolves every
    // field to blank — the order lands with phantom empty lines that slip past the
    // 0-lines guard. IsDegenerateParse detects that "lines present, all blank" shape so
    // the ingest path can fall back to the deterministic CSV parser. (Found live again
    // 2026-06-19: a cXML template on the sample supplier blanked every sample-order line.)

    private static ParsedOrderLine BlankLine(int n) => new(n, "", null, 0m, null, null);
    private static ParsedOrderLine RealLine(int n)  => new(n, "ACME-1", "Widget", 12m, "PCS", 4.50m);

    [Fact]
    public void All_blank_lines_with_no_po_number_is_degenerate()
    {
        var parsed = new ParsedOrder(null, null, null, null,
            new[] { BlankLine(1), BlankLine(2), BlankLine(3), BlankLine(4) });
        OrderIngestionService.IsDegenerateParse(parsed).Should().BeTrue();
    }

    [Fact]
    public void Any_line_with_real_data_is_not_degenerate()
    {
        var parsed = new ParsedOrder(null, null, null, null,
            new[] { BlankLine(1), RealLine(2) });
        OrderIngestionService.IsDegenerateParse(parsed).Should().BeFalse();
    }

    [Fact]
    public void Blank_lines_but_a_po_number_is_not_degenerate()
    {
        // A PO number means the header partially matched — the template is not a total
        // mismatch, so don't override it with the default parser.
        var parsed = new ParsedOrder("PO-123", null, null, null,
            new[] { BlankLine(1), BlankLine(2) });
        OrderIngestionService.IsDegenerateParse(parsed).Should().BeFalse();
    }

    [Fact]
    public void Zero_lines_is_not_degenerate()
    {
        // Handled by the separate 0-lines guard, which fails loudly — not a fallback case.
        var parsed = new ParsedOrder(null, null, null, null, System.Array.Empty<ParsedOrderLine>());
        OrderIngestionService.IsDegenerateParse(parsed).Should().BeFalse();
    }

    [Fact]
    public void A_line_with_only_quantity_is_not_degenerate()
    {
        var parsed = new ParsedOrder(null, null, null, null,
            new[] { new ParsedOrderLine(1, "", null, 5m, null, null) });
        OrderIngestionService.IsDegenerateParse(parsed).Should().BeFalse();
    }
}
