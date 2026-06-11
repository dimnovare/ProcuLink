using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Blob-retention sweep (founder-approved Flip C) — destructive job, so the tests centre on
/// the SAFETY properties:
/// <list type="bullet">
/// <item>default = NOTHING deleted anywhere (no org opted in → no storage call, no audit row);</item>
/// <item>dry-run (the second latch, default ON) audits what WOULD be deleted while the storage
/// mock proves <c>DeleteAsync</c> is NEVER invoked;</item>
/// <item>armed mode deletes blobs ONLY for terminal+aged orders of the opted-in org —
/// in-flight, young, retryable (<c>delivery_failed</c>) and other-org orders are untouched;</item>
/// <item>DB rows, file keys, <c>ArtifactSha256</c> hashes and provenance survive (blob-only);</item>
/// <item>idempotent re-run; per-key failure isolation (flag unset → retried next run);
/// bounded batches.</item>
/// </list>
/// </summary>
public class BlobRetentionServiceTests
{
    // ── Latch 1: per-org opt-in ─────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoOrgOptedIn_TouchesNothing_WritesNoAudit()
    {
        await using var db = CreateDb();
        var storage = NewStorage();
        var org = await SeedOrgAsync(db, retentionDays: null); // default: retention DISABLED
        await SeedOrderAsync(db, org, OrderStatusConstants.Delivered, ageDays: 400, withSource: true, artifactCount: 2);

        var result = await CreateService(db, storage, DeleteModeOptions()).RunAsync(default);

        result.Organisations.Should().BeEmpty("no org has retention enabled");
        storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await db.RetentionAuditLogs.CountAsync()).Should().Be(0);
        (await db.PurchaseOrders.AnyAsync(o => o.SourceFilePurgedAt != null)).Should().BeFalse();
        (await db.OutboundArtifacts.AnyAsync(a => a.BlobPurgedAt != null)).Should().BeFalse();
    }

    // ── Latch 2: global dry-run (default TRUE) ──────────────────────────────────

    [Fact]
    public async Task RunAsync_DryRunDefault_AuditsWouldDelete_DeletesNothing()
    {
        await using var db = CreateDb();
        var storage = NewStorage(sizePerBlob: 100);
        var org = await SeedOrgAsync(db, retentionDays: 30);
        var order = await SeedOrderAsync(db, org, OrderStatusConstants.Delivered, ageDays: 60, withSource: true, artifactCount: 2);

        // new BlobRetentionOptions() — the DEFAULTS: DryRun must be true out of the box.
        var options = new BlobRetentionOptions();
        options.DryRun.Should().BeTrue("dry-run is the second safety latch and must default ON");

        var result = await CreateService(db, storage, options).RunAsync(default);

        // The storage mock proves nothing was deleted — Times.Never is the contract.
        storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Nothing marked purged: a dry run leaves the world untouched.
        var reloaded = await db.PurchaseOrders.SingleAsync(o => o.Id == order);
        reloaded.SourceFilePurgedAt.Should().BeNull();
        (await db.OutboundArtifacts.Where(a => a.OrderId == order).AllAsync(a => a.BlobPurgedAt == null))
            .Should().BeTrue();

        // But the run IS audited: mode dry_run, counts = what WOULD have been deleted.
        var audit = await db.RetentionAuditLogs.SingleAsync(a => a.OrgId == org);
        audit.Mode.Should().Be(RetentionConstants.ModeDryRun);
        audit.FilesDeleted.Should().Be(3, "1 source + 2 artifacts would be deleted");
        audit.BytesEstimated.Should().Be(300, "3 blobs × 100 bytes (best-effort HEAD)");
        audit.DetailsJson.Should().Contain("\"retentionDays\":30").And.Contain("\"sourceFiles\":1").And.Contain("\"artifactBlobs\":2");

        result.Organisations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                OrgId = org,
                Mode = RetentionConstants.ModeDryRun,
                SourceFilesPurged = 1,
                ArtifactBlobsPurged = 2,
                DeleteFailures = 0,
            });
    }

    [Fact]
    public async Task RunAsync_DryRun_RepeatedRuns_KeepAuditing_NeverDelete()
    {
        await using var db = CreateDb();
        var storage = NewStorage();
        var org = await SeedOrgAsync(db, retentionDays: 30);
        await SeedOrderAsync(db, org, OrderStatusConstants.Delivered, ageDays: 60, withSource: true, artifactCount: 1);

        var service = CreateService(db, storage, new BlobRetentionOptions()); // DryRun default true
        await service.RunAsync(default);
        await service.RunAsync(default);

        storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await db.RetentionAuditLogs.CountAsync(a => a.OrgId == org)).Should().Be(2, "every run is audited");
        (await db.RetentionAuditLogs.Where(a => a.OrgId == org).AllAsync(a => a.Mode == RetentionConstants.ModeDryRun))
            .Should().BeTrue();
    }

    // ── Armed mode: opt-in + DryRun=false ───────────────────────────────────────

    [Fact]
    public async Task RunAsync_DeleteMode_PurgesBlobsOnlyForTerminalAgedOrdersOfOptedInOrg()
    {
        await using var db = CreateDb();
        var storage = NewStorage(sizePerBlob: 10);
        var optedIn = await SeedOrgAsync(db, retentionDays: 30);
        var notOpted = await SeedOrgAsync(db, retentionDays: null);

        var purgeable  = await SeedOrderAsync(db, optedIn, OrderStatusConstants.Delivered,      ageDays: 60, withSource: true, artifactCount: 2);
        var inFlight   = await SeedOrderAsync(db, optedIn, OrderStatusConstants.Delivering,     ageDays: 60, withSource: true, artifactCount: 1);
        var young      = await SeedOrderAsync(db, optedIn, OrderStatusConstants.Delivered,      ageDays: 5,  withSource: true, artifactCount: 1);
        var retryable  = await SeedOrderAsync(db, optedIn, OrderStatusConstants.DeliveryFailed, ageDays: 60, withSource: true, artifactCount: 1);
        var otherOrgs  = await SeedOrderAsync(db, notOpted, OrderStatusConstants.Delivered,     ageDays: 60, withSource: true, artifactCount: 1);

        var result = await CreateService(db, storage, DeleteModeOptions()).RunAsync(default);

        // Exactly the 3 blobs of the terminal+aged order of the opted-in org were deleted.
        storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        storage.Verify(s => s.DeleteAsync(It.Is<string>(k => k.StartsWith($"{purgeable}/")), It.IsAny<CancellationToken>()), Times.Exactly(3));

        // The purged order: blobs flagged, but the ROW + key + hash + provenance all survive.
        var purged = await db.PurchaseOrders.Include(o => o.OutboundArtifacts).SingleAsync(o => o.Id == purgeable);
        purged.SourceFilePurgedAt.Should().NotBeNull();
        purged.SourceFileKey.Should().NotBeNullOrEmpty("blob-only deletion: the key stays for auditability");
        purged.OutboundArtifacts.Should().HaveCount(2).And.OnlyContain(a =>
            a.BlobPurgedAt != null && a.FileKey != "" && a.ArtifactSha256 != null && a.ConnectionRevisionId != null);

        // Every protected order is untouched: in-flight, young, retryable, other-org.
        foreach (var id in new[] { inFlight, young, retryable, otherOrgs })
        {
            var o = await db.PurchaseOrders.Include(x => x.OutboundArtifacts).SingleAsync(x => x.Id == id);
            o.SourceFilePurgedAt.Should().BeNull($"order {id} must not be touched");
            o.OutboundArtifacts.Should().OnlyContain(a => a.BlobPurgedAt == null);
        }

        // Audit: one DELETE row for the opted-in org; NONE for the org that never opted in.
        var audit = await db.RetentionAuditLogs.SingleAsync(a => a.OrgId == optedIn);
        audit.Mode.Should().Be(RetentionConstants.ModeDelete);
        audit.FilesDeleted.Should().Be(3);
        audit.BytesEstimated.Should().Be(30);
        (await db.RetentionAuditLogs.AnyAsync(a => a.OrgId == notOpted)).Should().BeFalse();

        result.TotalBlobs.Should().Be(3);
        result.TotalFailures.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_DeleteMode_IsIdempotent_SecondRunDeletesNothingMore()
    {
        await using var db = CreateDb();
        var storage = NewStorage();
        var org = await SeedOrgAsync(db, retentionDays: 30);
        await SeedOrderAsync(db, org, OrderStatusConstants.Delivered, ageDays: 60, withSource: true, artifactCount: 2);

        var service = CreateService(db, storage, DeleteModeOptions());

        (await service.RunAsync(default)).TotalBlobs.Should().Be(3);
        (await service.RunAsync(default)).TotalBlobs.Should().Be(0, "everything purgeable was already purged");

        // No additional storage deletes on the second run.
        storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));

        // Both runs audited; the second proves "ran, found nothing".
        var audits = await db.RetentionAuditLogs.Where(a => a.OrgId == org).OrderBy(a => a.FilesDeleted).ToListAsync();
        audits.Should().HaveCount(2);
        audits[0].FilesDeleted.Should().Be(0);
        audits[1].FilesDeleted.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_DeleteMode_StorageFailure_LeavesFlagUnset_SoNextRunRetries()
    {
        await using var db = CreateDb();
        var org = await SeedOrgAsync(db, retentionDays: 30);
        var order = await SeedOrderAsync(db, org, OrderStatusConstants.Delivered, ageDays: 60, withSource: true, artifactCount: 1);
        var artifactKey = (await db.OutboundArtifacts.SingleAsync(a => a.OrderId == order)).FileKey;

        // The artifact delete fails on the FIRST attempt only; the source delete always succeeds.
        var storage = NewStorage();
        var failedOnce = false;
        storage.Setup(s => s.DeleteAsync(artifactKey, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (!failedOnce) { failedOnce = true; throw new IOException("transient R2 hiccup"); }
                return Task.CompletedTask;
            });

        var service = CreateService(db, storage, DeleteModeOptions());

        // Run 1: source purged, artifact delete failed → flag NOT set, failure counted.
        var run1 = await service.RunAsync(default);
        run1.Organisations.Single().DeleteFailures.Should().Be(1);
        run1.Organisations.Single().SourceFilesPurged.Should().Be(1);
        (await db.OutboundArtifacts.SingleAsync(a => a.OrderId == order)).BlobPurgedAt
            .Should().BeNull("a failed delete must stay retryable");

        // Run 2: exactly the failed blob is retried and now purged.
        var run2 = await service.RunAsync(default);
        run2.Organisations.Single().ArtifactBlobsPurged.Should().Be(1);
        run2.Organisations.Single().DeleteFailures.Should().Be(0);
        (await db.OutboundArtifacts.SingleAsync(a => a.OrderId == order)).BlobPurgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_DeleteMode_BatchSizeBoundsOrdersPerRun()
    {
        await using var db = CreateDb();
        var storage = NewStorage();
        var org = await SeedOrgAsync(db, retentionDays: 30);
        await SeedOrderAsync(db, org, OrderStatusConstants.Delivered, ageDays: 90, withSource: true, artifactCount: 0);
        await SeedOrderAsync(db, org, OrderStatusConstants.Delivered, ageDays: 60, withSource: true, artifactCount: 0);

        var options = DeleteModeOptions();
        options.OrderBatchSize = 1;
        var service = CreateService(db, storage, options);

        (await service.RunAsync(default)).TotalBlobs.Should().Be(1, "the batch cap bounds each run");
        (await service.RunAsync(default)).TotalBlobs.Should().Be(1, "the remainder drains on the next run");
        (await service.RunAsync(default)).TotalBlobs.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_EnabledOrgWithNoCandidates_StillWritesZeroAuditRow()
    {
        await using var db = CreateDb();
        var storage = NewStorage();
        var org = await SeedOrgAsync(db, retentionDays: 30);
        await SeedOrderAsync(db, org, OrderStatusConstants.Delivered, ageDays: 5, withSource: true, artifactCount: 1); // too young

        await CreateService(db, storage, DeleteModeOptions()).RunAsync(default);

        var audit = await db.RetentionAuditLogs.SingleAsync(a => a.OrgId == org);
        audit.FilesDeleted.Should().Be(0, "'the sweep ran and found nothing' is itself auditable");
        storage.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static BlobRetentionOptions DeleteModeOptions() => new() { DryRun = false };

    private static Mock<IFileStorageService> NewStorage(long? sizePerBlob = null)
    {
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.TryGetSizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sizePerBlob);
        storage.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return storage;
    }

    private static BlobRetentionService CreateService(
        ProcuLinkDbContext db, Mock<IFileStorageService> storage, BlobRetentionOptions options) =>
        new(db, storage.Object, options, NullLogger<BlobRetentionService>.Instance);

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, int? retentionDays)
    {
        var id = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = id,
            ClerkOrgId = $"org_{id:N}",
            Name = "Retention Org",
            Slug = $"retention-{id:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = DateTime.UtcNow,
            RetentionDays = retentionDays,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, string status, int ageDays, bool withSource, int artifactCount)
    {
        var id = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddDays(-ageDays);
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = id,
            OrgId = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber = $"PO-{ageDays}d",
            OrderDate = DateOnly.FromDateTime(createdAt),
            Currency = "EUR",
            Status = status,
            SourceFileKey = withSource ? $"{id}/source.csv" : null,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        for (var i = 0; i < artifactCount; i++)
        {
            db.OutboundArtifacts.Add(new OutboundArtifact
            {
                Id = Guid.NewGuid(),
                OrderId = id,
                OrgId = orgId,
                Format = "csv",
                FileKey = $"{id}/artifacts/{i}.csv",
                CreatedAt = createdAt,
                ArtifactSha256 = $"sha-{i}",
                ConnectionRevisionId = Guid.NewGuid(),
                ConfigDigest = "fixed:csv",
            });
        }
        await db.SaveChangesAsync();
        return id;
    }
}
