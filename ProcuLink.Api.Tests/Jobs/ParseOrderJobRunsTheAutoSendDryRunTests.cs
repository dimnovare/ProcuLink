using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// WP-33 stage 1 — <b>something actually calls the evaluator.</b>
///
/// <para>The recurring defect in this repo is a unit test that pins a function while nothing pins
/// that anything calls it: the gate table verified by a hand-typed string, the billing guard that
/// stayed green after its enforcement was deleted. <c>AutoSendDryRunPostgresTests</c> proves the
/// decision is correct; it would go on proving that after someone deleted the call site, and the
/// feature would simply never run in production.</para>
///
/// <para>So this drives the real <see cref="ParseOrderJob.ExecuteAsync"/> and asserts the
/// evaluation happened, with the right arguments, on the successful parse path — and that it did
/// NOT happen when the parse failed.</para>
/// </summary>
public sealed class ParseOrderJobRunsTheAutoSendDryRunTests
{
    [Fact]
    public async Task A_successful_parse_evaluates_the_order_for_auto_send()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var autoSend = new Mock<IAutoSendDryRunEvaluator>();
        autoSend
            .Setup(a => a.EvaluateAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AutoSendDryRunOutcome(false, false, AutoSendDecision.AutoTransformOff));

        var job = BuildJob(orgId, orderId, OrderStatusConstants.Ready, autoSend, out _);

        await job.ExecuteAsync(orderId, orgId, CancellationToken.None);

        autoSend.Verify(
            a => a.EvaluateAsync(orgId, orderId, It.IsAny<CancellationToken>()),
            Times.Once,
            "parse completion is where an order first becomes sendable, so it is where the auto-send "
          + "decision has to be made — if nothing calls the evaluator, the whole packet is inert.");
    }

    /// <summary>
    /// A parse that failed produced no sendable order, and the job throws before reaching the
    /// evaluation. Pinned so the dry-run data never contains decisions about orders that never
    /// parsed.
    /// </summary>
    [Fact]
    public async Task A_failed_parse_evaluates_nothing()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var autoSend = new Mock<IAutoSendDryRunEvaluator>();
        var job = BuildJob(orgId, orderId, OrderStatusConstants.Failed, autoSend, out _);

        await Assert.ThrowsAnyAsync<Exception>(
            () => job.ExecuteAsync(orderId, orgId, CancellationToken.None));

        autoSend.Verify(
            a => a.EvaluateAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The evaluation is bookkeeping, and bookkeeping must not fail a parse that succeeded. If it
    /// threw, Hangfire would retry a perfectly good parse three times for a reason unrelated to
    /// parsing — and the same swallow is what makes the job's retries safe for the evaluator too.
    /// </summary>
    [Fact]
    public async Task A_failing_evaluation_does_not_fail_the_parse()
    {
        var orgId   = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var autoSend = new Mock<IAutoSendDryRunEvaluator>();
        autoSend
            .Setup(a => a.EvaluateAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dry-run bookkeeping blew up"));

        var job = BuildJob(orgId, orderId, OrderStatusConstants.Ready, autoSend, out _);

        // No throw: the parse succeeded, and it stays succeeded.
        await job.ExecuteAsync(orderId, orgId, CancellationToken.None);

        autoSend.Verify(
            a => a.EvaluateAsync(orgId, orderId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static ParseOrderJob BuildJob(
        Guid orgId, Guid orderId, string parsedStatus,
        Mock<IAutoSendDryRunEvaluator> autoSend, out ProcuLinkDbContext db)
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase($"parsejob-autosend-{Guid.NewGuid():N}")
            .Options;

        db = new ProcuLinkDbContext(options);

        var entity = new PurchaseOrderEntity
        {
            Id        = orderId,
            OrgId     = orgId,
            PoNumber  = "PO-WIRING-1",
            Currency  = "EUR",
            Status    = parsedStatus,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var orders = new Mock<IOrderService>();
        orders
            .Setup(o => o.ParseStoredFileAsync(orgId, orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ParsedFileOutput>.Ok(new ParsedFileOutput(entity, null, "csv")));

        return new ParseOrderJob(
            orders.Object,
            NullLogger<ParseOrderJob>.Instance,
            db,
            Mock.Of<IAnalyticsService>(),
            Mock.Of<ISchemaFingerprintService>(),
            autoSend.Object);
    }
}
