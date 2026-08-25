using FluentAssertions;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// The <c>pipeline_failure_backlog</c> condition had no drain.
///
/// <para><b>The defect.</b> The count was an ALL-TIME count of orders whose current status is
/// <c>failed</c> or <c>transform_failed</c>, and <c>failed</c> is declared terminal —
/// <c>OrderStatusMachine.Transitions[Failed]</c> is the empty set, so nothing can move an order out
/// of it. With <c>PipelineFailureThreshold = 1</c>, one pilot user who uploaded a single unparseable
/// file and abandoned it pinned the condition bad permanently: it could never transition back to
/// healthy, so it re-alerted on every 30-minute cooldown expiry for the life of the workspace —
/// about 48 emails a day, forever, about one abandoned file.</para>
///
/// <para><b>The fix under test</b> is a trailing window on <c>UpdatedAt</c>, not a higher threshold.
/// A single recent failure still pages; a failure nobody has touched in three days does not.</para>
///
/// <para>The last two tests run the REAL <c>OpsHealthService</c> through the REAL
/// <c>WorkerHealthAlertService</c>, because the defect was only visible where the count met the
/// threshold. They come as a pair on purpose: the second proves the first is not vacuous — the same
/// harness, the same single order, aged differently, still alerts.</para>
/// </summary>
public class OpsHealthPipelineFailureWindowTests
{
    [Fact]
    public async Task PipelineFailureCount_ExcludesAFailureOlderThanTheWindow()
    {
        await using var db = CreateDb();
        // One abandoned unparseable upload, untouched for three days. This is the exact row that
        // used to alert every 30 minutes forever.
        await SeedFailedOrderAsync(db, OrderStatusConstants.Failed, ageHours: 72);

        var snap = await NewService(db).GetWorkerHealthSnapshotAsync(default);

        snap.FailedOrders.Should().Be(0);
        snap.PipelineFailedOrders.Should().Be(0,
            "a failure nobody has touched in three days is history, not a live incident");
    }

    [Fact]
    public async Task PipelineFailureCount_IncludesAFailureInsideTheWindow()
    {
        await using var db = CreateDb();
        await SeedFailedOrderAsync(db, OrderStatusConstants.Failed, ageHours: 1);
        await SeedFailedOrderAsync(db, OrderStatusConstants.TransformFailed, ageHours: 2);

        var snap = await NewService(db).GetWorkerHealthSnapshotAsync(default);

        snap.FailedOrders.Should().Be(1);
        snap.TransformFailedOrders.Should().Be(1);
        snap.PipelineFailedOrders.Should().Be(2,
            "a broken parser or output template shows up as RECENT failures, which must still page");
    }

    [Fact]
    public async Task PipelineFailureCount_ReportsTheWindowItMeasured()
    {
        await using var db = CreateDb();

        var snap = await NewService(db).GetWorkerHealthSnapshotAsync(default);

        snap.PipelineFailureWindowMinutes.Should().Be(1440,
            "the alert message states the window, so the snapshot has to carry it rather than let "
          + "the reader assume one");
    }

    [Fact]
    public async Task PipelineFailureWindow_IsConfigurable()
    {
        await using var db = CreateDb();
        await SeedFailedOrderAsync(db, OrderStatusConstants.Failed, ageHours: 3);

        var narrow = new WorkerHealthAlertOptions { PipelineFailureWindowMinutes = 60 };
        var snap = await NewService(db, narrow).GetWorkerHealthSnapshotAsync(default);

        snap.PipelineFailedOrders.Should().Be(0);
        snap.PipelineFailureWindowMinutes.Should().Be(60);
    }

    [Fact]
    public async Task DeadLetterCount_IsNotWindowed()
    {
        await using var db = CreateDb();
        // Deliberately older than any pipeline window. A dead-lettered order is a purchase order the
        // supplier never received; it stays a standing incident until an operator requeues it
        // (OrderStatusMachine.RequeueableFrom), which is the drain the pipeline statuses lack.
        await SeedFailedOrderAsync(db, OrderStatusConstants.DeliveryDeadLetter, ageHours: 240);
        await SeedFailedOrderAsync(db, OrderStatusConstants.DeliveryFailed, ageHours: 240);

        var snap = await NewService(db).GetWorkerHealthSnapshotAsync(default);

        snap.DeadLetterOrFailed.Should().Be(2,
            "ageing these out would stop paging about a supplier that is STILL not receiving orders");
    }

    // ── The condition end-to-end, through the real alert sweep ───────────────────

    [Fact]
    public async Task AbandonedPilotUpload_DoesNotAlertForever()
    {
        await using var db = CreateDb();
        await SeedFailedOrderAsync(db, OrderStatusConstants.Failed, ageHours: 72);

        var sink = new RecordingSink();
        await CreateSweep(db, sink).RunAsync(default);

        sink.Keys.Should().NotContain(OperationalAlertKeys.PipelineFailureBacklog,
            "one abandoned unparseable file must not hold the alert channel open indefinitely");
    }

    [Fact]
    public async Task ARecentPipelineFailure_StillAlerts_AndNamesTheWindow()
    {
        await using var db = CreateDb();
        await SeedFailedOrderAsync(db, OrderStatusConstants.Failed, ageHours: 1);

        var sink = new RecordingSink();
        await CreateSweep(db, sink).RunAsync(default);

        sink.Keys.Should().Contain(OperationalAlertKeys.PipelineFailureBacklog,
            "the threshold is still 1 — windowing the count must not raise the bar");
        sink.Messages.Single(m => m.Contains("pipeline failure backlog"))
            .Should().Contain("in the last 1440 min",
                "an alert that measured a window has to say which one");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static OpsHealthService NewService(
        ProcuLinkDbContext db, WorkerHealthAlertOptions? options = null)
    {
        var monitoring = new Mock<IMonitoringApi>();
        monitoring.Setup(m => m.Servers()).Returns(new List<ServerDto>
        {
            new() { Name = "worker-1", Heartbeat = DateTime.UtcNow.AddSeconds(-5) },
        });
        return new OpsHealthService(db, monitoring.Object, options);
    }

    /// <summary>The real sweep over the real health service — only the probe and sink are doubles.</summary>
    private static WorkerHealthAlertService CreateSweep(ProcuLinkDbContext db, IWorkerAlertSink sink)
    {
        var probe = new Mock<IOperationalAlertProbe>();
        probe.Setup(p => p.GetSignalsAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new OperationalAlertSignals(
                 new DeliveryFailureRateSignal(60, 0, 0),
                 Array.Empty<PullChannelSignal>(),
                 new AiTokenLatchSignal(0)));

        return new WorkerHealthAlertService(
            NewService(db), probe.Object, sink,
            new WorkerHealthAlertState(), new WorkerHealthAlertOptions(),
            NullLogger<WorkerHealthAlertService>.Instance);
    }

    private static async Task SeedFailedOrderAsync(
        ProcuLinkDbContext db, string status, int ageHours)
    {
        var at = DateTime.UtcNow.AddHours(-ageHours);
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = Guid.NewGuid(),
            OrgId      = Guid.NewGuid(),
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-PIPE",
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            Status     = status,
            CreatedAt  = at,
            UpdatedAt  = at,
        });
        await db.SaveChangesAsync();
    }

    private sealed class RecordingSink : IWorkerAlertSink
    {
        public List<string> Keys { get; } = new();
        public List<string> Messages { get; } = new();

        public Task<bool> AlertAsync(string alertKey, string message, CancellationToken ct)
        {
            Keys.Add(alertKey);
            Messages.Add(message);
            return Task.FromResult(true);
        }
    }
}
