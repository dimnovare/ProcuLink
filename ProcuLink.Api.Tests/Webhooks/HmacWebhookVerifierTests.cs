using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Webhooks;
using Xunit;

namespace ProcuLink.Api.Tests.Webhooks;

/// <summary>
/// HMAC webhook verifier behaviour: timestamp window, replay protection, signature compare,
/// and generic-error response for every failure path.
/// </summary>
public class HmacWebhookVerifierTests
{
    // Stable 32-byte (AES-256) base64 key for DeliveryEncryptionService in tests.
    private const string EncryptionKeyBase64 = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    private const string Slug                = "acme-co-1234";
    private const string TestSecret          = "shared-supplier-secret-for-tests-only";
    private const string GenericError        = "Signature verification failed";

    private static ProcuLinkDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new VerifierTestDbContext(opts);
    }

    private static DeliveryEncryptionService MakeCrypto()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = EncryptionKeyBase64,
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static async Task<(HmacWebhookVerifier Verifier, ProcuLinkDbContext Db, Guid OrgId, string Secret)>
        BuildVerifierAsync(bool persistOrg = true, string? overrideEncrypted = null)
    {
        var db     = MakeDb();
        var crypto = MakeCrypto();
        IDistributedCache cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var orgId  = Guid.NewGuid();
        var secret = TestSecret;

        if (persistOrg)
        {
            var encrypted = overrideEncrypted ?? crypto.Encrypt(secret);
            db.Organisations.Add(new Organisation
            {
                Id                      = orgId,
                ClerkOrgId              = "org_TEST",
                Name                    = "Acme",
                Slug                    = Slug,
                Plan                    = "pilot",
                AccountStatus           = "trialing",
                CreatedAt               = DateTime.UtcNow,
                TrialStartedAt          = DateTime.UtcNow,
                WebhookSecretEncrypted  = encrypted,
            });
            await db.SaveChangesAsync();
        }

        var verifier = new HmacWebhookVerifier(
            db, crypto, cache, NullLogger<HmacWebhookVerifier>.Instance);
        return (verifier, db, orgId, secret);
    }

    private static string SignHex(string secret, string timestamp, string nonce, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var data = Encoding.UTF8.GetBytes($"{timestamp}.{nonce}.{body}");
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }

    private static string NowIso() =>
        DateTimeOffset.UtcNow.ToString("o");

    [Fact]
    public async Task VerifyAsync_AcceptsValidSignature_WithFreshTimestampAndNonce()
    {
        var (verifier, _, orgId, secret) = await BuildVerifierAsync();

        var ts    = NowIso();
        var nonce = Guid.NewGuid().ToString("N");
        var body  = "{\"orderId\":\"" + Guid.NewGuid() + "\",\"status\":\"received\"}";
        var sig   = SignHex(secret, ts, nonce, body);

        var result = await verifier.VerifyAsync(Slug, ts, nonce, sig, body, CancellationToken.None);

        result.Valid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.OrganisationId.Should().Be(orgId);
    }

    [Fact]
    public async Task VerifyAsync_RejectsExpiredTimestamp_BeyondFiveMinutes()
    {
        var (verifier, _, _, secret) = await BuildVerifierAsync();

        var ts    = DateTimeOffset.UtcNow.AddSeconds(-400).ToString("o"); // > 300s in the past
        var nonce = Guid.NewGuid().ToString("N");
        var body  = "{}";
        var sig   = SignHex(secret, ts, nonce, body);

        var result = await verifier.VerifyAsync(Slug, ts, nonce, sig, body, CancellationToken.None);

        result.Valid.Should().BeFalse();
        result.ErrorMessage.Should().Be(GenericError);
        result.OrganisationId.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_RejectsFutureTimestamp_BeyondFiveMinutes()
    {
        var (verifier, _, _, secret) = await BuildVerifierAsync();

        var ts    = DateTimeOffset.UtcNow.AddSeconds(400).ToString("o"); // > 300s ahead
        var nonce = Guid.NewGuid().ToString("N");
        var body  = "{}";
        var sig   = SignHex(secret, ts, nonce, body);

        var result = await verifier.VerifyAsync(Slug, ts, nonce, sig, body, CancellationToken.None);

        result.Valid.Should().BeFalse();
        result.ErrorMessage.Should().Be(GenericError);
        result.OrganisationId.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_RejectsReplayedNonce_WithinCacheWindow()
    {
        var (verifier, _, orgId, secret) = await BuildVerifierAsync();

        var ts    = NowIso();
        var nonce = Guid.NewGuid().ToString("N");
        var body  = "{\"ping\":true}";
        var sig   = SignHex(secret, ts, nonce, body);

        // First call passes.
        var first = await verifier.VerifyAsync(Slug, ts, nonce, sig, body, CancellationToken.None);
        first.Valid.Should().BeTrue();
        first.OrganisationId.Should().Be(orgId);

        // Same nonce replayed within 600s — must fail with generic error.
        var second = await verifier.VerifyAsync(Slug, ts, nonce, sig, body, CancellationToken.None);
        second.Valid.Should().BeFalse();
        second.ErrorMessage.Should().Be(GenericError);
        second.OrganisationId.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_RejectsTamperedBody_WithSignatureMismatch()
    {
        var (verifier, _, _, secret) = await BuildVerifierAsync();

        var ts        = NowIso();
        var nonce     = Guid.NewGuid().ToString("N");
        var bodySent  = "{\"orderId\":\"00000000-0000-0000-0000-000000000001\"}";
        var bodySigned = "{\"orderId\":\"00000000-0000-0000-0000-000000000002\"}";

        // Compute signature over a different body than what we deliver — classic tamper case.
        var sig = SignHex(secret, ts, nonce, bodySigned);

        var result = await verifier.VerifyAsync(Slug, ts, nonce, sig, bodySent, CancellationToken.None);

        result.Valid.Should().BeFalse();
        result.ErrorMessage.Should().Be(GenericError);
        result.OrganisationId.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_RejectsUnknownSlug_WithGenericError()
    {
        // Org persisted with a different slug; we look up something else.
        var (verifier, _, _, secret) = await BuildVerifierAsync();

        var ts    = NowIso();
        var nonce = Guid.NewGuid().ToString("N");
        var body  = "{}";
        var sig   = SignHex(secret, ts, nonce, body);

        var result = await verifier.VerifyAsync("nobody-here", ts, nonce, sig, body, CancellationToken.None);

        result.Valid.Should().BeFalse();
        result.ErrorMessage.Should().Be(GenericError);
        result.OrganisationId.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAsync_RejectsMissingSecret_WithGenericError()
    {
        // Org has no webhook_secret_encrypted set.
        var db     = MakeDb();
        var crypto = MakeCrypto();
        IDistributedCache cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var orgId  = Guid.NewGuid();

        db.Organisations.Add(new Organisation
        {
            Id                     = orgId,
            ClerkOrgId             = "org_NOSEC",
            Name                   = "NoSecret",
            Slug                   = Slug,
            Plan                   = "pilot",
            AccountStatus          = "trialing",
            CreatedAt              = DateTime.UtcNow,
            TrialStartedAt         = DateTime.UtcNow,
            WebhookSecretEncrypted = null,
        });
        await db.SaveChangesAsync();

        var verifier = new HmacWebhookVerifier(
            db, crypto, cache, NullLogger<HmacWebhookVerifier>.Instance);

        var ts    = NowIso();
        var nonce = Guid.NewGuid().ToString("N");
        var body  = "{}";
        var sig   = SignHex(TestSecret, ts, nonce, body);

        var result = await verifier.VerifyAsync(Slug, ts, nonce, sig, body, CancellationToken.None);

        result.Valid.Should().BeFalse();
        result.ErrorMessage.Should().Be(GenericError);
        result.OrganisationId.Should().BeNull();
    }

    /// <summary>
    /// Minimal in-memory DbContext. Persists Organisation only; everything else
    /// ignored so we don't fight jsonb/FK mappings that aren't exercised by the verifier.
    /// </summary>
    private sealed class VerifierTestDbContext : ProcuLinkDbContext
    {
        public VerifierTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();

            modelBuilder.Entity<Organisation>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Slug);
                b.Property(x => x.WebhookSecretEncrypted);
                b.Ignore(x => x.Memberships);
                b.Ignore(x => x.Suppliers);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.AuditEvents);
                b.Ignore(x => x.ApiKeys);
                b.Ignore(x => x.IntegrationSubscriptions);
            });
        }
    }
}
