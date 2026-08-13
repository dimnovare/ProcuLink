using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Output;

public class UblOrderTransformServiceTests
{
    // ── UBL 2.1 namespaces (must match transformer) ──────────────────────────
    private static readonly XNamespace UblOrder = "urn:oasis:names:specification:ubl:schema:xsd:Order-2";
    private static readonly XNamespace Cac      = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc      = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private const string PeppolBisCustomizationId = "urn:fdc:peppol.eu:poacc:trns:order:3";

    private static PurchaseOrderEntity BuildOrder(
        string poNumber   = "PO-UBL-001",
        string currency   = "EUR",
        DateOnly? date    = null,
        Guid? orgId       = null,
        Guid? supplierId  = null,
        IEnumerable<PurchaseOrderLineEntity>? lines = null)
    {
        var order = new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId      ?? Guid.Parse("00000000-0000-0000-0000-000000000001"),
            SupplierId = supplierId ?? Guid.Parse("00000000-0000-0000-0000-000000000002"),
            PoNumber   = poNumber,
            OrderDate  = date ?? new DateOnly(2026, 5, 28),
            Currency   = currency,
            Status     = "ready",
        };

        order.Lines = (lines ?? new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 1,
                BuyerItemCode    = "BUYER-001",
                SupplierItemCode = "SUP-ABC-001",
                Description      = "Widget Type A",
                Quantity         = 10m,
                Unit             = "EA",
                UnitPrice        = 125.00m,
                NeedsReview      = false,
                Confidence       = 1.0f,
            }
        }).ToList();

        return order;
    }

    // ── Required tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task TransformAsync_GeneratesValidUblDocument_WithCorrectNamespaces()
    {
        var svc   = new UblOrderTransformService();
        var order = BuildOrder();

        var result = await svc.TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);

        result.ContentType.Should().Be("application/xml");
        result.FileExtension.Should().Be(".xml");

        var xml = await ReadContentAsString(result);
        var doc = XDocument.Parse(xml); // must be well-formed

        var root = doc.Root!;
        root.Name.LocalName.Should().Be("Order");
        root.Name.NamespaceName.Should().Be(UblOrder.NamespaceName,
            "the default namespace must be the UBL 2.1 Order-2 namespace");

        // cac/cbc namespaces must be declared and resolvable
        var firstCbc = doc.Descendants(Cbc + "UBLVersionID").FirstOrDefault();
        firstCbc.Should().NotBeNull("cbc namespace must be wired and a UBLVersionID element must exist");
        firstCbc!.Value.Should().Be("2.1");

        // SellerSupplierParty (cac) must be present
        var sellerParty = doc.Descendants(Cac + "SellerSupplierParty").FirstOrDefault();
        sellerParty.Should().NotBeNull("cac namespace must be wired and SellerSupplierParty must exist");
    }

    /// <summary>
    /// This test used to be <c>TransformAsync_IncludesPeppolBisCustomizationId</c>, and it asserted
    /// the opposite: that the emitted order carried <c>cbc:CustomizationID</c> =
    /// <c>urn:fdc:peppol.eu:poacc:trns:order:3</c> and <c>cbc:ProfileID</c> =
    /// <c>urn:fdc:peppol.eu:poacc:bis:order_only:3</c>. It was pinning the defect — those two
    /// elements declare the document a Peppol BIS Order-only 3.0 document to a receiving access
    /// point, and ProcuLink does not produce or verify BIS-conformant output. It is inverted here
    /// rather than deleted, so the change of intent is on the record.
    ///
    /// The full reasoning, the UBL 2.1 schema cardinalities, and the class-level guard live in
    /// <see cref="UblOrderDeclaresNoPeppolProfileTests"/>.
    /// </summary>
    [Fact]
    public async Task TransformAsync_DeclaresNoPeppolBisProfile()
    {
        var svc   = new UblOrderTransformService();
        var order = BuildOrder();

        var result = await svc.TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);
        var xml    = await ReadContentAsString(result);
        var doc    = XDocument.Parse(xml);

        doc.Descendants(Cbc + "CustomizationID").Should().BeEmpty(
            "cbc:CustomizationID is minOccurs=\"0\" in UBL 2.1 and ProcuLink applies no customization");
        doc.Descendants(Cbc + "ProfileID").Should().BeEmpty();
        xml.Should().NotContain("peppol");
    }

    [Fact]
    public async Task TransformAsync_ThrowsValidationException_WhenUnresolvedLinesPresent()
    {
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber       = 1,
                BuyerItemCode    = "B-001",
                SupplierItemCode = null, // unresolved
                Quantity         = 1m,
                UnitPrice        = 10m,
                NeedsReview      = true,
                Confidence       = 0.5f,
            }
        };

        var svc   = new UblOrderTransformService();
        var order = BuildOrder(lines: lines);

        var act = async () => await svc.TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);
        var ex  = await act.Should().ThrowAsync<TransformValidationException>();
        ex.Which.UnresolvedLineNumbers.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public async Task TransformAsync_RoundTripsThroughUblOrderParser()
    {
        var svc   = new UblOrderTransformService();
        var order = BuildOrder(
            poNumber: "PO-ROUNDTRIP-7",
            lines: new[]
            {
                new PurchaseOrderLineEntity
                {
                    LineNumber       = 1,
                    BuyerItemCode    = "B-001",
                    SupplierItemCode = "S-ROUND-001",
                    Description      = "Round-trip widget",
                    Quantity         = 4m,
                    Unit             = "EA",
                    UnitPrice        = 50.00m,
                    NeedsReview      = false,
                    Confidence       = 1.0f,
                },
                new PurchaseOrderLineEntity
                {
                    LineNumber       = 2,
                    BuyerItemCode    = "B-002",
                    SupplierItemCode = "S-ROUND-002",
                    Description      = "Round-trip widget 2",
                    Quantity         = 1m,
                    Unit             = "EA",
                    UnitPrice        = 12.50m,
                    NeedsReview      = false,
                    Confidence       = 1.0f,
                }
            });

        // Emit
        var result = await svc.TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);

        // Feed the emitted bytes straight into the inbound parser
        result.Content.Position = 0;
        var parser = new UblOrderParser();
        var parsed = await parser.ParseAsync(result.Content, CancellationToken.None);

        parsed.PoNumber.Should().Be("PO-ROUNDTRIP-7");

        // First line: BuyerItemCode resolution falls back to seller's item id when
        // no BuyersItemIdentification element is present, so the parser surfaces
        // the supplier item code we emitted.
        parsed.Lines.Should().HaveCount(2);
        var firstParsedLine = parsed.Lines.OrderBy(l => l.LineNumber).First();
        firstParsedLine.BuyerItemCode.Should().Be("S-ROUND-001",
            "the round-trip exposes the SellersItemIdentification ID as the only available identifier");
        firstParsedLine.Quantity.Should().Be(4m);
        firstParsedLine.UnitPrice.Should().Be(50.00m);
        firstParsedLine.Description.Should().Be("Round-trip widget");
    }

    // ── Address + contact + buyer-name (mirrors cXML; reuses canonical fields) ──

    /// <summary>
    /// Exemple-shaped, fully-populated address order: ship-to + bill-to + contact + buyer name.
    /// </summary>
    private static PurchaseOrderEntity BuildAddressedOrder()
    {
        var order = BuildOrder();
        order.BuyerName        = "EXEMPLE Achats";
        order.ContactName      = "Testperson Alex";
        order.ContactEmail     = "alex.testperson@buyer.example.com";
        order.ContactPhone     = "33100000000";
        order.ShipToName       = "Usine EXEMPLE Sud-3";
        order.ShipToStreet     = "12 rue des Essais B12-3 (CTX_0000)";
        order.ShipToCity       = "VILLE-EXEMPLE";
        order.ShipToPostalCode = "99040";
        order.ShipToCountry    = "FRANCE";
        order.BillToName       = "EXEMPLE Comptabilite Fournisseurs";
        order.BillToStreet     = "Place des Essais Nord";
        order.BillToCity       = "VILLE-EXEMPLE";
        order.BillToPostalCode = "99000";
        order.BillToCountry    = "FRANCE";
        return order;
    }

    [Fact]
    public async Task TransformAsync_NoAddressData_EmitsNoAddressBlocks()
    {
        // BYTE-SAFETY LOCK: an order with no ShipTo*/BillTo*/Contact* and no BuyerName must emit
        // NO PostalAddress / Contact / Delivery — existing UBL suppliers are byte-unaffected. The
        // buyer party falls back to the "ProcuLink Buyer" placeholder (BuyerName is blank here).
        var svc = new UblOrderTransformService();
        var xml = await ReadContentAsString(
            await svc.TransformAsync(BuildOrder(), OutputFormat.Ubl, CancellationToken.None));

        xml.Should().NotContain("PostalAddress");
        xml.Should().NotContain("cac:Contact");
        xml.Should().NotContain("<cac:Contact");
        xml.Should().NotContain("Delivery");
        xml.Should().Contain("ProcuLink Buyer", "buyer name falls back to the placeholder when blank");
    }

    [Fact]
    public async Task TransformAsync_WithBuyerName_ReplacesPlaceholder()
    {
        var svc   = new UblOrderTransformService();
        var order = BuildOrder();
        order.BuyerName = "Exemplar Stahl GmbH";

        var doc = XDocument.Parse(await ReadContentAsString(
            await svc.TransformAsync(order, OutputFormat.Ubl, CancellationToken.None)));

        var buyerParty = doc.Descendants(Cac + "BuyerCustomerParty").Single();
        buyerParty.Descendants(Cbc + "Name").First().Value.Should().Be("Exemplar Stahl GmbH");
    }

    [Fact]
    public async Task TransformAsync_WithAddresses_EmitsBuyerPostalAndContact_FromBillTo()
    {
        var svc = new UblOrderTransformService();
        var doc = XDocument.Parse(await ReadContentAsString(
            await svc.TransformAsync(BuildAddressedOrder(), OutputFormat.Ubl, CancellationToken.None)));

        var buyerParty = doc.Descendants(Cac + "BuyerCustomerParty").Single().Element(Cac + "Party")!;

        // BuyerCustomerParty/Party/PostalAddress fed from BillTo*.
        var postal = buyerParty.Element(Cac + "PostalAddress")!;
        postal.Should().NotBeNull();
        postal.Element(Cbc + "StreetName")!.Value.Should().Be("Place des Essais Nord");
        postal.Element(Cbc + "CityName")!.Value.Should().Be("VILLE-EXEMPLE");
        postal.Element(Cbc + "PostalZone")!.Value.Should().Be("99000");
        postal.Element(Cac + "Country")!.Element(Cbc + "IdentificationCode")!.Value.Should().Be("FRANCE");

        // BuyerCustomerParty/Party/Contact fed from Contact*.
        var contact = buyerParty.Element(Cac + "Contact")!;
        contact.Should().NotBeNull();
        contact.Element(Cbc + "Name")!.Value.Should().Be("Testperson Alex");
        contact.Element(Cbc + "Telephone")!.Value.Should().Be("33100000000");
        contact.Element(Cbc + "ElectronicMail")!.Value.Should().Be("alex.testperson@buyer.example.com");
    }

    [Fact]
    public async Task TransformAsync_WithShipTo_EmitsDeliveryBlock()
    {
        var svc = new UblOrderTransformService();
        var doc = XDocument.Parse(await ReadContentAsString(
            await svc.TransformAsync(BuildAddressedOrder(), OutputFormat.Ubl, CancellationToken.None)));

        var delivery = doc.Descendants(Cac + "Delivery").Single();

        // Delivery/DeliveryLocation/Address fed from ShipTo*.
        var address = delivery.Element(Cac + "DeliveryLocation")!.Element(Cac + "Address")!;
        address.Element(Cbc + "StreetName")!.Value.Should().Be("12 rue des Essais B12-3 (CTX_0000)");
        address.Element(Cbc + "CityName")!.Value.Should().Be("VILLE-EXEMPLE");
        address.Element(Cbc + "PostalZone")!.Value.Should().Be("99040");
        address.Element(Cac + "Country")!.Element(Cbc + "IdentificationCode")!.Value.Should().Be("FRANCE");

        // Delivery/DeliveryParty/PartyName/Name fed from ShipToName.
        delivery.Element(Cac + "DeliveryParty")!.Element(Cac + "PartyName")!.Element(Cbc + "Name")!
            .Value.Should().Be("Usine EXEMPLE Sud-3");
    }

    // ── Required-field validation (output-format hardening) ────────────────────

    [Fact]
    public async Task TransformAsync_ZeroUnitPrice_NowTransforms()
    {
        // A €0 line is a legitimately-free line (founder-approved): UBL transforms it, not held.
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-001", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 1m, Unit = "EA", UnitPrice = 0m,
                NeedsReview = false, Confidence = 1.0f,
            }
        };

        var svc = new UblOrderTransformService();
        var result = await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.Ubl, CancellationToken.None);

        result.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task TransformAsync_NegativeUnitPrice_IsFlaggedForReview()
    {
        // A negative unit price is financially impossible. UBL still holds it for review.
        var lines = new[]
        {
            new PurchaseOrderLineEntity
            {
                LineNumber = 1, BuyerItemCode = "B-001", SupplierItemCode = "SUP-1",
                Description = "Widget", Quantity = 1m, Unit = "EA", UnitPrice = -5m,
                NeedsReview = false, Confidence = 1.0f,
            }
        };

        var svc = new UblOrderTransformService();
        var act = async () => await svc.TransformAsync(BuildOrder(lines: lines), OutputFormat.Ubl, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<TransformValidationException>();
        ex.Which.Problems.Should().Contain(p => p.Kind == LineProblemKind.MissingOrZeroPrice);
    }

    // ── Bonus: confirm CanTransform routes correctly ─────────────────────────

    [Fact]
    public void CanTransform_ReturnsTrueForUblOnly()
    {
        var svc = new UblOrderTransformService();
        svc.CanTransform(OutputFormat.Ubl).Should().BeTrue();
        svc.CanTransform(OutputFormat.CXml).Should().BeFalse();
        svc.CanTransform(OutputFormat.Xml).Should().BeFalse();
        svc.CanTransform(OutputFormat.Csv).Should().BeFalse();
        svc.CanTransform(OutputFormat.Json).Should().BeFalse();
    }

    // ── Unrouted order (null SupplierId) — preview must stay valid UBL ──────────

    /// <summary>
    /// An order parked <c>unrouted</c> (routing Phase 0) carries a NULL SupplierId. It can never
    /// DELIVER while unrouted, but the Order Workshop live preview can render it in UBL. The
    /// supplier name is the supplier id placeholder; a null <c>Guid?.ToString()</c> is <c>""</c>,
    /// which would emit an empty <c>SellerSupplierParty/Party/PartyName/Name</c> — invalid Peppol
    /// BIS 3.0 (PartyName/Name is required). Coalescing to the zero GUID keeps it non-empty.
    /// </summary>
    [Fact]
    public async Task TransformAsync_NullSupplier_SellerNameFallsBackToZeroGuid_NotEmpty()
    {
        var order = BuildOrder();
        order.SupplierId = null;

        var result = await new UblOrderTransformService().TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);
        var doc    = XDocument.Parse(await ReadContentAsString(result));

        var sellerName = doc.Descendants(Cac + "SellerSupplierParty").Single()
            .Element(Cac + "Party")!.Element(Cac + "PartyName")!.Element(Cbc + "Name")!;
        sellerName.Value.Should().Be(Guid.Empty.ToString());
        sellerName.Value.Should().NotBeNullOrEmpty("an empty PartyName/Name is invalid Peppol BIS 3.0");
    }

    // ── Seller name is the supplier's NAME, not its primary key ────────────────

    /// <summary>
    /// The document goes to the supplier, and it used to tell them their own name was
    /// <c>3f2b91c4-…</c> — the row id. Every current ingest path writes the denormalized
    /// <c>SupplierName</c>, so it is the first source.
    /// </summary>
    [Fact]
    public async Task TransformAsync_UsesDenormalisedSupplierName_NotTheGuid()
    {
        var order = BuildOrder();
        order.SupplierName = "Exemplar Supplies OÜ";

        var sellerName = await ReadSellerName(order);

        sellerName.Should().Be("Exemplar Supplies OÜ");
        sellerName.Should().NotBe(order.SupplierId!.Value.ToString(),
            "the supplier reads this field as its own name");
        Guid.TryParse(sellerName, out _).Should().BeFalse("a GUID is a key, not a name");
    }

    /// <summary>
    /// Second source: the loaded navigation. Reached only when the denormalized column is blank,
    /// which is why the column is read first — it does not depend on the caller having included
    /// the <c>Supplier</c> navigation.
    /// </summary>
    [Fact]
    public async Task TransformAsync_FallsBackToLoadedSupplierNavigation()
    {
        var order = BuildOrder();
        order.SupplierName = "   ";
        order.Supplier = new Supplier { Id = order.SupplierId!.Value, Name = "Baltic Fasteners AS" };

        (await ReadSellerName(order)).Should().Be("Baltic Fasteners AS");
    }

    /// <summary>
    /// Last resort only. A routed order with no name anywhere still may not emit an empty
    /// PartyName/Name, so the id remains the floor — but it is now the third answer, not the first.
    /// </summary>
    [Fact]
    public async Task TransformAsync_RoutedOrderWithNoNameAnywhere_StillEmitsTheId()
    {
        var order = BuildOrder();
        order.SupplierName = null;

        (await ReadSellerName(order)).Should().Be(order.SupplierId!.Value.ToString());
    }

    private static async Task<string> ReadSellerName(PurchaseOrderEntity order)
    {
        var result = await new UblOrderTransformService().TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);
        var doc    = XDocument.Parse(await ReadContentAsString(result));
        return doc.Descendants(Cac + "SellerSupplierParty").Single()
            .Element(Cac + "Party")!.Element(Cac + "PartyName")!.Element(Cbc + "Name")!.Value;
    }

    // ── Party electronic address (cbc:EndpointID) ─────────────────────────────
    //
    // The ONLY supported source is Supplier.EdiCode, and only when it is provably a GS1
    // Global Location Number. schemeID is an EAS / ISO 6523 code, not free text, and GLN is
    // the one identifier whose scheme can be asserted from the value alone. A VAT number's
    // EAS code is country-dependent and no country is stored next to it, so VAT and the
    // registration number are deliberately refused rather than guessed at.

    /// <summary>
    /// A GS1 Global Location Number: 13 digits whose last digit is the GS1 modulo-10 check
    /// digit. Constructed and check-digit-verified for this suite; it identifies no real
    /// company. Weighted right-to-left (check digit ×1, then ×3, ×1, …) the digits sum to 20,
    /// which is ≡ 0 (mod 10) — the definition of a valid GS1 check digit.
    /// </summary>
    private const string ValidGln      = "7300010000001";
    private const string GlnSchemeId   = "0088";

    private static PurchaseOrderEntity BuildOrderWithSupplier(
        string? ediCode            = null,
        string? vatNumber          = null,
        string? registrationNumber = null)
    {
        var order = BuildOrder();
        order.SupplierName = "Exemplar Supplies OÜ";
        order.Supplier = new Supplier
        {
            Id                 = order.SupplierId!.Value,
            Name               = "Exemplar Supplies OÜ",
            EdiCode            = ediCode,
            VatNumber          = vatNumber,
            RegistrationNumber = registrationNumber,
        };
        return order;
    }

    private static async Task<(XDocument Doc, string Xml)> Render(PurchaseOrderEntity order)
    {
        var result = await new UblOrderTransformService().TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);
        var xml    = await ReadContentAsString(result);
        return (XDocument.Parse(xml), xml);
    }

    private static XElement SellerParty(XDocument doc) =>
        doc.Descendants(Cac + "SellerSupplierParty").Single().Element(Cac + "Party")!;

    [Fact]
    public async Task TransformAsync_SupplierEdiCodeIsGln_EmitsEndpointIdWithGs1Scheme()
    {
        var (doc, _) = await Render(BuildOrderWithSupplier(ediCode: ValidGln));

        var endpoint = SellerParty(doc).Element(Cbc + "EndpointID");
        endpoint.Should().NotBeNull("a check-digit-valid GLN is the one identifier whose EAS scheme is unambiguous");
        endpoint!.Value.Should().Be(ValidGln);
        endpoint.Attribute("schemeID").Should().NotBeNull("schemeID carries the EAS code and is what makes the value routable");
        endpoint.Attribute("schemeID")!.Value.Should().Be(GlnSchemeId, "EAS / ISO 6523 ICD 0088 is the GS1 GLN scheme");
    }

    [Fact]
    public async Task TransformAsync_GlnIsSurroundedByWhitespace_IsTrimmedNotRejected()
    {
        var (doc, _) = await Render(BuildOrderWithSupplier(ediCode: $"  {ValidGln}\t"));

        SellerParty(doc).Element(Cbc + "EndpointID")!.Value.Should().Be(ValidGln);
    }

    /// <summary>
    /// The Peppol participant form writes the scheme into the value ("0088:7300010000001").
    /// The scheme is then stated by the operator rather than guessed by us, so it is honoured —
    /// but only for 0088, and only when the payload is still a check-digit-valid GLN.
    /// </summary>
    [Fact]
    public async Task TransformAsync_PeppolParticipantForm_IsUnwrappedToTheBareGln()
    {
        var (doc, _) = await Render(BuildOrderWithSupplier(ediCode: $"{GlnSchemeId}:{ValidGln}"));

        var endpoint = SellerParty(doc).Element(Cbc + "EndpointID")!;
        endpoint.Value.Should().Be(ValidGln, "the scheme belongs in schemeID, not doubled into the value");
        endpoint.Attribute("schemeID")!.Value.Should().Be(GlnSchemeId);
    }

    /// <summary>
    /// THE OMISSION LOCK. Every supplier on the live production org today has all four identity
    /// columns null. Such an order must produce a document with NO cbc:EndpointID at all — not an
    /// empty element, not a placeholder, not a GUID. An absent endpoint is an honest gap; a
    /// fabricated one routes to the wrong party while claiming to be identified.
    /// </summary>
    [Fact]
    public async Task TransformAsync_SupplierWithNoIdentifiers_EmitsNoEndpointIdAnywhere()
    {
        var (doc, xml) = await Render(BuildOrderWithSupplier());

        doc.Descendants().Where(e => e.Name.LocalName == "EndpointID").Should().BeEmpty(
            "an unidentified party must carry no electronic address at all");
        xml.Should().NotContain("EndpointID",
            "not even as an empty element — a blank endpoint is a claim we cannot support");
    }

    /// <summary>
    /// A VAT number's EAS code is country-dependent and no country is stored beside it, and the
    /// registration number has the same problem. Neither may stand in for an endpoint, however
    /// tempting the presence of a value is.
    /// </summary>
    [Fact]
    public async Task TransformAsync_SupplierHasVatAndRegistrationButNoGln_EmitsNoEndpointId()
    {
        var (doc, xml) = await Render(BuildOrderWithSupplier(
            vatNumber: "EE100594103", registrationNumber: "10059410"));

        SellerParty(doc).Element(Cbc + "EndpointID").Should().BeNull(
            "a VAT number cannot be assigned an EAS scheme from stored data, so it is refused");
        xml.Should().NotContain("EndpointID");
    }

    [Theory]
    // 13 digits but the check digit is wrong — this exact value is the repo's own Peppol
    // options fixture, and it is NOT a valid GLN.
    [InlineData("1234567890123")]
    [InlineData("730001000000")]        // 12 digits
    [InlineData("73000100000012")]      // 14 digits
    [InlineData("EE100594103")]         // VAT-shaped
    [InlineData("730001000000X")]       // 13 chars, not all digits
    [InlineData("7300 0100 00001")]     // spaced — reformatting it would be guessing
    [InlineData("9930:DE123456789")]    // Peppol form, non-GLN scheme
    [InlineData("0088:1234567890123")]  // Peppol form, right scheme, bad payload
    [InlineData("0037:12345678")]       // Peppol form, Finnish OVT scheme
    [InlineData("0088:")]               // Peppol form, empty payload
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task TransformAsync_EdiCodeIsNotProvablyAGln_EmitsNoEndpointId(string? ediCode)
    {
        var (doc, xml) = await Render(BuildOrderWithSupplier(ediCode: ediCode));

        SellerParty(doc).Element(Cbc + "EndpointID").Should().BeNull();
        xml.Should().NotContain("EndpointID");
    }

    /// <summary>
    /// The nastiest shape, and the one a length-and-checksum test alone does NOT catch: a payload
    /// that really is a check-digit-valid 13-digit number, registered under a scheme that is not
    /// GLN. Finnish OVT (0037) and Norwegian organisation numbers (9908) are both plausible
    /// contents of this field. Unwrapping the prefix and relabelling the payload 0088 would emit a
    /// document whose endpoint is attributed to the wrong scheme — routable, and routed wrong.
    /// The prefix is honoured only when the operator stated 0088.
    /// </summary>
    [Theory]
    [InlineData("0037:7300010000001")]
    [InlineData("9908:7300010000001")]
    public async Task TransformAsync_ValidGlnPayloadUnderANonGlnScheme_IsRefused(string ediCode)
    {
        var (doc, _) = await Render(BuildOrderWithSupplier(ediCode: ediCode));

        SellerParty(doc).Element(Cbc + "EndpointID").Should().BeNull(
            "the payload passes the GS1 check digit, but the operator registered it under another "
            + "scheme — relabelling it 0088 would be asserting a registration the supplier does not have");
    }

    /// <summary>
    /// Proves the check digit is genuinely computed rather than the value merely being
    /// length-checked. Under GS1 modulo-10 (weights 1 and 3) no single-digit change can ever
    /// preserve the checksum, so every mutation of a valid GLN must be refused.
    /// </summary>
    [Theory]
    [InlineData("6300010000001")]  // first digit
    [InlineData("7400010000001")]  // second digit
    [InlineData("7300010000091")]  // eleventh digit
    [InlineData("7300010000002")]  // check digit itself
    public async Task TransformAsync_SingleDigitMutationOfAValidGln_IsRefused(string mutated)
    {
        mutated.Should().HaveLength(13).And.NotBe(ValidGln);

        var (doc, _) = await Render(BuildOrderWithSupplier(ediCode: mutated));

        SellerParty(doc).Element(Cbc + "EndpointID").Should().BeNull(
            "a mutated GLN fails the GS1 check digit, and a wrong endpoint is worse than none");
    }

    /// <summary>
    /// UBL 2.1 PartyType is an ordered xsd:sequence — cbc:EndpointID sits at position 5, ahead of
    /// cac:PartyIdentification (7), cac:PartyName (8), cac:PostalAddress (10) and cac:Contact (14).
    /// Emitting it after PartyName would be schema-invalid.
    /// </summary>
    [Fact]
    public async Task TransformAsync_EndpointId_PrecedesPartyNameInTheSequence()
    {
        var (doc, _) = await Render(BuildOrderWithSupplier(ediCode: ValidGln));

        var childNames = SellerParty(doc).Elements().Select(e => e.Name.LocalName).ToList();

        childNames.IndexOf("EndpointID").Should().Be(0);
        childNames.IndexOf("EndpointID").Should().BeLessThan(childNames.IndexOf("PartyName"));
    }

    /// <summary>
    /// The buyer side gets NOTHING, deliberately. The only stored buyer identifier is
    /// <c>BuyerTaxId</c> — free text lifted off the document by the LLM extractor, with no country
    /// and no scheme code stored beside it, so no EAS code can be derived for it. The Buyer entity
    /// and the Organisation carry no commerce identifier at all.
    /// </summary>
    [Fact]
    public async Task TransformAsync_BuyerTaxIdPresent_StillEmitsNoBuyerEndpointId()
    {
        var order = BuildOrderWithSupplier(ediCode: ValidGln);
        order.BuyerName  = "Acme Buyer AS";
        order.BuyerTaxId = "EE100123456";

        var (doc, _) = await Render(order);

        var buyerParty = doc.Descendants(Cac + "BuyerCustomerParty").Single().Element(Cac + "Party")!;
        buyerParty.Element(Cbc + "EndpointID").Should().BeNull(
            "a tax id has no derivable EAS scheme, so the buyer endpoint stays an honest gap");

        // …and the supplier's endpoint is unaffected by the buyer having an identifier.
        SellerParty(doc).Element(Cbc + "EndpointID")!.Value.Should().Be(ValidGln);
    }

    [Fact]
    public async Task TransformAsync_SupplierNavigationNotLoaded_EmitsNoEndpointId()
    {
        var order = BuildOrder();       // SupplierId set, Supplier navigation null
        order.SupplierName = "Exemplar Supplies OÜ";

        var (doc, xml) = await Render(order);

        SellerParty(doc).Element(Cbc + "EndpointID").Should().BeNull();
        xml.Should().NotContain("EndpointID");
    }

    [Fact]
    public async Task TransformAsync_UnroutedOrderWithNullSupplierId_EmitsNoEndpointId()
    {
        var order = BuildOrderWithSupplier(ediCode: ValidGln);
        order.SupplierId = null;

        var (doc, _) = await Render(order);

        SellerParty(doc).Element(Cbc + "EndpointID").Should().BeNull(
            "an unrouted order has no supplier to be identified as");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> ReadContentAsString(TransformResult result)
    {
        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        return await reader.ReadToEndAsync();
    }
}
