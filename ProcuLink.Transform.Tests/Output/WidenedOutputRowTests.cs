using System.Globalization;
using System.Text;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// WP-14 — the widened canonical output row, proven through the PUBLIC transform surface rather
/// than by inspecting the row bag. Every case here binds a name from an output config and asserts
/// the rendered document carries the parsed value, because "the key exists" is not the promise —
/// "the operator can emit it" is.
/// </summary>
public class WidenedOutputRowTests
{
    // ── Fixture: an order with EVERY business field populated with a distinctive value ──

    private const string ShipCity = "Tallinn";

    private static PurchaseOrderEntity FullyPopulatedOrder()
    {
        var order = new PurchaseOrderEntity
        {
            Id        = Guid.NewGuid(),
            PoNumber  = "PO-4711",
            OrderDate = new DateOnly(2026, 3, 4),
            Currency  = "EUR",
            BuyerName = "Acme Buyer AS",

            SupplierName          = "REDACTED-PARTY",
            SubTotal              = 100m,
            TaxTotal              = 22m,
            GrandTotal            = 122m,
            PaymentTerms          = "NET30",
            RequestedDeliveryDate = new DateOnly(2026, 4, 1),

            // ── WP-14 header additions ──
            BuyerOrderRef    = "REQ-9001",
            BuyerTaxId       = "EE100123456",
            ContactName      = "Mari Tamm",
            ContactEmail     = "mari.tamm@acme.example",
            ContactPhone     = "+372 555 0101",
            Incoterms        = "DAP",
            ShippingMethod   = "Road freight",
            ShipToName       = "Acme Warehouse",
            ShipToDeliverTo  = "Gate 4, Receiving",
            ShipToStreet     = "Sadama tee 12",
            ShipToCity       = ShipCity,
            ShipToPostalCode = "10111",
            ShipToCountry    = "EE",
            ShipToEmail      = "warehouse@acme.example",
            ShipToPhone      = "+372 555 0202",
            BillToName       = "Acme Finance",
            BillToDeliverTo  = "Accounts Payable",
            BillToStreet     = "Pärnu mnt 5",
            BillToCity       = "Tartu",
            BillToPostalCode = "51004",
            BillToCountry    = "EE",
            BillToEmail      = "ap@acme.example",
            BillToPhone      = "+372 555 0303",
        };

        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new()
            {
                LineNumber       = 1,
                BuyerItemCode    = "B-1",
                SupplierItemCode = "S-1",
                Description      = "Barcode scanner",
                Quantity         = 2m,
                Unit             = "PCS",
                UnitPrice        = 50m,
                NeedsReview      = false,

                LineAmount   = 100m,
                TaxRate      = 0.22m,
                DeliveryDate = new DateOnly(2026, 4, 2),

                // ── WP-14 line additions ──
                TaxAmount              = 22m,
                DiscountPercent        = 7.5m,
                NetAmount              = 92.5m,
                ManufacturerPartNumber = "REDACTED-ORDER-DATA",
                ManufacturerName       = "REDACTED-PARTY",
                CustomerPartNumber     = "CUST-777",
                Unspsc                 = "43211711",
                Recipient              = "Loading bay B",
                ContractNumber         = "FRAME-2026-11",
            },
        };

        return order;
    }

    /// <summary>An override whose CSV output binds exactly one canonical name in one scope.</summary>
    private static OrderMappingOverride BindOne(string canonicalField, bool lineScope)
    {
        var rule = new OutputFieldRule { OutputPath = "col", CanonicalField = canonicalField };
        return new OrderMappingOverride
        {
            Output = lineScope
                ? new OutputMappingConfig { Lines = new() { ["col"] = rule } }
                : new OutputMappingConfig { Header = new() { ["col"] = rule } },
        };
    }

    private static string RenderCsv(PurchaseOrderEntity order, OrderMappingOverride @override)
    {
        var result = new MappedTransformService().Build(order, @override, OutputFormat.Csv);
        using var reader = new StreamReader(result.Content, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The single data cell of a one-column CSV (line 2, after the header row).</summary>
    private static string SingleCsvValue(PurchaseOrderEntity order, OrderMappingOverride @override)
    {
        var lines = RenderCsv(order, @override)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();
        lines.Should().HaveCountGreaterThan(1, "a one-column CSV should have a header row and one data row");
        return lines[1].Trim('"');
    }

    // ── 1. One assertion per newly exposed HEADER field ──────────────────────────

    [Theory]
    [InlineData("BuyerOrderRef",    "REQ-9001")]
    [InlineData("BuyerTaxId",       "EE100123456")]
    [InlineData("ContactName",      "Mari Tamm")]
    [InlineData("ContactEmail",     "mari.tamm@acme.example")]
    [InlineData("ContactPhone",     "+372 555 0101")]
    [InlineData("Incoterms",        "DAP")]
    [InlineData("ShippingMethod",   "Road freight")]
    [InlineData("ShipToName",       "Acme Warehouse")]
    [InlineData("ShipToDeliverTo",  "Gate 4, Receiving")]
    [InlineData("ShipToStreet",     "Sadama tee 12")]
    [InlineData("ShipToCity",       ShipCity)]
    [InlineData("ShipToPostalCode", "10111")]
    [InlineData("ShipToCountry",    "EE")]
    [InlineData("ShipToEmail",      "warehouse@acme.example")]
    [InlineData("ShipToPhone",      "+372 555 0202")]
    [InlineData("BillToName",       "Acme Finance")]
    [InlineData("BillToDeliverTo",  "Accounts Payable")]
    [InlineData("BillToStreet",     "Pärnu mnt 5")]
    [InlineData("BillToCity",       "Tartu")]
    [InlineData("BillToPostalCode", "51004")]
    [InlineData("BillToCountry",    "EE")]
    [InlineData("BillToEmail",      "ap@acme.example")]
    [InlineData("BillToPhone",      "+372 555 0303")]
    public void CsvOutput_BindingANewHeaderField_EmitsTheParsedValue(string field, string expected)
    {
        SingleCsvValue(FullyPopulatedOrder(), BindOne(field, lineScope: false))
            .Should().Be(expected, "a custom output binding '{0}' must emit the parsed value", field);
    }

    // ── 2. One assertion per newly exposed LINE field ────────────────────────────

    [Theory]
    [InlineData("TaxAmount",              "22")]
    [InlineData("DiscountPercent",        "7.5")]
    [InlineData("NetAmount",              "92.5")]
    [InlineData("ManufacturerPartNumber", "REDACTED-ORDER-DATA")]
    [InlineData("ManufacturerName",       "REDACTED-PARTY")]
    [InlineData("CustomerPartNumber",     "CUST-777")]
    [InlineData("Unspsc",                 "43211711")]
    [InlineData("Recipient",              "Loading bay B")]
    [InlineData("ContractNumber",         "FRAME-2026-11")]
    public void CsvOutput_BindingANewLineField_EmitsTheParsedValue(string field, string expected)
    {
        SingleCsvValue(FullyPopulatedOrder(), BindOne(field, lineScope: true))
            .Should().Be(expected, "a custom output binding '{0}' must emit the parsed value", field);
    }

    // ── 3. Named acceptance criteria ─────────────────────────────────────────────

    [Fact]
    public void AcceptanceCriterion_CsvOutputBindingShipToCity_EmitsTheParsedCity()
    {
        // Ship-to is not optional on a physical purchase order. Before WP-14 this emitted nothing at
        // all, so a supplier requiring a delivery city simply could not be served by a custom output.
        var csv = RenderCsv(FullyPopulatedOrder(), BindOne("ShipToCity", lineScope: false));

        csv.Should().Contain(ShipCity);
    }

    [Fact]
    public void AcceptanceCriterion_TreeOutputBindingManufacturerPartNumber_EmitsIt()
    {
        // A STRUCTURED format (XML), not CSV: the tree emitter resolves leaves through the same row
        // bag, so a field missing from the bag renders as an empty element rather than failing.
        // OutputNodeTemplateInferrer already INFERS "ManufacturerPartNumber" as a target name from a
        // column called "manufacturerpart" — before WP-14 that inferred rule resolved to nothing.
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Xml,
            Root = new OutputNode
            {
                Name     = "Order",
                NodeType = OutputNodeType.Object,
                Children = new List<OutputNode>
                {
                    new()
                    {
                        Name       = "Items",
                        NodeType   = OutputNodeType.Array,
                        Collection = "lines",
                        Children = new List<OutputNode>
                        {
                            new()
                            {
                                Name     = "Item",
                                NodeType = OutputNodeType.Object,
                                Children = new List<OutputNode>
                                {
                                    new()
                                    {
                                        Name     = "MfrPartId",
                                        NodeType = OutputNodeType.Field,
                                        Rule     = new OutputFieldRule
                                        {
                                            OutputPath     = "MfrPartId",
                                            CanonicalField = "ManufacturerPartNumber",
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        var order    = FullyPopulatedOrder();
        var result   = new OutputTemplateEmitter().Emit(template, order, new OrderMappingOverride());
        using var sr = new StreamReader(result.Content, Encoding.UTF8);
        var xml      = sr.ReadToEnd();

        xml.Should().Contain("<MfrPartId>REDACTED-ORDER-DATA</MfrPartId>");
    }

    // ── 4. A user's custom field must never be clobbered by a NEW built-in key ───

    [Fact]
    public void HeaderScopedCustomField_IsNotClobbered_ByANewlyReservedLineKey()
    {
        // A line bag starts from the header bag, so header-scoped custom fields land first and the
        // built-in line keys are written over them. Reserving 9 new line names therefore threatens
        // any customer whose header-scoped custom field is called e.g. "ContractNumber": their
        // authored value would silently become the line's contract number. New keys must yield.
        var order = FullyPopulatedOrder();
        var ov = new OrderMappingOverride
        {
            CustomFields = new List<CustomField>
            {
                new() { Key = "ContractNumber", Scope = "header", Value = "USER-AUTHORED" },
            },
            Output = new OutputMappingConfig
            {
                Lines = new() { ["col"] = new OutputFieldRule { OutputPath = "col", CanonicalField = "ContractNumber" } },
            },
        };

        SingleCsvValue(order, ov).Should().Be("USER-AUTHORED",
            "the customer authored this header custom field before 'ContractNumber' became a built-in "
            + "line key; widening the row must not rewrite their document");
    }

    [Fact]
    public void HeaderScopedCustomField_IsStillClobbered_ByAPreExistingLineKey()
    {
        // The other half of the same rule, pinned deliberately: for the keys that were ALREADY
        // built-in line names, today's precedence (line value wins) is preserved exactly. Changing
        // it would alter live documents, which "additive only" forbids. The asymmetry is the point —
        // old behaviour frozen, new keys made safe.
        var order = FullyPopulatedOrder();
        var ov = new OrderMappingOverride
        {
            CustomFields = new List<CustomField>
            {
                new() { Key = "Description", Scope = "header", Value = "USER-AUTHORED" },
            },
            Output = new OutputMappingConfig
            {
                Lines = new() { ["col"] = new OutputFieldRule { OutputPath = "col", CanonicalField = "Description" } },
            },
        };

        SingleCsvValue(order, ov).Should().Be("Barcode scanner",
            "'Description' was a built-in line key before WP-14, so its precedence is frozen");
    }

    [Fact]
    public void LineScopedCustomField_StillWins_OverEveryBuiltInKey()
    {
        // Line-scoped custom fields are applied AFTER the built-ins and must stay authoritative.
        var order = FullyPopulatedOrder();
        var ov = new OrderMappingOverride
        {
            CustomFields = new List<CustomField>
            {
                new()
                {
                    Key = "Unspsc", Scope = "line",
                    LineValues = new Dictionary<int, string> { [1] = "LINE-AUTHORED" },
                },
            },
            Output = new OutputMappingConfig
            {
                Lines = new() { ["col"] = new OutputFieldRule { OutputPath = "col", CanonicalField = "Unspsc" } },
            },
        };

        SingleCsvValue(order, ov).Should().Be("LINE-AUTHORED");
    }

    // ── 5. Culture: the new decimals must be invariant, under a comma-decimal locale ──

    [Fact]
    public void NewDecimalFields_UseInvariantCulture_EvenUnderACommaDecimalLocale()
    {
        // This repo has a real history of 10x/100x comma-decimal corruption. A culture test that does
        // not actually swap the culture proves nothing, so swap to de-DE (comma decimal separator)
        // and assert the emitted text still uses a POINT.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            // Guard the guard: if the culture did not actually take effect the assertions below are
            // vacuous, so prove the ambient culture really would have produced a comma.
            7.5m.ToString().Should().Be("7,5", "the de-DE culture must actually be in effect");

            var order = FullyPopulatedOrder();
            SingleCsvValue(order, BindOne("DiscountPercent", lineScope: true)).Should().Be("7.5");
            SingleCsvValue(order, BindOne("NetAmount",       lineScope: true)).Should().Be("92.5");
            SingleCsvValue(order, BindOne("TaxAmount",       lineScope: true)).Should().Be("22");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void NewNullableFields_RenderAsEmpty_NeverAsTheWordNull()
    {
        var bare = new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(), PoNumber = "PO-1", OrderDate = new DateOnly(2026, 1, 1), Currency = "EUR",
            Lines = new List<PurchaseOrderLineEntity>
            {
                new() { LineNumber = 1, BuyerItemCode = "B", SupplierItemCode = "S", Quantity = 1m, UnitPrice = 1m },
            },
        };

        foreach (var field in new[] { "ShipToCity", "Incoterms", "BuyerTaxId", "ContactName" })
            SingleCsvValue(bare, BindOne(field, lineScope: false)).Should().BeEmpty();

        foreach (var field in new[] { "TaxAmount", "DiscountPercent", "NetAmount", "ManufacturerPartNumber", "Unspsc" })
            SingleCsvValue(bare, BindOne(field, lineScope: true)).Should().BeEmpty();
    }

    // ── 6. GOLDEN: a supplier with no custom output renders byte-identically ─────

    /// <summary>
    /// The default (no-override) path must be untouched by the widening. Two oracles:
    /// this one pins the EXACT BYTES of the fixed CSV transform for a fully-populated order, so any
    /// leak of a new field into the default document changes a checked-in literal.
    /// </summary>
    [Fact]
    public async Task Golden_FixedCsvTransform_ForAFullyPopulatedOrder_IsUnchanged()
    {
        var order = FullyPopulatedOrder();
        order.Supplier = new Supplier { Id = Guid.NewGuid(), Name = "REDACTED-PARTY" };

        var result   = await new CsvTransformService().TransformAsync(order, OutputFormat.Csv, CancellationToken.None);
        using var sr = new StreamReader(result.Content, Encoding.UTF8);
        var csv      = sr.ReadToEnd().Replace("\r\n", "\n");

        const string golden =
            "PoNumber,OrderDate,SupplierName,LineNumber,SupplierItemCode,Description,Quantity,Unit,UnitPrice,LineTotal,Currency\n"
            + "PO-4711,2026-03-04,REDACTED-PARTY,1,S-1,Barcode scanner,2,PCS,50,100,EUR\n";

        csv.Should().Be(golden,
            "a supplier with NO custom output must render byte-identically to before WP-14 — the "
            + "fixed transforms read typed columns and must never see the widened row bag");
    }

    /// <summary>
    /// The second oracle: the same fixed transforms produce identical bytes whether or not the
    /// WP-14 columns are populated. This survives an intentional edit to the golden literal above.
    /// </summary>
    [Theory]
    [InlineData(OutputFormat.Csv)]
    [InlineData(OutputFormat.Xml)]
    public async Task Golden_FixedTransform_ByteIdentical_WhetherOrNotWp14FieldsArePopulated(OutputFormat format)
    {
        var without = FullyPopulatedOrder();
        StripWp14Fields(without);
        var with = FullyPopulatedOrder();

        foreach (var o in new[] { without, with })
            o.Supplier = new Supplier { Id = Guid.NewGuid(), Name = "REDACTED-PARTY" };

        ITransformService svc = format == OutputFormat.Csv
            ? new CsvTransformService()
            : new XmlTransformService();

        var a = await Bytes(svc, without, format);
        var b = await Bytes(svc, with, format);

        a.Should().Equal(b,
            "the fixed {0} transform reads typed columns only; populating the WP-14 columns must not "
            + "change one byte of the default document", format);
    }

    private static async Task<byte[]> Bytes(ITransformService svc, PurchaseOrderEntity order, OutputFormat format)
    {
        var result = await svc.TransformAsync(order, format, CancellationToken.None);
        using var ms = new MemoryStream();
        await result.Content.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static void StripWp14Fields(PurchaseOrderEntity o)
    {
        o.BuyerOrderRef = o.BuyerTaxId = o.ContactName = o.ContactEmail = o.ContactPhone =
            o.Incoterms = o.ShippingMethod = null;
        o.ShipToName = o.ShipToDeliverTo = o.ShipToStreet = o.ShipToCity =
            o.ShipToPostalCode = o.ShipToCountry = o.ShipToEmail = o.ShipToPhone = null;
        o.BillToName = o.BillToDeliverTo = o.BillToStreet = o.BillToCity =
            o.BillToPostalCode = o.BillToCountry = o.BillToEmail = o.BillToPhone = null;

        foreach (var l in o.Lines)
        {
            l.TaxAmount = l.DiscountPercent = l.NetAmount = null;
            l.ManufacturerPartNumber = l.ManufacturerName = l.CustomerPartNumber =
                l.Unspsc = l.Recipient = l.ContractNumber = null;
        }
    }
}
