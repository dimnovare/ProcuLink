using System;
using System.Collections.Generic;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;
using Scriban.Runtime;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Phase 2: the per-line <c>{{ line.catalog.* }}</c> Scriban accessor. The catalog row is
/// pre-resolved into the model by the caller (no DB access at render time → sandbox preserved).
/// Catalog is a READ-ONLY suggestion — it never overwrites the PO value.
/// </summary>
public class CatalogScribanModelTests
{
    [Fact]
    public void Line_exposes_catalog_object_when_lookup_has_the_code()
    {
        var order = new PurchaseOrderEntity
        {
            PoNumber = "PO1", Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, SupplierItemCode = "S-1", Quantity = 2, UnitPrice = 5m } },
        };
        var lookup = new Dictionary<string, SupplierProduct>(StringComparer.OrdinalIgnoreCase)
        {
            ["S-1"] = new SupplierProduct { Code = "S-1", Name = "Widget", Unit = "PC", Price = 4.50m, Currency = "EUR", Barcode = "EAN9" },
        };

        var root = ScribanOrderModel.Build(order, @override: null, catalogLookup: lookup);
        var lines = (List<ScriptObject>)root["Lines"]!;
        var catalog = (ScriptObject)lines[0]["catalog"]!;

        Assert.Equal("S-1", catalog["code"]);
        Assert.Equal("Widget", catalog["name"]);
        Assert.Equal(4.50m, catalog["price"]);   // real number for arithmetic / variance
        Assert.Equal("EAN9", catalog["barcode"]);
    }

    [Fact]
    public void Line_exposes_empty_catalog_object_when_no_match()
    {
        var order = new PurchaseOrderEntity
        {
            PoNumber = "PO1", Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, SupplierItemCode = "MISSING", Quantity = 1, UnitPrice = 1m } },
        };

        var root = ScribanOrderModel.Build(order, @override: null, catalogLookup: new Dictionary<string, SupplierProduct>());
        var lines = (List<ScriptObject>)root["Lines"]!;
        var catalog = (ScriptObject)lines[0]["catalog"]!;

        Assert.False(catalog.ContainsKey("code")); // empty object, relaxed access → "" in templates
    }

    [Fact]
    public void Catalog_object_is_empty_when_no_lookup_passed_byte_identical_default()
    {
        var order = new PurchaseOrderEntity
        {
            PoNumber = "PO1", Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, SupplierItemCode = "S-1", Quantity = 1, UnitPrice = 1m } },
        };

        var root = ScribanOrderModel.Build(order, @override: null); // no catalogLookup
        var lines = (List<ScriptObject>)root["Lines"]!;
        var catalog = (ScriptObject)lines[0]["catalog"]!;

        Assert.False(catalog.ContainsKey("code"));
    }

    [Fact]
    public void Catalog_resolves_by_manufacturer_part_number_when_supplier_code_misses()
    {
        var order = new PurchaseOrderEntity
        {
            PoNumber = "PO1", Currency = "EUR",
            Lines = { new PurchaseOrderLineEntity { LineNumber = 1, SupplierItemCode = "UNKNOWN", ManufacturerPartNumber = "MPN-7", Quantity = 1, UnitPrice = 1m } },
        };
        var lookup = new Dictionary<string, SupplierProduct>(StringComparer.OrdinalIgnoreCase)
        {
            ["MPN-7"] = new SupplierProduct { Code = "S-9", Name = "ByMpn", Price = 3m },
        };

        var root = ScribanOrderModel.Build(order, @override: null, catalogLookup: lookup);
        var lines = (List<ScriptObject>)root["Lines"]!;
        var catalog = (ScriptObject)lines[0]["catalog"]!;

        Assert.Equal("S-9", catalog["code"]);
        Assert.Equal(3m, catalog["price"]);
    }
}
