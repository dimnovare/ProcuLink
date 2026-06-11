using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Group V1 — lifecycle (draft → test → publish → archive), pinning resolution, and
/// org-scoping for the versioned Supplier Connection. Uses the real
/// <see cref="ProcuLinkDbContext"/> on the EF InMemory provider, like
/// <see cref="SupplierAcceptanceServiceTests"/>.
/// </summary>
public class SupplierConnectionServiceTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Service under test. The suppliers in this fixture have no orders, so the launch-batch-3
    /// test pack short-circuits to pass-with-note (the replay engine is constructed but never
    /// invoked); the lifecycle behaviour under test is unchanged.
    /// </summary>
    private static SupplierConnectionService MakeSvc(ProcuLinkDbContext db) =>
        new(db,
            new ReplayService(db, Array.Empty<ITransformService>()),
            new ProcuLink.Transform.Conformance.ConformanceService());

    /// <summary>Evidence-gated publish: run the test pack (stores evidence) then publish.</summary>
    private static async Task<ConnectionPublishOutcome> TestThenPublishAsync(
        SupplierConnectionService svc, Guid orgId, Guid connectionId, Guid revisionId)
    {
        var test = await svc.MarkTestAsync(orgId, connectionId, revisionId, CancellationToken.None);
        Assert.Equal(ConnectionTestStatus.Completed, test.Status);
        return await svc.PublishAsync(orgId, connectionId, revisionId, "user", CancellationToken.None);
    }

    private static ConnectionRevisionDraftInput Bundle(string? output = "xml") =>
        new(
            InputMappingJson: "{\"x\":1}",
            OutputMappingJson: null,
            OutputFormat: output,
            DeliveryProtocol: "http",
            DeliveryConfigJson: "{\"url\":\"https://example.test\"}",
            DeliveryAutoDeliver: false,
            CredentialsRef: null,
            AcceptanceProfileId: null,
            AcceptanceVersionNo: null,
            CatalogMode: "live",
            ItemMappings: new List<ConnectionItemMappingInput>
            {
                new("B1", "S1", 1.0f, "manual"),
            });

    private static async Task<(Guid orgId, Guid supplierId)> SeedSupplier(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme OÜ", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    [Fact]
    public async Task EnsureConnection_CreatesOnce_AndIsIdempotent()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);

        var c1 = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);
        var c2 = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);

        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.Equal(c1!.Id, c2!.Id);
        Assert.Equal(1, db.SupplierConnections.Count());
        Assert.Equal("Acme OÜ", c1.Name);
    }

    [Fact]
    public async Task EnsureConnection_CrossTenant_ReturnsNull()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);

        var otherOrg = Guid.NewGuid();
        var result = await svc.EnsureConnectionAsync(otherOrg, supplierId, "user", CancellationToken.None);

        Assert.Null(result); // org B cannot create a connection over org A's supplier
    }

    [Fact]
    public async Task CreateDraft_FirstRevision_IsVersion1Draft()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);

        var draft = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle(), cloneFromActive: false, "user", CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Equal(1, draft!.VersionNo);
        Assert.Equal("draft", draft.Status);
        Assert.Equal("xml", draft.OutputFormat);
        Assert.Single(draft.ItemMappings);
    }

    [Fact]
    public async Task Publish_FlipsActive_AndArchivesPrior()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);

        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("xml"), false, "user", CancellationToken.None);
        var pub1 = await TestThenPublishAsync(svc, orgId, conn.Id, v1!.Id);
        Assert.Equal(ConnectionPublishOutcome.Published, pub1);

        // connection now points at v1
        var afterFirst = await svc.GetAsync(orgId, conn.Id, CancellationToken.None);
        Assert.Equal(v1.Id, afterFirst!.ActiveRevisionId);

        // second draft + publish
        var v2 = await svc.CreateDraftAsync(orgId, conn.Id, Bundle("csv"), false, "user", CancellationToken.None);
        var pub2 = await TestThenPublishAsync(svc, orgId, conn.Id, v2!.Id);
        Assert.Equal(ConnectionPublishOutcome.Published, pub2);

        var afterSecond = await svc.GetAsync(orgId, conn.Id, CancellationToken.None);
        Assert.Equal(v2.Id, afterSecond!.ActiveRevisionId); // active flipped to v2

        var r1 = await svc.GetRevisionAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        var r2 = await svc.GetRevisionAsync(orgId, conn.Id, v2.Id, CancellationToken.None);
        Assert.Equal("archived", r1!.Status);    // prior published archived
        Assert.NotNull(r1.EffectiveTo);
        Assert.Equal("published", r2!.Status);

        // Exactly one published per connection.
        Assert.Equal(1, db.SupplierConnectionRevisions.Count(r => r.ConnectionId == conn.Id && r.Status == "published"));
    }

    [Fact]
    public async Task Publish_AlreadyPublished_ReturnsFalse()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle(), false, "user", CancellationToken.None);
        await TestThenPublishAsync(svc, orgId, conn.Id, v1!.Id);

        var second = await svc.PublishAsync(orgId, conn.Id, v1.Id, "user", CancellationToken.None);
        Assert.Equal(ConnectionPublishOutcome.InvalidStatus, second);
    }

    [Fact]
    public async Task UpdateDraft_RejectsPublishedRevision_Immutability()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle(), false, "user", CancellationToken.None);
        await TestThenPublishAsync(svc, orgId, conn.Id, v1!.Id);

        var result = await svc.UpdateDraftAsync(orgId, conn.Id, v1.Id, Bundle("csv"), CancellationToken.None);
        Assert.False(result); // published is frozen

        // unchanged
        var rev = await svc.GetRevisionAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        Assert.Equal("xml", rev!.OutputFormat);
    }

    [Fact]
    public async Task UpdateDraft_AllowsDraft_AndReplacesBundle()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("xml"), false, "user", CancellationToken.None);

        var result = await svc.UpdateDraftAsync(orgId, conn.Id, v1!.Id, Bundle("csv"), CancellationToken.None);
        Assert.True(result);

        var rev = await svc.GetRevisionAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        Assert.Equal("csv", rev!.OutputFormat);
    }

    [Fact]
    public async Task CreateDraft_CloneFromActive_SnapshotsPublishedBundle()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("xml"), false, "user", CancellationToken.None);
        await TestThenPublishAsync(svc, orgId, conn.Id, v1!.Id);

        var v2 = await svc.CreateDraftAsync(orgId, conn.Id, input: null, cloneFromActive: true, "user", CancellationToken.None);

        Assert.NotNull(v2);
        Assert.Equal(2, v2!.VersionNo);
        Assert.Equal("draft", v2.Status);
        Assert.Equal("xml", v2.OutputFormat);          // cloned from published v1
        Assert.Equal("{\"x\":1}", v2.InputMappingJson);
        Assert.Single(v2.ItemMappings);
        Assert.Equal("S1", v2.ItemMappings[0].SupplierItemCode);
    }

    [Fact]
    public async Task Archive_ActiveRevision_ClearsActivePointer()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle(), false, "user", CancellationToken.None);
        await TestThenPublishAsync(svc, orgId, conn.Id, v1!.Id);

        var archived = await svc.ArchiveAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        Assert.True(archived);

        var after = await svc.GetAsync(orgId, conn.Id, CancellationToken.None);
        Assert.Null(after!.ActiveRevisionId);
        var rev = await svc.GetRevisionAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        Assert.Equal("archived", rev!.Status);
    }

    [Fact]
    public async Task AllQueries_AreOrgScoped()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgA, supplierA) = await SeedSupplier(db);
        var connA = await svc.EnsureConnectionAsync(orgA, supplierA, "a", CancellationToken.None);
        var vA = await svc.CreateDraftAsync(orgA, connA!.Id, Bundle(), false, "a", CancellationToken.None);

        var orgB = Guid.NewGuid();

        // org B cannot read org A's connection or revision.
        Assert.Null(await svc.GetAsync(orgB, connA.Id, CancellationToken.None));
        Assert.Null(await svc.GetRevisionAsync(orgB, connA.Id, vA!.Id, CancellationToken.None));
        Assert.Empty(await svc.ListAsync(orgB, CancellationToken.None));
        // org B cannot publish, test, or roll back org A's revision.
        Assert.Equal(ConnectionPublishOutcome.NotFound,
            await svc.PublishAsync(orgB, connA.Id, vA.Id, "b", CancellationToken.None));
        Assert.Equal(ConnectionTestStatus.NotFound,
            (await svc.MarkTestAsync(orgB, connA.Id, vA.Id, CancellationToken.None)).Status);
        Assert.Null(await svc.RunTestPackAsync(orgB, connA.Id, vA.Id, CancellationToken.None));
        Assert.Equal(ConnectionRollbackStatus.NotFound,
            (await svc.RollbackAsync(orgB, connA.Id, vA.Id, "b", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Resolver_ReturnsActiveRevision_ForOrgScopedConnection()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var resolver = new ConnectionResolver(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);

        // No active revision yet → null (fall back to live).
        Assert.Null(await resolver.ResolveActiveRevisionAsync(orgId, supplierId, CancellationToken.None));

        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle(), false, "user", CancellationToken.None);
        await TestThenPublishAsync(svc, orgId, conn.Id, v1!.Id);

        var resolved = await resolver.ResolveActiveRevisionAsync(orgId, supplierId, CancellationToken.None);
        Assert.Equal(v1.Id, resolved);

        // Cross-tenant: org B resolves nothing.
        Assert.Null(await resolver.ResolveActiveRevisionAsync(Guid.NewGuid(), supplierId, CancellationToken.None));
    }

    // ── Launch-batch-7 review fix (finding 2): snapshot item codes are TRIMMED at write ──
    // The live ItemMappingService.UpsertAsync trims both codes, and BOTH resolvers (live
    // ResolveManyAsync and the pinned-revision ResolveFromSnapshot) match against TRIMMED
    // requested codes — a padded snapshot code could never resolve.

    [Fact]
    public async Task CreateDraft_PaddedItemCodes_AreTrimmed_AndResolveFromSnapshot()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);

        var input = Bundle() with
        {
            ItemMappings = new List<ConnectionItemMappingInput> { new("  B-100  ", "  S-200 ", 1.0f, "manual") },
        };
        var draft = await svc.CreateDraftAsync(orgId, conn!.Id, input, cloneFromActive: false, "user", CancellationToken.None);

        var row = Assert.Single(draft!.ItemMappings);
        Assert.Equal("B-100", row.BuyerItemCode);   // trimmed at write — mirrors ItemMappingService.UpsertAsync
        Assert.Equal("S-200", row.SupplierItemCode);

        // …and therefore the pinned-revision snapshot resolver (trimmed Ordinal keys) resolves a
        // padded incoming line against it — the exact scenario the padded write used to break.
        var snapshot = new[] { new EffectiveRevisionItemMapping(row.BuyerItemCode, row.SupplierItemCode) };
        var lines = new List<ParsedOrderLine>
        {
            new(LineNumber: 1, BuyerItemCode: "  B-100 ", Description: "Widget",
                Quantity: 1m, Unit: "EA", UnitPrice: 10m),
        };
        var map = OrderIngestionService.ResolveFromSnapshot(snapshot, lines);
        Assert.Equal("S-200", map["B-100"]);
    }

    [Fact]
    public async Task UpdateDraft_PaddedItemCodes_AreTrimmed()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "user", CancellationToken.None);
        var draft = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle(), cloneFromActive: false, "user", CancellationToken.None);

        var updated = await svc.UpdateDraftAsync(orgId, conn.Id, draft!.Id, Bundle() with
        {
            ItemMappings = new List<ConnectionItemMappingInput> { new(" B-9\t", " S-9 ", 0.8f, "imported") },
        }, CancellationToken.None);

        Assert.True(updated);
        var row = await db.ConnectionRevisionItemMappings.SingleAsync(m => m.RevisionId == draft.Id);
        Assert.Equal("B-9", row.BuyerItemCode);
        Assert.Equal("S-9", row.SupplierItemCode);
    }
}
