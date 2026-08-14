using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Launch batch 3 — connection lifecycle minimum:
/// (1) the test pack stores REAL evidence (replay leg + conformance leg) on the revision,
/// (2) publish is evidence-gated (blocked without passing evidence, allowed with; content
///     edits void prior evidence), and
/// (3) rollback clones a previously-published revision into a NEW published revision and
///     moves the active pointer.
/// Uses the real <see cref="ProcuLinkDbContext"/> on the EF InMemory provider.
/// </summary>
public class ConnectionTestEvidenceAndRollbackTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SupplierConnectionService MakeSvc(
        ProcuLinkDbContext db, params ITransformService[] transformers) =>
        new(db,
            new ReplayService(db, transformers),
            new ProcuLink.Transform.Conformance.ConformanceService());

    private static ConnectionRevisionDraftInput Bundle(string? output = "csv") =>
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
            ItemMappings: new List<ConnectionItemMappingInput> { new("B1", "S1", 1.0f, "manual") });

    private static async Task<(Guid orgId, Guid supplierId)> SeedSupplier(ProcuLinkDbContext db)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Evidence OÜ", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (orgId, supplierId);
    }

    private static async Task SeedResolvedOrderAsync(ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-EV-1",
            BuyerName  = "Evidence Buyer",
            OrderDate  = new DateOnly(2026, 6, 1),
            Currency   = "EUR",
            Status     = "ready",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
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
    }

    /// <summary>
    /// An order the CSV transform REFUSES: an unresolved supplier item code makes
    /// <c>CsvTransformService.ValidateOrder</c> throw <c>TransformValidationException</c>, which
    /// replay records as a per-order <c>OutputError</c> rather than a crash. Seeded alongside a
    /// resolvable order so the replay window contains both.
    /// </summary>
    private static async Task SeedUnrenderableOrderAsync(ProcuLinkDbContext db, Guid orgId, Guid supplierId)
    {
        var orderId = Guid.NewGuid();
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = supplierId,
            PoNumber   = "PO-EV-2-UNRESOLVED",
            BuyerName  = "Evidence Buyer",
            OrderDate  = new DateOnly(2026, 6, 2),
            Currency   = "EUR",
            Status     = "ready",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-2", SupplierItemCode = null, Description = "Unmapped widget",
                    Quantity = 1m, Unit = "EA", UnitPrice = 5m, NeedsReview = true, Confidence = null,
                },
            },
        });
        await db.SaveChangesAsync();
    }

    // ── Test pack stores evidence ────────────────────────────────────────────

    [Fact]
    public async Task MarkTest_NoOrders_RecordsNotExercised_NotAPass()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle(), false, "u", CancellationToken.None);

        var outcome = await svc.MarkTestAsync(orgId, conn.Id, v1!.Id, CancellationToken.None);

        // The run itself SUCCEEDED (the revision is marked test, evidence is stored) — it just
        // proved nothing, and that is a third thing, neither pass nor fail.
        Assert.Equal(ConnectionTestStatus.Completed, outcome.Status);
        Assert.NotNull(outcome.Evidence);
        Assert.Equal(TestPackOutcome.NotExercised, outcome.Evidence!.Outcome);
        Assert.False(outcome.Evidence.Passed);

        var rev = await svc.GetRevisionAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        Assert.Equal("test", rev!.Status);
        Assert.Equal(TestPackOutcomeNames.NotExercised, rev.TestOutcome);
        Assert.False(rev.TestPassed);   // narrowed mirror is fail-closed
        Assert.NotNull(rev.TestedAt);
        Assert.NotNull(rev.TestResultJson);
        Assert.Contains("replay", rev.TestResultJson);      // summary JSON carries every leg
        Assert.Contains("conformance", rev.TestResultJson);

        // No leg may report a pass: nothing ran.
        Assert.DoesNotContain($"\"outcome\":\"{TestLegOutcomeNames.Passed}\"", rev.TestResultJson);
    }

    /// <summary>
    /// THE DEFECT, VERBATIM. A supplier with ZERO orders — every newly configured supplier, i.e.
    /// exactly the population that runs checks at onboarding — ran the pack, was told it passed,
    /// and could publish a connection whose endpoint, credentials and output format had never been
    /// exercised. Real deliveries then routed through it.
    /// </summary>
    [Fact]
    public async Task Publish_SupplierWithZeroOrders_IsRefused_BecauseNothingWasExercised()
    {
        var db = MakeDb();
        var svc = MakeSvc(db, new CsvTransformService());
        var (orgId, supplierId) = await SeedSupplier(db);
        // NO orders seeded for this supplier — this is the whole control.
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("csv"), false, "u", CancellationToken.None);

        var test = await svc.MarkTestAsync(orgId, conn.Id, v1!.Id, CancellationToken.None);
        Assert.Equal(ConnectionTestStatus.Completed, test.Status);

        var publish = await svc.PublishAsync(orgId, conn.Id, v1.Id, "u", CancellationToken.None);

        // Refused — and refused with the state that says what was NOT done, not with the one that
        // implies a test failed. Nothing failed. Nothing ran.
        Assert.Equal(ConnectionPublishOutcome.EvidenceNotExercised, publish);

        var rev = await svc.GetRevisionAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        Assert.NotEqual("published", rev!.Status);
        var after = await svc.GetAsync(orgId, conn.Id, CancellationToken.None);
        Assert.Null(after!.ActiveRevisionId);   // active pointer never moved
    }

    [Fact]
    public async Task MarkTest_WithRealOrder_RunsReplayLeg_AndStoresEvidence()
    {
        var db = MakeDb();
        // Real CSV transformer wired → the replay leg genuinely renders the recent order.
        var svc = MakeSvc(db, new CsvTransformService());
        var (orgId, supplierId) = await SeedSupplier(db);
        await SeedResolvedOrderAsync(db, orgId, supplierId);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("csv"), false, "u", CancellationToken.None);

        var outcome = await svc.MarkTestAsync(orgId, conn.Id, v1!.Id, CancellationToken.None);

        Assert.Equal(ConnectionTestStatus.Completed, outcome.Status);
        Assert.Equal(TestPackOutcome.Passed, outcome.Evidence!.Outcome);
        Assert.Contains("\"orderCount\":1", outcome.Evidence.SummaryJson);

        // CSV has no named standards profile, so the standards leg is NOT_APPLICABLE — reported,
        // and specifically NOT reported as a pass. It used to say `"skipped":true` and then be
        // counted as one by the pack's conjunction, which is how "checks passed" came to cover a
        // standards check that is permanently vacuous for the most common output format.
        Assert.Contains($"\"outcome\":\"{TestLegOutcomeNames.NotApplicable}\"", outcome.Evidence.SummaryJson);
    }

    /// <summary>
    /// The standards leg being permanently inapplicable must not become a deadlock either: CSV,
    /// JSON and XML are buildable, publishable formats (CSV is the sample default) and no operator
    /// action can ever give them a named profile. Replay is the load-bearing leg — it ran, against
    /// a real order — so publish proceeds.
    /// </summary>
    [Fact]
    public async Task Publish_CsvOutput_WithRealOrder_Publishes_DespiteInapplicableStandardsLeg()
    {
        var db = MakeDb();
        var svc = MakeSvc(db, new CsvTransformService());
        var (orgId, supplierId) = await SeedSupplier(db);
        await SeedResolvedOrderAsync(db, orgId, supplierId);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("csv"), false, "u", CancellationToken.None);

        await svc.MarkTestAsync(orgId, conn.Id, v1!.Id, CancellationToken.None);

        Assert.Equal(ConnectionPublishOutcome.Published,
            await svc.PublishAsync(orgId, conn.Id, v1.Id, "u", CancellationToken.None));
    }

    /// <summary>
    /// Replay renders one of two orders. The old criterion was `rendered > 0`, so this reported a
    /// PASS while the summary printed its own contradiction underneath — "2 orders, 1 render
    /// error". The replay window is the most RECENT orders, so a render error in it predicts real
    /// orders dying at transform.
    /// </summary>
    [Fact]
    public async Task RunTestPack_SomeOrdersRenderAndSomeDoNot_Fails_HeadlineMatchesDetail()
    {
        var db = MakeDb();
        var svc = MakeSvc(db, new CsvTransformService());
        var (orgId, supplierId) = await SeedSupplier(db);
        await SeedResolvedOrderAsync(db, orgId, supplierId);
        await SeedUnrenderableOrderAsync(db, orgId, supplierId);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("csv"), false, "u", CancellationToken.None);

        var evidence = await svc.RunTestPackAsync(orgId, conn.Id, v1!.Id, CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.Equal(TestPackOutcome.Failed, evidence!.Outcome);
        Assert.Contains("\"outputErrors\":1", evidence.SummaryJson);
        Assert.Contains($"\"outcome\":\"{TestLegOutcomeNames.Failed}\"", evidence.SummaryJson);

        Assert.Equal(ConnectionPublishOutcome.EvidenceRequired,
            await svc.PublishAsync(orgId, conn.Id, v1.Id, "u", CancellationToken.None));
    }

    [Fact]
    public async Task RunTestPack_RevisionThatCannotRenderAnyOrder_FailsHonestly()
    {
        var db = MakeDb();
        // NO transformers registered → every replayed order fails to render.
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        await SeedResolvedOrderAsync(db, orgId, supplierId);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("csv"), false, "u", CancellationToken.None);

        var evidence = await svc.RunTestPackAsync(orgId, conn.Id, v1!.Id, CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.Equal(TestPackOutcome.Failed, evidence!.Outcome); // stored honestly, not thrown
        var rev = await svc.GetRevisionAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        Assert.False(rev!.TestPassed);
        // A real fault, so it is FAILED — distinct from the not-exercised state, which means
        // nothing was found because nothing ran.
        Assert.Equal(TestPackOutcomeNames.Failed, rev.TestOutcome);
        Assert.Contains("\"outputErrors\":1", rev.TestResultJson!);

        // ...and the failed evidence does NOT satisfy the publish gate.
        var publish = await svc.PublishAsync(orgId, conn.Id, v1.Id, "u", CancellationToken.None);
        Assert.Equal(ConnectionPublishOutcome.EvidenceRequired, publish);
    }

    // ── Evidence-gated publish ───────────────────────────────────────────────

    [Fact]
    public async Task Publish_WithoutEvidence_ReturnsEvidenceRequired_AndDoesNotPublish()
    {
        var db = MakeDb();
        var svc = MakeSvc(db);
        var (orgId, supplierId) = await SeedSupplier(db);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle(), false, "u", CancellationToken.None);

        var result = await svc.PublishAsync(orgId, conn.Id, v1!.Id, "u", CancellationToken.None);

        Assert.Equal(ConnectionPublishOutcome.EvidenceRequired, result);
        var rev = await svc.GetRevisionAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        Assert.Equal("draft", rev!.Status); // untouched
        var after = await svc.GetAsync(orgId, conn.Id, CancellationToken.None);
        Assert.Null(after!.ActiveRevisionId); // pointer unmoved
    }

    [Fact]
    public async Task Publish_WithPassingEvidence_Publishes()
    {
        var db = MakeDb();
        // A real transformer AND a real order, so the pack genuinely exercises the revision.
        // This test used to seed neither and still reach Published — which was the defect wearing
        // a green tick.
        var svc = MakeSvc(db, new CsvTransformService());
        var (orgId, supplierId) = await SeedSupplier(db);
        await SeedResolvedOrderAsync(db, orgId, supplierId);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("csv"), false, "u", CancellationToken.None);

        var test = await svc.MarkTestAsync(orgId, conn.Id, v1!.Id, CancellationToken.None);
        Assert.Equal(TestPackOutcome.Passed, test.Evidence!.Outcome);

        var result = await svc.PublishAsync(orgId, conn.Id, v1.Id, "u", CancellationToken.None);

        Assert.Equal(ConnectionPublishOutcome.Published, result);
        var after = await svc.GetAsync(orgId, conn.Id, CancellationToken.None);
        Assert.Equal(v1.Id, after!.ActiveRevisionId);
    }

    [Fact]
    public async Task UpdateDraft_AfterTest_VoidsEvidence_PublishRequiresRetest()
    {
        var db = MakeDb();
        var svc = MakeSvc(db, new CsvTransformService(), new XmlTransformService());
        var (orgId, supplierId) = await SeedSupplier(db);
        await SeedResolvedOrderAsync(db, orgId, supplierId);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);
        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("csv"), false, "u", CancellationToken.None);

        // Test → evidence stored.
        await svc.MarkTestAsync(orgId, conn.Id, v1!.Id, CancellationToken.None);

        // Content edit AFTER the test → evidence voided, UpdatedAt stamped.
        await svc.UpdateDraftAsync(orgId, conn.Id, v1.Id, Bundle("xml"), CancellationToken.None);
        var edited = await svc.GetRevisionAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        Assert.Null(edited!.TestPassed);
        Assert.Null(edited.TestedAt);
        // The verdict is voided with the rest of the evidence — a stale "passed" must not outlive
        // the run that earned it and be read as a verdict on the edited bundle.
        Assert.Null(edited.TestOutcome);
        Assert.NotNull(edited.UpdatedAt);

        // Publish is blocked until a fresh test run.
        Assert.Equal(ConnectionPublishOutcome.EvidenceRequired,
            await svc.PublishAsync(orgId, conn.Id, v1.Id, "u", CancellationToken.None));

        // Re-test → publish allowed.
        await svc.MarkTestAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        Assert.Equal(ConnectionPublishOutcome.Published,
            await svc.PublishAsync(orgId, conn.Id, v1.Id, "u", CancellationToken.None));
    }

    // ── Rollback ─────────────────────────────────────────────────────────────

    private async Task<(SupplierConnectionService svc, ProcuLinkDbContext db, Guid orgId,
        SupplierConnection conn, SupplierConnectionRevision v1, SupplierConnectionRevision v2)>
        SeedTwoPublishedRevisionsAsync()
    {
        var db = MakeDb();
        // Rollback is about the pointer, not the gate — but reaching a published revision at all
        // now requires the pack to have genuinely exercised the bundle, so the fixture supplies
        // the transformers and the order that lets it.
        var svc = MakeSvc(db, new CsvTransformService(), new XmlTransformService());
        var (orgId, supplierId) = await SeedSupplier(db);
        await SeedResolvedOrderAsync(db, orgId, supplierId);
        var conn = await svc.EnsureConnectionAsync(orgId, supplierId, "u", CancellationToken.None);

        var v1 = await svc.CreateDraftAsync(orgId, conn!.Id, Bundle("xml"), false, "u", CancellationToken.None);
        await svc.MarkTestAsync(orgId, conn.Id, v1!.Id, CancellationToken.None);
        Assert.Equal(ConnectionPublishOutcome.Published,
            await svc.PublishAsync(orgId, conn.Id, v1.Id, "u", CancellationToken.None));

        var v2 = await svc.CreateDraftAsync(orgId, conn.Id, Bundle("csv"), false, "u", CancellationToken.None);
        await svc.MarkTestAsync(orgId, conn.Id, v2!.Id, CancellationToken.None);
        Assert.Equal(ConnectionPublishOutcome.Published,
            await svc.PublishAsync(orgId, conn.Id, v2.Id, "u", CancellationToken.None));

        return (svc, db, orgId, conn, v1, v2);
    }

    [Fact]
    public async Task Rollback_ClonesBundle_MovesPointer_ArchivesCurrent()
    {
        var (svc, db, orgId, conn, v1, v2) = await SeedTwoPublishedRevisionsAsync();

        // v1 is archived (superseded by v2); roll back to it.
        var result = await svc.RollbackAsync(orgId, conn.Id, v1.Id, "operator", CancellationToken.None);

        Assert.Equal(ConnectionRollbackStatus.Completed, result.Status);
        var v3 = result.NewRevision!;
        Assert.Equal(3, v3.VersionNo);                      // next version, NOT a mutation of v1
        Assert.Equal("published", v3.Status);
        Assert.Equal("rollback:operator", v3.CreatedBy);
        Assert.Equal("xml", v3.OutputFormat);               // v1's bundle restored
        Assert.Equal("{\"x\":1}", v3.InputMappingJson);
        Assert.Single(v3.ItemMappings);                     // child rows cloned
        Assert.Equal("S1", v3.ItemMappings[0].SupplierItemCode);
        Assert.NotEqual(v1.Id, v3.Id);

        // Pointer moved atomically to the clone; v2 archived; v1 stays archived/immutable.
        var after = await svc.GetAsync(orgId, conn.Id, CancellationToken.None);
        Assert.Equal(v3.Id, after!.ActiveRevisionId);
        var r1 = await svc.GetRevisionAsync(orgId, conn.Id, v1.Id, CancellationToken.None);
        var r2 = await svc.GetRevisionAsync(orgId, conn.Id, v2.Id, CancellationToken.None);
        Assert.Equal("archived", r1!.Status);
        Assert.Equal("archived", r2!.Status);
        Assert.NotNull(r2.EffectiveTo);

        // Exactly one published revision per connection still holds.
        Assert.Equal(1, db.SupplierConnectionRevisions.Count(
            r => r.ConnectionId == conn.Id && r.Status == "published"));
    }

    [Fact]
    public async Task Rollback_TargetNeverPublished_OrCurrentlyPublished_IsInvalidTarget()
    {
        var (svc, _, orgId, conn, _, v2) = await SeedTwoPublishedRevisionsAsync();

        // A fresh draft was never published → invalid rollback target.
        var draft = await svc.CreateDraftAsync(orgId, conn.Id, Bundle("json"), false, "u", CancellationToken.None);
        var toDraft = await svc.RollbackAsync(orgId, conn.Id, draft!.Id, "u", CancellationToken.None);
        Assert.Equal(ConnectionRollbackStatus.InvalidTarget, toDraft.Status);

        // The CURRENTLY published revision is a no-op target → invalid.
        var toCurrent = await svc.RollbackAsync(orgId, conn.Id, v2.Id, "u", CancellationToken.None);
        Assert.Equal(ConnectionRollbackStatus.InvalidTarget, toCurrent.Status);
    }
}
