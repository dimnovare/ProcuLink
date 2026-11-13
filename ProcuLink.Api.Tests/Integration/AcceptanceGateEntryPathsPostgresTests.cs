using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// WP-17 — a blocked order refuses to transform through the job door, on real Postgres.
///
/// <para><b>What is left after plan 13b.</b> The order API is a read-only archive: the browser send
/// (<c>POST /api/orders/{id}/transform</c>) and the inbox bulk send
/// (<c>POST /api/orders/{id}/redeliver</c>) were deleted with it, and their facts went with them.
/// What remains is the door they all led to — <c>TransformOrderJob</c> →
/// <c>OrderTransformService.TransformAsync</c> → the acceptance gate — driven exactly as Hangfire
/// runs it, for the REST-ingress and inbound-email shapes of order.</para>
///
/// <para><b>Why Postgres.</b> The transform claims the order with <c>ExecuteUpdateAsync</c>, which
/// EF InMemory cannot translate (it takes a fallback), so only here does the claim run the way
/// production runs it.</para>
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
public sealed class AcceptanceGateEntryPathsPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private string? _databaseConnectionString;
    private DbContextOptions<ProcuLinkDbContext>? _options;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null) return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_gate");

        var cs = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString) { Pooling = false }.ConnectionString;
        _options = new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(cs).Options;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    private ProcuLinkDbContext NewContext() => new(_options!);

    // ── The entry paths ───────────────────────────────────────────────────────

    /// <summary>
    /// An order pushed in over REST ingress (<c>plk_</c> API key). It reaches the transform
    /// through the Hangfire job, with no browser and no controller in the loop.
    /// </summary>
    [DockerRequiredFact]
    public async Task RestIngressOrder_isRefused()
    {
        var order = await SeedBlockedOrderAsync("rest-ingress", sourceFileKey: "ingress/rest/po.csv");
        AssertRefused(await SendThroughTheJobAsync(order));
    }

    /// <summary>
    /// An order that arrived as inbound email ({slug}@orders.proculink.eu). Same job door,
    /// no human in the loop at all, which is exactly the case the browser-only enforcement missed.
    /// </summary>
    [DockerRequiredFact]
    public async Task EmailIngestOrder_isRefused()
    {
        var order = await SeedBlockedOrderAsync("email-ingest",
            sourceFileKey: "ingress/email/po.csv", inboundSenderDomain: "acme.example");
        AssertRefused(await SendThroughTheJobAsync(order));
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
        var blocked = await SendThroughTheJobAsync(await SeedBlockedOrderAsync("control-usd", currency: "USD"));
        var allowed = await SendThroughTheJobAsync(await SeedBlockedOrderAsync("control-eur", currency: "EUR"));

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
        var blocked = await SendThroughTheJobAsync(
            await SeedBlockedOrderAsync("sev-error", severity: "error"));
        var allowed = await SendThroughTheJobAsync(
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

        var refused = await SendThroughTheJobAsync(order);
        Assert.Equal(OrderStatusConstants.TransformFailed, refused.Status);

        await using (var db = NewContext())
        {
            var gate = new AcceptanceGate(db, new SupplierAcceptanceService(db));
            var result = await gate.RecordOverrideAsync(
                order.OrgId, order.OrderId, "user_2opsLead",
                "Supplier confirmed they will accept USD for this PO (ticket TCK-4412).", CancellationToken.None);
            Assert.True(result.Recorded, result.Error);
        }

        var sent = await SendThroughTheJobAsync(order);
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
        Assert.Equal(OrderStatusConstants.TransformFailed, (await SendThroughTheJobAsync(order)).Status);

        await using (var db = NewContext())
        {
            var po = await db.PurchaseOrders.FirstAsync(o => o.Id == order.OrderId && o.OrgId == order.OrgId);
            po.Currency = "EUR";
            await db.SaveChangesAsync();
        }

        var sent = await SendThroughTheJobAsync(order);
        Assert.Equal(OrderStatusConstants.ReadyToDeliver, sent.Status);
        Assert.Single(sent.Artifacts);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed record Seeded(Guid OrgId, Guid OrderId);

    private sealed record Outcome(string Status, IReadOnlyList<Guid> Artifacts, string? ErrorMessage);

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

    /// <summary>REST ingress / inbound email: the Hangfire job, with no browser in the loop.</summary>
    private async Task<Outcome> SendThroughTheJobAsync(Seeded order)
    {
        await RunTransformJobAsync(order);
        return await ReadOutcomeAsync(order);
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

    private async Task<Outcome> ReadOutcomeAsync(Seeded order)
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

        return new Outcome(status, artifacts, message);
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
        string? inboundSenderDomain = null)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
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
            OrderDate = new DateOnly(2026, 7, 30), Status = OrderStatusConstants.Ready,
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
