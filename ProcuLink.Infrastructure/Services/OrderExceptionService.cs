using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="IOrderExceptionService"/>.
/// All exception generation flows through the idempotent <see cref="ReconcileAsync"/>:
/// callers never construct exceptions directly. Reconcile is the single source of truth
/// for clearing — it compares the order's current status + lines against existing
/// exceptions and decides resolve-vs-recreate per (orderId, code):
///   • condition still holds + no open/ignored row for that code → OPEN a new one;
///   • condition still holds + an open/ignored row already exists  → leave it (no dup);
///   • condition no longer holds + an open row exists              → AUTO-RESOLVE it;
///   • condition no longer holds + only a resolved row exists      → do NOTHING
///     (a resolved row for a cleared condition is never resurrected).
/// Rows the operator has <c>ignored</c> are never auto-resolved or reopened, and an
/// ignored row for the current problem suppresses opening a duplicate (don't nag about
/// something deliberately dismissed). A manual resolve on a still-broken order is the
/// one case Reconcile deliberately overrides: because the underlying condition still
/// holds, a fresh <c>open</c> row is recreated so the unresolved problem stays visible.
///
/// <para><b>An order can hold more than one problem at once.</b> Reconcile used to derive a
/// SINGLE problem and auto-resolve every open row whose code differed from it, which made the
/// exception list a "worst current status" indicator rather than a work list. That is fine while
/// every problem is a function of <c>Status</c> — those are mutually exclusive by construction —
/// but it silently discards any problem that is ORTHOGONAL to status. <c>duplicate_po_number</c>
/// is the first such problem: an order can be a duplicate AND need mapping, and under the old
/// shape whichever one was derived second erased the other on the next pipeline touch. So
/// <see cref="ReconcileAsync"/> now builds a SET of current problems and diffs against that set.
/// The status-derived problems still come from <see cref="ProblemFor"/> and are still mutually
/// exclusive among themselves; independent detectors are added alongside.</para>
///
/// <para><b>Why duplicate PO numbers are DETECTED and not PREVENTED.</b> The obvious fix — a
/// unique index on <c>(org_id, po_number)</c> — is wrong, and would cause a worse bug than the one
/// it fixes. A buyer legitimately re-sends a corrected PO under the same number; a unique index
/// rejects it at the database and the corrected order is simply lost, with the refusal surfacing
/// as an ingest error on a channel nobody is watching. Suppliers also legitimately reuse PO
/// numbers across years. And the placeholder minted for a document that carried no PO number is
/// not a PO number at all, so a unique index would reject the second upload of two genuinely
/// different documents. The reported symptom is <i>"the same PO is in my inbox four times and
/// nothing flagged it"</i> — the missing thing is the flag, not a block. Detection is advisory:
/// severity <c>warning</c>, nothing in the pipeline reads it, the operator decides.</para>
///
/// <para><b>Which copy gets flagged.</b> Reconcile runs per order, so the FIRST copy to arrive sees
/// no sibling and opens nothing; each later copy sees it and is flagged. The first copy picks the
/// warning up whenever anything next reconciles it. That asymmetry is deliberate rather than
/// papered over with a fan-out write to other orders' exception rows — and it is bounded by the
/// property that actually matters: <c>OrderTransformService</c> reconciles on transform success,
/// which every order passes through before delivery, so <b>no duplicate can reach a supplier
/// without having been flagged first</b>.</para>
public sealed class OrderExceptionService : IOrderExceptionService
{
    private readonly ProcuLinkDbContext _db;

    public OrderExceptionService(ProcuLinkDbContext db) => _db = db;

    /// <summary>Exception code for "another order in this workspace already carries this PO number".</summary>
    public const string DuplicatePoNumberCode = "duplicate_po_number";

    /// <summary>
    /// How far either side of an order's creation another order must fall to count as a duplicate
    /// of it.
    ///
    /// <para>Unbounded comparison is wrong: PO numbering schemes wrap, and suppliers legitimately
    /// reuse a number across years, so a long-lived workspace would eventually flag every order
    /// against an unrelated one from three years ago — noise that trains operators to ignore the
    /// signal. The real duplicate-ingress window is hours to days (a re-send, a re-drop, the same
    /// batch arriving by email AND on SFTP), so 90 days is generous by two orders of magnitude
    /// against the case it must catch, while still killing the across-years false positive.</para>
    ///
    /// <para>The window is applied SYMMETRICALLY around each order's own <c>CreatedAt</c>, which
    /// keeps the relation symmetric: whichever of a pair gets reconciled sees the other. An
    /// asymmetric "look backwards only" window would flag the second arrival but never the first,
    /// so an operator opening the older order would see nothing wrong with it.</para>
    /// </summary>
    public static readonly TimeSpan DuplicatePoNumberWindow = TimeSpan.FromDays(90);

    private static (string Code, string Stage, string Severity, string Message)? ProblemFor(
        string status, bool hasUnresolvedLines)
    {
        // Unrouted takes PRECEDENCE over unresolved lines: with no supplier you cannot resolve
        // item codes, so "assign a supplier" is the one actionable problem to surface.
        if (status == OrderStatusConstants.Unrouted)
            return ("unrouted_order", "Route", "warning", "Order needs a supplier before it can be routed.");
        if (status == OrderStatusConstants.PendingReview || hasUnresolvedLines)
            return ("unresolved_mapping", "Map", "warning", "Order has lines that need a supplier item code.");
        if (status == OrderStatusConstants.Failed)
            return ("parse_failed", "Parse", "error", "Parsing the source file failed for this order.");
        if (status == OrderStatusConstants.TransformFailed)
            return ("transform_failed", "Transform", "error", "Transform failed for this order.");
        if (status == OrderStatusConstants.DeliveryFailed)
            return ("delivery_failed", "Deliver", "error", "Delivery to the supplier failed.");
        // Severity is 'warning', not 'error': we do not know that this delivery failed, and an
        // unknown outcome dressed as a failure is what makes an operator click Send again — handing
        // the supplier the duplicate PO the park exists to prevent. This is the FALLBACK wording:
        // ReconcileAsync prefers the sentence the park itself wrote to the attempt row, because that
        // one knows WHY the order parked (a file-drop connection that will refuse a repeat send
        // needs the opposite advice from a channel that cannot de-duplicate one). Null protocol =
        // the channel-agnostic wording: this method works from order status alone.
        if (status == OrderStatusConstants.DeliveryUnconfirmed)
            return ("delivery_unconfirmed", "Deliver", "warning",
                DeliveryService.BuildUnconfirmedMessage(protocol: null));
        if (status == OrderStatusConstants.RejectedBySupplier)
            return ("supplier_rejected", "Deliver", "error", "The supplier rejected this order.");
        if (status == OrderStatusConstants.DeliveryDeadLetter)
            return ("dead_letter", "Deliver", "critical", "Delivery retries are exhausted (dead-letter).");
        return null;
    }

    public async Task ReconcileAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        var order = await _db.PurchaseOrders
            .Where(o => o.Id == orderId && o.OrgId == orgId)
            .Select(o => new { o.Status, o.PoNumber, o.PoNumberNormalized, o.CreatedAt, o.IsSample })
            .FirstOrDefaultAsync(ct);
        if (order is null) return;

        // Query the lines directly rather than via the Lines navigation so this works
        // regardless of whether the caller's DbContext maps the collection navigation.
        var hasUnresolved = await _db.PurchaseOrderLines
            .AnyAsync(l => l.OrderId == orderId && l.NeedsReview, ct);
        var problem = ProblemFor(order.Status, hasUnresolved);

        // A parked order's sentence is the park's OWN, read back rather than rebuilt. The park
        // knows WHY it stopped and writes that to the attempt it finalises; this method knows only
        // the status, and the two reasons need OPPOSITE advice — telling the operator of a
        // file-drop connection that will refuse a repeat send to "send it again" walks the order
        // into dead-letter and takes "mark it delivered" away with it. Reusing the builder was not
        // enough: it reused the FORM, not the sentence. Falls back to the generic wording if no
        // parked attempt carries one (a park written before this shipped, or a hand-set status).
        if (problem is { } parked && parked.Code == "delivery_unconfirmed")
        {
            var parkSentence = await _db.DeliveryAttempts
                .AsNoTracking()
                .Where(a => a.OrgId == orgId
                         && a.OrderId == orderId
                         && a.Status == DeliveryAttempt.StatusUnconfirmed)
                .OrderByDescending(a => a.AttemptedAt)
                .Select(a => a.ErrorMessage)
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(parkSentence))
                problem = (parked.Code, parked.Stage, parked.Severity, parkSentence!);
        }

        // The current problem SET: the one status-derived problem (if any), plus every independent
        // detector below. See the class doc for why this is a set and not a single value.
        var problems = new List<(string Code, string Stage, string Severity, string Message)>();
        if (problem is { } statusProblem) problems.Add(statusProblem);

        // ── duplicate PO number ────────────────────────────────────────────────────────
        // The detection key is (OrgId, PoNumberNormalized) inside a rolling window. Note what is
        // deliberately NOT in that key:
        //   • SUPPLIER is not in it. Scoping by supplier would miss the exact case this exists for:
        //     the same PO arriving by email AND on SFTP routes by auto-detect, so one copy can be
        //     routed and the other still `unrouted` with a null supplier. Keying on supplier makes
        //     the two invisible to each other and the cross-channel duplicate goes unflagged again.
        //   • BUYER is not in it, for the same reason — buyer name is parsed, and the two copies can
        //     disagree or be null.
        //   • The INGRESS CHANNEL is not in it, and that is the whole point. Every transport ledger
        //     (idempotency_keys, email_import_records, imported_sftp_files, imported_s3_objects)
        //     keys on transport identity — request key, message id, remote path, object key — so
        //     none of them can see any of the others. The PO number is the only key the two copies
        //     share, and until now nothing in production looked an order up by it at all.
        // Placeholders (null key) and sample orders are excluded — see PoNumberIdentity and
        // SampleOrderService for why comparing either would fabricate warnings.
        if (!order.IsSample && order.PoNumberNormalized is { } poKey)
        {
            var windowStart = order.CreatedAt - DuplicatePoNumberWindow;
            var windowEnd   = order.CreatedAt + DuplicatePoNumberWindow;

            var hasSibling = await _db.PurchaseOrders
                .AsNoTracking()
                .AnyAsync(o => o.OrgId == orgId
                            && o.Id != orderId
                            && !o.IsSample
                            && o.PoNumberNormalized == poKey
                            && o.CreatedAt >= windowStart
                            && o.CreatedAt <= windowEnd, ct);

            if (hasSibling)
            {
                // No sibling COUNT in the sentence on purpose. The recreate step below leaves an
                // existing open row untouched, so a count baked in at first detection would still
                // read "1 other order" after two more copies arrived — a number that is wrong and
                // looks authoritative. Multiplicity is already visible: each copy carries its own
                // row, so four copies show four warnings.
                problems.Add((
                    DuplicatePoNumberCode,
                    "Validate",
                    // Warning, never error: we do NOT know this is a mistake. A buyer re-sending a
                    // corrected PO under the same number is a legitimate, common thing, and dressing
                    // it as an error would push operators to delete the corrected copy.
                    "warning",
                    $"PO number {order.PoNumber} is already on another order here. It may be the " +
                    "same order received twice — check before sending it to the supplier."));
            }
        }

        // Load EVERY exception for this order — open, ignored AND resolved — so dedup can
        // consider resolved rows too. Considering resolved rows is what stops a resolved
        // exception for a now-cleared condition from being silently resurrected as a fresh
        // open one on the next pipeline touch.
        var existing = await _db.OrderExceptions
            .Where(e => e.OrgId == orgId && e.OrderId == orderId)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        // Auto-resolve OPEN exceptions whose condition no longer holds (so fixing the order
        // really clears them). Ignored rows are never touched — the operator dismissed them
        // deliberately. Resolved rows are already terminal and left alone.
        foreach (var ex in existing)
        {
            if (ex.State != "open") continue;
            // Diffed against the whole problem SET, not one problem: an open row survives as long
            // as ANY current problem still carries its code. Comparing against a single problem is
            // what used to auto-resolve a real duplicate_po_number the moment the order also needed
            // mapping.
            if (!problems.Any(p => p.Code == ex.Code))
            {
                ex.State      = "resolved";
                ex.ResolvedAt = now;
            }
        }

        // Recreate decision (resolve-vs-recreate): open a fresh exception ONLY when the
        // current condition holds AND there is no open OR ignored row for that code.
        //  - A still-present condition with no live row → recreate (covers a manual resolve
        //    on a still-broken order: the problem is real, so it must stay visible).
        //  - A condition that is gone → `problem` won't match any code, so nothing is
        //    recreated and a resolved row stays resolved (no resurrection).
        // Resolved rows are intentionally NOT a recreate-blocker here: when the condition is
        // gone they can't match `problem.Code`, and when the condition persists the problem
        // is genuine and should reappear.
        foreach (var p in problems)
        {
            if (existing.Any(e => e.Code == p.Code
                               && (e.State == "open" || e.State == "ignored")))
                continue;

            _db.OrderExceptions.Add(new OrderException
            {
                Id        = Guid.NewGuid(),
                OrgId     = orgId,
                OrderId   = orderId,
                Stage     = p.Stage,
                Code      = p.Code,
                Severity  = p.Severity,
                State     = "open",
                Message   = p.Message,
                CreatedAt = now,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<OrderException>> ListAsync(Guid orgId, string? state, CancellationToken ct)
    {
        var q = _db.OrderExceptions.AsNoTracking().Where(e => e.OrgId == orgId);
        if (!string.IsNullOrWhiteSpace(state))
            q = q.Where(e => e.State == state);
        return await q.OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OrderException>> ListForOrderAsync(Guid orgId, Guid orderId, CancellationToken ct) =>
        await _db.OrderExceptions.AsNoTracking()
            .Where(e => e.OrgId == orgId && e.OrderId == orderId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct);

    public Task<bool> ResolveAsync(Guid orgId, Guid exceptionId, CancellationToken ct)
        => SetStateAsync(orgId, exceptionId, "resolved", ct);

    public Task<bool> IgnoreAsync(Guid orgId, Guid exceptionId, CancellationToken ct)
        => SetStateAsync(orgId, exceptionId, "ignored", ct);

    private async Task<bool> SetStateAsync(Guid orgId, Guid exceptionId, string state, CancellationToken ct)
    {
        var ex = await _db.OrderExceptions
            .Where(e => e.Id == exceptionId && e.OrgId == orgId)
            .FirstOrDefaultAsync(ct);
        if (ex is null) return false;
        ex.State = state;
        if (state == "resolved") ex.ResolvedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
