using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// P2 hardening — proves on REAL Postgres (not EF InMemory, which cannot run triggers)
/// that the <c>AddReviewReasonAndPublishedRevisionImmutability</c> migration enforces
/// DB-level immutability for published connection revisions:
///
/// <list type="bullet">
/// <item>UPDATE of a content (bundle) column on a <c>status='published'</c> row is
/// REJECTED by the trigger — even raw SQL cannot rewrite a published bundle that
/// orders pin to for reproducibility.</item>
/// <item>The lifecycle flows the services actually run stay working: archive
/// (status + effective_to — PublishAsync/RollbackAsync/ArchiveAsync write shape) and
/// test evidence (test_result_json/tested_at/test_passed — RunTestPackAsync write
/// shape) both succeed on a published row.</item>
/// <item>Draft revisions stay fully editable (UpdateDraftAsync write shape).</item>
/// </list>
///
/// Also proves <c>purchase_order_lines.review_reason</c> is a REAL persisted column
/// (save + reload through a fresh context — the EF-Ignore silent-drop lesson).
/// Docker-gated (mirrors <see cref="ProvenancePersistencePostgresTests"/>); skips
/// where Docker is absent instead of failing the suite.
/// </summary>
public sealed class PublishedRevisionImmutabilityPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_immut_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
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

    [DockerRequiredFact]
    public async Task PublishedRevision_ContentUpdate_IsRejected_ButLifecycleAndEvidenceFlowsStillWork()
    {
        var (orgId, supplierId, connectionId) = await SeedOrgSupplierConnectionAsync();

        var publishedId = Guid.NewGuid();
        var draftId     = Guid.NewGuid();
        var now         = DateTime.UtcNow;

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
            {
                Id = publishedId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
                VersionNo = 1, Status = "published", PublishedAt = now, EffectiveFrom = now,
                CreatedAt = now, CatalogMode = "live",
                InputMappingJson  = """{"columns":{"po":"PO Number"}}""",
                OutputMappingJson = """{"fields":[{"target":"poNumber"}]}""",
                OutputFormat      = "csv",
                DeliveryProtocol  = "http",
            });
            db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
            {
                Id = draftId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
                VersionNo = 2, Status = "draft", CreatedAt = now, CatalogMode = "live",
                InputMappingJson = """{"columns":{"po":"PO"}}""",
            });
            await db.SaveChangesAsync();
        }

        // ── 1. Content UPDATE on the PUBLISHED row → trigger rejects the statement ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var published = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == publishedId);
            published.InputMappingJson = """{"columns":{"po":"TAMPERED"}}""";

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Contains("immutable", pgEx.MessageText, StringComparison.OrdinalIgnoreCase);
        }

        // The tamper attempt must not have landed.
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var published = await db.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == publishedId);
            Assert.Contains("PO Number", published.InputMappingJson);
        }

        // Every guarded content column individually rejects (delivery_*, credentials, acceptance_*, format).
        await AssertContentUpdateRejectedAsync(publishedId, r => r.OutputMappingJson   = """{"fields":[]}""");
        await AssertContentUpdateRejectedAsync(publishedId, r => r.OutputFormat        = "xml");
        await AssertContentUpdateRejectedAsync(publishedId, r => r.DeliveryProtocol    = "sftp");
        await AssertContentUpdateRejectedAsync(publishedId, r => r.DeliveryConfigJson  = """{"endpoint":"https://evil.example"}""");
        await AssertContentUpdateRejectedAsync(publishedId, r => r.DeliveryAutoDeliver = true);
        await AssertContentUpdateRejectedAsync(publishedId, r => r.CredentialsRef      = "tampered-credentials");
        await AssertContentUpdateRejectedAsync(publishedId, r => r.AcceptanceProfileId = Guid.NewGuid());
        await AssertContentUpdateRejectedAsync(publishedId, r => r.AcceptanceVersionNo = 99);

        // ── 2. Test-evidence columns stay MUTABLE on a published row (RunTestPackAsync shape) ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var published = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == publishedId);
            published.TestPassed     = true;
            published.TestedAt       = DateTime.UtcNow;
            published.TestResultJson = """{"replay":{"passed":true}}""";
            await db.SaveChangesAsync(); // must NOT throw
        }

        // ── 3. Archive flow stays working (PublishAsync / RollbackAsync / ArchiveAsync shape) ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var published = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == publishedId);
            published.Status      = "archived";
            published.EffectiveTo = DateTime.UtcNow;
            await db.SaveChangesAsync(); // must NOT throw — the mirror guard must not break archiving
        }

        // ── 4. Draft revisions stay fully editable (UpdateDraftAsync shape) ──
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var draft = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == draftId);
            draft.InputMappingJson  = """{"columns":{"po":"Order No"}}""";
            draft.OutputFormat      = "json";
            draft.DeliveryProtocol  = "http";
            await db.SaveChangesAsync(); // must NOT throw
        }

        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var archived = await verify.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == publishedId);
            Assert.Equal("archived", archived.Status);
            Assert.NotNull(archived.EffectiveTo);
            Assert.True(archived.TestPassed);
            // The published-era bundle survived every tamper attempt byte-for-byte intact.
            Assert.Contains("PO Number", archived.InputMappingJson);
            Assert.Equal("csv", archived.OutputFormat);
            Assert.Equal("http", archived.DeliveryProtocol);

            var draft = await verify.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == draftId);
            Assert.Contains("Order No", draft.InputMappingJson);
            Assert.Equal("json", draft.OutputFormat);
        }
    }

    [DockerRequiredFact]
    public async Task ReviewReason_IsARealColumn_RoundTripsThroughSaveAndReload()
    {
        var (orgId, supplierId, _) = await SeedOrgSupplierConnectionAsync();

        var orderId        = Guid.NewGuid();
        var flaggedLineId  = Guid.NewGuid();
        var cleanLineId    = Guid.NewGuid();
        var now            = DateTime.UtcNow;
        const string reason =
            "No supplier item code mapping was found for buyer code 'BUY-001'. " +
            "The quantity could not be read unambiguously from the source file.";

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = supplierId,
                PoNumber = "PO-RR-1", OrderDate = DateOnly.FromDateTime(now), Currency = "EUR",
                Status = "pending_review", CreatedAt = now, UpdatedAt = now,
            });
            db.PurchaseOrderLines.AddRange(
                new PurchaseOrderLineEntity
                {
                    Id = flaggedLineId, OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "BUY-001", Quantity = 5m, UnitPrice = 10m,
                    Confidence = 0f, NeedsReview = true, ReviewReason = reason,
                },
                new PurchaseOrderLineEntity
                {
                    Id = cleanLineId, OrderId = orderId, LineNumber = 2,
                    BuyerItemCode = "BUY-002", SupplierItemCode = "SUP-002",
                    Quantity = 1m, UnitPrice = 2m, Confidence = 1f, NeedsReview = false,
                });
            await db.SaveChangesAsync();
        }

        // Reload through a FRESH context — the value must come back from Postgres itself.
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var flagged = await db.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.Id == flaggedLineId);
            Assert.Equal(reason, flagged.ReviewReason);

            var clean = await db.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.Id == cleanLineId);
            Assert.Null(clean.ReviewReason); // never-flagged (and pre-migration) rows stay null

            // Resolution clears the reason — prove null round-trips back over a real value.
            var tracked = await db.PurchaseOrderLines.SingleAsync(l => l.Id == flaggedLineId);
            tracked.NeedsReview  = false;
            tracked.ReviewReason = null;
            await db.SaveChangesAsync();
        }

        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var resolved = await verify.PurchaseOrderLines.AsNoTracking().SingleAsync(l => l.Id == flaggedLineId);
            Assert.Null(resolved.ReviewReason);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(Guid orgId, Guid supplierId, Guid connectionId)> SeedOrgSupplierConnectionAsync()
    {
        var orgId        = Guid.NewGuid();
        var supplierId   = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var now          = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(_options!);
        db.Organisations.Add(new Organisation
        {
            Id            = orgId,
            ClerkOrgId    = $"org_immut_{orgId:N}",
            Name          = "Immutability Org",
            Slug          = $"immut-{orgId:N}",
            Plan          = "operations",
            AccountStatus = "active",
            CreatedAt     = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Immut Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId,
            Name = "Immut Supplier", ActiveRevisionId = null,
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        return (orgId, supplierId, connectionId);
    }

    /// <summary>
    /// Mutates ONE content column on the published revision and asserts the trigger
    /// rejects the UPDATE. A fresh context per attempt so the failed entry's state
    /// can't poison the next attempt.
    /// </summary>
    private async Task AssertContentUpdateRejectedAsync(
        Guid publishedRevisionId, Action<SupplierConnectionRevision> mutate)
    {
        await using var db = new ProcuLinkDbContext(_options!);
        var published = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == publishedRevisionId);
        mutate(published);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
    }
}
