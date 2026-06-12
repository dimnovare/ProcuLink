using FluentAssertions;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services.Mapping;

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
}
