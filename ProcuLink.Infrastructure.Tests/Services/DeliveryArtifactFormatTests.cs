using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Dispatchers;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// WP-20 — the artifact a supplier actually receives must be labelled and named for the format it
/// really is.
///
/// Two interop defects motivate this suite:
///
/// 1. <c>DeliveryService</c> re-derived the outbound content type / file extension from a switch
///    that only knew <c>xml</c> / <c>json</c> / <c>csv</c>. Every cXML, UBL and X12 artifact was
///    therefore dispatched as <c>application/octet-stream</c> named <c>PO-xxx.dat</c>. Receivers
///    that gate on content type or extension reject that payload, while ProcuLink reports the
///    delivery as successful.
///
/// 2. On the file-drop channels (SFTP / FTPS) the remote filename was the BARE PO number and the
///    upload overwrites. <c>po_number</c> carries no uniqueness constraint
///    (<c>ProcuLinkDbContext</c> declares it <c>IsRequired()</c> only), so two orders sharing a PO
///    number silently clobbered each other in the supplier's drop directory.
/// </summary>
public class DeliveryArtifactFormatTests
{
    // ── 1. Format → (content type, file extension) ────────────────────────────
    // Table-driven over the complete set of output-format identifiers the product supports
    // (DeliveryConfigService's allow-list and OrdersController's transform allow-list agree:
    // xml, csv, cxml, json, ubl, x12).

    [Theory]
    [InlineData("xml", "application/xml", "PO-SHARED.xml")]
    [InlineData("csv", "text/csv", "PO-SHARED.csv")]
    [InlineData("json", "application/json", "PO-SHARED.json")]
    [InlineData("cxml", "application/xml", "PO-SHARED.xml")]
    [InlineData("ubl", "application/xml", "PO-SHARED.xml")]
    [InlineData("x12", "application/EDI-X12", "PO-SHARED.x12")]
    public async Task DispatchArtifactAsync_LabelsArtifactForItsFormat(
        string format,
        string expectedContentType,
        string expectedFileName)
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var seed = await SeedOrderAsync(db, format: format);
        db.SupplierDeliveryConfigs.Add(MakeConfig(seed.OrgId, seed.SupplierId, encryption, DeliveryProtocolConstants.Http));
        await db.SaveChangesAsync();

        var dispatcher = new CapturingDispatcher(DeliveryProtocolConstants.Http);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.DispatchArtifactAsync(seed.OrgId, seed.OrderId, seed.ArtifactId, true, default);

        result.Success.Should().BeTrue();
        dispatcher.ContentTypes.Should().ContainSingle().Which.Should().Be(expectedContentType);
        dispatcher.FileNames.Should().ContainSingle().Which.Should().Be(expectedFileName);
    }

    [Fact]
    public async Task DispatchArtifactAsync_UnknownFormat_FallsBackToOctetStreamAndLogsTheFormat()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var seed = await SeedOrderAsync(db, format: "edifact");
        db.SupplierDeliveryConfigs.Add(MakeConfig(seed.OrgId, seed.SupplierId, encryption, DeliveryProtocolConstants.Http));
        await db.SaveChangesAsync();

        var dispatcher = new CapturingDispatcher(DeliveryProtocolConstants.Http);
        var logger = new RecordingLogger<DeliveryService>();
        var service = CreateService(db, dispatcher, encryption, logger: logger);

        await service.DispatchArtifactAsync(seed.OrgId, seed.OrderId, seed.ArtifactId, true, default);

        dispatcher.ContentTypes.Should().ContainSingle().Which.Should().Be("application/octet-stream");
        dispatcher.FileNames.Should().ContainSingle().Which.Should().Be("PO-SHARED.dat");
        logger.Entries
            .Where(e => e.Level == LogLevel.Warning)
            .Select(e => e.Message)
            .Should().Contain(m => m.Contains("edifact"),
                "an unrecognised output format must be surfaced, not silently downgraded to octet-stream");
    }

    // ── 2. File-drop naming: two orders sharing a PO number must not clobber ──

    [Fact]
    public async Task DispatchArtifactAsync_TwoOrdersSharingAPoNumber_GetDistinctSftpFileNames()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var first = await SeedOrderAsync(db, format: "cxml", orgId: orgId, supplierId: supplierId);
        var second = await SeedOrderAsync(db, format: "cxml", orgId: orgId, supplierId: supplierId);
        db.SupplierDeliveryConfigs.Add(MakeConfig(orgId, supplierId, encryption, DeliveryProtocolConstants.Sftp));
        await db.SaveChangesAsync();

        var dispatcher = new CapturingDispatcher(DeliveryProtocolConstants.Sftp);
        var service = CreateService(db, dispatcher, encryption);

        (await service.DispatchArtifactAsync(orgId, first.OrderId, first.ArtifactId, true, default))
            .Success.Should().BeTrue();
        (await service.DispatchArtifactAsync(orgId, second.OrderId, second.ArtifactId, true, default))
            .Success.Should().BeTrue();

        dispatcher.FileNames.Should().HaveCount(2);
        dispatcher.FileNames[0].Should().NotBe(
            dispatcher.FileNames[1],
            "both orders carry PO number 'PO-SHARED'; identical remote filenames overwrite each other in the supplier's drop directory");
        dispatcher.FileNames.Should().OnlyContain(n => n.StartsWith("PO-SHARED", StringComparison.Ordinal));
        dispatcher.FileNames.Should().OnlyContain(n => n.EndsWith(".xml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchArtifactAsync_SameOrderRedispatched_KeepsTheSameSftpFileName()
    {
        // The disambiguating suffix must be DETERMINISTIC per order. DeliveryService's A3
        // crash-recovery re-drive relies on SFTP/FTPS overwriting the same deterministic path so a
        // re-send cannot leave the supplier holding two copies (SftpDeliveryDispatcher declares
        // ResendSafety.Safe on exactly that basis). A timestamp suffix would break it.
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var seed = await SeedOrderAsync(db, format: "x12");
        db.SupplierDeliveryConfigs.Add(MakeConfig(seed.OrgId, seed.SupplierId, encryption, DeliveryProtocolConstants.Ftps));
        await db.SaveChangesAsync();

        var dispatcher = new CapturingDispatcher(DeliveryProtocolConstants.Ftps);
        var service = CreateService(db, dispatcher, encryption);

        await service.DispatchArtifactAsync(seed.OrgId, seed.OrderId, seed.ArtifactId, true, default);
        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == seed.OrderId);
        order.Status = OrderStatusConstants.ReadyToDeliver;
        await db.SaveChangesAsync();
        await service.DispatchArtifactAsync(seed.OrgId, seed.OrderId, seed.ArtifactId, true, default);

        dispatcher.FileNames.Should().HaveCount(2);
        dispatcher.FileNames[0].Should().Be(dispatcher.FileNames[1]);
        dispatcher.FileNames[0].Should().EndWith(".x12");
    }

    [Fact]
    public async Task DispatchArtifactAsync_NonFileDropChannel_KeepsThePlainPoFileName()
    {
        // HTTP / email / ERP channels do not write into a shared directory, so their filename stays
        // the human-readable PO number — only the extension is corrected.
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var seed = await SeedOrderAsync(db, format: "cxml");
        db.SupplierDeliveryConfigs.Add(MakeConfig(seed.OrgId, seed.SupplierId, encryption, DeliveryProtocolConstants.Email));
        await db.SaveChangesAsync();

        var dispatcher = new CapturingDispatcher(DeliveryProtocolConstants.Email);
        var service = CreateService(db, dispatcher, encryption);

        await service.DispatchArtifactAsync(seed.OrgId, seed.OrderId, seed.ArtifactId, true, default);

        dispatcher.FileNames.Should().ContainSingle().Which.Should().Be("PO-SHARED.xml");
    }

    [Fact]
    public async Task DispatchArtifactAsync_LegacyFileNameOptOut_RestoresTheBarePoFileName()
    {
        // Escape hatch for a supplier whose pickup automation matches the exact bare PO filename.
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var seed = await SeedOrderAsync(db, format: "cxml");
        var config = MakeConfig(seed.OrgId, seed.SupplierId, encryption, DeliveryProtocolConstants.Sftp);
        config.ConfigJson = "{\"host\":\"sftp.supplier.example\",\"legacyFileName\":true}";
        db.SupplierDeliveryConfigs.Add(config);
        await db.SaveChangesAsync();

        var dispatcher = new CapturingDispatcher(DeliveryProtocolConstants.Sftp);
        var service = CreateService(db, dispatcher, encryption);

        await service.DispatchArtifactAsync(seed.OrgId, seed.OrderId, seed.ArtifactId, true, default);

        dispatcher.FileNames.Should().ContainSingle().Which.Should().Be("PO-SHARED.xml");
    }

    // ── 3. File-drop overwrite is now an explicit, per-supplier decision ──────

    [Theory]
    [InlineData("{\"host\":\"h\"}", true)]
    [InlineData("{\"host\":\"h\",\"overwriteExisting\":true}", true)]
    [InlineData("{\"host\":\"h\",\"overwriteExisting\":false}", false)]
    [InlineData("{\"host\":\"h\",\"OverwriteExisting\":false}", false)]
    [InlineData("not json", true)]
    [InlineData("", true)]
    public void SftpDispatcher_ResolvesOverwriteFromConfig(string configJson, bool expected)
    {
        SftpDeliveryDispatcher.ResolveOverwriteExisting(configJson).Should().Be(expected);
    }

    [Theory]
    [InlineData("{\"host\":\"h\"}", true)]
    [InlineData("{\"host\":\"h\",\"overwriteExisting\":false}", false)]
    public void FtpsDispatcher_ResolvesOverwriteFromConfig(string configJson, bool expected)
    {
        FtpsDeliveryDispatcher.ResolveOverwriteExisting(configJson).Should().Be(expected);
    }

    [Fact]
    public void FtpsDispatcher_OverwriteDisabled_DoesNotSilentlyOverwriteTheRemoteFile()
    {
        FluentFTP.FtpRemoteExists.Overwrite.Should().Be(
            FtpsDeliveryDispatcher.ResolveRemoteExists("{\"host\":\"h\"}"));
        FluentFTP.FtpRemoteExists.Overwrite.Should().NotBe(
            FtpsDeliveryDispatcher.ResolveRemoteExists("{\"host\":\"h\",\"overwriteExisting\":false}"));
    }

    [Fact]
    public void SftpSanitiser_PreservesTheDisambiguatingSuffix()
    {
        // The dispatcher sanitises the filename before joining it to the remote directory. If the
        // sanitiser collapsed the suffix, two distinct names would still land on one remote path.
        var a = $"PO-SHARED-{Guid.NewGuid():N}.xml";
        var b = $"PO-SHARED-{Guid.NewGuid():N}.xml";

        SftpDeliveryDispatcher.SanitiseFileName(a).Should().NotBe(SftpDeliveryDispatcher.SanitiseFileName(b));
        FtpsDeliveryDispatcher.SanitiseFileName(a).Should().NotBe(FtpsDeliveryDispatcher.SanitiseFileName(b));
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeliveryFormatTestDbContext(options);
    }

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedOrderAsync(
        ProcuLinkDbContext db,
        string format,
        Guid? orgId = null,
        Guid? supplierId = null)
    {
        var org = orgId ?? Guid.NewGuid();
        var supplier = supplierId ?? Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = org,
            SupplierId = supplier,
            PoNumber = "PO-SHARED",
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
            Format = format,
            FileKey = $"artifact.{format}",
            CreatedAt = now,
        });

        await db.SaveChangesAsync();
        return (org, supplier, orderId, artifactId);
    }

    private static SupplierDeliveryConfig MakeConfig(
        Guid orgId,
        Guid supplierId,
        DeliveryEncryptionService encryption,
        string protocol) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = protocol,
            AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\",\"host\":\"sftp.supplier.example\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static DeliveryService CreateService(
        ProcuLinkDbContext db,
        IDeliveryDispatcher dispatcher,
        DeliveryEncryptionService? encryption = null,
        ILogger<DeliveryService>? logger = null) =>
        new(
            db,
            new FakeFileStorage(),
            encryption ?? CreateEncryption(),
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Infrastructure.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            logger ?? NullLogger<DeliveryService>.Instance);

    private sealed class CapturingDispatcher : IDeliveryDispatcher
    {
        public CapturingDispatcher(string protocol) => Protocol = protocol;

        public string Protocol { get; }
        public ResendSafety ResendSafety => ResendSafety.Safe;
        public List<string> FileNames { get; } = new();
        public List<string> ContentTypes { get; } = new();

        public Task<DeliveryResult> DispatchAsync(
            byte[] content,
            string fileName,
            string contentType,
            SupplierDeliveryConfig config,
            string decryptedCredentials,
            CancellationToken ct,
            string? idempotencyKey = null)
        {
            FileNames.Add(fileName);
            ContentTypes.Add(contentType);
            return Task.FromResult(new DeliveryResult(true, null, 200));
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);

        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");

        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("payload"u8.ToArray()));

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DeliveryFormatTestDbContext : ProcuLinkDbContext
    {
        public DeliveryFormatTestDbContext(DbContextOptions<ProcuLinkDbContext> options) : base(options) { }

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
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
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
