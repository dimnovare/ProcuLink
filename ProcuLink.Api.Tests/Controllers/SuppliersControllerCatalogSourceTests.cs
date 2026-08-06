using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Repositories;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Catalog;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Transform.Detection;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// BE-3 gate for the catalog pull-source endpoints on <see cref="SuppliersController"/>:
/// masked GET (never ciphertext/plaintext), PUT password keep/clear/replace semantics,
/// billing-gated enablement, save-time SSRF pre-check, interval clamp, the
/// enable-transition enqueue with its M1-residual dedupe guard, foreign-supplier 404s,
/// and the read-only test-fetch passthrough. Real settings service + real DbContext;
/// Hangfire client mocked to observe enqueues.
/// </summary>
public class SuppliersControllerCatalogSourceTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IConfiguration Config(bool allowPrivate = true) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delivery:EncryptionKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            ["Delivery:AllowPrivateNetworkTargets"] = allowPrivate ? "true" : "false",
        })
        .Build();

    private sealed record Harness(
        SuppliersController Controller,
        ProcuLinkDbContext Db,
        Guid OrgId,
        Supplier Supplier,
        CatalogSourceSettingsService Settings,
        OutboundRequestGuard Guard,
        DeliveryEncryptionService Encryption,
        Mock<IBackgroundJobClient> Jobs,
        Mock<IBillingService> Billing);

    private static Harness Build(bool allowPrivate = true, bool hasFeature = true)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var config = Config(allowPrivate);

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(orgId, BillingFeature.SftpIngestion, It.IsAny<CancellationToken>()))
               .ReturnsAsync(hasFeature);

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
            new Mock<ProcuLink.Core.Services.ISupplierConnectionService>().Object);

        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Acme", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        db.SaveChanges();

        var encryption = new DeliveryEncryptionService(config);
        var jobs = new Mock<IBackgroundJobClient>();
        var settings = new CatalogSourceSettingsService(db, encryption, jobs.Object);
        var guard = new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);

        return new Harness(controller, db, orgId, supplier, settings, guard, encryption, jobs, billing);
    }

    private static UpsertCatalogSourceRequest Request(
        string protocol = "sftp",
        string host = "files.example.com",
        int port = 0,
        string? username = "catalog",
        string? password = "secret",
        string remotePath = "/exports/catalog.csv",
        string? fileFormat = null,
        int? intervalHours = null,
        bool isEnabled = false)
        => new(protocol, host, port, username, password, remotePath, fileFormat, intervalHours, isEnabled);

    private static UpsertCatalogSourceRequest HttpRequest(
        string protocol = "https",
        string? url = "https://supplier.example/catalog.json",
        string? authMethod = "none",
        CatalogHttpAuthConfig? authConfig = null,
        string? httpMethod = null,
        bool isEnabled = false,
        string? fileFormat = null)
        => new(protocol, Host: "", Port: 0, Username: null, Password: null, RemotePath: "",
               fileFormat, SyncIntervalHours: null, isEnabled,
               Url: url, AuthMethod: authMethod, AuthConfig: authConfig, HttpMethod: httpMethod);

    // ── GET masking ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_NoSource_ReturnsNullSource()
    {
        var h = Build();

        var result = await h.Controller.GetCatalogSource(h.Supplier.Id, h.Settings, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ((object?)((dynamic)ok.Value!).source).Should().BeNull();
    }

    [Fact]
    public async Task Get_MasksPassword_NeverReturnsCiphertextOrPlaintext()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(password: "super-secret-password"),
            h.Settings, h.Guard, CancellationToken.None);

        var result = await h.Controller.GetCatalogSource(h.Supplier.Id, h.Settings, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var source = (CatalogSourceResponse)((dynamic)ok.Value!).source;
        source.HasPassword.Should().BeTrue();
        source.PasswordDisplay.Should().Be("********");

        // The response record carries no other secret-bearing member; prove the two
        // string fields that COULD leak don't.
        var stored = await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync();
        stored.EncryptedPassword.Should().NotBeNullOrEmpty();
        source.PasswordDisplay.Should().NotBe(stored.EncryptedPassword);
        System.Text.Json.JsonSerializer.Serialize(source)
            .Should().NotContain("super-secret-password")
            .And.NotContain(stored.EncryptedPassword);
    }

    // ── PUT password semantics: keep / clear / replace ────────────────────────

    [Fact]
    public async Task Put_NullPassword_KeepsStoredSecret()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(password: "original"),
            h.Settings, h.Guard, CancellationToken.None);
        var originalCipher = (await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync()).EncryptedPassword;

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(password: null),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var stored = await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync();
        stored.EncryptedPassword.Should().Be(originalCipher, "null means keep");
        h.Encryption.Decrypt(stored.EncryptedPassword!).Should().Be("original");
    }

    [Fact]
    public async Task Put_EmptyPassword_ClearsSecret_ForFtp()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(protocol: "ftp", password: "original"),
            h.Settings, h.Guard, CancellationToken.None);

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            Request(protocol: "ftp", username: null, password: ""),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var stored = await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync();
        stored.EncryptedPassword.Should().BeNull("empty string means clear (anonymous ftp)");
    }

    [Fact]
    public async Task Put_NewPassword_ReEncrypts()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(password: "first"),
            h.Settings, h.Guard, CancellationToken.None);

        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(password: "second"),
            h.Settings, h.Guard, CancellationToken.None);

        var stored = await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync();
        h.Encryption.Decrypt(stored.EncryptedPassword!).Should().Be("second");
    }

    [Fact]
    public async Task Put_SftpWithoutAnyPassword_Returns400()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(password: ""),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── validation ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("gopher")] // http/https are now valid (catalog HTTP pull v2) — use a still-invalid scheme
    [InlineData("scp")]
    [InlineData("")]
    public async Task Put_InvalidProtocol_Returns400(string protocol)
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(protocol: protocol),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Put_SftpWithoutUsername_Returns400()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(username: null),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Put_IntervalIsClamped_To1Through336()
    {
        var h = Build();

        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(intervalHours: 10_000),
            h.Settings, h.Guard, CancellationToken.None);
        (await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync()).SyncIntervalHours.Should().Be(336);

        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(intervalHours: -5),
            h.Settings, h.Guard, CancellationToken.None);
        (await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync()).SyncIntervalHours.Should().Be(1);
    }

    [Fact]
    public async Task Put_DefaultsPortByProtocol()
    {
        var h = Build();

        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(protocol: "sftp", port: 0),
            h.Settings, h.Guard, CancellationToken.None);
        (await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync()).Port.Should().Be(22);

        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(protocol: "ftps", port: 0),
            h.Settings, h.Guard, CancellationToken.None);
        (await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync()).Port.Should().Be(21);
    }

    // ── billing gate ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_EnableWithoutFeature_Returns403_WithUpgradeShape()
    {
        var h = Build(hasFeature: false);

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: true),
            h.Settings, h.Guard, CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(403);
        string error = ((dynamic)status.Value!).error;
        // WP-11: was pinned to "catalog_sync_requires_integration". Catalog sync gates on
        // SftpIngestion, whose minimum is Growth — the old expectation defended a 403 that
        // named a tier six times the price of the one that actually unlocks the feature.
        error.Should().Be($"catalog_sync_requires_{PlanConstants.GetMinimumPlan(BillingFeature.SftpIngestion)}");
    }

    [Fact]
    public async Task Put_DisabledSave_WithoutFeature_IsAllowed()
    {
        var h = Build(hasFeature: false);

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: false),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("configuring while disabled must not be billing-gated");
    }

    // ── save-time SSRF pre-check ──────────────────────────────────────────────

    [Fact]
    public async Task Put_PrivateHost_Returns400_HostNotAllowed_AndPersistsNothing()
    {
        var h = Build(allowPrivate: false);

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(host: "10.1.2.3"),
            h.Settings, h.Guard, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        string error = ((dynamic)bad.Value!).error;
        error.Should().Be("host_not_allowed");
        (await h.Db.SupplierCatalogSources.CountAsync()).Should().Be(0);
    }

    // ── enable-transition enqueue + M1-residual dedupe ────────────────────────

    private static void VerifyEnqueues(Mock<IBackgroundJobClient> jobs, Times times) =>
        jobs.Verify(c => c.Create(
            It.Is<Job>(j => j.Type == typeof(CatalogSyncSourceJob)),
            It.IsAny<IState>()), times);

    [Fact]
    public async Task Put_EnableTransition_EnqueuesExactlyOnce()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: false),
            h.Settings, h.Guard, CancellationToken.None);
        VerifyEnqueues(h.Jobs, Times.Never());

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: true),
            h.Settings, h.Guard, CancellationToken.None);

        VerifyEnqueues(h.Jobs, Times.Once());
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        bool enqueued = ((dynamic)ok.Value!).syncEnqueued;
        enqueued.Should().BeTrue();
    }

    [Fact]
    public async Task Put_AlreadyEnabled_DoesNotEnqueueAgain()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: true),
            h.Settings, h.Guard, CancellationToken.None);
        VerifyEnqueues(h.Jobs, Times.Once());

        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: true),
            h.Settings, h.Guard, CancellationToken.None);

        VerifyEnqueues(h.Jobs, Times.Once()); // still once — true→true is not a transition
    }

    [Fact]
    public async Task Put_EnableTransition_DedupesWhenRunning()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: false),
            h.Settings, h.Guard, CancellationToken.None);
        var source = await h.Db.SupplierCatalogSources.SingleAsync();
        source.LastSyncStatus = "running";
        source.LastSyncAt = DateTime.UtcNow.AddHours(-1); // not recent — 'running' alone must dedupe
        await h.Db.SaveChangesAsync();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: true),
            h.Settings, h.Guard, CancellationToken.None);

        VerifyEnqueues(h.Jobs, Times.Never());
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        bool enqueued = ((dynamic)ok.Value!).syncEnqueued;
        enqueued.Should().BeFalse();
    }

    [Fact]
    public async Task Put_EnableTransition_DedupesWhenSyncedWithinFiveMinutes()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: false),
            h.Settings, h.Guard, CancellationToken.None);
        var source = await h.Db.SupplierCatalogSources.SingleAsync();
        source.LastSyncStatus = "ok";
        source.LastSyncAt = DateTime.UtcNow.AddMinutes(-2);
        await h.Db.SaveChangesAsync();

        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: true),
            h.Settings, h.Guard, CancellationToken.None);

        VerifyEnqueues(h.Jobs, Times.Never());
    }

    [Fact]
    public async Task Put_Disable_DoesNotEnqueue()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: true),
            h.Settings, h.Guard, CancellationToken.None);
        VerifyEnqueues(h.Jobs, Times.Once());

        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(isEnabled: false),
            h.Settings, h.Guard, CancellationToken.None);

        VerifyEnqueues(h.Jobs, Times.Once()); // unchanged — disabling never enqueues
    }

    // ── tenancy: foreign supplier 404 on every route ──────────────────────────

    [Fact]
    public async Task AllRoutes_ForeignSupplier_Return404()
    {
        var h = Build();
        var foreign = new Supplier { Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), Name = "Foreign", CreatedAt = DateTime.UtcNow };
        h.Db.Suppliers.Add(foreign);
        await h.Db.SaveChangesAsync();

        (await h.Controller.GetCatalogSource(foreign.Id, h.Settings, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        (await h.Controller.UpsertCatalogSource(foreign.Id, Request(), h.Settings, h.Guard, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        (await h.Controller.DeleteCatalogSource(foreign.Id, h.Settings, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        (await h.Controller.TestFetchCatalogSource(foreign.Id, new Mock<ICatalogPullService>(MockBehavior.Strict).Object, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    // ── delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesSource_AndReportsHonestly()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id, Request(),
            h.Settings, h.Guard, CancellationToken.None);

        var result = await h.Controller.DeleteCatalogSource(h.Supplier.Id, h.Settings, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        bool deleted = ((dynamic)ok.Value!).deleted;
        deleted.Should().BeTrue();
        (await h.Db.SupplierCatalogSources.CountAsync()).Should().Be(0);

        // Deleting again reports false — no source existed.
        var second = await h.Controller.DeleteCatalogSource(h.Supplier.Id, h.Settings, CancellationToken.None);
        bool deletedAgain = ((dynamic)(second as OkObjectResult)!.Value!).deleted;
        deletedAgain.Should().BeFalse();
    }

    // ── test-fetch passthrough (BE-5 controller leg) ──────────────────────────

    [Fact]
    public async Task TestFetch_ReturnsServiceResult()
    {
        var h = Build();
        var pull = new Mock<ICatalogPullService>();
        var expected = CatalogTestFetchResult.Failure("No catalog source is configured for this supplier.");
        pull.Setup(p => p.TestFetchAsync(h.OrgId, h.Supplier.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await h.Controller.TestFetchCatalogSource(h.Supplier.Id, pull.Object, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(expected);
    }

    [Fact]
    public void TestFetch_IsRateLimited_WithUploadPolicy()
    {
        var method = typeof(SuppliersController).GetMethod(nameof(SuppliersController.TestFetchCatalogSource))!;
        var attr = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute), false)
            .Cast<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()
            .Single();
        attr.PolicyName.Should().Be("upload");
    }

    // ── HTTP(S) catalog pull (plan 2026-06-12 v2 — B5/B6) ─────────────────────

    [Fact]
    public async Task Put_Http_SavesUrlAndAuthMethod()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(authMethod: "bearer", authConfig: new CatalogHttpAuthConfig(BearerToken: "tok-abc")),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var stored = await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync();
        stored.Protocol.Should().Be("https");
        stored.Url.Should().Be("https://supplier.example/catalog.json");
        stored.AuthMethod.Should().Be("bearer");
        stored.HttpMethod.Should().Be("GET");
        stored.AuthConfigEncrypted.Should().NotBeNullOrEmpty();
        // The secret is encrypted, not stored as plaintext.
        stored.AuthConfigEncrypted.Should().NotContain("tok-abc");
        h.Encryption.Decrypt(stored.AuthConfigEncrypted!).Should().Contain("tok-abc");
    }

    [Fact]
    public async Task Get_Http_MasksAuthConfig_NeverReturnsSecretOrCiphertext()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(authMethod: "apikey",
                authConfig: new CatalogHttpAuthConfig(ApiKeyHeader: "X-Api-Key", ApiKeyValue: "super-secret-key")),
            h.Settings, h.Guard, CancellationToken.None);

        var result = await h.Controller.GetCatalogSource(h.Supplier.Id, h.Settings, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var source = (CatalogSourceResponse)((dynamic)ok.Value!).source;
        source.AuthMethod.Should().Be("apikey");
        source.HasAuthConfig.Should().BeTrue();
        source.Url.Should().Be("https://supplier.example/catalog.json");

        var stored = await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync();
        // Serializing the masked response must leak NEITHER the plaintext secret NOR the ciphertext.
        System.Text.Json.JsonSerializer.Serialize(source)
            .Should().NotContain("super-secret-key")
            .And.NotContain(stored.AuthConfigEncrypted!);
    }

    [Fact]
    public async Task Put_Http_NullAuthConfig_KeepsStoredSecret()
    {
        var h = Build();
        await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(authMethod: "bearer", authConfig: new CatalogHttpAuthConfig(BearerToken: "original-token")),
            h.Settings, h.Guard, CancellationToken.None);
        var originalCipher = (await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync()).AuthConfigEncrypted;

        // Re-save with no AuthConfig (null = keep).
        await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(authMethod: "bearer", authConfig: null),
            h.Settings, h.Guard, CancellationToken.None);

        var stored = await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync();
        stored.AuthConfigEncrypted.Should().Be(originalCipher, "null AuthConfig means keep");
        h.Encryption.Decrypt(stored.AuthConfigEncrypted!).Should().Contain("original-token");
    }

    [Fact]
    public async Task Put_Http_UrlWithUserInfo_Returns400_CredentialsInUrlNotAllowed()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(url: "https://user:pass@supplier.example/catalog.json"),
            h.Settings, h.Guard, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        string error = ((dynamic)bad.Value!).error;
        error.Should().Be("credentials_in_url_not_allowed");
        (await h.Db.SupplierCatalogSources.CountAsync()).Should().Be(0, "nothing is persisted on a rejected save");
    }

    [Fact]
    public async Task Put_Http_MissingUrl_Returns400()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(url: null),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Put_Http_NonHttpScheme_Returns400()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(url: "ftp://supplier.example/catalog.json"),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Put_Http_PrivateUrl_Returns400_HostNotAllowed()
    {
        var h = Build(allowPrivate: false);

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(url: "http://169.254.169.254/latest/meta-data/"),
            h.Settings, h.Guard, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        string error = ((dynamic)bad.Value!).error;
        error.Should().Be("host_not_allowed");
        (await h.Db.SupplierCatalogSources.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Put_Http_PostMethod_Returns400()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(httpMethod: "POST"),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>("v2 supports GET only");
    }

    [Fact]
    public async Task Put_Http_JsonFileFormat_IsAccepted()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(fileFormat: "json"),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        (await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync()).FileFormat.Should().Be("json");
    }

    // ── Transport security (TLS) ──────────────────────────────────────────────

    /// <summary>
    /// A catalog pull sends the configured auth header — API key, bearer token, or Basic — on every
    /// request. Over plain http that credential is readable on the wire along with the whole price
    /// list.
    /// </summary>
    [Fact]
    public async Task Put_Http_CleartextUrl_Returns400_RequiresTls()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(url: "http://supplier.example/catalog.json"),
            h.Settings, h.Guard, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ((string)((dynamic)bad.Value!).error).Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        ((string)((dynamic)bad.Value!).message).Should().Contain("https://");
        (await h.Db.SupplierCatalogSources.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Put_Http_HttpsUrl_IsStillAccepted()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(url: "https://supplier.example/catalog.json"),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData("http://localhost:9000/catalog.json")]
    [InlineData("http://127.0.0.1:53412/catalog.json")]
    public async Task Put_Http_LoopbackCleartextUrl_IsAcceptedForLocalDevelopment(string url)
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(url: url),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    /// <summary>
    /// The OAuth2 client-credentials exchange POSTs the client id AND client secret to tokenUrl.
    /// A cleartext tokenUrl therefore leaks the secret itself, not merely the catalog it protects.
    /// It was never inspected at save — only at fetch time, and then only by the SSRF guard.
    /// </summary>
    [Fact]
    public async Task Put_Http_CleartextOAuthTokenUrl_Returns400_RequiresTls()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(authMethod: "oauth2_client_credentials", authConfig: new CatalogHttpAuthConfig(
                TokenUrl: "http://auth.supplier.example/oauth/token",
                ClientId: "client-abc",
                ClientSecret: "hunter2")),
            h.Settings, h.Guard, CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ((string)((dynamic)bad.Value!).error).Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        ((string)((dynamic)bad.Value!).message).Should().NotContain("hunter2");
        (await h.Db.SupplierCatalogSources.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Put_Http_HttpsOAuthTokenUrl_IsAccepted()
    {
        var h = Build();

        var result = await h.Controller.UpsertCatalogSource(h.Supplier.Id,
            HttpRequest(authMethod: "oauth2_client_credentials", authConfig: new CatalogHttpAuthConfig(
                TokenUrl: "https://auth.supplier.example/oauth/token",
                ClientId: "client-abc",
                ClientSecret: "shh")),
            h.Settings, h.Guard, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
