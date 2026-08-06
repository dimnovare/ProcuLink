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
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Conformance;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// The HTTP face of the connection-revision transport rule: an operator who types a cleartext or
/// credential-bearing delivery endpoint gets a readable 400 with the machine-readable policy code —
/// not a 500 from an unhandled <see cref="OutboundUrlPolicyException"/> — and an operator whose
/// endpoint was saved before enforcement existed is TOLD, on read, rather than left exposed.
///
/// <para>The warning mirrors <c>DeliveryConfigResponse.InsecureTransportWarning</c> deliberately:
/// the revision editor and the delivery-config editor show the same fact about the same blob, and
/// neither ever echoes the URL — a stored userinfo URL is exactly the case where quoting it would
/// copy the password onto the screen.</para>
/// </summary>
public sealed class ConnectionRevisionTransportSecurityControllerTests
{
    private const string SecretPassword   = "hunter2";
    private const string CleartextUrl     = "http://supplier.example/orders";
    private const string CleartextUrlJson = $$"""{"url":"{{CleartextUrl}}"}""";
    private const string UserInfoUrlJson  = $$"""{"url":"https://admin:{{SecretPassword}}@supplier.example/orders"}""";
    private const string SecureUrlJson    = """{"url":"https://supplier.example/orders"}""";

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

        // Every feature granted: this suite is about the transport rule, and a billing 403 would
        // answer these requests before the rule under test ever ran.
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
    public async Task CreateDraft_WithACleartextEndpoint_Returns400WithThePolicyCode()
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle(CleartextUrlJson)),
            CancellationToken.None);

        var (error, message) = Assert400(result);
        error.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        message.Should().Contain("https://");
        h.Db.SupplierConnectionRevisions.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDraft_WithCredentialsInTheUrl_Returns400_AndTheBodyNeverCarriesTheSecret()
    {
        var h = Build();
        var revisionId = SeedLegacyDraft(h, SecureUrlJson);

        var result = await h.Controller.UpdateDraft(
            h.Connection.Id, revisionId,
            new UpdateConnectionRevisionRequest(Bundle(UserInfoUrlJson)),
            CancellationToken.None);

        // The refusal is asserted first: with no refusal there is no body, and every
        // "must not contain the secret" assertion below would pass while proving nothing.
        var (error, message) = Assert400(result);
        error.Should().Be(OutboundUrlPolicy.ErrorCredentialsInUrl);
        message.Should().NotContain(SecretPassword);
        message.Should().NotContain("supplier.example");
    }

    /// <summary>
    /// The negative control for the two above. A controller that answered 400 to every draft write
    /// would satisfy both, and would also have broken the feature.
    /// </summary>
    [Fact]
    public async Task CreateDraft_WithAnHttpsEndpoint_Succeeds()
    {
        var h = Build();

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle(SecureUrlJson)),
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ConnectionRevisionDto>().Subject;
        dto.DeliveryConfigJson.Should().Be(SecureUrlJson);
        dto.InsecureTransportWarning.Should().BeNull();
    }

    /// <summary>
    /// The sibling refusal in the same request body: an encrypted-credential reference a caller can
    /// only have got from outside this API. Refused with its own code, and the response never echoes
    /// the blob.
    /// </summary>
    [Fact]
    public async Task CreateDraft_WithACallerSuppliedCredentialsRef_Returns400WithItsOwnCode()
    {
        var h = Build();
        const string blob = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

        var result = await h.Controller.CreateDraft(
            h.Connection.Id,
            new CreateConnectionRevisionRequest(
                CloneFromActive: false, Bundle(SecureUrlJson) with { CredentialsRef = blob }),
            CancellationToken.None);

        var (error, message) = Assert400(result);
        error.Should().Be(ClientSuppliedCredentialsRefException.Code);
        message.Should().NotContain(blob);
        h.Db.SupplierConnectionRevisions.Should().BeEmpty();
    }

    // ── Read path tells the operator about a config that predates enforcement ─

    [Fact]
    public async Task GetRevision_ForAnEndpointThatPredatesEnforcement_WarnsWithoutQuotingTheUrl()
    {
        var h = Build();
        var revisionId = SeedLegacyDraft(h, CleartextUrlJson);

        var result = await h.Controller.GetRevision(h.Connection.Id, revisionId, CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ConnectionRevisionDto>().Subject;
        dto.InsecureTransportWarning.Should().NotBeNullOrWhiteSpace(
            "delivery continues from this revision, so the operator has to be able to find out");
        dto.InsecureTransportWarning.Should().NotContain(CleartextUrl);
    }

    [Fact]
    public async Task GetRevision_ForAStoredUserInfoEndpoint_NeverPutsThePasswordInTheWarning()
    {
        var h = Build();
        var revisionId = SeedLegacyDraft(h, UserInfoUrlJson);

        var result = await h.Controller.GetRevision(h.Connection.Id, revisionId, CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ConnectionRevisionDto>().Subject;
        dto.InsecureTransportWarning.Should().NotBeNullOrWhiteSpace();
        dto.InsecureTransportWarning.Should().NotContain(SecretPassword);
    }

    [Fact]
    public async Task GetRevision_ForAnEndpointThatStillPasses_CarriesNoWarning()
    {
        var h = Build();
        var revisionId = SeedLegacyDraft(h, SecureUrlJson);

        var result = await h.Controller.GetRevision(h.Connection.Id, revisionId, CancellationToken.None);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ConnectionRevisionDto>().Subject;
        dto.InsecureTransportWarning.Should().BeNull(
            "a warning on every revision would train operators to ignore it");
    }
}
