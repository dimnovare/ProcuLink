using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
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
[Collection("postgres-container")]
public sealed class PublishedRevisionImmutabilityPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_immut");

        var connectionString = new NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;

        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
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

    // ── Fill-only exemption (retention batch rider, migration AddBlobRetentionSweep) ──

    /// <summary>
    /// The Fable re-backfill (merged 699b3c6) must FILL <c>output_mapping_json</c> on
    /// published revisions that predate output snapshotting. Proves the replaced trigger
    /// function allows EXACTLY the NULL→value transition on output_mapping_json — and
    /// nothing else: a non-null overwrite, an erase back to NULL, and every other content
    /// column are all still rejected on a published row.
    /// </summary>
    [DockerRequiredFact]
    public async Task PublishedRevision_OutputMappingFillOnly_IsAllowed_EverythingElseStillBlocked()
    {
        var (orgId, supplierId, connectionId) = await SeedOrgSupplierConnectionAsync();

        var publishedId = Guid.NewGuid();
        var now         = DateTime.UtcNow;

        // A published revision from BEFORE output snapshotting: output_mapping_json is NULL.
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
            {
                Id = publishedId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
                VersionNo = 1, Status = "published", PublishedAt = now, EffectiveFrom = now,
                CreatedAt = now, CatalogMode = "live",
                InputMappingJson  = """{"columns":{"po":"PO Number"}}""",
                OutputMappingJson = null, // pre-snapshot era
                OutputFormat      = "csv",
                DeliveryProtocol  = "http",
            });
            await db.SaveChangesAsync();
        }

        // ── 1. FILL (NULL→value) on the published row → ALLOWED (the exemption) ──
        const string backfilled = """{"fields":[{"target":"poNumber","source":"po_number"}]}""";
        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var published = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == publishedId);
            published.OutputMappingJson = backfilled;
            await db.SaveChangesAsync(); // must NOT throw — re-backfill write shape
        }

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            var published = await db.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == publishedId);
            // jsonb normalizes formatting/key order — assert semantically, not byte-for-byte.
            Assert.NotNull(published.OutputMappingJson);
            Assert.Contains("po_number", published.OutputMappingJson);
        }

        // ── 2. OVERWRITE (value→other value) → still REJECTED ──
        await AssertContentUpdateRejectedAsync(publishedId, r => r.OutputMappingJson = """{"fields":[]}""");

        // ── 3. ERASE (value→NULL) → still REJECTED (fill-only, not clear-and-refill) ──
        await AssertContentUpdateRejectedAsync(publishedId, r => r.OutputMappingJson = null);

        // ── 4. Every OTHER content column → still REJECTED on the same row ──
        await AssertContentUpdateRejectedAsync(publishedId, r => r.InputMappingJson    = """{"columns":{"po":"TAMPERED"}}""");
        await AssertContentUpdateRejectedAsync(publishedId, r => r.OutputFormat        = "xml");
        await AssertContentUpdateRejectedAsync(publishedId, r => r.DeliveryProtocol    = "sftp");
        await AssertContentUpdateRejectedAsync(publishedId, r => r.DeliveryConfigJson  = """{"endpoint":"https://evil.example"}""");
        await AssertContentUpdateRejectedAsync(publishedId, r => r.CredentialsRef      = "tampered");

        // The backfilled value survived every rejected tamper attempt.
        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var published = await verify.SupplierConnectionRevisions.AsNoTracking().SingleAsync(r => r.Id == publishedId);
            Assert.NotNull(published.OutputMappingJson);
            Assert.Contains("po_number", published.OutputMappingJson);
            Assert.Contains("PO Number", published.InputMappingJson);
        }
    }

    // ── Retention schema (same migration) — real persisted columns/table ──────

    /// <summary>
    /// Proves on real Postgres that the retention batch's schema is REAL (the EF-Ignore /
    /// silent-drop lesson): <c>organisations.retention_days</c>,
    /// <c>purchase_orders.source_file_purged_at</c>, <c>outbound_artifacts.blob_purged_at</c>
    /// and a full <c>retention_audit_log</c> row all round-trip through save + fresh-context
    /// reload, and the columns default to NULL (= retention disabled / blob present).
    /// </summary>
    [DockerRequiredFact]
    public async Task RetentionSchema_RoundTripsThroughRealPostgres()
    {
        var (orgId, supplierId, _) = await SeedOrgSupplierConnectionAsync();
        var orderId    = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var auditId    = Guid.NewGuid();
        var now        = DateTime.UtcNow;

        await using (var db = new ProcuLinkDbContext(_options!))
        {
            // Defaults: a freshly-seeded org has retention DISABLED.
            var org = await db.Organisations.SingleAsync(o => o.Id == orgId);
            Assert.Null(org.RetentionDays);
            org.RetentionDays = 90;

            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = supplierId,
                PoNumber = "PO-RET-1", OrderDate = DateOnly.FromDateTime(now), Currency = "EUR",
                Status = "delivered", SourceFileKey = $"{orgId}/{orderId}/source.csv",
                SourceFilePurgedAt = now, CreatedAt = now.AddDays(-100), UpdatedAt = now,
            });
            db.OutboundArtifacts.Add(new OutboundArtifact
            {
                Id = artifactId, OrderId = orderId, OrgId = orgId, Format = "csv",
                FileKey = $"{orgId}/{orderId}/artifacts/{artifactId}.csv", CreatedAt = now.AddDays(-100),
                ArtifactSha256 = "sha-survives-the-purge", BlobPurgedAt = now,
            });
            db.RetentionAuditLogs.Add(new RetentionAuditLog
            {
                Id = auditId, OrgId = orgId, RunAt = now, Mode = "delete",
                FilesDeleted = 2, BytesEstimated = 12_345,
                DetailsJson = """{"retentionDays":90,"sourceFiles":1,"artifactBlobs":1}""",
            });
            await db.SaveChangesAsync();
        }

        // Reload through a FRESH context — values must come back from Postgres itself.
        await using (var verify = new ProcuLinkDbContext(_options!))
        {
            var org = await verify.Organisations.AsNoTracking().SingleAsync(o => o.Id == orgId);
            Assert.Equal(90, org.RetentionDays);

            var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.NotNull(order.SourceFilePurgedAt);
            Assert.NotNull(order.SourceFileKey); // blob-only: the key survives

            var artifact = await verify.OutboundArtifacts.AsNoTracking().SingleAsync(a => a.Id == artifactId);
            Assert.NotNull(artifact.BlobPurgedAt);
            Assert.Equal("sha-survives-the-purge", artifact.ArtifactSha256); // hash survives

            var audit = await verify.RetentionAuditLogs.AsNoTracking().SingleAsync(a => a.Id == auditId);
            Assert.Equal("delete", audit.Mode);
            Assert.Equal(2, audit.FilesDeleted);
            Assert.Equal(12_345, audit.BytesEstimated);
            // jsonb normalizes formatting — parse, don't string-match.
            using var details = System.Text.Json.JsonDocument.Parse(audit.DetailsJson!);
            Assert.Equal(90, details.RootElement.GetProperty("retentionDays").GetInt32());
            Assert.Equal(1, details.RootElement.GetProperty("sourceFiles").GetInt32());
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
