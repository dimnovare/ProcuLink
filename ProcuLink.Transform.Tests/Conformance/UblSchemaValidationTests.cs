using System.Xml.Linq;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Conformance;

/// <summary>
/// Validates ProcuLink's OWN emitted UBL against the vendored OASIS schema, and pins the things a
/// presence-only checker could never see.
///
/// <para>The vendored artifact itself — completeness, digests, the closed resolver — is
/// <see cref="UblSchemaCatalogTests"/>.</para>
/// </summary>
public class UblSchemaValidationTests
{
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    // ── The negative control ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole packet rests on this. A schema that rejects our own output would not be a
    /// conformance feature, it would be an outage: <c>UblProfileChecker</c>'s verdict feeds
    /// <c>SupplierConnectionService</c>'s test pack, and a failing report blocks publishing a
    /// supplier connection revision.
    ///
    /// <para>Both shapes are checked, because they are different documents. The minimal order is
    /// what a bare CSV upload produces; the full one additionally carries the optional blocks the
    /// emitter null-gates — <c>cbc:EndpointID</c>, <c>cac:PostalAddress</c>, <c>cac:Contact</c> and
    /// <c>cac:Delivery</c> — and those are exactly the elements whose POSITION in UBL's ordered
    /// sequences the old presence-only checker had no opinion about.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmittedUblValidatesCleanAgainstTheOasisSchema(bool withOptionalBlocks)
    {
        var xml = await EmitAsync(BuildOrder(withOptionalBlocks));

        var result = UblSchemaValidator.Validate(xml);

        result.Valid.Should().BeTrue(
            "ProcuLink's emitted UBL must satisfy the OASIS UBL 2.1 Order schema. A failure here is a "
            + "real defect in our OUTPUT and outranks any schema tuning — never loosen the schema to "
            + "make it pass. Findings: " + string.Join(" | ", result.Findings.Select(f => f.ToString())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheProfileCheckerReportsTheSchemaCheckAsPassed(bool withOptionalBlocks)
    {
        var xml = await EmitAsync(BuildOrder(withOptionalBlocks));

        var report = new ConformanceService().Check(xml, OutputFormat.Ubl);

        report.OverallPass.Should().BeTrue(report.ToMarkdown());
        report.Checks.Should().ContainSingle(c => c.Code == "ubl.xsd")
            .Which.Passed.Should().BeTrue();
    }

    // ── The mutation the old checker structurally could not see ──────────────────────────

    /// <summary>
    /// UBL's content models are ordered <c>xsd:sequence</c>es. Transposing two mandatory elements
    /// leaves every one of them PRESENT, so every presence-and-cardinality check still passes — and
    /// the document is invalid UBL that a receiving system will reject.
    ///
    /// <para>This is the case that justifies vendoring a schema at all, so it is pinned in both
    /// directions: the named checks still pass (proving they are blind to it) and
    /// <c>ubl.xsd</c> fails (proving the schema is doing work they cannot).</para>
    /// </summary>
    [Fact]
    public async Task TransposedElementsFailTheSchemaWhileEveryPresenceCheckStillPasses()
    {
        var doc = XDocument.Parse(await EmitAsync(BuildOrder(withOptionalBlocks: false)));
        var root = doc.Root!;

        // cbc:ID must precede cbc:IssueDate in UBL 2.1's OrderType sequence. Swap them: nothing is
        // added, nothing is removed, every element is still present exactly once.
        var id = root.Element(Cbc + "ID")!;
        var issueDate = root.Element(Cbc + "IssueDate")!;
        id.Remove();
        issueDate.AddAfterSelf(id);

        var mutated = doc.ToString();
        mutated.Should().Contain("<cbc:ID>").And.Contain("<cbc:IssueDate>",
            "the mutation must transpose, not delete — otherwise it proves nothing about ORDER");

        var report = new ConformanceService().Check(mutated, OutputFormat.Ubl);

        report.Checks.Where(c => c.Code != "ubl.xsd").Should().OnlyContain(c => c.Passed,
            "every presence and cardinality check is blind to element ORDER — that blindness is the "
            + "reason the XSD had to be vendored");

        var schema = report.Checks.Single(c => c.Code == "ubl.xsd");
        schema.Passed.Should().BeFalse("the OASIS schema declares OrderType as an ordered sequence");
        schema.Severity.Should().Be(ConformanceSeverity.Error);
        report.OverallPass.Should().BeFalse();
    }

    /// <summary>
    /// The same blindness one level down, in <c>LineItemType</c>: <c>cac:Price</c> precedes
    /// <c>cac:Item</c>. Included because the line loop is where per-order data actually varies.
    /// </summary>
    [Fact]
    public async Task TransposedLineItemChildrenFailTheSchema()
    {
        var doc = XDocument.Parse(await EmitAsync(BuildOrder(withOptionalBlocks: false)));

        var lineItem = doc.Root!.Element(Cac + "OrderLine")!.Element(Cac + "LineItem")!;
        var price = lineItem.Element(Cac + "Price")!;
        var item = lineItem.Element(Cac + "Item")!;
        price.Remove();
        item.AddAfterSelf(price);

        var result = UblSchemaValidator.Validate(doc.ToString());

        result.Valid.Should().BeFalse();
        result.Findings.Should().NotBeEmpty();
    }

    // ── Silent-pass traps ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A document outside the UBL namespace has no schema to be judged against, and .NET reports
    /// that as a WARNING while validating nothing. A caller counting only errors reads it as a
    /// clean pass — a verdict about a document the validator never examined.
    /// </summary>
    [Fact]
    public void ADocumentInTheWrongNamespaceIsNotReportedAsValid()
    {
        const string notUbl = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Order xmlns="urn:example:not-ubl">
              <ID>PO-1</ID>
            </Order>
            """;

        var result = UblSchemaValidator.Validate(notUbl);

        result.Valid.Should().BeFalse(
            "nothing was validated, and 'nothing was checked' must never render as 'valid'");
        result.Findings.Should().ContainSingle()
            .Which.Message.Should().Contain("urn:oasis:names:specification:ubl:schema:xsd:Order-2");
    }

    [Fact]
    public void AMalformedDocumentIsNotReportedAsValid()
    {
        var result = UblSchemaValidator.Validate("<Order><unclosed>");

        result.Valid.Should().BeFalse();
        result.Findings.Should().NotBeEmpty();
    }

    /// <summary>
    /// A missing MANDATORY element must fail the schema too, not only the named presence check —
    /// otherwise <c>ubl.xsd</c> would be passing on documents it should reject and its agreement
    /// with the other checks would be a coincidence.
    /// </summary>
    [Fact]
    public async Task ARemovedMandatoryElementFailsTheSchema()
    {
        var doc = XDocument.Parse(await EmitAsync(BuildOrder(withOptionalBlocks: false)));
        doc.Root!.Element(Cbc + "ID")!.Remove();

        UblSchemaValidator.Validate(doc.ToString()).Valid.Should().BeFalse();
    }

    // ── Report plumbing ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The malformed-XML path builds its check list by hand, separately from the happy path. Two
    /// hand-maintained lists is how a code silently exists on one path and not the other, so the
    /// two are compared rather than trusted.
    /// </summary>
    [Fact]
    public async Task BothCodePathsReportTheSameSetOfCheckCodes()
    {
        var service = new ConformanceService();

        var wellFormed = service.Check(await EmitAsync(BuildOrder(false)), OutputFormat.Ubl);
        var malformed = service.Check("<Order><unclosed>", OutputFormat.Ubl);

        malformed.Checks.Select(c => c.Code).Distinct()
            .Should().BeEquivalentTo(wellFormed.Checks.Select(c => c.Code).Distinct(),
                "a check that exists only on one path is a check that silently disappears for the "
                + "documents most likely to need it");
    }

    /// <summary>
    /// The downloadable report leaves the product as evidence, so the schema verdict must be legible
    /// in it — and must not quietly re-acquire the overstated wording the Peppol claim was removed for.
    /// </summary>
    [Fact]
    public async Task TheMarkdownReportStatesTheSchemaVerdictWithoutOverclaiming()
    {
        var report = new ConformanceService().Check(await EmitAsync(BuildOrder(true)), OutputFormat.Ubl);
        var markdown = report.ToMarkdown();

        markdown.Should().Contain("ubl.xsd");
        markdown.Should().NotContainEquivalentOf("peppol");
        markdown.Should().NotContainEquivalentOf("certified");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────

    private static async Task<string> EmitAsync(PurchaseOrderEntity order)
    {
        var result = await new UblOrderTransformService()
            .TransformAsync(order, OutputFormat.Ubl, CancellationToken.None);

        using var reader = new StreamReader(result.Content);
        return await reader.ReadToEndAsync();
    }

    /// <param name="withOptionalBlocks">
    /// Populates the fields that make the emitter write its null-gated blocks: a GLN EdiCode
    /// (<c>cbc:EndpointID</c>), BillTo* (<c>cac:PostalAddress</c>), Contact* (<c>cac:Contact</c>)
    /// and ShipTo* (<c>cac:Delivery</c>).
    /// </param>
    private static PurchaseOrderEntity BuildOrder(bool withOptionalBlocks)
    {
        var order = new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            SupplierId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            PoNumber = "PO-UBL-XSD-001",
            OrderDate = new DateOnly(2026, 5, 28),
            Currency = "EUR",
            Status = "ready",
            BuyerName = "Buyer Placeholder",
            SupplierName = "Supplier Placeholder",
        };

        order.Lines = new List<PurchaseOrderLineEntity>
        {
            new()
            {
                LineNumber = 1,
                BuyerItemCode = "BUYER-001",
                SupplierItemCode = "SUP-ABC-001",
                Description = "Placeholder line item",
                Quantity = 10m,
                Unit = "EA",
                UnitPrice = 125.00m,
                NeedsReview = false,
                Confidence = 1.0f,
            },
            new()
            {
                LineNumber = 2,
                BuyerItemCode = "BUYER-002",
                SupplierItemCode = "SUP-ABC-002",
                Description = "Second placeholder line item",
                Quantity = 2.5m,
                Unit = "KGM",
                UnitPrice = 9.95m,
                NeedsReview = false,
                Confidence = 1.0f,
            },
        };

        if (!withOptionalBlocks)
        {
            return order;
        }

        // 7300010000001 carries a valid GS1 modulo-10 check digit, which is what makes the emitter
        // willing to state schemeID 0088.
        order.Supplier = new Supplier
        {
            Id = order.SupplierId!.Value,
            OrgId = order.OrgId,
            Name = "Supplier Placeholder",
            EdiCode = "7300010000001",
        };

        order.BillToStreet = "1 Example Street";
        order.BillToCity = "Tallinn";
        order.BillToPostalCode = "10111";
        order.BillToCountry = "EE";

        order.ContactName = "Ordering Contact";
        order.ContactPhone = "+372 0000000";
        order.ContactEmail = "orders@example.invalid";

        order.ShipToName = "Warehouse Placeholder";
        order.ShipToStreet = "2 Example Road";
        order.ShipToCity = "Tartu";
        order.ShipToPostalCode = "51004";
        order.ShipToCountry = "EE";

        return order;
    }
}
