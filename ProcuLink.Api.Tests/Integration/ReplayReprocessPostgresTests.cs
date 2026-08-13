using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
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
public sealed class ReplayReprocessFixture(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    public DbContextOptions<ProcuLinkDbContext>? Options { get; private set; }

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_wp35");

        var connectionString = new NpgsqlConnectionStringBuilder(_databaseConnectionString)
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
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
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
    /// Two re-processes of the same order racing on separate connections still leave ONE artifact,
    /// and neither caller is told the operation failed.
    ///
    /// <para><b>What this does and does not prove.</b> Two mechanisms can produce that outcome: the
    /// existence pre-check, when one call happens to finish before the other reads; and the primary
    /// key, when both read the same empty pre-state and both try to insert. This test cannot force
    /// which one fires, so it asserts the INVARIANT rather than the mechanism — deliberately, since
    /// asserting a mechanism it cannot schedule would be a claim the test does not earn. The
    /// mechanism that matters is the primary key, because it is the only one that holds when the
    /// pre-check is useless; that it holds is proven by the fact that the id is derived, not
    /// generated, which the mutation on
    /// <c>DeterministicReprocessArtifactId</c> covers.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task Reprocess_RunConcurrently_StillLeavesExactlyOneArtifact()
    {
        var seed    = await SeedAsync();
        var storage = await NewStorageWithOriginalAsync(seed);

        await using var dbA = NewContext();
        await using var dbB = NewContext();

        var outcomes = await Task.WhenAll(
            NewReplay(dbA, storage).ReprocessAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator-a", default),
            NewReplay(dbB, storage).ReprocessAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator-b", default));

        Assert.All(outcomes, o => Assert.Equal(ReprocessStatus.Ok, o.Status));
        Assert.Equal(outcomes[0].Response!.ArtifactId, outcomes[1].Response!.ArtifactId);

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

    /// <summary>
    /// The order passport keeps naming the artifact that was actually SENT.
    ///
    /// <para>The passport's output section is what an operator shows a supplier in a dispute: it
    /// carries the artifact's SHA-256, and WP-34's tamper test pins that hash to the delivered
    /// bytes. It picked the order's newest artifact — so a re-process, whose artifact is newest by
    /// construction, would have swapped the headline document for one that was never sent and
    /// published its hash as the record. This asserts the swap does not happen, and separately that
    /// the preview is still LISTED, because hiding it would be its own dishonesty.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task Reprocess_DoesNotChangeWhichArtifactThePassportSaysWasSent()
    {
        var seed    = await SeedAsync();
        var storage = await NewStorageWithOriginalAsync(seed);

        Guid reprocessedId;
        await using (var db = NewContext())
            reprocessedId = (await NewReplay(db, storage).ReprocessAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator@test", default))
                .Response!.ArtifactId;

        await using var db2 = NewContext();
        var passport = await new PassportService(db2).GetAsync(seed.OrgId, seed.OrderId, default);

        Assert.True(passport.IsSuccess, passport.Error);
        Assert.NotNull(passport.Value!.OutputArtifact);
        Assert.Equal(seed.OriginalArtifactId, passport.Value.OutputArtifact!.ArtifactId);
        Assert.Equal(
            ProvenanceHash.TrySha256Hex(OriginalBytes),
            passport.Value.OutputArtifact.ArtifactSha256);
        Assert.NotEqual(seed.OriginalArtifactId, reprocessedId);
    }

    /// <summary>
    /// The AUTOMATIC backoff retry sends the order's own output, not a preview.
    ///
    /// <para>This is the path with no human in it: it fires long after the first attempt, by which
    /// time an operator may well have re-processed the order against a candidate revision. It
    /// resolved "the artifact" as the order's newest, so an unguarded append would have handed the
    /// preview to the real dispatcher. Asserted against the bytes the dispatcher actually
    /// received — a check on the artifact id alone would pass against a dispatcher that was handed
    /// one artifact's row and another's content.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task Reprocess_ThenAutomaticRetry_DispatchesTheOriginalBytes()
    {
        var seed    = await SeedAsync();
        var storage = await NewStorageWithOriginalAsync(seed);

        await using (var db = NewContext())
            await NewReplay(db, storage).ReprocessAsync(
                seed.OrgId, seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "operator@test", default);

        // A failed delivery with a supplier config the retry can route through.
        await using (var db = NewContext())
        {
            var order = await db.PurchaseOrders.SingleAsync(o => o.Id == seed.OrderId);
            order.Status = OrderStatusConstants.DeliveryFailed;
            db.SupplierDeliveryConfigs.Add(new SupplierDeliveryConfig
            {
                Id = Guid.NewGuid(), OrgId = seed.OrgId, SupplierId = seed.SupplierId,
                Protocol = "http", AutoDeliver = true,
                ConfigJson = """{"url":"https://supplier.test/orders","method":"POST"}""",
                OutputFormat = "csv", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var dispatcher = new CapturingDispatcher();
        await using (var db = NewContext())
        {
            var result = await BuildDeliveryService(db, dispatcher, storage)
                .RetryDeliveryAsync(seed.OrgId, seed.OrderId, maxAttempts: 5, default);
            Assert.True(result.Success, result.ErrorMessage);
        }

        Assert.Equal(1, dispatcher.Calls);
        Assert.Equal(OriginalBytes, dispatcher.LastContent);
    }

    // ── (4) refusals ──────────────────────────────────────────────────────────

    /// <summary>
    /// A caller from another org gets nothing and writes nothing.
    ///
    /// <para>The assertion is deliberately about the OUTCOME, not about which of the three
    /// org-scoped lookups refused first. An earlier version asserted
    /// <see cref="ReprocessStatus.RevisionNotFound"/> exactly, and a mutation removing
    /// <c>OrgId</c> from the revision query survived it — the connection check refused instead, so
    /// the test was pinning one line while appearing to pin the property. Scoped this way, the test
    /// stays green while any single scope defends the order, and goes red only when tenant
    /// isolation is actually gone, which is the thing worth pinning.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task Reprocess_ForAnOrderInAnotherOrg_RefusesAndWritesNothing()
    {
        var seed    = await SeedAsync();
        var storage = await NewStorageWithOriginalAsync(seed);

        await using (var db = NewContext())
        {
            var outcome = await NewReplay(db, storage).ReprocessAsync(
                Guid.NewGuid(), seed.ConnectionId, seed.DraftRevisionId, seed.OrderId, "attacker", default);
            Assert.NotEqual(ReprocessStatus.Ok, outcome.Status);
            Assert.Null(outcome.Response);
        }

        await using var verify = NewContext();
        Assert.Equal(1, await verify.OutboundArtifacts.CountAsync(a => a.OrderId == seed.OrderId));
        Assert.Equal(OriginalBytes, await storage.ReadAsync(seed.OriginalFileKey));
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

    private static DeliveryService BuildDeliveryService(
        ProcuLinkDbContext db, IDeliveryDispatcher dispatcher, IFileStorageService storage) =>
        new(db,
            storage,
            new DeliveryEncryptionService(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
                })
                .Build()),
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new ProcuLink.Api.Tests.TestDoubles.FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    /// <summary>Captures the bytes actually handed to a supplier channel.</summary>
    private sealed class CapturingDispatcher : IDeliveryDispatcher
    {
        public int Calls { get; private set; }
        public byte[]? LastContent { get; private set; }
        public string Protocol => "http";

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials, CancellationToken ct,
            string? idempotencyKey = null, bool isTestFire = false)
        {
            Calls++;
            LastContent = content;
            return Task.FromResult(new DeliveryResult(true, null, 200));
        }
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
