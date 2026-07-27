using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// ONE postgres:16 + migrated schema shared by every case of the equivalence matrix.
///
/// <para>xUnit builds a fresh test-class instance per THEORY CASE, so the usual
/// <c>IAsyncLifetime</c>-on-the-test-class pattern started and migrated a container per case —
/// 64 of them for this one class, the heaviest Docker load in the repo. Measured 2026-07-26:
/// run alone, 19 of 64 cases failed, every one of them an Npgsql
/// "Timeout during reading attempt" inside <c>InitializeAsync</c> with zero assertion failures,
/// and the failing cases DIFFERED between runs — the moving-target signature of host contention,
/// not of a defect. A class fixture is created once and shared by all 64.</para>
///
/// <para>Sharing is safe because every case seeds its OWN organisation and order under fresh
/// <see cref="Guid"/>s and the claim predicate filters on both (see <c>SeedOrderAsync</c>) — no
/// case can read or claim another's row.</para>
/// </summary>
public sealed class DeliveryClaimEquivalencePostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;

    /// <summary>Null when Docker is unavailable — the Docker-gated theory skips before touching it.</summary>
    public DbContextOptions<ProcuLinkDbContext>? Options { get; private set; }

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_claimeq_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
            // Both timeouts are about the Docker HOST's load, never about anything under test:
            // opening the first connection to a cold container, and running the whole migration
            // chain over it, both outlive the 15 s / 30 s defaults on a busy machine.
            Timeout = 60,
            CommandTimeout = 180,
        }.ConnectionString;

        Options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(180))
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(Options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }
}

/// <summary>
/// The relational-vs-compiled equivalence matrix for the canonical delivery-claim predicate.
///
/// <para>The shared <see cref="DeliveryClaim"/> factory guarantees the relational
/// <c>ExecuteUpdateAsync</c> path and the InMemory emulation consume the same EXPRESSION — that is
/// true by construction and needs no test. What it does NOT guarantee is that <b>Npgsql's
/// TRANSLATION of the expression agrees with C#'s EVALUATION of it</b>: null handling, collation,
/// and <c>= ANY(@p)</c> semantics all live in that gap, and the InMemory provider never executes
/// <c>ExecuteUpdateAsync</c> at all. This matrix pins the gap for EVERY status in the machine ×
/// {fresh, stale} <c>UpdatedAt</c> × both dispatch activations, so a newly added status enters the
/// matrix automatically — if Postgres and the compiled predicate ever disagree, the InMemory suite
/// is asserting behaviour production does not have.</para>
///
/// <para>The retry claim (<see cref="DeliveryClaim.ClaimableForRetry"/>) is the same factory over
/// <see cref="OrderStatusMachine.ClaimableForRetryFrom"/>, whose contents equal
/// <see cref="OrderStatusMachine.ClaimableForAutomaticDispatchFrom"/> (asserted in
/// <c>OrderStatusMachineTests</c>) — so the automatic half of this matrix exercises the identical
/// expression shape and array contents the retry claim sends to Postgres.</para>
///
/// <para><c>= ANY</c> is Npgsql/EF8 behaviour; do not assume it survives a provider or EF major
/// upgrade — this matrix is what pins it. Docker-gated; skips where Docker is absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class DeliveryClaimEquivalencePostgresTests
    : IClassFixture<DeliveryClaimEquivalencePostgresFixture>
{
    private readonly DeliveryClaimEquivalencePostgresFixture _fixture;

    public DeliveryClaimEquivalencePostgresTests(DeliveryClaimEquivalencePostgresFixture fixture)
        => _fixture = fixture;

    private ProcuLinkDbContext NewContext() => new(_fixture.Options!);

    public static IEnumerable<object[]> StatusMatrix() =>
        from status in OrderStatusMachine.AllStatuses
        from stale in new[] { false, true }
        from requireAutoDeliver in new[] { false, true }
        select new object[] { status, stale, requireAutoDeliver };

    [DockerRequiredTheory]
    [MemberData(nameof(StatusMatrix))]
    public async Task RelationalClaim_AgreesWith_CompiledPredicate(
        string status, bool stale, bool requireAutoDeliver)
    {
        var (orgId, orderId) = await SeedOrderAsync(status, stale);

        var now = DateTime.UtcNow;
        var staleBefore = now.AddMinutes(-2);
        var pred = DeliveryClaim.ClaimableForDispatch(orgId, orderId, requireAutoDeliver, staleBefore);

        await using var db = NewContext();

        var entity = await db.PurchaseOrders.AsNoTracking()
            .SingleAsync(o => o.Id == orderId && o.OrgId == orgId);
        var compiledVerdict = pred.Compile()(entity);

        var rowsClaimed = await db.PurchaseOrders
            .Where(pred)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OrderStatusConstants.Delivering)
                .SetProperty(o => o.UpdatedAt, now));

        (rowsClaimed == 1).Should().Be(compiledVerdict,
            $"status '{status}' (stale={stale}, requireAutoDeliver={requireAutoDeliver}): Postgres's " +
            "translation of the claim and the compiled predicate must reach the same verdict, or the " +
            "InMemory emulation is asserting behaviour production does not have");
    }

    /// <summary>
    /// One order per matrix case, in its own org, so the 64 cases cannot interfere: an
    /// <c>ExecuteUpdateAsync</c> that claims in one case mutates a row no other case reads.
    ///
    /// <para>This is load-bearing, not belt-and-braces: the cases now share ONE database
    /// (<see cref="DeliveryClaimEquivalencePostgresFixture"/>), so the fresh <see cref="Guid"/>s
    /// here — not a fresh schema — are the whole of the isolation. Every read and every claim in
    /// the theory is filtered on both ids. Do not replace them with constants.</para>
    /// </summary>
    private async Task<(Guid OrgId, Guid OrderId)> SeedOrderAsync(string status, bool stale)
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_claimeq_{orgId:N}", Name = "Claim Equivalence Org",
            Slug = $"claimeq-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = null,
            PoNumber = "PO-CLAIM-EQ", OrderDate = new DateOnly(2026, 7, 1),
            Currency = "EUR", Status = status,
            CreatedAt = now.AddHours(-1),
            // Stale = comfortably past the 2-minute reclaim window (a crashed worker's orphan);
            // fresh = just stamped (another worker's live claim).
            UpdatedAt = stale ? now.AddMinutes(-30) : now,
        });
        await db.SaveChangesAsync();

        return (orgId, orderId);
    }
}
