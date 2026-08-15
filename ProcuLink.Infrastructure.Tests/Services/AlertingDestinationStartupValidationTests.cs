using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Startup validation of the operator-alert DESTINATION on the host that raises the alerts.
///
/// <para><b>The gap this closes.</b> <c>Alerting:Email:To</c> was validated (a warning if absent);
/// <c>Email:Postmark:ServerToken</c>, which that address cannot work without, was in neither the
/// required nor the optional list. Setting only the address produced a completely silent startup
/// and one unread <c>LogWarning</c> per alert thereafter — the alarm looked configured and reached
/// nobody. And with the address and <c>Sentry:Dsn</c> BOTH unset, every condition the sweep
/// evaluates is delivered into a no-op.</para>
///
/// <para><b>Why fail-fast rather than log.</b> This is the one failure in the alerting stack that
/// cannot be reported at runtime: with no destination there is, by construction, nowhere to report
/// it to. Configuration does not change by itself, so the failure surfaces at deploy — in front of
/// the person deploying — instead of during the incident it was supposed to catch. Same pattern the
/// validator already uses for <c>DataProtection:EncryptionKey</c>.</para>
/// </summary>
public class AlertingDestinationStartupValidationTests
{
    [Fact]
    public void Production_alertRaisingHostWithNoDestinationAtAll_refusesToStart()
    {
        var act = () => Validate(Config(), "Production");

        act.Should().Throw<StartupConfigurationException>()
            .Which.Message.Should().Contain("ALERTING__EMAIL__TO").And.Contain("SENTRY__DSN");
    }

    [Fact]
    public void Production_sentryOnly_starts()
    {
        // The provider token is present so this test can only fail on the ALERTING rule. Without
        // it the host is refused by ValidateOutboundEmailTransport instead — a real refusal, but
        // not this test's claim, and it would report "Sentry alone is not enough" for the wrong
        // reason.
        var act = () => Validate(
            Config(sentryDsn: "https://k@o0.ingest.example.com/1", postmarkToken: "pm-token"),
            "Production");

        act.Should().NotThrow("Sentry alone still reaches the operator");
    }

    [Fact]
    public void Production_emailWithItsProviderToken_starts()
    {
        var act = () => Validate(
            Config(alertEmailTo: "founder@example.com", postmarkToken: "pm-token"), "Production");

        act.Should().NotThrow();
    }

    [Fact]
    public void Production_emailAddressWithNoProviderToken_refusesToStart()
    {
        var act = () => Validate(Config(alertEmailTo: "founder@example.com"), "Production");

        act.Should().Throw<StartupConfigurationException>(
                "an alert address with no provider behind it is the configuration that looks "
              + "complete and delivers nothing")
            .Which.Message.Should().Contain("EMAIL__POSTMARK__SERVERTOKEN");
    }

    [Fact]
    public void Production_emailAddressWithNoProviderToken_refusesEvenWhenSentryWorks()
    {
        var act = () => Validate(
            Config(alertEmailTo: "founder@example.com", sentryDsn: "https://k@o0.ingest.example.com/1"),
            "Production");

        act.Should().Throw<StartupConfigurationException>(
            "a declared route that cannot work is a broken route, and nothing at runtime would "
          + "ever say so while a second transport keeps succeeding");
    }

    [Fact]
    public void Production_hostThatRaisesNoAlerts_isUnaffected()
    {
        var act = () => Validate(
            Config(postmarkToken: "pm-token"), "Production", raisesOperatorAlerts: false);

        act.Should().NotThrow("only the host running the alert sweep needs a destination");
    }

    [Fact]
    public void NonProduction_noDestination_warnsButStarts()
    {
        var log = new RecordingLogger();

        var act = () => Validate(Config(), "Development", logger: log);

        act.Should().NotThrow("local runs must not need alerting secrets");
        log.Entries.Should().Contain(e => e.Message.Contains("ALERTING__EMAIL__TO"));
    }

    /// <summary>
    /// This test used to assert the opposite — that an absent provider token with no alert address
    /// produced a startup WARNING and the host came up. That warning was the whole defect: a
    /// Sentry-only Production deploy booted clean while every emailed purchase order was dead,
    /// because the only hard failure touching the token was gated on <c>Alerting:Email:To</c>, a
    /// different key entirely. The token is no longer an optional-key gap; it is a boot refusal
    /// owned by <c>ValidateOutboundEmailTransport</c>. Kept here, inverted, so the old behaviour
    /// cannot come back by someone restoring a deleted test.
    /// </summary>
    [Fact]
    public void MissingProviderToken_refusesProduction_evenWithNoAlertAddressAndAHealthySentry()
    {
        var act = () => Validate(
            Config(sentryDsn: "https://k@o0.ingest.example.com/1"), "Production");

        act.Should().Throw<StartupConfigurationException>(
                "the alerting route is fine here — it is SUPPLIER email that has no transport, and "
              + "a warning about it reaches nobody on a host that serves no HTTP")
            .Which.MissingKeys.Should().Contain(StartupConfigurationValidator.EmailProviderTokenKey);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static void Validate(
        IConfiguration configuration,
        string environmentName,
        bool raisesOperatorAlerts = true,
        ILogger? logger = null) =>
        StartupConfigurationValidator.Validate(
            configuration,
            logger ?? new RecordingLogger(),
            environmentName,
            StartupConfigurationValidator.WorkerRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "ProcuLink.Worker",
            raisesOperatorAlerts);

    /// <summary>
    /// A Worker configuration that is complete apart from the alerting keys under test, so a throw
    /// can only come from the alerting rules and never from an unrelated missing key.
    /// </summary>
    private static IConfiguration Config(
        string? alertEmailTo = null,
        string? postmarkToken = null,
        string? sentryDsn = null)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var key in StartupConfigurationValidator.WorkerRequiredKeys)
            dict[key] = "configured-value";

        dict["Delivery:EncryptionKey"] = Convert.ToBase64String(SequentialBytes(32));
        dict["DataProtection:EncryptionKey"] = Convert.ToBase64String(SequentialBytes(32));
        dict["Alerting:Email:To"] = alertEmailTo;
        dict["Email:Postmark:ServerToken"] = postmarkToken;
        dict["Sentry:Dsn"] = sentryDsn;

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static byte[] SequentialBytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i + 1);
        return b;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
