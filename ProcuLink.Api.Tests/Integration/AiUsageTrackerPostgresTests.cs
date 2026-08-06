using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Audit batch A #5 — proves on REAL Postgres that <see cref="AiUsageTracker.IncrementAsync"/>
/// is concurrency-safe: parallel workers issue an atomic relative UPDATE
/// (<c>tokens_used = tokens_used + n</c>) instead of a read-modify-write that loses
/// increments, and the first-insert race on the (org_id, year, month) primary key
/// (Postgres 23505) recovers into the same atomic update rather than throwing out of a
/// Hangfire job. Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class AiUsageTrackerPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_aiusage");

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    [DockerRequiredFact]
    public async Task ParallelIncrements_NeverLoseAnUpdate_IncludingTheFirstInsertRace()
    {
        var orgId = Guid.NewGuid();
        const int workers = 16;
        const long tokensEach = 100;

        // Each "worker" gets its own DbContext + tracker (a DbContext is not thread-safe),
        // exactly like parallel Hangfire job activations. All start with NO row present,
        // so several also race the first INSERT (23505 → atomic-update recovery).
        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            await using var db = new ProcuLinkDbContext(_options!);
            await new AiUsageTracker(db, EmptyConfig()).IncrementAsync(orgId, tokensEach);
        });
        await Task.WhenAll(tasks);

        await using var verify = new ProcuLinkDbContext(_options!);
        var row = await verify.AiUsageMonthly.AsNoTracking().SingleAsync(u => u.OrgId == orgId);
        Assert.Equal(workers * tokensEach, row.TokensUsed); // pre-fix this under-counted
    }

    [DockerRequiredFact]
    public async Task SequentialIncrements_AccumulateOnTheExistingRow()
    {
        var orgId = Guid.NewGuid();

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var tracker = new AiUsageTracker(db, EmptyConfig());
            await tracker.IncrementAsync(orgId, 250); // insert path
            await tracker.IncrementAsync(orgId, 750); // atomic-update path
        }

        await using var verify = new ProcuLinkDbContext(_options!);
        var row = await verify.AiUsageMonthly.AsNoTracking().SingleAsync(u => u.OrgId == orgId);
        Assert.Equal(1000, row.TokensUsed);
    }
}
