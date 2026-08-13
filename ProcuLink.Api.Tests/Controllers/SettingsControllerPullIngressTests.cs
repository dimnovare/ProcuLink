using ProcuLink.TestSupport;
using FluentAssertions;
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
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Organisation;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Gate for the SFTP and S3/R2 pull-ingress settings endpoints on
/// <see cref="SettingsController"/> (the GET/PUT that the frontend's
/// getSftpSettings/updateSftpSettings/getS3Settings/updateS3Settings clients call).
/// Mirrors <see cref="SuppliersControllerCatalogSourceTests"/>: real
/// <see cref="PullIngressSettingsService"/> + real DbContext + real
/// <see cref="OutboundRequestGuard"/>; billing/email/org-settings services mocked.
/// Covers: masked GET (never ciphertext/plaintext), PUT persists + encrypts,
/// blank-secret-keeps-existing, billing-gated enablement (403), required-field +
/// missing-secret 400s, save-time SSRF pre-check, and invalid default-supplier reject.
/// </summary>
public class SettingsControllerPullIngressTests
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
        SettingsController Controller,
        ProcuLinkDbContext Db,
        Guid OrgId,
        Supplier Supplier,
        DeliveryEncryptionService Encryption,
        Mock<IBillingService> Billing);

    private static Harness Build(bool allowPrivate = true, bool hasSftp = true, bool hasS3 = true)
    {
        var db = NewDb();
        var orgId = Guid.NewGuid();
        var config = Config(allowPrivate);

        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.HasFeatureAsync(orgId, BillingFeature.SftpIngestion, It.IsAny<CancellationToken>()))
               .ReturnsAsync(hasSftp);
        billing.Setup(b => b.HasFeatureAsync(orgId, BillingFeature.S3Ingestion, It.IsAny<CancellationToken>()))
               .ReturnsAsync(hasS3);

        var encryption = new DeliveryEncryptionService(config);
        var pullIngress = new PullIngressSettingsService(db, encryption);
        var guard = new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);

        var controller = new SettingsController(
            tenant.Object,
            new Mock<IEmailSettingsService>().Object,
            pullIngress,
            new Mock<IOrganisationSettingsService>().Object,
            billing.Object,
            guard,
            db,
            InboundAddressTestHarness.Create(db),
            config);

        var supplier = new Supplier { Id = Guid.NewGuid(), OrgId = orgId, Name = "Acme", CreatedAt = DateTime.UtcNow };
        db.Suppliers.Add(supplier);
        db.SaveChanges();

        return new Harness(controller, db, orgId, supplier, encryption, billing);
    }

    private static UpdateSftpIngressRequest Sftp(
        bool enabled = true,
        string host = "sftp.supplier.example",
        int port = 22,
        string username = "buyer",
        string? password = "secret",
        string remoteDirectory = "/incoming/orders",
        Guid? defaultSupplierId = null)
        => new(enabled, host, port, username, password, remoteDirectory, defaultSupplierId);

    private static UpdateS3IngressRequest S3(
        bool enabled = true,
        string bucketName = "orders-inbound",
        string keyPrefix = "incoming/",
        string region = "eu-west-1",
        string accessKeyId = "AKIA123",
        string? secretKey = "super-secret",
        Guid? defaultSupplierId = null,
        string? serviceUrl = null)
        => new(enabled, bucketName, keyPrefix, region, accessKeyId, secretKey, defaultSupplierId, serviceUrl);

    // ── SFTP: GET masking + PUT persist/encrypt ────────────────────────────────

    [Fact]
    public async Task GetSftp_WhenNoneConfigured_ReturnsDisabledEmpty()
    {
        var h = Build();

        var result = await h.Controller.GetSftp(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<SftpIngressResponse>().Subject;
        body.Enabled.Should().BeFalse();
        body.HasPassword.Should().BeFalse();
        body.PasswordDisplay.Should().BeNull();
    }

    [Fact]
    public async Task PutSftp_PersistsAndEncrypts_AndGetMasksPassword()
    {
        var h = Build();

        var put = await h.Controller.UpdateSftp(
            Sftp(password: "super-secret-password", defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        var putOk = put.Should().BeOfType<OkObjectResult>().Subject;
        var putBody = putOk.Value.Should().BeOfType<SftpIngressResponse>().Subject;
        putBody.Enabled.Should().BeTrue();
        putBody.Host.Should().Be("sftp.supplier.example");
        putBody.HasPassword.Should().BeTrue();
        putBody.PasswordDisplay.Should().Be("********");

        // Persisted ciphertext is the encrypted form, decryptable to the plaintext.
        var row = await h.Db.SftpIngressConfigs.AsNoTracking().SingleAsync();
        row.OrgId.Should().Be(h.OrgId);
        row.EncryptedPassword.Should().NotBe("super-secret-password");
        h.Encryption.Decrypt(row.EncryptedPassword, CredentialScope.ForOrg(
            h.OrgId, CredentialPurpose.OrgIngressSftpPassword)).Should().Be("super-secret-password");

        // The GET response leaks neither the plaintext nor the ciphertext.
        var get = await h.Controller.GetSftp(CancellationToken.None);
        var body = ((OkObjectResult)get).Value.Should().BeOfType<SftpIngressResponse>().Subject;
        System.Text.Json.JsonSerializer.Serialize(body)
            .Should().NotContain("super-secret-password")
            .And.NotContain(row.EncryptedPassword);
    }

    [Fact]
    public async Task PutSftp_BlankPassword_KeepsExistingSecret()
    {
        var h = Build();
        await h.Controller.UpdateSftp(Sftp(password: "original", defaultSupplierId: h.Supplier.Id), CancellationToken.None);
        var originalCipher = (await h.Db.SftpIngressConfigs.AsNoTracking().SingleAsync()).EncryptedPassword;

        // null password = keep saved secret (the frontend sends null when the field is left blank).
        var result = await h.Controller.UpdateSftp(
            Sftp(port: 2222, password: null, defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var row = await h.Db.SftpIngressConfigs.AsNoTracking().SingleAsync();
        row.EncryptedPassword.Should().Be(originalCipher, "null password means keep");
        row.Port.Should().Be(2222);
        h.Encryption.Decrypt(row.EncryptedPassword, CredentialScope.ForOrg(
            h.OrgId, CredentialPurpose.OrgIngressSftpPassword)).Should().Be("original");
    }

    // ── SFTP: billing gate + validation ────────────────────────────────────────

    [Fact]
    public async Task PutSftp_EnableWithoutFeature_Returns403_WithUpgradeShape()
    {
        var h = Build(hasSftp: false);

        var result = await h.Controller.UpdateSftp(Sftp(defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(403);
        // WP-11: this line used to pin "sftp_ingestion_requires_integration" — the wrong plan.
        // SftpIngestion's minimum is Growth, so the old expectation was defending a 403 that
        // told a Growth customer to buy Integration. Derived from the gate table now, so the
        // test cannot re-pin a plan the gate does not actually require.
        ((string)((dynamic)status.Value!).error).Should()
            .Be($"sftp_ingestion_requires_{PlanConstants.GetMinimumPlan(BillingFeature.SftpIngestion)}");
        (await h.Db.SftpIngressConfigs.CountAsync()).Should().Be(0, "a gated enable persists nothing");
    }

    [Fact]
    public async Task PutSftp_DisabledSave_WithoutFeature_IsAllowed()
    {
        var h = Build(hasSftp: false);

        var result = await h.Controller.UpdateSftp(
            Sftp(enabled: false, defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>("configuring while disabled must not be billing-gated");
    }

    [Fact]
    public async Task PutSftp_EnableMissingHost_Returns400()
    {
        var h = Build();

        var result = await h.Controller.UpdateSftp(
            Sftp(host: "", defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PutSftp_EnableMissingDefaultSupplier_Returns400()
    {
        var h = Build();

        var result = await h.Controller.UpdateSftp(Sftp(defaultSupplierId: null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PutSftp_EnableWithoutAnyPassword_Returns400()
    {
        var h = Build();

        // Enable with null password and no secret on file → still no secret → 400.
        var result = await h.Controller.UpdateSftp(
            Sftp(password: null, defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PutSftp_PrivateHost_Returns400_HostNotAllowed_AndPersistsNothing()
    {
        var h = Build(allowPrivate: false);

        var result = await h.Controller.UpdateSftp(
            Sftp(host: "10.1.2.3", defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ((string)((dynamic)bad.Value!).error).Should().Be("host_not_allowed");
        (await h.Db.SftpIngressConfigs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PutSftp_ForeignDefaultSupplier_Returns400_SupplierNotFound()
    {
        var h = Build();
        var foreign = new Supplier { Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), Name = "Foreign", CreatedAt = DateTime.UtcNow };
        h.Db.Suppliers.Add(foreign);
        await h.Db.SaveChangesAsync();

        var result = await h.Controller.UpdateSftp(Sftp(defaultSupplierId: foreign.Id), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Db.SftpIngressConfigs.CountAsync()).Should().Be(0);
    }

    // ── S3/R2: GET masking + PUT persist/encrypt ───────────────────────────────

    [Fact]
    public async Task GetS3_WhenNoneConfigured_ReturnsDisabledEmpty()
    {
        var h = Build();

        var result = await h.Controller.GetS3(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<S3IngressResponse>().Subject;
        body.Enabled.Should().BeFalse();
        body.HasSecretKey.Should().BeFalse();
        body.SecretKeyDisplay.Should().BeNull();
    }

    [Fact]
    public async Task PutS3_PersistsAndEncrypts_AndGetMasksSecret()
    {
        var h = Build();

        var put = await h.Controller.UpdateS3(
            S3(secretKey: "super-secret-key", defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        var putOk = put.Should().BeOfType<OkObjectResult>().Subject;
        var putBody = putOk.Value.Should().BeOfType<S3IngressResponse>().Subject;
        putBody.BucketName.Should().Be("orders-inbound");
        putBody.AccessKeyId.Should().Be("AKIA123");
        putBody.HasSecretKey.Should().BeTrue();
        putBody.SecretKeyDisplay.Should().Be("********");

        var row = await h.Db.S3IngressConfigs.AsNoTracking().SingleAsync();
        row.OrgId.Should().Be(h.OrgId);
        row.EncryptedSecretKey.Should().NotBe("super-secret-key");
        h.Encryption.Decrypt(row.EncryptedSecretKey, CredentialScope.ForOrg(
            h.OrgId, CredentialPurpose.OrgIngressS3SecretKey)).Should().Be("super-secret-key");

        var get = await h.Controller.GetS3(CancellationToken.None);
        var body = ((OkObjectResult)get).Value.Should().BeOfType<S3IngressResponse>().Subject;
        System.Text.Json.JsonSerializer.Serialize(body)
            .Should().NotContain("super-secret-key")
            .And.NotContain(row.EncryptedSecretKey);
    }

    [Fact]
    public async Task PutS3_BlankSecret_KeepsExistingSecret()
    {
        var h = Build();
        await h.Controller.UpdateS3(S3(secretKey: "original", defaultSupplierId: h.Supplier.Id), CancellationToken.None);
        var originalCipher = (await h.Db.S3IngressConfigs.AsNoTracking().SingleAsync()).EncryptedSecretKey;

        var result = await h.Controller.UpdateS3(
            S3(keyPrefix: "newer/", secretKey: null, defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var row = await h.Db.S3IngressConfigs.AsNoTracking().SingleAsync();
        row.EncryptedSecretKey.Should().Be(originalCipher, "null secret means keep");
        row.KeyPrefix.Should().Be("newer/");
        h.Encryption.Decrypt(row.EncryptedSecretKey, CredentialScope.ForOrg(
            h.OrgId, CredentialPurpose.OrgIngressS3SecretKey)).Should().Be("original");
    }

    // ── S3/R2: billing gate + validation + SSRF on ServiceUrl ──────────────────

    [Fact]
    public async Task PutS3_EnableWithoutFeature_Returns403_WithUpgradeShape()
    {
        var h = Build(hasS3: false);

        var result = await h.Controller.UpdateS3(S3(defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(403);
        // WP-11: was pinned to "s3_ingestion_requires_integration"; S3Ingestion's minimum is Growth.
        ((string)((dynamic)status.Value!).error).Should()
            .Be($"s3_ingestion_requires_{PlanConstants.GetMinimumPlan(BillingFeature.S3Ingestion)}");
        (await h.Db.S3IngressConfigs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PutS3_EnableMissingBucket_Returns400()
    {
        var h = Build();

        var result = await h.Controller.UpdateS3(
            S3(bucketName: "", defaultSupplierId: h.Supplier.Id), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PutS3_EnableMissingDefaultSupplier_Returns400()
    {
        var h = Build();

        var result = await h.Controller.UpdateS3(S3(defaultSupplierId: null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PutS3_PrivateServiceUrl_Returns400_HostNotAllowed_AndPersistsNothing()
    {
        var h = Build(allowPrivate: false);

        // A tenant-controlled ServiceUrl (R2/MinIO) is the SSRF vector and is pre-checked.
        var result = await h.Controller.UpdateS3(
            S3(serviceUrl: "http://169.254.169.254/latest/meta-data/", defaultSupplierId: h.Supplier.Id),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ((string)((dynamic)bad.Value!).error).Should().Be("host_not_allowed");
        (await h.Db.S3IngressConfigs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PutS3_ForeignDefaultSupplier_Returns400_SupplierNotFound()
    {
        var h = Build();
        var foreign = new Supplier { Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), Name = "Foreign", CreatedAt = DateTime.UtcNow };
        h.Db.Suppliers.Add(foreign);
        await h.Db.SaveChangesAsync();

        var result = await h.Controller.UpdateS3(S3(defaultSupplierId: foreign.Id), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Db.S3IngressConfigs.CountAsync()).Should().Be(0);
    }

    // ── Org-scoping: another org's config is invisible ─────────────────────────

    [Fact]
    public async Task GetSftp_IsOrgScoped_DoesNotLeakOtherOrgConfig()
    {
        var h = Build();
        // Seed a config owned by a DIFFERENT org directly.
        var otherOrgId = Guid.NewGuid();
        h.Db.SftpIngressConfigs.Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = otherOrgId,
            Host = "other-org.example",
            Username = "other",
            EncryptedPassword = h.Encryption.Encrypt(
                "other-secret",
                CredentialScope.ForOrg(otherOrgId, CredentialPurpose.OrgIngressSftpPassword)),
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await h.Db.SaveChangesAsync();

        var result = await h.Controller.GetSftp(CancellationToken.None);

        var body = ((OkObjectResult)result).Value.Should().BeOfType<SftpIngressResponse>().Subject;
        body.Host.Should().BeEmpty("the current org has no config; the other org's row must not leak");
        body.Enabled.Should().BeFalse();
    }

    // ── S3 ingress transport security (TLS) ───────────────────────────────────

    /// <summary>
    /// A custom ServiceUrl points the S3 client at a tenant-chosen endpoint (R2, MinIO, or any
    /// S3-compatible server). Over plain http the SigV4-signed request and every object fetched
    /// through it — the purchase order files themselves — cross the network in the clear.
    /// </summary>
    [Fact]
    public async Task UpdateS3_CleartextServiceUrl_Returns400_RequiresTls()
    {
        var h = Build();

        var result = await h.Controller.UpdateS3(
            S3(defaultSupplierId: h.Supplier.Id, serviceUrl: "http://s3.supplier.example"),
            CancellationToken.None);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        ((string)((dynamic)bad.Value!).error).Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
    }

    [Fact]
    public async Task UpdateS3_HttpsServiceUrl_IsAccepted()
    {
        var h = Build();

        var result = await h.Controller.UpdateS3(
            S3(defaultSupplierId: h.Supplier.Id, serviceUrl: "https://s3.supplier.example"),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    /// <summary>A local MinIO on loopback is the normal way to develop against S3 ingress.</summary>
    [Fact]
    public async Task UpdateS3_LoopbackServiceUrl_IsAcceptedForLocalDevelopment()
    {
        var h = Build();

        var result = await h.Controller.UpdateS3(
            S3(defaultSupplierId: h.Supplier.Id, serviceUrl: "http://127.0.0.1:9000"),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    /// <summary>The standard AWS endpoint (no ServiceUrl) is https by construction — unchanged.</summary>
    [Fact]
    public async Task UpdateS3_NoServiceUrl_IsUnaffected()
    {
        var h = Build();

        var result = await h.Controller.UpdateS3(
            S3(defaultSupplierId: h.Supplier.Id, serviceUrl: null),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
