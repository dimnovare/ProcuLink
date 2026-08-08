using System.Text;
using System.Text.Json;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// Tests for the WHOLE-DOCUMENT Scriban template output mode
/// (<see cref="ScribanTemplateTransformService"/> + <see cref="ScribanOrderModel"/>):
///   • the founder's distributor-style nested JSON renders correctly, including the Lines loop
///     and <c>for.last</c> comma handling;
///   • the documented aliases (OrderNr / DistributorPid / OrderedPrice / Qty) resolve;
///   • ShippingAddress populates from custom fields and from CanonicalJson;
///   • missing fields are empty (never "null") and unknown members render empty (relaxed access);
///   • the curated safe builtins (string/array/math) work, but there is no file/IO surface;
///   • an invalid template fails closed with a clear message (never throws / never delivers);
///   • the review guard (NeedsReview / null SupplierItemCode) still blocks the transform.
/// </summary>
public class ScribanTemplateTransformServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PurchaseOrderEntity BuildOrder(
        IEnumerable<PurchaseOrderLineEntity>? lines = null,
        JsonDocument? canonicalJson = null,
        string? supplierName = null)
    {
        var order = new PurchaseOrderEntity
        {
            Id            = Guid.NewGuid(),
            OrgId         = Guid.NewGuid(),
            SupplierId    = Guid.NewGuid(),
            PoNumber      = "PO-9001",
            BuyerName     = "Acme Buyer Ltd",
            OrderDate     = new DateOnly(2026, 5, 1),
            Currency      = "EUR",
            Status        = "ready",
            SupplierName  = supplierName,
            CanonicalJson = canonicalJson,
        };

        order.Lines = (lines ?? new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 3m, Unit = "EA", UnitPrice = 10m,
                NeedsReview = false, Confidence = 1.0f,
            },
            new PurchaseOrderLineEntity
            {
                LineNumber = 2, BuyerItemCode = "B-2", SupplierItemCode = "SUP-2",
                Description = "Gadget", Quantity = 2m, Unit = "EA", UnitPrice = 5.5m,
                NeedsReview = false, Confidence = 1.0f,
            },
        }).ToList();

        return order;
    }

    private static string ReadText(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // The exact distributor-style example the founder gave.
    private const string DistributorJsonTemplate =
        "{\"customerOrderNumber\":\"{{OrderNr}}\"," +
        "\"notes\":\"Order for {{ShippingAddress.Company}}\"," +
        "\"shipToInfo\":{\"contact\":\"{{ShippingAddress.FirstName}} {{ShippingAddress.LastName}}\"," +
        "\"city\":\"{{ShippingAddress.City}}\"}," +
        "\"lines\":[{{ for Line in Lines }}{\"customerLineNumber\":\"{{Line.LineNr}}\"," +
        "\"distributorPartNumber\":\"{{Line.DistributorPid}}\"," +
        "\"quantity\":{{Line.Qty}}," +
        "\"unitPrice\":{{Line.OrderedPrice}}}{{ if !for.last }},{{ end }}{{ end }}]}";

    // ── The headline test: the founder's distributor example renders to valid JSON ──

    [Fact]
    public void DistributorJsonExample_RendersValidNestedJson_WithLinesLoopAndForLastComma()
    {
        var canonical = JsonDocument.Parse("""
            {
              "shippingAddress": {
                "company": "Northwind Trading OÜ",
                "firstName": "Mari",
                "lastName": "Tamm",
                "city": "Tallinn"
              }
            }
            """);
        var order = BuildOrder(canonicalJson: canonical);
        var @override = new OrderMappingOverride { OutputTemplate = DistributorJsonTemplate };

        var result = new ScribanTemplateTransformService().Build(order, @override);
        var text   = ReadText(result);

        // Must be valid JSON (no trailing comma, correct nesting).
        using var doc = JsonDocument.Parse(text);
        var rootEl = doc.RootElement;

        rootEl.GetProperty("customerOrderNumber").GetString().Should().Be("PO-9001");
        rootEl.GetProperty("notes").GetString().Should().Be("Order for Northwind Trading OÜ");
        rootEl.GetProperty("shipToInfo").GetProperty("contact").GetString().Should().Be("Mari Tamm");
        rootEl.GetProperty("shipToInfo").GetProperty("city").GetString().Should().Be("Tallinn");

        var linesEl = rootEl.GetProperty("lines");
        linesEl.GetArrayLength().Should().Be(2);

        var l1 = linesEl[0];
        l1.GetProperty("customerLineNumber").GetString().Should().Be("1");
        l1.GetProperty("distributorPartNumber").GetString().Should().Be("SUP-1");
        l1.GetProperty("quantity").GetDecimal().Should().Be(3m);
        l1.GetProperty("unitPrice").GetDecimal().Should().Be(10m);

        var l2 = linesEl[1];
        l2.GetProperty("distributorPartNumber").GetString().Should().Be("SUP-2");
        l2.GetProperty("unitPrice").GetDecimal().Should().Be(5.5m);

        // Content type defaults to JSON; extension follows.
        result.ContentType.Should().Be("application/json");
        result.FileExtension.Should().Be(".json");
    }

    [Fact]
    public void ForLast_NoTrailingComma_SingleLine()
    {
        var order = BuildOrder(lines: new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Description = "Solo", Quantity = 1m, Unit = "EA", UnitPrice = 9m,
                NeedsReview = false, Confidence = 1.0f,
            },
        });
        var @override = new OrderMappingOverride { OutputTemplate = DistributorJsonTemplate };

        var text = ReadText(new ScribanTemplateTransformService().Build(order, @override));

        // One element, no comma inside the array.
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("lines").GetArrayLength().Should().Be(1);
    }

    // ── Aliases ──

    [Fact]
    public void Aliases_OrderNr_DistributorPid_OrderedPrice_Qty_AllResolve()
    {
        var order = BuildOrder();
        var tmpl =
            "{{OrderNr}}|{{PoNumber}}|" +
            "{{ for L in Lines }}{{L.LineNr}}={{L.DistributorPid}}@{{L.OrderedPrice}}x{{L.Qty}};{{ end }}";
        var @override = new OrderMappingOverride { OutputTemplate = tmpl };

        var text = ReadText(new ScribanTemplateTransformService().Build(order, @override));

        text.Should().Be("PO-9001|PO-9001|1=SUP-1@10x3;2=SUP-2@5.5x2;");
    }

    // ── ShippingAddress from custom fields ──

    [Fact]
    public void ShippingAddress_PopulatesFromHeaderCustomFields()
    {
        var order = BuildOrder();
        var @override = new OrderMappingOverride
        {
            CustomFields = new()
            {
                new CustomField { Key = "City",        Scope = "header", Value = "Riga" },
                new CustomField { Key = "ShipToCompany", Scope = "header", Value = "Baltic Foods AS" },
            },
            OutputTemplate = "{{ShippingAddress.City}}|{{ShippingAddress.Company}}|{{ShippingAddress.PostalCode}}",
        };

        var text = ReadText(new ScribanTemplateTransformService().Build(order, @override));

        // City direct, Company via "ShipTo"+key, PostalCode absent => empty.
        text.Should().Be("Riga|Baltic Foods AS|");
    }

    [Fact]
    public void ShippingAddress_AllKeysPresentAndEmptyWhenAbsent()
    {
        var order = BuildOrder();
        var svc   = new ScribanTemplateTransformService();

        // "All keys present" is the claim in the name, so the key set itself is under test.
        // Without this the sweep below verifies whatever the list happens to hold — drop a key
        // from the model and the test still renders every remaining key correctly and reports
        // green, having stopped checking the one that went missing.
        ScribanOrderModel.ShippingAddressKeys.Should().BeEquivalentTo(new[]
        {
            "Company", "FirstName", "LastName", "Address1", "Address2",
            "City", "ProvinceCode", "State", "PostalCode", "CountryCode", "Phone", "Email",
        });

        foreach (var key in ScribanOrderModel.ShippingAddressKeys)
        {
            var outcome = svc.Render("{{ ShippingAddress." + key + " }}", order, null);
            outcome.Ok.Should().BeTrue();
            outcome.Text.Should().Be("", $"absent {key} must render empty, not 'null'");
        }
    }

    // ── Missing-field safety / relaxed member access ──

    [Fact]
    public void UnknownMember_RendersEmpty_NeverNullText()
    {
        var order = BuildOrder();
        var outcome = new ScribanTemplateTransformService()
            .Render("[{{ TotallyUnknownField }}][{{ Lines[0].NoSuchThing }}]", order, null);

        outcome.Ok.Should().BeTrue();
        outcome.Text.Should().Be("[][]");
    }

    [Fact]
    public void MissingEnrichmentTotals_RenderEmpty()
    {
        var order = BuildOrder(); // SubTotal/TaxTotal/GrandTotal all null
        var outcome = new ScribanTemplateTransformService()
            .Render("{{SubTotal}}|{{TaxTotal}}|{{GrandTotal}}|{{PaymentTerms}}", order, null);

        outcome.Ok.Should().BeTrue();
        outcome.Text.Should().Be("|||");
    }

    // ── Safe builtins work; no IO surface ──

    [Fact]
    public void SafeBuiltins_String_Array_Math_AreAvailable()
    {
        var order = BuildOrder();
        var tmpl =
            "{{ BuyerName | string.upcase }}|" +
            "{{ Lines | array.size }}|" +
            "{{ math.round 5.567 1 }}";
        var outcome = new ScribanTemplateTransformService().Render(tmpl, order, null);

        outcome.Ok.Should().BeTrue();
        outcome.Text.Should().Be("ACME BUYER LTD|2|5.6");
    }

    [Fact]
    public void Include_FailsClosed_NoTemplateLoader()
    {
        var order = BuildOrder();
        // {{ include }} requires a TemplateLoader, which we leave null → it must NOT read any file.
        var outcome = new ScribanTemplateTransformService()
            .Render("{{ include 'etc/passwd' }}", order, null);

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().NotBeNullOrEmpty();
    }

    // ── Invalid-template safety ──

    [Fact]
    public void InvalidTemplate_Render_ReturnsFailure_NeverThrows()
    {
        var order = BuildOrder();
        // Unbalanced for/end → compile error.
        var outcome = new ScribanTemplateTransformService()
            .Render("{{ for x in Lines }}oops", order, null);

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("compile");
    }

    [Fact]
    public void InvalidTemplate_Build_ThrowsTransformTemplateException_WithMessage()
    {
        var order = BuildOrder();
        var @override = new OrderMappingOverride { OutputTemplate = "{{ for x in Lines }}oops" };

        var act = () => new ScribanTemplateTransformService().Build(order, @override);

        act.Should().Throw<TransformTemplateException>()
            .WithMessage("*compile*");
    }

    // ── Review guard ──

    [Fact]
    public void Build_UnresolvedLine_ThrowsTransformValidationException()
    {
        var order = BuildOrder(lines: new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-1", SupplierItemCode = "SUP-1",
                Description = "OK", Quantity = 1m, Unit = "EA", UnitPrice = 1m,
                NeedsReview = false, Confidence = 1.0f,
            },
            new PurchaseOrderLineEntity
            {
                LineNumber = 2, BuyerItemCode = "B-2", SupplierItemCode = null, // unresolved
                Description = "Needs review", Quantity = 1m, Unit = "EA", UnitPrice = 1m,
                NeedsReview = true, Confidence = 0.1f,
            },
        });
        var @override = new OrderMappingOverride { OutputTemplate = "{{OrderNr}}" };

        var act = () => new ScribanTemplateTransformService().Build(order, @override);

        act.Should().Throw<TransformValidationException>()
            .Which.UnresolvedLineNumbers.Should().Contain(2);
    }

    [Fact]
    public void Build_NoTemplate_ThrowsArgumentException()
    {
        var order = BuildOrder();
        var @override = new OrderMappingOverride { OutputTemplate = "   " };

        var act = () => new ScribanTemplateTransformService().Build(order, @override);

        act.Should().Throw<ArgumentException>();
    }

    // ── Content type / extension ──

    [Fact]
    public void CustomContentType_IsHonoured_AndDrivesExtension()
    {
        var order = BuildOrder();
        var @override = new OrderMappingOverride
        {
            OutputTemplate            = "PO,{{OrderNr}}\n{{ for L in Lines }}{{L.DistributorPid}},{{L.Qty}}\n{{ end }}",
            OutputTemplateContentType = "text/csv",
        };

        var result = new ScribanTemplateTransformService().Build(order, @override);

        result.ContentType.Should().Be("text/csv");
        result.FileExtension.Should().Be(".csv");
        ReadText(result).Should().Contain("PO,PO-9001");
    }

    // ── Reader gating ──

    [Fact]
    public void HasUsableTemplate_TrueOnlyForNonBlankTemplate()
    {
        OrderMappingOverrideReader.HasUsableTemplate(null).Should().BeFalse();
        OrderMappingOverrideReader.HasUsableTemplate(new OrderMappingOverride()).Should().BeFalse();
        OrderMappingOverrideReader.HasUsableTemplate(
            new OrderMappingOverride { OutputTemplate = "  " }).Should().BeFalse();
        OrderMappingOverrideReader.HasUsableTemplate(
            new OrderMappingOverride { OutputTemplate = "{{OrderNr}}" }).Should().BeTrue();
    }

    [Fact]
    public void Reader_RoundTripsTemplateThroughCanonicalJson()
    {
        var overrideJson = JsonSerializer.Serialize(new
        {
            mappingOverride = new
            {
                outputTemplate            = "{{OrderNr}}",
                outputTemplateContentType = "text/plain",
            },
        });
        using var canonical = JsonDocument.Parse(overrideJson);

        var read = OrderMappingOverrideReader.Read(canonical);

        read.Should().NotBeNull();
        read!.OutputTemplate.Should().Be("{{OrderNr}}");
        read.OutputTemplateContentType.Should().Be("text/plain");
        OrderMappingOverrideReader.HasUsableTemplate(read).Should().BeTrue();
    }
}
