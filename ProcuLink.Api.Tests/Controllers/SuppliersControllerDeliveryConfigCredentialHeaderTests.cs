using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services.StarterTemplates;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// The HTTP face of the credential-header rule on <c>PUT /api/suppliers/{id}/delivery-config</c>: an
/// operator who types a credential into the extra-headers map of <c>config_json</c> gets a readable
/// 400 with the machine-readable code — not the 500 an unhandled exception would give, and not the
/// bare <c>{ error: "&lt;message&gt;" }</c> with no machine-readable code at all that the generic
/// <see cref="ArgumentException"/> catch produces — and the response never echoes the token.
///
/// <para>Harness shape mirrors <c>ConnectionRevisionTransportSecurityControllerTests</c> (mocked
/// <see cref="ICurrentTenantService"/> for a fixed org id, <see cref="IBillingService"/> granting
/// every feature so a billing 403 cannot answer before the rule under test runs). Duplicated rather
/// than shared: that file belongs to an open pull request this branch is stacked on.</para>
/// </summary>
public sealed class SuppliersControllerDeliveryConfigCredentialHeaderTests
{
    private const string Token = "t0ps3cret";

    private sealed record Harness(
        SuppliersController Controller, ProcuLinkDbContext Db, Guid OrgId, Guid SupplierId);

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
        // would answer the request before the rule under test ever ran.
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        var encryption = new DeliveryEncryptionService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build());

        // Not revision-governed — this suite is about the write path itself, not the republish
        // side effect, so both calls are stubbed to their "nothing to do" outcomes.
        var connections = new Mock<ISupplierConnectionService>();
        connections
            .Setup(c => c.RepublishLiveDeliveryAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliveryRepublishOutcome(DeliveryRepublishStatus.NotGoverned, null));
        connections
            .Setup(c => c.DescribeDeliveryGovernanceAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliveryGovernanceInfo(false, null, null));

        // The REAL DeliveryConfigService, sharing the same db as the controller — a mock here would
        // only prove the controller trusts its dependency, not that the guard under test actually
        // refuses anything.
        var controller = new SuppliersController(
            new Mock<ISupplierProfileRepository>().Object,
            new Mock<IItemMappingService>().Object,
            db,
            tenant.Object,
            billing.Object,
            new Mock<IPoMappingService>().Object,
            new DeliveryConfigService(db, encryption),
            new Mock<IDeliveryService>().Object,
            new Mock<IAnalyticsService>().Object,
            new Mock<IFileStorageService>().Object,
            new Mock<ISourceColumnExtractor>().Object,
            new Mock<IStarterTemplateService>().Object,
            new Mock<ISupplierCatalogService>().Object,
            connections.Object);

        return new Harness(controller, db, orgId, supplierId);
    }

    /// <summary>
    /// The refusal must reach the caller as a 400 with the machine-readable code — not the 500 an
    /// unhandled exception would give, and not the bare `{ error: "<message>" }` with no
    /// machine-readable code at all that the generic ArgumentException catch produces.
    /// </summary>
    [Fact]
    public async Task UpsertDeliveryConfig_WithACredentialHeader_Returns400WithTheCode_AndNeverEchoesTheToken()
    {
        var h = Build();

        var result = await h.Controller.UpsertDeliveryConfig(
            h.SupplierId,
            new UpsertDeliveryConfigRequest(
                DeliveryProtocolConstants.Http, false,
                $$$"""{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer {{{Token}}}"}}""",
                null),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var value = (dynamic)bad.Value!;
        ((string)value.error).Should().Be("credential_header_in_delivery_config");
        ((string)value.message).Should().Contain("'Authorization'");
        ((string)value.message).Should().NotContain(Token);
    }

    /// <summary>An ordinary header still saves through the same action — otherwise the test above
    /// would pass for a rule that refused everything.</summary>
    [Fact]
    public async Task UpsertDeliveryConfig_WithAnOrdinaryHeader_Succeeds()
    {
        var h = Build();

        var result = await h.Controller.UpsertDeliveryConfig(
            h.SupplierId,
            new UpsertDeliveryConfigRequest(
                DeliveryProtocolConstants.Http, false,
                """{"url":"https://supplier.example/orders","headers":{"X-Correlation-Id":"abc"}}""",
                null),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
