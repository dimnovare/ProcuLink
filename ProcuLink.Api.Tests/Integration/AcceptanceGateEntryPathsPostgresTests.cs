using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Contracts;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// WP-17 — a blocked order refuses to transform through EVERY entry path, on real Postgres.
///
/// <para><b>Why Postgres.</b> <c>OrdersController.Transform</c> claims the order with
/// <c>ExecuteUpdateAsync</c>, which EF InMemory cannot translate — the browser path cannot be
/// driven end to end anywhere else. The transform's own claim has an InMemory fallback, so only
/// here do the controller claim and the service claim both run the way production runs them.</para>
///
/// <para><b>What "entry paths" means after WP-17.</b> There is exactly ONE server-side TRANSFORM
/// door — <c>OrderTransformService.TransformAsync</c>, reached only via <c>TransformOrderJob</c>,
/// which only <c>OrdersController.Transform</c> enqueues (<c>AcceptanceGateSingleDoorTests</c> pins
/// that structurally). Browser send, REST ingress and inbound email all reach the supplier through
/// that door, so they inherit one answer.
///
/// <para>The inbox's bulk "Send selected" does NOT. It calls
/// <c>POST /api/orders/{id}/redeliver</c> once per selected row, which dispatches a STORED artifact
/// and never touches the transform. Because <c>Organisation.AutoDeliver</c> defaults to false,
/// <c>ready_to_deliver</c> is the ordinary resting state of a live order and that endpoint is the
/// mainline manual send in this product — so it is gated in its own right
/// (<see cref="InboxBulkSend_refusesEveryOrderInTheBatch"/>, and the unit-level proof for it and
/// its siblings in <c>AcceptanceGateBlocksDeliveryTests</c>). Driving the transform endpoint three
/// times would have proved nothing about it.</para></para>
///
/// <para><b>The negative control is the load-bearing test here.</b> Everything else in this file
/// would stay green if the orders were refused for an unrelated reason — a missing supplier, an
/// unresolved line, a status the claim rejects. <see cref="NegativeControl_theOnlyDifferenceIsTheRule"/>
/// runs the same fixture twice, changing ONE field, and asserts the two runs DIFFER. If the gate
/// stops working, or if something else starts refusing everything, that test fails.</para>
///
/// Docker-gated; skips cleanly where Docker is absent.
/// </summary>
[Collection("postgres-container")]
public sealed class AcceptanceGateEntryPathsPostgresTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _pg = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase($"proculink_gate_{Guid.NewGuid():N}")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _pg.StartAsync();

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;

        await using var migrateDb = new ProcuLinkDbContext(_options);
        await migrateDb.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    // ── The four entry paths ──────────────────────────────────────────────────

    /// <summary>Path 1 — a coordinator clicks Send in the order workshop.</summary>
    [DockerRequiredFact]
    public async Task BrowserSend_isRefused()
    {
        var order = await SeedBlockedOrderAsync("browser");
        var outcome = await SendThroughTheControllerAsync(order);

        AssertRefused(outcome);
        Assert.Equal(StatusCodes.Status202Accepted, outcome.HttpStatus);   // the API accepted the request…
        Assert.Equal(OrderStatusConstants.TransformFailed, outcome.Status); // …and the server refused the send
    }

    /// <summary>
    /// Path 2 — the inbox's bulk "Send selected", driven through the endpoint it ACTUALLY calls:
    /// <c>POST /api/orders/{id}/redeliver</c>, once per selected row, on orders that already hold a
    /// stored artifact and sit in <c>ready_to_deliver</c>. This is the mainline manual send, and it
    /// never goes near the transform.
    ///
    /// <para>The batch shape is the assertion that matters here: three orders selected together must
    /// ALL be refused and NONE of them may enqueue a delivery. One row slipping through a shared
    /// code path is precisely the failure a per-order test would not see.</para>
    /// </summary>
    [DockerRequiredFact]
    public async Task InboxBulkSend_refusesEveryOrderInTheBatch()
    {
        var batch = new List<Seeded>();
        for (var i = 0; i < 3; i++)
            batch.Add(await SeedBlockedOrderAsync($"bulk-{i}",
                status: OrderStatusConstants.ReadyToDeliver, withArtifact: true));

        var enqueued = 0;
        foreach (var order in batch)
        {
            var (status, error, created) = await BulkSendOneAsync(order);

            Assert.Equal(StatusCodes.Status409Conflict, status);
            Assert.NotNull(error);
            Assert.Contains("Currency must be EUR", error!);
            Assert.Contains("USD", error!);
            Assert.Contains("Set currency to EUR", error!);
            Assert.DoesNotContain("failed rule", error!);
            enqueued += created;
        }

        Assert.Equal(0, enqueued);

        // …and no order in the batch was moved on by the attempt: a refusal must leave the row
        // exactly where the operator left it, still selectable, still fixable.
        await using var db = NewContext();
        foreach (var order in batch)
        {
            var status = await db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == order.OrderId && o.OrgId == order.OrgId)
                .Select(o => o.Status).FirstAsync();
            Assert.Equal(OrderStatusConstants.ReadyToDeliver, status);
        }
    }

    /// <summary>
    /// NEGATIVE CONTROL for the bulk send: same fixture, same endpoint, the ONE difference is the
    /// currency the supplier's rule judges. Without this, "the bulk send is refused" and "the bulk
    /// send is broken" look identical.
    /// </summary>
    [DockerRequiredFact]
    public async Task InboxBulkSend_whenTheOrdersSatisfyTheRule_sendsEveryOne()
    {
        var batch = new List<Seeded>();
        for (var i = 0; i < 3; i++)
            batch.Add(await SeedBlockedOrderAsync($"bulk-ok-{i}", currency: "EUR",
                status: OrderStatusConstants.ReadyToDeliver, withArtifact: true));

        var enqueued = 0;
        foreach (var order in batch)
        {
            var (status, error, created) = await BulkSendOneAsync(order);
            Assert.Equal(StatusCodes.Status202Accepted, status);
            Assert.Null(error);
            enqueued += created;
        }

        Assert.Equal(batch.Count, enqueued);
    }

    /// <summary>
    /// Path 3 — an order pushed in over REST ingress (<c>plk_</c> API key). It reaches the transform
    /// through the Hangfire job, with no browser and no controller in the loop.
    /// </summary>
    [DockerRequiredFact]
    public async Task RestIngressOrder_isRefused()
    {
        var order = await SeedBlockedOrderAsync("rest-ingress", sourceFileKey: "ingress/rest/po.csv");
        AssertRefused(await SendThroughTheJobAsync(order));
    }

    /// <summary>
    /// Path 4 — an order that arrived as inbound email ({slug}@orders.proculink.eu). Same job door,
    /// no human in the loop at all, which is exactly the case the browser-only enforcement missed.
    /// </summary>
    [DockerRequiredFact]
    public async Task EmailIngestOrder_isRefused()
    {
        var order = await SeedBlockedOrderAsync("email-ingest",
            sourceFileKey: "ingress/email/po.csv", inboundSenderDomain: "acme.com");
        AssertRefused(await SendThroughTheJobAsync(order));
    }

    /// <summary>
    /// The three TRANSFORM-door paths must AGREE. Asserting each one separately would still pass if
    /// one of them started refusing for a different reason, or stopped refusing while the others
    /// carried the suite; this compares them to each other, so any single path drifting fails the
    /// test. (The inbox bulk-send is deliberately absent: it is not a transform-door path, and
    /// folding it in here is exactly the conflation that let it go ungated.)
    /// </summary>
    [DockerRequiredFact]
    public async Task AllTransformDoorPaths_reachTheSameRefusal()
    {
        var outcomes = new List<(string Path, Outcome Result)>
        {
            ("browser send",     await SendThroughTheControllerAsync(await SeedBlockedOrderAsync("agree-browser"))),
            ("REST ingress",     await SendThroughTheJobAsync(await SeedBlockedOrderAsync("agree-rest", sourceFileKey: "ingress/rest/po.csv"))),
            ("inbound email",    await SendThroughTheJobAsync(await SeedBlockedOrderAsync("agree-email", inboundSenderDomain: "acme.com"))),
        };

        var statuses = outcomes.Select(o => o.Result.Status).Distinct().ToList();
        Assert.True(statuses.Count == 1,
            "the entry paths disagree about the outcome: "
          + string.Join(", ", outcomes.Select(o => $"{o.Path} → {o.Result.Status}")));
        Assert.Equal(OrderStatusConstants.TransformFailed, statuses[0]);

        Assert.All(outcomes, o => Assert.Empty(o.Result.Artifacts));
        Assert.All(outcomes, o => Assert.Contains("Currency must be EUR", o.Result.ErrorMessage ?? ""));
    }

    // ── The negative control ──────────────────────────────────────────────────

    /// <summary>
    /// NEGATIVE CONTROL. Two runs of the identical fixture through the identical path; the ONLY
    /// difference is the order's currency, which is the field the supplier's blocking rule judges.
    /// The assertion is on the DIFFERENCE: if both runs refuse, something other than the gate is
    /// stopping these orders and every other test in this file is meaningless; if both succeed, the
    /// gate is not enforcing. Either way this fails.
    /// </summary>
    [DockerRequiredFact]
    public async Task NegativeControl_theOnlyDifferenceIsTheRule()
    {
        var blocked = await SendThroughTheControllerAsync(await SeedBlockedOrderAsync("control-usd", currency: "USD"));
        var allowed = await SendThroughTheControllerAsync(await SeedBlockedOrderAsync("control-eur", currency: "EUR"));

        Assert.NotEqual(blocked.Status, allowed.Status);

        Assert.Equal(OrderStatusConstants.TransformFailed, blocked.Status);
        Assert.Empty(blocked.Artifacts);

        Assert.Equal(OrderStatusConstants.ReadyToDeliver, allowed.Status);
        Assert.Single(allowed.Artifacts);
    }

    /// <summary>
    /// A second control on the other axis: same non-conforming order, same rule, same field — only
    /// the rule's SEVERITY differs. A warning is advice; it must not stop the send. Together with
    /// the currency control this pins the gate to the two properties it is supposed to read and
    /// nothing else.
    /// </summary>
    [DockerRequiredFact]
    public async Task NegativeControl_aWarningRuleDoesNotStopTheSend()
    {
        var blocked = await SendThroughTheControllerAsync(
            await SeedBlockedOrderAsync("sev-error", severity: "error"));
        var allowed = await SendThroughTheControllerAsync(
            await SeedBlockedOrderAsync("sev-warning", severity: "warning"));

        Assert.NotEqual(blocked.Status, allowed.Status);
        Assert.Equal(OrderStatusConstants.TransformFailed, blocked.Status);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, allowed.Status);
    }

    // ── The audited override ──────────────────────────────────────────────────

    /// <summary>
    /// The way out: an operator records an override with a reason, and the SAME order that was just
    /// refused now sends — with both the grant and its use in the audit trail.
    /// </summary>
    [DockerRequiredFact]
    public async Task RecordedOverride_letsTheSameOrderThrough_andIsAudited()
    {
        var order = await SeedBlockedOrderAsync("override");

        var refused = await SendThroughTheControllerAsync(order);
        Assert.Equal(OrderStatusConstants.TransformFailed, refused.Status);

        await using (var db = NewContext())
        {
            var gate = new AcceptanceGate(db, new SupplierAcceptanceService(db));
            var result = await gate.RecordOverrideAsync(
                order.OrgId, order.OrderId, "user_2opsLead",
                "Supplier confirmed they will accept USD for this PO (ticket TCK-4412).", CancellationToken.None);
            Assert.True(result.Recorded, result.Error);
        }

        var sent = await SendThroughTheControllerAsync(order);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, sent.Status);
        Assert.Single(sent.Artifacts);

        await using var check = NewContext();
        var actions = await check.AuditEvents.AsNoTracking()
            .Where(a => a.OrgId == order.OrgId && a.EntityId == order.OrderId)
            .Select(a => a.Action)
            .ToListAsync();

        Assert.Contains(AcceptanceGateAudit.BlockedAction, actions);        // the refusal
        Assert.Contains(AcceptanceGateAudit.OverriddenAction, actions);     // who + why
        Assert.Contains(AcceptanceGateAudit.OverrideUsedAction, actions);   // the guarantee actually waived

        var granted = await check.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.OrgId == order.OrgId && a.EntityId == order.OrderId
                           && a.Action == AcceptanceGateAudit.OverriddenAction);
        var payload = granted.Payload!.RootElement;
        Assert.Equal("user_2opsLead", payload.GetProperty(AcceptanceGateAudit.ActorKey).GetString());
        Assert.Contains("TCK-4412", payload.GetProperty(AcceptanceGateAudit.ReasonKey).GetString()!);
    }

    /// <summary>
    /// A refused order must stay RECOVERABLE. <c>transform_failed</c> is re-claimable by design, so
    /// fixing the underlying data and sending again works — a gate that parked orders in a state
    /// nothing can leave would trade a silent failure for a louder one.
    /// </summary>
    [DockerRequiredFact]
    public async Task ARefusedOrder_sendsOnceTheDataIsFixed()
    {
        var order = await SeedBlockedOrderAsync("recover");
        Assert.Equal(OrderStatusConstants.TransformFailed, (await SendThroughTheControllerAsync(order)).Status);

        await using (var db = NewContext())
        {
            var po = await db.PurchaseOrders.FirstAsync(o => o.Id == order.OrderId && o.OrgId == order.OrgId);
            po.Currency = "EUR";
            await db.SaveChangesAsync();
        }

        var sent = await SendThroughTheControllerAsync(order);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, sent.Status);
        Assert.Single(sent.Artifacts);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed record Seeded(Guid OrgId, Guid OrderId);

    private sealed record Outcome(
        string Status, IReadOnlyList<Guid> Artifacts, string? ErrorMessage, int HttpStatus);

    private static void AssertRefused(Outcome outcome)
    {
        Assert.Equal(OrderStatusConstants.TransformFailed, outcome.Status);
        Assert.Empty(outcome.Artifacts);
        Assert.NotNull(outcome.ErrorMessage);
        // Plain language, one sentence per failure: what failed, actual vs expected, and the fix.
        Assert.Contains("Currency must be EUR", outcome.ErrorMessage!);
        Assert.Contains("USD", outcome.ErrorMessage!);
        Assert.Contains("Set currency to EUR", outcome.ErrorMessage!);
        // No dev-rule jargon leaking into the operator's face.
        Assert.DoesNotContain("failed rule", outcome.ErrorMessage!);
        Assert.DoesNotContain("BlockOnFail", outcome.ErrorMessage!);
    }

    /// <summary>
    /// ONE row of the inbox's bulk "Send selected", through the endpoint the browser really calls:
    /// <c>POST /api/orders/{id}/redeliver</c>. Returns the HTTP status, the `error` string the inbox
    /// shows next to the PO when the call fails, and how many Hangfire jobs the call created —
    /// because "refused" has to mean nothing was enqueued, not merely that the response looked bad.
    /// </summary>
    private async Task<(int Status, string? Error, int JobsCreated)> BulkSendOneAsync(Seeded order)
    {
        await using var db = NewContext();

        var po = await db.PurchaseOrders.AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.OutboundArtifacts)
            .FirstAsync(o => o.Id == order.OrderId && o.OrgId == order.OrgId);

        var (ctrl, jobs) = BuildController(db, order.OrgId, po);
        var created = 0;
        jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback(() => created++)
            .Returns(Guid.NewGuid().ToString());

        var response = await ctrl.Redeliver(order.OrderId, CancellationToken.None);

        var status = response switch
        {
            ObjectResult o     => o.StatusCode ?? StatusCodes.Status200OK,
            StatusCodeResult s => s.StatusCode,
            _                  => StatusCodes.Status200OK,
        };

        string? error = null;
        if (response is ObjectResult { Value: { } body } && status >= 400)
            error = body.GetType().GetProperty("error")?.GetValue(body) as string;

        return (status, error, created);
    }

    /// <summary>Browser send: the real HTTP transform endpoint, then the job it enqueued.</summary>
    private async Task<Outcome> SendThroughTheControllerAsync(Seeded order)
    {
        int httpStatus;
        await using (var db = NewContext())
        {
            var po = await db.PurchaseOrders.AsNoTracking().Include(o => o.Lines)
                .FirstAsync(o => o.Id == order.OrderId && o.OrgId == order.OrgId);

            var (ctrl, jobs) = BuildController(db, order.OrgId, po);
            var response = await ctrl.Transform(order.OrderId, new TransformRequest("csv"), CancellationToken.None);
            httpStatus = (response as ObjectResult)?.StatusCode ?? StatusCodes.Status200OK;

            // The endpoint must actually have enqueued work — otherwise the "refusal" below would be
            // the transform never running, which is a different bug wearing the same clothes.
            jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
        }

        await RunTransformJobAsync(order);
        return await ReadOutcomeAsync(order, httpStatus);
    }

    /// <summary>REST ingress / inbound email: the Hangfire job, with no browser in the loop.</summary>
    private async Task<Outcome> SendThroughTheJobAsync(Seeded order)
    {
        await RunTransformJobAsync(order);
        return await ReadOutcomeAsync(order, StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Executes <see cref="TransformOrderJob"/> exactly as Hangfire would. The job rethrows a failed
    /// transform so Hangfire can record it, which is what a refusal looks like from the job's side.
    /// </summary>
    private async Task RunTransformJobAsync(Seeded order)
    {
        await using var db = NewContext();
        var job = new TransformOrderJob(
            Build(db),
            new Mock<IBackgroundJobClient>().Object,
            NullLogger<TransformOrderJob>.Instance,
            db,
            new Mock<IAnalyticsService>().Object);

        try
        {
            await job.ExecuteAsync(order.OrderId, order.OrgId, "csv", CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Expected on the refusal paths — Hangfire's failure signal, not a test failure.
        }
    }

    private async Task<Outcome> ReadOutcomeAsync(Seeded order, int httpStatus)
    {
        await using var db = NewContext();

        var status = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == order.OrderId && o.OrgId == order.OrgId)
            .Select(o => o.Status).FirstAsync();

        var artifacts = await db.OutboundArtifacts.AsNoTracking()
            .Where(a => a.OrderId == order.OrderId && a.OrgId == order.OrgId)
            .Select(a => a.Id).ToListAsync();

        // The string the workshop shows the user: OrdersController reads the `error` key off the
        // most recent TransformFailed audit event.
        var failure = await db.AuditEvents.AsNoTracking()
            .Where(a => a.OrgId == order.OrgId && a.EntityId == order.OrderId && a.Action == "TransformFailed")
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();

        var message = failure?.Payload?.RootElement.TryGetProperty("error", out var err) == true
            ? err.GetString()
            : null;

        return new Outcome(status, artifacts, message, httpStatus);
    }

    private static OrderService Build(ProcuLinkDbContext db)
    {
        var fileStorage = new Mock<IFileStorageService>();
        fileStorage
            .Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("artifact-key");

        return new OrderService(
            db,
            fileStorage.Object,
            new OrderParserFactory(new IPurchaseOrderParser[] { new CsvOrderParser() }),
            new Mock<IItemMappingService>().Object,
            new OrderExceptionService(db),
            new Mock<IPoMappingService>().Object,
            new Mock<IAiMappingService>().Object,
            new ITransformService[] { new CsvTransformService() },
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService());
    }

    private static (OrdersController Ctrl, Mock<IBackgroundJobClient> Jobs) BuildController(
        ProcuLinkDbContext db, Guid orgId, PurchaseOrderEntity order)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(orgId);

        var orders = new Mock<IOrderService>();
        orders.Setup(o => o.GetByIdAsync(orgId, order.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<PurchaseOrderEntity>.Ok(order));

        var billing = new Mock<IBillingService>();
        billing.Setup(b => b.CheckOrderLimitAsync(orgId, It.IsAny<CancellationToken>()))
               .ReturnsAsync(new LimitCheckResult(Allowed: true, PilotExpired: false, Plan: "operations", Limit: 1000));

        var jobs = new Mock<IBackgroundJobClient>();

        var ctrl = new OrdersController(
            orders.Object,
            tenant.Object,
            jobs.Object,
            db,
            NullLogger<OrdersController>.Instance,
            billing.Object,
            new Mock<IIdempotencyService>().Object,
            new Mock<IOrderExceptionService>().Object,
            // The REAL acceptance service against the REAL DbContext: OrdersController builds its
            // acceptance gate from these two when DI supplies none, so /redeliver is exercised with
            // the same gate production uses rather than a stand-in that always says "clear".
            new SupplierAcceptanceService(db),
            new Mock<IOrderMappingOverrideService>().Object,
            new PromoteMappingService(db, new PoMappingService(db)),
            new Mock<IFileStorageService>().Object,
            new Mock<ProcuLink.Transform.Tokenizing.ISourceTokenizer>().Object,
            Array.Empty<ITransformService>());

        return (ctrl, jobs);
    }

    /// <summary>
    /// A fully-resolved, otherwise-perfect <c>ready</c> order with an ACTIVE acceptance profile whose
    /// single rule judges the currency. Every invariant passes and every line is resolved, so nothing
    /// but that rule can be the thing that refuses the order.
    /// </summary>
    private async Task<Seeded> SeedBlockedOrderAsync(
        string tag,
        string currency = "USD",
        string severity = "error",
        string? sourceFileKey = null,
        string? inboundSenderDomain = null,
        string? status = null,
        bool withArtifact = false)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = NewContext();
        db.Organisations.Add(new Organisation
        {
            Id = orgId, ClerkOrgId = $"org_gate_{orgId:N}", Name = $"Gate Org {tag}",
            Slug = $"gate-{orgId:N}", Plan = "operations", AccountStatus = "active", CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Acme GmbH", CreatedAt = now });
        await db.SaveChangesAsync();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = supplierId,
            PoNumber = $"PO-GATE-{tag}", BuyerName = "Buyer Ltd", Currency = currency,
            OrderDate = new DateOnly(2026, 7, 30), Status = status ?? OrderStatusConstants.Ready,
            SourceFileKey = sourceFileKey, InboundSenderDomain = inboundSenderDomain,
            InboundSenderDomainCapturedAt = inboundSenderDomain is null ? null : now,
            CreatedAt = now, UpdatedAt = now,
            Lines =
            {
                new PurchaseOrderLineEntity
                {
                    Id = Guid.NewGuid(), OrderId = orderId, LineNumber = 1,
                    BuyerItemCode = "B-1", SupplierItemCode = "SUP-1", Description = "Widget",
                    Quantity = 3m, Unit = "EA", UnitPrice = 10m, NeedsReview = false, Confidence = 1.0f,
                },
            },
            // A stored artifact + a delivery status is the shape the inbox bulk-send operates on:
            // the order was transformed at some earlier point (possibly before the rule existed) and
            // is now sitting in ready_to_deliver waiting for a human to press Send.
            OutboundArtifacts = withArtifact
                ? new List<OutboundArtifact>
                  {
                      new()
                      {
                          Id = artifactId, OrderId = orderId, OrgId = orgId, Format = "csv",
                          FileKey = $"{orgId}/{orderId}/artifacts/{artifactId}.csv", CreatedAt = now,
                      },
                  }
                : new List<OutboundArtifact>(),
        });

        db.SupplierAcceptanceProfiles.Add(new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = 1, Status = "active", CreatedAt = now, EffectiveFrom = now,
            Rules =
            {
                new SupplierAcceptanceRule
                {
                    Id = Guid.NewGuid(), Scope = "order", FieldPath = "currency", Operator = "equals",
                    ExpectedValue = "EUR", Severity = severity, BlockOnFail = false,
                },
            },
        });

        await db.SaveChangesAsync();
        return new Seeded(orgId, orderId);
    }
}
