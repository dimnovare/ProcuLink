using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Api.Tests.TestSupport;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// The exception list's window has to be applied by the DATABASE, and its sort has to be TOTAL.
///
/// <para><b>Why the in-memory tests cannot settle either point.</b> The in-memory provider will
/// happily fetch every row and slice the result afterwards, which looks identical from the
/// caller's side and fixes nothing — the response is bounded while the query is not. And a
/// tie-break column is invisible in the rows of a small table, because it usually comes back in
/// insertion order whether or not the sort is total. Both claims are about the SQL, so both are
/// asserted against the SQL the service actually sends.</para>
///
/// <para><b>Why the tie-break matters here specifically.</b> <c>ReconcileAsync</c> stamps every
/// exception it opens in one pass with the same <c>now</c>, so rows sharing a <c>created_at</c> to
/// the tick are the normal case rather than a corner one. Postgres may return tied rows in any
/// order, per query — so a Skip/Take walk over <c>ORDER BY created_at DESC</c> alone can hand the
/// same row to two pages and never show another at all. An operator's work list would be missing
/// items with nothing anywhere reporting an error.</para>
///
/// <para>Docker-gated; skips cleanly where Docker is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class ExceptionListPagingPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;
    private readonly CapturedSqlInterceptor _capture = new();

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_excpaging");

        var cs = new NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(cs)
            .AddInterceptors(_capture)
            .Options;
    }

    public async Task DisposeAsync() => await postgres.DropDatabaseAsync(_databaseConnectionString);

    /// <summary>
    /// Seeds <paramref name="count"/> exceptions that ALL share one <c>created_at</c> — the shape
    /// ReconcileAsync produces — so the tie-break is the only thing that can order them.
    /// </summary>
    private async Task<Guid> SeedTiedAsync(int count)
    {
        var orgId = Guid.NewGuid();
        var stamp = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id = orgId, Name = "Fabrikam", Slug = $"fabrikam-{orgId:N}",
            ClerkOrgId = $"org_{orgId:N}", CreatedAt = DateTime.UtcNow,
        });
        db.OrderExceptions.AddRange(Enumerable.Range(0, count).Select(i => new OrderException
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            OrderId   = Guid.NewGuid(),
            Stage     = "Map",
            Code      = "unresolved_mapping",
            Severity  = "warning",
            State     = "open",
            Message   = $"exception {i}",
            CreatedAt = stamp,
        }));
        await db.SaveChangesAsync();
        return orgId;
    }

    [DockerRequiredFact]
    public async Task ListAsync_PagesInSql_AndOrdersByATotalSort()
    {
        var orgId = await SeedTiedAsync(40);

        await using var db = new ProcuLinkDbContext(_options!);
        var svc = new OrderExceptionService(db);

        _capture.Clear();
        var page = await svc.ListAsync(orgId, null, 2, 10, CancellationToken.None);

        Assert.Equal(10, page.Rows.Count);
        Assert.Equal(40, page.Total);

        // One COUNT and one page read — never a fetch-everything-then-slice.
        var rowQuery = Assert.Single(_capture.Commands, c => c.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("OFFSET", rowQuery, StringComparison.OrdinalIgnoreCase);

        // The tie-break. Without `id` in the ORDER BY, this walk is not exhaustive.
        var orderBy = rowQuery[rowQuery.LastIndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase)..];
        Assert.Contains("created_at", orderBy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".id", orderBy, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The property the tie-break exists for, stated end to end: walking every page of a fully
    /// tied history yields each row exactly once.
    /// </summary>
    [DockerRequiredFact]
    public async Task ListAsync_WalkingEveryPageOfATiedHistory_YieldsEachRowExactlyOnce()
    {
        var orgId = await SeedTiedAsync(37);

        await using var db = new ProcuLinkDbContext(_options!);
        var svc = new OrderExceptionService(db);

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var slice = await svc.ListAsync(orgId, null, page, 10, CancellationToken.None);
            seen.AddRange(slice.Rows.Select(r => r.Id));
        }

        Assert.Equal(37, seen.Count);
        Assert.Equal(37, seen.Distinct().Count());
    }
}
