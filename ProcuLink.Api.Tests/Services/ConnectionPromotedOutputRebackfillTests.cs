using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Launch-batch-7 review fix (finding 1): the V1 backfill used to write
/// <c>output_mapping_json = null</c> while copying the FULL <c>SupplierPoMapping.ConfigJson</c>
/// (which contains the batch-4A promoted Output section) into <c>input_mapping_json</c> — so a
/// flag-ON pinned order would silently revert to the fixed transformer.
///
/// Guards:
/// <list type="bullet">
///   <item>FORWARD: a fresh backfill now snapshots the promoted Output section into
///       <c>output_mapping_json</c> in the revision-consumable shape (camelCase
///       <see cref="OutputMappingConfig"/>), while a config without a usable Output still
///       snapshots null (fixed transformer, today's behaviour).</item>
///   <item>RE-BACKFILL: <see cref="ConnectionBackfillService.RebackfillPromotedOutputAsync"/>
///       repairs legacy null-output rows created by the backfill — fill-null-only, idempotent,
///       scoped to "system:backfill" rows; rows without a usable promoted Output (none / empty /
///       malformed / no po-mapping) and user-authored revisions stay untouched.</item>
/// </list>
/// (2026-11-13, plan 13b: the CONSUMABLE-SHAPE PROOF fact — pinned vs unpinned order rendered
/// through <c>OrderService</c> — went with the order pipeline's transform.)
/// </summary>
public class ConnectionPromotedOutputRebackfillTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions CamelCaseInsensitive = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>A supplier PoMappingConfig carrying a USABLE batch-4A promoted Output section.</summary>
    private static PoMappingConfig PromotedConfig() => new()
    {
        Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "po" } },
        Output = new OutputMappingConfig
        {
            Header = { ["po"]   = new OutputFieldRule { OutputPath = "PromotedRef",  CanonicalField = "PoNumber" } },
            Lines  = { ["code"] = new OutputFieldRule { OutputPath = "PromotedCode", CanonicalField = "SupplierItemCode" } },
        },
    };

    private static async Task<(Guid orgId, Guid supplierId)> SeedSupplierAsync(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Promoted OÜ", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    /// <summary>
    /// Seeds a LEGACY-shaped backfilled rev-1: published, CreatedBy "system:backfill",
    /// output_mapping_json NULL — exactly what the pre-fix backfill produced.
    /// </summary>
    private static async Task<SupplierConnectionRevision> SeedLegacyBackfilledRevisionAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, string createdBy = ConnectionBackfillService.BackfillActor)
    {
        var now = DateTime.UtcNow;
        var connectionId = Guid.NewGuid();
        var revision = new SupplierConnectionRevision
        {
            Id            = Guid.NewGuid(),
            ConnectionId  = connectionId,
            OrgId         = orgId,
            SupplierId    = supplierId,
            VersionNo     = 1,
            Status        = "published",
            EffectiveFrom = now,
            PublishedAt   = now,
            CreatedAt     = now,
            CreatedBy     = createdBy,
            PublishedBy   = createdBy,
            OutputMappingJson = null, // the legacy bug shape
        };
        db.SupplierConnections.Add(new SupplierConnection
        {
            Id = connectionId, OrgId = orgId, SupplierId = supplierId, Name = "conn",
            ActiveRevisionId = revision.Id, CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now,
        });
        db.SupplierConnectionRevisions.Add(revision);
        await db.SaveChangesAsync();
        return revision;
    }

    // ── FORWARD: fresh backfills snapshot the promoted Output ──────────────────

    [Fact]
    public async Task Backfill_SupplierConfigWithPromotedOutput_SnapshotsConsumableOutputJson()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        // Persist through the REAL PoMappingService so ConfigJson is stored exactly as production stores it.
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);

        var created = await new ConnectionBackfillService(db).BackfillAllAsync(CancellationToken.None);
        Assert.Equal(1, created);

        var rev = await db.SupplierConnectionRevisions.SingleAsync();
        Assert.NotNull(rev.OutputMappingJson);

        // The snapshot must be the EXACT consumable shape: the camelCase OutputMappingConfig the
        // revision transform path (OrderTransformService.RevisionOutputSerializerOptions /
        // ReplayService.DeserializeOutputConfig) reads back.
        var roundTripped = JsonSerializer.Deserialize<OutputMappingConfig>(rev.OutputMappingJson!, CamelCaseInsensitive);
        Assert.NotNull(roundTripped);
        Assert.Equal("PromotedRef",  roundTripped!.Header["po"].OutputPath);
        Assert.Equal("PromotedCode", roundTripped.Lines["code"].OutputPath);
        Assert.Equal(JsonSerializer.Serialize(PromotedConfig().Output, CamelCase), rev.OutputMappingJson);

        // The input snapshot is still the FULL ConfigJson, byte-identical (prime directive intact).
        var stored = await db.SupplierPoMappings.SingleAsync();
        Assert.Equal(stored.ConfigJson, rev.InputMappingJson);
    }

    [Fact]
    public async Task Backfill_SupplierConfigWithoutOutput_StillSnapshotsNull()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, new PoMappingConfig
        {
            Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "po" } },
        }, CancellationToken.None);

        await new ConnectionBackfillService(db).BackfillAllAsync(CancellationToken.None);

        var rev = await db.SupplierConnectionRevisions.SingleAsync();
        Assert.Null(rev.OutputMappingJson); // no promoted Output → fixed transformer, today's behaviour
    }

    // ── RE-BACKFILL: legacy null-output rows are repaired ──────────────────────

    [Fact]
    public async Task Rebackfill_FillsNullOutput_FromPromotedSupplierConfig()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);
        var legacy = await SeedLegacyBackfilledRevisionAsync(db, orgId, supplierId);

        var updated = await new ConnectionBackfillService(db).RebackfillPromotedOutputAsync(CancellationToken.None);

        Assert.Equal(1, updated);
        var rev = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id);
        Assert.Equal(JsonSerializer.Serialize(PromotedConfig().Output, CamelCase), rev.OutputMappingJson);
        Assert.Equal("published", rev.Status); // lifecycle untouched — content repair only
    }

    [Fact]
    public async Task Rebackfill_IsIdempotent_SecondRunIsNoOp()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);
        var legacy = await SeedLegacyBackfilledRevisionAsync(db, orgId, supplierId);
        var svc = new ConnectionBackfillService(db);

        var first  = await svc.RebackfillPromotedOutputAsync(CancellationToken.None);
        var jsonAfterFirst = (await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id)).OutputMappingJson;
        var second = await svc.RebackfillPromotedOutputAsync(CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // re-run repairs nothing
        Assert.Equal(jsonAfterFirst,
            (await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id)).OutputMappingJson);
    }

    [Fact]
    public async Task Rebackfill_NeverOverwritesNonNullSnapshot()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);
        var legacy = await SeedLegacyBackfilledRevisionAsync(db, orgId, supplierId);

        // The revision already carries a (different) output snapshot — must stay byte-identical.
        const string existing = "{\"header\":{\"po\":{\"outputPath\":\"PinnedRef\",\"canonicalField\":\"PoNumber\"}},\"lines\":{}}";
        var tracked = await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id);
        tracked.OutputMappingJson = existing;
        await db.SaveChangesAsync();

        var updated = await new ConnectionBackfillService(db).RebackfillPromotedOutputAsync(CancellationToken.None);

        Assert.Equal(0, updated);
        Assert.Equal(existing,
            (await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == legacy.Id)).OutputMappingJson);
    }

    [Fact]
    public async Task Rebackfill_RowsWithoutUsablePromotedOutput_AreLeftUntouched()
    {
        var db = NewDb();
        var svc = new ConnectionBackfillService(db);

        // (a) config with NO Output section
        var (orgA, supplierA) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgA, supplierA, new PoMappingConfig
        {
            Header = { ["PoNumber"] = new FieldMappingEntry { ExternalField = "po" } },
        }, CancellationToken.None);
        var revA = await SeedLegacyBackfilledRevisionAsync(db, orgA, supplierA);

        // (b) MALFORMED ConfigJson (legacy/free-form mapping JSON) — must skip, never throw
        var (orgB, supplierB) = await SeedSupplierAsync(db);
        db.SupplierPoMappings.Add(new SupplierPoMapping
        {
            Id = Guid.NewGuid(), OrgId = orgB, SupplierId = supplierB,
            ConfigJson = "{ not valid json", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var revB = await SeedLegacyBackfilledRevisionAsync(db, orgB, supplierB);

        // (c) NO po-mapping row at all
        var (orgC, supplierC) = await SeedSupplierAsync(db);
        var revC = await SeedLegacyBackfilledRevisionAsync(db, orgC, supplierC);

        var updated = await svc.RebackfillPromotedOutputAsync(CancellationToken.None);

        Assert.Equal(0, updated);
        Assert.Null((await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == revA.Id)).OutputMappingJson);
        Assert.Null((await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == revB.Id)).OutputMappingJson);
        Assert.Null((await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == revC.Id)).OutputMappingJson);
    }

    [Fact]
    public async Task Rebackfill_UserAuthoredRevisionWithNullOutput_IsNeverTouched()
    {
        var db = NewDb();
        var (orgId, supplierId) = await SeedSupplierAsync(db);
        await new PoMappingService(db).UpsertAsync(orgId, supplierId, PromotedConfig(), CancellationToken.None);
        // A USER published this revision with a null output mapping — that means "fixed
        // transformer" by choice and must never be rewritten from the live table.
        var userRev = await SeedLegacyBackfilledRevisionAsync(db, orgId, supplierId, createdBy: "user:someone");

        var updated = await new ConnectionBackfillService(db).RebackfillPromotedOutputAsync(CancellationToken.None);

        Assert.Equal(0, updated);
        Assert.Null((await db.SupplierConnectionRevisions.SingleAsync(r => r.Id == userRev.Id)).OutputMappingJson);
    }
}
