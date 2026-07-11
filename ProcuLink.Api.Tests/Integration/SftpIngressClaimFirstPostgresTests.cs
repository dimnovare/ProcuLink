using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;
using ProcuLink.Infrastructure.Services.Security;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Proves the claim-first SFTP ingress on REAL Postgres, where the (OrgId, RemotePath) unique
/// index is actually enforced (EF InMemory ignores it). Two guarantees:
/// (a) a re-poll / Hangfire retry of the same file creates NO second order (the committed claim
///     is seen and skipped); and
/// (b) many concurrent same-org polls of the same file create EXACTLY ONE order — the losers hit
///     the unique-index claim (23505) and skip instead of importing a duplicate.
/// Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class SftpIngressClaimFirstPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_sftp_cf_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _pg.StartAsync();

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    private static DeliveryEncryptionService Enc()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            { ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]) })
            .Build();
        return new DeliveryEncryptionService(cfg);
    }

    private static OutboundRequestGuard AllowPrivateGuard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                { ["Delivery:AllowPrivateNetworkTargets"] = "true" })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    private async Task<Guid> SeedOrgSupplierConfigAsync(string remoteDir)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_sftp_{orgId:N}", Name = "SFTP CF Org",
            Slug = $"sftp-cf-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "SFTP Supplier", CreatedAt = now });
        db.Set<SftpIngressConfig>().Add(new SftpIngressConfig
        {
            Id = Guid.NewGuid(), OrgId = orgId, Host = "sftp.example.com", Port = 22, Username = "u",
            EncryptedPassword = Enc().Encrypt("pw"), RemoteDirectory = remoteDir, DefaultSupplierId = supplierId,
            IsEnabled = true, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private SftpIngressService NewService(ProcuLinkDbContext db, CountingOrderService orders, CountingParseEnqueuer enqueuer, ISftpClientFactory factory) =>
        new(db, orders, enqueuer, Enc(), factory, AllowPrivateGuard(), NullLogger<SftpIngressService>.Instance);

    [DockerRequiredFact]
    public async Task RePoll_AfterImport_CreatesNoSecondOrder()
    {
        const string remotePath = "/incoming/po-retry.csv";
        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var orders = new CountingOrderService();
        var enqueuer = new CountingParseEnqueuer();
        var factory = new OneFileSftpFactory(remotePath, "h1,h2\r\nv1,v2"u8.ToArray());

        await using (var db1 = new ProcuLinkDbContext(_options!))
            (await NewService(db1, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(1, "the first poll imports the new file");

        // Second poll = a Hangfire retry / next scheduled poll with the same file still present.
        await using (var db2 = new ProcuLinkDbContext(_options!))
            (await NewService(db2, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None))
                .Should().Be(0, "the committed claim from poll 1 must make the re-poll a no-op");

        orders.TotalCreates.Should().Be(1, "the file must produce exactly ONE order stub across re-polls");
        enqueuer.Count.Should().Be(1);

        await using var verify = new ProcuLinkDbContext(_options!);
        (await verify.Set<ImportedSftpFile>().CountAsync(f => f.OrgId == orgId && f.RemotePath == remotePath))
            .Should().Be(1, "exactly one dedupe ledger row");
    }

    [DockerRequiredFact]
    public async Task ConcurrentPolls_OfSameFile_CreateExactlyOneOrder()
    {
        const int workers = 8;
        const string remotePath = "/incoming/po-concurrent.csv";
        var orgId = await SeedOrgSupplierConfigAsync("/incoming");
        var orders = new CountingOrderService();       // shared across all polls
        var enqueuer = new CountingParseEnqueuer();
        var factory = new OneFileSftpFactory(remotePath, "h1,h2\r\nv1,v2"u8.ToArray());

        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            await using var db = new ProcuLinkDbContext(_options!);
            return await NewService(db, orders, enqueuer, factory).PollAsync(orgId, CancellationToken.None);
        });
        var results = await Task.WhenAll(tasks);

        results.Sum().Should().Be(1,
            "exactly one concurrent poll may import the file; the rest hit the unique-index claim and skip");
        orders.TotalCreates.Should().Be(1, "concurrent polls of the same file must create exactly ONE order stub");
        enqueuer.Count.Should().Be(1);

        await using var verify = new ProcuLinkDbContext(_options!);
        (await verify.Set<ImportedSftpFile>().CountAsync(f => f.OrgId == orgId && f.RemotePath == remotePath))
            .Should().Be(1, "exactly one dedupe ledger row survives the concurrent race");
    }

    // ── SFTP test doubles ─────────────────────────────────────────────────────

    private sealed class OneFileSftpFactory : ISftpClientFactory
    {
        private readonly string _remotePath;
        private readonly byte[] _content;
        public OneFileSftpFactory(string remotePath, byte[] content) { _remotePath = remotePath; _content = content; }
        public ISftpSession Connect(string host, int port, string username, string password)
            => new OneFileSftpSession(_remotePath, _content);
    }

    private sealed class OneFileSftpSession : ISftpSession
    {
        private readonly string _remotePath;
        private readonly byte[] _content;
        public OneFileSftpSession(string remotePath, byte[] content) { _remotePath = remotePath; _content = content; }
        public IEnumerable<string> ListFileNames(string remoteDirectory) => new[] { _remotePath };
        public MemoryStream DownloadFile(string remotePath) => new(_content);
        public Stream OpenRead(string remotePath) => new MemoryStream(_content);
        public void Dispose() { }
    }
}
