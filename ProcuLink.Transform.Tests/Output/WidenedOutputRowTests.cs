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

    /// <summary>
    /// The single data cell of a one-column CSV (line 2, after the header row).
    ///
    /// <para>Normalises CRLF first and does NOT drop empty entries. Both matter: an empty cell makes
    /// the data row an EMPTY line, which <c>RemoveEmptyEntries</c> would discard — on Windows the
    /// stray <c>\r</c> kept it alive, so the test passed locally and failed only on Linux CI. Splitting
    /// a normalised string keeps index 1 meaning "the data row" on both.</para>
    /// </summary>
    private static string SingleCsvValue(PurchaseOrderEntity order, OrderMappingOverride @override)
    {
        var lines = RenderCsv(order, @override).Replace("\r\n", "\n").Split('\n');
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
    /// Oracle 1 — a supplier with NO custom output. Pins the EXACT BYTES of the fixed CSV transform
    /// for a fully-populated order.
    ///
    /// <para>Note what this literal actually shows: the fixed transform ALREADY emitted the ship-to,
    /// bill-to and contact columns natively. That is precisely why the gap was invisible — the
    /// DEFAULT document carried the address, and only a supplier who needed a CUSTOM layout found
    /// the fields unreachable. The literal was verified byte-for-byte against the pre-WP-14 tree by
    /// stashing the two changed production files and re-running this test.</para>
    /// </summary>
    [Fact]
    public async Task Golden_FixedCsvTransform_NoCustomOutput_IsByteIdenticalToPreWp14()
    {
        var order = FullyPopulatedOrder();
        order.Supplier = new Supplier { Id = Guid.NewGuid(), Name = "REDACTED-PARTY" };

        var result   = await new CsvTransformService().TransformAsync(order, OutputFormat.Csv, CancellationToken.None);
        using var sr = new StreamReader(result.Content, Encoding.UTF8);
        var csv      = sr.ReadToEnd().Replace("\r\n", "\n");

        const string golden =
            "PoNumber,OrderDate,Currency,BuyerName,SupplierItemCode,Description,Quantity,Unit,UnitPrice,LineTotal,"
            + "ShipToName,ShipToStreet,ShipToCity,ShipToPostalCode,ShipToCountry,"
            + "BillToName,BillToStreet,BillToCity,BillToPostalCode,BillToCountry,"
            + "ContactName,ContactEmail,ContactPhone\n"
            + "PO-4711,2026-03-04,EUR,Acme Buyer AS,S-1,Barcode scanner,2,PCS,50,100,"
            + "Acme Warehouse,Sadama tee 12,Tallinn,10111,EE,"
            + "Acme Finance,Pärnu mnt 5,Tartu,51004,EE,"
            + "Mari Tamm,mari.tamm@acme.example,+372 555 0101\n";

        csv.Should().Be(golden,
            "a supplier with NO custom output must render byte-identically to before WP-14");
    }

    /// <summary>
    /// Oracle 2 — the one that actually exercises the CHANGED code. The fixed transforms above do
    /// not call <see cref="MappedTransformService"/> at all, so they cannot regress from this work;
    /// an EXISTING override can. This pins the exact bytes of the native override path for an
    /// override that binds only PRE-WP-14 names — the shape every current customer has. Widening
    /// the row bag must not perturb one byte of it.
    /// </summary>
    [Fact]
    public void Golden_NativeOverride_BindingOnlyPreWp14Names_IsByteIdentical()
    {
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = new()
                {
                    ["PO"]   = new OutputFieldRule { OutputPath = "PO",   CanonicalField = "PoNumber" },
                    ["Date"] = new OutputFieldRule { OutputPath = "Date", CanonicalField = "OrderDate" },
                    ["Cur"]  = new OutputFieldRule { OutputPath = "Cur",  CanonicalField = "Currency" },
                    ["Sup"]  = new OutputFieldRule { OutputPath = "Sup",  CanonicalField = "SupplierName" },
                    ["Tot"]  = new OutputFieldRule { OutputPath = "Tot",  CanonicalField = "GrandTotal" },
                },
                Lines = new()
                {
                    ["Ln"]   = new OutputFieldRule { OutputPath = "Ln",   CanonicalField = "LineNumber" },
                    ["Code"] = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" },
                    ["Desc"] = new OutputFieldRule { OutputPath = "Desc", CanonicalField = "Description" },
                    ["Qty"]  = new OutputFieldRule { OutputPath = "Qty",  CanonicalField = "Quantity" },
                    ["Prc"]  = new OutputFieldRule { OutputPath = "Prc",  CanonicalField = "UnitPrice" },
                    ["Amt"]  = new OutputFieldRule { OutputPath = "Amt",  CanonicalField = "LineTotal" },
                    ["Vat"]  = new OutputFieldRule { OutputPath = "Vat",  CanonicalField = "TaxRate" },
                    ["Del"]  = new OutputFieldRule { OutputPath = "Del",  CanonicalField = "DeliveryDate" },
                },
            },
        };

        var csv = RenderCsv(FullyPopulatedOrder(), ov).Replace("\r\n", "\n");

        const string golden =
            "PO,Date,Cur,Sup,Tot,Ln,Code,Desc,Qty,Prc,Amt,Vat,Del\n"
            + "REDACTED-PARTY";

        csv.Should().Be(golden,
            "an override that binds only pre-WP-14 names must render byte-identically after the "
            + "widening — 23 new header keys and 9 new line keys are INERT unless a rule names them");
    }

    /// <summary>
    /// Oracle 3 — the same guarantee for the tree emitter, whose leaves resolve through the same
    /// widened bag. XML, so the structured family is covered too.
    /// </summary>
    [Fact]
    public void Golden_TreeOutput_BindingOnlyPreWp14Names_IsByteIdentical()
    {
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
                        Name     = "Number",
                        NodeType = OutputNodeType.Field,
                        Rule     = new OutputFieldRule { OutputPath = "Number", CanonicalField = "PoNumber" },
                    },
                    new()
                    {
                        Name       = "Lines",
                        NodeType   = OutputNodeType.Array,
                        Collection = "lines",
                        Children = new List<OutputNode>
                        {
                            new()
                            {
                                Name     = "Line",
                                NodeType = OutputNodeType.Object,
                                Children = new List<OutputNode>
                                {
                                    new()
                                    {
                                        Name     = "Code",
                                        NodeType = OutputNodeType.Field,
                                        Rule     = new OutputFieldRule { OutputPath = "Code", CanonicalField = "SupplierItemCode" },
                                    },
                                    new()
                                    {
                                        Name     = "Qty",
                                        NodeType = OutputNodeType.Field,
                                        Rule     = new OutputFieldRule { OutputPath = "Qty", CanonicalField = "Quantity" },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        var result   = new OutputTemplateEmitter().Emit(template, FullyPopulatedOrder(), new OrderMappingOverride());
        using var sr = new StreamReader(result.Content, Encoding.UTF8);
        var xml      = sr.ReadToEnd().Replace("\r\n", "\n");

        const string golden =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<Order>\n"
            + "  <Number>PO-4711</Number>\n"
            + "  <Lines>\n"
            + "    <Line>\n"
            + "      <Code>S-1</Code>\n"
            + "      <Qty>2</Qty>\n"
            + "    </Line>\n"
            + "  </Lines>\n"
            + "</Order>";

        xml.Should().Be(golden,
            "a tree output binding only pre-WP-14 names must render byte-identically after the widening");
    }
}
