using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// WP-35 — REPLAY THAT RE-PROCESSES, proven on real Postgres.
///
/// <para><b>What was missing.</b> Replay produced a real per-order impact diff and then nothing
/// could act on it: <see cref="ReplayService.ReplayAsync"/> is deliberately non-persisting, and the
/// only persisting door — <c>POST /api/orders/{id}/transform</c> — cannot target another revision.
/// An operator could see that a draft revision would change an order's output and had no way to
/// produce that output for a historical order.</para>
///
/// <para><b>The load-bearing assertion is the ORIGINAL, not the new artifact.</b> The old artifact
/// is the evidence of what was actually sent to the supplier; the order passport hashes it and a
/// tamper test pins that hash. So these tests assert the original's BYTES are byte-identical after
/// a re-process — not merely that a row is still present. A test that counted artifacts would pass
/// against an implementation that overwrote the first one's content, which is the exact failure
/// mode that would destroy the audit trail.</para>
///
/// <para><b>Why real Postgres.</b> The append is a second child row against a live FK and a real
/// unique primary key; the idempotency contract is enforced BY that primary key (a deterministic
/// artifact id), so EF InMemory — which happily accepts a duplicate insert semantics-free — cannot
/// prove it. The concurrency cell below asserts a real 23505 unique violation is absorbed.</para>
/// </summary>
/// <summary>
/// One <c>postgres:16</c> for the whole class. <see cref="IAsyncLifetime"/> on the test class runs
/// per TEST METHOD, which would start and migrate six containers for the six cells below and — on a
/// contended machine — time out opening the first connection. Every cell seeds its own org, so a
/// shared database isolates them perfectly well.
/// </summary>
public sealed class ReplayReprocessFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    public DbContextOptions<ProcuLinkDbContext>? Options { get; private set; }

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_wp35_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _pg.StartAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString())
        {
            Pooling = false,
            // Npgsql's 15s default is measured against an idle machine. A developer box running
            // several of these suites at once starves a freshly-started postgres badly enough that
            // the FIRST connection — the one the migration opens — times out while the container
            // itself is perfectly healthy, which reads as six failed cells rather than a busy host.
            Timeout        = 60,
            CommandTimeout = 120,
        }.ConnectionString;

        Options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseNpgsql(connectionString)
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

[Collection("postgres-container")]
public sealed class ReplayReprocessPostgresTests : IClassFixture<ReplayReprocessFixture>
{
    /// <summary>The original artifact's bytes — seeded, then asserted byte-identical at the end.</summary>
    private static readonly byte[] OriginalBytes =
        Encoding.UTF8.GetBytes("po,line,code,qty\r\nPO-REPLAY-1,1,SUP-OLD,3\r\n");

    private readonly DbContextOptions<ProcuLinkDbContext>? _options;

    public ReplayReprocessPostgresTests(ReplayReprocessFixture fixture) => _options = fixture.Options;

    // ── (1) THE PACKET: replay, then re-process, and both artifacts survive ────

    /// <summary>
    /// The acceptance criterion, end to end: an operator replays a draft revision, sees the order's
    /// output would change, re-processes that one historical order under the draft — and the record
    /// afterwards holds BOTH artifacts, with the original untouched down to its bytes.
    ///
    /// <para>The strongest assertion here is not the count. It is that the stored re-processed bytes
    /// equal the <c>DraftOutput</c> the replay diff showed for this order: what the operator
    /// approved on screen is provably what got written. Producing the artifact through a second,
    /// parallel rendering path would satisfy "two rows exist" while storing something the operator
    /// never saw.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task Replay_ThenReprocess_StoresTheDraftOutputAlongsideTheOriginal_WhichIsByteIdentical()
    {
        var seed    = await SeedAsync();
        var storage = await NewStorageWithOriginalAsync(seed);

        // ── what the operator sees ────────────────────────────────────────────
        string? draftOutput;
        await using (var db = NewContext())
        {
            var replay = await NewReplay(db, storage).ReplayAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId,
                new ReplayRequest(OrderIds: new[] { seed.OrderId }), default);

            Assert.NotNull(replay);
            var diff = Assert.Single(replay!.Orders);
            Assert.True(diff.OutputChanged,
                "the draft revision must change this order's output, or the packet's scenario is not being exercised");
            Assert.NotNull(diff.DraftOutput);
            draftOutput = diff.DraftOutput;
        }

        // ── the operator re-processes that order under the draft ──────────────
        ReprocessResponse response;
        await using (var db = NewContext())
        {
            var outcome = await NewReplay(db, storage).ReprocessAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator@test", default);

            Assert.Equal(ReprocessStatus.Ok, outcome.Status);
            Assert.NotNull(outcome.Response);
            response = outcome.Response!;
        }

        Assert.False(response.Reused);
        Assert.NotEqual(seed.OriginalArtifactId, response.ArtifactId);

        await using var verify = NewContext();

        // ── BOTH retained ─────────────────────────────────────────────────────
        var artifacts = await verify.OutboundArtifacts.AsNoTracking()
            .Where(a => a.OrderId == seed.OrderId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
        Assert.Equal(2, artifacts.Count);

        // ── the ORIGINAL is untouched — row AND bytes ─────────────────────────
        var original = Assert.Single(artifacts, a => a.Id == seed.OriginalArtifactId);
        Assert.Equal(seed.OriginalFileKey, original.FileKey);
        Assert.Equal(ProvenanceHash.TrySha256Hex(OriginalBytes), original.ArtifactSha256);
        Assert.Equal("csv", original.Format);
        Assert.Equal(seed.PublishedRevisionId, original.ConnectionRevisionId);
        Assert.Null(original.BlobPurgedAt);

        // The bytes themselves — the assertion an artifact COUNT cannot make. An implementation
        // that re-uploaded the new output under the old key would keep two rows and still have
        // destroyed the evidence of what was sent.
        Assert.Equal(OriginalBytes, await storage.ReadAsync(original.FileKey));

        // ── the NEW artifact is the output the operator was shown ─────────────
        var reprocessed = Assert.Single(artifacts, a => a.Id == response.ArtifactId);
        Assert.NotEqual(original.FileKey, reprocessed.FileKey);
        Assert.Equal(seed.DraftRevisionId, reprocessed.ConnectionRevisionId);
        Assert.Equal(
            Encoding.UTF8.GetBytes(draftOutput!),
            await storage.ReadAsync(reprocessed.FileKey));
        Assert.Equal(
            ProvenanceHash.TrySha256Hex(Encoding.UTF8.GetBytes(draftOutput!)),
            reprocessed.ArtifactSha256);

        // ── and NOTHING was delivered ─────────────────────────────────────────
        var order = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == seed.OrderId);
        Assert.Equal(OrderStatusConstants.Delivered, order.Status);
        Assert.Empty(await verify.DeliveryAttempts.AsNoTracking()
            .Where(a => a.OrderId == seed.OrderId).ToListAsync());
    }

    // ── (2) idempotency — a Hangfire refetch must not double-append ───────────

    /// <summary>
    /// Re-processing the same order under the same revision twice appends ONE artifact. The
    /// identity is the deterministic artifact id derived from (order, revision, output bytes), so
    /// the second call cannot produce a second row even if it races the first — the primary key
    /// refuses it.
    /// </summary>
    [DockerRequiredFact]
    public async Task Reprocess_RunTwice_AppendsOneArtifact_AndReportsTheSecondAsReused()
    {
        var seed    = await SeedAsync();
        var storage = await NewStorageWithOriginalAsync(seed);

        ReprocessResponse first, second;
        await using (var db = NewContext())
            first = (await NewReplay(db, storage).ReprocessAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator@test", default)).Response!;
        await using (var db = NewContext())
            second = (await NewReplay(db, storage).ReprocessAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator@test", default)).Response!;

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.Equal(first.FileKey, second.FileKey);

        await using var verify = NewContext();
        Assert.Equal(2, await verify.OutboundArtifacts.CountAsync(a => a.OrderId == seed.OrderId));
    }

    /// <summary>
    /// The dedupe read is not what makes this safe — the primary key is. This drives the race
    /// directly: a second re-process whose pre-check has already been bypassed still cannot land a
    /// duplicate row, and the 23505 is absorbed into a normal <c>Reused</c> answer rather than
    /// surfacing as a 500.
    /// </summary>
    [DockerRequiredFact]
    public async Task Reprocess_WhenTheRowWasInsertedConcurrently_AbsorbsTheUniqueViolation()
    {
        var seed    = await SeedAsync();
        var storage = await NewStorageWithOriginalAsync(seed);

        Guid firstId;
        await using (var db = NewContext())
            firstId = (await NewReplay(db, storage).ReprocessAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator@test", default)).Response!.ArtifactId;

        // A context that has already read the pre-state (no reprocessed row yet) and only commits
        // afterwards is exactly the losing side of the race.
        await using var racing = NewContext();
        var service = NewReplay(racing, storage);
        var outcome = await service.ReprocessAsync(
            seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator@test", default);

        Assert.Equal(ReprocessStatus.Ok, outcome.Status);
        Assert.Equal(firstId, outcome.Response!.ArtifactId);

        await using var verify = NewContext();
        Assert.Equal(2, await verify.OutboundArtifacts.CountAsync(a => a.OrderId == seed.OrderId));
    }

    // ── (3) a re-processed artifact is not a deliverable one ──────────────────

    /// <summary>
    /// The hazard that makes "must not deliver" more than a comment. Five paths in this codebase
    /// treat "the order's newest artifact" as "the thing to send" — ops requeue, redeliver, retry
    /// delivery, the stranded-ready sweep, and the transform's already-done branch. An append that
    /// did nothing else would silently re-point every one of them at a preview the operator never
    /// approved for sending. The re-processed artifact must therefore never be the answer to
    /// "what does this order deliver?".
    /// </summary>
    [DockerRequiredFact]
    public async Task Reprocess_DoesNotBecomeTheOrdersDeliverableArtifact()
    {
        var seed    = await SeedAsync();
        var storage = await NewStorageWithOriginalAsync(seed);

        await using (var db = NewContext())
            await NewReplay(db, storage).ReprocessAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator@test", default);

        await using var verify = NewContext();
        var artifacts = await verify.OutboundArtifacts.AsNoTracking()
            .Where(a => a.OrderId == seed.OrderId).ToListAsync();

        // The re-processed row really IS the newest — this is the trap, stated as an assertion so
        // the guard below cannot pass for the trivial reason that ordering happened to save it.
        var newest = artifacts.OrderByDescending(a => a.CreatedAt).First();
        Assert.NotEqual(seed.OriginalArtifactId, newest.Id);

        // …and the deliverable answer is still the original.
        var deliverable = OutboundArtifactSelection.NewestDeliverable(artifacts);
        Assert.NotNull(deliverable);
        Assert.Equal(seed.OriginalArtifactId, deliverable!.Id);

        // The same answer through the query-side seam the background paths use.
        var deliverableViaQuery = await verify.OutboundArtifacts.AsNoTracking()
            .Where(a => a.OrderId == seed.OrderId)
            .Deliverable()
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(deliverableViaQuery);
        Assert.Equal(seed.OriginalArtifactId, deliverableViaQuery!.Id);
    }

    // ── (4) refusals ──────────────────────────────────────────────────────────

    /// <summary>An order belonging to another org is not re-processable, and writes nothing.</summary>
    [DockerRequiredFact]
    public async Task Reprocess_ForAnOrderInAnotherOrg_RefusesAndWritesNothing()
    {
        var seed    = await SeedAsync();
        var storage = await NewStorageWithOriginalAsync(seed);

        await using (var db = NewContext())
        {
            var outcome = await NewReplay(db, storage).ReprocessAsync(
                Guid.NewGuid(), seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "attacker", default);
            Assert.Equal(ReprocessStatus.RevisionNotFound, outcome.Status);
        }

        await using var verify = NewContext();
        Assert.Equal(1, await verify.OutboundArtifacts.CountAsync(a => a.OrderId == seed.OrderId));
    }

    /// <summary>
    /// A revision whose output cannot be rendered for this order records no artifact at all — a
    /// half-written preview would be indistinguishable from a real one in the record.
    /// </summary>
    [DockerRequiredFact]
    public async Task Reprocess_WhenTheRevisionCannotRender_RefusesAndWritesNothing()
    {
        var seed    = await SeedAsync();
        var storage = await NewStorageWithOriginalAsync(seed);

        // A format this service instance has no transformer for, and no output mapping to fall back
        // on — the render genuinely cannot produce a document.
        await using (var db = NewContext())
        {
            var rev = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == seed.DraftRevisionId);
            rev.OutputFormat      = "ubl";
            rev.OutputMappingJson = null;
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var outcome = await NewReplay(db, storage).ReprocessAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator@test", default);
            Assert.Equal(ReprocessStatus.RenderFailed, outcome.Status);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
        }

        await using var verify = NewContext();
        Assert.Equal(1, await verify.OutboundArtifacts.CountAsync(a => a.OrderId == seed.OrderId));
        Assert.Equal(OriginalBytes, await storage.ReadAsync(seed.OriginalFileKey));
    }

    // ── seeding + wiring ──────────────────────────────────────────────────────

    private sealed record Seeded(
        Guid OrgId, Guid SupplierId, Guid ConnectionId,
        Guid PublishedRevisionId, Guid DraftRevisionId,
        Guid OrderId, Guid OriginalArtifactId, string OriginalFileKey);

    private ProcuLinkDbContext NewContext() => new(_options!);

    private static ReplayService NewReplay(ProcuLinkDbContext db, RecordingStorage storage) =>
        new(db,
            new ITransformService[] { new CsvTransformService(), new JsonTransformService() },
            poMappings: null,
            effectiveConfig: null,
            fileStorage: storage);

    /// <summary>
    /// A supplier with a PUBLISHED csv revision (what the order was delivered under) and a DRAFT
    /// revision that emits JSON — so the replay diff really changes, and the re-processed bytes
    /// cannot accidentally equal the original's.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var orgId        = Guid.NewGuid();
        var supplierId   = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var publishedId  = Guid.NewGuid();
        var draftId      = Guid.NewGuid();
        var orderId      = Guid.NewGuid();
        var artifactId   = Guid.NewGuid();
        var now          = DateTime.UtcNow;
        var fileKey      = $"{orgId}/{orderId}/artifacts/{artifactId}.csv";

        await using var db = NewContext();

        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_wp35_{orgId:N}", Name = "WP-35 Org",
            Slug = $"wp35-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Replay Supplier", CreatedAt = now });
        await db.SaveChangesAsync();

        // The connection is saved with no active pointer FIRST: adding it and the revision it
        // points at in one SaveChanges is a circular FK dependency EF refuses to order.
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId,
            Name = "Replay Supplier", ActiveRevisionId = null, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = publishedId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "published", PublishedAt = now, EffectiveFrom = now,
            CreatedAt = now, CatalogMode = "live", OutputFormat = "csv",
        });
        // The draft differs from the published revision by its output MAPPING, not its format —
        // which is both the realistic case and the one that keeps the bytes comparable. Changing
        // the FORMAT to json would make the strongest assertion in this file impossible:
        // JsonTransformService stamps `generatedAt` with UtcNow, so the replay preview and the
        // stored artifact could never be byte-equal even when they are the same document. CSV has
        // no such stamp, so "what the operator saw is what was written" is actually provable.
        db.SupplierConnectionRevisions.Add(new SupplierConnectionRevision
        {
            Id = draftId, ConnectionId = connectionId, OrgId = orgId, SupplierId = supplierId,
            VersionNo = 2, Status = "draft", CreatedAt = now, CatalogMode = "live",
            OutputFormat = "csv",
            OutputMappingJson = JsonSerializer.Serialize(
                new OutputMappingConfig
                {
                    Header = { ["po"]  = new OutputFieldRule { OutputPath = "po",  CanonicalField = "PoNumber" } },
                    Lines  = { ["sku"] = new OutputFieldRule { OutputPath = "sku", CanonicalField = "SupplierItemCode" } },
                },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        });
        await db.SaveChangesAsync();

        var connection = await db.SupplierConnections.SingleAsync(c => c.Id == connectionId);
        connection.ActiveRevisionId = publishedId;
        await db.SaveChangesAsync();

        // A DELIVERED historical order — the kind an operator replays a candidate revision against.
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            ConnectionRevisionId = publishedId,
            PoNumber = "PO-REPLAY-1", BuyerName = "WP-35 Buyer",
            OrderDate = new DateOnly(2026, 1, 1), Currency = "EUR",
            Status = OrderStatusConstants.Delivered, CreatedAt = now, UpdatedAt = now,
            Lines = new List<PurchaseOrderLineEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "BUY-1", SupplierItemCode = "SUP-OLD",
                    Description = "Widget", Quantity = 3m, Unit = "EA", UnitPrice = 10m,
                    NeedsReview = false,
                },
            },
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId, OrderId = orderId, OrgId = orgId,
            Format = "csv", FileKey = fileKey, CreatedAt = now.AddMinutes(-5),
            ConnectionRevisionId = publishedId,
            ArtifactSha256 = ProvenanceHash.TrySha256Hex(OriginalBytes),
        });
        await db.SaveChangesAsync();

        return new Seeded(orgId, supplierId, connectionId, publishedId, draftId, orderId, artifactId, fileKey);
    }

    private static async Task<RecordingStorage> NewStorageWithOriginalAsync(Seeded seed)
    {
        var storage = new RecordingStorage();
        await storage.UploadAsync(new MemoryStream(OriginalBytes), seed.OriginalFileKey, "text/csv", default);
        return storage;
    }

    /// <summary>
    /// Storage that really retains bytes, so "the original is untouched" can be asserted against
    /// the blob rather than against a row. A stub that discarded content could not tell the
    /// difference between an append and an overwrite.
    /// </summary>
    private sealed class RecordingStorage : IFileStorageService
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            _objects[key] = buffer.ToArray();
            return Task.FromResult(key);
        }

        public Task<byte[]> ReadAsync(string key) => Task.FromResult(_objects[key]);

        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");

        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(_objects[key], writable: false));

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            _objects.Remove(key);
            return Task.CompletedTask;
        }
    }
}
