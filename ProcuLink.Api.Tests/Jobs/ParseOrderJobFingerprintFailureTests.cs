using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// F2's second half: the contract of the <c>try/catch</c> around the fingerprint recorder.
///
/// <para>That catch is why F2 was invisible for weeks — the fingerprint threw on every file-backed
/// assign-supplier re-parse and the job reported success. The catch is still RIGHT: the parse is
/// what the order needs, and it has already committed by the time the recorder runs. Throwing here
/// would report a red job for an order that is correctly parsed, routed and ready to deliver, and
/// would burn the Hangfire retries on a re-entry the <c>status != 'parsing'</c> guard turns into a
/// no-op. A failed LEARN must not fail the PARSE.</para>
///
/// <para>What must never regress is the other half: the failure has to stay VISIBLE. These pin
/// both — the job survives, and the exception reaches the log at Warning or above with the order id
/// and the exception object attached, not a bare message. Written after the fix as a deliberate
/// characterisation of behaviour that is being KEPT, so they passed on their first run; the
/// production change in this PR is the detach, not the catch.</para>
/// </summary>
public class ParseOrderJobFingerprintFailureTests
{
    private static ProcuLinkDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static (ParseOrderJob Job, CapturingLogger Log) NewJob(
        ProcuLinkDbContext db, Guid orgId, Guid orderId, Exception fingerprintFailure)
    {
        var orders = new Mock<IOrderService>();
        orders.Setup(s => s.ParseStoredFileAsync(orgId, orderId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<ParsedFileOutput>.Ok(new ParsedFileOutput(
                  new PurchaseOrderEntity { Id = orderId, OrgId = orgId, Status = OrderStatusConstants.Ready },
                  new[] { "po_number", "sku" },
                  "csv")));

        var fingerprints = new Mock<ISchemaFingerprintService>();
        fingerprints
            .Setup(s => s.RecordParseSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(fingerprintFailure);

        var log = new CapturingLogger();
        return (new ParseOrderJob(orders.Object, log, db, new FakeAnalyticsService(), fingerprints.Object), log);
    }

    [Fact]
    public async Task FingerprintThrows_ParseIsStillReportedSuccessful()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await using var db = NewDb();

        var failure = new DbUpdateConcurrencyException(
            "Database operation expected to affect 1 row(s), but actually affected 0 row(s).");
        var (job, log) = NewJob(db, orgId, orderId, failure);

        var act = async () => await job.ExecuteAsync(orderId, orgId, CancellationToken.None);

        // No throw = Hangfire marks the job Succeeded, which is correct: the parse committed and the
        // order is resolved. A red job here would be a lie about the order's actual state.
        await act.Should().NotThrowAsync(
            "a failed LEARN must not fail the PARSE — throwing would mark a correctly parsed, routed, "
            + "deliverable order as a failed job and burn the Hangfire retries on a no-op re-entry");

        // The precondition, pinned: this test only says anything about fingerprint failure if the
        // recorder actually threw. Without this, a ParseOrderJob that stopped calling
        // RecordParseSuccessAsync entirely would still go green here.
        log.Entries.Should().Contain(e => ReferenceEquals(e.Exception, failure),
            "the injected fingerprint failure has to reach the job's catch for this to be the F2 scenario");

        // "Reported successful" is the job's OWN report of the outcome, not merely the absence of an
        // exception: it reached the completion line and announced the parsed status. Both failure
        // paths — a Fail Result and the terminal-status guard — log at Error immediately before they
        // throw, so an Error entry here would mean the job reported the parse as failed.
        log.Entries.Should().Contain(
            e => e.Level == LogLevel.Information
              && e.Message.Contains("completed")
              && e.Message.Contains(orderId.ToString())
              && e.Message.Contains(OrderStatusConstants.Ready),
            $"the job must report the order as parsed to '{OrderStatusConstants.Ready}', which is the "
            + "status the fingerprint failure must not have disturbed");

        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Error,
            "an Error entry is the job reporting a failed parse — the only failure here was the "
            + "non-fatal fingerprint recorder, which logs at Warning");
    }

    [Fact]
    public async Task FingerprintThrows_TheFailureIsLoggedAtWarningOrAbove_WithTheExceptionAndOrderId()
    {
        var orgId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await using var db = NewDb();

        var failure = new DbUpdateConcurrencyException(
            "Database operation expected to affect 1 row(s), but actually affected 0 row(s).");
        var (job, log) = NewJob(db, orgId, orderId, failure);

        await job.ExecuteAsync(orderId, orgId, CancellationToken.None);

        var reported = log.Entries.Should().ContainSingle(e => e.Level >= LogLevel.Warning,
            "a swallowed fingerprint failure that logs below Warning is indistinguishable from success")
            .Subject;

        // The exception OBJECT, not just a message: F2 was diagnosed from the
        // DbUpdateConcurrencyException type and its row count, neither of which survives
        // interpolation into a bare string.
        reported.Exception.Should().BeSameAs(failure);
        reported.Message.Should().Contain(orderId.ToString());
    }

    /// <summary>Minimal <see cref="ILogger{T}"/> that records what was logged, so a test can assert on
    /// the LEVEL and the attached exception rather than only on the fact that nothing threw.</summary>
    private sealed class CapturingLogger : ILogger<ParseOrderJob>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
