using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// B-6 — "the same PO is in my inbox four times and nothing flagged it".
///
/// <para>The sting of that finding is CROSS-CHANNEL duplication. Each ingress path keeps its own
/// ledger keyed on transport identity — <c>idempotency_keys</c> on a request key,
/// <c>email_import_records</c> on a message id, <c>imported_sftp_files</c> on a remote path,
/// <c>imported_s3_objects</c> on an object key — and none of them can see any of the others. So the
/// tests below never assert "a duplicate is detected" in the abstract: they seed the two copies with
/// DIFFERENT transport ledger rows and DIFFERENT suppliers, leaving the PO number as the only key
/// the two share, and assert that this is enough.</para>
///
/// <para>Every positive here is paired with a negative. A detector that flags everything is worse
/// than no detector — it costs the operator the same attention and teaches them to ignore it — so
/// each "this trips" test has a sibling proving the nearest non-duplicate does NOT trip.</para>
/// </summary>
public class OrderExceptionDuplicatePoNumberTests
{
    private static ProcuLinkDbContext MakeDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Seeds an order the way an ingest path would: PO number and its comparison key resolved
    /// together through <see cref="PoNumberIdentity"/>, exactly as production does.
    /// </summary>
    private static Guid SeedOrder(
        ProcuLinkDbContext db,
        Guid orgId,
        string? poNumber,
        DateTime createdAt,
        Guid? supplierId = null,
        string status = "ready",
        bool unresolvedLine = false,
        bool isSample = false,
        string? inboundSenderDomain = null)
    {
        var orderId = Guid.NewGuid();
        var resolved = PoNumberIdentity.Resolve(poNumber, createdAt, orderId);

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id                  = orderId,
            OrgId               = orgId,
            SupplierId          = supplierId ?? Guid.NewGuid(),
            PoNumber            = resolved.Value,
            PoNumberNormalized  = resolved.Normalized,
            Currency            = "EUR",
            Status              = status,
            IsSample            = isSample,
            InboundSenderDomain = inboundSenderDomain,
            OrderDate           = DateOnly.FromDateTime(createdAt),
            CreatedAt           = createdAt,
            UpdatedAt           = createdAt,
            Lines = new List<PurchaseOrderLineEntity>
            {
                new()
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B1", NeedsReview = unresolvedLine,
                    Quantity = 1, UnitPrice = 1,
                },
            },
        });
        db.SaveChanges();
        return orderId;
    }

    /// <summary>Records the email channel's ledger claim for an order (IMAP / Postmark).</summary>
    private static void SeedEmailLedger(ProcuLinkDbContext db, Guid orgId, string messageId)
    {
        db.EmailImportRecords.Add(new EmailImportRecord
        {
            Id             = Guid.NewGuid(),
            OrgId          = orgId,
            ImapMessageId  = messageId,
            AttachmentHash = Guid.NewGuid().ToString("N"),
            ImportedAt     = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>Records the SFTP channel's ledger claim for an order.</summary>
    private static void SeedSftpLedger(ProcuLinkDbContext db, Guid orgId, Guid orderId, string remotePath)
    {
        db.ImportedSftpFiles.Add(new ImportedSftpFile
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            RemotePath = remotePath,
            FileHash   = Guid.NewGuid().ToString("N"),
            OrderId    = orderId,
            ImportedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static Task<List<OrderException>> OpenExceptionsFor(ProcuLinkDbContext db, Guid orderId) =>
        db.OrderExceptions.Where(e => e.OrderId == orderId && e.State == "open").ToListAsync();

    // ── the finding itself: cross-channel ────────────────────────────────────────────────

    /// <summary>
    /// THE test for B-6. Same PO number, two different ingress channels, two different suppliers,
    /// two ledgers that cannot see each other. Nothing but the PO number links them.
    /// </summary>
    [Fact]
    public async Task SamePoNumber_ArrivingByEmailAndBySftp_IsFlagged()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        // Copy 1 — arrived by email, routed to supplier A. Its ledger key is a message id.
        var emailOrder = SeedOrder(db, orgId, "PO-4471", at,
            supplierId: Guid.NewGuid(), inboundSenderDomain: "buyer.example.com");
        SeedEmailLedger(db, orgId, "postmark:msg-abc-123");

        // Copy 2 — the same document dropped on SFTP an hour later, auto-detected to a DIFFERENT
        // supplier. Its ledger key is a remote path. Neither ledger row mentions the other.
        var sftpOrder = SeedOrder(db, orgId, "PO-4471", at.AddHours(1),
            supplierId: Guid.NewGuid());
        SeedSftpLedger(db, orgId, sftpOrder, "/in/2026-08-15/po4471.csv");

        await new OrderExceptionService(db).ReconcileAsync(orgId, sftpOrder, CancellationToken.None);

        var open = await OpenExceptionsFor(db, sftpOrder);
        var dup  = Assert.Single(open, e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
        Assert.Equal("Validate", dup.Stage);
        Assert.Equal("warning", dup.Severity);
        Assert.Contains("PO-4471", dup.Message);

        // Anti-vacuity for the pairing itself: the email copy carries no duplicate exception,
        // because nothing has reconciled it since the SFTP copy landed. Proves the assertion above
        // came from the reconcile under test and not from blanket seeding.
        Assert.Empty(await OpenExceptionsFor(db, emailOrder));
    }

    /// <summary>
    /// ANTI-VACUITY for the test above: identical cross-channel setup — two channels, two ledgers,
    /// two suppliers, same org, same hour — differing ONLY in the PO number. Nothing is flagged.
    /// </summary>
    [Fact]
    public async Task DifferentPoNumbers_AcrossTheSameTwoChannels_IsNotFlagged()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, orgId, "PO-4471", at, inboundSenderDomain: "buyer.example.com");
        SeedEmailLedger(db, orgId, "postmark:msg-abc-123");

        var sftpOrder = SeedOrder(db, orgId, "PO-4472", at.AddHours(1));
        SeedSftpLedger(db, orgId, sftpOrder, "/in/2026-08-15/po4472.csv");

        await new OrderExceptionService(db).ReconcileAsync(orgId, sftpOrder, CancellationToken.None);

        Assert.DoesNotContain(await OpenExceptionsFor(db, sftpOrder),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
    }

    // ── the placeholder must not be a false positive ─────────────────────────────────────

    /// <summary>
    /// The placeholder collides by construction: it used to be <c>PO-{yyyyMMddHHmmss}</c>, truncated
    /// to whole seconds, so two uploads in the same second produced the identical string. Two
    /// genuinely different documents must never be reported as the same PO.
    /// </summary>
    [Fact]
    public async Task TwoPlaceholderOrdersCreatedInTheSameSecond_AreNotFlagged()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var sameSecond = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        var first  = SeedOrder(db, orgId, poNumber: null, createdAt: sameSecond);
        var second = SeedOrder(db, orgId, poNumber: null, createdAt: sameSecond);

        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, first, CancellationToken.None);
        await svc.ReconcileAsync(orgId, second, CancellationToken.None);

        Assert.DoesNotContain(await OpenExceptionsFor(db, first),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
        Assert.DoesNotContain(await OpenExceptionsFor(db, second),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
    }

    /// <summary>
    /// The other half of the placeholder fix: they are no longer the same STRING either, so the
    /// operator reading the queue sees two distinct orders rather than the same PO number twice.
    /// </summary>
    [Fact]
    public async Task PlaceholdersMintedInTheSameSecond_AreDistinctStrings_AndCarryNoComparisonKey()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var sameSecond = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        var first  = SeedOrder(db, orgId, poNumber: null, createdAt: sameSecond);
        var second = SeedOrder(db, orgId, poNumber: null, createdAt: sameSecond);

        var a = await db.PurchaseOrders.SingleAsync(o => o.Id == first);
        var b = await db.PurchaseOrders.SingleAsync(o => o.Id == second);

        Assert.NotEqual(a.PoNumber, b.PoNumber);
        Assert.Null(a.PoNumberNormalized);
        Assert.Null(b.PoNumberNormalized);
    }

    // ── the key: what is in it, and what is deliberately not ─────────────────────────────

    /// <summary>
    /// Supplier is NOT part of the detection key, and this pins it. The cross-channel case routes
    /// the two copies independently, so one can still be <c>unrouted</c> with a null supplier while
    /// the other is routed — scoping detection by supplier would make exactly that pair invisible.
    /// </summary>
    [Fact]
    public async Task SamePoNumber_WhenOneCopyHasNoSupplierYet_IsStillFlagged()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        var routed = SeedOrder(db, orgId, "PO-9001", at, supplierId: Guid.NewGuid());
        db.PurchaseOrders.Single(o => o.Id == routed).SupplierId = Guid.NewGuid();

        var unrouted = SeedOrder(db, orgId, "PO-9001", at.AddMinutes(5), status: "ready");
        var unroutedEntity = await db.PurchaseOrders.SingleAsync(o => o.Id == unrouted);
        unroutedEntity.SupplierId = null;
        await db.SaveChangesAsync();

        await new OrderExceptionService(db).ReconcileAsync(orgId, unrouted, CancellationToken.None);

        Assert.Contains(await OpenExceptionsFor(db, unrouted),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
    }

    /// <summary>Casing and padding differ across channels; the same PO is still the same PO.</summary>
    [Fact]
    public async Task SamePoNumber_DifferingOnlyInCaseAndWhitespace_IsFlagged()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, orgId, "po-4471 ", at);
        var second = SeedOrder(db, orgId, "  PO-4471", at.AddHours(2));

        await new OrderExceptionService(db).ReconcileAsync(orgId, second, CancellationToken.None);

        Assert.Contains(await OpenExceptionsFor(db, second),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
    }

    /// <summary>
    /// ANTI-VACUITY for normalization: it trims and case-folds, and stops there. <c>PO-1001</c> and
    /// <c>PO1001</c> are different supplier-facing identifiers and must not be folded together.
    /// </summary>
    [Fact]
    public async Task PoNumbersDifferingByPunctuation_AreNotFoldedTogether()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, orgId, "PO-1001", at);
        var second = SeedOrder(db, orgId, "PO1001", at.AddHours(1));

        await new OrderExceptionService(db).ReconcileAsync(orgId, second, CancellationToken.None);

        Assert.DoesNotContain(await OpenExceptionsFor(db, second),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
    }

    /// <summary>Suppliers legitimately reuse PO numbers across years — the window is what stops that noise.</summary>
    [Fact]
    public async Task SamePoNumber_ReusedOutsideTheWindow_IsNotFlagged()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, orgId, "PO-1001", at - OrderExceptionService.DuplicatePoNumberWindow.Add(TimeSpan.FromDays(1)));
        var recent = SeedOrder(db, orgId, "PO-1001", at);

        await new OrderExceptionService(db).ReconcileAsync(orgId, recent, CancellationToken.None);

        Assert.DoesNotContain(await OpenExceptionsFor(db, recent),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
    }

    /// <summary>
    /// Paired with the test above so the window is proved to have a live edge in BOTH directions —
    /// otherwise a detector that never fires would pass the out-of-window test for the wrong reason.
    /// </summary>
    [Fact]
    public async Task SamePoNumber_JustInsideTheWindow_IsFlagged()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, orgId, "PO-1001", at - OrderExceptionService.DuplicatePoNumberWindow.Subtract(TimeSpan.FromDays(1)));
        var recent = SeedOrder(db, orgId, "PO-1001", at);

        await new OrderExceptionService(db).ReconcileAsync(orgId, recent, CancellationToken.None);

        Assert.Contains(await OpenExceptionsFor(db, recent),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
    }

    /// <summary>Tenant isolation: another workspace's identical PO number is not this workspace's duplicate.</summary>
    [Fact]
    public async Task SamePoNumber_InADifferentOrg_IsNotFlagged()
    {
        var db    = MakeDb();
        var mine  = Guid.NewGuid();
        var other = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, other, "PO-4471", at);
        var ours = SeedOrder(db, mine, "PO-4471", at.AddHours(1));

        await new OrderExceptionService(db).ReconcileAsync(mine, ours, CancellationToken.None);

        Assert.DoesNotContain(await OpenExceptionsFor(db, ours),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
    }

    /// <summary>The demo order's PO number is a constant, so two of them must not report each other.</summary>
    [Fact]
    public async Task SampleOrders_AreNotFlagged()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, orgId, "DEMO-2026-001", at, isSample: true);
        var second = SeedOrder(db, orgId, "DEMO-2026-001", at.AddMinutes(1), isSample: true);

        await new OrderExceptionService(db).ReconcileAsync(orgId, second, CancellationToken.None);

        Assert.DoesNotContain(await OpenExceptionsFor(db, second),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
    }

    // ── co-existence with the status-derived problems ────────────────────────────────────

    /// <summary>
    /// Reconcile used to derive ONE problem and auto-resolve every open row whose code differed from
    /// it. A duplicate that also needed mapping therefore erased itself on the next pipeline touch.
    /// Both rows must stay open — they are different pieces of work.
    /// </summary>
    [Fact]
    public async Task DuplicateAndUnresolvedMapping_CoexistAsTwoOpenExceptions()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, orgId, "PO-4471", at);
        var second = SeedOrder(db, orgId, "PO-4471", at.AddHours(1),
            status: "pending_review", unresolvedLine: true);

        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, second, CancellationToken.None);

        // Capture the row IDENTITY, not just its state. The single-problem shape resolves the
        // duplicate row and then — because the recreate step sees no live row for that code any
        // more — immediately opens a REPLACEMENT. "Two open rows, one per code" therefore still
        // holds under the regression, and an assertion that stops at open-state/count cannot see
        // it. What the regression actually destroys is row continuity: a warning the operator
        // ignored or was reading is replaced by a different row on every pipeline touch, and the
        // resolved corpses pile up. So pin the id.
        var dupBefore = await db.OrderExceptions.SingleAsync(
            e => e.OrderId == second && e.Code == OrderExceptionService.DuplicatePoNumberCode);

        // A SECOND pass is the regression: the single-problem shape churned the row here.
        await svc.ReconcileAsync(orgId, second, CancellationToken.None);

        var open = await OpenExceptionsFor(db, second);
        Assert.Contains(open, e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
        Assert.Contains(open, e => e.Code == "unresolved_mapping");
        Assert.Equal(2, open.Count);

        // Same row, still open, and no second one was ever created for this code.
        var dupAfter = await db.OrderExceptions.SingleAsync(
            e => e.OrderId == second && e.Code == OrderExceptionService.DuplicatePoNumberCode);
        Assert.Equal(dupBefore.Id, dupAfter.Id);
        Assert.Equal("open", dupAfter.State);
        Assert.Null(dupAfter.ResolvedAt);
    }

    /// <summary>Repeated reconciles never accumulate rows.</summary>
    [Fact]
    public async Task Reconcile_IsIdempotent_ForTheDuplicateException()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, orgId, "PO-4471", at);
        var second = SeedOrder(db, orgId, "PO-4471", at.AddHours(1));

        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, second, CancellationToken.None);
        await svc.ReconcileAsync(orgId, second, CancellationToken.None);
        await svc.ReconcileAsync(orgId, second, CancellationToken.None);

        // Counted across EVERY state, not just open. Counting only open rows would call a service
        // that resolves-and-recreates on each pass "idempotent" — it always leaves exactly one open
        // row while quietly accumulating a resolved one per reconcile. Idempotent means no new rows.
        Assert.Equal(1, await db.OrderExceptions.CountAsync(
            e => e.OrderId == second
              && e.Code == OrderExceptionService.DuplicatePoNumberCode));
        Assert.Equal("open", (await db.OrderExceptions.SingleAsync(
            e => e.OrderId == second
              && e.Code == OrderExceptionService.DuplicatePoNumberCode)).State);
    }

    /// <summary>
    /// The operator's escape hatch. Correcting the PO number changes the comparison key, so the next
    /// reconcile finds no sibling and auto-resolves the warning. Without this the operator would fix
    /// the problem and still be nagged about it.
    /// </summary>
    [Fact]
    public async Task CorrectingThePoNumber_AutoResolvesTheDuplicateException()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, orgId, "PO-4471", at);
        var second = SeedOrder(db, orgId, "PO-4471", at.AddHours(1));

        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, second, CancellationToken.None);
        Assert.Contains(await OpenExceptionsFor(db, second),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);

        // What OrderResolutionService does when the operator retypes the header PO number.
        var entity = await db.PurchaseOrders.SingleAsync(o => o.Id == second);
        entity.PoNumber           = "PO-4471-B";
        entity.PoNumberNormalized = PoNumberIdentity.Normalize("PO-4471-B");
        await db.SaveChangesAsync();

        await svc.ReconcileAsync(orgId, second, CancellationToken.None);

        Assert.DoesNotContain(await OpenExceptionsFor(db, second),
            e => e.Code == OrderExceptionService.DuplicatePoNumberCode);
        Assert.Equal("resolved", (await db.OrderExceptions
            .SingleAsync(e => e.OrderId == second
                           && e.Code == OrderExceptionService.DuplicatePoNumberCode)).State);
    }

    /// <summary>An operator who dismissed the warning is not nagged again.</summary>
    [Fact]
    public async Task IgnoredDuplicate_IsNotReopened()
    {
        var db    = MakeDb();
        var orgId = Guid.NewGuid();
        var at    = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

        SeedOrder(db, orgId, "PO-4471", at);
        var second = SeedOrder(db, orgId, "PO-4471", at.AddHours(1));

        var svc = new OrderExceptionService(db);
        await svc.ReconcileAsync(orgId, second, CancellationToken.None);

        var row = await db.OrderExceptions.SingleAsync(
            e => e.OrderId == second && e.Code == OrderExceptionService.DuplicatePoNumberCode);
        Assert.True(await svc.IgnoreAsync(orgId, row.Id, CancellationToken.None));

        await svc.ReconcileAsync(orgId, second, CancellationToken.None);

        Assert.Equal(1, await db.OrderExceptions.CountAsync(
            e => e.OrderId == second && e.Code == OrderExceptionService.DuplicatePoNumberCode));
        Assert.Equal("ignored", (await db.OrderExceptions.SingleAsync(e => e.Id == row.Id)).State);
    }
}
