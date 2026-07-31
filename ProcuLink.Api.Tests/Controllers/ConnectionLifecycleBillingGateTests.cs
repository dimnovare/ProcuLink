using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Conformance;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// WP-11 follow-through — <b>the third write path to a gated delivery channel.</b>
///
/// <para>WP-11 gated the two paths that AUTHOR a delivery configuration: the live row
/// (<c>PUT /api/suppliers/{id}/delivery-config</c>) and a connection revision draft
/// (<c>POST|PUT /api/connections/{id}/revisions…</c>). There is a third that ACTIVATES one, and it
/// was not gated: <c>POST …/revisions/{revId}/rollback</c> clones a previously-published, now
/// archived revision — bundle verbatim, including <c>DeliveryProtocol</c> and <c>OutputFormat</c> —
/// into a <b>new published revision</b> and moves the connection's active pointer to it.</para>
///
/// <para><b>Why the authoring gate does not cover it.</b> The target passed the gate when it was
/// authored, and nothing revokes a stored revision when an org downgrades. So an org that held
/// Enterprise, published an <c>erp_erply</c> revision, moved to a stock channel, and then dropped to
/// Growth can press Rollback and be delivering over ERP again — a capability it demonstrably no
/// longer pays for, re-acquired through an endpoint whose two siblings both refuse it. Publish has
/// the same shape: a draft authored on Enterprise can be published after the downgrade.</para>
///
/// <para><b>What this does NOT claim to fix.</b> An org whose gated revision is <i>still published</i>
/// keeps delivering through it after a downgrade, because there is no feature check anywhere on the
/// delivery path — <c>DeliverOrderJob</c> and <c>DeliveryService</c> consult
/// <c>CanProcessOrdersAsync</c> (account status and volume), never <c>HasFeatureAsync</c>. That is a
/// billing-policy decision about revoking capability from live traffic, not a bug in this endpoint,
/// and it is recorded in the PR rather than decided here. This file closes the narrower, unambiguous
/// hole: an endpoint that hands back a gated capability on request.</para>
/// </summary>
public sealed class ConnectionLifecycleBillingGateTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>Grants exactly one feature and denies every other — same shape as the WP-11 tests.</summary>
    private static Mock<IBillingService> BillingGranting(BillingFeature? granted)
    {
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((Guid _, BillingFeature f, CancellationToken _) => f == granted);
        return billing;
    }

    private sealed record Harness(
        ConnectionsController Controller,
        ProcuLinkDbContext Db,
        Guid ConnectionId,
        Guid ArchivedRevisionId,
        Guid DraftRevisionId);

    /// <summary>
    /// A connection whose history contains an archived, previously-published revision on
    /// <paramref name="protocol"/>/<paramref name="outputFormat"/> (the rollback target) plus a draft
    /// on the same bundle (the publish target) — the state an org lands in after using a gated
    /// channel and then moving off it.
    /// </summary>
    private static Harness Build(BillingFeature? granted, string? protocol, string? outputFormat = null)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme", CreatedAt = DateTime.UtcNow });

        // SupplierConnection and its revisions carry a circular FK, so the connection is saved
        // unpinned first and the active pointer set afterwards.
        var connection = new SupplierConnection
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId, Name = "Acme",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.SupplierConnections.Add(connection);
        db.SaveChanges();

        var archived = new SupplierConnectionRevision
        {
            Id = Guid.NewGuid(), OrgId = orgId, ConnectionId = connection.Id, VersionNo = 1,
            Status = "archived", DeliveryProtocol = protocol, OutputFormat = outputFormat,
            CreatedAt = DateTime.UtcNow, PublishedAt = DateTime.UtcNow.AddDays(-2),
        };
        var live = new SupplierConnectionRevision
        {
            Id = Guid.NewGuid(), OrgId = orgId, ConnectionId = connection.Id, VersionNo = 2,
            Status = "published", DeliveryProtocol = "sftp", OutputFormat = "xml",
            CreatedAt = DateTime.UtcNow, PublishedAt = DateTime.UtcNow.AddDays(-1),
        };
        var draft = new SupplierConnectionRevision
        {
            Id = Guid.NewGuid(), OrgId = orgId, ConnectionId = connection.Id, VersionNo = 3,
            Status = "draft", DeliveryProtocol = protocol, OutputFormat = outputFormat,
            CreatedAt = DateTime.UtcNow,
        };
        db.SupplierConnectionRevisions.AddRange(archived, live, draft);
        db.SaveChanges();

        connection.ActiveRevisionId = live.Id;
        db.SaveChanges();

        var controller = new ConnectionsController(
            new SupplierConnectionService(db, new Mock<IReplayService>().Object, new Mock<IConformanceService>().Object),
            new Mock<IReplayService>().Object,
            tenant.Object,
            BillingGranting(granted).Object);

        return new Harness(controller, db, connection.Id, archived.Id, draft.Id);
    }

    private static void Assert403For(IActionResult result, BillingFeature feature, string capability)
    {
        var status = result.Should().BeOfType<ObjectResult>(
            $"the {feature} gate must refuse this activation, not fall through").Subject;
        status.StatusCode.Should().Be(403);

        var error = (string)((dynamic)status.Value!).error;
        // The FULL code, not just its plan suffix: a 403 naming the right plan for the wrong
        // capability sends the customer to the right price for the wrong reason.
        error.Should().Be(BillingGateErrors.RequiresPlan(capability, feature));
    }

    // ── Rollback ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("erp_erply", null, "erp_delivery")]
    [InlineData("erp_directo", null, "erp_delivery")]
    public async Task Rollback_CannotReactivateAnErpChannelTheOrgNoLongerHolds(
        string protocol, string? format, string capability)
    {
        var h = Build(granted: null, protocol, format);

        var result = await h.Controller.Rollback(h.ConnectionId, h.ArchivedRevisionId, CancellationToken.None);

        Assert403For(result, BillingFeature.ErpConnectors, capability);
    }

    [Fact]
    public async Task Rollback_CannotReactivateAWebhookChannelTheOrgNoLongerHolds()
    {
        var h = Build(granted: null, "http");

        var result = await h.Controller.Rollback(h.ConnectionId, h.ArchivedRevisionId, CancellationToken.None);

        Assert403For(result, BillingFeature.WebhookDelivery, "webhook_delivery");
    }

    [Fact]
    public async Task Rollback_CannotReactivateACxmlOutputTheOrgNoLongerHolds()
    {
        var h = Build(granted: null, "sftp", "cxml");

        var result = await h.Controller.Rollback(h.ConnectionId, h.ArchivedRevisionId, CancellationToken.None);

        Assert403For(result, BillingFeature.Cxml, "cxml_output");
    }

    /// <summary>
    /// The negative control. A gate that refused every rollback would pass every test above while
    /// breaking the feature for everyone, so the allowed path has to be pinned too.
    /// </summary>
    [Fact]
    public async Task Rollback_IsAllowed_WhenTheOrgStillHoldsTheFeature()
    {
        var h = Build(granted: BillingFeature.ErpConnectors, "erp_erply");

        var result = await h.Controller.Rollback(h.ConnectionId, h.ArchivedRevisionId, CancellationToken.None);

        (result as ObjectResult)?.StatusCode.Should().NotBe(403,
            "an org that still holds ErpConnectors must be able to roll back to its ERP revision");
    }

    /// <summary>A stock channel was never gated and must not become gated by this change.</summary>
    [Theory]
    [InlineData("sftp")]
    [InlineData("ftps")]
    [InlineData("email")]
    public async Task Rollback_OfAStockChannel_IsNotGated(string protocol)
    {
        var h = Build(granted: null, protocol, "xml");

        var result = await h.Controller.Rollback(h.ConnectionId, h.ArchivedRevisionId, CancellationToken.None);

        (result as ObjectResult)?.StatusCode.Should().NotBe(403,
            $"{protocol} is included on every paid plan — gating it here would take away something " +
            "Growth already pays for");
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_CannotActivateADraftOnAChannelTheOrgNoLongerHolds()
    {
        var h = Build(granted: null, "erp_erply");

        var result = await h.Controller.Publish(h.ConnectionId, h.DraftRevisionId, CancellationToken.None);

        Assert403For(result, BillingFeature.ErpConnectors, "erp_delivery");
    }

    [Fact]
    public async Task Publish_IsAllowed_WhenTheOrgStillHoldsTheFeature()
    {
        var h = Build(granted: BillingFeature.ErpConnectors, "erp_erply");

        var result = await h.Controller.Publish(h.ConnectionId, h.DraftRevisionId, CancellationToken.None);

        (result as ObjectResult)?.StatusCode.Should().NotBe(403,
            "holding the feature must still allow publishing");
    }

    /// <summary>
    /// When a bundle needs two features and the org holds neither, the 403 must name the dearer one
    /// — naming the cheaper gate sends the customer to a plan that would still refuse. Same rule the
    /// authoring paths follow, and the reason this reuses DeliveryCapabilityGate rather than
    /// re-deriving a decision here.
    /// </summary>
    [Fact]
    public async Task Rollback_WhenTwoGatesAreUnmet_NamesTheHigherTierOne()
    {
        var h = Build(granted: null, "erp_erply", "cxml");

        var result = await h.Controller.Rollback(h.ConnectionId, h.ArchivedRevisionId, CancellationToken.None);

        // ErpConnectors = Enterprise, Cxml = Operations.
        Assert403For(result, BillingFeature.ErpConnectors, "erp_delivery");
    }
}
