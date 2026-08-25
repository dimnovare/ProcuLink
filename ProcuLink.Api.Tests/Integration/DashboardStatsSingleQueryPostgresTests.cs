using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Tests.TestSupport;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// GET /api/dashboard/stats must reach the database ONCE, and the query it sends must be one
/// Postgres can actually run.
///
/// <para><b>The defect.</b> The four KPIs came from four sequential awaited <c>CountAsync</c>
/// calls over the same table with the same predicate — four separate round trips to a managed
/// Postgres, serialised, on the first paint of the landing page. The endpoint immediately below
/// it in the same file already grouped.</para>
///
/// <para><b>Why this test is here and not beside the in-memory ones.</b> The in-memory provider
/// evaluates any grouping client-side, so it will happily "pass" a query no relational provider
/// can translate, and it issues no commands to count. Neither of the two things that matter —
/// that the grouped shape translates to SQL, and that it is ONE round trip — is observable
/// there. Both are observable here: the commands the production code sends are captured off the
/// real connection.</para>
///
/// <para>The value assertions are deliberately duplicated from
/// <c>DashboardStatsKpiTests</c> rather than trusted from it: a query that translated, ran once,
/// and returned different numbers than the in-memory provider would otherwise pass both files.</para>
///
/// <para>Docker-gated; skips cleanly where Docker is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class DashboardStatsSingleQueryPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;
    private readonly CapturedSqlInterceptor _capture = new();

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_dashstats");

        var cs = new NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(cs)
            .AddInterceptors(_capture)
            .Options;
    }

    public async Task DisposeAsync() => await postgres.DropDatabaseAsync(_databaseConnectionString);

    // ── Fixture ──────────────────────────────────────────────────────────────

    private static DateTime MonthStart =>
        new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PurchaseOrderEntity Order(
        Guid orgId, Guid supplierId, string status, DateTime createdAt, bool isSample = false) => new()
    {
        Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
        PoNumber = $"PO-{Guid.NewGuid():N}", Status = status,
        OrderDate = DateOnly.FromDateTime(createdAt),
        Currency = "EUR", CreatedAt = createdAt, UpdatedAt = createdAt,
        IsSample = isSample,
    };

    /// <summary>
    /// The same shape as the in-memory fixture: four real orders this month, three real orders
    /// before it, two practice orders and two orders belonging to somebody else.
    /// </summary>
    private async Task<Guid> SeedAsync()
    {
        var orgId      = Guid.NewGuid();
        var otherOrg   = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var otherSup   = Guid.NewGuid();

        await using var db = new ProcuLinkDbContext(_options!);

        db.Organisations.AddRange(
            new Organisation
            {
                Id = orgId, Name = "Fabrikam", Slug = $"fabrikam-{orgId:N}",
                ClerkOrgId = $"org_{orgId:N}", CreatedAt = DateTime.UtcNow,
            },
            new Organisation
            {
                Id = otherOrg, Name = "Contoso", Slug = $"contoso-{otherOrg:N}",
                ClerkOrgId = $"org_{otherOrg:N}", CreatedAt = DateTime.UtcNow,
            });
        db.Suppliers.AddRange(
            new Supplier { Id = supplierId, OrgId = orgId,    Name = "Acme",  CreatedAt = DateTime.UtcNow },
            new Supplier { Id = otherSup,   OrgId = otherOrg, Name = "Globex", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var thisMonth = MonthStart.AddHours(3);
        var justBefore = MonthStart.AddSeconds(-1);

        db.PurchaseOrders.AddRange(
            Order(orgId, supplierId, OrderStatusConstants.PendingReview, thisMonth),
            Order(orgId, supplierId, OrderStatusConstants.PendingReview, thisMonth),
            Order(orgId, supplierId, OrderStatusConstants.Delivered,     thisMonth),
            Order(orgId, supplierId, OrderStatusConstants.Parsing,       thisMonth),

            Order(orgId, supplierId, OrderStatusConstants.PendingReview, justBefore),
            Order(orgId, supplierId, OrderStatusConstants.Delivered,     justBefore),
            Order(orgId, supplierId, OrderStatusConstants.Delivered,     MonthStart.AddYears(-1)),

            Order(orgId, supplierId, OrderStatusConstants.Delivered,     thisMonth, isSample: true),
            Order(orgId, supplierId, OrderStatusConstants.PendingReview, thisMonth, isSample: true),

            Order(otherOrg, otherSup, OrderStatusConstants.Delivered,     thisMonth),
            Order(otherOrg, otherSup, OrderStatusConstants.PendingReview, thisMonth));
        await db.SaveChangesAsync();

        return orgId;
    }

    private static int Stat(object payload, string name) =>
        (int)payload.GetType().GetProperty(name)!.GetValue(payload)!;

    // ── The guard ────────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task GetStats_SendsOneGroupedQuery_AndReportsTheSameFourNumbers()
    {
        var orgId = await SeedAsync();

        await using var db = new ProcuLinkDbContext(_options!);
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);
        var ctrl = new DashboardController(db, tenant.Object);

        // Everything before this point is fixture traffic.
        _capture.Clear();

        var result = await ctrl.GetStats(CancellationToken.None);

        var commands = _capture.Commands;
        Assert.True(commands.Count == 1,
            "GET /api/dashboard/stats must reach the database once; it sent "
            + $"{commands.Count} command(s):{Environment.NewLine}{_capture.Describe()}");

        // The shape, not just the count: one round trip that loaded every row and counted in
        // memory would also be "one command", and would be worse than the four counts it replaced.
        Assert.Contains("GROUP BY", commands[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT(", commands[0], StringComparison.OrdinalIgnoreCase);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(4, Stat(ok.Value!, "totalOrdersThisMonth"));
        Assert.Equal(7, Stat(ok.Value!, "totalOrders"));
        Assert.Equal(3, Stat(ok.Value!, "pendingReview"));
        Assert.Equal(3, Stat(ok.Value!, "delivered"));
    }
}
