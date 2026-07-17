# Webhook Status From-Guard + Rejection Semantics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop an HMAC-authenticated supplier callback from marking a never-dispatched order `delivered`, and stop a supplier rejection from being written as a transport failure that a sweeper then re-sends.

**Architecture:** One canonical from-status set (`OrderStatusMachine.WebhookReportableFrom`) gates the only two mutating branches of `WebhookIngressController.Status`. A reported status that already matches short-circuits to an idempotent 200; a reported terminal status from a non-dispatched state is audited and answered 409. `status == "rejected"` starts writing `rejected_by_supplier` instead of `delivery_failed`. The two status maps are then corrected to match the new reachability.

**Tech Stack:** .NET 8, ASP.NET Core 8, EF Core 8, xUnit, FluentAssertions, Moq.

**Spec:** [`docs/superpowers/specs/2026-07-16-webhook-status-from-guard-design.md`](../specs/2026-07-16-webhook-status-from-guard-design.md)

## Global Constraints

- Branch `fix/webhook-status-from-guard`, base `main` @ db02350, worktree `recursing-gould-83bf7f`. Do not rebase onto `claude/confident-elbakyan-e26059`.
- Build: `dotnet build ProcuLink.slnx`. Windows dev, Linux CI — local green ≠ CI green.
- Security-adjacent (CLAUDE.md high-care area): `/code-review` before merge, never skip.
- All EF queries org-scoped. No raw SQL. EF Core only.
- Never write an ORDER status as a string literal — reference `OrderStatusConstants` / `OrderStatusMachine`. The WEBHOOK WIRE vocabulary (`"delivered"`, `"rejected"`, `"received"`, `"in_progress"`) stays as literals: it is the supplier-facing payload contract, not an order status, and the existing `AllowedStatuses` set already spells it that way. The `switch` that maps wire value → order status is exactly where the two vocabularies meet.
- Do NOT touch `KnownObserverOnlyEdges` — it does not exist on this base. It is on the unmerged, diverged `claude/confident-elbakyan-e26059` (dff05af).
- Plain-language user-facing copy: the 409 body is one human sentence with actual-vs-expected.

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `ProcuLink.Core/Constants/OrderStatusMachine.cs` | Canonical status machine + operation entry guards | Add `WebhookReportableFrom`; promote 4 edges into `Transitions` |
| `ProcuLink.Api/Controllers/WebhookIngressController.cs` | HMAC webhook ingress endpoints | Rewrite the `Status` mutate block; add `RejectStatusCallbackAsync` |
| `ProcuLink.Infrastructure/Services/OrderStatusTransitionObserver.cs` | Log-only transition telemetry | Add `delivery_held → delivered`; remove 2 `rejected_by_supplier` edges |
| `ProcuLink.Api.Tests/Controllers/WebhookIngressControllerTests.cs` | Endpoint behaviour | Add helpers + 9 tests |
| `ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs` | Machine pins | Add `WebhookReportableFrom` pin + 5 InlineData |

---

### Task 1: `WebhookReportableFrom` canonical set

The controller must reference one canonical set, not a hand-written literal — this mirrors the existing `RedeliverableFrom` convention in the same file.

**Files:**
- Modify: `ProcuLink.Core/Constants/OrderStatusMachine.cs:82-83` (insert after `RedeliverableFrom`)
- Test: `ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs:86` (insert after `RedeliverableFrom_MatchesThePriorLiteralExactly`)

**Interfaces:**
- Consumes: `OrderStatusConstants` (already `using static` in both files).
- Produces: `public static readonly IReadOnlySet<string> OrderStatusMachine.WebhookReportableFrom` — consumed by Task 3.

- [ ] **Step 1: Write the failing test**

In `ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs`, insert immediately after the `RedeliverableFrom_MatchesThePriorLiteralExactly` fact (line 86):

```csharp
    [Fact]
    public void WebhookReportableFrom_IsExactlyTheDispatchedStates()
    {
        // A supplier status callback may report a terminal outcome ONLY for an order that was
        // genuinely dispatched. rejected_by_supplier is deliberately absent (a supplier that
        // rejected must not silently flip the order to delivered); delivery_held is present
        // (delivery_failed -> delivery_held is real, so a held order may already have been sent).
        OrderStatusMachine.WebhookReportableFrom.Should()
            .BeEquivalentTo(new[]
            {
                ReadyToDeliver, Delivering, Delivered,
                DeliveryFailed, DeliveryDeadLetter, DeliveryHeld,
            });
    }

    [Fact]
    public void WebhookReportableFrom_ExcludesEveryPreDispatchState()
    {
        // The bug this guards: a callback could force 'delivered' onto an order still in the
        // parse/review pipeline -- marked shipped, never sent. Pinned explicitly so a future
        // widening of the set has to argue with this test.
        foreach (var preDispatch in new[]
                 {
                     PendingParse, Parsing, Unrouted, PendingReview, Ready,
                     Transforming, TransformFailed, Failed, RejectedBySupplier,
                 })
            OrderStatusMachine.WebhookReportableFrom.Should().NotContain(preDispatch);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter "FullyQualifiedName~OrderStatusMachineTests"
```

Expected: FAIL to **compile** with `CS0117: 'OrderStatusMachine' does not contain a definition for 'WebhookReportableFrom'`. A compile failure is a valid RED here — the symbol does not exist yet.

- [ ] **Step 3: Write the minimal implementation**

In `ProcuLink.Core/Constants/OrderStatusMachine.cs`, insert immediately after the `RedeliverableFrom` declaration (after line 83, before `private static readonly IReadOnlySet<string> EmptySet`):

```csharp
    /// <summary>
    /// A supplier status callback (<c>POST /api/webhook-ingress/{slug}/status</c>) may report a
    /// terminal outcome — <see cref="OrderStatusConstants.Delivered"/> or
    /// <see cref="OrderStatusConstants.RejectedBySupplier"/> — ONLY for an order that was
    /// genuinely dispatched. Anything earlier in the pipeline means the supplier is reporting on
    /// an order it was never sent (a stale or mis-mapped orderId), which the endpoint answers 409
    /// rather than marking a never-sent order delivered.
    ///
    /// <para>NOTE (corrected during implementation): this para originally claimed a pre-send
    /// race. It is FALSE -- the D-1 claim commits Status=Delivering (DeliveryService.cs:205-224)
    /// BEFORE OpenDispatchAttemptAsync (:315) and the wire send (:320), so an order is never
    /// still ready_to_deliver when a supplier could ACK. See commit 5370f6c for the accurate
    /// prose: ready_to_deliver stays in the set because the behaviour is pinned by a live test,
    /// the observer already declares the intent, and an MV-1 mapping edit can reset a DISPATCHED
    /// order back through ready -> ready_to_deliver, where a late ACK legitimately lands.</para>
    ///
    /// <para><c>delivery_held</c> is included because <c>delivery_failed → delivery_held</c> is a
    /// real edge (A5): a held order may already have been sent, and refusing its late ACK would
    /// make the reactivation re-drive send it a SECOND time.</para>
    ///
    /// <para><c>rejected_by_supplier</c> is deliberately ABSENT: a supplier that rejected must not
    /// silently flip the order to delivered, because a human has likely already acted on the
    /// rejection. A genuine retraction is an operator re-drive, not an automatic write. A REPEATED
    /// rejection callback is still a 200 — the endpoint short-circuits when the reported status
    /// already matches the order's status.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> WebhookReportableFrom =
        Set(ReadyToDeliver, Delivering, Delivered, DeliveryFailed, DeliveryDeadLetter, DeliveryHeld);
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter "FullyQualifiedName~OrderStatusMachineTests"
```

Expected: PASS. All pre-existing `OrderStatusMachineTests` still pass (nothing else changed).

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Core/Constants/OrderStatusMachine.cs ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs
git commit -m "feat(orders): add OrderStatusMachine.WebhookReportableFrom

The canonical set of from-states a supplier status callback may report a
terminal outcome for. Mirrors the RedeliverableFrom convention so the
controller references one canonical set instead of a hand-written literal.

Unused until the next commit wires it into WebhookIngressController.Status.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: A supplier rejection writes `rejected_by_supplier` (P1)

`status == "rejected"` currently writes `delivery_failed`. `StrandedFailedDeliveryDetectionService.cs:46` builds its sweep predicate on the explicit written premise that *"a supplier rejection lands in rejected_by_supplier"* — false today — and `DeliveryService.RetryDeliveryAsync:803` retries from `delivery_failed`. So a rejected PO ages past the 3h threshold, gets swept, and is re-sent to the supplier who rejected it.

This task also drops the `!= delivered` condition on the rejection branch (spec D3): a business rejection arriving after our HTTP 200 is currently answered `200 OK` and dropped, which contradicts the "HTTP 200 ≠ supplier business acceptance" north star.

**Files:**
- Modify: `ProcuLink.Api/Controllers/WebhookIngressController.cs:1-10` (add `using`), `:163-175` (the mutate block)
- Test: `ProcuLink.Api.Tests/Controllers/WebhookIngressControllerTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 yet.
- Produces: test helpers `SeedOrderAsync(ProcuLinkDbContext, Guid orgId, Guid orderId, string status) -> Task` and `StubVerifier(Mock<IHmacWebhookVerifier>, string slug, Guid orgId) -> void`, both consumed by Task 3.

- [ ] **Step 1: Add the test helpers**

In `ProcuLink.Api.Tests/Controllers/WebhookIngressControllerTests.cs`, add these two helpers immediately after the `SetHttpContext` method (after line 73), and add `using ProcuLink.Core.Constants;` to the file's using block:

```csharp
    /// <summary>Seeds one routed order in <paramref name="status"/> for <paramref name="orgId"/>.</summary>
    private static async Task SeedOrderAsync(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, string status)
    {
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id         = orderId,
            OrgId      = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber   = "PO-GUARD-001",
            Status     = status,
            OrderDate  = DateOnly.FromDateTime(DateTime.UtcNow),
            Currency   = "EUR",
            CreatedAt  = DateTime.UtcNow,
            UpdatedAt  = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Stubs the HMAC verifier to accept, resolving to <paramref name="orgId"/>.</summary>
    private static void StubVerifier(Mock<IHmacWebhookVerifier> verifier, string slug, Guid orgId)
        => verifier
            .Setup(v => v.VerifyAsync(
                slug, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HmacVerificationResult(true, null, orgId));
```

- [ ] **Step 2: Write the failing tests**

Append to `ProcuLink.Api.Tests/Controllers/WebhookIngressControllerTests.cs`, inside the class, after the existing `Status_WhenSupplierReportsDelivered_OrderStatusUpdatedAndAuditWritten` test (after line 251):

```csharp
    [Theory]
    [InlineData(OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.DeliveryFailed)]
    [InlineData(OrderStatusConstants.ReadyToDeliver)]
    public async Task Status_RejectedCallback_WritesRejectedBySupplier_NotDeliveryFailed(string from)
    {
        // A supplier business rejection is NOT a transport failure. Writing delivery_failed lets
        // StrandedFailedDeliveryDetectionService sweep the order after its aged threshold and
        // re-drive it (RetryDeliveryAsync retries from delivery_failed) -- re-sending a PO the
        // supplier explicitly rejected. That sweeper's own comment (:46) justifies its predicate
        // on the premise that "a supplier rejection lands in rejected_by_supplier".
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, from);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"rejected\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);
    }

    [Fact]
    public async Task Status_RejectedCallbackForDeliveredOrder_WritesRejectedBySupplier()
    {
        // HTTP 200 is not supplier business acceptance. The prior `order.Status != "delivered"`
        // condition answered a post-delivery business rejection with 200 OK and dropped it.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.Delivered);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"rejected\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter "FullyQualifiedName~WebhookIngressControllerTests"
```

Expected: 4 FAIL. The three Theory cases fail with `Expected order.Status to be "rejected_by_supplier", but found "delivery_failed"`. `Status_RejectedCallbackForDeliveredOrder_WritesRejectedBySupplier` fails with `…but found "delivered"` (the rejection was ignored entirely).

- [ ] **Step 4: Write the minimal implementation**

In `ProcuLink.Api/Controllers/WebhookIngressController.cs`, add to the using block (after line 6, `using ProcuLink.Core.Entities;`):

```csharp
using ProcuLink.Core.Constants;
```

Then replace lines 163-175 (the comment plus both `if` branches) with:

```csharp
        // A supplier business rejection is NOT a transport failure. delivery_failed would put the
        // order back in reach of the retry machinery -- StrandedFailedDeliveryDetectionService
        // sweeps aged delivery_failed orders with attempts remaining, and RetryDeliveryAsync
        // retries from delivery_failed -- so we would re-send a PO the supplier explicitly
        // rejected. (That sweeper's predicate is justified on the premise that a supplier
        // rejection lands in rejected_by_supplier: StrandedFailedDeliveryDetectionService.cs:46.)
        //
        // A rejection is honoured even for an already-delivered order: HTTP 200 from the channel
        // is transport success, never supplier business acceptance.
        if (status == "rejected")
        {
            order.Status    = OrderStatusConstants.RejectedBySupplier;
            order.UpdatedAt = DateTime.UtcNow;
        }
        else if (status == "delivered" && order.Status != OrderStatusConstants.Delivered)
        {
            order.Status    = OrderStatusConstants.Delivered;
            order.UpdatedAt = DateTime.UtcNow;
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter "FullyQualifiedName~WebhookIngressControllerTests"
```

Expected: PASS, including the pre-existing `Status_WhenSupplierReportsDelivered_OrderStatusUpdatedAndAuditWritten`.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Api/Controllers/WebhookIngressController.cs ProcuLink.Api.Tests/Controllers/WebhookIngressControllerTests.cs
git commit -m "fix(webhooks): a supplier rejection writes rejected_by_supplier, not delivery_failed

WebhookIngressController.Status wrote delivery_failed for status=='rejected'.
That is a transport-failure state, and it is retryable:
StrandedFailedDeliveryDetectionService sweeps delivery_failed orders aged past
the 3h threshold with attempts remaining and re-drives them, and
RetryDeliveryAsync retries from delivery_failed (:803). So a rejected PO aged
out and was re-sent to the supplier who rejected it.

The sweeper's own predicate comment (:46) justifies itself on the premise that
'a supplier rejection lands in rejected_by_supplier'. It did not. Now it does,
so the sweep excludes rejected orders exactly as its comment claims.

Also drop the `order.Status != delivered` condition on the rejection branch: a
business rejection arriving after our HTTP 200 was answered 200 OK and dropped
on the floor, contradicting the 'HTTP 200 != supplier business acceptance'
product rule. delivered -> rejected_by_supplier is already in both status maps.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: From-status guard + 409 + rejected-callback audit

The endpoint loads the order with no from-status predicate, so an HMAC-authenticated callback can force a terminal status onto an order in `pending_parse`, `parsing`, `unrouted`, `pending_review`, `ready`, or `transforming` — marked shipped, never sent.

**Files:**
- Modify: `ProcuLink.Api/Controllers/WebhookIngressController.cs` (the block written in Task 2), plus a new private helper before the `// ── helpers ──` divider (line 204)
- Test: `ProcuLink.Api.Tests/Controllers/WebhookIngressControllerTests.cs`

**Interfaces:**
- Consumes: `OrderStatusMachine.WebhookReportableFrom` (Task 1); `SeedOrderAsync` / `StubVerifier` (Task 2).
- Produces: `private Task<IActionResult> RejectStatusCallbackAsync(Guid orgId, PurchaseOrderEntity order, string reportedStatus, StatusPayload payload, CancellationToken ct)`; audit action string `"webhook_status_rejected"`.

- [ ] **Step 1: Write the failing tests**

Append to `ProcuLink.Api.Tests/Controllers/WebhookIngressControllerTests.cs` inside the class:

```csharp
    [Theory]
    [InlineData(OrderStatusConstants.PendingParse,  "delivered")]
    [InlineData(OrderStatusConstants.Parsing,       "delivered")]
    [InlineData(OrderStatusConstants.Unrouted,      "delivered")]
    [InlineData(OrderStatusConstants.PendingReview, "delivered")]
    [InlineData(OrderStatusConstants.Ready,         "delivered")]
    [InlineData(OrderStatusConstants.Transforming,  "delivered")]
    [InlineData(OrderStatusConstants.PendingParse,  "rejected")]
    [InlineData(OrderStatusConstants.Ready,         "rejected")]
    public async Task Status_TerminalCallbackForNeverDispatchedOrder_Returns409_AndDoesNotMutate(
        string from, string reported)
    {
        // The order was never sent to a supplier, so a supplier cannot be reporting on it. Marking
        // it delivered would be a silent lost order: shipped in the UI, never actually sent.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, from);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"{reported}\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(from, "a rejected callback must not mutate the order");
    }

    [Fact]
    public async Task Status_DeliveredCallbackForRejectedBySupplierOrder_Returns409_AndDoesNotMutate()
    {
        // rejected_by_supplier is terminal for webhooks: a supplier that rejected must not silently
        // flip the order to delivered -- a human has likely already acted on the rejection. A
        // genuine retraction is an operator re-drive, not an automatic write.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.RejectedBySupplier);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);
    }

    [Fact]
    public async Task Status_DuplicateRejectedCallback_IsIdempotent200_NotConflict()
    {
        // Callback endpoints get retried. A supplier re-posting a rejection it already delivered
        // must not get a 409 for work that succeeded -- this short-circuit is what lets
        // rejected_by_supplier stay OUT of WebhookReportableFrom.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.RejectedBySupplier);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"rejected\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.RejectedBySupplier);
        db.AuditEvents.Should().ContainSingle(e => e.Action == "webhook_status");
    }

    [Theory]
    [InlineData(OrderStatusConstants.ReadyToDeliver)]
    [InlineData(OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.DeliveryFailed)]
    [InlineData(OrderStatusConstants.DeliveryDeadLetter)]
    [InlineData(OrderStatusConstants.DeliveryHeld)]
    public async Task Status_DeliveredCallbackForDispatchedOrder_Returns200_AndMarksDelivered(string from)
    {
        // Every dispatched state accepts a late positive ACK. delivery_held is included because
        // delivery_failed -> delivery_held is real (A5): refusing a held order's ACK would make the
        // reactivation re-drive send it a SECOND time.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, from);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.Delivered);
    }

    [Fact]
    public async Task Status_RejectedCallback_WritesWebhookStatusRejectedAudit_WithActualStatus()
    {
        // A 409 nobody can see is a silent ignore with extra steps. The audit is what makes the
        // supplier's integration error actionable, so it carries the order's real status.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.PendingParse);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"delivered\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        await ctrl.Status("status-slug", CancellationToken.None);

        var audit = db.AuditEvents.Should()
            .ContainSingle(e => e.Action == "webhook_status_rejected" && e.EntityId == orderId)
            .Subject;
        audit.OrgId.Should().Be(orgId);
        var json = audit.Payload!.RootElement;
        json.GetProperty("ReportedStatus").GetString().Should().Be("delivered");
        json.GetProperty("OrderStatusAtReceipt").GetString().Should().Be(OrderStatusConstants.PendingParse);
        db.AuditEvents.Should().NotContain(e => e.Action == "webhook_status");
    }

    [Theory]
    [InlineData("received")]
    [InlineData("in_progress")]
    public async Task Status_NonMutatingCallback_FromAnyState_Returns200_AndDoesNotMutate(string reported)
    {
        // received/in_progress are pure telemetry -- they mutate nothing, so guarding them would
        // add noise without preventing harm. They stay 200 from any state.
        var db      = MakeDb();
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var (ctrl, verifier, _) = Build(db);

        await SeedOrderAsync(db, orgId, orderId, OrderStatusConstants.PendingParse);
        SetHttpContext(ctrl, body: $"{{\"orderId\":\"{orderId}\",\"status\":\"{reported}\"}}");
        StubVerifier(verifier, "status-slug", orgId);

        var result = await ctrl.Status("status-slug", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        var order = await db.PurchaseOrders.FindAsync(orderId);
        order!.Status.Should().Be(OrderStatusConstants.PendingParse);
        db.AuditEvents.Should().ContainSingle(e => e.Action == "webhook_status");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter "FullyQualifiedName~WebhookIngressControllerTests"
```

Expected FAIL:
- `Status_TerminalCallbackForNeverDispatchedOrder…` (8 cases): `Expected result to be ConflictObjectResult, but found OkObjectResult`.
- `Status_DeliveredCallbackForRejectedBySupplierOrder…`: same.
- `Status_RejectedCallback_WritesWebhookStatusRejectedAudit…`: `Expected db.AuditEvents to contain a single item matching e.Action == "webhook_status_rejected", but no such item was found`.
- `Status_DuplicateRejectedCallback…`, `Status_DeliveredCallbackForDispatchedOrder…`, `Status_NonMutatingCallback…`: PASS already (they pin behaviour Task 2 left correct — they must stay green).

- [ ] **Step 3: Write the minimal implementation**

In `ProcuLink.Api/Controllers/WebhookIngressController.cs`, replace the block written in Task 2 with:

```csharp
        // A supplier callback may report a terminal outcome ONLY for an order that was genuinely
        // dispatched (OrderStatusMachine.WebhookReportableFrom). Without this, an HMAC-authenticated
        // callback could force 'delivered' onto an order still in pending_parse/parsing/unrouted/
        // pending_review/ready/transforming -- a SILENT LOST ORDER: shipped in the UI, never sent.
        //
        // A rejection writes rejected_by_supplier, never delivery_failed: delivery_failed is a
        // retryable transport state, so StrandedFailedDeliveryDetectionService would sweep the aged
        // order and re-drive it (RetryDeliveryAsync retries from delivery_failed) -- re-sending a PO
        // the supplier explicitly rejected. That sweeper's predicate is justified on exactly this
        // premise (StrandedFailedDeliveryDetectionService.cs:46).
        //
        // 'received'/'in_progress' are pure telemetry: they mutate nothing, so they are NOT guarded
        // (a 409 there would add noise without preventing harm) and stay 200 from any state.
        var target = status switch
        {
            "delivered" => OrderStatusConstants.Delivered,
            "rejected"  => OrderStatusConstants.RejectedBySupplier,
            _           => null,
        };

        // Reported status already matches => idempotent replay. Callback endpoints get retried, and
        // a supplier re-posting a rejection it already delivered must not get a 409 for work that
        // succeeded. This short-circuit is what lets rejected_by_supplier stay OUT of the from-set.
        if (target is not null && !string.Equals(order.Status, target, StringComparison.Ordinal))
        {
            if (!OrderStatusMachine.WebhookReportableFrom.Contains(order.Status))
                return await RejectStatusCallbackAsync(orgId, order, status, payload, ct);

            order.Status    = target;
            order.UpdatedAt = DateTime.UtcNow;
        }
```

Then add this helper immediately before the `// ── helpers ──` divider:

```csharp
    /// <summary>
    /// A terminal status callback for an order that was never dispatched — almost always a supplier
    /// integration posting a stale or mis-mapped orderId. Audited (a 409 nobody can see is a silent
    /// ignore with extra steps) and answered 409: the request is well-formed and authentic, it
    /// conflicts with the order's current state. Well-behaved clients treat 4xx as permanent and
    /// stop retrying, which is what we want for a genuine integration error.
    ///
    /// <para>The order is tracked but left unmodified, so this SaveChanges writes the audit row
    /// only — never a status change.</para>
    /// </summary>
    private async Task<IActionResult> RejectStatusCallbackAsync(
        Guid                   orgId,
        PurchaseOrderEntity    order,
        string                 reportedStatus,
        StatusPayload          payload,
        CancellationToken      ct)
    {
        var auditPayload = JsonSerializer.Serialize(new
        {
            ReportedStatus       = reportedStatus,
            OrderStatusAtReceipt = order.Status,
            payload.Reason,
            OccurredAt           = payload.OccurredAt ?? DateTimeOffset.UtcNow,
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            UserId     = null,
            EntityType = "PurchaseOrder",
            EntityId   = order.Id,
            Action     = "webhook_status_rejected",
            Payload    = JsonDocument.Parse(auditPayload),
            CreatedAt  = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Webhook status={ReportedStatus} REJECTED for order {OrderId} (org {OrgId}): the order is " +
            "'{OrderStatus}' and was never dispatched to a supplier, so a supplier cannot report a " +
            "terminal outcome for it. Almost always an integration posting a stale or mis-mapped orderId.",
            reportedStatus, order.Id, orgId, order.Status);

        return Conflict(new
        {
            error = $"This order has not been sent to a supplier yet (it is '{order.Status}'), "
                  + $"so a '{reportedStatus}' update cannot be applied to it. Check that the orderId "
                  + "in the callback matches an order you received.",
        });
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter "FullyQualifiedName~WebhookIngressControllerTests"
```

Expected: PASS, all cases, including the pre-existing `Status_WhenSupplierReportsDelivered_OrderStatusUpdatedAndAuditWritten` (its seed is `ready_to_deliver`, which is in the from-set).

- [ ] **Step 5: Verify the guard is load-bearing (mutation check)**

Temporarily add `OrderStatusConstants.PendingParse` to `WebhookReportableFrom` in `ProcuLink.Core/Constants/OrderStatusMachine.cs`, re-run the Api.Tests filter above, and confirm `Status_TerminalCallbackForNeverDispatchedOrder_Returns409_AndDoesNotMutate` FAILS for the `pending_parse` cases. Then revert the edit and confirm green again. A guard test that cannot fail is not a guard test.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Api/Controllers/WebhookIngressController.cs ProcuLink.Api.Tests/Controllers/WebhookIngressControllerTests.cs
git commit -m "fix(webhooks): guard the status callback on a dispatched from-status

WebhookIngressController.Status loaded the order by id with no from-status
predicate. The only guard was 'not already delivered', so an HMAC-authenticated
supplier callback could drive ANY order in ANY state straight to a terminal,
customer-visible status -- including one still in pending_parse, parsing,
unrouted, pending_review, ready, or transforming. An order that was never sent
got marked delivered: a silent lost order.

Gate both mutating branches on OrderStatusMachine.WebhookReportableFrom and
answer 409 otherwise, audited as webhook_status_rejected with the order's actual
status. A supplier posting a callback for a not-yet-sent order is a real
integration error and is now surfaced rather than silently applied.

A reported status that already matches short-circuits to an idempotent 200 --
callback endpoints get retried, and that short-circuit is what lets
rejected_by_supplier stay out of the from-set while a repeated rejection still
succeeds.

Blast radius of the bug was bounded: HMAC slug is the tenant key (no
cross-tenant reach), and the write needs a v4 orderId Guid that a supplier only
learns by receiving the document. The realistic trigger was a mis-mapped
integration, not an attacker.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Correct both status maps to the new reachability

Three of the five edges that `OrderStatusMachine.Transitions` calls impossible are real flows the observer documents as intended ("late supplier ACK"). `delivery_held → delivered` becomes newly reachable under the guard. The two `rejected_by_supplier` edges become unreachable.

**Files:**
- Modify: `ProcuLink.Core/Constants/OrderStatusMachine.cs:38-52`
- Modify: `ProcuLink.Infrastructure/Services/OrderStatusTransitionObserver.cs:85-87` and `:106-110`
- Test: `ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs:10-56`

**Interfaces:**
- Consumes: `OrderStatusMachine.WebhookReportableFrom` (Task 1) — as the argument for which edges are reachable.
- Produces: nothing consumed downstream.

- [ ] **Step 1: Write the failing tests**

In `ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs`, add these four lines to the `IsAllowed_RealTransitions_AreAllowed` Theory (after line 34, `[InlineData(DeliveryDeadLetter, Ready)]`):

```csharp
    // Supplier status webhook: a late positive ACK from every dispatched state. All four are
    // documented as intended in OrderStatusTransitionObserver and gated by WebhookReportableFrom.
    [InlineData(ReadyToDeliver, Delivered)]      // ACK races our own 'delivering' write
    [InlineData(DeliveryFailed, Delivered)]      // late positive ACK after a failed attempt
    [InlineData(DeliveryDeadLetter, Delivered)]  // late positive ACK after dead-lettering
    [InlineData(DeliveryHeld, Delivered)]        // ACK for an order sent before the billing hold
```

And add this line to the `IsAllowed_ImpossibleTransitions_AreRejected` Theory (after line 54, `[InlineData(Parsing, Delivered)]`):

```csharp
    [InlineData(RejectedBySupplier, Delivered)]  // terminal for webhooks: no silent un-rejection
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter "FullyQualifiedName~OrderStatusMachineTests"
```

Expected: 4 FAIL — `IsAllowed_RealTransitions_AreAllowed` for each new case, with `Expected OrderStatusMachine.IsAllowed(from, to) to be true because <from> -> delivered is a real flow, but found False`. The `RejectedBySupplier, Delivered` impossible-case already PASSES (`Transitions[RejectedBySupplier]` is empty) and must stay green.

- [ ] **Step 3: Write the minimal implementation**

In `ProcuLink.Core/Constants/OrderStatusMachine.cs`, replace lines 36-52 (the `ReadyToDeliver` comment block through the `DeliveryDeadLetter` entry) with:

```csharp
            // ready_to_deliver/delivered → ready: a mapping edit after transform (MV-1) invalidates
            // the artifact and resets the order so the next Send re-transforms.
            // ready_to_deliver → delivery_held: a mid-pipeline billing flip pauses (not fails) delivery.
            // ready_to_deliver → delivered: NOTE -- the pre-send race this line originally cited is
            // FALSE (the D-1 claim commits Status=Delivering before the send). See commit 5370f6c for
            // the accurate prose: an MV-1 mapping edit resets a DISPATCHED order to ready, the next
            // Send re-transforms to ready_to_deliver, and a late ACK for the original dispatch lands.
            [ReadyToDeliver]     = Set(Delivering, Delivered, DeliveryFailed, DeliveryHeld, Ready, RejectedBySupplier),
            // Billing hold → released back to ready_to_deliver when the org returns to good standing.
            // delivery_held → delivered: a late ACK for an order sent before the hold landed
            // (delivery_failed → delivery_held is real, A5).
            [DeliveryHeld]       = Set(ReadyToDeliver, Delivered, Ready, RejectedBySupplier),
            [Delivering]         = Set(Delivered, DeliveryFailed, RejectedBySupplier),
            [Delivered]          = Set(DeliveryFailed, Ready, RejectedBySupplier),
            // delivery_failed/delivery_dead_letter → ready: the MV-1 sibling — a mapping edit after a
            // failed/dead-lettered delivery invalidates the stored artifact (Retry/requeue would ship it
            // un-re-transformed), so the order resets and the next Send re-transforms.
            // delivery_failed → delivery_held: A5 — a backoff retry for an org that lapsed to
            // read_only/past_due since the first attempt is held (not delivered) via HoldForBillingAsync.
            // delivery_failed/delivery_dead_letter → delivered: a late positive ACK from the supplier
            // status webhook. Both are gated by WebhookReportableFrom.
            [DeliveryFailed]     = Set(Delivering, Delivered, DeliveryDeadLetter, DeliveryHeld, Ready, RejectedBySupplier),
            // dead_letter → delivery_failed keeps this a superset of OrderStatusTransitionObserver's
            // map (a requeued dead-letter that fails again, or a late failure webhook) so IsAllowed
            // never rejects a transition the observer treats as expected.
            [DeliveryDeadLetter] = Set(Delivering, Delivered, DeliveryFailed, Ready, RejectedBySupplier),
```

In `ProcuLink.Infrastructure/Services/OrderStatusTransitionObserver.cs`, replace lines 84-87 (the `DeliveryHeld` comment + entry) with:

```csharp
            // Billing hold → released back to ready_to_deliver (re-driven) on reactivation, or a
            // late supplier ACK for an order that was already sent before the hold landed.
            [OrderStatusConstants.DeliveryHeld] = Set(
                OrderStatusConstants.ReadyToDeliver, OrderStatusConstants.Ready,
                OrderStatusConstants.Delivered, OrderStatusConstants.RejectedBySupplier),
```

And replace lines 106-110 (the `RejectedBySupplier` comment + entry) with:

```csharp
            // Rejected: corrected and re-driven through the loop. NOT delivered/delivery_failed --
            // WebhookIngressController.Status gates terminal callbacks on
            // OrderStatusMachine.WebhookReportableFrom, which deliberately excludes
            // rejected_by_supplier, so no path un-rejects an order automatically any more. If one
            // appears, this map SHOULD warn about it.
            [OrderStatusConstants.RejectedBySupplier] = Set(
                OrderStatusConstants.PendingReview, OrderStatusConstants.Ready,
                OrderStatusConstants.Transforming, OrderStatusConstants.Delivering),
```

- [ ] **Step 4: Run the full Infrastructure suite to verify**

```bash
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj
```

Expected: PASS. Specifically confirm `IsTerminal_TrueForTerminalStates` still passes for `rejected_by_supplier` — `Transitions[RejectedBySupplier]` stays `Set()` and was not touched.

- [ ] **Step 5: Build the solution and run both suites**

```bash
dotnet build ProcuLink.slnx --no-incremental
dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj
```

Expected: build clean; both suites green. Record the actual pass counts — do not claim green without the output. Known noise: `TwoConcurrentRetries…` in Api.Tests is a Docker-gated flake.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Core/Constants/OrderStatusMachine.cs ProcuLink.Infrastructure/Services/OrderStatusTransitionObserver.cs ProcuLink.Infrastructure.Tests/Constants/OrderStatusMachineTests.cs
git commit -m "fix(orders): reconcile both status maps with the webhook guard

OrderStatusMachine.Transitions called four real flows impossible. Three are
documented as intended in OrderStatusTransitionObserver -- ready_to_deliver ->
delivered ('may report a terminal state straight from ready_to_deliver'),
delivery_failed -> delivered and delivery_dead_letter -> delivered ('late
supplier ACK') -- and ready_to_deliver -> delivered is pinned by a live Api
test. They are late-ACK flows, not gaps. Promote them.

delivery_held -> delivered is newly reachable now that delivery_held is in
WebhookReportableFrom, so add it to both maps.

Remove rejected_by_supplier -> delivered and -> delivery_failed from the
observer: the guard makes them unreachable, so the observer SHOULD warn if a
future path performs one. rejected_by_supplier keeps an empty Transitions entry
and stays terminal -- IsTerminal_TrueForTerminalStates is untouched.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Review, push, and hand off

**Files:** none (process task).

- [ ] **Step 1: Run `/code-review`**

Security-adjacent, high-care area per CLAUDE.md — never skip. Review the full branch diff against `main`.

- [ ] **Step 2: Apply review findings**

Use `superpowers:receiving-code-review` — verify each finding technically before implementing it. Do not perform agreement.

- [ ] **Step 3: Push and check CI**

```bash
git push -u origin fix/webhook-status-from-guard
gh run list --branch fix/webhook-status-from-guard --limit 3
```

Windows dev, Linux CI: local green ≠ CI green. Wait for the verdict.

- [ ] **Step 4: Notify the concurrent session**

Tell session `confident-elbakyan-e26059` (branch `claude/confident-elbakyan-e26059`, dff05af) that main has advanced and its `KnownObserverOnlyEdges` needs pruning on rebase. All five webhook entries are now stale:

- `ready_to_deliver → delivered`, `delivery_failed → delivered`, `delivery_dead_letter → delivered` — promoted into `Transitions`, no longer drift.
- `rejected_by_supplier → delivered`, `rejected_by_supplier → delivery_failed` — removed from the observer, no longer drift.

Its two-sided assertion will fail and name each one. Its other exemptions (`Failed→*`, `TransformFailed→*`, `PendingParse→*`, `PendingReview→Failed`, `Ready→Failed`) are untouched. Its commit message premise — *"reachable only via WebhookIngressController.Status … reads as a missing guard rather than an intended flow"* — needs correcting: three of the five were intended flows.

- [ ] **Step 5: Finish the branch**

Use `superpowers:finishing-a-development-branch` to decide merge vs PR.
