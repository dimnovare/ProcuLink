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
public sealed class DeliveryClaimEquivalencePostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

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

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

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
