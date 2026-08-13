using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Dispatchers;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// WP-20 — the envelope a byte-perfect document is delivered in.
///
/// <para>
/// Two defects live here. First, <c>DeliveryService</c> re-derived the outbound content type and
/// file extension from the artifact's format string with a switch that only knew xml/json/csv, so
/// cXML, UBL and X12 went to real suppliers as <c>application/octet-stream</c> named
/// <c>PO-xxx.dat</c> — receivers that gate on content type or extension bounce a document that is
/// otherwise correct. Second, the SFTP/FTPS remote filename was the bare PO number, so two orders
/// that share a PO number wrote to the SAME remote path and the first one was silently gone.
/// </para>
/// </summary>
public class DeliveryContentTypeAndFileNameTests
{
    // ── Content type + extension, per delivered format ────────────────────────

    [Theory]
    [InlineData("cxml", "application/xml",     ".xml")]
    [InlineData("x12",  "application/edi-x12", ".x12")]
    [InlineData("ubl",  "application/xml",     ".xml")]
    [InlineData("xml",  "application/xml",     ".xml")]
    [InlineData("json", "application/json",    ".json")]
    [InlineData("csv",  "text/csv",            ".csv")]
    public async Task DispatchArtifact_SendsTheContentTypeAndExtensionTheFormatActuallyIs(
        string artifactFormat, string expectedContentType, string expectedExtension)
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, artifactFormat, poNumber: "PO-123");
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, DeliveryProtocolConstants.Http));
        await db.SaveChangesAsync();

        var dispatcher = new RecordingDispatcher(DeliveryProtocolConstants.Http);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        dispatcher.Sends.Should().HaveCount(1);
        dispatcher.Sends[0].ContentType.Should().Be(expectedContentType);
        dispatcher.Sends[0].FileName.Should().EndWith(expectedExtension);
    }

    // ── SFTP: two orders sharing one PO number must both land ─────────────────

    [Fact]
    public async Task TwoOrdersSharingOnePoNumber_BothLandOverSftp_NeitherIsClobbered()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        // Same PO number on two genuinely different orders — a buyer re-using a PO number across
        // sites, or two source systems that number independently. Today both derive the identical
        // remote path and the SECOND upload overwrites the FIRST: one supplier order simply gone.
        var first  = await SeedOrderAsync(db, "cxml", "PO-123", orgId, supplierId, body: "FIRST-ORDER-PAYLOAD");
        var second = await SeedOrderAsync(db, "cxml", "PO-123", orgId, supplierId, body: "SECOND-ORDER-PAYLOAD");

        db.SupplierDeliveryConfigs.Add(MakeConfig(
            orgId, supplierId, encryption, DeliveryProtocolConstants.Sftp,
            configJson: "{\"host\":\"sftp.supplier.example\",\"remotePath\":\"/inbound/orders\"}"));
        await db.SaveChangesAsync();

        // A remote directory modelled the way an SFTP server behaves: one entry per remote path,
        // a second write to the same path REPLACES the first. The path is built with the real
        // dispatcher helpers, so this measures the filename the supplier's server actually sees.
        var remoteDirectory = new SftpDirectoryDouble();
        var service = CreateService(db, remoteDirectory, encryption, new KeyedFileStorage(db));

        (await service.DispatchArtifactAsync(orgId, first.OrderId,  first.ArtifactId,  true, default)).Success.Should().BeTrue();
        (await service.DispatchArtifactAsync(orgId, second.OrderId, second.ArtifactId, true, default)).Success.Should().BeTrue();

        remoteDirectory.Files.Should().HaveCount(2,
            "two different orders must occupy two different remote paths — sharing a PO number must never cost the supplier an order");
        remoteDirectory.Files.Values.Select(Encoding.UTF8.GetString)
            .Should().BeEquivalentTo(new[] { "FIRST-ORDER-PAYLOAD", "SECOND-ORDER-PAYLOAD" });
    }

    [Fact]
    public async Task RedeliveringTheSameOrder_TargetsTheSameRemotePath()
    {
        // The collision fix must not break A3 idempotency: the same order re-sent (crash-recovery
        // re-drive, operator redeliver) must still resolve to ONE deterministic remote path, so a
        // re-send replaces its own file instead of leaving the supplier holding two copies.
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var ids = await SeedOrderAsync(db, "cxml", "PO-123", orgId, supplierId);

        db.SupplierDeliveryConfigs.Add(MakeConfig(
            orgId, supplierId, encryption, DeliveryProtocolConstants.Sftp,
            configJson: "{\"host\":\"sftp.supplier.example\",\"remotePath\":\"/inbound/orders\"}"));
        await db.SaveChangesAsync();

        var remoteDirectory = new SftpDirectoryDouble();
        var service = CreateService(db, remoteDirectory, encryption, new KeyedFileStorage(db));

        await service.DispatchArtifactAsync(orgId, ids.OrderId, ids.ArtifactId, true, default);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId);
        order.Status = OrderStatusConstants.ReadyToDeliver;
        await db.SaveChangesAsync();
        await service.DispatchArtifactAsync(orgId, ids.OrderId, ids.ArtifactId, true, default);

        remoteDirectory.Writes.Should().Be(2);
        remoteDirectory.Files.Should().HaveCount(1, "the same order must keep one deterministic remote path");
    }

    // ── Which channels get the order-id qualifier ─────────────────────────────

    [Theory]
    [InlineData(DeliveryProtocolConstants.Sftp)]
    [InlineData(DeliveryProtocolConstants.Ftps)]
    [InlineData(DeliveryProtocolConstants.Ftp)]
    public void FileDropChannels_QualifyTheNameWithTheOrder(string protocol)
    {
        var order    = OrderNamed("PO-123", Guid.Parse("a1b2c3d4-0000-0000-0000-000000000000"));
        var artifact = new OutboundArtifact { Format = "cxml" };

        DeliveryService.BuildFileName(order, artifact, protocol).Should().Be("PO-123-a1b2c3d4.xml");
    }

    [Theory]
    [InlineData(DeliveryProtocolConstants.Http)]
    [InlineData(DeliveryProtocolConstants.Email)]
    [InlineData(DeliveryProtocolConstants.ErpErply)]
    public void ChannelsWhereTheNameIsNotAnIdentity_KeepThePlainPoNumber(string protocol)
    {
        // Not because the name can never repeat there — see
        // OffTheFileDropChannels_TwoOrdersCanShareOneName below, which is the accepted residual —
        // but because on these channels the name is not WHERE the document lives. HTTP never
        // transmits it at all, the ERP connectors carry it as metadata beside a payload that names
        // the order itself, and email delivers each document as its own message. Nothing can be
        // overwritten and no order can be lost; the email dispatchers put this string in front of a
        // human ("Purchase Order PO-123") where an id suffix would be noise.
        var order    = OrderNamed("PO-123", Guid.Parse("a1b2c3d4-0000-0000-0000-000000000000"));
        var artifact = new OutboundArtifact { Format = "cxml" };

        DeliveryService.BuildFileName(order, artifact, protocol).Should().Be("PO-123.xml");
    }

    [Fact]
    public void OffTheFileDropChannels_TwoOrdersCanShareOneName_AndThatIsTheAcceptedResidual()
    {
        // ACCEPTED RISK, pinned so it cannot change silently.
        //
        // Two genuinely different orders whose PO numbers differ only in punctuation collapse to one
        // name (the sanitiser maps everything outside letters/digits/. _ - to '-'), as do two orders
        // that share a PO number outright. On SFTP/FTPS that used to cost the supplier an order and
        // is fixed by the order-id suffix. Off those channels it costs nothing structural: both
        // documents still arrive, in full, each in its own HTTP request / ERP post / email. What
        // remains is a HUMAN ambiguity — a supplier who saves two same-named attachments into one
        // folder keeps one, and the default email subject ("Purchase Order PO-123") reads the same
        // for both.
        //
        // Not fixed here, deliberately: qualifying the email name would change the subject line
        // every email supplier already filters on, which is a customer conversation of its own and
        // does not belong in a packet about content types. The payload always carries the PO number
        // and the order, so nothing is unrecoverable.
        var artifact = new OutboundArtifact { Format = "cxml" };

        var a = DeliveryService.BuildFileName(
            OrderNamed("PO:123", Guid.NewGuid()), artifact, DeliveryProtocolConstants.Email);
        var b = DeliveryService.BuildFileName(
            OrderNamed("PO 123", Guid.NewGuid()), artifact, DeliveryProtocolConstants.Email);

        a.Should().Be(b, "documented residual — see the reviewer note in the PR body");

        // The same two orders over SFTP do NOT collide: the qualifier is the order id.
        var sftpA = DeliveryService.BuildFileName(
            OrderNamed("PO:123", Guid.NewGuid()), artifact, DeliveryProtocolConstants.Sftp);
        var sftpB = DeliveryService.BuildFileName(
            OrderNamed("PO 123", Guid.NewGuid()), artifact, DeliveryProtocolConstants.Sftp);

        sftpA.Should().NotBe(sftpB);
    }

    [Fact]
    public void TheQualifier_IsTheOrderId_SoARedeliveryResolvesToTheSameName()
    {
        var orderId  = Guid.NewGuid();
        var artifact = new OutboundArtifact { Format = "cxml" };

        var first  = DeliveryService.BuildFileName(OrderNamed("PO-9", orderId), artifact, DeliveryProtocolConstants.Sftp);
        var second = DeliveryService.BuildFileName(OrderNamed("PO-9", orderId), artifact, DeliveryProtocolConstants.Sftp);

        first.Should().Be(second, "a timestamp would make every re-send a new file at the supplier");
    }

    // ── The PO-number sanitiser must not depend on the OS ─────────────────────

    [Theory]
    // Windows rejects these and Linux allows them, so Path.GetInvalidFileNameChars() used to give
    // a DIFFERENT remote filename in development than in production. Pinned to the literal set.
    [InlineData("PO:123",      "PO-123")]
    [InlineData("PO?123",      "PO-123")]
    [InlineData("PO*123",      "PO-123")]
    [InlineData("PO\"123",     "PO-123")]
    [InlineData("PO<123>",     "PO-123")]
    [InlineData("PO|123",      "PO-123")]
    [InlineData("PO/123",      "PO-123")]
    [InlineData("PO\\123",     "PO-123")]
    [InlineData("PO 123",      "PO-123")]
    [InlineData("PO-2026.01",  "PO-2026.01")]
    [InlineData("PO_123",      "PO_123")]
    [InlineData("  PO-1  ",    "PO-1")]
    [InlineData("PO#123",      "PO-123")]
    public void SanitizeFileToken_IsPlatformIndependent(string poNumber, string expected)
    {
        DeliveryService.SanitizeFileToken(poNumber).Should().Be(expected);
    }

    // ── Non-ASCII must reach the supplier intact ──────────────────────────────

    [Theory]
    // Production is Linux, where Path.GetInvalidFileNameChars() is only { '\0', '/' }: every one of
    // these ALREADY reaches suppliers unchanged today, through both this sanitiser and the
    // dispatchers' own (which uses the Unicode-aware char.IsLetterOrDigit). Reducing the allow-list
    // to ASCII would silently mangle them — a customer-visible change, on the filename AND on the
    // default email subject, for exactly the European buyers this product is sold to.
    [InlineData("Ordre-Nº7",     "Ordre-Nº7")]
    [InlineData("BESTELLUNG-Ä1", "BESTELLUNG-Ä1")]
    [InlineData("TELLIMUS-Õ5",   "TELLIMUS-Õ5")]
    [InlineData("PO-Öl-42",      "PO-Öl-42")]
    [InlineData("PO-Über-9",     "PO-Über-9")]
    [InlineData("Straße-77",     "Straße-77")]
    [InlineData("Commande-Été",  "Commande-Été")]
    [InlineData("Pedido-Niño",   "Pedido-Niño")]
    [InlineData("Achat-Français", "Achat-Français")]
    [InlineData("注文-2026",      "注文-2026")]
    public void SanitizeFileToken_KeepsLettersAndDigitsThatAreNotAscii(string poNumber, string expected)
    {
        DeliveryService.SanitizeFileToken(poNumber).Should().Be(expected);
    }

    [Theory]
    [InlineData("PO-Ä1")]
    [InlineData("PO-Ö1")]
    [InlineData("Ordre-Nº7")]
    public void ANonAsciiPoNumber_SurvivesBothSanitisersUnchanged(string poNumber)
    {
        // The two passes must agree, or the name recorded here is not the name on the server. They
        // use the SAME allow-list (Unicode letter/digit plus . _ -); only the substitute character
        // differs (- here, _ in the dispatchers), and because - is itself in the allow-list the
        // second pass is a no-op on anything this method produces.
        var order = OrderNamed(poNumber, Guid.Parse("a1b2c3d4-0000-0000-0000-000000000000"));
        var built = DeliveryService.BuildFileName(order, new OutboundArtifact { Format = "cxml" },
                                                  DeliveryProtocolConstants.Sftp);

        SftpDeliveryDispatcher.SanitiseFileName(built).Should().Be(built);
        FtpsDeliveryDispatcher.SanitiseFileName(built).Should().Be(built);
    }

    [Fact]
    public void TwoPoNumbersThatDifferOnlyInAnAccent_DoNotCollapseToOneName()
    {
        // PO-Ä1 and PO-Ö1 are two different purchase orders. An ASCII-only allow-list turns both
        // into PO--1 — the same class of collision this packet exists to remove.
        var a = DeliveryService.SanitizeFileToken("PO-Ä1");
        var b = DeliveryService.SanitizeFileToken("PO-Ö1");

        a.Should().NotBe(b);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    [InlineData("###")]
    public void SanitizeFileToken_FallsBackWhenNothingSurvives(string poNumber)
    {
        DeliveryService.SanitizeFileToken(poNumber).Should().Be("order");
    }

    [Fact]
    public void TheSanitisedName_SurvivesTheDispatchersOwnSanitiserUnchanged()
    {
        // Two sanitisers run in sequence (DeliveryService, then the SFTP dispatcher). If they
        // disagreed, the name recorded here would not be the name on the supplier's server.
        var order    = OrderNamed("PO/2026:01", Guid.Parse("a1b2c3d4-0000-0000-0000-000000000000"));
        var artifact = new OutboundArtifact { Format = "x12" };

        var built = DeliveryService.BuildFileName(order, artifact, DeliveryProtocolConstants.Sftp);

        built.Should().Be("PO-2026-01-a1b2c3d4.x12");
        SftpDeliveryDispatcher.SanitiseFileName(built).Should().Be(built);
        FtpsDeliveryDispatcher.SanitiseFileName(built).Should().Be(built);
    }

    private static PurchaseOrderEntity OrderNamed(string poNumber, Guid id) =>
        new() { Id = id, PoNumber = poNumber };

    // ── Harness ───────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeliveryEnvelopeTestDbContext(options);
    }

    private static DeliveryEncryptionService CreateEncryption()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedOrderAsync(
        ProcuLinkDbContext db,
        string artifactFormat,
        string poNumber,
        Guid? orgId = null,
        Guid? supplierId = null,
        string body = "PAYLOAD")
    {
        var org      = orgId ?? Guid.NewGuid();
        var supplier = supplierId ?? Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = org,
            SupplierId = supplier,
            PoNumber = poNumber,
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = OrderStatusConstants.ReadyToDeliver,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId,
            OrderId = orderId,
            OrgId = org,
            Format = artifactFormat,
            FileKey = $"{org}/{orderId}/artifacts/{artifactId}::{body}",
            CreatedAt = now,
        });

        await db.SaveChangesAsync();
        return (org, supplier, orderId, artifactId);
    }

    private static SupplierDeliveryConfig MakeConfig(
        Guid orgId,
        Guid supplierId,
        DeliveryEncryptionService encryption,
        string protocol,
        string configJson = "{\"url\":\"https://supplier.example/orders\"}") =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = protocol,
            AutoDeliver = true,
            ConfigJson = configJson,
            EncryptedCredentials = encryption.Encrypt(
                "{\"type\":\"none\"}",
                CredentialScope.ForSupplier(orgId, CredentialPurpose.SupplierDeliveryCredentials, supplierId)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static DeliveryService CreateService(
        ProcuLinkDbContext db,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService? encryption = null,
        IFileStorageService? storage = null) =>
        new(
            db,
            storage ?? new ConstantFileStorage(),
            encryption ?? CreateEncryption(),
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Infrastructure.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    /// <summary>Captures exactly what the dispatcher was handed, for any protocol.</summary>
    private sealed class RecordingDispatcher : IDeliveryDispatcher
    {
        public RecordingDispatcher(string protocol) => Protocol = protocol;

        public string Protocol { get; }
        public ResendSafety ResendSafety => ResendSafety.Safe;
        public List<(string FileName, string ContentType, byte[] Content)> Sends { get; } = new();

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
        {
            Sends.Add((fileName, contentType, content));
            return Task.FromResult(new DeliveryResult(true, null, 200));
        }
    }

    /// <summary>
    /// An SFTP remote directory the way a server holds one: keyed by the remote path the REAL
    /// dispatcher would compute, and a second write to the same path replaces the first.
    /// </summary>
    private sealed class SftpDirectoryDouble : IDeliveryDispatcher
    {
        public string Protocol => DeliveryProtocolConstants.Sftp;
        public ResendSafety ResendSafety => ResendSafety.Safe;
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public int Writes { get; private set; }

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
        {
            var dir  = SftpDeliveryDispatcher.NormaliseRemoteDir("/inbound/orders");
            var path = $"{dir.TrimEnd('/')}/{SftpDeliveryDispatcher.SanitiseFileName(fileName)}";
            Files[path] = content;
            Writes++;
            return Task.FromResult(new DeliveryResult(true, null, 200));
        }
    }

    /// <summary>Serves per-artifact bytes so a lost upload is visible as lost CONTENT, not just a lost path.</summary>
    private sealed class KeyedFileStorage : IFileStorageService
    {
        private readonly ProcuLinkDbContext _db;
        public KeyedFileStorage(ProcuLinkDbContext db) => _db = db;

        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);

        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");

        public Task<Stream> DownloadAsync(string key, CancellationToken ct)
        {
            var marker = key.Split("::").Last();
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(marker)));
        }

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ConstantFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);

        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");

        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("PAYLOAD"u8.ToArray()));

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class DeliveryEnvelopeTestDbContext : ProcuLinkDbContext
    {
        public DeliveryEnvelopeTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<AuditEvent>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();

            modelBuilder.Entity<PurchaseOrderEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
                b.Ignore(x => x.Lines);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.CanonicalJson);
            });

            modelBuilder.Entity<OutboundArtifact>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
            });

            modelBuilder.Entity<SupplierDeliveryConfig>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.Supplier);
            });

            modelBuilder.Entity<DeliveryAttempt>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
                b.Ignore(x => x.Organisation);
            });

            modelBuilder.Entity<PurchaseOrderLineEntity>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Order);
            });

            modelBuilder.Entity<OrderException>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
            });
        }
    }
}
