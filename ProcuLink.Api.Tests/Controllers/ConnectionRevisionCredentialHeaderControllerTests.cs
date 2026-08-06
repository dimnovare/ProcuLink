using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Conformance;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// The HTTP face of the connection-revision credential-header rule: an operator who types a
/// credential into the delivery config's extra-headers map gets a readable 400 with the
/// machine-readable policy code — not a 500 from an unhandled
/// <see cref="CredentialHeaderInConfigException"/> — and the response body never carries the token,
/// mirroring <see cref="ConnectionRevisionTransportSecurityControllerTests"/> for the sibling
/// transport rule.
/// </summary>
public sealed class ConnectionRevisionCredentialHeaderControllerTests
{
    private const string Token = "t0ps3cret";
    // $$$ / {{{...}}}: see ConnectionRevisionCredentialHeaderTests.cs for why $$ / {{...}} does not
    // compile here (CS9007 — two literal closing braces follow the interpolated token).
    private static readonly string WithToken =
        $$$"""{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer {{{Token}}}"}}""";
    private const string Clean =
        """{"url":"https://supplier.example/orders","headers":{"X-Correlation-Id":"abc"}}""";

    // ── helpers copied verbatim from ConnectionRevisionTransportSecurityControllerTests.cs:37-117.
    // Private to that class, so duplicated rather than shared — see task brief: that file belongs to
    // an open PR this branch is stacked on and must not be edited. ─────────────────────────────────

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

        // Every feature granted: this suite is about the credential-header rule, and a billing 403
        // would answer these requests before the rule under test ever ran.
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", CreatedAt = DateTime.UtcNow });
        var connection = new SupplierConnection
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId, Name = "Acme",
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

    private static ConnectionRevisionBundleDto Bundle(string? configJson) => new(
        InputMappingJson: "{}",
        OutputMappingJson: null,
        OutputFormat: "xml",
        DeliveryProtocol: DeliveryProtocolConstants.Http,
        DeliveryConfigJson: configJson,
        DeliveryAutoDeliver: false,
        CredentialsRef: null,
        AcceptanceProfileId: null,
        AcceptanceVersionNo: null,
        CatalogMode: "live",
        ItemMappings: new List<ConnectionItemMappingDto>());

    private static Guid SeedLegacyDraft(Harness h, string configJson)
    {
        var rev = new SupplierConnectionRevision
        {
            Id                 = Guid.NewGuid(),
            ConnectionId       = h.Connection.Id,
            OrgId              = h.OrgId,
            SupplierId         = h.Connection.SupplierId,
            VersionNo          = 1,
            Status             = "draft",
            CreatedAt          = DateTime.UtcNow.AddDays(-3),
            CatalogMode        = "live",
            OutputFormat       = "xml",
            DeliveryProtocol   = DeliveryProtocolConstants.Http,
            DeliveryConfigJson = configJson,
        };
        h.Db.SupplierConnectionRevisions.Add(rev);
        h.Db.SaveChanges();
        return rev.Id;
    }

    private static (string Error, string Message) Assert400(IActionResult result)
    {
        var bad = result.Should().BeOfType<BadRequestObjectResult>(
            "an endpoint the policy refuses is the caller's mistake, not a server fault").Subject;
        var value = (dynamic)bad.Value!;
        return ((string)value.error, (string)value.message);
    }

    // ── Refusal reaches the caller as a 400, with the shared policy's code ────

    [Fact]
    public async Task CreateDraft_WithACredentialHeader_Returns400WithTheCode()
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle(WithToken)),
            CancellationToken.None);

        var (error, message) = Assert400(result);
        // Deviation from the brief: it compared against the literal "credential_header_in_delivery_config"
        // rather than the exception's own Code constant. A hard-coded literal here is exactly the kind
        // of duplication that lets a code string drift silently — see ConnectionRevisionTransportSecurityControllerTests,
        // which compares against OutboundUrlPolicy.ErrorInsecureTransport / ClientSuppliedCredentialsRefException.Code
        // instead of a literal, for the same reason.
        error.Should().Be(CredentialHeaderInConfigException.Code);
        message.Should().Contain("'Authorization'");
        h.Db.SupplierConnectionRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDraft_WithACredentialHeader_Returns400_AndTheBodyNeverCarriesTheToken()
    {
        var h = Build();
        var revisionId = SeedLegacyDraft(h, Clean);

        var result = await h.Controller.UpdateDraft(
            h.Connection.Id, revisionId,
            new UpdateConnectionRevisionRequest(Bundle(WithToken)),
            CancellationToken.None);

        // The refusal is asserted first: with no refusal there is no body, and the "must not contain
        // the token" assertion below would pass while proving nothing.
        var (error, message) = Assert400(result);
        error.Should().Be(CredentialHeaderInConfigException.Code);
        message.Should().NotContain(Token);
    }

    /// <summary>
    /// The negative control. A controller that answered 400 to every draft write would satisfy both
    /// refusal tests above, and would also have broken the feature.
    /// </summary>
    [Fact]
    public async Task CreateDraft_WithOrdinaryHeaders_Succeeds()
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle(Clean)),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
