using System.Text;
using System.Text.Json;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Tests.SourceMap;

/// <summary>
/// Tests for <see cref="SourceMapReDerive"/> and the integration of the SourceMap layer with
/// <see cref="MappedTransformService"/>.
///
/// Key invariants:
///  1. Token-value resolution: SourceToken id lookup → raw value before manipulators.
///  2. FixedValue fallback when no token match.
///  3. Pass-through fallback when neither token nor FixedValue set.
///  4. Manipulator chain applied after value selection.
///  5. No-SourceMap → byte-for-byte unchanged (default path invariant).
///  6. Unresolved-line guard: SupplierItemCode can never become non-empty via SourceMap
///     when the line is NeedsReview (guard is on the entity flag, not the remap result).
///  7. SupplierItemCode empty/whitespace from remap → kept empty so validation blocks delivery.
/// </summary>
public class SourceMapReDeriverTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, string> MakeHeaderRow(
        string poNumber = "PO-001",
        string buyerName = "Acme",
        string currency = "EUR") => new()
    {
        ["PoNumber"]  = poNumber,
        ["OrderDate"] = "2026-01-01",
        ["BuyerName"] = buyerName,
        ["Currency"]  = currency,
        ["SupplierName"] = "Supplier Co",
    };

    private static Dictionary<string, string> MakeLineRow(
        string supplierItemCode = "SUP-1") => new()
    {
        ["PoNumber"]         = "PO-001",
        ["OrderDate"]        = "2026-01-01",
        ["BuyerName"]        = "Acme",
        ["Currency"]         = "EUR",
        ["SupplierName"]     = "Supplier Co",
        ["LineNumber"]       = "1",
        ["BuyerItemCode"]    = "BUY-1",
        ["SupplierItemCode"] = supplierItemCode,
        ["Description"]      = "Widget",
        ["Quantity"]         = "3",
        ["Unit"]             = "EA",
        ["UnitPrice"]        = "10",
        ["LineTotal"]        = "30",
    };

    private static SourceToken MakeCsvToken(int row, int col, string value) =>
        new(Id: $"cell:r{row}c{col}", Label: $"Row {row}, col{col}", Value: value, Group: row == 1 ? "header" : "line");

    // ── 1. Token-value resolution ─────────────────────────────────────────────

    [Fact]
    public void ApplyToHeaderRow_SourceTokenMatch_OverridesFieldValue()
    {
        var tokens = new[] { MakeCsvToken(1, 1, "NEW-PO-999") };
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { SourceToken = "cell:r1c1" },
            },
        };

        var result = SourceMapReDerive.ApplyToHeaderRow(MakeHeaderRow(), @override, tokens);

        result["PoNumber"].Should().Be("NEW-PO-999");
    }

    [Fact]
    public void ApplyToLineRow_SourceTokenMatch_OverridesFieldValue()
    {
        var tokens = new[] { MakeCsvToken(2, 5, "SUP-REMAP") };
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["SupplierItemCode"] = new SourceFieldRule { SourceToken = "cell:r2c5" },
            },
        };

        var result = SourceMapReDerive.ApplyToLineRow(MakeLineRow(), @override, tokens);

        result["SupplierItemCode"].Should().Be("SUP-REMAP");
    }

    // ── 2. FixedValue fallback ────────────────────────────────────────────────

    [Fact]
    public void ApplyToHeaderRow_FixedValue_UsedWhenNoSourceToken()
    {
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["Currency"] = new SourceFieldRule { FixedValue = "USD" },
            },
        };

        var result = SourceMapReDerive.ApplyToHeaderRow(MakeHeaderRow(), @override, Array.Empty<SourceToken>());

        result["Currency"].Should().Be("USD");
    }

    [Fact]
    public void ApplyToLineRow_FixedValue_UsedWhenTokenNotFound()
    {
        // Rule says token "cell:r99c1" but it's not in the token list — falls back to FixedValue.
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["Description"] = new SourceFieldRule
                {
                    SourceToken = "cell:r99c1",
                    FixedValue  = "FALLBACK-DESC",
                },
            },
        };

        var result = SourceMapReDerive.ApplyToLineRow(MakeLineRow(), @override, Array.Empty<SourceToken>());

        result["Description"].Should().Be("FALLBACK-DESC");
    }

    // ── 3. Pass-through when neither token nor FixedValue ─────────────────────

    [Fact]
    public void ApplyToHeaderRow_NoTokenNoFixed_KeepsExistingValue()
    {
        // Rule for PoNumber but both SourceToken and FixedValue are null — pass-through.
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { SourceToken = null, FixedValue = null },
            },
        };

        var result = SourceMapReDerive.ApplyToHeaderRow(MakeHeaderRow("ORIG-PO"), @override, Array.Empty<SourceToken>());

        result["PoNumber"].Should().Be("ORIG-PO");
    }

    // ── 4. Manipulator chain applied ──────────────────────────────────────────

    [Fact]
    public void ApplyToHeaderRow_Manipulator_AppliedAfterTokenValue()
    {
        var tokens = new[] { MakeCsvToken(1, 1, "  po-001  ") };
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule
                {
                    SourceToken   = "cell:r1c1",
                    Manipulators  = new List<ManipulatorEntry>
                    {
                        new ManipulatorEntry { Type = "Trim", Params = new List<string>() },
                    },
                },
            },
        };

        var result = SourceMapReDerive.ApplyToHeaderRow(MakeHeaderRow(), @override, tokens);

        result["PoNumber"].Should().Be("po-001");
    }

    [Fact]
    public void ApplyToHeaderRow_Manipulator_AppliedAfterFixedValue()
    {
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["BuyerName"] = new SourceFieldRule
                {
                    FixedValue   = "acme corp",
                    Manipulators = new List<ManipulatorEntry>
                    {
                        new ManipulatorEntry { Type = "Replace", Params = new List<string> { "acme", "ACME" } },
                    },
                },
            },
        };

        var result = SourceMapReDerive.ApplyToHeaderRow(MakeHeaderRow(), @override, Array.Empty<SourceToken>());

        result["BuyerName"].Should().Be("ACME corp");
    }

    // ── 5. No-SourceMap invariant ──────────────────────────────────────────────

    [Fact]
    public void ApplyToHeaderRow_NullSourceMap_ReturnsSameDictionary()
    {
        var headerRow = MakeHeaderRow();
        var @override = new OrderMappingOverride(); // no SourceMap

        var result = SourceMapReDerive.ApplyToHeaderRow(headerRow, @override, Array.Empty<SourceToken>());

        // When no SourceMap, the SAME reference is returned (fast path).
        result.Should().BeSameAs(headerRow);
    }

    [Fact]
    public void ApplyToLineRow_NullSourceMap_ReturnsSameDictionary()
    {
        var lineRow  = MakeLineRow();
        var @override = new OrderMappingOverride();

        var result = SourceMapReDerive.ApplyToLineRow(lineRow, @override, Array.Empty<SourceToken>());

        result.Should().BeSameAs(lineRow);
    }

    [Fact]
    public void ApplyToHeaderRow_EmptySourceMap_ReturnsSameDictionary()
    {
        var headerRow = MakeHeaderRow();
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>(), // empty
        };

        var result = SourceMapReDerive.ApplyToHeaderRow(headerRow, @override, Array.Empty<SourceToken>());

        result.Should().BeSameAs(headerRow);
    }

    // ── 6 & 7. Unresolved-guard: SupplierItemCode must stay empty ─────────────

    [Fact]
    public void ApplyToLineRow_SourceMapProducesEmptySupplierCode_KeptEmpty()
    {
        // A SourceMap that maps SupplierItemCode to an explicitly empty FixedValue must NOT
        // produce a non-empty code — the guard restores empty string so delivery validation blocks.
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["SupplierItemCode"] = new SourceFieldRule { FixedValue = "   " }, // whitespace only
            },
        };

        var result = SourceMapReDerive.ApplyToLineRow(MakeLineRow(""), @override, Array.Empty<SourceToken>());

        result["SupplierItemCode"].Should().BeEmpty();
    }

    [Fact]
    public void ApplyToLineRow_SourceMapProducesValidSupplierCode_AppliedCorrectly()
    {
        // Sanity: a non-empty remap DOES produce the expected value.
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["SupplierItemCode"] = new SourceFieldRule { FixedValue = "SUP-XYZ" },
            },
        };

        var result = SourceMapReDerive.ApplyToLineRow(MakeLineRow(""), @override, Array.Empty<SourceToken>());

        result["SupplierItemCode"].Should().Be("SUP-XYZ");
    }

    // ── Integration: MappedTransformService + SourceMap produces correct CSV ──

    [Fact]
    public void MappedTransformService_WithSourceMap_RemapsToCsvOutput()
    {
        // Build an order entity
        var order = BuildOrder();

        // Token list: simulate a CSV source file where cell:r2c1 holds the PO number "REMAPPED-PO"
        var tokens = new List<SourceToken>
        {
            MakeCsvToken(1, 1, "po_number"),    // header cell
            MakeCsvToken(2, 1, "REMAPPED-PO"),  // data cell
        };

        // Override: SourceMap remaps PoNumber to token cell:r2c1; Output maps PoNumber to "po_num" column.
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["PoNumber"] = new SourceFieldRule { SourceToken = "cell:r2c1" },
            },
            Output = new OutputMappingConfig
            {
                Header = new Dictionary<string, OutputFieldRule>
                {
                    ["po"] = new OutputFieldRule { OutputPath = "po_num", CanonicalField = "PoNumber" },
                },
                Lines = new Dictionary<string, OutputFieldRule>
                {
                    ["sup"] = new OutputFieldRule { OutputPath = "sup_code", CanonicalField = "SupplierItemCode" },
                },
            },
        };

        var result = new MappedTransformService().Build(order, @override, OutputFormat.Csv, tokens);

        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content, Encoding.UTF8);
        var csv = reader.ReadToEnd();

        // The CSV header row should have the output column names.
        csv.Should().Contain("po_num");
        // The data row should have the REMAPPED PO number (from token), not the original "PO-9001".
        csv.Should().Contain("REMAPPED-PO");
        csv.Should().NotContain("PO-9001");
    }

    [Fact]
    public void MappedTransformService_NoSourceMap_ProducesSameOutputAsWithoutTokens()
    {
        // The default (no SourceMap) path must be byte-for-byte identical.
        var order = BuildOrder();
        var @override = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = new Dictionary<string, OutputFieldRule>
                {
                    ["po"] = new OutputFieldRule { OutputPath = "po_num", CanonicalField = "PoNumber" },
                },
                Lines = new Dictionary<string, OutputFieldRule>
                {
                    ["sup"] = new OutputFieldRule { OutputPath = "sup_code", CanonicalField = "SupplierItemCode" },
                },
            },
        };

        // With empty tokens (no SourceMap).
        var resultA = new MappedTransformService().Build(order, @override, OutputFormat.Csv);
        resultA.Content.Position = 0;
        var csvA = new StreamReader(resultA.Content, Encoding.UTF8).ReadToEnd();

        // With null tokens explicitly.
        var resultB = new MappedTransformService().Build(order, @override, OutputFormat.Csv, null);
        resultB.Content.Position = 0;
        var csvB = new StreamReader(resultB.Content, Encoding.UTF8).ReadToEnd();

        csvA.Should().Be(csvB, "no SourceMap path must be byte-for-byte identical regardless of token argument");
        csvA.Should().Contain("PO-9001"); // original parsed PO number present
    }

    [Fact]
    public void MappedTransformService_UnresolvedLine_StillThrowsTransformValidationException_WithSourceMap()
    {
        // Even with a SourceMap that re-maps SupplierItemCode to a non-empty value,
        // a line flagged NeedsReview = true must still fail the ValidateOrder guard.
        var order = BuildOrder(needsReview: true);
        var @override = new OrderMappingOverride
        {
            SourceMap = new Dictionary<string, SourceFieldRule>
            {
                ["SupplierItemCode"] = new SourceFieldRule { FixedValue = "SUP-999" },
            },
            Output = new OutputMappingConfig
            {
                Header = new Dictionary<string, OutputFieldRule>(),
                Lines  = new Dictionary<string, OutputFieldRule>
                {
                    ["sup"] = new OutputFieldRule { OutputPath = "code", CanonicalField = "SupplierItemCode" },
                },
            },
        };

        // Act — must throw because NeedsReview=true on the line entity
        var act = () => new MappedTransformService().Build(order, @override, OutputFormat.Csv, Array.Empty<SourceToken>());

        act.Should().Throw<TransformValidationException>();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static PurchaseOrderEntity BuildOrder(bool needsReview = false)
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-9001",
            BuyerName  = "Test Buyer",
            OrderDate  = new DateOnly(2026, 1, 1),
            Currency   = "EUR",
            Status     = "ready",
        };

        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 1,
                BuyerItemCode    = "B-1",
                SupplierItemCode = needsReview ? null : "SUP-1",
                Description      = "Widget",
                Quantity         = 2m,
                Unit             = "EA",
                UnitPrice        = 5m,
                NeedsReview      = needsReview,
                Confidence       = 1.0f,
            },
        };

        return order;
    }
}
