using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Phase-2b: per-order mapper enrichment endpoints
/// (mapping-suggestions / validation / catalog-hints / ai-suggestion-decisions).
/// Verifies the response DTOs serialize to the exact frontend shapes, org isolation
/// (a foreign order → 404), the honest empty-when-nothing path, EU comma-decimal
/// variance correctness, validation severity → status mapping, and that the decision
/// recorder writes a durable row. InMemory DbContext; real services reused.
/// </summary>
public class MapperEnrichmentControllerTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static MapperEnrichmentController BuildController(
        ProcuLinkDbContext db,
        Guid orgId,
        ISupplierAcceptanceService acceptance,
        IPoMappingService poMappings,
        IFieldMappingSuggester suggester,
        IAiSuggestionDecisionService decisions)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        return new MapperEnrichmentController(
            db,
            tenant.Object,
            acceptance,
            poMappings,
            suggester,
            decisions,
            NullLogger<MapperEnrichmentController>.Instance);
    }

    /// <summary>Builds a controller wired with sensible empty-returning mock collaborators.</summary>
    private static MapperEnrichmentController BuildWithEmptyCollaborators(ProcuLinkDbContext db, Guid orgId)
    {
        var acceptance = new Mock<ISupplierAcceptanceService>();
        acceptance
            .Setup(s => s.ValidateOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<ProcuLink.Core.Services.OutputFormat?>()))
            .ReturnsAsync(new List<OrderValidationResult>());

        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var suggester = new Mock<IFieldMappingSuggester>();
        suggester
            .Setup(s => s.SuggestFieldMappingsAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FieldMappingSuggestion>());

        var decisions = new AiSuggestionDecisionService(db);

        return BuildController(db, orgId, acceptance.Object, poMappings.Object, suggester.Object, decisions);
    }

    private static async Task<(Guid orgId, Guid supplierId, Guid orderId)> SeedOrderAsync(
        ProcuLinkDbContext db, string status = "pending_review")
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        db.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Slug = $"org-{orgId:N}".Substring(0, 12) });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", Code = "ACME" });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-1",
            Currency = "EUR",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId);
    }

    private static void AddLine(
        ProcuLinkDbContext db, Guid orderId, int lineNumber,
        string? supplierItemCode, decimal unitPrice, string? mpn = null)
    {
        db.PurchaseOrderLines.Add(new PurchaseOrderLineEntity
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            LineNumber = lineNumber,
            BuyerItemCode = $"B{lineNumber}",
            SupplierItemCode = supplierItemCode,
            ManufacturerPartNumber = mpn,
            Quantity = 1,
            UnitPrice = unitPrice,
        });
    }

    private static void AddCatalogProduct(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId,
        string code, decimal? price, string? currency = "EUR", string? barcode = null)
    {
        db.SupplierProducts.Add(new SupplierProduct
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Code = code,
            Price = price,
            Currency = currency,
            Barcode = barcode,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    // ── mapping-suggestions ───────────────────────────────────────────────────

    [Fact]
    public async Task MappingSuggestions_ForeignOrder_Returns404()
    {
        using var db = NewDb();
        var (orgId, _, orderId) = await SeedOrderAsync(db);

        // A DIFFERENT org asks for this order's suggestions → must not leak.
        var ctrl = BuildWithEmptyCollaborators(db, Guid.NewGuid());

        var result = await ctrl.GetMappingSuggestions(orderId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task MappingSuggestions_NoSavedMappingNoHeuristic_ReturnsEmpty()
    {
        using var db = NewDb();
        var (orgId, _, orderId) = await SeedOrderAsync(db);
        var ctrl = BuildWithEmptyCollaborators(db, orgId);

        var result = await ctrl.GetMappingSuggestions(orderId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<MappingSuggestionDto>>()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public async Task MappingSuggestions_SavedSupplierMapping_YieldsHighConfidenceRawSuggestion()
    {
        using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedOrderAsync(db);

        var acceptance = new Mock<ISupplierAcceptanceService>();
        var suggester = new Mock<IFieldMappingSuggester>();
        suggester
            .Setup(s => s.SuggestFieldMappingsAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FieldMappingSuggestion>());

        // The supplier's saved PO mapping is a LEARNED source→canonical map → high confidence.
        var config = new PoMappingConfig
        {
            Header = new Dictionary<string, FieldMappingEntry>
            {
                ["PoNumber"] = new() { ExternalField = "Bestellnummer" },
            },
            Lines = new Dictionary<string, FieldMappingEntry>
            {
                ["SupplierItemCode"] = new() { ExternalField = "Ihre Materialnr" },
                // FixedValue-only entry contributes no source column → must be skipped.
                ["Currency"] = new() { FixedValue = "EUR" },
            },
        };
        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(orgId, supplierId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var ctrl = BuildController(db, orgId, acceptance.Object, poMappings.Object, suggester.Object,
            new AiSuggestionDecisionService(db));

        var result = await ctrl.GetMappingSuggestions(orderId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<IReadOnlyList<MappingSuggestionDto>>().Subject;

        list.Should().HaveCount(2);
        list.Should().Contain(s =>
            s.TargetKey == "PoNumber" && s.SourceId == "Bestellnummer"
            && s.SourceKind == "raw" && s.Confidence >= 0.9);
        list.Should().Contain(s =>
            s.TargetKey == "SupplierItemCode" && s.SourceId == "Ihre Materialnr"
            && s.SourceKind == "raw");
        // The FixedValue-only entry must NOT appear (no source column to wire).
        list.Should().NotContain(s => s.TargetKey == "Currency");
    }

    // ── validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Validation_ForeignOrder_Returns404()
    {
        using var db = NewDb();
        var (_, _, orderId) = await SeedOrderAsync(db);

        var acceptance = new Mock<ISupplierAcceptanceService>();
        // Service signals "order not found for this org" by returning null.
        acceptance
            .Setup(s => s.ValidateOrderAsync(It.IsAny<Guid>(), orderId, It.IsAny<CancellationToken>(), It.IsAny<ProcuLink.Core.Services.OutputFormat?>()))
            .ReturnsAsync((IReadOnlyList<OrderValidationResult>?)null);

        var ctrl = BuildController(db, Guid.NewGuid(), acceptance.Object,
            Mock.Of<IPoMappingService>(), Mock.Of<IFieldMappingSuggester>(),
            new AiSuggestionDecisionService(db));

        var result = await ctrl.GetValidation(orderId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// Severity drives the amber "review" badge; the GATE drives "Blocking". Those used to be the
    /// same computation (<c>failed &amp;&amp; severity == "error"</c>) — which is how a failing
    /// mandatory invariant, always error severity, came back claiming to block a delivery the server
    /// performs anyway. The blocking set is therefore stubbed alongside the rows, because that is
    /// what the endpoint really consults; the behavioural proof against the REAL evaluator
    /// (invariants included) is <c>ValidationBlockingMatchesTheGateTests</c>.
    /// </summary>
    [Fact]
    public async Task Validation_MapsSeverityToStatusAndBlocking()
    {
        using var db = NewDb();
        var (orgId, _, orderId) = await SeedOrderAsync(db);

        var rows = new List<OrderValidationResult>
        {
            // A failing error rule → review + blocking.
            new() { OrgId = orgId, OrderId = orderId, Severity = "error", Status = "fail",
                    Code = "required.supplierItemCode", Message = "Supplier code is required",
                    LineNumber = 2 },
            // A failing warning rule → review + advisory (non-blocking).
            new() { OrgId = orgId, OrderId = orderId, Severity = "warning", Status = "fail",
                    Code = "city.looks_like_label", Message = "City looks like a label" },
            // A passing rule → valid, no reason.
            new() { OrgId = orgId, OrderId = orderId, Severity = "info", Status = "pass",
                    Code = "date.sanity", Message = "" },
        };

        var acceptance = new Mock<ISupplierAcceptanceService>();
        acceptance
            .Setup(s => s.ValidateOrderAsync(orgId, orderId, It.IsAny<CancellationToken>(), It.IsAny<ProcuLink.Core.Services.OutputFormat?>()))
            .ReturnsAsync(rows);
        // The gate refuses on the supplier rule and on nothing else — the warning is advice, and a
        // row the gate does not name never gets a blocking badge no matter what its severity says.
        acceptance
            .Setup(s => s.GetBlockingFailuresAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ProcuLink.Core.Services.AcceptanceBlocker(
                    "required.supplierItemCode", 2, "Supplier code is required"),
            });

        var ctrl = BuildController(db, orgId, acceptance.Object,
            Mock.Of<IPoMappingService>(), Mock.Of<IFieldMappingSuggester>(),
            new AiSuggestionDecisionService(db));

        var result = await ctrl.GetValidation(orderId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<IReadOnlyList<FieldValidationStateDto>>().Subject;

        list.Should().HaveCount(3);

        var error = list.Single(v => v.Key == "required.supplierItemCode");
        error.State.Should().Be("review");
        error.Blocking.Should().BeTrue();
        error.Reason.Should().Be("Supplier code is required");

        var warning = list.Single(v => v.Key == "city.looks_like_label");
        warning.State.Should().Be("review");
        warning.Blocking.Should().BeFalse();

        var pass = list.Single(v => v.Key == "date.sanity");
        pass.State.Should().Be("valid");
        pass.Reason.Should().BeNull();
        pass.Blocking.Should().BeFalse();
    }

    // ── catalog-hints ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CatalogHints_ForeignOrder_Returns404()
    {
        using var db = NewDb();
        var (_, _, orderId) = await SeedOrderAsync(db);
        var ctrl = BuildWithEmptyCollaborators(db, Guid.NewGuid());

        var result = await ctrl.GetCatalogHints(orderId, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task CatalogHints_NoCatalogMatch_ReturnsEmpty()
    {
        using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedOrderAsync(db);
        AddLine(db, orderId, 1, supplierItemCode: "UNKNOWN", unitPrice: 10m);
        await db.SaveChangesAsync();

        var ctrl = BuildWithEmptyCollaborators(db, orgId);

        var result = await ctrl.GetCatalogHints(orderId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<CatalogPriceHintDto>>().Which.Should().BeEmpty();
    }

    [Fact]
    public async Task CatalogHints_ComputesVariance_ForMatchedLine()
    {
        using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedOrderAsync(db);

        // Catalog price 110, PO price 100 → variance (catalog − po)/po = (110-100)/100 = +10%.
        AddCatalogProduct(db, orgId, supplierId, code: "SKU-1", price: 110m, currency: "EUR");
        AddLine(db, orderId, 1, supplierItemCode: "SKU-1", unitPrice: 100m);
        await db.SaveChangesAsync();

        var ctrl = BuildWithEmptyCollaborators(db, orgId);

        var result = await ctrl.GetCatalogHints(orderId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<IReadOnlyList<CatalogPriceHintDto>>().Subject;

        list.Should().HaveCount(1);
        var hint = list[0];
        hint.LineKey.Should().Be("line:1"); // the key format the UI indexes by
        hint.CatalogCode.Should().Be("SKU-1");
        hint.CatalogPrice.Should().Be(110m);
        hint.PoPrice.Should().Be(100m);
        hint.VariancePercent.Should().Be(10m);
        hint.Currency.Should().Be("EUR");
    }

    [Theory]
    // EU comma-decimal "73,22" must parse to 73.22 (NOT 7322 — a 100× corruption).
    [InlineData("73,22", 73.22, 73.22, 0)]      // equal prices → 0% variance, no corruption
    [InlineData("1.234,56", 1234.56, 1234.56, 0)] // EU thousands+decimal
    public void CatalogVariance_EuAwarePrice_ComputesWithoutCorruption(
        string rawCatalogPrice, double expectedCatalog, double poPrice, double expectedVariance)
    {
        // The per-order endpoint computes variance via the same EU-aware parse the catalog
        // import + PriceVarianceGuard use, so a comma-decimal catalog price is never read as
        // a 100×/1000×-inflated value. Tested directly against the controller's pure helper.
        var (catalog, variance) = MapperEnrichmentController.ComputeCatalogVariance(
            rawCatalogPrice, (decimal)poPrice);

        catalog.Should().Be((decimal)expectedCatalog);
        variance.Should().Be((decimal)expectedVariance);
    }

    [Fact]
    public void CatalogVariance_StandardCase_UsesCatalogMinusPoOverPo()
    {
        // (catalog 110 − po 100)/po 100 × 100 = +10% (the frontend's documented formula).
        var (catalog, variance) = MapperEnrichmentController.ComputeCatalogVariance("110", 100m);
        catalog.Should().Be(110m);
        variance.Should().Be(10m);
    }

    [Fact]
    public async Task CatalogHints_MatchesByManufacturerPartNumber()
    {
        using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedOrderAsync(db);

        // No supplier item code resolved yet, but the MPN matches a catalog code.
        AddCatalogProduct(db, orgId, supplierId, code: "MPN-9", price: 50m);
        AddLine(db, orderId, 1, supplierItemCode: null, unitPrice: 40m, mpn: "MPN-9");
        await db.SaveChangesAsync();

        var ctrl = BuildWithEmptyCollaborators(db, orgId);

        var result = await ctrl.GetCatalogHints(orderId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<IReadOnlyList<CatalogPriceHintDto>>().Subject;

        list.Should().HaveCount(1);
        list[0].LineKey.Should().Be("line:1");
        list[0].CatalogCode.Should().Be("MPN-9");
        list[0].VariancePercent.Should().Be(25m); // (catalog 50 − po 40)/po 40 × 100 = +25%
    }

    [Fact]
    public async Task CatalogHints_NullCatalogPrice_YieldsNullVariance()
    {
        using var db = NewDb();
        var (orgId, supplierId, orderId) = await SeedOrderAsync(db);

        AddCatalogProduct(db, orgId, supplierId, code: "SKU-NOPRICE", price: null);
        AddLine(db, orderId, 1, supplierItemCode: "SKU-NOPRICE", unitPrice: 20m);
        await db.SaveChangesAsync();

        var ctrl = BuildWithEmptyCollaborators(db, orgId);

        var result = await ctrl.GetCatalogHints(orderId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<IReadOnlyList<CatalogPriceHintDto>>().Subject;

        list.Should().HaveCount(1);
        list[0].CatalogPrice.Should().BeNull();
        list[0].PoPrice.Should().Be(20m);
        list[0].VariancePercent.Should().BeNull();
    }

    // ── ai-suggestion-decisions ───────────────────────────────────────────────

    [Fact]
    public async Task RecordDecision_ForeignOrder_Returns404()
    {
        using var db = NewDb();
        var (_, _, orderId) = await SeedOrderAsync(db);
        var ctrl = BuildWithEmptyCollaborators(db, Guid.NewGuid());

        var result = await ctrl.RecordDecision(
            orderId,
            new RecordSuggestionDecisionRequest("lines[1].supplierItemCode", "SKU-1", true, 0.92),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        (await db.AiSuggestionDecisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RecordDecision_WritesDurableRow()
    {
        using var db = NewDb();
        var (orgId, _, orderId) = await SeedOrderAsync(db);
        var ctrl = BuildWithEmptyCollaborators(db, orgId);

        var result = await ctrl.RecordDecision(
            orderId,
            new RecordSuggestionDecisionRequest("lines[2].supplierItemCode", "SKU-42", true, 0.88),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();

        var row = await db.AiSuggestionDecisions.SingleAsync();
        row.OrgId.Should().Be(orgId);
        row.OrderId.Should().Be(orderId);
        row.LineNumber.Should().Be(2);
        row.SuggestedSupplierItemCode.Should().Be("SKU-42");
        row.ChosenSupplierItemCode.Should().Be("SKU-42");
        row.Decision.Should().Be(AiSuggestionDecisionKind.Accepted);
        row.Confidence.Should().Be(0.88);
    }

    [Fact]
    public async Task RecordDecision_Reject_RecordsRejectedWithNoChosenCode()
    {
        using var db = NewDb();
        var (orgId, _, orderId) = await SeedOrderAsync(db);
        var ctrl = BuildWithEmptyCollaborators(db, orgId);

        await ctrl.RecordDecision(
            orderId,
            new RecordSuggestionDecisionRequest("buyerName", "Field-X", false, 0.4),
            CancellationToken.None);

        var row = await db.AiSuggestionDecisions.SingleAsync();
        row.Decision.Should().Be(AiSuggestionDecisionKind.Rejected);
        row.ChosenSupplierItemCode.Should().BeNull();
        // Header-level target with no line index → line 0.
        row.LineNumber.Should().Be(0);
    }
}
