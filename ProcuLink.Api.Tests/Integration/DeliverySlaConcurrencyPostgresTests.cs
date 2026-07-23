using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Tier-D #5 — proves two overlapping SLA sweeps cannot double-insert the DeliverySlaBreached audit
/// row on REAL Postgres, where the atomic ExecuteUpdateAsync claim actually runs (the EF InMemory
/// provider cannot translate it, so DeliverySlaServiceTests would pass against the bug).
///
/// Before the fix both sweeps SELECT the same unflagged overdue order (the !SlaBreached guard sat in
/// the SELECT), both set the flag in memory, and both append an audit row. After the fix the guard
/// is in the UPDATE, so only the sweep whose claim affects a row writes the audit event.
///
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class DeliverySlaConcurrencyPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_sla_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        // Pooling=false so each concurrent context opens its OWN physical connection — the claim race
        // is only real when two sweeps hold two connections (a pooled single connection would
        // serialise them and hide the bug the atomic claim must defend against).
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
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

    /// <summary>Seeds org + supplier + one overdue, unflagged, still-delivering order.</summary>
    private Task<(Guid OrgId, Guid OrderId)> SeedOverdueOrderAsync() =>
        SeedOverdueOrderAsync(OrderStatusConstants.Delivering);

    /// <summary>Seeds org + supplier + one overdue, unflagged order in the given status.</summary>
    private async Task<(Guid OrgId, Guid OrderId)> SeedOverdueOrderAsync(string status)
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_sla_{orgId:N}", Name = "SLA Org",
            Slug = $"sla-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "SLA Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = "PO-SLA-CONC-1", BuyerName = "Buyer", OrderDate = new DateOnly(2026, 6, 1),
            Currency = "EUR", Status = status,
            DeliveryDueAt = now.AddMinutes(-5), SlaBreached = false,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        return (orgId, orderId);
    }

    [DockerRequiredFact]
    public async Task RunAsync_TwoOverlappingSweeps_WriteExactlyOneBreachAudit()
    {
        var (_, orderId) = await SeedOverdueOrderAsync();

        await using var dbA = NewContext();
        await using var dbB = NewContext();

        var sweepA = new DeliverySlaService(dbA, NullLogger<DeliverySlaService>.Instance);
        var sweepB = new DeliverySlaService(dbB, NullLogger<DeliverySlaService>.Instance);

        var flagged = await Task.WhenAll(
            Task.Run(() => sweepA.RunAsync(CancellationToken.None)),
            Task.Run(() => sweepB.RunAsync(CancellationToken.None)));

        await using var verify = NewContext();

        var auditCount = await verify.AuditEvents
            .CountAsync(e => e.EntityId == orderId && e.Action == "DeliverySlaBreached");
        auditCount.Should().Be(1,
            "the guard must live in the UPDATE — an overlapping sweep that loses the claim must write no audit row");

        flagged.Sum().Should().Be(1, "exactly one sweep may claim the breach");

        var order = await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.SlaBreached.Should().BeTrue();
    }

    [DockerRequiredFact]
    public async Task RunAsync_TwoSweepsOverManyOrders_EachOrderGetsExactlyOneAudit()
    {
        // Several orders in one sweep set: each claim holds a row lock until the sweep's transaction
        // commits, so this exercises the multi-row claim path the single-order test cannot reach.
        // Both sweeps walk the set in the same total order (OrderBy(o => o.Id)), so they queue on the
        // same rows in the same sequence rather than deadlocking.
        var orderIds = new List<Guid>();
        for (var i = 0; i < 5; i++)
            orderIds.Add((await SeedOverdueOrderAsync()).OrderId);

        await using var dbA = NewContext();
        await using var dbB = NewContext();

        var flagged = await Task.WhenAll(
            Task.Run(() => new DeliverySlaService(dbA, NullLogger<DeliverySlaService>.Instance)
                .RunAsync(CancellationToken.None)),
            Task.Run(() => new DeliverySlaService(dbB, NullLogger<DeliverySlaService>.Instance)
                .RunAsync(CancellationToken.None)));

        await using var verify = NewContext();

        foreach (var orderId in orderIds)
        {
            var auditCount = await verify.AuditEvents
                .CountAsync(e => e.EntityId == orderId && e.Action == "DeliverySlaBreached");
            auditCount.Should().Be(1, $"order {orderId} must be audited exactly once across both sweeps");
        }

        flagged.Sum().Should().Be(orderIds.Count,
            "every order is claimed exactly once, by whichever sweep won it");
    }

    [DockerRequiredFact]
    public async Task RunAsync_RejectedBySupplierOrder_IsNotFlagged()
    {
        // A supplier that terminally rejected the PO has settled it. Even if a legacy row left
        // DeliveryDueAt live (the write-paths now clear it, but pre-fix rows exist), the sweep must
        // not raise a false "delivery overdue" on a rejected_by_supplier order — it is excluded
        // belt-and-braces, exactly as Delivered and DeliveryDeadLetter are.
        var (_, orderId) = await SeedOverdueOrderAsync(OrderStatusConstants.RejectedBySupplier);

        await using var db = NewContext();
        var flagged = await new DeliverySlaService(db, NullLogger<DeliverySlaService>.Instance)
            .RunAsync(CancellationToken.None);

        flagged.Should().Be(0, "rejected_by_supplier is a terminal supplier outcome excluded from the SLA sweep");

        await using var verify = NewContext();
        var order = await verify.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.SlaBreached.Should().BeFalse("a settled (rejected) order must never be marked SLA-breached");

        var auditCount = await verify.AuditEvents
            .CountAsync(e => e.EntityId == orderId && e.Action == "DeliverySlaBreached");
        auditCount.Should().Be(0, "no breach audit may be written for a rejected order");
    }

    [DockerRequiredFact]
    public async Task RunAsync_SecondSweepAfterFirst_IsIdempotent()
    {
        var (_, orderId) = await SeedOverdueOrderAsync();

        await using (var dbA = NewContext())
            (await new DeliverySlaService(dbA, NullLogger<DeliverySlaService>.Instance)
                .RunAsync(CancellationToken.None)).Should().Be(1);

        await using (var dbB = NewContext())
            (await new DeliverySlaService(dbB, NullLogger<DeliverySlaService>.Instance)
                .RunAsync(CancellationToken.None)).Should().Be(0, "an already-flagged order no longer matches");

        await using var verify = NewContext();
        var auditCount = await verify.AuditEvents
            .CountAsync(e => e.EntityId == orderId && e.Action == "DeliverySlaBreached");
        auditCount.Should().Be(1);
    }
}
