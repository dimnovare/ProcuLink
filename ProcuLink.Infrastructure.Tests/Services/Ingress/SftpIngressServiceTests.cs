using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Infrastructure.Tests.Services.Ingress;

/// <summary>
/// Unit tests for <see cref="SftpIngressService"/> using an in-memory DbContext and
/// test-double replacements for SFTP connectivity and order creation.
/// </summary>
public class SftpIngressServiceTests
{
    // ── 1. No config → returns 0, no SFTP attempted ──────────────────────────

    [Fact]
    public async Task NullConfig_ReturnsZero_NoConnectionAttempted()
    {
        await using var db = CreateDb();
        var sftpFactory = new RecordingFakeSftpFactory();
        var orders = new NoOpOrderService();
        var svc = MakeService(db, orders, sftpFactory);

        var orgId = Guid.NewGuid(); // no config seeded for this org
        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0);
        sftpFactory.ConnectCalls.Should().Be(0, "no SFTP connection must be attempted when config is absent");
    }

    // ── 2. Config disabled → returns 0 ───────────────────────────────────────

    [Fact]
    public async Task DisabledConfig_ReturnsZero_NoConnectionAttempted()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: false);

        var sftpFactory = new RecordingFakeSftpFactory();
        var orders = new NoOpOrderService();
        var svc = MakeService(db, orders, sftpFactory);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0);
        sftpFactory.ConnectCalls.Should().Be(0, "disabled config must not trigger a connection");
    }

    // ── 3. Already imported file → skipped ───────────────────────────────────

    [Fact]
    public async Task AlreadyImportedFile_IsSkipped_CountIsZero()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/po-001.csv";

        // Seed the dedupe record so the service thinks it was already imported.
        db.Set<ImportedSftpFile>().Add(new ImportedSftpFile
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            RemotePath = remotePath,
            FileHash = "aabbcc",
            ImportedAt = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, content: "po,date\r\n001,2026-05-28"u8.ToArray());
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, "already-imported file must not produce a new order stub");
        orders.CreateStubCalls.Should().Be(0);
    }

    // ── 4. Unsupported extension → skipped ───────────────────────────────────

    [Fact]
    public async Task UnsupportedExtension_IsSkipped_CountIsZero()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/proposal.docx";

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, content: new byte[] { 1, 2, 3 });
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, ".docx is not an accepted extension");
        orders.CreateStubCalls.Should().Be(0, "unsupported file must never reach CreateStubAsync");
    }

    // ── 5. Happy path: new CSV file → imported, dedupe record written ────────

    [Fact]
    public async Task NewCsvFile_IsImported_DedupeRecordWrittenAndCountIsOne()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/po-new.csv";
        var csvBytes = "header1,header2\r\nval1,val2"u8.ToArray();

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, csvBytes);
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(1, "one new CSV file should produce an import count of 1");
        orders.CreateStubCalls.Should().Be(1, "one stub must be created for the new file");

        var dedupe = await db.Set<ImportedSftpFile>()
            .FirstOrDefaultAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);

        dedupe.Should().NotBeNull("a dedupe record must be written after successful import");
        dedupe!.FileHash.Should().NotBeNullOrEmpty("SHA-256 hash must be stored");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SftpIngressService MakeService(
        ProcuLinkDbContext db,
        IOrderService orders,
        ISftpClientFactory sftpFactory)
    {
        // DeliveryEncryptionService requires a real 32-byte key.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var encryption = new DeliveryEncryptionService(config);

        return new SftpIngressService(
            db,
            orders,
            encryption,
            sftpFactory,
            NullLogger<SftpIngressService>.Instance);
    }

    private static async Task SeedConfigAsync(ProcuLinkDbContext db, Guid orgId, bool isEnabled)
    {
        // The password is the empty string encrypted with the all-zero 32-byte key.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var encryption = new DeliveryEncryptionService(config);

        db.Set<SftpIngressConfig>().Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Host = "sftp.example.com",
            Port = 22,
            Username = "testuser",
            EncryptedPassword = encryption.Encrypt("hunter2"),
            RemoteDirectory = "/incoming",
            IsEnabled = isEnabled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SftpIngressTestDbContext(options);
    }

    // ── Test-double SFTP factory ──────────────────────────────────────────────

    /// <summary>Counts Connect() calls; never returns a session (returns no-op).</summary>
    private sealed class RecordingFakeSftpFactory : ISftpClientFactory
    {
        public int ConnectCalls { get; private set; }

        public ISftpSession Connect(string host, int port, string username, string password)
        {
            ConnectCalls++;
            return new EmptySftpSession();
        }
    }

    /// <summary>SFTP factory that presents a single file to the service.</summary>
    private sealed class SingleFileFakeSftpFactory : ISftpClientFactory
    {
        private readonly string _remotePath;
        private readonly byte[] _content;

        public SingleFileFakeSftpFactory(string remotePath, byte[] content)
        {
            _remotePath = remotePath;
            _content = content;
        }

        public ISftpSession Connect(string host, int port, string username, string password)
            => new SingleFileSftpSession(_remotePath, _content);
    }

    private sealed class EmptySftpSession : ISftpSession
    {
        public IEnumerable<string> ListFileNames(string remoteDirectory)
            => Enumerable.Empty<string>();

        public MemoryStream DownloadFile(string remotePath)
            => new MemoryStream();

        public void Dispose() { }
    }

    private sealed class SingleFileSftpSession : ISftpSession
    {
        private readonly string _remotePath;
        private readonly byte[] _content;

        public SingleFileSftpSession(string remotePath, byte[] content)
        {
            _remotePath = remotePath;
            _content = content;
        }

        public IEnumerable<string> ListFileNames(string remoteDirectory)
            => new[] { _remotePath };

        public MemoryStream DownloadFile(string remotePath)
        {
            var ms = new MemoryStream(_content);
            ms.Position = 0;
            return ms;
        }

        public void Dispose() { }
    }

    // ── Test-double order service ─────────────────────────────────────────────

    private sealed class NoOpOrderService : IOrderService
    {
        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Stream fileStream,
            string filename, string contentType, CancellationToken ct)
            => throw new NotImplementedException("NoOpOrderService must not be called.");

        public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid o, Guid s, Stream f, string fn, string ct2, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(Guid o, Guid s, ExtractedOrder order, string source, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ParseStoredFileAsync(Guid o, Guid id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> GetByIdAsync(Guid o, Guid id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(Guid o, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<TransformResponse>> TransformAsync(Guid o, Guid id, OutputFormat fmt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<DownloadUrl>> GetDownloadUrlAsync(Guid o, Guid id, Guid aid, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ResolveAsync(Guid o, Guid id, IReadOnlyList<LineResolution> r, bool s, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class RecordingOrderService : IOrderService
    {
        public int CreateStubCalls { get; private set; }

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Stream fileStream,
            string filename, string contentType, CancellationToken ct)
        {
            CreateStubCalls++;
            var stub = new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(),
                OrgId = organisationId,
                SupplierId = supplierId,
                Status = "parsing",
                SourceFileKey = $"{organisationId}/{Guid.NewGuid()}/{filename}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(stub));
        }

        public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid o, Guid s, Stream f, string fn, string ct2, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(Guid o, Guid s, ExtractedOrder order, string source, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ParseStoredFileAsync(Guid o, Guid id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> GetByIdAsync(Guid o, Guid id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(Guid o, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<TransformResponse>> TransformAsync(Guid o, Guid id, OutputFormat fmt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<DownloadUrl>> GetDownloadUrlAsync(Guid o, Guid id, Guid aid, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ResolveAsync(Guid o, Guid id, IReadOnlyList<LineResolution> r, bool s, CancellationToken ct)
            => throw new NotImplementedException();
    }

    // ── In-memory DbContext ───────────────────────────────────────────────────

    /// <summary>
    /// Minimal in-memory DbContext that materialises only what the SFTP ingress
    /// service touches: SftpIngressConfig and ImportedSftpFile.
    /// Other entities are ignored to avoid fabricating unnecessary fixtures,
    /// following the same <c>modelBuilder.Ignore&lt;T&gt;()</c> pattern used by
    /// <c>InboundEmailTestDbContext</c>.
    /// </summary>
    private sealed class SftpIngressTestDbContext : ProcuLinkDbContext
    {
        public SftpIngressTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<Organisation>();
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<Supplier>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<AuditEvent>();

            // Only materialise the two new entities.
            modelBuilder.Entity<SftpIngressConfig>(b =>
            {
                b.HasKey(x => x.Id);
            });

            modelBuilder.Entity<ImportedSftpFile>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.OrgId, x.RemotePath }).IsUnique();
            });
        }
    }
}
