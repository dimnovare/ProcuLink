using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;

namespace ProcuLink.Infrastructure.Tests.Jobs;

/// <summary>
/// BE-6 gate for the catalog sync jobs:
///  • dispatcher due-selection (due / not-due / disabled / never-synced),
///  • the soft lock makes a just-dispatched source not-due for the next window,
///  • the source job persists <c>failed</c> + the safe message BEFORE rethrowing,
///  • deleted/disabled sources bail silently,
///  • nothing rethrown from the job ever carries host/username text (M4).
/// </summary>
public class CatalogSyncJobTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SupplierCatalogSource NewSource(
        Guid orgId, bool enabled = true, DateTime? lastSyncAt = null, int intervalHours = 24) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = orgId,
        SupplierId = Guid.NewGuid(),
        Protocol = "sftp",
        Host = "files.example.com",
        Port = 22,
        Username = "u",
        RemotePath = "/exports/catalog.csv",
        FileFormat = "auto",
        SyncIntervalHours = intervalHours,
        IsEnabled = enabled,
        LastSyncAt = lastSyncAt,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── IsDue ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 24, true)]      // never synced → due
    [InlineData(-25, 24, true)]       // interval elapsed → due
    [InlineData(-1, 24, false)]       // synced an hour ago → not due
    [InlineData(-30, 48, false)]      // longer interval not yet elapsed → not due
    [InlineData(0, 0, false)]         // pathological interval clamps to 1h → just-synced is not due
    [InlineData(-2, 0, true)]         // clamped to 1h → 2h ago is due
    public void IsDue_CoversTheWindowCases(int? hoursAgo, int intervalHours, bool expected)
    {
        var now = DateTime.UtcNow;
        DateTime? lastSync = hoursAgo is null ? null : now.AddHours(hoursAgo.Value);

        CatalogSyncDispatcherJob.IsDue(lastSync, intervalHours, now).Should().Be(expected);
    }

    // ── dispatcher ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatcher_EnqueuesOneChildPerDueSource_SkipsDisabledAndNotDue()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();

        var due       = NewSource(orgId, lastSyncAt: DateTime.UtcNow.AddHours(-25));
        var neverRun  = NewSource(orgId, lastSyncAt: null);
        var notDue    = NewSource(orgId, lastSyncAt: DateTime.UtcNow.AddMinutes(-5));
        var disabled  = NewSource(orgId, enabled: false, lastSyncAt: null);
        db.SupplierCatalogSources.AddRange(due, neverRun, notDue, disabled);
        await db.SaveChangesAsync();

        var jobs = new Mock<IBackgroundJobClient>();
        var dispatcher = new CatalogSyncDispatcherJob(db, jobs.Object, NullLogger<CatalogSyncDispatcherJob>.Instance);

        await dispatcher.ExecuteAsync(CancellationToken.None);

        jobs.Verify(c => c.Create(
                It.Is<Job>(j => j.Type == typeof(CatalogSyncSourceJob)),
                It.IsAny<IState>()),
            Times.Exactly(2), "exactly the due + never-synced sources get a child job");
    }

    [Fact]
    public async Task Dispatcher_AfterSoftLock_SourceIsNotDueForTheNextWindow()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var source = NewSource(orgId, lastSyncAt: DateTime.UtcNow.AddHours(-25));
        db.SupplierCatalogSources.Add(source);
        await db.SaveChangesAsync();

        // The child job's soft lock stamps LastSyncAt=now + running.
        var pull = new Mock<ICatalogPullService>();
        pull.Setup(p => p.PullAsync(orgId, source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogPullResult("ok", 1, 0, 0, "HASH"));
        var job = new CatalogSyncSourceJob(db, pull.Object, NullLogger<CatalogSyncSourceJob>.Instance);
        await job.ExecuteAsync(orgId, source.Id, CancellationToken.None);

        // Next dispatcher window: nothing due — the soft lock prevents double-dispatch.
        var jobs = new Mock<IBackgroundJobClient>();
        var dispatcher = new CatalogSyncDispatcherJob(db, jobs.Object, NullLogger<CatalogSyncDispatcherJob>.Instance);
        await dispatcher.ExecuteAsync(CancellationToken.None);

        jobs.Verify(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    // ── source job ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SourceJob_SetsSoftLock_BeforeInvokingPull()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var source = NewSource(orgId, lastSyncAt: null);
        db.SupplierCatalogSources.Add(source);
        await db.SaveChangesAsync();

        string? statusAtPullTime = null;
        DateTime? lastSyncAtPullTime = null;
        var pull = new Mock<ICatalogPullService>();
        pull.Setup(p => p.PullAsync(orgId, source.Id, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                var row = db.SupplierCatalogSources.AsNoTracking().Single(s => s.Id == source.Id);
                statusAtPullTime = row.LastSyncStatus;
                lastSyncAtPullTime = row.LastSyncAt;
            })
            .ReturnsAsync(new CatalogPullResult("ok", 0, 0, 0, "HASH"));

        var job = new CatalogSyncSourceJob(db, pull.Object, NullLogger<CatalogSyncSourceJob>.Instance);
        await job.ExecuteAsync(orgId, source.Id, CancellationToken.None);

        statusAtPullTime.Should().Be("running", "the soft lock must be persisted BEFORE the pull starts");
        lastSyncAtPullTime.Should().NotBeNull();
    }

    [Fact]
    public async Task SourceJob_Failure_PersistsFailedAndSafeMessage_BeforeRethrowing()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var source = NewSource(orgId);
        db.SupplierCatalogSources.Add(source);
        await db.SaveChangesAsync();

        const string safe = "Authentication failed — check the username and password.";
        var pull = new Mock<ICatalogPullService>();
        pull.Setup(p => p.PullAsync(orgId, source.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CatalogSyncException(safe));

        var job = new CatalogSyncSourceJob(db, pull.Object, NullLogger<CatalogSyncSourceJob>.Instance);
        var act = () => job.ExecuteAsync(orgId, source.Id, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<CatalogSyncException>()).Which;
        thrown.Message.Should().Be(safe);
        thrown.InnerException.Should().BeNull();

        var row = await db.SupplierCatalogSources.AsNoTracking().SingleAsync(s => s.Id == source.Id);
        row.LastSyncStatus.Should().Be("failed", "the honest status must be visible even while Hangfire retries");
        row.LastSyncError.Should().Be(safe);
    }

    [Fact]
    public async Task SourceJob_UnexpectedRawException_IsSanitized_NoHostOrUserLeaks()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var source = NewSource(orgId);
        db.SupplierCatalogSources.Add(source);
        await db.SaveChangesAsync();

        var pull = new Mock<ICatalogPullService>();
        pull.Setup(p => p.PullAsync(orgId, source.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom at catalog-user@files.internal.example:22"));

        var job = new CatalogSyncSourceJob(db, pull.Object, NullLogger<CatalogSyncSourceJob>.Instance);
        var act = () => job.ExecuteAsync(orgId, source.Id, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<CatalogSyncException>()).Which;
        thrown.Message.Should().NotContain("catalog-user");
        thrown.Message.Should().NotContain("files.internal.example");
        thrown.InnerException.Should().BeNull();

        var row = await db.SupplierCatalogSources.AsNoTracking().SingleAsync(s => s.Id == source.Id);
        row.LastSyncStatus.Should().Be("failed");
        row.LastSyncError.Should().NotContain("catalog-user");
        row.LastSyncError.Should().NotContain("files.internal.example");
    }

    [Fact]
    public async Task SourceJob_MissingSource_BailsSilently()
    {
        await using var db = NewDb();
        var pull = new Mock<ICatalogPullService>(MockBehavior.Strict);

        var job = new CatalogSyncSourceJob(db, pull.Object, NullLogger<CatalogSyncSourceJob>.Instance);
        await job.ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        pull.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SourceJob_DisabledSource_BailsSilently()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var source = NewSource(orgId, enabled: false);
        db.SupplierCatalogSources.Add(source);
        await db.SaveChangesAsync();

        var pull = new Mock<ICatalogPullService>(MockBehavior.Strict);
        var job = new CatalogSyncSourceJob(db, pull.Object, NullLogger<CatalogSyncSourceJob>.Instance);

        await job.ExecuteAsync(orgId, source.Id, CancellationToken.None);

        pull.VerifyNoOtherCalls();
        var row = await db.SupplierCatalogSources.AsNoTracking().SingleAsync(s => s.Id == source.Id);
        row.LastSyncStatus.Should().BeNull("a disabled source must not be soft-locked");
    }

    [Fact]
    public async Task SourceJob_WrongOrg_BailsSilently_TenancyGuard()
    {
        await using var db = NewDb();
        var source = NewSource(Guid.NewGuid());
        db.SupplierCatalogSources.Add(source);
        await db.SaveChangesAsync();

        var pull = new Mock<ICatalogPullService>(MockBehavior.Strict);
        var job = new CatalogSyncSourceJob(db, pull.Object, NullLogger<CatalogSyncSourceJob>.Instance);

        await job.ExecuteAsync(Guid.NewGuid() /* different org */, source.Id, CancellationToken.None);

        pull.VerifyNoOtherCalls();
    }
}
