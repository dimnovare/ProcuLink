using System;
using System.Collections.Generic;
using ProcuLink.Transform.Mapping;
using ProcuLink.Transform.Mapping.Manipulators;
using Xunit;

namespace ProcuLink.Transform.Tests.Mapping;

/// <summary>
/// Phase 2: <c>LoadCatalogProduct</c> manipulator. The catalog row is pre-injected into the value
/// bag by the caller under reserved <c>__catalog_*</c> keys; the manipulator only ever sees the
/// row (never the DB) and returns the requested field's RAW string (a suggestion). Missing → "".
/// </summary>
public class LoadCatalogProductManipulatorTests
{
    private static IReadOnlyDictionary<string, string> Row() => new Dictionary<string, string>
    {
        ["__catalog_price"]   = "4.50",
        ["__catalog_code"]    = "S-1",
        ["__catalog_unit"]    = "PC",
        ["__catalog_barcode"] = "EAN9",
        ["SupplierItemCode"]  = "S-1",
    };

    [Theory]
    [InlineData("price", "4.50")]
    [InlineData("code", "S-1")]
    [InlineData("unit", "PC")]
    [InlineData("barcode", "EAN9")]
    public void Extracts_the_requested_catalog_field(string field, string expected)
    {
        var m = new LoadCatalogProductManipulator(new[] { field });
        Assert.Equal(expected, m.Apply(value: "ignored", Row()));
    }

    [Fact]
    public void Field_name_is_case_insensitive()
    {
        var m = new LoadCatalogProductManipulator(new[] { "PRICE" });
        Assert.Equal("4.50", m.Apply("x", Row()));
    }

    [Fact]
    public void Missing_catalog_field_returns_empty_not_throws()
    {
        var m = new LoadCatalogProductManipulator(new[] { "price" });
        Assert.Equal(string.Empty, m.Apply("x", new Dictionary<string, string>()));
    }

    [Fact]
    public void Unknown_field_param_throws()
    {
        Assert.Throws<ArgumentException>(() => new LoadCatalogProductManipulator(new[] { "weight" }));
    }

    [Fact]
    public void Wrong_param_count_throws()
    {
        Assert.Throws<ArgumentException>(() => new LoadCatalogProductManipulator(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => new LoadCatalogProductManipulator(new[] { "price", "code" }));
    }

    [Fact]
    public void Registry_resolves_LoadCatalogProduct()
    {
        var m = ManipulatorRegistry.Resolve("LoadCatalogProduct", new[] { "price" });
        Assert.IsType<LoadCatalogProductManipulator>(m);
    }
}
