using ProcuLink.TestSupport;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
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
/// WP-11 defect #1 — <b>a 403 must never name a plan the gate does not require</b>.
///
/// <para>Four gated endpoints returned an error code containing the word
/// <c>integration</c> while the feature they gate on
/// (<see cref="BillingFeature.EmailIngestion"/> / <see cref="BillingFeature.SftpIngestion"/> /
/// <see cref="BillingFeature.S3Ingestion"/>) has a minimum plan of <b>Growth</b>
/// (<c>PlanConstants.MinimumPlan</c>). A customer reading the code was told to buy a
/// €999 tier to unlock something their €149 tier already includes.</para>
///
/// <para>These tests assert the code against <see cref="PlanConstants.GetMinimumPlan"/>
/// — the gate's own source of truth — rather than against a hardcoded plan name, so the
/// codes stay honest automatically if a minimum plan is ever re-tiered. That is the
/// actual fix: the previous codes could drift because they were free-form strings.</para>
/// </summary>
public class BillingGateErrorCodeTests
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

    /// <summary>
    /// The plan a 403 for <paramref name="feature"/> is allowed to name — read straight
    /// off the gate table so the expectation cannot drift from the gate.
    /// </summary>
    private static string MinPlan(BillingFeature feature) => PlanConstants.GetMinimumPlan(feature)!;

    private static string ErrorOf(IActionResult result)
    {
        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(403);
        return (string)((dynamic)status.Value!).error;
    }

    // ── SettingsController: email / sftp / s3 ────────────────────────────────

    private sealed record SettingsHarness(SettingsController Controller, Guid OrgId, Supplier Supplier);

    private static SettingsHarness BuildSettings()
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var config = Config();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        // Every feature denied — we are only interested in the 403 body.
        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        var encryption = new DeliveryEncryptionService(config);
        var controller = new SettingsController(
            tenant.Object,
            new Mock<IEmailSettingsService>().Object,
            new PullIngressSettingsService(db, encryption),
            new Mock<IOrganisationSettingsService>().Object,
            billing.Object,
            new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance),
            db,
            InboundAddressTestHarness.Create(db),
            config);

        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Acme", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        db.SaveChanges();

        return new SettingsHarness(controller, orgId, supplier);
    }

    [Fact]
    public async Task EmailIngestion403_NamesTheMinimumPlanTheGateActuallyRequires()
    {
        var h = BuildSettings();

        var result = await h.Controller.UpdateEmail(
            new UpdateEmailSettingsRequest(
                Enabled: true, Host: "imap.example.com", Port: 993, UseSsl: true,
                Username: "buyer", Password: "secret", Folder: "INBOX",
                DefaultSupplierId: h.Supplier.Id),
            CancellationToken.None);

        ErrorOf(result).Should().Be(
            $"email_ingestion_requires_{MinPlan(BillingFeature.EmailIngestion)}",
            "the 403 must name the plan the gate really requires — never a higher, more expensive one");
    }

    [Fact]
    public async Task SftpIngestion403_NamesTheMinimumPlanTheGateActuallyRequires()
    {
        var h = BuildSettings();

        var result = await h.Controller.UpdateSftp(
            new UpdateSftpIngressRequest(
                Enabled: true, Host: "sftp.example.com", Port: 22, Username: "buyer",
                Password: "secret", RemoteDirectory: "/in", DefaultSupplierId: h.Supplier.Id),
            CancellationToken.None);

        ErrorOf(result).Should().Be(
            $"sftp_ingestion_requires_{MinPlan(BillingFeature.SftpIngestion)}");
    }

    [Fact]
    public async Task S3Ingestion403_NamesTheMinimumPlanTheGateActuallyRequires()
    {
        var h = BuildSettings();

        var result = await h.Controller.UpdateS3(
            new UpdateS3IngressRequest(
                Enabled: true, BucketName: "orders", KeyPrefix: "in/", Region: "eu-central-1",
                AccessKeyId: "AKIA", SecretKey: "secret", DefaultSupplierId: h.Supplier.Id),
            CancellationToken.None);

        ErrorOf(result).Should().Be(
            $"s3_ingestion_requires_{MinPlan(BillingFeature.S3Ingestion)}");
    }

    // ── SuppliersController: catalog sync source ─────────────────────────────

    [Fact]
    public async Task CatalogSync403_NamesTheMinimumPlanTheGateActuallyRequires()
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var config = Config();

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(false);

        var controller = new SuppliersController(
            new Mock<ISupplierProfileRepository>().Object,
            new Mock<IItemMappingService>().Object,
            db,
            tenant.Object,
            billing.Object,
            new Mock<IPoMappingService>().Object,
            new Mock<IDeliveryConfigService>().Object,
            new Mock<IDeliveryService>().Object,
            new TestDoubles.FakeAnalyticsService(),
            new Mock<IFileStorageService>().Object,
            new SourceColumnExtractor(),
            new ProcuLink.Api.Services.StarterTemplates.StarterTemplateService(),
            new SupplierCatalogService(db),
            new Mock<ISupplierConnectionService>().Object);

        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Acme", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        db.SaveChanges();

        var encryption = new DeliveryEncryptionService(config);
        var settings = new CatalogSourceSettingsService(db, encryption, new Mock<IBackgroundJobClient>().Object);
        var guard = new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);

        var result = await controller.UpsertCatalogSource(
            supplier.Id,
            new UpsertCatalogSourceRequest(
                Protocol: "sftp",
                Host: "sftp.example.com",
                Port: 22,
                Username: "buyer",
                Password: "secret",
                RemotePath: "/catalog.csv",
                FileFormat: "csv",
                SyncIntervalHours: 24,
                IsEnabled: true),
            settings, guard, CancellationToken.None);

        ErrorOf(result).Should().Be(
            $"catalog_sync_requires_{MinPlan(BillingFeature.SftpIngestion)}",
            "catalog sync gates on SftpIngestion, so the code must name that feature's minimum plan");
    }
}
