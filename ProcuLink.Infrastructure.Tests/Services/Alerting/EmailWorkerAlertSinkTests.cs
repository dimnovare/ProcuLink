using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure.Services.Alerting;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services.Alerting;

/// <summary>
/// WP-37 — the alert transport the founder actually watches. Verifies that a configured sink really
/// hands a message to the email API, that an UNCONFIGURED sink is a silent no-op (no recipient, or
/// no provider token) rather than a throw, and that a transport failure never escapes — a sink that
/// throws would abort the sweep and suppress every other condition in the same run.
/// </summary>
public class EmailWorkerAlertSinkTests
{
    private const string Key = OperationalAlertKeys.DeliveryFailureRate;
    private const string Message = "Delivery failure rate 80% over 60 min (8/10 attempts failed).";

    [Fact]
    public async Task AlertAsync_Configured_SendsOneEmailToTheConfiguredRecipient()
    {
        var client = new FakeEmailApiClient { IsConfigured = true, DefaultFrom = "orders@proculink.eu" };
        var sink = Sink(client, to: "founder@example.com");

        await sink.AlertAsync(Key, Message);

        client.LastMessage.Should().NotBeNull();
        client.LastMessage!.To.Should().Equal("founder@example.com");
        client.LastMessage.From.Should().Be("orders@proculink.eu");
        client.LastMessage.TextBody.Should().Contain(Message);
    }

    [Fact]
    public async Task AlertAsync_SubjectCarriesPrefixAndAlertKey()
    {
        var client = new FakeEmailApiClient { IsConfigured = true };
        var sink = Sink(client, to: "founder@example.com");

        await sink.AlertAsync(Key, Message);

        client.LastMessage!.Subject.Should().StartWith("[ProcuLink alert]");
        client.LastMessage.Subject.Should().Contain(Key);
    }

    [Fact]
    public async Task AlertAsync_MultipleRecipients_AreSplitAndTrimmed()
    {
        var client = new FakeEmailApiClient { IsConfigured = true };
        var sink = Sink(client, to: " founder@example.com ; ops@example.com ");

        await sink.AlertAsync(Key, Message);

        client.LastMessage!.To.Should().Equal("founder@example.com", "ops@example.com");
    }

    [Fact]
    public async Task AlertAsync_NoRecipientConfigured_IsASilentNoOp()
    {
        var client = new FakeEmailApiClient { IsConfigured = true };
        var sink = Sink(client, to: null);

        var act = async () => await sink.AlertAsync(Key, Message);

        await act.Should().NotThrowAsync();
        client.LastMessage.Should().BeNull("an unconfigured alert destination must never send");
    }

    [Fact]
    public async Task AlertAsync_EmailProviderNotConfigured_IsASilentNoOp()
    {
        var client = new FakeEmailApiClient { IsConfigured = false };
        var sink = Sink(client, to: "founder@example.com");

        var act = async () => await sink.AlertAsync(Key, Message);

        await act.Should().NotThrowAsync();
        client.LastMessage.Should().BeNull("no provider token means nothing can be transmitted");
    }

    [Fact]
    public async Task AlertAsync_TransportThrows_DoesNotEscape()
    {
        var sink = new EmailWorkerAlertSink(
            new ThrowingEmailApiClient(),
            new AlertingEmailOptions { To = "founder@example.com" },
            NullLogger<EmailWorkerAlertSink>.Instance);

        var act = async () => await sink.AlertAsync(Key, Message);

        await act.Should().NotThrowAsync(
            "a throwing alert transport would abort the sweep and suppress every other condition");
    }

    [Fact]
    public async Task AlertAsync_ProviderReturnsFailure_DoesNotThrow()
    {
        var client = new FakeEmailApiClient
        {
            IsConfigured = true,
            ResultToReturn = new EmailApiResult(false, "422 inactive recipient", 422),
        };
        var sink = Sink(client, to: "founder@example.com");

        var act = async () => await sink.AlertAsync(Key, Message);

        await act.Should().NotThrowAsync();
        client.LastMessage.Should().NotBeNull("the send was attempted; only the provider refused");
    }

    // ── Reporting whether the operator was actually reached ──────────────────────

    [Fact]
    public async Task AlertAsync_Configured_ReportsDelivered()
    {
        var client = new FakeEmailApiClient { IsConfigured = true };

        (await Sink(client, to: "founder@example.com").AlertAsync(Key, Message)).Should().BeTrue();
    }

    [Fact]
    public async Task AlertAsync_NoRecipientConfigured_ReportsNotDelivered()
    {
        var client = new FakeEmailApiClient { IsConfigured = true };

        (await Sink(client, to: null).AlertAsync(Key, Message)).Should().BeFalse(
            "a silent no-op must be reported as 'nobody was told', not as a delivered alert");
    }

    [Fact]
    public async Task AlertAsync_EmailProviderNotConfigured_ReportsNotDelivered()
    {
        var client = new FakeEmailApiClient { IsConfigured = false };

        (await Sink(client, to: "founder@example.com").AlertAsync(Key, Message)).Should().BeFalse(
            "an alert address with no Email:Postmark:ServerToken behind it reaches nobody");
    }

    [Fact]
    public async Task AlertAsync_ProviderRefused_ReportsNotDelivered()
    {
        var client = new FakeEmailApiClient
        {
            IsConfigured = true,
            ResultToReturn = new EmailApiResult(false, "422 inactive recipient", 422),
        };

        (await Sink(client, to: "founder@example.com").AlertAsync(Key, Message)).Should().BeFalse();
    }

    [Fact]
    public async Task AlertAsync_TransportThrows_ReportsNotDelivered()
    {
        var sink = new EmailWorkerAlertSink(
            new ThrowingEmailApiClient(),
            new AlertingEmailOptions { To = "founder@example.com" },
            NullLogger<EmailWorkerAlertSink>.Instance);

        (await sink.AlertAsync(Key, Message)).Should().BeFalse();
    }

    // ── Leaving a trace of a DELIVERED alert ─────────────────────────────────────
    //
    // Failures logged; successes were silent. A delivered operational alert therefore left no
    // record anywhere in ProcuLink, and "did that alert actually go out?" could only be answered
    // by opening Postmark and reading the outbound list by hand — with no identifier to look up.

    [Fact]
    public async Task AlertAsync_Delivered_LogsTheAlertKeyAndTheProviderMessageId()
    {
        var log = new CapturingLogger();
        var client = new FakeEmailApiClient
        {
            IsConfigured = true,
            ResultToReturn = new EmailApiResult(true, null, 200, MessageId: "b7bc2f4a-e38e-4336-af7d-e6c392c2f817"),
        };

        await new EmailWorkerAlertSink(
            client, new AlertingEmailOptions { To = "founder@example.com" }, log)
            .AlertAsync(Key, Message);

        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information)
           .Which.Line.Should().Contain(Key)
           .And.Contain("b7bc2f4a-e38e-4336-af7d-e6c392c2f817");
    }

    [Fact]
    public async Task AlertAsync_Delivered_LogsTheRecipientDomainOnly_NeverTheAddress()
    {
        var log = new CapturingLogger();
        var client = new FakeEmailApiClient { IsConfigured = true };

        await new EmailWorkerAlertSink(
            client, new AlertingEmailOptions { To = "founder@example.com" }, log)
            .AlertAsync(Key, Message);

        var line = log.Entries.Single(e => e.Level == LogLevel.Information).Line;
        line.Should().Contain("example.com", "the destination domain is the routing evidence");
        line.Should().NotContain("founder@example.com",
            "the local part identifies a PERSON — InboundEmailRouter.ExtractSenderDomain is the "
          + "privacy boundary this log reuses, and logs are a Sentry breadcrumb surface");
    }

    [Fact]
    public async Task AlertAsync_ProviderRefused_LogsNoSuccessLine()
    {
        var log = new CapturingLogger();
        var client = new FakeEmailApiClient
        {
            IsConfigured = true,
            ResultToReturn = new EmailApiResult(false, "422 inactive recipient", 422),
        };

        await new EmailWorkerAlertSink(
            client, new AlertingEmailOptions { To = "founder@example.com" }, log)
            .AlertAsync(Key, Message);

        log.Entries.Should().NotContain(e => e.Level == LogLevel.Information,
            "a refused alert must never leave a line that reads like a delivery");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Captures the fully rendered log line — exactly what reaches stdout and Sentry.</summary>
    private sealed class CapturingLogger : ILogger<EmailWorkerAlertSink>
    {
        public List<(LogLevel Level, string Line)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                                Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }


    private static EmailWorkerAlertSink Sink(IEmailApiClient client, string? to) =>
        new(client, new AlertingEmailOptions { To = to }, NullLogger<EmailWorkerAlertSink>.Instance);

    private sealed class ThrowingEmailApiClient : IEmailApiClient
    {
        public bool IsConfigured => true;
        public string DefaultFrom => "orders@proculink.eu";
        public Task<EmailApiResult> SendAsync(EmailApiMessage message, CancellationToken ct = default) =>
            throw new HttpRequestException("simulated transport failure");
    }
}
