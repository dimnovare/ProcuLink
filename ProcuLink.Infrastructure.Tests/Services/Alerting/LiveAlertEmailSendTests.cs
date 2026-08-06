using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Services.Alerting;
using ProcuLink.Infrastructure.Services.Alerting;
using ProcuLink.Infrastructure.Services.Email;
using ProcuLink.TestSupport;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Alerting;

/// <summary>
/// WP-37 — the one test that proves a real notification actually lands. Everything else in this
/// packet captures the outbound call with a double; this one performs a genuine HTTPS send through
/// the production <see cref="PostmarkEmailApiClient"/> to a real inbox, so the founder can verify
/// the destination end-to-end rather than trusting the wiring diagram.
///
/// <para><b>Statically skipped by default</b> — it needs a live Postmark token and a real
/// recipient, neither of which belongs in the repo. Both are read from the environment and never
/// logged. Run it with:</para>
/// <code>
/// PROCULINK_LIVE_ENDPOINT_TESTS=1 \
/// PROCULINK_LIVE_POSTMARK_TOKEN=&lt;server token&gt; \
/// PROCULINK_LIVE_ALERT_EMAIL_TO=you@example.com \
/// PROCULINK_LIVE_ALERT_EMAIL_FROM=alerts@your-verified-domain \
/// dotnet test ProcuLink.Infrastructure.Tests --filter FullyQualifiedName~LiveAlertEmailSendTests
/// </code>
/// <para>It is an <c>EnvironmentGatedFact</c>, not an early <c>return</c>, so an unconfigured run
/// reports a declared SKIP with the reason — never a green no-op.</para>
/// </summary>
public class LiveAlertEmailSendTests
{
    private const string TokenVar     = "PROCULINK_LIVE_POSTMARK_TOKEN";
    private const string RecipientVar = "PROCULINK_LIVE_ALERT_EMAIL_TO";
    private const string SenderVar    = "PROCULINK_LIVE_ALERT_EMAIL_FROM";

    [EnvironmentGatedFact(
        "sends a REAL alert email through Postmark to a real inbox",
        LiveTestEnvironment.EndpointOptIn,
        TokenVar, RecipientVar, SenderVar)]
    public async Task AlertAsync_reallyDeliversAnAlertEmail()
    {
        var recipient = Environment.GetEnvironmentVariable(RecipientVar)!;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Postmark:ServerToken"] = Environment.GetEnvironmentVariable(TokenVar),
                ["Email:Postmark:From"]        = Environment.GetEnvironmentVariable(SenderVar),
            })
            .Build();

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(PostmarkEmailApiClient.HttpClientName))
               .Returns(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

        var client = new PostmarkEmailApiClient(
            config, factory.Object, NullLogger<PostmarkEmailApiClient>.Instance);

        client.IsConfigured.Should().BeTrue(
            $"the live send needs a real token in {TokenVar}");

        var sink = new EmailWorkerAlertSink(
            client,
            new AlertingEmailOptions { To = recipient, SubjectPrefix = "[ProcuLink alert · staged test]" },
            NullLogger<EmailWorkerAlertSink>.Instance);

        var stamp = DateTime.UtcNow.ToString("O");

        await sink.AlertAsync(
            OperationalAlertKeys.WorkerHeartbeatLost,
            $"STAGED TEST — this is not a real incident. Sent at {stamp} by LiveAlertEmailSendTests.");

        // EmailWorkerAlertSink swallows provider failures by contract, so asserting on the sink
        // alone would pass even if Postmark refused. Re-send the same message through the client
        // directly and assert the provider's own verdict — that is the part that proves delivery.
        var direct = await client.SendAsync(new Core.Services.Email.EmailApiMessage(
            From:     client.DefaultFrom,
            To:       new[] { recipient },
            Subject:  "[ProcuLink alert · staged test] delivery receipt probe",
            TextBody: $"Provider-verdict probe for the staged alert sent at {stamp}."));

        direct.Success.Should().BeTrue(
            $"Postmark refused the live send: {direct.StatusCode} {direct.Error}");
    }
}
