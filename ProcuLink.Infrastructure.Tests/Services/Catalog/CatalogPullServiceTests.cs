using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Catalog;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Infrastructure.Tests.Services.Catalog;

/// <summary>
/// BE-4 gate for <see cref="CatalogPullService"/> — the single hardened fetch pipeline.
/// Fake per-protocol seams; REAL guard, encryption, parser, and catalog sink, so every
/// security control is exercised exactly as in production:
///  • happy path persists counts + hash (and the sink really upserts),
///  • unchanged-hash short-circuits BEFORE parsing (proven with bytes that cannot parse),
///  • SSRF guard rejects before the factory is ever invoked,
///  • H2: an oversized/lying stream aborts mid-copy at cap+1,
///  • H3: a stalling connect is cut by the overall deadline,
///  • M4: a raw exception carrying user@host NEVER reaches the rethrown message,
///  • decrypt-null and deleted-supplier map to their honest enumerated messages,
///  • test-fetch reports honestly and provably writes nothing.
/// </summary>
public class CatalogPullServiceTests
{
    private const string Csv = "code,name,unit_price\nA-1,Widget,9.50\nB-2,Gadget,1.25\n";
    private static readonly byte[] CsvBytes = Encoding.UTF8.GetBytes(Csv);

    // ── harness ───────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IConfiguration Config(bool allowPrivate) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()),
            ["Delivery:AllowPrivateNetworkTargets"] = allowPrivate ? "true" : "false",
        })
        .Build();

    private sealed record Harness(
        ProcuLinkDbContext Db,
        CatalogPullService Service,
        DeliveryEncryptionService Encryption,
        RecordingSftpFactory Sftp,
        RecordingCatalogSink Sink,
        Guid OrgId,
        Guid SupplierId,
        Guid SourceId);

    private static async Task<Harness> BuildAsync(
        byte[]? sftpContent = null,
        bool allowPrivate = true,
        string host = "files.example.com",
        string protocol = "sftp",
        string? password = "secret",
        Action<SupplierCatalogSource>? mutateSource = null,
        bool deleteSupplier = false,
        Func<Stream>? sftpStreamFactory = null,
        TimeSpan? connectDelay = null)
    {
        var db = NewDb();
        var config = Config(allowPrivate);
        var encryption = new DeliveryEncryptionService(config);
        var guard = new OutboundRequestGuard(config, NullLogger<OutboundRequestGuard>.Instance);
        var sftp = new RecordingSftpFactory(
            sftpStreamFactory ?? (() => new MemoryStream(sftpContent ?? CsvBytes)),
            connectDelay ?? TimeSpan.Zero);
        var sink = new RecordingCatalogSink(new SupplierCatalogService(db));

        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Pull Org",
            Slug = $"pull-{orgId:N}",
            CreatedAt = DateTime.UtcNow,
        });
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Pull Supplier",
            CreatedAt = DateTime.UtcNow,
            DeletedAt = deleteSupplier ? DateTime.UtcNow : null,
        });

        var source = new SupplierCatalogSource
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = protocol,
            Host = host,
            Port = protocol == "sftp" ? 22 : 21,
            Username = "catalog",
            EncryptedPassword = password is null ? null : encryption.Encrypt(password),
            RemotePath = "/exports/catalog.csv",
            FileFormat = "auto",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        mutateSource?.Invoke(source);
        db.SupplierCatalogSources.Add(source);
        await db.SaveChangesAsync();

        var service = new CatalogPullService(
            db, encryption, guard, sftp, new ThrowingFtpFactory(),
            new ThrowingHttpClientFactory(), sink,
            NullLogger<CatalogPullService>.Instance);

        return new Harness(db, service, encryption, sftp, sink, orgId, supplierId, source.Id);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // ── happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pull_HappyPath_UpsertsAndPersistsCountsAndHash()
    {
        var h = await BuildAsync();

        var result = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        result.Status.Should().Be("ok");
        result.Created.Should().Be(2);
        result.Updated.Should().Be(0);
        result.FileHash.Should().Be(Sha256Hex(CsvBytes));

        var source = await h.Db.SupplierCatalogSources.SingleAsync(s => s.Id == h.SourceId);
        source.LastSyncStatus.Should().Be("ok");
        source.LastSyncError.Should().BeNull();
        source.LastSyncCreated.Should().Be(2);
        source.LastSyncUpdated.Should().Be(0);
        source.LastFileHash.Should().Be(Sha256Hex(CsvBytes));
        source.LastSyncAt.Should().NotBeNull();

        (await h.Db.SupplierProducts.CountAsync(p => p.OrgId == h.OrgId && p.SupplierId == h.SupplierId))
            .Should().Be(2);
    }

    [Fact]
    public async Task Pull_IsIdempotent_SecondRunWithSameFileIsUnchanged()
    {
        var h = await BuildAsync();

        var first = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);
        first.Status.Should().Be("ok");

        var second = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        second.Status.Should().Be("unchanged");
        (await h.Db.SupplierProducts.CountAsync()).Should().Be(2, "the upsert sink must not have run again");
        h.Sink.UpsertCalls.Should().Be(1);
    }

    // ── unchanged-hash short-circuit (no parse) ───────────────────────────────

    [Fact]
    public async Task Pull_UnchangedHash_ShortCircuits_WithoutParsing()
    {
        // The bytes are NOT a valid zip, and the source forces FileFormat=xlsx — if the
        // parser ran, XLWorkbook would throw and the pull would fail. A clean "unchanged"
        // therefore PROVES the parse step was skipped.
        var garbage = Encoding.UTF8.GetBytes("definitely-not-an-xlsx");
        var h = await BuildAsync(
            sftpContent: garbage,
            mutateSource: s =>
            {
                s.FileFormat = "xlsx";
                s.LastFileHash = Sha256Hex(garbage);
                s.LastSyncStatus = "ok";
                s.LastSyncError = null;
            });

        var result = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        result.Status.Should().Be("unchanged");
        h.Sink.UpsertCalls.Should().Be(0);

        var source = await h.Db.SupplierCatalogSources.SingleAsync(s => s.Id == h.SourceId);
        source.LastSyncStatus.Should().Be("unchanged");
        source.LastSyncError.Should().BeNull();
    }

    [Fact]
    public async Task Pull_UnchangedHash_UnderJobSoftLock_RunningStatus_StillSkips()
    {
        // The job's soft lock flips status to 'running' before PullAsync — the skip must
        // still fire (LastSyncError == null marks the previous run as completed-fine).
        var garbage = Encoding.UTF8.GetBytes("definitely-not-an-xlsx");
        var h = await BuildAsync(
            sftpContent: garbage,
            mutateSource: s =>
            {
                s.FileFormat = "xlsx";
                s.LastFileHash = Sha256Hex(garbage);
                s.LastSyncStatus = "running";
                s.LastSyncError = null;
            });

        var result = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        result.Status.Should().Be("unchanged");
        h.Sink.UpsertCalls.Should().Be(0);
    }

    [Fact]
    public async Task Pull_SameHash_AfterPreviousFailure_ReparsesInsteadOfSkipping()
    {
        var h = await BuildAsync(
            mutateSource: s =>
            {
                s.LastFileHash = Sha256Hex(CsvBytes);
                s.LastSyncStatus = "failed";
                s.LastSyncError = "Could not connect to the server.";
            });

        var result = await h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        result.Status.Should().Be("ok", "a failed previous run must not be masked by the hash skip");
        h.Sink.UpsertCalls.Should().Be(1);
    }

    // ── SSRF guard (before connect) ───────────────────────────────────────────

    [Fact]
    public async Task Pull_GuardRejectsPrivateHost_BeforeFactoryIsInvoked()
    {
        var h = await BuildAsync(allowPrivate: false, host: "10.0.0.1");

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        (await act.Should().ThrowAsync<CatalogSyncException>())
            .Which.Message.Should().Be(CatalogPullService.ErrHostNotAllowed);
        h.Sftp.ConnectCalls.Should().Be(0, "the SSRF guard must run BEFORE any connection attempt");
    }

    // ── H2: oversized / lying stream ──────────────────────────────────────────

    [Fact]
    public async Task Pull_OversizeStream_AbortsMidCopy_WithSafeMessage()
    {
        var h = await BuildAsync(sftpStreamFactory: () => new EndlessZeroStream());

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        (await act.Should().ThrowAsync<CatalogSyncException>())
            .Which.Message.Should().Be("Catalog file exceeds 256 MB.");
        h.Sink.UpsertCalls.Should().Be(0);
    }

    // ── H3: overall deadline on a stalling connect ────────────────────────────

    [Fact]
    public async Task Pull_StallingConnect_IsCutByOverallDeadline()
    {
        var h = await BuildAsync(connectDelay: TimeSpan.FromSeconds(20));
        h.Service.OverallDeadline = TimeSpan.FromMilliseconds(250);

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        (await act.Should().ThrowAsync<CatalogSyncException>())
            .Which.Message.Should().Be(CatalogPullService.ErrTimedOut);
    }

    // ── honest credential / supplier failures ─────────────────────────────────

    [Fact]
    public async Task Pull_UndecryptableCredentials_MapsToHonestMessage()
    {
        var h = await BuildAsync(mutateSource: s => s.EncryptedPassword = "bm90LWEtdmFsaWQtZW52ZWxvcGU=");

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        (await act.Should().ThrowAsync<CatalogSyncException>())
            .Which.Message.Should().Be(CatalogPullService.ErrCredentialsUnreadable);
        h.Sftp.ConnectCalls.Should().Be(0);
    }

    [Fact]
    public async Task Pull_SoftDeletedSupplier_MapsToHonestMessage()
    {
        var h = await BuildAsync(deleteSupplier: true);

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        (await act.Should().ThrowAsync<CatalogSyncException>())
            .Which.Message.Should().Be(CatalogPullService.ErrSupplierGone);
        h.Sftp.ConnectCalls.Should().Be(0);
    }

    // ── M4: sanitized error mapping ───────────────────────────────────────────

    [Fact]
    public async Task Pull_RawExceptionWithUserAtHost_NeverLeaks_AndCarriesNoInner()
    {
        const string leaky = "connect failed for catalog-user@supplier-host.internal banner SSH-2.0-OpenSSH";
        var h = await BuildAsync(sftpStreamFactory: () => throw new InvalidOperationException(leaky));

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<CatalogSyncException>()).Which;
        thrown.Message.Should().Be(CatalogPullService.ErrUnexpected);
        thrown.Message.Should().NotContain("catalog-user");
        thrown.Message.Should().NotContain("supplier-host");
        thrown.InnerException.Should().BeNull("M4: Hangfire/Sentry must only ever see sanitized text");
    }

    [Fact]
    public async Task Pull_AuthFailure_MapsToEnumeratedMessage()
    {
        var h = await BuildAsync(sftpStreamFactory: () =>
            throw new Renci.SshNet.Common.SshAuthenticationException("Permission denied for catalog-user@10.1.2.3"));

        var act = () => h.Service.PullAsync(h.OrgId, h.SourceId, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<CatalogSyncException>()).Which;
        thrown.Message.Should().Be(CatalogPullService.ErrAuthFailed);
        thrown.Message.Should().NotContain("10.1.2.3");
        thrown.InnerException.Should().BeNull();
    }

    // ── BE-5: test-fetch (read-only honesty probe) ────────────────────────────

    [Fact]
    public async Task TestFetch_ReportsMapping_Samples_AndCounts()
    {
        var csv = "sku,description,unit_price,warehouse_zone\n" +
                  string.Join("\n", Enumerable.Range(1, 7).Select(i => $"C-{i},Item {i},{i}.00,Z{i}"));
        var h = await BuildAsync(sftpContent: Encoding.UTF8.GetBytes(csv));

        var result = await h.Service.TestFetchAsync(h.OrgId, h.SupplierId, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Error.Should().BeNull();
        result.FileName.Should().Be("catalog.csv");
        result.DetectedFormat.Should().Be("csv");
        result.HeaderColumns.Should().Equal("sku", "description", "unit_price", "warehouse_zone");
        result.MappedFields.Should().Contain(new KeyValuePair<string, string>("sku", "code"));
        result.MappedFields.Should().Contain(new KeyValuePair<string, string>("description", "name"));
        result.MappedFields.Should().Contain(new KeyValuePair<string, string>("unit_price", "price"));
        result.UnmappedColumns.Should().Equal("warehouse_zone");
        result.ParsedRows.Should().Be(7);
        result.RowsWithCode.Should().Be(7);
        result.SampleRows.Should().HaveCount(5, "sample rows are capped at 5");
        result.SampleRows[0].Code.Should().Be("C-1");
    }

    [Fact]
    public async Task TestFetch_WritesNothing_SourceRowAndCatalogUntouched()
    {
        var h = await BuildAsync();
        var before = await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync(s => s.Id == h.SourceId);

        var result = await h.Service.TestFetchAsync(h.OrgId, h.SupplierId, CancellationToken.None);

        result.Ok.Should().BeTrue();
        h.Sink.UpsertCalls.Should().Be(0, "test-fetch must never import");
        (await h.Db.SupplierProducts.CountAsync()).Should().Be(0);

        var after = await h.Db.SupplierCatalogSources.AsNoTracking().SingleAsync(s => s.Id == h.SourceId);
        after.LastSyncAt.Should().Be(before.LastSyncAt);
        after.LastSyncStatus.Should().Be(before.LastSyncStatus);
        after.LastSyncError.Should().Be(before.LastSyncError);
        after.LastFileHash.Should().Be(before.LastFileHash);
        after.UpdatedAt.Should().Be(before.UpdatedAt);
    }

    [Fact]
    public async Task TestFetch_GuardReject_ReturnsOkFalse_WithSafeMessage()
    {
        var h = await BuildAsync(allowPrivate: false, host: "192.168.1.10");

        var result = await h.Service.TestFetchAsync(h.OrgId, h.SupplierId, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(CatalogPullService.ErrHostNotAllowed);
        h.Sftp.ConnectCalls.Should().Be(0);
    }

    [Fact]
    public async Task TestFetch_Oversize_ReturnsOkFalse_WithSafeMessage()
    {
        var h = await BuildAsync(sftpStreamFactory: () => new EndlessZeroStream());

        var result = await h.Service.TestFetchAsync(h.OrgId, h.SupplierId, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("Catalog file exceeds 256 MB.");
    }

    [Fact]
    public async Task TestFetch_NoSourceConfigured_ReturnsOkFalse()
    {
        var h = await BuildAsync();
        var otherSupplier = Guid.NewGuid(); // no source for this supplier

        var result = await h.Service.TestFetchAsync(h.OrgId, otherSupplier, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be(CatalogPullService.ErrNoSourceConfigured);
    }

    // ── test doubles ──────────────────────────────────────────────────────────

    /// <summary>SFTP factory: counts connects, optional stalling connect, stream-per-open.</summary>
    internal sealed class RecordingSftpFactory : ISftpClientFactory
    {
        private readonly Func<Stream> _streamFactory;
        private readonly TimeSpan _connectDelay;

        public int ConnectCalls { get; private set; }

        public RecordingSftpFactory(Func<Stream> streamFactory, TimeSpan connectDelay)
        {
            _streamFactory = streamFactory;
            _connectDelay = connectDelay;
        }

        public ISftpSession Connect(string host, int port, string username, string password)
        {
            ConnectCalls++;
            if (_connectDelay > TimeSpan.Zero)
                Thread.Sleep(_connectDelay); // simulates a stalling server (sync, like SSH.NET)
            return new FakeSession(_streamFactory);
        }

        private sealed class FakeSession : ISftpSession
        {
            private readonly Func<Stream> _streamFactory;
            public FakeSession(Func<Stream> streamFactory) => _streamFactory = streamFactory;

            public IEnumerable<string> ListFileNames(string remoteDirectory) => Enumerable.Empty<string>();
            public MemoryStream DownloadFile(string remotePath) => throw new NotSupportedException();
            public Stream OpenRead(string remotePath) => _streamFactory();
            public void Dispose() { }
        }
    }

    /// <summary>The FTP seam must never be touched by sftp-protocol tests.</summary>
    private sealed class ThrowingFtpFactory : IFtpFetchClientFactory
    {
        public IFtpFetchSession Connect(string host, int port, string username, string password, bool explicitTls)
            => throw new InvalidOperationException("FTP factory must not be invoked in these tests.");
    }

    /// <summary>The HTTP client factory must never be touched by sftp/ftp-protocol tests.</summary>
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("HTTP client factory must not be invoked in these tests.");
    }

    /// <summary>Wraps the REAL catalog sink, counting upsert calls.</summary>
    internal sealed class RecordingCatalogSink : ISupplierCatalogService
    {
        private readonly ISupplierCatalogService _inner;
        public int UpsertCalls { get; private set; }

        public RecordingCatalogSink(ISupplierCatalogService inner) => _inner = inner;

        public Task<IReadOnlyList<SupplierProduct>> ListAsync(Guid orgId, Guid supplierId, string? query, int? take, CancellationToken ct)
            => _inner.ListAsync(orgId, supplierId, query, take, ct);

        public Task<(int Created, int Updated)> UpsertManyAsync(Guid orgId, Guid supplierId, IEnumerable<SupplierProduct> products, CancellationToken ct)
        {
            UpsertCalls++;
            return _inner.UpsertManyAsync(orgId, supplierId, products, ct);
        }

        public Task<int> CountAsync(Guid orgId, Guid supplierId, CancellationToken ct)
            => _inner.CountAsync(orgId, supplierId, ct);

        public Task<int> DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct)
            => _inner.DeleteAsync(orgId, supplierId, ct);
    }

    /// <summary>Infinite zero stream — the lying/oversized server.</summary>
    private sealed class EndlessZeroStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
