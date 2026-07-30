using System.Reflection;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// WP-14 — the STRUCTURED delivery path must not drop the widened fields.
///
/// <para>The trap: <see cref="EffectiveEntityResolver"/> renders XML/cXML/UBL/X12 from a CLONE of
/// the order, and <c>Clone</c> copies a HAND-WRITTEN list of columns. Widening the row bag without
/// widening the clone would leave the CSV/JSON native path looking perfect while the structured
/// path shipped blanks — the row bag for a line is built from the CLONE's line, so a field the
/// clone forgot resolves to an empty string with no error anywhere.</para>
///
/// <para>The guard is reflection-driven rather than a fixed list, so it automatically covers any
/// field added to <see cref="CanonicalRowFields"/> later. That is deliberate: a hand-written list
/// here would be a third place to forget, which is the exact failure mode being fixed.</para>
/// </summary>
public class EffectiveEntityResolverCarriesWidenedFieldsTests
{
    private static PurchaseOrderEntity PopulatedOrder()
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

            BuyerOrderRef    = "REQ-9001",
            BuyerTaxId       = "EE100123456",
            ContactName      = "Mari Tamm",
            ContactEmail     = "mari.tamm@acme.example",
            ContactPhone     = "+372 555 0101",
            Incoterms        = "DAP",
            ShippingMethod   = "Road freight",
            ShipToName       = "Acme Warehouse",
            ShipToDeliverTo  = "Gate 4",
            ShipToStreet     = "Sadama tee 12",
            ShipToCity       = "Tallinn",
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
                LineAmount       = 100m,
                TaxRate          = 0.22m,
                DeliveryDate     = new DateOnly(2026, 4, 2),

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

    [Fact]
    public void Clone_CarriesEveryBindableHeaderField()
    {
        var source = PopulatedOrder();
        var clone  = EffectiveEntityResolver.Resolve(source, new OrderMappingOverride());

        var dropped = CanonicalRowFields.Header
            .Select(name => typeof(PurchaseOrderEntity).GetProperty(name, BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p is not null)
            .Where(p => !Equals(p!.GetValue(source), p.GetValue(clone)))
            .Select(p => p!.Name)
            .ToList();

        dropped.Should().BeEmpty(
            "the structured delivery path (XML/cXML/UBL/X12) renders from this clone, so any bindable "
            + "field the clone drops arrives EMPTY at a real supplier with no error raised: {0}",
            string.Join(", ", dropped));
    }

    [Fact]
    public void Clone_CarriesEveryBindableLineField()
    {
        var source = PopulatedOrder();
        var clone  = EffectiveEntityResolver.Resolve(source, new OrderMappingOverride());

        var sourceLine = source.Lines.Single();
        var cloneLine  = clone.Lines.Single();

        var dropped = CanonicalRowFields.Line
            .Where(name => !CanonicalRowFields.DerivedLineKeys.Contains(name))
            .Select(name => typeof(PurchaseOrderLineEntity).GetProperty(name, BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p is not null)
            .Where(p => !Equals(p!.GetValue(sourceLine), p.GetValue(cloneLine)))
            .Select(p => p!.Name)
            .ToList();

        dropped.Should().BeEmpty(
            "every bindable LINE field must survive the clone, or the structured path emits blanks "
            + "while the CSV/JSON path looks correct: {0}",
            string.Join(", ", dropped));
    }

    [Fact]
    public void StructuredPathRowBag_SeesTheWidenedLineFields()
    {
        // End-to-end proof of the same defect at the layer that matters: the row bag the structured
        // path resolves rules against is built from the CLONE's line, not the original.
        var source = PopulatedOrder();
        var clone  = EffectiveEntityResolver.Resolve(source, new OrderMappingOverride());

        var row = MappedTransformService.BuildLineRow(
            clone, new OrderMappingOverride(), clone.Lines.Single());

        row["ManufacturerPartNumber"].Should().Be("REDACTED-ORDER-DATA");
        row["Unspsc"].Should().Be("43211711");
        row["ContractNumber"].Should().Be("FRAME-2026-11");
        row["Incoterms"].Should().Be("DAP");
        row["ShipToCity"].Should().Be("Tallinn");
    }

    [Fact]
    public void AnOutputColumnNamedAfterANewCanonicalField_DoesNotHijackTheTypedColumn()
    {
        // Deliberate scope decision, pinned so it cannot drift: EffectiveEntityResolver's write-back
        // set (the rules that overwrite TYPED columns for fixed-shape formats) is NOT widened by
        // WP-14. Widening it would be non-additive — `ResolveTargetField` matches a rule by its
        // OutputPath, so an existing customer whose cXML output column happens to be called
        // "ShipToCity" would suddenly have that rule rewrite the real ship-to column and change the
        // document they receive today. Binding a new name emits it; it never rewrites the entity.
        var source = PopulatedOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = new()
                {
                    ["ShipToCity"] = new OutputFieldRule { OutputPath = "ShipToCity", FixedValue = "HIJACKED" },
                },
            },
        };

        var clone = EffectiveEntityResolver.Resolve(source, ov);

        clone.ShipToCity.Should().Be("Tallinn",
            "an output rule named after a newly bindable field must not overwrite the typed column "
            + "that the fixed structured transforms render from");
    }

    // ── The write-back set is a CONTRACT the frontend picker mirrors ─────────────
    //
    // For a structured format (cXML/UBL/X12/XML) an output rule can only change the document by
    // rewriting a canonical value this resolver recognises; every other rule is silently skipped.
    // The frontend greys those names out in the source picker rather than offering a binding that
    // does nothing — so the exact membership of these two sets is now a cross-repo contract, and
    // `src/lib/api/canonicalFields.test.ts` pins the same lists on that side.
    //
    // Pinned BEHAVIOURALLY (does a rule actually rewrite the column?) rather than by reflecting the
    // private HashSets: a reflection test would pass if the sets were right but ResolveTargetField
    // stopped consulting them.

    public static TheoryData<string, string> RecognisedHeaderFields() => new()
    {
        { "PoNumber", "REWRITTEN" },
        { "Currency", "USD" },
        { "SupplierName", "REWRITTEN" },
        { "BuyerName", "REWRITTEN" },
    };

    [Theory]
    [MemberData(nameof(RecognisedHeaderFields))]
    public void ARecognisedHeaderField_DoesRewriteTheTypedColumn(string field, string value)
    {
        // Non-vacuity for the test above: the write-back set must not be EMPTY, or
        // "does not hijack" would pass for every name and mean nothing.
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = new() { [field] = new OutputFieldRule { OutputPath = field, FixedValue = value } },
            },
        };

        var clone = EffectiveEntityResolver.Resolve(PopulatedOrder(), ov);

        var actual = field switch
        {
            "PoNumber"     => clone.PoNumber,
            "Currency"     => clone.Currency,
            "SupplierName" => clone.SupplierName,
            "BuyerName"    => clone.BuyerName,
            _              => null,
        };
        actual.Should().Be(value,
            "'{0}' is in the structured write-back set, so a rule targeting it must reach the "
            + "delivered document — this is what the frontend picker keeps offering for cXML/UBL/X12",
            field);
    }

    [Theory]
    [InlineData("LineNumber", "7")]
    [InlineData("BuyerItemCode", "REWRITTEN")]
    [InlineData("SupplierItemCode", "REWRITTEN")]
    [InlineData("Description", "REWRITTEN")]
    [InlineData("Unit", "BOX")]
    public void ARecognisedLineField_DoesRewriteTheTypedColumn(string field, string value)
    {
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Lines = new() { [field] = new OutputFieldRule { OutputPath = field, FixedValue = value } },
            },
        };

        var line = EffectiveEntityResolver.Resolve(PopulatedOrder(), ov).Lines.Single();

        var actual = field switch
        {
            "LineNumber"       => line.LineNumber.ToString(),
            "BuyerItemCode"    => line.BuyerItemCode,
            "SupplierItemCode" => line.SupplierItemCode,
            "Description"      => line.Description,
            "Unit"             => line.Unit,
            _                  => null,
        };
        actual.Should().Be(value, "'{0}' is in the structured write-back LINE set", field);
    }

    [Theory]
    [InlineData("Incoterms")]
    [InlineData("BuyerTaxId")]
    [InlineData("ShipToName")]
    [InlineData("ContactEmail")]
    [InlineData("BillToCity")]
    public void AWidenedHeaderField_IsNotInTheWriteBackSet(string field)
    {
        // The other side of the cross-repo contract: these are BINDABLE (they emit on CSV/JSON) but
        // NOT write-back, so a structured-format rule naming them is skipped — which is exactly what
        // the frontend now greys out instead of pretending it works.
        var source = PopulatedOrder();
        var ov = new OrderMappingOverride
        {
            Output = new OutputMappingConfig
            {
                Header = new() { [field] = new OutputFieldRule { OutputPath = field, FixedValue = "HIJACKED" } },
            },
        };

        var clone = EffectiveEntityResolver.Resolve(source, ov);

        var actual = field switch
        {
            "Incoterms"    => clone.Incoterms,
            "BuyerTaxId"   => clone.BuyerTaxId,
            "ShipToName"   => clone.ShipToName,
            "ContactEmail" => clone.ContactEmail,
            "BillToCity"   => clone.BillToCity,
            _              => null,
        };
        actual.Should().NotBe("HIJACKED",
            "'{0}' must stay outside the write-back set, or an existing customer's output column of "
            + "that name starts rewriting their real data", field);
    }
}
