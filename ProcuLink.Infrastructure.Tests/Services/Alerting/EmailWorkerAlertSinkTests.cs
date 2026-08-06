using FluentAssertions;
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

    // ── Helpers ──────────────────────────────────────────────────────────────────

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
