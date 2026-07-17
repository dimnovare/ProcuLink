# Delivery Unconfirmed Park — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a delivery crash-recovery re-drive hits a channel that cannot de-duplicate (ERP, email, legacy SMTP), do not re-send — park the order in a new `delivery_unconfirmed` state and let a human choose "Send again" or "Mark as delivered".

**Architecture:** Dispatchers declare a `ResendSafety` tier (`Safe` / `BestEffort` / `Unsafe`). `DeliveryService.OpenDispatchAttemptAsync` already re-adopts the pre-send `dispatching` attempt row on crash recovery; it now reports *that it re-adopted*, and that boolean + an `Unsafe` tier is the park trigger. SFTP/FTPS (`Safe`) and HTTP (`BestEffort`) re-drive exactly as today.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit + FluentAssertions, Hangfire. Backend only — the frontend is a separate plan in the `project-proculink` repo.

**Spec:** `docs/superpowers/specs/2026-07-16-delivery-unconfirmed-park-design.md`

## Global Constraints

- **Org-scoping is absolute.** Every EF query filters `OrganisationId`/`OrgId`. No exceptions.
- **No raw SQL.** EF Core only.
- **No database migration.** Both new statuses are string values in existing columns; there is no CHECK constraint on order status (verified — the only `HasCheckConstraint` in the repo is the org-slug one).
- **TDD.** Every task writes the failing test first and runs it to watch it fail before implementing.
- **Register every new transition in BOTH maps** — `OrderStatusMachine.Transitions` AND `OrderStatusTransitionObserver.AllowedTransitions`. Registering in only one is the exact drift `d4d6eac` had to fix.
- **Plain-language copy**, pinned verbatim in the spec. The park sentence is:
  > `Delivery unconfirmed. We sent this order to {supplier} but lost the connection before they confirmed it, and {channel} cannot tell us whether it arrived. Check with {supplier}, then either send it again or mark it delivered.`
- **Never fabricate an observed outcome.** "Mark as delivered" moves the *order* to `delivered` but leaves the *attempt* row `unconfirmed`.
- Build/test commands (run from repo root):
  - `dotnet build ProcuLink.slnx`
  - `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj`
  - `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj`
  - Windows dev, Linux CI: after pushing, check `gh run list`. Local green ≠ CI green.

---

### Task 1: ResendSafety tier on IDeliveryDispatcher

**Files:**
- Create: `ProcuLink.Core/Services/Delivery/ResendSafety.cs`
- Modify: `ProcuLink.Core/Services/Delivery/IDeliveryDispatcher.cs`
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/SftpDeliveryDispatcher.cs`
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/FtpsDeliveryDispatcher.cs`
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs`
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/EmailApiDeliveryDispatcher.cs`
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/SmtpDeliveryDispatcher.cs`
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/ErpDeliveryDispatchers.cs`
- Test: `ProcuLink.Infrastructure.Tests/Services/Dispatchers/DispatcherResendSafetyTests.cs` (create)

**Interfaces:**
- Produces: `ProcuLink.Core.Services.Delivery.ResendSafety` enum with members `Safe`, `BestEffort`, `Unsafe`; and `IDeliveryDispatcher.ResendSafety { get; }` — a **defaulted** interface property returning `ResendSafety.Unsafe`. Task 3 consumes both.

**Why defaulted, not abstract:** 6 production dispatchers implement this interface — and so do 14 test doubles. An abstract member costs 14 files of churn to buy what the table test below already provides. The default direction is fail-safe: a dispatcher that forgets to declare parks on crash recovery (conservative, never a duplicate).

- [ ] **Step 1: Write the failing test**

Create `ProcuLink.Infrastructure.Tests/Services/Dispatchers/DispatcherResendSafetyTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Dispatchers;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// Every production dispatcher must state, explicitly and on purpose, whether re-sending the
/// same artifact after an UNKNOWN outcome (a crash-recovery re-drive) can duplicate at the
/// counterparty. DeliveryService parks instead of re-sending when the tier is Unsafe.
///
/// This test is the enforcement point: a new production dispatcher fails here until someone
/// lists it and thinks about its idempotency contract. The interface default (Unsafe) is the
/// fail-safe backstop, not a substitute for that thought.
/// </summary>
public class DispatcherResendSafetyTests
{
    public static TheoryData<string, ResendSafety> ExpectedTiers => new()
    {
        // Deterministic overwrite filename — re-sending overwrites the same file.
        { "sftp", ResendSafety.Safe },
        { "ftps", ResendSafety.Safe },
        // Sends Idempotency-Key + X-Message-Id; honouring them is the supplier's choice.
        { "http", ResendSafety.BestEffort },
        // Message-ID dedup by a receiving MTA is best-effort and rarely applied.
        { "email", ResendSafety.Unsafe },
        { "smtp", ResendSafety.Unsafe },
        // No dedupe signal reaches the ERP endpoint at all.
        { "erp_erply", ResendSafety.Unsafe },
        { "erp_directo", ResendSafety.Unsafe },
    };

    [Theory]
    [MemberData(nameof(ExpectedTiers))]
    public void Dispatcher_DeclaresExpectedResendSafety(string protocol, ResendSafety expected)
    {
        var dispatcher = AllProductionDispatchers()
            .Single(d => string.Equals(d.Protocol, protocol, StringComparison.OrdinalIgnoreCase));

        dispatcher.ResendSafety.Should().Be(expected);
    }

    [Fact]
    public void EveryProductionDispatcher_IsCoveredByThisTest()
    {
        var covered = ExpectedTiers.Select(row => (string)row[0]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = AllProductionDispatchers().Select(d => d.Protocol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        actual.Should().BeEquivalentTo(covered,
            "a new delivery channel must declare its re-send safety on purpose — add it to ExpectedTiers");
    }

    private static IReadOnlyList<IDeliveryDispatcher> AllProductionDispatchers() => new IDeliveryDispatcher[]
    {
        new SftpDeliveryDispatcher(NullLogger<SftpDeliveryDispatcher>.Instance),
        new FtpsDeliveryDispatcher(NullLogger<FtpsDeliveryDispatcher>.Instance),
        new HttpDeliveryDispatcher(new FakeHttpClientFactory(), NullLogger<HttpDeliveryDispatcher>.Instance),
        new EmailApiDeliveryDispatcher(new FakeEmailApiClient(), NullLogger<EmailApiDeliveryDispatcher>.Instance),
        new SmtpDeliveryDispatcher(NullLogger<SmtpDeliveryDispatcher>.Instance),
        new ErplyDeliveryDispatcher(new IErpConnector[] { new FakeErpConnector("erp_erply") }),
        new DirectoDeliveryDispatcher(new IErpConnector[] { new FakeErpConnector("erp_directo") }),
    };
}
```

**Note for the implementer:** the exact constructor arguments above are a starting point — check each dispatcher's real constructor and adapt. The existing test doubles you need (`FakeHttpClientFactory`, `FakeEmailApiClient`, `FakeErpConnector`) may already exist under `ProcuLink.Infrastructure.Tests/TestDoubles/` or inside sibling dispatcher test files (`HttpDeliveryDispatcherTests.cs`, `EmailApiDeliveryDispatcherTests.cs`, `ErpConnectorTests.cs`) — reuse them; only write a new minimal fake if none exists. Do NOT change a dispatcher's constructor to make this test easier.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter DispatcherResendSafetyTests`
Expected: FAIL to COMPILE — `'IDeliveryDispatcher' does not contain a definition for 'ResendSafety'` and `The type or namespace name 'ResendSafety' could not be found`.

- [ ] **Step 3: Create the enum**

Create `ProcuLink.Core/Services/Delivery/ResendSafety.cs`:

```csharp
namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Whether re-sending the SAME artifact after an UNKNOWN outcome can duplicate at the
/// counterparty. Consulted only on a crash-recovery re-drive (a re-adopted in-flight
/// <c>dispatching</c> attempt row) — never on a first send, and never on a send whose
/// outcome was actually observed.
/// </summary>
public enum ResendSafety
{
    /// <summary>
    /// Re-sending cannot duplicate: the channel is inherently idempotent (SFTP/FTPS write a
    /// deterministic filename and overwrite). Re-drive freely.
    /// </summary>
    Safe,

    /// <summary>
    /// A dedupe signal IS transmitted, but honouring it is the counterparty's choice (HTTP
    /// <c>Idempotency-Key</c>). Re-drive; the residual is documented, not silently assumed away.
    /// </summary>
    BestEffort,

    /// <summary>
    /// No dedupe signal reaches the counterparty (ERP endpoints ignore the key; a caller-supplied
    /// email <c>Message-ID</c> is rarely honoured by a receiving MTA). A re-send after an unknown
    /// outcome duplicates the PO, so DeliveryService parks the order for a human decision instead.
    /// The interface default — a channel that has not thought about this parks rather than duplicates.
    /// </summary>
    Unsafe,
}
```

- [ ] **Step 4: Add the defaulted member to the interface**

In `ProcuLink.Core/Services/Delivery/IDeliveryDispatcher.cs`, add inside the interface, after `Protocol`:

```csharp
    /// <summary>
    /// Whether re-sending the same artifact after an UNKNOWN outcome can duplicate at the
    /// counterparty. Read by <c>DeliveryService</c> ONLY when a crash-recovery re-drive re-adopts
    /// an in-flight <c>dispatching</c> row: an <see cref="Core.Services.Delivery.ResendSafety.Unsafe"/>
    /// channel is parked for a human decision instead of blindly re-sent.
    /// <para>
    /// Defaults to <see cref="Core.Services.Delivery.ResendSafety.Unsafe"/> — the fail-safe
    /// direction. A dispatcher that has not declared its idempotency contract parks (conservative)
    /// rather than duplicates. Production dispatchers must still declare their tier explicitly;
    /// <c>DispatcherResendSafetyTests</c> enforces that.
    /// </para>
    /// </summary>
    ResendSafety ResendSafety => ResendSafety.Unsafe;
```

- [ ] **Step 5: Declare the tier on each production dispatcher**

`SftpDeliveryDispatcher` and `FtpsDeliveryDispatcher` — add next to `Protocol`:

```csharp
    // The deterministic filename is overwritten on re-send, so a crash-recovery re-drive
    // cannot leave the supplier holding two copies.
    public ResendSafety ResendSafety => ResendSafety.Safe;
```

`HttpDeliveryDispatcher`:

```csharp
    // Sends Idempotency-Key + X-Message-Id. Whether the supplier honours them is their choice,
    // so this is best-effort, not a guarantee — re-drive, and document the residual.
    public ResendSafety ResendSafety => ResendSafety.BestEffort;
```

`EmailApiDeliveryDispatcher` and `SmtpDeliveryDispatcher`:

```csharp
    // Only a deterministic Message-ID is set. Receiving-MTA dedup on a caller-supplied
    // Message-ID is best-effort and rarely applied, so a re-send after an unknown outcome
    // most likely lands a duplicate email.
    public ResendSafety ResendSafety => ResendSafety.Unsafe;
```

`ErpDeliveryDispatcherBase` (covers both `ErplyDeliveryDispatcher` and `DirectoDeliveryDispatcher`):

```csharp
    // No dedupe signal reaches the ERP: the connector contract accepts no idempotency key, and
    // both connectors are generic HTTP posts to a tenant-configured URL with no document model
    // or lookup API. A re-send after an unknown outcome creates a DUPLICATE ERP order.
    public ResendSafety ResendSafety => ResendSafety.Unsafe;
```

Each dispatcher file needs `using ProcuLink.Core.Services.Delivery;` if not already present.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter DispatcherResendSafetyTests`
Expected: PASS (9 tests — 7 theory rows + coverage test).

- [ ] **Step 7: Full build to prove the 14 test doubles still compile**

Run: `dotnet build ProcuLink.slnx`
Expected: Build succeeded, 0 errors. (This is the point of the defaulted member — if this fails, the member was made abstract by mistake.)

- [ ] **Step 8: Commit**

```bash
git add ProcuLink.Core/Services/Delivery/ResendSafety.cs ProcuLink.Core/Services/Delivery/IDeliveryDispatcher.cs ProcuLink.Infrastructure/Services/Dispatchers/ ProcuLink.Infrastructure.Tests/Services/Dispatchers/DispatcherResendSafetyTests.cs
git commit -m "feat(delivery): dispatchers declare re-send safety tier

Each channel is the only thing that knows its own idempotency contract.
Defaulted to Unsafe (fail-safe: an undeclared channel parks rather than
duplicates); a table test forces every production dispatcher to state its
tier on purpose.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: The two new statuses and their transitions

**Files:**
- Modify: `ProcuLink.Core/Entities/DeliveryAttempt.cs` (status constants block, ~line 64-77)
- Modify: `ProcuLink.Core/Constants/OrderStatusConstants.cs` (~line 22-50)
- Modify: `ProcuLink.Core/Constants/OrderStatusMachine.cs` (~line 38-52)
- Modify: `ProcuLink.Infrastructure/Services/OrderStatusTransitionObserver.cs` (~line 88-101)
- Test: `ProcuLink.Infrastructure.Tests/Services/OrderStatusTransitionObserverTests.cs` (existing — add cases)

**Interfaces:**
- Produces: `DeliveryAttempt.StatusUnconfirmed` (`"unconfirmed"`), `OrderStatusConstants.DeliveryUnconfirmed` (`"delivery_unconfirmed"`). Tasks 3–7 consume both.

- [ ] **Step 1: Write the failing test**

Add to `ProcuLink.Infrastructure.Tests/Services/OrderStatusTransitionObserverTests.cs` — follow the file's existing observer-silence pattern (the A5 `delivery_failed→delivery_held` case added in `d4d6eac` is the template; copy its shape exactly):

```csharp
    // The park (unknown-outcome crash recovery on a non-idempotent channel) moves
    // delivering → delivery_unconfirmed. Both transition maps must carry it, or the observer
    // logs a spurious "unexpected transition" for a move the system performs on purpose —
    // the exact map drift d4d6eac had to fix for A5.
    [Theory]
    [InlineData(OrderStatusConstants.Delivering, OrderStatusConstants.DeliveryUnconfirmed)]
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, OrderStatusConstants.Delivering)]
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, OrderStatusConstants.Delivered)]
    [InlineData(OrderStatusConstants.DeliveryUnconfirmed, OrderStatusConstants.Ready)]
    public void ParkTransitions_AreRegisteredInBothMaps_AndObserverStaysSilent(string from, string to)
    {
        OrderStatusMachine.IsAllowed(from, to).Should().BeTrue(
            $"{from} → {to} is a real flow the park performs");

        // Observer silence: assert via the same mechanism the sibling A5 test uses in this file.
        AssertObserverSilent(from, to);
    }
```

**Note for the implementer:** `AssertObserverSilent` is a placeholder for whatever this file already does to assert observer silence — read the existing A5 `delivery_failed → delivery_held` test in this file first and mirror it exactly (it may capture a fake logger, or call the observer directly). Do not invent a new assertion helper if the file has one.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter OrderStatusTransitionObserverTests`
Expected: FAIL to COMPILE — `'OrderStatusConstants' does not contain a definition for 'DeliveryUnconfirmed'`.

- [ ] **Step 3: Add the attempt status constant**

In `ProcuLink.Core/Entities/DeliveryAttempt.cs`, in the status-values block after `StatusDispatching`:

```csharp
    /// <summary>
    /// Terminal: the send HAPPENED but its outcome is unknown, and the channel cannot tell us
    /// whether it arrived. Set when a crash-recovery re-drive re-adopts an in-flight
    /// <c>dispatching</c> row on a <see cref="Core.Services.Delivery.ResendSafety.Unsafe"/>
    /// channel: re-sending could duplicate the PO, so the order is parked for a human decision
    /// instead. COUNTS toward the retry attempt cap — it consumed a real send.
    /// <para>
    /// Never rewritten to <c>success</c> by the operator's "Mark as delivered": that moves the
    /// ORDER to delivered and audits the human's assertion separately. We never fabricate a
    /// supplier ACK we did not observe.
    /// </para>
    /// </summary>
    public const string StatusUnconfirmed = "unconfirmed";
```

- [ ] **Step 4: Add the order status constant**

In `ProcuLink.Core/Constants/OrderStatusConstants.cs`, after `DeliveryFailed`:

```csharp
    /// <summary>
    /// A send happened but its outcome is unknown (a crash lost the ACK) on a channel that cannot
    /// de-duplicate a re-send — ERP, email, legacy SMTP. Re-sending could hand the supplier a
    /// duplicate PO, so the order waits for a human: "Send again" or "Mark as delivered".
    /// Deliberately NOT <c>delivery_failed</c> — we do not know that it failed. Non-billable
    /// until an operator confirms delivery (the meter counts only delivered/rejected_by_supplier).
    /// </summary>
    public const string DeliveryUnconfirmed = "delivery_unconfirmed";
```

Add `DeliveryUnconfirmed,` to the `All` set (after `DeliveryFailed,`).

- [ ] **Step 5: Register in OrderStatusMachine**

In `ProcuLink.Core/Constants/OrderStatusMachine.cs`:

Change the `[Delivering]` entry to add `DeliveryUnconfirmed`:

```csharp
            // delivering → delivery_unconfirmed: the park — a crash-recovery re-drive on a channel
            // that cannot de-duplicate stops rather than risk a duplicate PO.
            [Delivering]         = Set(Delivered, DeliveryFailed, DeliveryUnconfirmed, RejectedBySupplier),
```

Add a new entry after `[DeliveryFailed]`:

```csharp
            // Unknown-outcome park. The operator decides: send again (→ delivering) or confirm the
            // supplier got it (→ delivered). A mapping edit invalidates the artifact (→ ready, the
            // MV-1 sibling). Dead-letter/failed remain reachable if a later re-send exhausts retries.
            [DeliveryUnconfirmed] = Set(Delivering, Delivered, DeliveryFailed, DeliveryDeadLetter, Ready, RejectedBySupplier),
```

- [ ] **Step 6: Mirror in OrderStatusTransitionObserver**

In `ProcuLink.Infrastructure/Services/OrderStatusTransitionObserver.cs`, change `[OrderStatusConstants.Delivering]` to include `OrderStatusConstants.DeliveryUnconfirmed`, and add the mirrored entry:

```csharp
            [OrderStatusConstants.Delivering] = Set(
                OrderStatusConstants.Delivered, OrderStatusConstants.DeliveryFailed,
                OrderStatusConstants.DeliveryUnconfirmed,
                OrderStatusConstants.RejectedBySupplier, OrderStatusConstants.DeliveryDeadLetter),
            // Unknown-outcome park: the operator sends again or confirms delivery. Mirrors
            // OrderStatusMachine — both maps or neither (the d4d6eac drift lesson).
            [OrderStatusConstants.DeliveryUnconfirmed] = Set(
                OrderStatusConstants.Delivering, OrderStatusConstants.Delivered,
                OrderStatusConstants.DeliveryFailed, OrderStatusConstants.DeliveryDeadLetter,
                OrderStatusConstants.Ready, OrderStatusConstants.RejectedBySupplier),
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter OrderStatusTransitionObserverTests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add ProcuLink.Core/Entities/DeliveryAttempt.cs ProcuLink.Core/Constants/OrderStatusConstants.cs ProcuLink.Core/Constants/OrderStatusMachine.cs ProcuLink.Infrastructure/Services/OrderStatusTransitionObserver.cs ProcuLink.Infrastructure.Tests/Services/OrderStatusTransitionObserverTests.cs
git commit -m "feat(delivery): add delivery_unconfirmed order status and unconfirmed attempt status

Registered in BOTH transition maps (machine + observer) with a test pinning
both, per the d4d6eac drift lesson.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: Park instead of re-sending (the core change)

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/DeliveryService.cs` (dispatch path ~line 306-348; `OpenDispatchAttemptAsync` ~line 358-400)
- Test: `ProcuLink.Infrastructure.Tests/Services/DeliveryServiceUnconfirmedParkTests.cs` (create)

**Interfaces:**
- Consumes: `ResendSafety` + `IDeliveryDispatcher.ResendSafety` (Task 1); `DeliveryAttempt.StatusUnconfirmed`, `OrderStatusConstants.DeliveryUnconfirmed` (Task 2).
- Produces: `OpenDispatchAttemptAsync` now returns `(DeliveryAttempt Attempt, bool ReAdopted)`. Private — no other task consumes it.

- [ ] **Step 1: Write the failing tests**

Create `ProcuLink.Infrastructure.Tests/Services/DeliveryServiceUnconfirmedParkTests.cs`. Copy the private helpers (`CreateDb`, `CreateEncryption`, `SeedOrderAsync`, `CreateService`, `MakeConfig`, `NoOpIntegrationTriggerService`, `FakeFileStorage`, `FakeAnalyticsService`) from `DeliveryServiceIdempotencyTests.cs` — that file is the direct sibling and its helpers are the established shape. `MakeConfig` there hardcodes `Protocol = "http"`; parameterise your copy so a test can pass `"erp_erply"`.

```csharp
/// <summary>
/// A3 follow-up — the unknown-outcome park. The A3 idempotency key de-duplicates a
/// crash-recovery re-send for SFTP/FTPS (deterministic overwrite) and HTTP (Idempotency-Key,
/// if honoured), but NOT for ERP (no dedupe signal reaches the endpoint) or email
/// (caller-supplied Message-ID dedup is best-effort). On those channels a re-drive of a send
/// whose outcome we never learned is parked for a human instead of blindly repeated.
/// </summary>
public class DeliveryServiceUnconfirmedParkTests
{
    private const int MaxAttempts = 3;

    // The whole point: an Unsafe channel whose in-flight row is re-adopted must NOT be re-sent.
    [Fact]
    public async Task ReAdopt_OnUnsafeChannel_DoesNotReSend_AndParksOrderUnconfirmed()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "erp_erply"));

        // The exact post-crash state: order still 'delivering', an in-flight 'dispatching' row
        // committed before the (unobserved) send, never finalised.
        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = ids.OrderId,
            OrgId = ids.OrgId,
            Channel = "erp_erply",
            Destination = "https://erp.example/orders",
            Status = DeliveryAttempt.StatusDispatching,
            AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30),
            IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 200), "erp_erply", ResendSafety.Unsafe);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        dispatcher.Calls.Should().Be(0, "an unknown outcome on a channel that cannot de-duplicate must never be blindly re-sent");
        result.Success.Should().BeFalse();

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId);
        order.Status.Should().Be(OrderStatusConstants.DeliveryUnconfirmed);

        var attempts = await db.DeliveryAttempts.Where(a => a.OrderId == ids.OrderId).ToListAsync();
        attempts.Should().ContainSingle("the in-flight row is finalised in place, never duplicated");
        attempts[0].Status.Should().Be(DeliveryAttempt.StatusUnconfirmed);
        attempts[0].IdempotencyKey.Should().Be(key);
    }

    // Regression guard: today's behaviour on channels that CAN de-duplicate must not change.
    [Theory]
    [InlineData(ResendSafety.Safe)]
    [InlineData(ResendSafety.BestEffort)]
    public async Task ReAdopt_OnSafeOrBestEffortChannel_StillReSends(ResendSafety tier)
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "http"));

        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = ids.OrderId,
            OrgId = ids.OrgId,
            Channel = "http",
            Destination = "https://supplier.example/orders",
            Status = DeliveryAttempt.StatusDispatching,
            AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30),
            IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 202), "http", tier);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        dispatcher.Calls.Should().Be(1, "an idempotent-or-best-effort channel re-drives exactly as before");
        result.Success.Should().BeTrue();
        (await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
    }

    // The common path must not park: only a RE-ADOPTED row means "we already sent this".
    [Fact]
    public async Task FirstSend_OnUnsafeChannel_DeliversNormally()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryFailed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "email"));
        await db.SaveChangesAsync();

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 200), "email", ResendSafety.Unsafe);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        dispatcher.Calls.Should().Be(1, "a first send on an unsafe channel is a normal delivery, not a park");
        result.Success.Should().BeTrue();
        (await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);
    }

    // A parked order is not billable: the meter counts only delivered + rejected_by_supplier.
    [Fact]
    public async Task ParkedOrder_IsNotBillable()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "erp_directo"));
        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
            Channel = "erp_directo", Destination = "https://directo.example",
            Status = DeliveryAttempt.StatusDispatching, AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30), IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new CountingDispatcher(new DeliveryResult(true, null, 200), "erp_directo", ResendSafety.Unsafe), encryption);
        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        // Mirrors StripeBillingService.ApplyMeterStatusFilter's billable set exactly.
        var billable = await db.PurchaseOrders.CountAsync(o =>
            o.OrgId == ids.OrgId &&
            (o.Status == OrderStatusConstants.Delivered || o.Status == OrderStatusConstants.RejectedBySupplier));

        billable.Should().Be(0, "we never charge for a delivery we cannot confirm");
    }

    private sealed class CountingDispatcher : IDeliveryDispatcher
    {
        private readonly DeliveryResult _result;
        public int Calls { get; private set; }
        public string Protocol { get; }
        public ResendSafety ResendSafety { get; }

        public CountingDispatcher(DeliveryResult result, string protocol, ResendSafety resendSafety)
        {
            _result = result;
            Protocol = protocol;
            ResendSafety = resendSafety;
        }

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType,
            SupplierDeliveryConfig config, string decryptedCredentials,
            CancellationToken ct, string? idempotencyKey = null)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter DeliveryServiceUnconfirmedParkTests`
Expected: `ReAdopt_OnUnsafeChannel_DoesNotReSend_AndParksOrderUnconfirmed` FAILS with `Expected dispatcher.Calls to be 0, but found 1` — **this is the bug, reproduced**. `ParkedOrder_IsNotBillable` fails with `Expected billable to be 0, but found 1`. The other two should already PASS (they pin today's correct behaviour).

- [ ] **Step 3: Make OpenDispatchAttemptAsync report re-adoption**

In `ProcuLink.Infrastructure/Services/DeliveryService.cs`, change the signature and both return sites:

```csharp
    private async Task<(DeliveryAttempt Attempt, bool ReAdopted)> OpenDispatchAttemptAsync(
        PurchaseOrderEntity order,
        OutboundArtifact artifact,
        SupplierDeliveryConfig config,
        string idempotencyKey,
        CancellationToken ct)
    {
        var existing = await _db.DeliveryAttempts
            .Where(a => a.OrderId == order.Id && a.OrgId == order.OrgId
                     && a.Status == DeliveryAttempt.StatusDispatching
                     && a.IdempotencyKey == idempotencyKey)
            .OrderByDescending(a => a.AttemptedAt)
            .FirstOrDefaultAsync(ct);

        // Re-adopting an in-flight row IS the "we already sent this artifact, and never learned
        // the outcome" signal — the row was committed before the send, so its survival means the
        // send was attempted and the process died before finalising it.
        if (existing is not null)
            return (existing, true);
```

…and the fresh-insert path returns `(attempt, false)`.

- [ ] **Step 4: Park in the dispatch path**

In the dispatch path, replace the `var attempt = await OpenDispatchAttemptAsync(...);` line with:

```csharp
        var idempotencyKey = BuildIdempotencyKey(order.Id, artifact.Id);
        var (attempt, reAdopted) = await OpenDispatchAttemptAsync(order, artifact, config, idempotencyKey, ct);

        // ── The unknown-outcome park ──────────────────────────────────────────────
        // A re-adopted in-flight row means the previous activation SENT this artifact and died
        // before learning whether the supplier accepted it. On a channel that cannot de-duplicate
        // (ERP: no dedupe signal reaches the endpoint; email: caller-supplied Message-ID dedup is
        // best-effort), re-sending would hand the supplier a duplicate PO. We cannot know whether
        // the ACK happened — the crash destroyed the only transaction that could have recorded it —
        // so we do not guess: park the order and let a human decide. Safe/BestEffort channels
        // re-drive unchanged.
        if (reAdopted && dispatcher.ResendSafety == ResendSafety.Unsafe)
            return await ParkUnconfirmedAsync(order, attempt, config, ct);
```

- [ ] **Step 5: Implement ParkUnconfirmedAsync**

Add the method to `DeliveryService` (place it directly after `OpenDispatchAttemptAsync`). The audit-event shape mirrors `HoldForBillingAsync` (~line 989) exactly:

```csharp
    /// <summary>
    /// The unknown-outcome park: finalise the re-adopted in-flight row as
    /// <c>unconfirmed</c> and stop. NO send occurs, and NO retry is scheduled — the order waits
    /// for an operator to either send it again or confirm the supplier received it.
    /// <para>
    /// The SLA timer is deliberately left running: a parked order SHOULD nag until a human
    /// resolves it, so <c>DeliveryDueAt</c>/<c>SlaBreached</c> are untouched here.
    /// </para>
    /// </summary>
    private async Task<DeliveryResult> ParkUnconfirmedAsync(
        PurchaseOrderEntity order,
        DeliveryAttempt attempt,
        SupplierDeliveryConfig config,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var message = BuildUnconfirmedMessage(config.Protocol);

        attempt.Status = DeliveryAttempt.StatusUnconfirmed;
        attempt.AttemptedAt = now;
        attempt.ErrorMessage = message;

        order.Status = OrderStatusConstants.DeliveryUnconfirmed;
        order.UpdatedAt = now;

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            orderId = order.Id,
            fromStatus = OrderStatusConstants.Delivering,
            toStatus = OrderStatusConstants.DeliveryUnconfirmed,
            channel = config.Protocol,
            idempotencyKey = attempt.IdempotencyKey,
            parkedAt = now,
            detail = "Crash-recovery re-drive on a channel that cannot de-duplicate a re-send. "
                   + "The artifact was sent but the outcome was never observed; re-sending could "
                   + "duplicate the PO, so the order is parked for an operator decision.",
        });

        _db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OrgId = order.OrgId,
            UserId = null,
            EntityType = "Order",
            EntityId = order.Id,
            Action = "DeliveryUnconfirmed",
            Payload = System.Text.Json.JsonDocument.Parse(payload),
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "DeliveryUnconfirmed: order {OrderId} (org {OrgId}) parked — a crash-recovery re-drive "
            + "re-adopted an in-flight send on {Protocol}, which cannot de-duplicate a re-send. "
            + "NOT re-sent; waiting for an operator to send again or mark delivered.",
            order.Id, order.OrgId, config.Protocol);

        return new DeliveryResult(false, message);
    }

    /// <summary>
    /// The operator-facing park sentence. Plain language, one sentence of what happened plus what
    /// to do — never internal vocabulary (no "idempotency", "re-adopt", "dispatching row").
    /// </summary>
    internal static string BuildUnconfirmedMessage(string protocol) =>
        $"Delivery unconfirmed. We sent this order but lost the connection before the supplier "
        + $"confirmed it, and {DescribeChannel(protocol)} cannot tell us whether it arrived. "
        + $"Check with the supplier, then either send it again or mark it delivered.";

    private static string DescribeChannel(string protocol) => protocol?.ToLowerInvariant() switch
    {
        "email" or "smtp" => "email",
        "erp_erply" => "the Erply connection",
        "erp_directo" => "the Directo connection",
        _ => "this delivery channel",
    };
```

**Note for the implementer:** `AuditEvent` may need `Core.Entities.` qualification — match whichever form the surrounding code in this file already uses (line ~989 uses the bare name, line ~1089 uses the qualified one).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter DeliveryServiceUnconfirmedParkTests`
Expected: PASS (5 tests).

- [ ] **Step 7: Run the whole delivery suite for regressions**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter Delivery`
Expected: all PASS. The A3 tests in `DeliveryServiceIdempotencyTests` matter most — its `CapturingDispatcher` has `Protocol => "http"` and does not declare a tier, so it inherits the `Unsafe` default. **If `CrashAfterSendBeforeCommit_ReDrive_ReAdoptsInFlightRow_SameKey_NoSecondDelivery` now fails**, that is this change working as designed: the test asserts a re-adopted row re-sends, which is now the park path. Fix it by giving that test double `public ResendSafety ResendSafety => ResendSafety.BestEffort;` (it is modelling an HTTP supplier) — do NOT weaken the park.

- [ ] **Step 8: Commit**

```bash
git add ProcuLink.Infrastructure/Services/DeliveryService.cs ProcuLink.Infrastructure.Tests/Services/
git commit -m "fix(delivery): park unknown-outcome re-drives instead of duplicating the PO

A crash between the supplier ACK and the terminal commit leaves the order
'delivering' with a surviving in-flight 'dispatching' row. The stuck sweep
re-adopts it and re-sends. On ERP and email no dedupe signal reaches the
counterparty, so that re-send is a duplicate PO.

The re-adopted row IS the 'sent, outcome unknown' signal. On an Unsafe channel
we now stop: finalise the row 'unconfirmed', park the order in
delivery_unconfirmed, audit it, schedule no retry. A human sends again or marks
it delivered. SFTP/FTPS and HTTP re-drive unchanged.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3B: A parked result must never enter the retry queue

**Files:**
- Modify: `ProcuLink.Core/Services/Delivery/DeliveryResult.cs`
- Modify: `ProcuLink.Infrastructure/Services/DeliveryService.cs` (`ParkUnconfirmedAsync` return)
- Modify: `ProcuLink.Api/Jobs/DeliverOrderJob.cs` (~line 99-118)
- Modify: `ProcuLink.Infrastructure/Jobs/RetryDeliveryJob.cs` (~line 74-100)
- Test: `ProcuLink.Infrastructure.Tests/Jobs/RetryDeliveryJobBackoffTests.cs` (existing — add cases)

**Interfaces:**
- Consumes: `ParkUnconfirmedAsync` (Task 3).
- Produces: `DeliveryResult.Parked` (bool, defaults false).

**Why this task exists — read this before starting.** Task 3 alone does NOT work. Both jobs decide whether to schedule an automatic backoff retry by branching on `result.ResponseCode`: they bail out only for a 4xx supplier rejection. A parked result is `Success=false` with a **null** `ResponseCode`, so it falls straight through to `ScheduleRetry` — the retry loop would re-drive the parked order and re-send the exact PO the park exists to protect. Without this task the feature is decorative: it renames the state and duplicates the order anyway.

- [ ] **Step 1: Write the failing tests**

Add to `ProcuLink.Infrastructure.Tests/Jobs/RetryDeliveryJobBackoffTests.cs` (mirror the file's existing `Mock<IBackgroundJobClient>` arrange — it already asserts scheduling behaviour):

```csharp
    // The park's whole purpose is that a HUMAN decides. An automatic retry would re-send the
    // exact PO we refused to re-send, so a parked result must leave the backoff queue untouched.
    [Fact]
    public async Task ParkedResult_SchedulesNoRetry()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        // Arrange the job with a delivery service returning a parked result:
        //   new DeliveryResult(false, "Delivery unconfirmed…", ResponseCode: null, Parked: true)

        await job.ExecuteAsync(orderId, orgId, ct);

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never(),
            "a parked delivery waits for an operator, never for the backoff queue");
    }

    // Regression guard: an ordinary transient failure must STILL be retried.
    [Fact]
    public async Task OrdinaryTransientFailure_StillSchedulesRetry()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        // delivery service returns: new DeliveryResult(false, "connection reset", ResponseCode: null)
        //   → Parked defaults to false

        await job.ExecuteAsync(orderId, orgId, ct);

        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once(),
            "a normal transient failure still enters the backoff queue — the park must not break retries");
    }
```

**Note for the implementer:** the `jobs.Verify(...)` signature above is indicative — match whatever assertion this file already uses to detect a scheduled job (Hangfire's `IBackgroundJobClient.Create` is what `ScheduleRetry` ultimately calls, but the existing tests may verify at a different seam). Read a neighbouring test first and mirror it. Add the equivalent pair for `DeliverOrderJob` in `ProcuLink.Api.Tests/Jobs/` if a suitable home exists there; if not, the `RetryDeliveryJob` pair plus the guard in both jobs is acceptable — say so in your report rather than silently skipping it.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter RetryDeliveryJobBackoffTests`
Expected: `ParkedResult_SchedulesNoRetry` FAILS — it will not compile first (`Parked` does not exist); once the property exists it fails with the retry scheduled `Times.Once` instead of `Never`. **That failure is the bug this task fixes.** `OrdinaryTransientFailure_StillSchedulesRetry` should pass throughout.

- [ ] **Step 3: Add the flag to DeliveryResult**

`ProcuLink.Core/Services/Delivery/DeliveryResult.cs` — append a fifth positional parameter (defaulted, so all existing call sites compile unchanged):

```csharp
/// <param name="Parked">
/// True when the delivery was deliberately NOT sent because its outcome could not be known and the
/// channel cannot de-duplicate a re-send (the unknown-outcome park — see
/// <c>DeliveryService.ParkUnconfirmedAsync</c>). The order is waiting on an operator decision, so
/// callers MUST NOT schedule an automatic retry: doing so would re-send the exact PO the park
/// exists to protect. Distinct from an ordinary failure, which stays retryable.
/// </param>
public record DeliveryResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode = null,
    string? ResponseBody = null,
    bool Parked = false);
```

- [ ] **Step 4: Return the flag from the park**

In `DeliveryService.ParkUnconfirmedAsync` (Task 3), change the return:

```csharp
        return new DeliveryResult(false, message, ResponseCode: null, ResponseBody: null, Parked: true);
```

- [ ] **Step 5: Guard DeliverOrderJob**

In `ProcuLink.Api/Jobs/DeliverOrderJob.cs`, immediately BEFORE the existing 4xx check:

```csharp
        // A parked delivery was deliberately not sent: its outcome is unknown and the channel
        // cannot de-duplicate, so an automatic retry would re-send the exact PO the park refused
        // to re-send. It waits for an operator ("Send again" / "Mark as delivered"), not the queue.
        if (result.Parked)
        {
            _logger.LogWarning(
                "DeliverOrderJob: order {OrderId} is parked (delivery unconfirmed); no automatic retry scheduled.",
                orderId);
            return;
        }

        // A 4xx is an explicit supplier rejection — retrying the same payload won't help, so it
        // is left for operator review (status 'rejected_by_supplier'). Only transient failures
        // (5xx / network, no 4xx code) enter the automatic backoff queue.
        if (result.ResponseCode is >= 400 and <= 499)
            return;
```

- [ ] **Step 6: Guard RetryDeliveryJob**

In `ProcuLink.Infrastructure/Jobs/RetryDeliveryJob.cs`, BEFORE the `IsSupplierRejection` check (and before the attempt-cap check, so a parked order is never dead-lettered by the queue either):

```csharp
        if (result.Parked)
        {
            // Parked: the send's outcome is unknown and this channel cannot de-duplicate. Neither
            // retry NOR dead-letter it — an operator decides. Returning here also keeps the order
            // in 'delivery_unconfirmed' rather than letting the cap escalate it to dead-letter.
            _logger.LogWarning(
                "RetryDeliveryJob: order {OrderId} is parked (delivery unconfirmed); not rescheduling.",
                orderId);
            return;
        }
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter RetryDeliveryJobBackoffTests`
Expected: PASS.

- [ ] **Step 8: Re-run the park tests to confirm the whole path holds**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter "DeliveryServiceUnconfirmedParkTests|RetryDeliveryJobBackoffTests"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add ProcuLink.Core/Services/Delivery/DeliveryResult.cs ProcuLink.Infrastructure/Services/DeliveryService.cs ProcuLink.Api/Jobs/DeliverOrderJob.cs ProcuLink.Infrastructure/Jobs/RetryDeliveryJob.cs ProcuLink.Infrastructure.Tests/Jobs/RetryDeliveryJobBackoffTests.cs
git commit -m "fix(delivery): a parked delivery never enters the retry queue

Both jobs only skipped the backoff queue for a 4xx rejection. A parked result is
Success=false with no response code, so it fell through to ScheduleRetry — the
retry loop would have re-sent the exact PO the park refused to re-send, making
the park decorative. DeliveryResult.Parked now says so explicitly and both jobs
bail out before scheduling (and before the cap check, so a parked order is not
dead-lettered either).

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: "Send again" from a parked order

**Files:**
- Modify: `ProcuLink.Core/Constants/OrderStatusMachine.cs` (`RedeliverableFrom`, ~line 82)
- Modify: `ProcuLink.Api/Controllers/OrdersController.cs` (`Redeliver`, ~line 1767-1806)
- Test: `ProcuLink.Api.Tests/Controllers/OrdersControllerRedeliverTests.cs` (create if absent — search first; `DeliveriesControllerTests.cs` may be the right home)

**Interfaces:**
- Consumes: `OrderStatusConstants.DeliveryUnconfirmed` (Task 2).

**Why the controller changes:** `Redeliver` hardcodes its 400 message as `"Order must be in 'delivery_failed' or 'ready_to_deliver' status to redeliver"`. Adding a third status to `RedeliverableFrom` makes that sentence a lie. Derive it from the set.

- [ ] **Step 1: Write the failing test**

```csharp
    // The operator's "Send again" on a parked order — the whole point of the park is that a
    // HUMAN, not the retry loop, decides to accept the duplicate risk.
    [Fact]
    public async Task Redeliver_FromDeliveryUnconfirmed_IsAccepted()
    {
        // Arrange an order in delivery_unconfirmed with an outbound artifact (follow this file's
        // existing arrange helpers for the tenant/org context and DbContext seeding).
        var response = await Redeliver(orderId);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        (await Db.PurchaseOrders.SingleAsync(o => o.Id == orderId)).Status
            .Should().Be(OrderStatusConstants.Delivering);
    }

    // The 400 message must name the statuses that are ACTUALLY redeliverable, not a stale literal.
    [Fact]
    public async Task Redeliver_FromInvalidStatus_ErrorMessage_ListsEveryRedeliverableStatus()
    {
        var response = await Redeliver(orderIdInStatus: OrderStatusConstants.Parsing);

        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var error = await ReadErrorAsync(response);
        foreach (var status in OrderStatusMachine.RedeliverableFrom)
            error.Should().Contain(status, "the operator must be told every status they can redeliver from");
    }
```

**Note for the implementer:** adapt the arrange/act helpers to whatever this controller's existing tests use (WebApplicationFactory vs direct controller instantiation) — read a neighbouring test in the same file first and mirror it. Do not introduce a new test harness style.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter Redeliver`
Expected: FAIL — the first with a 400 (`delivery_unconfirmed` not in `RedeliverableFrom`), the second because the hardcoded message never mentions `delivery_unconfirmed`.

- [ ] **Step 3: Add the status to RedeliverableFrom**

In `ProcuLink.Core/Constants/OrderStatusMachine.cs`:

```csharp
    /// <summary>
    /// A manual "send again" (OrdersController.Redeliver) is valid only from a
    /// stalled-but-recoverable delivery state. (A dead-lettered order is rescued by
    /// the separate ops "requeue delivery" path, not by redeliver.)
    /// <para>
    /// delivery_unconfirmed is included: the park's entire purpose is to let a HUMAN choose to
    /// re-send, accepting the duplicate risk the automatic retry must not take on their behalf.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> RedeliverableFrom =
        Set(DeliveryFailed, ReadyToDeliver, DeliveryUnconfirmed);
```

- [ ] **Step 4: Derive the error message from the set**

In `ProcuLink.Api/Controllers/OrdersController.cs`, replace the hardcoded 400 body:

```csharp
        if (!ProcuLink.Core.Constants.OrderStatusMachine.RedeliverableFrom.Contains(order.Status))
            return BadRequest(new
            {
                // Derived from the set, never a literal: adding a redeliverable status must not
                // leave this sentence quietly lying about which statuses are valid.
                error = $"Order must be in one of these statuses to redeliver: "
                      + $"{string.Join(", ", ProcuLink.Core.Constants.OrderStatusMachine.RedeliverableFrom.OrderBy(s => s, StringComparer.Ordinal))} "
                      + $"(current: '{order.Status}')."
            });
```

Update the XML doc comment above the endpoint: `Valid source statuses: delivery_failed, ready_to_deliver, delivery_unconfirmed.`

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter Redeliver`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Core/Constants/OrderStatusMachine.cs ProcuLink.Api/Controllers/OrdersController.cs ProcuLink.Api.Tests/
git commit -m "feat(delivery): allow Send again from a parked (delivery_unconfirmed) order

Also derives the redeliver 400 message from RedeliverableFrom — the hardcoded
'delivery_failed or ready_to_deliver' literal would otherwise start lying the
moment the set grew.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: "Mark as delivered" endpoint

**Files:**
- Modify: `ProcuLink.Api/Controllers/OrdersController.cs` (add after `Redeliver`, ~line 1806)
- Test: same file as Task 4's tests

**Interfaces:**
- Consumes: `OrderStatusConstants.DeliveryUnconfirmed` (Task 2).
- Produces: `POST /api/orders/{id}/mark-delivered` → `202 Accepted` `{ status = "delivered" }`. The frontend plan consumes this.

- [ ] **Step 1: Write the failing tests**

```csharp
    // The operator confirms out-of-band (phone/portal) that the supplier DID receive it.
    [Fact]
    public async Task MarkDelivered_FromDeliveryUnconfirmed_SetsDelivered_AndClearsSla()
    {
        var response = await MarkDelivered(orderIdInStatus: OrderStatusConstants.DeliveryUnconfirmed);

        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var order = await Db.PurchaseOrders.SingleAsync(o => o.Id == orderId);
        order.Status.Should().Be(OrderStatusConstants.Delivered);
        order.DeliveryDueAt.Should().BeNull("a confirmed delivery closes the SLA window");
        order.SlaBreached.Should().BeFalse();
    }

    // We never fabricate a supplier ACK we did not observe.
    [Fact]
    public async Task MarkDelivered_LeavesAttemptRowUnconfirmed_AndAuditsTheHumanAssertion()
    {
        await MarkDelivered(orderIdInStatus: OrderStatusConstants.DeliveryUnconfirmed);

        var attempt = await Db.DeliveryAttempts.SingleAsync(a => a.OrderId == orderId);
        attempt.Status.Should().Be(DeliveryAttempt.StatusUnconfirmed,
            "the send's outcome was never observed — only the operator's assertion is new");

        var audit = await Db.AuditEvents.SingleAsync(e => e.EntityId == orderId && e.Action == "DeliveryConfirmedManually");
        audit.UserId.Should().NotBeNull("the human who asserted delivery is on the record");
    }

    [Fact]
    public async Task MarkDelivered_FromAnyOtherStatus_Is400()
    {
        var response = await MarkDelivered(orderIdInStatus: OrderStatusConstants.DeliveryFailed);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task MarkDelivered_ForAnotherOrgsOrder_Is404()
    {
        var response = await MarkDelivered(orderIdOwnedBy: OtherOrgId);
        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // Billing consequence: metering is a status query, so this is what makes the order chargeable.
    [Fact]
    public async Task MarkDelivered_MakesOrderBillable()
    {
        await MarkDelivered(orderIdInStatus: OrderStatusConstants.DeliveryUnconfirmed);

        // Mirrors StripeBillingService.ApplyMeterStatusFilter's billable set exactly.
        var billable = await Db.PurchaseOrders.CountAsync(o =>
            o.OrgId == OrgId &&
            (o.Status == OrderStatusConstants.Delivered || o.Status == OrderStatusConstants.RejectedBySupplier));

        billable.Should().Be(1);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter MarkDelivered`
Expected: FAIL — 404 from every test (the route does not exist).

- [ ] **Step 3: Implement the endpoint**

Add to `ProcuLink.Api/Controllers/OrdersController.cs` after `Redeliver`:

```csharp
    // ── POST /api/orders/{id}/mark-delivered ─────────────────────────────────

    /// <summary>
    /// Operator confirmation that a parked (<c>delivery_unconfirmed</c>) order DID reach the
    /// supplier — established out-of-band, e.g. the supplier confirmed by phone or the order is
    /// visible in their portal. Closes the order truthfully without re-sending it.
    /// <para>
    /// The delivery ATTEMPT row stays <c>unconfirmed</c>: its outcome was never observed and this
    /// endpoint does not change that. What is new is the operator's assertion, recorded as its own
    /// audit event with the acting user. Only valid from <c>delivery_unconfirmed</c>.
    /// </para>
    /// <para>
    /// Billing: metering counts <c>delivered</c>/<c>rejected_by_supplier</c> by query, so this is
    /// the point at which the order becomes chargeable — correct, since the operator has just
    /// confirmed the supplier received it.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/mark-delivered")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkDelivered(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        var getResult = await _orders.GetByIdAsync(orgId, id, ct);

        if (!getResult.IsSuccess)
            return NotFound();

        var order = getResult.Value!;

        if (order.Status != OrderStatusConstants.DeliveryUnconfirmed)
            return BadRequest(new
            {
                error = $"Only an order whose delivery is unconfirmed can be marked delivered "
                      + $"(current: '{order.Status}')."
            });

        var now = DateTime.UtcNow;
        order.Status = OrderStatusConstants.Delivered;
        order.UpdatedAt = now;
        // A confirmed delivery closes the SLA window — mirrors DeliveryService's success path.
        order.DeliveryDueAt = null;
        order.SlaBreached = false;

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            orderId = id,
            fromStatus = OrderStatusConstants.DeliveryUnconfirmed,
            toStatus = OrderStatusConstants.Delivered,
            confirmedAt = now,
            detail = "Operator confirmed out-of-band that the supplier received this order. "
                   + "The delivery attempt itself was never acknowledged; this records the "
                   + "operator's assertion, not an observed supplier ACK.",
        });

        _db.AuditEvents.Add(new ProcuLink.Core.Entities.AuditEvent
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            UserId = _tenant.UserId,
            EntityType = "Order",
            EntityId = id,
            Action = "DeliveryConfirmedManually",
            Payload = System.Text.Json.JsonDocument.Parse(payload),
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "DeliveryConfirmedManually: order {OrderId} (org {OrgId}) marked delivered by operator {UserId} "
            + "after an unconfirmed delivery.",
            id, orgId, _tenant.UserId);

        return Accepted(new { status = "delivered" });
    }
```

**Note for the implementer:** confirm the tenant accessor's user property name (`_tenant.UserId` vs similar) by reading how another endpoint in this controller stamps `UserId` on an audit event. If no such endpoint exists, check `ITenantContext`/`_tenant`'s type definition. Do not guess.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter MarkDelivered`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Api/Controllers/OrdersController.cs ProcuLink.Api.Tests/
git commit -m "feat(delivery): add mark-delivered for a parked order

Lets an operator close a delivery_unconfirmed order truthfully once the supplier
confirms receipt out-of-band. The attempt row stays 'unconfirmed' — we never
fabricate a supplier ACK we did not observe; the human's assertion is audited
separately with the acting user.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5B: The park sentence must actually reach the operator

**Files:**
- Modify: `ProcuLink.Api/Controllers/OrdersController.cs` (`Get`, ~line 346 and ~line 375)
- Test: `ProcuLink.Api.Tests/Controllers/` — the existing test file covering `GET /api/orders/{id}`'s `errorMessage` (search for a test asserting `errorMessage` for `delivery_failed`; add alongside it)

**Interfaces:**
- Consumes: `OrderStatusConstants.DeliveryUnconfirmed` (Task 2); the park sentence written to `attempt.ErrorMessage` by `ParkUnconfirmedAsync` (Task 3).

**Why:** `GET /api/orders/{id}` only populates `errorMessage` when the order's status is in a hardcoded literal list — `failed`, `transform_failed`, `delivery_failed`, `rejected_by_supplier`, `delivery_dead_letter`. A parked order is none of those, so the whole block is skipped and the API returns `errorMessage: null`. The operator would see an order sitting in "Delivery unconfirmed" with **no explanation of what happened or what to do** — the exact plain-language sentence this feature is built around would never leave the database.

- [ ] **Step 1: Write the failing test**

```csharp
    // A parked order MUST explain itself. Without this the operator sees a status they've never
    // seen before and no sentence telling them what happened or which action to take.
    [Fact]
    public async Task Get_ParkedOrder_ReturnsTheUnconfirmedExplanation()
    {
        // Arrange: an order in delivery_unconfirmed whose latest DeliveryAttempt is
        // StatusUnconfirmed with ErrorMessage = the park sentence (as ParkUnconfirmedAsync writes it).

        var dto = await GetOrder(orderId);

        dto.ErrorMessage.Should().NotBeNull("a parked order must tell the operator what happened");
        dto.ErrorMessage.Should().Contain("unconfirmed");
        dto.ErrorMessage.Should().Contain("mark it delivered",
            "the sentence must name the action available to them");
    }
```

**Note for the implementer:** mirror the arrange/act of the neighbouring `errorMessage` test for `delivery_failed` in the same file. Do not assert the full sentence verbatim — assert the substrings above, so rewording the copy doesn't break the test for no reason.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter Get_ParkedOrder`
Expected: FAIL — `Expected dto.ErrorMessage not to be <null>`.

- [ ] **Step 3: Add the status to both gates**

In `ProcuLink.Api/Controllers/OrdersController.cs`, the outer gate (~line 346):

```csharp
        // delivery_unconfirmed included: a parked order must surface its explanation ("we sent this
        // but never learned whether it arrived — send again or mark delivered"), or the operator
        // gets a status with no sentence and no guidance.
        if (entity.Status is "failed" or "transform_failed" or "delivery_failed"
                          or "rejected_by_supplier" or "delivery_dead_letter"
                          or OrderStatusConstants.DeliveryUnconfirmed)
```

And the attempt-message fallback (~line 375) — this is the branch that actually carries the park sentence, since `ParkUnconfirmedAsync` writes it to `attempt.ErrorMessage` (the `DeliveryUnconfirmed` audit payload uses `detail`, not the `error`/`lastError` keys the block above looks for):

```csharp
            if (errorMessage is null && entity.Status is "delivery_failed" or "delivery_dead_letter"
                                                      or OrderStatusConstants.DeliveryUnconfirmed)
```

**Note for the implementer:** the surrounding code uses bare string literals rather than `OrderStatusConstants`. Use the constant for the new value regardless — do not propagate the literal habit. Leave the existing literals alone; rewriting them is out of scope for this task.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter Get_ParkedOrder`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Api/Controllers/OrdersController.cs ProcuLink.Api.Tests/
git commit -m "fix(delivery): surface the park explanation on GET /api/orders/{id}

The errorMessage block is gated on a hardcoded status list, so a parked order
returned errorMessage: null — the operator would see an unfamiliar status with
no sentence and no guidance.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: A mapping edit invalidates a parked order's artifact (MV-1 sibling)

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/OrderMappingOverrideService.cs` (`IsPastReady`, ~line 104-109)
- Test: `ProcuLink.Infrastructure.Tests/Services/OrderMappingOverrideServiceTests.cs` (existing — add an InlineData row, ~line 216-225)

**Interfaces:**
- Consumes: `OrderStatusConstants.DeliveryUnconfirmed` (Task 2).

**Why:** a parked order is redeliverable (Task 4), and redeliver ships the LATEST STORED artifact without re-transforming. So a mapping edit after a park must reset the order to `ready` — otherwise "Send again" invisibly ships pre-edit content. This is exactly the MV-1 sibling bug already documented for `delivery_failed` / `delivery_dead_letter` in that method's comment.

- [ ] **Step 1: Write the failing test**

In `OrderMappingOverrideServiceTests.cs`, add to the existing `UpsertAsync_ChangedOverride_PastReady_ResetsStatusToReady` theory:

```csharp
    // A parked order is redeliverable and Send again ships the STORED artifact without
    // re-transforming, so a mapping edit must invalidate it — the same MV-1 sibling reasoning
    // that covers delivery_failed and delivery_dead_letter.
    [InlineData(ProcuLink.Core.Constants.OrderStatusConstants.DeliveryUnconfirmed)]
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter OrderMappingOverrideServiceTests`
Expected: FAIL — the new row asserts status becomes `ready` but it stays `delivery_unconfirmed`.

- [ ] **Step 3: Add the status to IsPastReady**

In `ProcuLink.Infrastructure/Services/OrderMappingOverrideService.cs`:

```csharp
    private static bool IsPastReady(string status) =>
        status is OrderStatusConstants.ReadyToDeliver
               or OrderStatusConstants.Transforming
               or OrderStatusConstants.Delivered
               or OrderStatusConstants.DeliveryFailed
               or OrderStatusConstants.DeliveryDeadLetter
               // A parked order is redeliverable and Send again ships the stored artifact
               // without re-transforming — a mapping edit must invalidate it too.
               or OrderStatusConstants.DeliveryUnconfirmed;
```

Extend that method's XML doc comment to name `delivery_unconfirmed` alongside the two delivery-failure states.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter OrderMappingOverrideServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Infrastructure/Services/OrderMappingOverrideService.cs ProcuLink.Infrastructure.Tests/Services/OrderMappingOverrideServiceTests.cs
git commit -m "fix(delivery): mapping edit invalidates a parked order's stored artifact

MV-1 sibling: delivery_unconfirmed is redeliverable and Send again ships the
stored artifact without re-transforming, so an edit must reset it to ready.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 7: Real-Postgres proof of the park

**Files:**
- Modify: `ProcuLink.Api.Tests/Integration/DeliveryCrashRecoveryPostgresTests.cs` (existing — its `CapturingKeyDispatcher` is at ~line 212)

**Interfaces:**
- Consumes: everything from Tasks 1–3.

**Why a separate task:** the InMemory provider masks Postgres behaviour (a known trap in this codebase). The A3 crash-recovery path already has a real-Postgres test; the park needs the same treatment or we are only proving it against a fake.

- [ ] **Step 1: Write the failing test**

Add to `DeliveryCrashRecoveryPostgresTests.cs`, mirroring the existing stale-`delivering` reclaim test's arrange exactly but with an ERP delivery config:

```csharp
    // The A3 sibling on a channel that CANNOT de-duplicate: the same stale-'delivering' reclaim
    // must NOT re-send — it must park. Proven on real Postgres because the InMemory provider
    // masks the reclaim's concurrency/staleness semantics.
    [SkippableFact]
    public async Task StaleDelivering_OnErpChannel_IsParkedUnconfirmed_NotReSent()
    {
        // Arrange: order 'delivering', stale UpdatedAt (past the reclaim window), an unfinalised
        // 'dispatching' attempt row carrying the deterministic key, and an erp_erply config.
        // Follow this file's existing arrange helpers and its Docker/Postgres skip guard.

        var dispatcher = new CapturingKeyDispatcher(new DeliveryResult(true, null, 200))
        {
            // erp_erply: no dedupe signal reaches the endpoint.
        };

        // Act: the stuck sweep re-drives.

        // Assert
        dispatcher.Calls.Should().Be(0, "an ERP re-drive after an unknown outcome would duplicate the PO");
        order.Status.Should().Be(OrderStatusConstants.DeliveryUnconfirmed);
        attempts.Should().ContainSingle();
        attempts[0].Status.Should().Be(DeliveryAttempt.StatusUnconfirmed);
    }
```

**Note for the implementer:** `CapturingKeyDispatcher` currently hardcodes its protocol and inherits the `Unsafe` default. Give it a settable `Protocol` and `ResendSafety` so the existing HTTP test can keep asserting a re-send (`BestEffort`) while the new test asserts a park (`Unsafe`) — do not fork a second dispatcher double. Keep the file's existing Docker skip guard so the suite still runs where Postgres is unavailable.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter DeliveryCrashRecoveryPostgresTests`
Expected: FAIL with `Expected dispatcher.Calls to be 0, but found 1` (skipped entirely if Docker/Postgres is unavailable — if it skips, say so plainly rather than reporting a pass).

- [ ] **Step 3: Confirm it passes against the Task 3 implementation**

Run: `dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj --filter DeliveryCrashRecoveryPostgresTests`
Expected: PASS. No production code should be needed — Task 3 already implements this. If it does not pass, the bug is real; do not weaken the test.

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Api.Tests/Integration/DeliveryCrashRecoveryPostgresTests.cs
git commit -m "test(delivery): real-Postgres proof that an ERP re-drive parks instead of re-sending

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 8: Honest documentation of the residual (offer ⇔ works)

**Files:**
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/ErpDeliveryDispatchers.cs` (the A3 comment, ~line 45-49)
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/EmailApiDeliveryDispatcher.cs` (the A3 comment, ~line 91-93)

**Interfaces:** none — comments only.

**Why:** both dispatchers currently carry a comment documenting the duplicate risk as a live, unmitigated limitation. After Task 3 that is no longer true, and a stale honest-comment is still a wrong comment.

- [ ] **Step 1: Update the ERP comment**

In `ErpDeliveryDispatchers.cs`, replace the existing A3 comment inside `DispatchAsync`:

```csharp
        // A3 idempotency: the ERP connector contract accepts no idempotency key, and both
        // connectors are generic HTTP posts to a tenant-configured URL — there is no ERP document
        // model or lookup API to dedupe against. So idempotencyKey is intentionally unused here.
        //
        // The duplicate-order risk this used to carry is now handled upstream rather than ignored:
        // this dispatcher declares ResendSafety.Unsafe, so DeliveryService PARKS a crash-recovery
        // re-drive (delivery_unconfirmed) for an operator decision instead of blindly re-sending.
        // A duplicate is still possible if the operator chooses "Send again" — that is their
        // informed call, not something the system does behind their back.
```

- [ ] **Step 2: Update the email comment**

In `EmailApiDeliveryDispatcher.cs`, replace the existing A3 comment:

```csharp
        // A3 idempotency: a deterministic Message-ID (stable across a re-send of the same artifact)
        // lets a receiving MTA de-duplicate. Best-effort only — MTA dedup on a caller-supplied
        // Message-ID is rarely applied, which is why this dispatcher declares ResendSafety.Unsafe
        // and DeliveryService parks an unknown-outcome re-drive rather than risking a duplicate email.
```

- [ ] **Step 3: Verify the build**

Run: `dotnet build ProcuLink.slnx`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Infrastructure/Services/Dispatchers/
git commit -m "docs(delivery): ERP/email comments reflect the park, not an unmitigated duplicate risk

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 8B: Ops health counts parked orders

**Files:**
- Modify: `ProcuLink.Core/Services/IOpsHealthService.cs` (`OpsHealthSummary`, ~line 55-84)
- Modify: `ProcuLink.Infrastructure/Services/OpsHealthService.cs` (`GetHealthAsync`, ~line 32-70)
- Test: `ProcuLink.Infrastructure.Tests/Services/` — the existing `OpsHealthService` test file (search for it; add alongside the dead-letter count test)

**Interfaces:**
- Consumes: `OrderStatusConstants.DeliveryUnconfirmed` (Task 2).
- Produces: `OpsHealthSummary.DeliveryUnconfirmed` (int) — the **frontend plan's Task 4 is blocked on this**.

**Why:** the Health page renders a green "All clear" banner from these counts. A parked order is invisible to every one of them, so the page would tell an operator everything is fine while a PO sits unsent waiting on *them*. That banner is the surface most likely to be trusted at a glance, so a false green there is worse than a missing tile.

**The classification call:** `DeliveryUnconfirmed` goes into `TotalProblemOrders`, not the informational bucket. `PendingReview` and `PendingRouting` are excluded from it because they are *normal workflow backlogs* — every order passes through review. A parked order is not workflow: it is a crash whose PO may never have reached the supplier. It is a fault that happens to need a human to resolve.

- [ ] **Step 1: Write the failing test**

Mirror the existing dead-letter count test in the `OpsHealthService` test file:

```csharp
    // The Health page's "All clear" banner is computed from these counts. A parked order is a PO
    // sitting unsent, waiting on a human — the one thing that banner must never hide.
    [Fact]
    public async Task GetHealthAsync_CountsParkedOrders_AndTreatsThemAsProblems()
    {
        await using var db = CreateDb();
        var orgId = Guid.NewGuid();
        SeedOrder(db, orgId, OrderStatusConstants.DeliveryUnconfirmed);
        await db.SaveChangesAsync();

        var health = await new OpsHealthService(db /* + whatever the ctor needs */)
            .GetHealthAsync(orgId, default);

        health.DeliveryUnconfirmed.Should().Be(1);
        health.TotalProblemOrders.Should().BeGreaterThan(0,
            "an unsent PO waiting on a human is a problem, not a normal review backlog");
    }

    // Org-scoping, like every other count on this service.
    [Fact]
    public async Task GetHealthAsync_ParkedCount_IsOrgScoped()
    {
        await using var db = CreateDb();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        SeedOrder(db, theirs, OrderStatusConstants.DeliveryUnconfirmed);
        await db.SaveChangesAsync();

        var health = await new OpsHealthService(db).GetHealthAsync(mine, default);

        health.DeliveryUnconfirmed.Should().Be(0);
    }
```

**Note for the implementer:** adapt `CreateDb`/`SeedOrder`/the constructor to the existing test file's helpers — read a neighbouring count test first and mirror it exactly.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter OpsHealth`
Expected: FAIL to COMPILE — `'OpsHealthSummary' does not contain a definition for 'DeliveryUnconfirmed'`.

- [ ] **Step 3: Add the count to the record**

`ProcuLink.Core/Services/IOpsHealthService.cs` — add as a **defaulted** parameter at the END of the parameter list (after `PendingRouting`). C# requires defaulted parameters last, and every existing construction site is positional — inserting it next to `DeliveryFailed` where it reads more naturally would break them all:

```csharp
    int       PendingRouting            = 0,
    // Orders whose delivery outcome is unknown after a crash on a channel that cannot
    // de-duplicate a re-send. Counted as a PROBLEM, not an informational backlog: unlike
    // PendingReview/PendingRouting (normal workflow), this is a fault — a PO that may never
    // have reached the supplier — and it stays parked until a human resolves it.
    int       DeliveryUnconfirmed       = 0)
```

And include it in `TotalProblemOrders`:

```csharp
    public int TotalProblemOrders =>
        ParsingStuck + DeliveringStuck + TransformFailed + DeliveryFailed +
        DeliveryDeadLetter + RejectedBySupplier + Failed + DeliveryUnconfirmed;
```

- [ ] **Step 4: Populate it**

`ProcuLink.Infrastructure/Services/OpsHealthService.cs` — the existing `GROUP BY o.Status` already returns every status, so this is one more `Count(...)` call with no extra round-trip:

```csharp
            DeliveryUnconfirmed: Count(OrderStatusConstants.DeliveryUnconfirmed),
```

**Note for the implementer:** check whether the existing construction uses positional or named arguments and match it. If positional, the new value goes last.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --filter OpsHealth`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Core/Services/IOpsHealthService.cs ProcuLink.Infrastructure/Services/OpsHealthService.cs ProcuLink.Infrastructure.Tests/Services/
git commit -m "feat(ops): count parked orders in health, as a problem not a backlog

Unblocks the frontend Health tile. Without the count, 'All clear' renders green
while a PO sits unsent waiting on a human.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 9: Full verification before the PR

- [ ] **Step 1: Build the whole solution non-incrementally**

Run: `dotnet build ProcuLink.slnx --no-incremental`
Expected: Build succeeded, 0 errors, 0 warnings introduced by this branch.

(`--no-incremental` is deliberate: this is a worktree, and a stale incremental build has masked a real failure here before.)

- [ ] **Step 2: Run every backend test**

Run:
```bash
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj
dotnet test ProcuLink.Api.Tests/ProcuLink.Api.Tests.csproj
dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj
```
Expected: all green. One known-noise test may fail on Postgres flake: `TwoConcurrentRetries…` (Docker-gated). Any OTHER failure is yours — do not wave it through.

- [ ] **Step 3: Report honestly**

State the actual counts (`Passed: N, Failed: 0, Skipped: M`). If any Postgres test SKIPPED for lack of Docker, say so explicitly — a skipped park test is not a proven park.

- [ ] **Step 4: Push and check CI**

```bash
git push -u origin HEAD
gh run list --limit 3
```
Local green ≠ CI green (Windows dev, Linux CI). Wait for the run and report the real result.

---

## Follow-up (not this plan)

- **Frontend** (`project-proculink`, separate repo + PR): `UnifiedStatusBadge` label, the two operator actions behind `useConfirm()` with risk-stating copy in both directions, `src/lib/standards/catalog.ts` per-channel idempotency tier, delivery help article. Blocked on nothing — can start in parallel once Task 5 pins the endpoint shape.
- **Postmark Messages API probing** for email (positive evidence of a prior send) — explicitly out of scope per the spec.
