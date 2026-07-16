using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Audit batch A #1 — proves the transform idempotency guard on REAL Postgres, where the
/// atomic <c>ExecuteUpdateAsync</c> ready/transforming → transforming claim actually runs
/// (the EF InMemory provider cannot translate it; the tracker-branch semantics are covered
/// by <c>Services/TransformIdempotencyGuardTests</c>):
/// <list type="bullet">
/// <item>a duplicated Hangfire run AFTER completion is a Skipped no-op — no second
/// artifact, no re-enqueued delivery (the double-sent-PO bug);</item>
/// <item>a failed template transform writes 'transform_failed' IN THE DATABASE —
/// pinning the change-tracker original-value sync after the claim (without it the write
/// diffs as a no-op and the order is stranded in 'transforming');</item>
/// <item>a 'transform_failed' order stays re-claimable, so the recovery path (fix the
/// template, re-transform) is not a dead end.</item>
/// </list>
/// Docker-gated; skips where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class TransformIdempotencyPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_idem_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

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

    private static OrderService Build(ProcuLinkDbContext db)
    {
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("artifact-key");

        return new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    /// <summary>Seeds org + supplier + a fully-resolved 'ready' order (FKs enforced — parents first).</summary>
    private async Task<(Guid OrgId, Guid OrderId)> SeedResolvedOrderAsync()
    {
        var orgId      = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id            = orgId,
            ClerkOrgId    = $"org_idem_{orgId:N}",
            Name          = "Idempotency Org",
            Slug          = $"idem-{orgId:N}",
            Plan          = "operations",
            AccountStatus = "active",
            CreatedAt     = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Idem Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-IDEM-PG-1",
            BuyerName  = "Idem Buyer",
            OrderDate  = new DateOnly(2026, 6, 1),
            Currency   = "EUR",
            Status     = OrderStatusConstants.Ready,
            CreatedAt  = now,
            UpdatedAt  = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
            },
        });
        await db.SaveChangesAsync();

        return (orgId, orderId);
    }

    [DockerRequiredFact]
    public async Task RetryAfterCompletion_SkipsAtomically_NoSecondArtifact()
    {
        var (orgId, orderId) = await SeedResolvedOrderAsync();

        // First run (its own context scope, like a real Hangfire job activation).
        Guid firstArtifactId;
        await using (var db = NewContext())
        {
            var first = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
            Assert.True(first.IsSuccess, first.Error);
            Assert.False(first.Value!.Skipped);
            firstArtifactId = first.Value.ArtifactId;
        }

        // Duplicated/retried run in a FRESH scope: the atomic claim affects 0 rows
        // (status is ready_to_deliver) → Skipped no-op pointing at the existing artifact.
        await using (var db = NewContext())
        {
            var retry = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
            Assert.True(retry.IsSuccess, retry.Error);
            Assert.True(retry.Value!.Skipped);
            Assert.Equal(firstArtifactId, retry.Value.ArtifactId);
        }

        await using (var db = NewContext())
        {
            Assert.Equal(1, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));
            var status = await db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, status);
        }
    }

    [DockerRequiredFact]
    public async Task StuckTransforming_IsReclaimable_AndSelfHeals()
    {
        var (orgId, orderId) = await SeedResolvedOrderAsync();

        // Simulate a crashed first attempt: in-flight status persisted, no artifact.
        await using (var db = NewContext())
        {
            await db.PurchaseOrders.Where(o => o.Id == orderId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatusConstants.Transforming));
        }

        await using (var db = NewContext())
        {
            var result = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
            Assert.False(result.Value!.Skipped);
        }

        await using (var db = NewContext())
        {
            Assert.Equal(1, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));
            var status = await db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, status);
        }
    }

    [DockerRequiredFact]
    public async Task TemplateFailure_WritesTransformFailed_InTheDatabase()
    {
        var (orgId, orderId) = await SeedResolvedOrderAsync();

        // A stored broken whole-document template makes the transform throw
        // TransformTemplateException AFTER the atomic claim flipped the row to
        // 'transforming' — the failure must produce a REAL UPDATE to 'transform_failed'
        // (pinning the change-tracker original-value sync after the claim: without it the
        // write diffs as a no-op against a stale original and the order strands in
        // 'transforming').
        await using (var db = NewContext())
        {
            var stored = await new OrderMappingOverrideService(db).UpsertAsync(
                orgId, orderId, new OrderMappingOverride { OutputTemplate = "{{ for }}" }, CancellationToken.None);
            Assert.True(stored);
        }

        await using (var db = NewContext())
        {
            var result = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
            Assert.False(result.IsSuccess); // broken template surfaces as a failure, never delivers
        }

        await using (var db = NewContext())
        {
            var status = await db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync();
            // NOT 'transforming' (stranded) and NOT 'ready' (indistinguishable from
            // "never transformed" — the silent strand that hid failures from ops health).
            Assert.Equal(OrderStatusConstants.TransformFailed, status);
            Assert.Equal(0, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));
        }
    }

    [DockerRequiredFact]
    public async Task TransformFailedOrder_IsReclaimable_OnRealPostgres()
    {
        // The recovery half, proven against the REAL ExecuteUpdateAsync claim (the InMemory
        // branch is covered by Services/TransformFailureVisibilityTests): a transform_failed
        // order must be re-claimable once the template is fixed, or the visible-failure fix
        // would trade a silent strand for a permanent one.
        var (orgId, orderId) = await SeedResolvedOrderAsync();

        await using (var db = NewContext())
        {
            await db.PurchaseOrders.Where(o => o.Id == orderId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatusConstants.TransformFailed));
        }

        await using (var db = NewContext())
        {
            var result = await Build(db).TransformAsync(orgId, orderId, OutputFormat.Csv, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
            Assert.False(result.Value!.Skipped); // re-claimed and genuinely re-run, not a no-op
        }

        await using (var db = NewContext())
        {
            Assert.Equal(1, await db.OutboundArtifacts.CountAsync(a => a.OrderId == orderId));
            var status = await db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, status);
        }
    }
}
