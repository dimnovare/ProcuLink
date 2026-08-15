using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Startup validation of the OUTBOUND EMAIL TRANSPORT — the one the supplier's purchase order
/// travels on, as distinct from the operator-alert routing covered by
/// <see cref="AlertingDestinationStartupValidationTests"/>.
///
/// <para><b>The gap this closes.</b> <c>Email:Postmark:ServerToken</c> was labelled optional, and
/// the only hard failure that mentioned it fired on a DIFFERENT key — <c>Alerting:Email:To</c>. A
/// Production deploy that configured <c>Sentry:Dsn</c> and no alert address therefore booted
/// completely clean with no email transport whatsoever, and every purchase order on the offered
/// <c>email</c> delivery channel then failed one supplier at a time inside
/// <c>EmailApiDeliveryDispatcher</c>. Nothing at startup said so, and on the Worker — which serves
/// no HTTP and has no readiness surface — nothing ever would.</para>
///
/// <para><b>What must stay true.</b> The check is unconditional on the <c>Validate</c> seam: it is
/// not behind <c>raisesOperatorAlerts</c>, not behind the required-key list a host passes, and not
/// satisfied by any neighbouring key. Each of those is a way the original defect comes back
/// wearing different clothes, so each has a test below.</para>
/// </summary>
public class OutboundEmailTransportStartupValidationTests
{
    private const string Sentry = "https://k@o0.ingest.example.com/1";

    [Fact]
    public void Production_noProviderToken_refusesToStart()
    {
        var act = () => ValidateWorker(Config(), "Production");

        act.Should().Throw<StartupConfigurationException>(
                "a host with no outbound email transport cannot deliver an 'email'-channel purchase "
              + "order, and it fails per-send on a process that started up looking healthy")
            .Which.MissingKeys.Should().Contain(StartupConfigurationValidator.EmailProviderTokenKey);
    }

    /// <summary>
    /// The refusal must name the environment-variable spelling a deployer actually sets, and the
    /// declared-incapacity escape hatch. A refusal that lists a colon-separated key and no way
    /// forward is how a hard-fail gets deleted rather than satisfied.
    /// </summary>
    [Fact]
    public void Production_refusal_tellsTheDeployerBothWaysOut()
    {
        var act = () => ValidateWorker(Config(), "Production");

        var message = act.Should().Throw<StartupConfigurationException>().Which.Message;

        message.Should().Contain("EMAIL__POSTMARK__SERVERTOKEN");
        message.Should().Contain("DELIVERY__ALLOWNOEMAILCHANNEL");
    }

    /// <summary>
    /// The anti-drift assertion, and the reason this check does not take a host-supplied flag.
    /// <c>raisesOperatorAlerts: false</c> IS the API's call shape (<c>ProcuLink.Api/Program.cs</c>
    /// omits the argument). Both hosts register <c>EmailApiDeliveryDispatcher</c> unconditionally,
    /// so both can be handed an email delivery they cannot perform — a check only the Worker got
    /// would leave the API booting clean into the same incapacity.
    /// </summary>
    [Fact]
    public void Production_apiCallShape_isRefusedToo_notJustTheAlertRaisingWorker()
    {
        var act = () => StartupConfigurationValidator.Validate(
            Config(requiredKeys: StartupConfigurationValidator.ApiRequiredKeys),
            new RecordingLogger(),
            "Production",
            StartupConfigurationValidator.ApiRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "ProcuLink.Api",
            raisesOperatorAlerts: false);

        act.Should().Throw<StartupConfigurationException>(
            "this check rides the Validate seam with no parameter precisely so a host cannot opt "
          + "out of it by forgetting an argument");
    }

    [Fact]
    public void Production_withProviderToken_starts()
    {
        var log = new RecordingLogger();

        var act = () => ValidateWorker(Config(postmarkToken: "pm-token"), "Production", log);

        act.Should().NotThrow("a configured email transport is exactly what Production asks for");

        // Did not throw because it PASSED, not because it never looked: a working transport is
        // silent, and in particular does not log the declared-incapacity Error.
        log.Entries.Should().NotContain(
            e => e.Message.Contains(StartupConfigurationValidator.AllowNoEmailChannelKey),
            "nothing was declared unavailable here");
    }

    /// <summary>
    /// The legitimate deployment: a self-hosted operator with genuinely no email channel. It boots
    /// — but the incapacity is stated at Error, in every environment, because on the Worker a log
    /// line is the only surface that exists.
    /// </summary>
    [Fact]
    public void Production_declaredNoEmailChannel_starts_butSaysSoAtErrorLevel()
    {
        var log = new RecordingLogger();

        var act = () => ValidateWorker(Config(allowNoEmailChannel: true), "Production", log);

        act.Should().NotThrow("a declared absence is a decision, not an accident");

        log.Entries.Should().Contain(
            e => e.Level == LogLevel.Error
                 && e.Message.Contains("CANNOT SEND EMAIL")
                 && e.Message.Contains(StartupConfigurationValidator.EmailProviderTokenKey),
            "the escape hatch must be loud — a silent opt-out is the same non-signal as the "
          + "optional-key warning this check replaced");
    }

    /// <summary>
    /// The defect being fixed, rebuilt with a different pair of keys — and rejected.
    /// <c>Delivery:EnableSmtp</c> is the nearest neighbouring key and is also "email", so it is the
    /// obvious thing for a later change to treat as satisfying this gate. It must not:
    /// <c>SmtpDeliveryDispatcher.Protocol</c> is <c>smtp</c> while
    /// <c>EmailApiDeliveryDispatcher.Protocol</c> is <c>email</c>, and <c>DeliveryService</c> keys
    /// dispatchers by protocol — so a supplier saved on the offered <c>email</c> channel is served
    /// by neither. Making a gate conditional on a different key is exactly how B-2 happened.
    /// </summary>
    [Fact]
    public void Production_enablingLegacySmtp_doesNotSatisfyTheEmailChannelGate()
    {
        var config = Config(extra: new (string, string?)[] { ("Delivery:EnableSmtp", "true") });

        var act = () => ValidateWorker(config, "Production");

        act.Should().Throw<StartupConfigurationException>(
            "the retired 'smtp' dispatcher does not serve the offered 'email' channel, so enabling "
          + "it delivers nothing for an email-channel supplier — accepting it here would rebuild "
          + "the original 'gated on a different key' defect");
    }

    [Fact]
    public void NonProduction_noProviderToken_warnsButStarts()
    {
        var log = new RecordingLogger();

        var act = () => ValidateWorker(Config(), "Development", log);

        act.Should().NotThrow("local runs must not need a Postmark token");
        log.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning
                 && e.Message.Contains(StartupConfigurationValidator.EmailProviderTokenKey)
                 && e.Message.Contains("fail-fast in Production"));
    }

    /// <summary>
    /// Directional guard. The token spent its life in <see cref="StartupConfigurationValidator.OptionalKeys"/>,
    /// where its absence produced one warning among several and blocked nothing. Putting it back
    /// there would restore the exact defect while leaving every test above green, because a
    /// duplicate optional-key warning changes no control flow.
    /// </summary>
    [Fact]
    public void TheProviderToken_isNotListedAsAnOptionalKey()
    {
        StartupConfigurationValidator.OptionalKeys.Should().NotContain(
            StartupConfigurationValidator.EmailProviderTokenKey,
            "'optional' is what this key was called while a Sentry-only Production deploy booted "
          + "clean with every emailed purchase order dead; it is governed by "
          + "ValidateOutboundEmailTransport now");

        // Anti-vacuity: the list is really populated, so NotContain is a claim about this key
        // rather than about an empty collection.
        StartupConfigurationValidator.OptionalKeys.Should().Contain("Sentry:Dsn");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static void ValidateWorker(
        IConfiguration configuration, string environmentName, ILogger? logger = null) =>
        StartupConfigurationValidator.Validate(
            configuration,
            logger ?? new RecordingLogger(),
            environmentName,
            StartupConfigurationValidator.WorkerRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "ProcuLink.Worker",
            // Sentry alone satisfies the ALERTING rule (Config sets a DSN), so any throw below
            // can only come from the outbound-email rule under test.
            raisesOperatorAlerts: true);

    /// <summary>
    /// A configuration that is complete apart from the email transport under test — including a
    /// Sentry DSN, so the operator-alert rule is satisfied and cannot be the source of a throw.
    /// </summary>
    private static IConfiguration Config(
        string? postmarkToken = null,
        bool allowNoEmailChannel = false,
        IReadOnlyList<string>? requiredKeys = null,
        (string Key, string? Value)[]? extra = null)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var key in requiredKeys ?? StartupConfigurationValidator.WorkerRequiredKeys)
            dict[key] = "configured-value";

        dict["Delivery:EncryptionKey"] = Convert.ToBase64String(SequentialBytes(32));
        dict["DataProtection:EncryptionKey"] = Convert.ToBase64String(SequentialBytes(32));
        dict["Security:ApiKeyHashSecret"] = "a-sufficiently-long-secret-value";
        dict["Sentry:Dsn"] = Sentry;
        dict[StartupConfigurationValidator.EmailProviderTokenKey] = postmarkToken;
        if (allowNoEmailChannel)
            dict[StartupConfigurationValidator.AllowNoEmailChannelKey] = "true";

        foreach (var (key, value) in extra ?? Array.Empty<(string, string?)>())
            dict[key] = value;

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
