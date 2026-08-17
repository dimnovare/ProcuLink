using ProcuLink.TestSupport;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Core.Services.Organisation;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Repositories;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Catalog;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Transform.Detection;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// WP-11 — <b>the behavioural half of the ladder guard: every declared feature is really
/// refused below its plan.</b>
///
/// <para><c>BillingFeatureGateCoverageTests</c> proves each <see cref="BillingFeature"/> has a
/// correct plan boundary and a registered enforcement site. This class proves those sites
/// actually fire, by calling the real controller/service with the feature denied and asserting
/// a 403 — and, crucially, calling it again with the feature GRANTED and asserting it does not
/// 403. A one-sided gate test passes just as happily against a gate that refuses everyone.</para>
///
/// <para>Every declared feature except the three ingestion channels was enforced NOWHERE
/// before this change: a Pilot org could configure a webhook, wire up an Enterprise-only ERP
/// connector, author custom acceptance rules, bulk-import mappings, and read the org-wide
/// audit trail — all sold as belonging to tiers between €149 and "contact sales".</para>
///
/// <para><b>What deliberately stays open</b> is asserted too (see the "still open" region):
/// per-order audit, single-mapping edits, and the stock delivery channels. Gating those would
/// take away things lower tiers already pay for, which is the same dishonesty pointed the
/// other way.</para>
/// </summary>
public class BillingFeatureEnforcementTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delivery:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["Delivery:AllowPrivateNetworkTargets"] = "true",
        })
        .Build();

    /// <summary>Billing double that grants exactly one feature and denies the rest.</summary>
    private static Mock<IBillingService> BillingGranting(BillingFeature? granted)
    {
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((Guid _, BillingFeature f, CancellationToken _) => f == granted);
        billing.Setup(b => b.CheckOrderLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new LimitCheckResult(true, false, PlanConstants.Enterprise, int.MaxValue));
        return billing;
    }

    /// <summary>
    /// Asserts the refusal is a 403 carrying a well-formed <c>{capability}_requires_{plan}</c> code
    /// naming the plan that really unlocks <paramref name="feature"/>.
    ///
    /// <para><b>Why this is a full-shape match and not <c>EndWith</c>.</b> The original form checked
    /// only the <c>_requires_&lt;plan&gt;</c> suffix, so a 403 whose capability segment named a
    /// DIFFERENT capability passed — and the capability is a hand-written literal sitting next to
    /// the feature at each gate (<c>RequiresPlan("s3_ingestion", BillingFeature.SftpIngestion)</c> is
    /// a one-word copy-paste away). That mismatch is invisible to the plan check yet lands a
    /// customer on an upsell for the wrong feature. Pass <paramref name="capability"/> wherever
    /// production hardcodes one, and the exact string is pinned.</para>
    /// </summary>
    private static void Assert403NamingTheRightPlan(
        IActionResult result, BillingFeature feature, string? capability = null)
    {
        var status = result.Should().BeOfType<ObjectResult>(
            $"the {feature} gate must refuse, not fall through").Subject;
        status.StatusCode.Should().Be(403);

        var error = (string)((dynamic)status.Value!).error;

        if (capability is not null)
        {
            error.Should().Be(BillingGateErrors.RequiresPlan(capability, feature),
                "the 403 must name both the capability being refused and the plan that unlocks it");
            return;
        }

        // No capability supplied (the delivery gates derive theirs from DeliveryCapabilityGate, and
        // ConnectionLifecycleBillingGateTests pins those exactly): still require a well-formed code
        // with a non-empty snake_case capability segment, so a malformed or bare code cannot pass.
        error.Should().MatchRegex($"^[a-z0-9]+(_[a-z0-9]+)*_requires_{PlanConstants.GetMinimumPlan(feature)}$",
            "the 403 must be a complete {capability}_requires_{plan} code naming the plan that "
          + "actually unlocks the feature (WP-11 defect #1)");
    }

    private static void AssertNot403(IActionResult result, BillingFeature feature) =>
        (result as ObjectResult)?.StatusCode.Should().NotBe(403,
            $"an org that HAS {feature} must not be refused — otherwise the gate test is vacuous");

    // ═══ SuppliersController: WebhookDelivery / ErpConnectors / Cxml / BulkMapping ═══

    private sealed record SupplierHarness(
        SuppliersController Controller, ProcuLinkDbContext Db, Guid OrgId, Supplier Supplier);

    /// <param name="existingDelivery">
    /// The delivery configuration already stored for this supplier, or null for "none yet".
    /// The edit gate subtracts what the stored row already requires, so this is what separates
    /// creating a configuration from re-saving one.
    /// </param>
    private static SupplierHarness BuildSuppliers(
        BillingFeature? granted,
        SupplierDeliveryConfig? existingDelivery = null)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var deliveryConfig = new Mock<IDeliveryConfigService>();
        deliveryConfig.Setup(d => d.GetEntityAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingDelivery);
        deliveryConfig.Setup(d => d.UpsertAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpsertDeliveryConfigRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid sid, UpsertDeliveryConfigRequest r, CancellationToken _) =>
                new DeliveryConfigResponse(
                    SupplierId: sid, Protocol: r.Protocol, AutoDeliver: r.AutoDeliver, ConfigJson: r.ConfigJson,
                    HasCredentials: false, CredentialsDisplay: null,
                    CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow,
                    OutputFormat: r.OutputFormat ?? "xml"));

        // The ALLOWED path runs further into the method than the refused one (it saves, then
        // annotates with revision governance), so this double must answer or the positive
        // half of every gate test dies on a NullReference instead of proving anything.
        var connections = new Mock<ISupplierConnectionService>();
        connections.Setup(c => c.DescribeDeliveryGovernanceAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliveryGovernanceInfo(false, null, null));

        var controller = new SuppliersController(
            new Mock<ISupplierProfileRepository>().Object,
            new Mock<IItemMappingService>().Object,
            db,
            tenant.Object,
            BillingGranting(granted).Object,
            new Mock<IPoMappingService>().Object,
            deliveryConfig.Object,
            new Mock<IDeliveryService>().Object,
            new TestDoubles.FakeAnalyticsService(),
            new Mock<IFileStorageService>().Object,
            new SourceColumnExtractor(),
            new ProcuLink.Api.Services.StarterTemplates.StarterTemplateService(),
            new SupplierCatalogService(db),
            connections.Object);

        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Acme", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        db.SaveChanges();

        return new SupplierHarness(controller, db, orgId, supplier);
    }

    private static UpsertDeliveryConfigRequest Delivery(string protocol, string? outputFormat = null) =>
        new(Protocol: protocol, AutoDeliver: false, ConfigJson: "{}", CredentialsJson: null, OutputFormat: outputFormat);

    [Fact]
    public async Task WebhookDelivery_IsRefused_WhenThePlanDoesNotIncludeIt()
    {
        var h = BuildSuppliers(granted: null);

        var result = await h.Controller.UpsertDeliveryConfig(h.Supplier.Id, Delivery("http"), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.WebhookDelivery);
    }

    [Fact]
    public async Task WebhookDelivery_IsAllowed_WhenThePlanIncludesIt()
    {
        var h = BuildSuppliers(granted: BillingFeature.WebhookDelivery);

        var result = await h.Controller.UpsertDeliveryConfig(h.Supplier.Id, Delivery("http"), CancellationToken.None);

        AssertNot403(result, BillingFeature.WebhookDelivery);
    }

    [Theory]
    [InlineData("erp_erply")]
    [InlineData("erp_directo")]
    public async Task ErpConnectors_AreRefused_WhenThePlanDoesNotIncludeThem(string protocol)
    {
        var h = BuildSuppliers(granted: null);

        var result = await h.Controller.UpsertDeliveryConfig(h.Supplier.Id, Delivery(protocol), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.ErpConnectors);
    }

    [Fact]
    public async Task ErpConnectors_AreAllowed_OnEnterprise()
    {
        var h = BuildSuppliers(granted: BillingFeature.ErpConnectors);

        var result = await h.Controller.UpsertDeliveryConfig(h.Supplier.Id, Delivery("erp_erply"), CancellationToken.None);

        AssertNot403(result, BillingFeature.ErpConnectors);
    }

    [Fact]
    public async Task ErpConnector_OnAPlanWithWebhooksButNotErp_StillNamesTheErpGate()
    {
        // Guards the ordering inside RequiredFeatureForDeliveryConfig: an ERP protocol must
        // report the ERP (Enterprise) gate, never a cheaper one it happens to also fail.
        var h = BuildSuppliers(granted: BillingFeature.WebhookDelivery);

        var result = await h.Controller.UpsertDeliveryConfig(h.Supplier.Id, Delivery("erp_directo"), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.ErpConnectors);
    }

    [Fact]
    public async Task Cxml_OutputFormat_IsRefused_WhenThePlanDoesNotIncludeIt()
    {
        var h = BuildSuppliers(granted: null);

        var result = await h.Controller.UpsertDeliveryConfig(
            h.Supplier.Id, Delivery("sftp", outputFormat: "cxml"), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.Cxml);
    }

    [Fact]
    public async Task Cxml_OutputFormat_IsAllowed_FromOperationsUp()
    {
        var h = BuildSuppliers(granted: BillingFeature.Cxml);

        var result = await h.Controller.UpsertDeliveryConfig(
            h.Supplier.Id, Delivery("sftp", outputFormat: "cxml"), CancellationToken.None);

        AssertNot403(result, BillingFeature.Cxml);
    }

    [Fact]
    public async Task BulkMappingImport_IsRefused_WhenThePlanDoesNotIncludeIt()
    {
        var h = BuildSuppliers(granted: null);

        var result = await h.Controller.ImportMappings(h.Supplier.Id, CsvFile("buyer,supplier\nA-1,S-1\n"), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.BulkMapping, "bulk_mapping_import");
    }

    [Fact]
    public async Task BulkMappingImport_IsAllowed_FromOperationsUp()
    {
        var h = BuildSuppliers(granted: BillingFeature.BulkMapping);

        var result = await h.Controller.ImportMappings(h.Supplier.Id, CsvFile("buyer,supplier\nA-1,S-1\n"), CancellationToken.None);

        AssertNot403(result, BillingFeature.BulkMapping);
    }

    private static IFormFile CsvFile(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "mappings.csv");
    }

    // ═══ SupplierAcceptanceController: CustomSupplierRules ═══════════════════

    private static SupplierAcceptanceController BuildAcceptance(
        BillingFeature? granted, Mock<ISupplierAcceptanceService> svc)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(Guid.NewGuid());
        return new SupplierAcceptanceController(svc.Object, tenant.Object, BillingGranting(granted).Object);
    }

    [Fact]
    public async Task CustomSupplierRules_CreateVersion_IsRefused_WhenThePlanDoesNotIncludeThem()
    {
        var svc = new Mock<ISupplierAcceptanceService>();
        var controller = BuildAcceptance(granted: null, svc);

        var result = await controller.CreateVersion(
            Guid.NewGuid(),
            new CreateAcceptanceProfileRequest("sftp", "xml", new List<AcceptanceRuleDto>()),
            CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.CustomSupplierRules, "custom_supplier_rules");
        svc.Verify(s => s.CreateVersionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<AcceptanceRuleInput>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never, "a refused authoring call must not reach the service");
    }

    [Fact]
    public async Task CustomSupplierRules_Activate_IsRefused_WhenThePlanDoesNotIncludeThem()
    {
        var controller = BuildAcceptance(granted: null, new Mock<ISupplierAcceptanceService>());

        var result = await controller.Activate(Guid.NewGuid(), 2, CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.CustomSupplierRules, "custom_supplier_rules");
    }

    [Fact]
    public async Task CustomSupplierRules_ListVersions_StaysOpen()
    {
        var svc = new Mock<ISupplierAcceptanceService>();
        svc.Setup(s => s.ListVersionsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new List<SupplierAcceptanceProfile>());
        var controller = BuildAcceptance(granted: null, svc);

        var result = await controller.ListVersions(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>(
            "a downgraded org must still be able to see what its suppliers enforce");
    }

    // ═══ AuditController: AdvancedAudit ══════════════════════════════════════

    private static AuditController BuildAudit(BillingFeature? granted, ProcuLinkDbContext db, Guid orgId)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        return new AuditController(db, tenant.Object, BillingGranting(granted).Object);
    }

    [Fact]
    public async Task AdvancedAudit_OrgWideTrail_IsRefused_WhenThePlanDoesNotIncludeIt()
    {
        var db = NewDb();
        var controller = BuildAudit(granted: null, db, Guid.NewGuid());

        var result = await controller.GetAuditLog(ct: CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.AdvancedAudit, "advanced_audit");
    }

    [Fact]
    public async Task AdvancedAudit_OrgWideTrail_IsAllowed_FromOperationsUp()
    {
        var db = NewDb();
        var controller = BuildAudit(granted: BillingFeature.AdvancedAudit, db, Guid.NewGuid());

        var result = await controller.GetAuditLog(ct: CancellationToken.None);

        AssertNot403(result, BillingFeature.AdvancedAudit);
    }

    // ═══ SettingsController: EmailIngestion / SftpIngestion / S3Ingestion ════

    private sealed record SettingsHarness(SettingsController Controller, Supplier Supplier);

    private static SettingsHarness BuildSettings(BillingFeature? granted)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var config = Config();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var controller = new SettingsController(
            tenant.Object,
            new Mock<IEmailSettingsService>().Object,
            new PullIngressSettingsService(db, new DeliveryEncryptionService(config)),
            new Mock<IOrganisationSettingsService>().Object,
            BillingGranting(granted).Object,
            new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance),
            db,
            InboundAddressTestHarness.Create(db),
            config);

        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Acme", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        db.SaveChanges();

        return new SettingsHarness(controller, supplier);
    }

    [Fact]
    public async Task EmailIngestion_IsRefused_WhenThePlanDoesNotIncludeIt()
    {
        var h = BuildSettings(granted: null);

        var result = await h.Controller.UpdateEmail(
            new UpdateEmailSettingsRequest(true, "imap.example.com", 993, true, "buyer", "pw", "INBOX", h.Supplier.Id),
            CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.EmailIngestion, "email_ingestion");
    }

    [Fact]
    public async Task SftpIngestion_IsRefused_WhenThePlanDoesNotIncludeIt()
    {
        var h = BuildSettings(granted: null);

        var result = await h.Controller.UpdateSftp(
            new UpdateSftpIngressRequest(true, "sftp.example.com", 22, "buyer", "pw", "/in", h.Supplier.Id),
            CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.SftpIngestion, "sftp_ingestion");
    }

    [Fact]
    public async Task S3Ingestion_IsRefused_WhenThePlanDoesNotIncludeIt()
    {
        var h = BuildSettings(granted: null);

        var result = await h.Controller.UpdateS3(
            new UpdateS3IngressRequest(true, "orders", "in/", "eu-central-1", "AKIA", "sk", h.Supplier.Id),
            CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.S3Ingestion, "s3_ingestion");
    }

    [Fact]
    public async Task SftpIngestion_AlsoGatesTheSupplierCatalogPullSource()
    {
        var h = BuildSuppliers(granted: null);
        var encryption = new DeliveryEncryptionService(Config());
        var settings = new CatalogSourceSettingsService(h.Db, encryption, new Mock<IBackgroundJobClient>().Object);
        var guard = new OutboundRequestGuard(Config(), NullLogger<OutboundRequestGuard>.Instance);

        var result = await h.Controller.UpsertCatalogSource(
            h.Supplier.Id,
            new UpsertCatalogSourceRequest(
                Protocol: "sftp", Host: "sftp.example.com", Port: 22, Username: "buyer",
                Password: "pw", RemotePath: "/catalog.csv", FileFormat: "csv",
                SyncIntervalHours: 24, IsEnabled: true),
            settings, guard, CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.SftpIngestion, "catalog_sync");
    }

    // ═══ Sso ═════════════════════════════════════════════════════════════════

    [Fact]
    public void Sso_IsGatedAsPresentationMetadata_NotAsAServerRefusal()
    {
        // SSO is delivered by Clerk Enterprise Connections; ProcuLink's gate is the
        // BillingStatus.SsoAvailable flag that drives the Settings tab's available/upsell
        // state. The behavioural proof that the flag follows the plan lives in
        // StripeBillingServicePricingTests.GetStatus_{Enterprise|BelowEnterprise}_*; this
        // asserts the shared predicate those tests and the service both read.
        PlanConstants.PlanHasFeature(PlanConstants.Enterprise, BillingFeature.Sso).Should().BeTrue();
        PlanConstants.PlanHasFeature(PlanConstants.Distributor, BillingFeature.Sso).Should().BeFalse(
            "Distributor outranks Integration but is still not Enterprise");
    }

    // ═══ ConnectionsController: the VERSIONED delivery path ══════════════════
    //
    // A pinned order delivers through its connection REVISION, not through the live
    // delivery-config row. Gating only the live row left the whole thing bypassable: save a
    // draft revision with DeliveryProtocol = "http", publish it, and a Pilot org delivers by
    // webhook. Both paths now share DeliveryCapabilityGate.RequiredFeature.

    private static ConnectionsController BuildConnections(
        BillingFeature? granted, Mock<ISupplierConnectionService> svc)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(Guid.NewGuid());
        return new ConnectionsController(
            svc.Object, new Mock<IReplayService>().Object, tenant.Object, BillingGranting(granted).Object);
    }

    private static ConnectionRevisionBundleDto Bundle(string? protocol, string? outputFormat) => new(
        InputMappingJson: null, OutputMappingJson: null, OutputFormat: outputFormat,
        DeliveryProtocol: protocol, DeliveryConfigJson: null, DeliveryAutoDeliver: false,
        CredentialsRef: null, AcceptanceProfileId: null, AcceptanceVersionNo: null,
        CatalogMode: "live", ItemMappings: null);

    [Theory]
    [InlineData("http", null, "WebhookDelivery")]
    [InlineData("erp_erply", null, "ErpConnectors")]
    [InlineData("sftp", "cxml", "Cxml")]
    public async Task ConnectionRevisionDraft_CannotSelectAGatedChannelOrFormat(
        string protocol, string? format, string expectedFeature)
    {
        var feature = Enum.Parse<BillingFeature>(expectedFeature);
        var svc = new Mock<ISupplierConnectionService>();
        var controller = BuildConnections(granted: null, svc);

        var result = await controller.CreateDraft(
            Guid.NewGuid(),
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle: Bundle(protocol, format)),
            CancellationToken.None);

        Assert403NamingTheRightPlan(result, feature);
        svc.Verify(c => c.CreateDraftAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ConnectionRevisionDraftInput?>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never, "a refused draft must never be persisted");
    }

    [Fact]
    public async Task ConnectionRevisionUpdate_IsGatedToo_SoTheCreateGateIsNotBypassable()
    {
        var svc = new Mock<ISupplierConnectionService>();
        var controller = BuildConnections(granted: null, svc);

        var result = await controller.UpdateDraft(
            Guid.NewGuid(), Guid.NewGuid(),
            new UpdateConnectionRevisionRequest(Bundle("http", null)),
            CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.WebhookDelivery);
    }

    [Fact]
    public async Task ConnectionRevisionDraft_WithAStockChannel_IsNotGated()
    {
        var svc = new Mock<ISupplierConnectionService>();
        var controller = BuildConnections(granted: null, svc);

        var result = await controller.CreateDraft(
            Guid.NewGuid(),
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle: Bundle("sftp", "xml")),
            CancellationToken.None);

        (result as ObjectResult)?.StatusCode.Should().NotBe(403,
            "sftp + xml is included on every paid plan");
    }

    [Fact]
    public void BothDeliveryWritePaths_ShareOneGateDecision()
    {
        // The live row and the versioned revision must never disagree about what is gated —
        // two copies of this logic is how one path silently stops enforcing.
        DeliveryCapabilityGate.RequiredFeatures("HTTP", null).Select(r => r.Feature)
            .Should().ContainSingle().Which.Should().Be(BillingFeature.WebhookDelivery,
                "comparison must be case-insensitive");
        DeliveryCapabilityGate.RequiredFeatures("  erp_directo  ", null).Select(r => r.Feature)
            .Should().ContainSingle().Which.Should().Be(BillingFeature.ErpConnectors,
                "comparison must trim");
        DeliveryCapabilityGate.RequiredFeatures("sftp", "xml").Should().BeEmpty();
        DeliveryCapabilityGate.RequiredFeatures(null, null).Should().BeEmpty();
    }

    [Fact]
    public void ChannelAndFormat_AreBothRequired_NotEitherOr()
    {
        // The channel and the output format are sold separately, so a config can need BOTH.
        // An earlier version returned the first match and checked only that, which let an
        // http+cxml config through on Growth: WebhookDelivery matched, passed, and the cXML
        // requirement (Operations) was never evaluated.
        DeliveryCapabilityGate.RequiredFeatures("http", "cxml").Select(r => r.Feature)
            .Should().BeEquivalentTo(new[] { BillingFeature.WebhookDelivery, BillingFeature.Cxml });
    }

    [Fact]
    public async Task MixedTierConfig_IsRefused_WhenOnlyTheCHEAPERFeatureIsHeld()
    {
        // THE bypass this guards: a Growth org has WebhookDelivery but not Cxml. Saving
        // http + cxml must still be refused, and must name the Operations gate — telling them
        // to buy Growth would send them to a plan that still would not let them save.
        var h = BuildSuppliers(granted: BillingFeature.WebhookDelivery);

        var result = await h.Controller.UpsertDeliveryConfig(
            h.Supplier.Id, Delivery("http", outputFormat: "cxml"), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.Cxml);
    }

    [Fact]
    public async Task MixedTierConfig_IsRefused_WhenOnlyTheDEARERFeatureIsHeld()
    {
        // The mirror case: Cxml held, WebhookDelivery not. Still refused, naming the webhook gate.
        var h = BuildSuppliers(granted: BillingFeature.Cxml);

        var result = await h.Controller.UpsertDeliveryConfig(
            h.Supplier.Id, Delivery("http", outputFormat: "cxml"), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.WebhookDelivery);
    }

    // ── Editing an EXISTING configuration ────────────────────────────────────
    //
    // Found in production 2026-08-17. Re-saving a supplier's delivery config is the only way to
    // rotate a credential, and the only migration path off the unbound (version 1) envelopes,
    // which cannot be rebound automatically. The gate ran unconditionally, so an org whose plan
    // no longer covered an existing configuration could not save it at all — a billing check
    // standing in front of a security action, and the longer it stood the more valuable the
    // unrotatable secret became.

    private static SupplierDeliveryConfig Stored(string protocol, string outputFormat) =>
        new() { Id = Guid.NewGuid(), Protocol = protocol, OutputFormat = outputFormat };

    [Fact]
    public async Task ExistingCxmlConfig_CanBeReSaved_WhenThePlanNoLongerCoversIt()
    {
        // Granted NOTHING, yet the stored row is already http + cxml. Saving it again introduces
        // no capability: it already delivers this way, because delivery never consults the gate.
        var h = BuildSuppliers(granted: null, existingDelivery: Stored("http", "cxml"));

        var result = await h.Controller.UpsertDeliveryConfig(
            h.Supplier.Id, Delivery("http", outputFormat: "cxml"), CancellationToken.None);

        AssertNot403(result, BillingFeature.Cxml);
    }

    [Fact]
    public async Task CreatingTheSameCxmlConfig_IsStillRefused_WhenNothingIsStoredYet()
    {
        // Anti-vacuity control for the test above, and the whole point of the change: identical
        // request, identical (empty) entitlements, ONLY the stored row differs. If this ever goes
        // green the edit gate has stopped gating creation and the exemption has swallowed the rule.
        var h = BuildSuppliers(granted: null, existingDelivery: null);

        var result = await h.Controller.UpsertDeliveryConfig(
            h.Supplier.Id, Delivery("http", outputFormat: "cxml"), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.Cxml);
    }

    [Fact]
    public async Task EditingAnExistingConfig_StillRefusesACapabilityItWouldINTRODUCE()
    {
        // An sftp + xml row requires nothing. Turning it into http + cxml introduces both, so the
        // exemption must not apply — otherwise "has any config at all" would be a free upgrade.
        //
        // Both are unmet, and the refusal names Cxml (Operations) rather than WebhookDelivery
        // (Growth): the gate reports the WORST unmet tier, because naming the cheaper one would
        // send the customer to a plan that still would not let them save.
        var h = BuildSuppliers(granted: null, existingDelivery: Stored("sftp", "xml"));

        var result = await h.Controller.UpsertDeliveryConfig(
            h.Supplier.Id, Delivery("http", outputFormat: "cxml"), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.Cxml);
    }

    [Fact]
    public async Task PartiallyOverlappingEdit_ExemptsOnlyTheCapabilityAlreadyStored()
    {
        // The nuance case. Stored http + xml, so WebhookDelivery is already in force and exempt.
        // The edit adds cxml, which is NOT stored — that half must still be refused, and must name
        // the Operations gate rather than the webhook one the org is being forgiven for.
        var h = BuildSuppliers(granted: null, existingDelivery: Stored("http", "xml"));

        var result = await h.Controller.UpsertDeliveryConfig(
            h.Supplier.Id, Delivery("http", outputFormat: "cxml"), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.Cxml);
    }

    [Fact]
    public async Task FirstUnmetForEdit_WithNoStoredRow_IsExactlyFirstUnmet()
    {
        // The creation path must not merely "look" unchanged — it must BE the old decision, for
        // every combination the gate knows about, or a future edit to one drifts from the other.
        var billing = BillingGranting(null).Object;
        var orgId = Guid.NewGuid();

        foreach (var (protocol, format) in new[]
                 {
                     ("http", "cxml"), ("http", "xml"), ("erp_erply", "xml"),
                     ("erp_directo", "cxml"), ("sftp", "xml"), ("email", "csv"),
                 })
        {
            var unconditional = await DeliveryCapabilityGate.FirstUnmetAsync(
                billing, orgId, protocol, format, CancellationToken.None);
            var forEdit = await DeliveryCapabilityGate.FirstUnmetForEditAsync(
                billing, orgId, protocol, format, null, null, CancellationToken.None);

            forEdit.Should().Be(unconditional,
                $"with nothing stored, editing {protocol}/{format} must decide exactly as creating it");
        }
    }

    [Fact]
    public async Task MixedTierConfig_OnTheVersionedPath_IsRefusedToo()
    {
        var svc = new Mock<ISupplierConnectionService>();
        var controller = BuildConnections(granted: BillingFeature.WebhookDelivery, svc);

        var result = await controller.CreateDraft(
            Guid.NewGuid(),
            new CreateConnectionRevisionRequest(CloneFromActive: false, Bundle: Bundle("http", "cxml")),
            CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.Cxml);
    }

    [Fact]
    public async Task WhenSeveralGatesAreUnmet_The403NamesTheHIGHESTTierOne()
    {
        // ERP (Enterprise) + cXML (Operations), nothing held. Naming Operations would send the
        // customer to a plan that still refuses the save.
        var h = BuildSuppliers(granted: null);

        var result = await h.Controller.UpsertDeliveryConfig(
            h.Supplier.Id, Delivery("erp_erply", outputFormat: "cxml"), CancellationToken.None);

        Assert403NamingTheRightPlan(result, BillingFeature.ErpConnectors);
    }

    // ═══ What must STAY open ═════════════════════════════════════════════════

    [Theory]
    [InlineData("sftp")]
    [InlineData("ftps")]
    [InlineData("email")]
    public async Task StockDeliveryChannels_AreNotGated_OnAnyPaidPlan(string protocol)
    {
        // Growth is sold "all channels". Only the webhook and ERP protocols are tiered,
        // so a plain SFTP/FTPS/email delivery config must save with NO feature granted.
        var h = BuildSuppliers(granted: null);

        var result = await h.Controller.UpsertDeliveryConfig(h.Supplier.Id, Delivery(protocol), CancellationToken.None);

        (result as ObjectResult)?.StatusCode.Should().NotBe(403,
            $"{protocol} delivery is included on every paid plan — gating it would contradict the pricing cards");
    }

    [Fact]
    public async Task StockOutputFormats_AreNotGated()
    {
        var h = BuildSuppliers(granted: null);

        foreach (var format in new[] { "xml", "csv", "json", "ubl", "x12" })
        {
            var result = await h.Controller.UpsertDeliveryConfig(
                h.Supplier.Id, Delivery("sftp", outputFormat: format), CancellationToken.None);

            (result as ObjectResult)?.StatusCode.Should().NotBe(403,
                $"{format} is not sold as a tier differentiator — only cXML is");
        }
    }
}
