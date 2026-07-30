using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// WP-14 — the canonical output ROW must expose every business field on
/// <see cref="PurchaseOrderEntity"/> and <see cref="PurchaseOrderLineEntity"/>.
///
/// <para>The row bags built by <c>MappedTransformService.BuildHeaderRow</c> /
/// <c>BuildLineRow</c> are the ONLY vocabulary a custom output mapping, a designed output tree
/// (<see cref="OutputTemplateEmitter"/>) or a per-field Scriban expression can bind. Anything absent
/// from the bag is unreachable — it does not matter that the column is parsed, persisted and
/// correct. Before this suite the bag carried 10 header + 11 line names, so a physical purchase
/// order could not emit a DELIVERY ADDRESS through any custom output path.</para>
///
/// <para>The real deliverable is <see cref="HeaderRow_ExposesEveryBusinessFieldOnTheEntity"/> /
/// <see cref="LineRow_ExposesEveryBusinessFieldOnTheEntity"/>: they REFLECT over the entities and
/// fail when a property has neither a row key nor an explicit, named exclusion. A new column added
/// to either entity therefore cannot silently re-open this gap — the author must either expose it
/// or write down why it stays internal.</para>
/// </summary>
public class CanonicalRowCompletenessTests
{
    // ── Explicit exclusion registry ──────────────────────────────────────────────
    // Every entity property that is deliberately NOT bindable in a supplier document, each named
    // with the reason it stays internal. This list is the "written exclusion" the completeness
    // test demands: adding a column to the entity without touching either this list or the row
    // builder is a TEST FAILURE, by design.

    private static readonly IReadOnlyDictionary<string, string> ExcludedHeaderProperties =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Id"]                            = "surrogate key",
            ["OrgId"]                         = "tenancy scope — never leaves the system",
            ["SupplierId"]                    = "internal FK; the supplier's name ships as SupplierName",
            ["Status"]                        = "internal pipeline state, not a document field",
            ["SourceFileKey"]                 = "internal blob-storage key",
            ["CanonicalJson"]                 = "the raw parse tree, not a scalar field (bind src:: tokens instead)",
            ["CreatedAt"]                     = "audit column",
            ["UpdatedAt"]                     = "audit column",
            ["IsSample"]                      = "internal onboarding flag",
            ["RequeueCount"]                  = "internal retry counter",
            ["DeliveryRequeueCount"]          = "internal retry counter",
            ["SchemaFingerprintHash"]         = "internal column-layout hash",
            ["InboundSenderDomain"]           = "restricted-retention datum — must never reach a supplier document",
            ["InboundSenderDomainCapturedAt"] = "retention clock for InboundSenderDomain",
            ["DeliveryDueAt"]                 = "internal SLA timer",
            ["SlaBreached"]                   = "internal SLA flag",
            ["HeldFromStatus"]                = "internal billing-hold bookkeeping",
            ["SourceFilePurgedAt"]            = "internal blob-retention bookkeeping",
            ["ConnectionRevisionId"]          = "internal reproducibility pin",
            ["Organisation"]                  = "navigation property",
            ["Supplier"]                      = "navigation property",
            ["Lines"]                         = "navigation property",
            ["OutboundArtifacts"]             = "navigation property",
            ["DeliveryAttempts"]              = "navigation property",
            ["Parties"]                       = "navigation property",
            ["SourceCapture"]                 = "navigation property",
        };

    private static readonly IReadOnlyDictionary<string, string> ExcludedLineProperties =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Id"]                          = "surrogate key",
            ["OrderId"]                     = "internal FK",
            ["Confidence"]                  = "internal parse-quality metric",
            ["NeedsReview"]                 = "internal review flag",
            ["ReviewReason"]                = "internal review annotation",
            ["AiSuggestedSupplierItemCode"] = "unaccepted AI suggestion — never delivered",
            ["AiSuggestionConfidence"]      = "unaccepted AI suggestion metadata",
            ["AiSuggestionReason"]          = "unaccepted AI suggestion metadata",
            ["AiSuggestionProvenance"]      = "unaccepted AI suggestion metadata",
            ["Order"]                       = "navigation property",
        };

    // ── Fixtures ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// An order with EVERY business column populated with a distinguishable value, so a per-field
    /// assertion proves the row carries that column's own value rather than a neighbour's.
    /// </summary>
    private static PurchaseOrderEntity FullyPopulatedOrder()
    {
        var orderId = Guid.NewGuid();
        var order = new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-14",
            BuyerName  = "Buyer Ltd",
            OrderDate  = new DateOnly(2026, 5, 1),
            Currency   = "EUR",
            Status     = "ready",

            SupplierName          = "Supplier AS",
            SubTotal              = 100m,
            TaxTotal             = 25m,
            GrandTotal            = 125m,
            PaymentTerms          = "NET30",
            DocumentType          = "purchase_order",
            RequestedDeliveryDate = new DateOnly(2026, 6, 15),

            ContactName    = "Contact Person",
            ContactEmail   = "contact@buyer.example",
            ContactPhone   = "+47 11 11 11 11",
            Incoterms      = "DAP",
            ShippingMethod = "Road freight",
            BuyerOrderRef  = "REQ-77",
            BuyerTaxId     = "NO999888777MVA",

            ShipToName       = "Ship To Warehouse",
            ShipToDeliverTo  = "Goods In, Bay 4",
            ShipToStreet     = "Leveringsgata 12",
            ShipToCity       = "Trondheim",
            ShipToPostalCode = "7010",
            ShipToCountry    = "NO",
            ShipToEmail      = "warehouse@buyer.example",
            ShipToPhone      = "+47 22 22 22 22",

            BillToName       = "Bill To Finance",
            BillToDeliverTo  = "Accounts Payable",
            BillToStreet     = "Fakturagata 3",
            BillToCity       = "Oslo",
            BillToPostalCode = "0150",
            BillToCountry    = "NO",
            BillToEmail      = "ap@buyer.example",
            BillToPhone      = "+47 33 33 33 33",
        };

        order.Lines.Add(FullyPopulatedLine(orderId));
        return order;
    }

    private static PurchaseOrderLineEntity FullyPopulatedLine(Guid orderId) => new()
    {
        Id               = Guid.NewGuid(),
        OrderId          = orderId,
        LineNumber       = 1,
        BuyerItemCode    = "B-1",
        SupplierItemCode = "SUP-1",
        Description      = "Barcode scanner",
        Quantity         = 4m,
        Unit             = "EA",
        UnitPrice        = 12.5m,
        NeedsReview      = false,
        Confidence       = 1.0f,

        LineAmount   = 50m,
        TaxRate      = 0.25m,
        TaxAmount    = 12.5m,
        DeliveryDate = new DateOnly(2026, 6, 20),

        ManufacturerPartNumber = "REDACTED-ORDER-DATA",
        ManufacturerName       = "REDACTED-PARTY",
        CustomerPartNumber     = "CUST-4711",
        DiscountPercent        = 7.5m,
        Unspsc                 = "43211711",
        Recipient              = "Receiving Dept",
        ContractNumber         = "CTR-2026-01",
        NetAmount              = 46.25m,
    };

    private static Dictionary<string, string> HeaderRow() =>
        MappedTransformService.BuildHeaderRow(FullyPopulatedOrder(), new OrderMappingOverride());

    private static Dictionary<string, string> LineRow()
    {
        var order = FullyPopulatedOrder();
        return MappedTransformService.BuildLineRow(order, new OrderMappingOverride(), order.Lines[0]);
    }

    private static IEnumerable<PropertyInfo> BusinessProperties(Type entity, IReadOnlyDictionary<string, string> excluded) =>
        entity.GetProperties(BindingFlags.Public | BindingFlags.Instance)
              .Where(p => !excluded.ContainsKey(p.Name))
              .OrderBy(p => p.Name, StringComparer.Ordinal);

    // ── THE completeness guards ──────────────────────────────────────────────────

    [Fact]
    public void HeaderRow_ExposesEveryBusinessFieldOnTheEntity()
    {
        var row = HeaderRow();

        var unreachable = BusinessProperties(typeof(PurchaseOrderEntity), ExcludedHeaderProperties)
            .Where(p => !row.ContainsKey(p.Name))
            .Select(p => p.Name)
            .ToList();

        unreachable.Should().BeEmpty(
            "every business column on PurchaseOrderEntity must be bindable from a custom output " +
            "mapping or a designed output tree. Either add the property to BuildHeaderRow or add it " +
            "to ExcludedHeaderProperties with the reason it stays internal. Unreachable: {0}",
            string.Join(", ", unreachable));
    }

    [Fact]
    public void LineRow_ExposesEveryBusinessFieldOnTheEntity()
    {
        var row = LineRow();

        var unreachable = BusinessProperties(typeof(PurchaseOrderLineEntity), ExcludedLineProperties)
            .Where(p => !row.ContainsKey(p.Name))
            .Select(p => p.Name)
            .ToList();

        unreachable.Should().BeEmpty(
            "every business column on PurchaseOrderLineEntity must be bindable from a custom output " +
            "mapping or a designed output tree. Either add the property to BuildLineRow or add it to " +
            "ExcludedLineProperties with the reason it stays internal. Unreachable: {0}",
            string.Join(", ", unreachable));
    }

    /// <summary>
    /// The exclusion registry must describe THIS entity, not a historical one. A property renamed or
    /// deleted leaves a stale exclusion behind, which would silently keep a real business field
    /// excluded under its old name.
    /// </summary>
    [Fact]
    public void ExclusionRegistry_NamesOnlyPropertiesThatStillExist()
    {
        var headerNames = typeof(PurchaseOrderEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var lineNames = typeof(PurchaseOrderLineEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        ExcludedHeaderProperties.Keys.Where(k => !headerNames.Contains(k)).Should()
            .BeEmpty("a stale header exclusion hides nothing and misleads the next author");
        ExcludedLineProperties.Keys.Where(k => !lineNames.Contains(k)).Should()
            .BeEmpty("a stale line exclusion hides nothing and misleads the next author");
    }

    // ── One assertion per newly exposed HEADER field ─────────────────────────────

    [Theory]
    [InlineData("DocumentType",     "purchase_order")]
    [InlineData("ContactName",      "Contact Person")]
    [InlineData("ContactEmail",     "contact@buyer.example")]
    [InlineData("ContactPhone",     "+47 11 11 11 11")]
    [InlineData("Incoterms",        "DAP")]
    [InlineData("ShippingMethod",   "Road freight")]
    [InlineData("BuyerOrderRef",    "REQ-77")]
    [InlineData("BuyerTaxId",       "NO999888777MVA")]
    [InlineData("ShipToName",       "Ship To Warehouse")]
    [InlineData("ShipToDeliverTo",  "Goods In, Bay 4")]
    [InlineData("ShipToStreet",     "Leveringsgata 12")]
    [InlineData("ShipToCity",       "Trondheim")]
    [InlineData("ShipToPostalCode", "7010")]
    [InlineData("ShipToCountry",    "NO")]
    [InlineData("ShipToEmail",      "warehouse@buyer.example")]
    [InlineData("ShipToPhone",      "+47 22 22 22 22")]
    [InlineData("BillToName",       "Bill To Finance")]
    [InlineData("BillToDeliverTo",  "Accounts Payable")]
    [InlineData("BillToStreet",     "Fakturagata 3")]
    [InlineData("BillToCity",       "Oslo")]
    [InlineData("BillToPostalCode", "0150")]
    [InlineData("BillToCountry",    "NO")]
    [InlineData("BillToEmail",      "ap@buyer.example")]
    [InlineData("BillToPhone",      "+47 33 33 33 33")]
    public void HeaderRow_ExposesNewlyWidenedField(string key, string expected)
    {
        HeaderRow().Should().ContainKey(key).WhoseValue.Should().Be(expected);
    }

    // ── One assertion per newly exposed LINE field ───────────────────────────────

    [Theory]
    [InlineData("TaxAmount",              "12.5")]
    [InlineData("ManufacturerPartNumber", "REDACTED-ORDER-DATA")]
    [InlineData("ManufacturerName",       "REDACTED-PARTY")]
    [InlineData("CustomerPartNumber",     "CUST-4711")]
    [InlineData("DiscountPercent",        "7.5")]
    [InlineData("Unspsc",                 "43211711")]
    [InlineData("Recipient",              "Receiving Dept")]
    [InlineData("ContractNumber",         "CTR-2026-01")]
    [InlineData("NetAmount",              "46.25")]
    public void LineRow_ExposesNewlyWidenedField(string key, string expected)
    {
        LineRow().Should().ContainKey(key).WhoseValue.Should().Be(expected);
    }

    // ── Formatting discipline: null ⇒ string.Empty, never "null" / "0" ───────────

    [Theory]
    [InlineData("DocumentType")]
    [InlineData("ContactName")]
    [InlineData("Incoterms")]
    [InlineData("BuyerTaxId")]
    [InlineData("ShipToCity")]
    [InlineData("BillToCity")]
    public void HeaderRow_NullBusinessField_IsEmptyString(string key)
    {
        var order = FullyPopulatedOrder();
        typeof(PurchaseOrderEntity).GetProperty(key)!.SetValue(order, null);

        MappedTransformService.BuildHeaderRow(order, new OrderMappingOverride())[key]
            .Should().Be(string.Empty, "an absent header field emits nothing, never a literal or a 0");
    }

    [Theory]
    [InlineData("TaxAmount")]
    [InlineData("ManufacturerPartNumber")]
    [InlineData("ManufacturerName")]
    [InlineData("DiscountPercent")]
    [InlineData("NetAmount")]
    [InlineData("Recipient")]
    public void LineRow_NullBusinessField_IsEmptyString(string key)
    {
        var order = FullyPopulatedOrder();
        typeof(PurchaseOrderLineEntity).GetProperty(key)!.SetValue(order.Lines[0], null);

        MappedTransformService.BuildLineRow(order, new OrderMappingOverride(), order.Lines[0])[key]
            .Should().Be(string.Empty, "an absent line field emits nothing, never a literal or a 0");
    }

    /// <summary>
    /// Numbers use the invariant culture, exactly like the pre-existing UnitPrice/TaxRate keys —
    /// a decimal comma would corrupt a CSV column and break a receiving ERP.
    /// </summary>
    [Fact]
    public void LineRow_NewDecimalFields_UseInvariantCulture()
    {
        var order = FullyPopulatedOrder();
        order.Lines[0].DiscountPercent = 1234.56m;
        order.Lines[0].NetAmount       = 9876.54m;
        order.Lines[0].TaxAmount       = 0.05m;

        var row = MappedTransformService.BuildLineRow(order, new OrderMappingOverride(), order.Lines[0]);

        row["DiscountPercent"].Should().Be(1234.56m.ToString(CultureInfo.InvariantCulture)).And.Be("1234.56");
        row["NetAmount"].Should().Be("9876.54");
        row["TaxAmount"].Should().Be("0.05");
    }

    // ── Acceptance criteria: the widened names reach real output ────────────────

    /// <summary>
    /// AC: "a CSV output binding ShipToCity emits the parsed city." This is the packet's headline —
    /// before the widening a physical PO could not carry a delivery address through any custom output.
    /// </summary>
    [Fact]
    public void Csv_OutputBindingShipToCity_EmitsTheParsedCity()
    {
        var order = FullyPopulatedOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header =
                {
                    ["po"]   = new OutputFieldRule { OutputPath = "OrderRef",     CanonicalField = "PoNumber" },
                    ["city"] = new OutputFieldRule { OutputPath = "DeliveryCity", CanonicalField = "ShipToCity" },
                },
                Lines = { ["sku"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" } },
            },
        };

        var csv = ReadAll(new MappedTransformService().Build(order, ov, OutputFormat.Csv));

        csv.Should().Contain("DeliveryCity", "the bound output path becomes a CSV column");
        csv.Should().Contain("Trondheim", "the parsed ship-to city must reach the supplier document");
    }

    [Fact]
    public void Csv_OutputBindingWholeShipToBlock_EmitsEveryAddressLine()
    {
        var order = FullyPopulatedOrder();
        var ov = new OrderMappingOverride { Output = new OutputMappingConfig() };
        foreach (var f in new[] { "ShipToName", "ShipToStreet", "ShipToCity", "ShipToPostalCode", "ShipToCountry" })
            ov.Output!.Header[f] = new OutputFieldRule { OutputPath = f, CanonicalField = f };
        ov.Output!.Lines["sku"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" };

        var csv = ReadAll(new MappedTransformService().Build(order, ov, OutputFormat.Csv));

        csv.Should().Contain("Ship To Warehouse")
           .And.Contain("Leveringsgata 12")
           .And.Contain("Trondheim")
           .And.Contain("7010")
           .And.Contain("NO");
    }

    /// <summary>AC: "a tree binding ManufacturerPartNumber emits it."</summary>
    [Fact]
    public void Tree_BindingManufacturerPartNumber_EmitsIt()
    {
        var template = new OutputNodeTemplate
        {
            Format = OutputFormat.Json,
            Root = OutputNode.Obj("root",
                OutputNode.FieldOf("shipToCity",
                    new OutputFieldRule { OutputPath = "shipToCity", CanonicalField = "ShipToCity" }),
                OutputNode.Arr("items",
                    OutputNode.Obj("item",
                        OutputNode.FieldOf("mpn",
                            new OutputFieldRule { OutputPath = "mpn", CanonicalField = "ManufacturerPartNumber" }),
                        OutputNode.FieldOf("manufacturer",
                            new OutputFieldRule { OutputPath = "manufacturer", CanonicalField = "ManufacturerName" })))),
        };

        var json = ReadAll(new OutputTemplateEmitter()
            .Emit(template, FullyPopulatedOrder(), new OrderMappingOverride()));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("shipToCity").GetString().Should().Be("Trondheim");
        var item = root.GetProperty("items")[0];
        item.GetProperty("mpn").GetString().Should().Be("REDACTED-ORDER-DATA");
        item.GetProperty("manufacturer").GetString().Should().Be("REDACTED-PARTY");
    }

    /// <summary>
    /// Widening the bag must stay INERT: an override that names none of the new keys produces exactly
    /// the bytes it produced before. The bag is a vocabulary, not an emitter.
    /// </summary>
    [Fact]
    public void Csv_UnboundNewFields_DoNotLeakIntoOutput()
    {
        var order = FullyPopulatedOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = { ["po"] = new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = "PoNumber" } },
                Lines  = { ["sku"] = new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" } },
            },
        };

        var csv = ReadAll(new MappedTransformService().Build(order, ov, OutputFormat.Csv));

        csv.Trim().Should().Be("OrderRef,ItemCode\nPO-14,SUP-1".ReplaceLineEndings("\n").Trim(),
            "no unbound key may appear in the document");
        csv.Should().NotContain("Trondheim").And.NotContain("REDACTED-PARTY");
    }

    private static string ReadAll(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
