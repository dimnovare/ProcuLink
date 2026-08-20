using FluentAssertions;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.EntityFrameworkCore;
using Moq;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Reliability — cross-tenant worker-health snapshot used by the alert sweep. Verifies that
/// dead-letter and failed-delivery orders are counted across ALL organisations (it is a system
/// probe, not a tenant view), and that Hangfire server-heartbeat health is reflected via the
/// injected <see cref="IMonitoringApi"/>.
/// </summary>
public class OpsHealthServiceWorkerSnapshotTests
{
    [Fact]
    public async Task GetWorkerHealthSnapshotAsync_CountsDeadLetterAndFailedAcrossAllOrgs()
    {
        await using var db = CreateDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        await SeedOrderAsync(db, orgA, OrderStatusConstants.DeliveryDeadLetter);
        await SeedOrderAsync(db, orgB, OrderStatusConstants.DeliveryDeadLetter);
        await SeedOrderAsync(db, orgA, OrderStatusConstants.DeliveryFailed);
        await SeedOrderAsync(db, orgB, OrderStatusConstants.Delivered);   // not counted
        await SeedOrderAsync(db, orgA, OrderStatusConstants.Delivering);  // not counted

        var svc = new OpsHealthService(db, HealthyMonitoring(secondsAgo: 5));

        var snap = await svc.GetWorkerHealthSnapshotAsync(default);

        snap.DeadLetterOrders.Should().Be(2);
        snap.FailedDeliveryOrders.Should().Be(1);
        snap.DeadLetterOrFailed.Should().Be(3);
        snap.WorkerHealthy.Should().BeTrue();
        snap.ActiveWorkers.Should().Be(1);
    }

    [Fact]
    public async Task GetWorkerHealthSnapshotAsync_CountsPipelineFailuresAcrossAllOrgs()
    {
        await using var db = CreateDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        await SeedOrderAsync(db, orgA, OrderStatusConstants.Failed);
        await SeedOrderAsync(db, orgB, OrderStatusConstants.Failed);
        await SeedOrderAsync(db, orgA, OrderStatusConstants.TransformFailed);
        await SeedOrderAsync(db, orgB, OrderStatusConstants.Delivered);       // not counted
        await SeedOrderAsync(db, orgA, OrderStatusConstants.DeliveryFailed);  // delivery bucket, not pipeline

        var svc = new OpsHealthService(db, HealthyMonitoring(secondsAgo: 5));

        var snap = await svc.GetWorkerHealthSnapshotAsync(default);

        snap.FailedOrders.Should().Be(2);
        snap.TransformFailedOrders.Should().Be(1);
        snap.PipelineFailedOrders.Should().Be(3);
        snap.FailedDeliveryOrders.Should().Be(1, "delivery failures stay in the dead-letter bucket");
    }

    [Fact]
    public async Task GetWorkerHealthSnapshotAsync_StaleHeartbeat_ReportsUnhealthy()
    {
        await using var db = CreateDb();
        var svc = new OpsHealthService(db, HealthyMonitoring(secondsAgo: 600)); // way past the 60s deadline

        var snap = await svc.GetWorkerHealthSnapshotAsync(default);

        snap.WorkerHealthy.Should().BeFalse();
        snap.DeadLetterOrFailed.Should().Be(0);
    }

    [Fact]
    public async Task GetWorkerHealthSnapshotAsync_NoServers_ReportsUnhealthy()
    {
        await using var db = CreateDb();
        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.Servers()).Returns(new List<ServerDto>());

        var snap = await new OpsHealthService(db, monitoring.Object).GetWorkerHealthSnapshotAsync(default);

        snap.WorkerHealthy.Should().BeFalse();
        snap.ActiveWorkers.Should().Be(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IMonitoringApi HealthyMonitoring(int secondsAgo)
    {
        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.Servers()).Returns(new List<ServerDto>
        {
            new() { Name = "worker-1", Heartbeat = DateTime.UtcNow.AddSeconds(-secondsAgo) },
        });
        return monitoring.Object;
    }

    private static async Task SeedOrderAsync(ProcuLinkDbContext db, Guid orgId, string status)
    {
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber = "PO-OPS",
            OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency = "EUR",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
