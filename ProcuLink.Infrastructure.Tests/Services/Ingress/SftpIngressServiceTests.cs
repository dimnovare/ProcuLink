using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

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

    // ── 4. Enabled config without supplier → skipped before connecting ──────

    [Fact]
    public async Task EnabledConfigWithoutDefaultSupplier_ReturnsZero_NoConnectionAttempted()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true, createDefaultSupplier: false);

        var sftpFactory = new RecordingFakeSftpFactory();
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, sftpFactory);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, "pull ingress must not create org-scoped orders without a supplier route");
        sftpFactory.ConnectCalls.Should().Be(0, "unsafe config must stop before touching the external SFTP server");
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

    // ── Oversized file → skipped before CreateStubAsync ──────────────────────

    [Fact]
    public async Task OversizedFile_IsSkipped_NeverReachesCreateStub()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/huge.csv";
        var huge = new byte[IngressLimits.MaxFileBytes + 1];

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, huge);
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, fakeSftp);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, "an oversized file must not be imported");
        orders.CreateStubCalls.Should().Be(0, "oversized file must never reach CreateStubAsync");
    }

    // ── SEC-1: SSRF — private host blocked BEFORE connect ────────────────────

    [Fact]
    public async Task PrivateHost_IsBlockedBySsrfGuard_NoConnectionAttempted()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        // Private literal IP (RFC-1918) — the strict guard blocks it without any DNS lookup.
        await SeedConfigAsync(db, orgId, isEnabled: true, host: "10.0.0.5");

        var sftpFactory = new RecordingFakeSftpFactory();
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, sftpFactory, StrictGuard());

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, "a poll against a private/internal host must be refused");
        sftpFactory.ConnectCalls.Should().Be(0,
            "the SSRF guard must reject the host BEFORE the SFTP factory is ever invoked");
        orders.CreateStubCalls.Should().Be(0);
    }

    // ── SEC-1 / H2: bounded read aborts on a stream that lies about its length ─

    [Fact]
    public async Task LyingOversizedStream_IsAbortedMidCopy_NeverReachesCreateStub()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/lying.csv";
        // A stream that yields cap + 1 MB of bytes regardless of any declared size — the
        // bounded read must abort at cap+1 instead of materializing the whole thing.
        var lyingFactory = new LyingStreamSftpFactory(remotePath, totalBytes: IngressLimits.MaxFileBytes + 1_048_576);
        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, lyingFactory);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(0, "a file exceeding the cap must be skipped, not imported");
        orders.CreateStubCalls.Should().Be(0, "the oversized file must never reach CreateStubAsync");
    }

    // ── 5. Happy path: new CSV file → imported, dedupe record written, parse job enqueued ──

    [Fact]
    public async Task NewCsvFile_IsImported_DedupeRecordWrittenAndCountIsOne()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        var supplierId = await SeedConfigAsync(db, orgId, isEnabled: true);

        const string remotePath = "/incoming/po-new.csv";
        var csvBytes = "header1,header2\r\nval1,val2"u8.ToArray();

        var fakeSftp = new SingleFileFakeSftpFactory(remotePath, csvBytes);
        var orders = new RecordingOrderService();
        var enqueuer = new FakeParseJobEnqueuer();
        var svc = MakeService(db, orders, fakeSftp, enqueuer: enqueuer);

        var result = await svc.PollAsync(orgId, default);

        result.Should().Be(1, "one new CSV file should produce an import count of 1");
        orders.CreateStubCalls.Should().Be(1, "one stub must be created for the new file");
        orders.SupplierIds.Should().ContainSingle().Which.Should().Be(
            supplierId!.Value,
            "SFTP pull imports must be assigned to the configured supplier, not Guid.Empty");

        enqueuer.EnqueuedOrderIds.Should().ContainSingle(
            "a parse job must be enqueued for the imported order");
        enqueuer.EnqueuedOrgIds.Should().ContainSingle().Which.Should().Be(
            orgId, "parse job must be scoped to the correct org");

        var dedupe = await db.Set<ImportedSftpFile>()
            .FirstOrDefaultAsync(f => f.OrgId == orgId && f.RemotePath == remotePath);

        dedupe.Should().NotBeNull("a dedupe record must be written after successful import");
        dedupe!.FileHash.Should().NotBeNullOrEmpty("SHA-256 hash must be stored");
    }

    // ── LIVE: real SFTP poll against a real SFTP server ──────────────────────
    // Gated behind PROCULINK_LIVE_ENDPOINT_TESTS=1; connects to a real SFTP
    // server (env PROCULINK_LIVE_SFTP_*) with the PRODUCTION RenciSftpClientFactory,
    // lists + downloads a real PO file, and imports it via the in-memory DbContext.
    [Fact]
    [Trait("Category", "LiveEndpoint")]
    public async Task Live_SftpIngress_RealPollImportsFile()
    {
        if (Environment.GetEnvironmentVariable("PROCULINK_LIVE_ENDPOINT_TESTS") != "1") return;
        var host = Environment.GetEnvironmentVariable("PROCULINK_LIVE_SFTP_HOST") ?? "";
        if (string.IsNullOrEmpty(host)) return;

        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var encConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
            .Build();
        var encryption = new DeliveryEncryptionService(encConfig);

        db.Set<Supplier>().Add(new Supplier
        { Id = supplierId, OrgId = orgId, Name = "Live SFTP supplier", CreatedAt = DateTime.UtcNow });
        db.Set<SftpIngressConfig>().Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Host = host,
            Port = int.TryParse(Environment.GetEnvironmentVariable("PROCULINK_LIVE_SFTP_PORT"), out var p) ? p : 22,
            Username = Environment.GetEnvironmentVariable("PROCULINK_LIVE_SFTP_USER") ?? "",
            EncryptedPassword = encryption.Encrypt(Environment.GetEnvironmentVariable("PROCULINK_LIVE_SFTP_PASS") ?? ""),
            RemoteDirectory = Environment.GetEnvironmentVariable("PROCULINK_LIVE_SFTP_INGEST_DIR") ?? "/upload",
            DefaultSupplierId = supplierId,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var orders = new RecordingOrderService();
        var svc = MakeService(db, orders, new RenciSftpClientFactory(), enqueuer: new FakeParseJobEnqueuer());

        var imported = await svc.PollAsync(orgId, default);

        imported.Should().BeGreaterThanOrEqualTo(1, "the real SFTP poll should import at least one PO file from the server");
        orders.CreateStubCalls.Should().BeGreaterThanOrEqualTo(1);
        orders.SupplierIds.Should().Contain(supplierId, "imports must be routed to the configured default supplier");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SftpIngressService MakeService(
        ProcuLinkDbContext db,
        IOrderService orders,
        ISftpClientFactory sftpFactory,
        OutboundRequestGuard? guard = null,
        IParseJobEnqueuer? enqueuer = null)
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
            enqueuer ?? new FakeParseJobEnqueuer(),
            encryption,
            sftpFactory,
            // Default guard allows private targets so the fixture host (sftp.example.com / a
            // private literal) passes the network-range check without a real DNS lookup — the
            // SSRF-blocked test below supplies a strict (flag=false) guard explicitly.
            guard ?? AllowPrivateGuard(),
            NullLogger<SftpIngressService>.Instance);
    }

    /// <summary>Guard with AllowPrivateNetworkTargets=true — skips range validation (no DNS).</summary>
    private static OutboundRequestGuard AllowPrivateGuard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    /// <summary>Strict guard (flag=false) — enforces the SSRF network-range blocklist.</summary>
    private static OutboundRequestGuard StrictGuard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "false" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    private static async Task<Guid?> SeedConfigAsync(
        ProcuLinkDbContext db,
        Guid orgId,
        bool isEnabled,
        bool createDefaultSupplier = true,
        string host = "sftp.example.com")
    {
        // The password is the empty string encrypted with the all-zero 32-byte key.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var encryption = new DeliveryEncryptionService(config);

        Guid? supplierId = null;
        if (createDefaultSupplier)
        {
            supplierId = Guid.NewGuid();
            db.Set<Supplier>().Add(new Supplier
            {
                Id = supplierId.Value,
                OrgId = orgId,
                Name = "SFTP supplier",
                CreatedAt = DateTime.UtcNow,
            });
        }

        db.Set<SftpIngressConfig>().Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Host = host,
            Port = 22,
            Username = "testuser",
            EncryptedPassword = encryption.Encrypt("hunter2"),
            RemoteDirectory = "/incoming",
            DefaultSupplierId = supplierId,
            IsEnabled = isEnabled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return supplierId;
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

        public Stream OpenRead(string remotePath)
            => new MemoryStream();

        public void Dispose() { }
    }

    /// <summary>
    /// SFTP factory whose <c>OpenRead</c> returns a forward-only stream that yields
    /// <c>totalBytes</c> bytes — used to prove the bounded read aborts at cap+1 instead
    /// of buffering the whole (oversized) file (H2 regression).
    /// </summary>
    private sealed class LyingStreamSftpFactory : ISftpClientFactory
    {
        private readonly string _remotePath;
        private readonly long _totalBytes;

        public LyingStreamSftpFactory(string remotePath, long totalBytes)
        {
            _remotePath = remotePath;
            _totalBytes = totalBytes;
        }

        public ISftpSession Connect(string host, int port, string username, string password)
            => new LyingStreamSftpSession(_remotePath, _totalBytes);
    }

    private sealed class LyingStreamSftpSession : ISftpSession
    {
        private readonly string _remotePath;
        private readonly long _totalBytes;

        public LyingStreamSftpSession(string remotePath, long totalBytes)
        {
            _remotePath = remotePath;
            _totalBytes = totalBytes;
        }

        public IEnumerable<string> ListFileNames(string remoteDirectory) => new[] { _remotePath };
        public MemoryStream DownloadFile(string remotePath) => throw new NotSupportedException();
        public Stream OpenRead(string remotePath) => new EndlessForwardStream(_totalBytes);
        public void Dispose() { }
    }

    /// <summary>Forward-only read stream that produces <c>length</c> zero bytes; not seekable.</summary>
    private sealed class EndlessForwardStream : Stream
    {
        private readonly long _length;
        private long _produced;

        public EndlessForwardStream(long length) => _length = length;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _produced;
            if (remaining <= 0) return 0;
            var n = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, n);
            _produced += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _produced; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

        public Stream OpenRead(string remotePath)
            => new MemoryStream(_content);

        public void Dispose() { }
    }

    // ── Test-double parse-job enqueuer ────────────────────────────────────────

    private sealed class FakeParseJobEnqueuer : IParseJobEnqueuer
    {
        public List<Guid> EnqueuedOrderIds { get; } = new();
        public List<Guid> EnqueuedOrgIds   { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            EnqueuedOrderIds.Add(orderId);
            EnqueuedOrgIds.Add(orgId);
            return Task.CompletedTask;
        }
    }

    // ── Test-double order service ─────────────────────────────────────────────

    private sealed class NoOpOrderService : IOrderService
    {
        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Stream fileStream,
            string filename, string contentType, CancellationToken ct)
            => throw new NotImplementedException("NoOpOrderService must not be called.");

        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => throw new NotImplementedException("NoOpOrderService must not be called.");

        public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid o, Guid s, Stream f, string fn, string ct2, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(Guid o, Guid s, ExtractedOrder order, string source, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<ParsedFileOutput>> ParseStoredFileAsync(Guid o, Guid id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> GetByIdAsync(Guid o, Guid id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(Guid o, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(Guid organisationId, int page, int pageSize, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListWindowAsync(Guid organisationId, int skip, int take, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<TransformResponse>> TransformAsync(Guid o, Guid id, OutputFormat fmt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<DownloadUrl>> GetDownloadUrlAsync(Guid o, Guid id, Guid aid, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ResolveAsync(Guid o, Guid id, IReadOnlyList<LineResolution> r, bool s, CancellationToken ct, ResolveHeaderFields? header = null)
            => throw new NotImplementedException();
        public Task<Result<int>> AcceptAiSuggestionsAsync(Guid o, Guid id, double minConfidence, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(Guid organisationId, Guid orderId, string reason, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class RecordingOrderService : IOrderService
    {
        public int CreateStubCalls { get; private set; }
        public List<Guid> SupplierIds { get; } = new();

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Stream fileStream,
            string filename, string contentType, CancellationToken ct)
        {
            CreateStubCalls++;
            SupplierIds.Add(supplierId);
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

        // Unrouted hold path records a Guid.Empty supplier so tests can assert it was used.
        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => CreateStubAsync(organisationId, Guid.Empty, fileStream, filename, contentType, ct);

        public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid o, Guid s, Stream f, string fn, string ct2, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(Guid o, Guid s, ExtractedOrder order, string source, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<ParsedFileOutput>> ParseStoredFileAsync(Guid o, Guid id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> GetByIdAsync(Guid o, Guid id, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(Guid o, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(Guid organisationId, int page, int pageSize, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListWindowAsync(Guid organisationId, int skip, int take, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<TransformResponse>> TransformAsync(Guid o, Guid id, OutputFormat fmt, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<DownloadUrl>> GetDownloadUrlAsync(Guid o, Guid id, Guid aid, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ResolveAsync(Guid o, Guid id, IReadOnlyList<LineResolution> r, bool s, CancellationToken ct, ResolveHeaderFields? header = null)
            => throw new NotImplementedException();
        public Task<Result<int>> AcceptAiSuggestionsAsync(Guid o, Guid id, double minConfidence, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(Guid organisationId, Guid orderId, string reason, CancellationToken ct)
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
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<AuditEvent>();
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

            // Only materialise the two new entities.
            modelBuilder.Entity<SftpIngressConfig>(b =>
            {
                b.HasKey(x => x.Id);
            });

            modelBuilder.Entity<Supplier>(b =>
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
