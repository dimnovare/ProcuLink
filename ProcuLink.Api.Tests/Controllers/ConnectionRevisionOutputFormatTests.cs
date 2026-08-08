using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Conformance;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// The defect, verbatim: a connection revision could be written naming an output format nothing in
/// this solution can build.
///
/// <para><c>SupplierConnectionService.ApplyScalars</c> assigned <c>rev.OutputFormat</c> straight from
/// the request with no allow-list, while both HTTP paths that write the LIVE delivery-config row ran
/// one. <see cref="OutputFormat"/> carries three values no transform can build —
/// <see cref="OutputFormat.UblOrder"/>, <see cref="OutputFormat.X12_850"/> and
/// <see cref="OutputFormat.EdifactOrders"/> — they name conformance PROFILES, and EDIFACT is
/// inbound-only (<c>EdifactOrderParser</c> reads it; nothing writes it). Every reader re-hydrates
/// them with <c>Enum.TryParse(ignoreCase: true)</c>, so <c>"edifactOrders"</c> sailed through.</para>
///
/// <para>With <c>Connections:RevisionAuthority</c> on — which it is in production on both Railway
/// services — a published revision is the authority for what a pinned order is transformed as, so
/// such a revision is real orders dying at
/// <c>OrderTransformService</c>'s "No transform service registered for format '…'", terminally, with
/// nothing failing until the first order arrives. And published revision content is frozen by
/// <c>proculink_block_published_revision_content_update</c>, so by then the row cannot be edited
/// back.</para>
///
/// <para><b>Both sides of every assertion are DERIVED from the DI registrations</b>
/// (<see cref="OutputTransformRegistry.AddOutputTransforms"/> — the same call both hosts make), never
/// typed out. Registering a seventh transform moves it from the refused theory to the accepted one
/// with no edit here. <see cref="TheDerivedSetIsNotVacuous"/> pins the shape so a broken derivation
/// reads as a failure rather than as an empty allow-list that refuses everything, or a total one
/// that accepts everything.</para>
/// </summary>
public sealed class ConnectionRevisionOutputFormatTests
{
    // ── The derivation. Both theories feed from here. ─────────────────────────

    /// <summary>
    /// Exactly what the hosts register, resolved out of a real container. Not
    /// <c>OutputTransformRegistry.All</c> directly: this is the guard, so it asks the question the
    /// production code's own answer must be checked against.
    /// </summary>
    private static IReadOnlyList<ITransformService> RegisteredTransforms() =>
        new ServiceCollection()
            .AddOutputTransforms()
            .BuildServiceProvider()
            .GetServices<ITransformService>()
            .ToList();

    private static IReadOnlyList<OutputFormat> BuildableFormats()
    {
        var registered = RegisteredTransforms();
        return Enum.GetValues<OutputFormat>().Where(f => registered.Any(t => t.CanTransform(f))).ToList();
    }

    private static IReadOnlyList<OutputFormat> UnbuildableFormats()
    {
        var buildable = BuildableFormats();
        return Enum.GetValues<OutputFormat>().Where(f => !buildable.Contains(f)).ToList();
    }

    public static TheoryData<OutputFormat> Buildable()
    {
        var data = new TheoryData<OutputFormat>();
        foreach (var f in BuildableFormats()) data.Add(f);
        return data;
    }

    public static TheoryData<OutputFormat> Unbuildable()
    {
        var data = new TheoryData<OutputFormat>();
        foreach (var f in UnbuildableFormats()) data.Add(f);
        return data;
    }

    // ── Anti-vacuity floor ────────────────────────────────────────────────────

    /// <summary>
    /// A derivation that silently produced an empty set on either side would make both theories below
    /// pass without executing a single case. The counts are asserted, not just non-emptiness, and the
    /// specific value the defect was reported against is named — if this test needs updating, a
    /// transform was added or removed and the ladder deserves a decision, not a re-baseline.
    /// </summary>
    [Fact]
    public void TheDerivedSetIsNotVacuous()
    {
        var buildable = BuildableFormats();
        var unbuildable = UnbuildableFormats();

        buildable.Should().HaveCount(6,
            "six ITransformService implementations are registered (Xml, Csv, cXML, Json, UBL Order, X12) " +
            "and each answers CanTransform for exactly one format — a different count means a transform " +
            "was added or removed, or the derivation is broken");
        unbuildable.Should().HaveCount(3,
            "UblOrder, X12_850 and EdifactOrders name conformance profiles that no transform builds");

        unbuildable.Should().Contain(OutputFormat.EdifactOrders,
            "EDIFACT is inbound-only — EdifactOrderParser reads it and no ITransformService writes it; " +
            "this is the value the revision write path used to accept");

        // The production allow-list must BE the derived set, not a copy that agrees today.
        OutputTransformRegistry.Catalog.Buildable.Should().Equal(buildable);
        OutputTransformRegistry.Catalog.AllowedListForMessage.Should().Be("xml, csv, cxml, json, ubl, x12");
    }

    // ── The defect: an unbuildable format must be refused at write time ───────

    [Theory]
    [MemberData(nameof(Unbuildable))]
    public async Task CreateDraft_WithAFormatNoRegisteredTransformCanBuild_Is400_AndWritesNothing(
        OutputFormat format)
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle(format.ToString())),
            CancellationToken.None);

        var (error, message) = Assert400(result);
        error.Should().Be(UnsupportedOutputFormatException.Code);
        message.Should().Contain(format.ToString(), "the refusal must name the format the caller sent");
        message.Should().Contain("xml, csv, cxml, json, ubl, x12", "and the set they may choose from");

        h.Db.SupplierConnectionRevisions.Should().BeEmpty(
            "a refused bundle must not leave a half-applied revision behind");
    }

    [Theory]
    [MemberData(nameof(Unbuildable))]
    public async Task UpdateDraft_WithAFormatNoRegisteredTransformCanBuild_Is400_AndLeavesTheDraftAlone(
        OutputFormat format)
    {
        var h = Build();
        var revisionId = SeedDraft(h, outputFormat: "csv");

        var result = await h.Controller.UpdateDraft(
            h.Connection.Id, revisionId,
            new UpdateConnectionRevisionRequest(Bundle(format.ToString())),
            CancellationToken.None);

        var (error, _) = Assert400(result);
        error.Should().Be(UnsupportedOutputFormatException.Code);

        h.Db.SupplierConnectionRevisions.Single().OutputFormat.Should().Be("csv",
            "ApplyScalars validates before ANY assignment, so a refusal cannot leave the draft " +
            "half-updated with the rest of the bundle applied");
    }

    /// <summary>
    /// The reported case, spelled the way it was reported. Kept as its own named test alongside the
    /// derived theory: the theory is what keeps this true for the NEXT unbuildable format, and this is
    /// the receipt for the one that was actually shipped.
    /// </summary>
    [Fact]
    public async Task CreateDraft_WithEdifactOrders_TheReportedCase_Is400()
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle("edifactOrders")),
            CancellationToken.None);

        var (error, message) = Assert400(result);
        error.Should().Be(UnsupportedOutputFormatException.Code);
        message.Should().Contain("edifactOrders");

        h.Db.SupplierConnectionRevisions.Should().BeEmpty();
    }

    /// <summary>
    /// <c>DeliveryMediaTypes</c> carries a row for <see cref="OutputFormat.EdifactOrders"/> —
    /// <c>application/edifact</c> / <c>.edi</c> — and an <c>"edifact"</c> token alias. Those rows are
    /// read-side lookup data (the table is exhaustive over the enum by design), and they are NOT a
    /// claim that the format can be produced. Pinned here so nobody reads the row as permission.
    /// </summary>
    [Fact]
    public void AMediaTypeRowIsNotAnOfferToBuildTheFormat()
    {
        DeliveryMediaTypes.All.Should().ContainKey(OutputFormat.EdifactOrders,
            "the media-type table is exhaustive over OutputFormat so no format can be silently mis-typed");

        OutputTransformRegistry.Catalog.IsBuildable(OutputFormat.EdifactOrders).Should().BeFalse();
        OutputTransformRegistry.Catalog.IsBuildableToken("edifact").Should().BeFalse();
    }

    // ── …and every format that CAN be built is still accepted ─────────────────

    [Theory]
    [MemberData(nameof(Buildable))]
    public async Task CreateDraft_WithEveryFormatARegisteredTransformCanBuild_Succeeds(OutputFormat format)
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle(format.ToString())),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>(
            "a format some registered transform can build must stay writable — an allow-list that " +
            "refuses everything is as broken as one that refuses nothing");

        h.Db.SupplierConnectionRevisions.Single().OutputFormat
            .Should().Be(OutputFormatCatalog.Token(format),
                "an accepted format is normalised to the persisted lowercase token, the same spelling " +
                "the live delivery-config row stores");
    }

    /// <summary>
    /// Matching is case-insensitive on both write paths, as it always was on the delivery-config one —
    /// so an operator who types <c>CXML</c> is not refused for casing, and the stored value is still
    /// the single token spelling.
    /// </summary>
    [Fact]
    public async Task CreateDraft_WithAnUppercaseFormat_IsAccepted_AndStoredAsTheToken()
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle("  CXML  ")),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        h.Db.SupplierConnectionRevisions.Single().OutputFormat.Should().Be("cxml");
    }

    /// <summary>
    /// Null/blank is "not set", not a refusal — the revision then falls back to the caller's or the
    /// live row's format, which is what a mapping-only partial update relies on.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateDraft_WithNoFormat_IsAccepted_AndStoresNull(string? format)
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle(format)),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        h.Db.SupplierConnectionRevisions.Single().OutputFormat.Should().BeNull();
    }

    /// <summary>
    /// Clone-from-active copies a bundle that is ALREADY stored and does not pass through
    /// <c>ApplyScalars</c>. It stays allowed for the same reason the transport and credential-header
    /// rules exempt it: refusing it would turn a write-time guard into an outage for whoever already
    /// has such a revision, with no path to publish a mapping fix.
    /// </summary>
    [Fact]
    public async Task CreateDraft_CloningAnActiveRevisionThatAlreadyHasABadFormat_IsNotRefused()
    {
        var h = Build();
        var activeId = SeedDraft(h, outputFormat: "edifactOrders", status: "published");
        h.Connection.ActiveRevisionId = activeId;
        h.Db.SaveChanges();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: true, Bundle: null),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>(
            "an already-stored bundle is not a caller-supplied one; the guard is on what a caller " +
            "INTRODUCES, and stranding an existing revision would be worse than the defect");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed record Harness(
        ConnectionsController Controller, ProcuLinkDbContext Db, Guid OrgId, SupplierConnection Connection);

    private static Harness Build()
    {
        var db = new ProcuLinkDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        // Every feature granted: this suite is about the format allow-list, and a billing 403 would
        // answer these requests before the rule under test ever ran.
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Northwind", CreatedAt = DateTime.UtcNow });
        var connection = new SupplierConnection
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId, Name = "Northwind",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.SupplierConnections.Add(connection);
        db.SaveChanges();

        var controller = new ConnectionsController(
            new SupplierConnectionService(db, new Mock<IReplayService>().Object, new Mock<IConformanceService>().Object),
            new Mock<IReplayService>().Object,
            tenant.Object,
            billing.Object);

        return new Harness(controller, db, orgId, connection);
    }

    private static ConnectionRevisionBundleDto Bundle(string? outputFormat) => new(
        InputMappingJson: "{}",
        OutputMappingJson: null,
        OutputFormat: outputFormat,
        DeliveryProtocol: DeliveryProtocolConstants.Http,
        DeliveryConfigJson: """{"url":"https://supplier.example/orders"}""",
        DeliveryAutoDeliver: false,
        CredentialsRef: null,
        AcceptanceProfileId: null,
        AcceptanceVersionNo: null,
        CatalogMode: "live",
        ItemMappings: new List<ConnectionItemMappingDto>());

    private static Guid SeedDraft(Harness h, string outputFormat, string status = "draft")
    {
        var rev = new SupplierConnectionRevision
        {
            Id                 = Guid.NewGuid(),
            ConnectionId       = h.Connection.Id,
            OrgId              = h.OrgId,
            SupplierId         = h.Connection.SupplierId,
            VersionNo          = 1,
            Status             = status,
            CreatedAt          = DateTime.UtcNow.AddDays(-3),
            CatalogMode        = "live",
            OutputFormat       = outputFormat,
            DeliveryProtocol   = DeliveryProtocolConstants.Http,
            DeliveryConfigJson = """{"url":"https://supplier.example/orders"}""",
        };
        h.Db.SupplierConnectionRevisions.Add(rev);
        h.Db.SaveChanges();
        return rev.Id;
    }

    private static (string Error, string Message) Assert400(IActionResult result)
    {
        var bad = result.Should().BeOfType<BadRequestObjectResult>(
            "a format nothing can build is the caller's mistake, not a server fault — and it must be " +
            "answered at write time, not by an order dying at transform").Subject;
        var value = (dynamic)bad.Value!;
        return ((string)value.error, (string)value.message);
    }
}
