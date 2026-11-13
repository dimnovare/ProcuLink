using System.Text;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// WP-14 — the fixed CSV transform for an order with every business field populated. The widened
/// order row bag these cases were first written for went with the order render chain (plan 13b,
/// 2026-11-13); what stays is the fixed transform's own golden bytes.
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

            SupplierName          = "Exemplar Supplies OÜ",
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
                ManufacturerPartNumber = "LTQ2500-BK-BTK1",
                ManufacturerName       = "Litware",
                CustomerPartNumber     = "CUST-777",
                Unspsc                 = "43211711",
                Recipient              = "Loading bay B",
                ContractNumber         = "FRAME-2026-11",
            },
        };

        return order;
    }

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
        order.Supplier = new Supplier { Id = Guid.NewGuid(), Name = "Exemplar Supplies OÜ" };

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
            // The one deliberate drift since WP-14: a leading + is a formula prefix (CSV injection),
            // so the phone carries a ' — CsvTransformService.NeutraliseFormula.
            + "Mari Tamm,mari.tamm@acme.example,'+372 555 0101\n";

        csv.Should().Be(golden,
            "a supplier with NO custom output must render byte-identically to before WP-14");
    }
}
