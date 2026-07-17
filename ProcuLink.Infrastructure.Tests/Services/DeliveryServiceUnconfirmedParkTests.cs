using FluentAssertions;
using Hangfire;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services;

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

    // CRITICAL: a park at the attempt-cap edge must not be immediately overwritten by
    // dead-lettering. RetryDeliveryAsync's cap logic decides `willDeadLetterOnFailure` from
    // `priorAttempts + 1 >= maxAttempts` BEFORE dispatching, then dead-letters if the dispatch
    // result is `Success=false` — which a park always is. Without a guard, the crashed send being
    // the LAST allowed attempt (priorAttempts == maxAttempts - 1) means DeadLetterAsync fires
    // right after ParkUnconfirmedAsync and clobbers every park constraint: the order becomes
    // 'delivery_dead_letter' instead of 'delivery_unconfirmed', DeliveryDueAt is nulled (killing
    // the SLA nag the park deliberately leaves running), the order becomes permanently
    // non-retryable (blocking the operator's "Send again"), and the audit fabricates
    // "DeliveryDeadLettered — retries exhausted" over an attempt row that says 'unconfirmed'.
    [Fact]
    public async Task ReAdopt_OnUnsafeChannel_AtLastAllowedAttempt_ParksInsteadOfDeadLettering()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "erp_erply"));

        // Two prior TERMINAL attempts already recorded (priorAttempts == 2), so with
        // MaxAttempts == 3 this crash-recovery re-drive is the LAST allowed attempt
        // (priorAttempts + 1 >= maxAttempts) — exactly the edge the existing park tests
        // (which all run at priorAttempts == 0) never exercise.
        db.DeliveryAttempts.AddRange(
            new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                Channel = "erp_erply", Destination = "https://erp.example/orders",
                Status = DeliveryAttempt.StatusFailed, AttemptNumber = 1,
                AttemptedAt = DateTime.UtcNow.AddHours(-2), ErrorMessage = "HTTP 503",
            },
            new DeliveryAttempt
            {
                Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
                Channel = "erp_erply", Destination = "https://erp.example/orders",
                Status = DeliveryAttempt.StatusFailed, AttemptNumber = 2,
                AttemptedAt = DateTime.UtcNow.AddHours(-1), ErrorMessage = "HTTP 503",
            });

        // The in-flight row for THIS (3rd) attempt, re-adopted as the unknown-outcome park.
        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            OrderId = ids.OrderId,
            OrgId = ids.OrgId,
            Channel = "erp_erply",
            Destination = "https://erp.example/orders",
            Status = DeliveryAttempt.StatusDispatching,
            AttemptNumber = 3,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30),
            IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 200), "erp_erply", ResendSafety.Unsafe);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        dispatcher.Calls.Should().Be(0, "the unknown outcome must never be blindly re-sent, even at the attempt cap");
        result.Success.Should().BeFalse();
        result.Parked.Should().BeTrue();

        var order = await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId);
        order.Status.Should().Be(OrderStatusConstants.DeliveryUnconfirmed,
            "a park is a deferral to a human, not a failure — it must never be overwritten by dead-lettering");
        order.DeliveryDueAt.Should().NotBeNull(
            "the SLA nag the park deliberately leaves running must not be killed by DeadLetterAsync nulling DeliveryDueAt");

        var attempts = await db.DeliveryAttempts.Where(a => a.OrderId == ids.OrderId).ToListAsync();
        attempts.Should().HaveCount(3, "the re-adopted row is finalised in place, never duplicated, and dead-lettering must not add its own bookkeeping");
        attempts.Single(a => a.AttemptNumber == 3).Status.Should().Be(DeliveryAttempt.StatusUnconfirmed);

        var auditActions = await db.AuditEvents.Where(a => a.EntityId == ids.OrderId).Select(a => a.Action).ToListAsync();
        auditActions.Should().Contain("DeliveryUnconfirmed");
        auditActions.Should().NotContain("DeliveryDeadLettered",
            "the audit trail must not fabricate a 'retries exhausted' event over an attempt row that says unconfirmed");
    }

    // The other half of the park: the operator's "Send again" must REACH the supplier. The park is
    // only honest if the human's decision is actually executed — an accepted 202 that dispatches
    // nothing leaves the order parked forever while telling the operator it was sent.
    // Drives the real DeliverOrderJob path (the public DispatchArtifactAsync, which makes its own
    // claim), because the claim is exactly what was broken: a claim that excludes
    // delivery_unconfirmed matches 0 rows and no-ops as a BENIGN success.
    [Theory]
    [InlineData("erp_erply")]
    [InlineData("email")]
    public async Task OperatorReSend_FromParkedOrder_ActuallyDispatches(string protocol)
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryUnconfirmed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol));

        // The exact post-park state: the attempt row was finalised TERMINAL ('unconfirmed'), which
        // is what lets this re-send open a fresh attempt instead of re-adopting an in-flight row
        // and immediately re-parking.
        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
            Channel = protocol, Destination = "https://supplier.example/orders",
            Status = DeliveryAttempt.StatusUnconfirmed, AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30), IdempotencyKey = key,
            ErrorMessage = DeliveryService.BuildUnconfirmedMessage(protocol),
        });
        await db.SaveChangesAsync();

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 200), protocol, ResendSafety.Unsafe);
        var service = CreateService(db, dispatcher, encryption);

        // requireAutoDeliver: false — an explicit operator re-send bypasses the AutoDeliver flag,
        // exactly as DeliverOrderJob.EnqueueRedeliver drives it.
        var result = await service.DispatchArtifactAsync(ids.OrgId, ids.OrderId, ids.ArtifactId, false, default);

        dispatcher.Calls.Should().Be(1, "the operator explicitly accepted the duplicate risk — their send must reach the supplier");
        result.Success.Should().BeTrue();
        result.Parked.Should().BeFalse("a fresh operator-driven send is not a re-adopted unknown outcome");

        (await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.Delivered);

        var attempts = await db.DeliveryAttempts.Where(a => a.OrderId == ids.OrderId).ToListAsync();
        attempts.Should().HaveCount(2, "the re-send opens its own attempt row beside the parked one");
        attempts.Single(a => a.Status == DeliveryAttempt.StatusUnconfirmed).AttemptNumber
            .Should().Be(1, "the parked row records an outcome we never observed — a later send never rewrites it");
    }

    // A park never enters the delivered-only meter's billable set (delivered + rejected_by_supplier).
    // Scoped precisely: that meter is Billing:CountDeliveredOnly, which DEFAULTS OFF, so this pins
    // the flag-ON behaviour. With the flag OFF the shipped count-at-creation meter counts every
    // order whatever its status — a park included, exactly as delivery_failed is counted today.
    [Fact]
    public async Task ParkedOrder_IsNotInTheDeliveredOnlyMeterBillableSet()
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

    // The audit event is a binding constraint on the park, not an implementation detail: it is
    // the only durable, queryable record that an order was parked rather than delivered or
    // dead-lettered (org-scoped, so a support/ops query never leaks across tenants).
    [Fact]
    public async Task ReAdopt_OnUnsafeChannel_WritesDeliveryUnconfirmedAuditEvent()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "email"));

        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
            Channel = "email", Destination = "orders@supplier.example",
            Status = DeliveryAttempt.StatusDispatching, AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30), IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new CountingDispatcher(new DeliveryResult(true, null, 200), "email", ResendSafety.Unsafe), encryption);
        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        var audit = await db.AuditEvents.SingleAsync(a => a.Action == "DeliveryUnconfirmed");
        audit.OrgId.Should().Be(ids.OrgId, "the audit event must be org-scoped");
        audit.EntityType.Should().Be("Order");
        audit.EntityId.Should().Be(ids.OrderId);
    }

    // IMPORTANT: a re-adopted in-flight row proves only that a send was ATTEMPTED — a crash
    // between the marker commit and the network write (or a cancelled token on shutdown) parks
    // with no send at all. The operator sentence must never assert a send we cannot prove.
    [Theory]
    [InlineData("erp_erply", "the Erply connection")]
    [InlineData("erp_directo", "the Directo connection")]
    [InlineData("email", "email")]
    [InlineData("sftp", "this delivery channel")]
    public void BuildUnconfirmedMessage_NeverAssertsAConfirmedSend(string protocol, string expectedChannelPhrase)
    {
        var message = DeliveryService.BuildUnconfirmedMessage(protocol);

        message.Should().Contain("may have sent this order",
            "a re-adopted row only proves the send was attempted, not that it happened");
        message.Should().NotContain("We sent this order",
            "asserting a definite send fabricates an outcome the crash-recovery path cannot observe");
        message.Should().Contain(expectedChannelPhrase);
        message.Should().Contain("send it again or mark it delivered");

        // Plain-language rule: no internal vocabulary leaks into operator-facing copy.
        message.Should().NotContainAny("idempotency", "re-adopt", "dispatching row", "park");
    }

    // ── A retry that fires when the order is ALREADY parked (I3) ─────────────────────────────
    // Reachable: the stuck sweep bumps UpdatedAt before enqueueing, so its retry finds the row
    // non-stale, no-ops, and schedules a backoff retry; any such retry firing after the park
    // seeds the loop. RetryDeliveryAsync's non-retryable early gate persists NO attempt row, so
    // the count never advances — a caller that reschedules on that result reschedules at the SAME
    // delay forever. The park is only recognisable to the existing guards if the early return
    // carries Parked.

    [Fact]
    public async Task RetryDeliveryAsync_OnAlreadyParkedOrder_ReturnsParkedResult()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedParkedOrderAsync(db, encryption, "erp_erply");

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 200), "erp_erply", ResendSafety.Unsafe);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Success.Should().BeFalse();
        result.Parked.Should().BeTrue("an already-parked order is still parked — the callers' park guards must recognise it");
        dispatcher.Calls.Should().Be(0, "the automatic queue must never re-send a park on the operator's behalf");
    }

    [Fact]
    public async Task RetryDeliveryJob_OnAlreadyParkedOrder_SchedulesNoFurtherRetry()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedParkedOrderAsync(db, encryption, "erp_erply");

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 200), "erp_erply", ResendSafety.Unsafe);
        var jobs = new CapturingJobClient();
        var job = new RetryDeliveryJob(
            CreateService(db, dispatcher, encryption),
            jobs,
            NullLogger<RetryDeliveryJob>.Instance,
            new DeliveryReliabilityOptions { MaxAttempts = MaxAttempts, BackoffMinutes = new[] { 30, 60, 120 } });

        await job.ExecuteAsync(ids.OrderId, ids.OrgId, default);

        jobs.Captured.Should().BeEmpty(
            "the attempt count cannot advance past a park, so a rescheduled retry would repeat at the same delay forever");
        dispatcher.Calls.Should().Be(0);
        (await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryUnconfirmed, "only an operator moves an order out of the park");
    }

    // Regression guard: the Parked flag marks the park ONLY. Dead-letter and delivered keep their
    // own semantics — mislabelling either as a park would misroute the callers' guards.
    [Fact]
    public async Task RetryDeliveryAsync_DeadLetteredOrDelivered_AreNotReportedAsParked()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();

        var deadLetter = await SeedOrderAsync(db, OrderStatusConstants.DeliveryDeadLetter);
        var delivered = await SeedOrderAsync(db, OrderStatusConstants.Delivered);
        await db.SaveChangesAsync();

        var service = CreateService(db, new CountingDispatcher(new DeliveryResult(true, null, 200), "http", ResendSafety.Unsafe), encryption);

        var deadLetterResult = await service.RetryDeliveryAsync(deadLetter.OrgId, deadLetter.OrderId, MaxAttempts, default);
        deadLetterResult.Success.Should().BeFalse();
        deadLetterResult.Parked.Should().BeFalse("retries are exhausted — that is not a park");

        var deliveredResult = await service.RetryDeliveryAsync(delivered.OrgId, delivered.OrderId, MaxAttempts, default);
        deliveredResult.Success.Should().BeTrue();
        deliveredResult.Parked.Should().BeFalse("a confirmed delivery is not a park");
    }

    // ── The park must reach the exception surface (F1) ───────────────────────────────────────
    // A park writes a terminal status like every other delivery outcome, so it must reconcile
    // exceptions like every other delivery outcome. Two failures ride on this: a parked order with
    // no exception row is invisible to the tiles/dashboard (the operator sees "not all clear" over
    // a grid of zeros), and — worse — a park that FOLLOWS a failed attempt leaves that attempt's
    // stale 'delivery_failed' exception OPEN. The order then asserts both "Delivery to the supplier
    // failed" (error) and "delivery unconfirmed" at once. An operator who believes the red one
    // clicks Send and hands the supplier the duplicate PO this whole feature exists to prevent.

    [Fact]
    public async Task ParkAfterAFailedAttempt_ResolvesTheStaleFailureException_AndNeverShowsBoth()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "erp_erply"));

        // Attempt 1 genuinely failed and opened a 'delivery_failed' exception (PersistAttemptAsync's
        // reconcile). Attempt 2 then crashed mid-send, leaving the in-flight row this re-drive
        // re-adopts — so the park lands on top of a live failure exception.
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
            Channel = "erp_erply", Destination = "https://erp.example/orders",
            Status = DeliveryAttempt.StatusFailed, AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddHours(-2), ErrorMessage = "HTTP 503",
        });
        db.OrderExceptions.Add(new OrderException
        {
            Id = Guid.NewGuid(), OrgId = ids.OrgId, OrderId = ids.OrderId,
            Stage = "Deliver", Code = "delivery_failed", Severity = "error", State = "open",
            Message = "Delivery to the supplier failed.",
            CreatedAt = DateTime.UtcNow.AddHours(-2),
        });

        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
            Channel = "erp_erply", Destination = "https://erp.example/orders",
            Status = DeliveryAttempt.StatusDispatching, AttemptNumber = 2,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30), IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var dispatcher = new CountingDispatcher(new DeliveryResult(true, null, 200), "erp_erply", ResendSafety.Unsafe);
        var service = CreateService(db, dispatcher, encryption);

        var result = await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        result.Parked.Should().BeTrue();
        (await db.PurchaseOrders.SingleAsync(o => o.Id == ids.OrderId)).Status
            .Should().Be(OrderStatusConstants.DeliveryUnconfirmed);

        var open = await db.OrderExceptions
            .Where(e => e.OrderId == ids.OrderId && e.State == "open")
            .ToListAsync();

        open.Should().ContainSingle(
            "a parked order has exactly one problem — an unknown outcome; telling the operator it ALSO definitely failed "
            + "invites the Send-again duplicate the park exists to prevent");
        open[0].Code.Should().Be("delivery_unconfirmed");

        (await db.OrderExceptions.SingleAsync(e => e.Code == "delivery_failed")).State
            .Should().Be("resolved", "the failure that preceded the park is no longer the order's condition");
    }

    [Fact]
    public async Task Park_OpensAWarningException_ThatNeverClaimsTheSendHappened()
    {
        await using var db = CreateDb();
        var encryption = CreateEncryption();
        var ids = await SeedOrderAsync(db, OrderStatusConstants.Delivering);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol: "email"));

        var key = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
            Channel = "email", Destination = "orders@supplier.example",
            Status = DeliveryAttempt.StatusDispatching, AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30), IdempotencyKey = key,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, new CountingDispatcher(new DeliveryResult(true, null, 200), "email", ResendSafety.Unsafe), encryption);
        await service.RetryDeliveryAsync(ids.OrgId, ids.OrderId, MaxAttempts, default);

        var ex = await db.OrderExceptions.SingleAsync(e => e.OrderId == ids.OrderId && e.State == "open");

        ex.Code.Should().Be("delivery_unconfirmed");
        ex.Stage.Should().Be("Deliver");
        ex.Severity.Should().Be("warning",
            "we do not know that this delivery failed — dressing an unknown outcome as an error is the same lie in a different colour");
        ex.Message.Should().Contain("may have sent this order",
            "the exception surface must not assert a send the crash-recovery path never observed");
        ex.Message.Should().Contain("send it again or mark it delivered", "the operator's two choices are the point of the park");
        ex.Message.Should().NotContainAny("idempotency", "re-adopt", "dispatching row", "park");
    }

    /// <summary>The exact post-park state: order parked, its attempt row finalised 'unconfirmed'.</summary>
    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedParkedOrderAsync(
        ProcuLinkDbContext db, DeliveryEncryptionService encryption, string protocol)
    {
        var ids = await SeedOrderAsync(db, OrderStatusConstants.DeliveryUnconfirmed);
        db.SupplierDeliveryConfigs.Add(MakeConfig(ids.OrgId, ids.SupplierId, encryption, protocol));
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), OrderId = ids.OrderId, OrgId = ids.OrgId,
            Channel = protocol, Destination = "https://erp.example/orders",
            Status = DeliveryAttempt.StatusUnconfirmed, AttemptNumber = 1,
            AttemptedAt = DateTime.UtcNow.AddMinutes(-30),
            IdempotencyKey = DeliveryService.BuildIdempotencyKey(ids.OrderId, ids.ArtifactId),
            ErrorMessage = DeliveryService.BuildUnconfirmedMessage(protocol),
        });
        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>Captures every scheduled/enqueued job so "scheduled nothing" is a real assertion.</summary>
    private sealed class CapturingJobClient : IBackgroundJobClient
    {
        public List<(Hangfire.Common.Job Job, IState State)> Captured { get; } = new();

        public string Create(Hangfire.Common.Job job, IState state)
        {
            Captured.Add((job, state));
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    // ── Helpers (copied from DeliveryServiceIdempotencyTests.cs — direct sibling) ───────────

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeliveryEncryptionService CreateEncryption()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:EncryptionKey"] = key })
            .Build();
        return new DeliveryEncryptionService(config);
    }

    private static async Task<(Guid OrgId, Guid SupplierId, Guid OrderId, Guid ArtifactId)> SeedOrderAsync(
        ProcuLinkDbContext db, string status)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = "PO-PARK-1",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.OutboundArtifacts.Add(new OutboundArtifact
        {
            Id = artifactId,
            OrderId = orderId,
            OrgId = orgId,
            Format = "csv",
            FileKey = "artifact.csv",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return (orgId, supplierId, orderId, artifactId);
    }

    private static DeliveryService CreateService(
        ProcuLinkDbContext db, IDeliveryDispatcher dispatcher, DeliveryEncryptionService encryption) =>
        new(
            db,
            new FakeFileStorage(),
            encryption,
            new[] { dispatcher },
            new NoOpIntegrationTriggerService(),
            new FakeAnalyticsService(),
            new OrderExceptionService(db),
            NullLogger<DeliveryService>.Instance);

    // Parameterised over DeliveryServiceIdempotencyTests's version so a test can pass a
    // non-"http" protocol (erp_erply / erp_directo / email) to exercise the Unsafe park.
    private static SupplierDeliveryConfig MakeConfig(
        Guid orgId, Guid supplierId, DeliveryEncryptionService encryption, string protocol = "http") =>
        new()
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = protocol,
            AutoDeliver = true,
            ConfigJson = "{\"url\":\"https://supplier.example/orders\"}",
            EncryptedCredentials = encryption.Encrypt("{\"type\":\"none\"}"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

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

    private sealed class NoOpIntegrationTriggerService : IIntegrationTriggerService
    {
        public Task EnqueueAsync(Guid organisationId, string eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class FakeFileStorage : IFileStorageService
    {
        public Task<string> UploadAsync(Stream content, string key, string contentType, CancellationToken ct) =>
            Task.FromResult(key);
        public Task<string> GetSignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct) =>
            Task.FromResult($"https://files.example/{key}");
        public Task<Stream> DownloadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream("order,line\r\n1,ok\r\n"u8.ToArray()));
        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }
}
