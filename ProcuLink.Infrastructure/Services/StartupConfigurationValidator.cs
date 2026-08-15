using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Fail-fast validation of required configuration keys when the host runs in
/// the <c>Production</c> environment. The API and Worker both call this after
/// <c>builder.Build()</c> but before <c>Run()</c>. Missing required keys cause
/// a single <see cref="StartupConfigurationException"/> that lists every gap;
/// missing optional keys log a warning but never block startup.
/// </summary>
public static class StartupConfigurationValidator
{
    /// <summary>
    /// Required keys for the ASP.NET API service. Postgres, Clerk, R2, Stripe,
    /// delivery encryption, the API-key hash secret, and the public frontend URL
    /// are all hard prerequisites.
    /// </summary>
    public static readonly IReadOnlyList<string> ApiRequiredKeys = new[]
    {
        "ConnectionStrings:DefaultConnection",
        "Clerk:Authority",
        "Storage:R2AccountId",
        "Storage:R2AccessKeyId",
        "Storage:R2SecretAccessKey",
        "Storage:R2Endpoint",
        "Storage:R2BucketName",
        "Stripe:SecretKey",
        "Stripe:WebhookSecret",
        "Stripe:GrowthPriceId",
        "Stripe:OperationsPriceId",
        "Stripe:IntegrationPriceId",
        // Distributor is a sold, self-serve tier (Stripe product + price exist), so its
        // monthly price ID is required in Production like the other self-serve plans.
        "Stripe:DistributorPriceId",
        "Delivery:EncryptionKey",
        "Security:ApiKeyHashSecret",
        "Frontend:Url",
    };

    /// <summary>
    /// Worker required keys — the worker does not serve HTTP, take payments, or
    /// link back to the SPA, so it skips Stripe/Frontend/Sentry hard checks.
    /// </summary>
    public static readonly IReadOnlyList<string> WorkerRequiredKeys = new[]
    {
        "ConnectionStrings:DefaultConnection",
        "Clerk:Authority",
        "Storage:R2AccountId",
        "Storage:R2AccessKeyId",
        "Storage:R2SecretAccessKey",
        "Storage:R2Endpoint",
        "Storage:R2BucketName",
        "Delivery:EncryptionKey",
    };

    /// <summary>Optional keys: missing values log a warning but do not block startup.</summary>
    public static readonly IReadOnlyList<string> OptionalKeys = new[]
    {
        "Ai:OpenAI:ApiKey",
        "Sentry:Dsn",
        // Operator alert destination (WP-37). Unset is a valid deploy — the email alert sink
        // becomes a silent no-op — but it is worth a startup warning, because with this AND
        // Sentry:Dsn both unset the five alert conditions are evaluated and reach nobody.
        // On a host that RAISES alerts, that combination is a hard failure; see
        // ValidateOperatorAlertDestination.
        "Alerting:Email:To",
        // NOTE: Email:Postmark:ServerToken used to live HERE, labelled optional. It is not
        // optional — it is the only outbound email transport this product ships with enabled, and
        // "optional" is why a Sentry-only deploy booted clean with every emailed purchase order
        // dead. It is now governed by ValidateOutboundEmailTransport, which refuses Production
        // unless a transport exists or its absence is DECLARED. Do not re-add it to this list:
        // an optional-key warning is the exact non-signal that let this ship.
        //
        // Yearly price variants are optional until annual billing is exposed in the
        // pricing UI. (The MONTHLY Distributor price is required — see ApiRequiredKeys
        // above — because Distributor is a sold, self-serve tier.)
        "Stripe:DistributorYearlyPriceId",
        "Stripe:GrowthYearlyPriceId",
        "Stripe:OperationsYearlyPriceId",
        "Stripe:IntegrationYearlyPriceId",
    };

    /// <summary>
    /// Validate <paramref name="configuration"/> against the supplied
    /// <paramref name="requiredKeys"/> and <paramref name="optionalKeys"/>.
    /// Only runs the strict required-key check when <paramref name="environmentName"/>
    /// equals <c>Production</c>; in non-production environments missing required
    /// keys still log a warning but do not throw, so dev/test envs keep their
    /// "just works without secrets" ergonomics.
    /// </summary>
    public static void Validate(
        IConfiguration configuration,
        ILogger logger,
        string environmentName,
        IReadOnlyList<string> requiredKeys,
        IReadOnlyList<string> optionalKeys,
        string componentName,
        bool raisesOperatorAlerts = false)
    {
        var isProduction = string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);

        var missingRequired = requiredKeys
            .Where(k => string.IsNullOrWhiteSpace(configuration[k]))
            .ToList();

        var missingOptional = optionalKeys
            .Where(k => string.IsNullOrWhiteSpace(configuration[k]))
            .ToList();

        foreach (var key in missingOptional)
        {
            logger.LogWarning(
                "{Component} startup: optional configuration key '{Key}' is not set; related feature will run in degraded/no-op mode.",
                componentName, key);
        }

        AnnounceRevisionAuthority(configuration, logger, componentName);

        if (raisesOperatorAlerts)
            ValidateOperatorAlertDestination(configuration, logger, environmentName, isProduction, componentName);

        // Deliberately NOT behind raisesOperatorAlerts, and deliberately not behind any other
        // host-supplied flag: both hosts register EmailApiDeliveryDispatcher unconditionally
        // (ProcuLink.Api/Program.cs, ProcuLink.Worker/Program.cs), so both can be handed an
        // 'email' delivery they cannot perform. Riding the Validate seam with no parameter is
        // what makes a third host inherit this check by existing rather than by remembering —
        // and RevisionAuthorityHostCoverageTests already asserts every declared host calls
        // Validate.
        ValidateOutboundEmailTransport(configuration, logger, environmentName, isProduction, componentName);

        // Production hardening: a PRESENT Delivery:EncryptionKey must still not be the
        // all-zero placeholder (or otherwise invalid). Absence is covered by the
        // missing-key check; this covers a present-but-insecure value, so prod can never
        // encrypt supplier credentials with a publicly-known key.
        if (isProduction && requiredKeys.Contains("Delivery:EncryptionKey"))
        {
            var keyError = ValidateEncryptionKey(configuration["Delivery:EncryptionKey"]);
            if (keyError is not null)
                throw new StartupConfigurationException(
                    $"{componentName} cannot start in Production: {keyError}",
                    new[] { "Delivery:EncryptionKey" });
        }

        // Production hardening: Security:ApiKeyHashSecret must be at least 16 characters
        // so that the HMAC key has meaningful entropy. A short/default value would let an
        // attacker with DB read access brute-force the secret.
        if (isProduction && requiredKeys.Contains("Security:ApiKeyHashSecret"))
        {
            var secretError = ValidateApiKeyHashSecret(configuration["Security:ApiKeyHashSecret"]);
            if (secretError is not null)
                throw new StartupConfigurationException(
                    $"{componentName} cannot start in Production: {secretError}",
                    new[] { "Security:ApiKeyHashSecret" });
        }

        // Production hardening: DataProtection:EncryptionKey must be set so that ASP.NET Data
        // Protection keys are encrypted at rest. If absent, the key ring XML is stored in cleartext
        // in the database — a DB read gives an attacker all session and data-protection keys.
        if (isProduction && string.IsNullOrWhiteSpace(configuration["DataProtection:EncryptionKey"]))
        {
            throw new StartupConfigurationException(
                $"{componentName} cannot start in Production without DataProtection:EncryptionKey — " +
                "ASP.NET Data Protection keys would be stored as cleartext XML in the database. " +
                "Generate a 32-byte base64 key (`openssl rand -base64 32`) and set it via " +
                "the DATAPROTECTION__ENCRYPTIONKEY environment variable.",
                new[] { "DataProtection:EncryptionKey" });
        }

        // ── M5 (SEC-1): loud warning for the global SSRF kill-switch ─────────
        // Delivery:AllowPrivateNetworkTargets=true disables ALL SSRF network-range
        // protection across every delivery dispatcher AND every pull poller (SFTP/S3/IMAP).
        // The catalog plan's M5 disposition is ACCEPTED-LITE: emit a loud Error-level
        // startup log whenever the flag is true, regardless of environment. (A non-Production
        // environment — e.g. a shared staging — would otherwise enable it silently.) The hard
        // fail-closed in Production below already existed on main and is intentionally LEFT
        // INTACT; M5 deliberately deferred ADDING a new hard-fail as a rider, not removing an
        // existing one. In Production the Error log fires immediately before that throw, whose
        // StartupConfigurationException is captured by the host Sentry integration — satisfying
        // M5's "Sentry message in Production when the flag is true" without a direct Sentry
        // dependency in this Infrastructure-layer validator.
        var allowPrivateNetworkTargets =
            configuration.GetValue<bool>("Delivery:AllowPrivateNetworkTargets", false);
        if (allowPrivateNetworkTargets)
        {
            logger.LogError(
                "{Component} startup ({Env}): Delivery:AllowPrivateNetworkTargets=true — SSRF protection " +
                "is DISABLED for ALL tenant-configured delivery endpoints AND pull pollers (SFTP/S3/IMAP). " +
                "This flag is for localhost dev testing ONLY and must never run with real tenant traffic. " +
                "Unset the DELIVERY__ALLOWPRIVATENETWORKTARGETS environment variable.",
                componentName, environmentName);
        }

        // Production hardening: Delivery:AllowPrivateNetworkTargets=true bypasses all SSRF
        // network-range protection for HTTP delivery. This flag exists only for localhost dev testing
        // and must never reach production.
        if (isProduction && allowPrivateNetworkTargets)
        {
            throw new StartupConfigurationException(
                $"{componentName} cannot start in Production with Delivery:AllowPrivateNetworkTargets=true — " +
                "this disables SSRF protection for all tenant-configured HTTP delivery endpoints. " +
                "Remove or unset the DELIVERY__ALLOWPRIVATENETWORKTARGETS environment variable.",
                new[] { "Delivery:AllowPrivateNetworkTargets" });
        }

        if (missingRequired.Count == 0)
            return;

        if (isProduction)
        {
            var keysList = string.Join(", ", missingRequired);
            var message =
                $"{componentName} cannot start in Production: missing required configuration key(s): {keysList}. " +
                "Set these via environment variables (use '__' instead of ':' on Railway) or appsettings.Production.json.";
            throw new StartupConfigurationException(message, missingRequired);
        }

        // Non-production: warn loudly but allow the process to come up so that
        // local devs are not forced to provide every secret to run unit tests.
        foreach (var key in missingRequired)
        {
            logger.LogWarning(
                "{Component} startup ({Env}): required configuration key '{Key}' is not set. This will fail-fast in Production.",
                componentName, environmentName, key);
        }
    }

    /// <summary>
    /// Validates that the host running the operator-alert sweep can actually deliver an alert.
    ///
    /// <para><b>Why this is fail-fast and not a log line.</b> Every other failure in the alerting
    /// stack can now be reported through the alerting stack — a blind probe, a timed-out snapshot
    /// and an undelivered alert all reach the operator or land in a log a working Sentry ships. This
    /// one cannot: with no destination configured there is, by construction, nowhere to report it
    /// to, and the <c>LogError</c> that would report it goes to a Sentry that the same missing
    /// configuration disabled. Refusing to boot is the only remaining loud option.</para>
    ///
    /// <para><b>Why that is safe.</b> Configuration does not change by itself, so this can only fire
    /// on a deploy, in front of the person deploying, who sees a crash loop within a minute. The
    /// alternative — booting clean with a dead alarm — is discovered during the incident the alarm
    /// existed to catch. Same trade the validator already makes for
    /// <c>DataProtection:EncryptionKey</c>.</para>
    ///
    /// <para><b>Two rules, both narrow.</b> (1) At least one destination must exist. (2) A DECLARED
    /// destination must be able to work: an <c>Alerting:Email:To</c> with no
    /// <c>Email:Postmark:ServerToken</c> behind it is refused even when Sentry is healthy, because
    /// nothing at runtime would ever say that half the routing is dead — the sink logs one warning
    /// per alert and the other transport keeps reporting success.</para>
    /// </summary>
    private static void ValidateOperatorAlertDestination(
        IConfiguration configuration,
        ILogger logger,
        string environmentName,
        bool isProduction,
        string componentName)
    {
        var alertEmailTo = configuration["Alerting:Email:To"];
        var postmarkToken = configuration["Email:Postmark:ServerToken"];
        var sentryDsn = configuration["Sentry:Dsn"];

        var hasEmailRoute = !string.IsNullOrWhiteSpace(alertEmailTo);
        var hasSentryRoute = !string.IsNullOrWhiteSpace(sentryDsn);
        var emailRouteIsBroken = hasEmailRoute && string.IsNullOrWhiteSpace(postmarkToken);

        if (!hasEmailRoute && !hasSentryRoute)
        {
            const string message =
                "no operator alert destination is configured: both Alerting:Email:To and Sentry:Dsn " +
                "are unset, so every worker-health, delivery-failure, stalled-channel and AI-cap " +
                "alert is evaluated and delivered into a no-op. Set ALERTING__EMAIL__TO (with " +
                "EMAIL__POSTMARK__SERVERTOKEN) or SENTRY__DSN.";

            if (isProduction)
                throw new StartupConfigurationException(
                    $"{componentName} cannot start in Production: {message}",
                    new[] { "Alerting:Email:To", "Sentry:Dsn" });

            logger.LogWarning(
                "{Component} startup ({Env}): {Message} This will fail-fast in Production.",
                componentName, environmentName, message);
            return;
        }

        if (!emailRouteIsBroken)
            return;

        const string brokenEmailMessage =
            "Alerting:Email:To is configured but Email:Postmark:ServerToken is not, so every emailed " +
            "alert is dropped by the provider client behind a single warning. Set " +
            "EMAIL__POSTMARK__SERVERTOKEN, or unset ALERTING__EMAIL__TO if alerts route elsewhere.";

        if (isProduction)
            throw new StartupConfigurationException(
                $"{componentName} cannot start in Production: {brokenEmailMessage}",
                new[] { "Email:Postmark:ServerToken" });

        logger.LogWarning(
            "{Component} startup ({Env}): {Message} This will fail-fast in Production.",
            componentName, environmentName, brokenEmailMessage);
    }

    /// <summary>The one outbound-email transport that the <c>email</c> delivery channel has.</summary>
    public const string EmailProviderTokenKey = "Email:Postmark:ServerToken";

    /// <summary>
    /// The operator's explicit declaration that this deployment is not expected to send email at
    /// all. Absence of a capability is a configuration accident; this key is what turns it into a
    /// decision. Set <c>DELIVERY__ALLOWNOEMAILCHANNEL=true</c>.
    /// </summary>
    public const string AllowNoEmailChannelKey = "Delivery:AllowNoEmailChannel";

    /// <summary>
    /// Refuses a Production host that cannot send email, unless the operator has DECLARED that.
    ///
    /// <para><b>What was wrong.</b> <see cref="EmailProviderTokenKey"/> sat in
    /// <see cref="OptionalKeys"/> — a warning — and the only hard failure touching it was
    /// <see cref="ValidateOperatorAlertDestination"/>, which fires only when a DIFFERENT key
    /// (<c>Alerting:Email:To</c>) is set. So a deploy that set <c>Sentry:Dsn</c> and no alert
    /// address booted completely clean with no email transport at all, and every <c>email</c>-channel
    /// purchase order then failed one at a time at
    /// <c>EmailApiDeliveryDispatcher</c> — "Email delivery is not configured on this deployment
    /// (no email-API provider token)." A per-send failure on a process that started up healthy is
    /// the shape that reaches a customer before it reaches an operator.</para>
    ///
    /// <para><b>Why refuse to boot rather than warn.</b> The choice turns on whether startup can
    /// tell that email is actually needed here. It cannot, and not because the data is hidden:
    /// <c>supplier_delivery_configs.protocol</c> is a plain, unencrypted text column, so a
    /// cross-org <c>AnyAsync(c =&gt; c.Protocol == "email")</c> is trivially available. It is the
    /// QUESTION that startup cannot answer — that row is per-tenant and mutable, an org can add an
    /// email supplier an hour after boot (and <c>SampleOrderService</c> writes one for anybody who
    /// opens the sample order), so a boot-time "nobody needs email" is stale by construction and
    /// would re-open this exact hole for the next tenant. A probe that must be re-run continuously
    /// to stay true is not a startup check.</para>
    ///
    /// <para><b>Why the escape hatch is a key and not an inference.</b> A self-hosted deployment
    /// really can have no email channel, so an unconditional hard requirement would be wrong. But
    /// the escape hatch is a DECLARATION, never a side effect of some other setting — that is the
    /// precise defect being fixed here. In particular <c>Delivery:EnableSmtp</c> does NOT satisfy
    /// this check even though it is also "email": <c>SmtpDeliveryDispatcher.Protocol</c> is
    /// <c>smtp</c> and <c>EmailApiDeliveryDispatcher.Protocol</c> is <c>email</c>, and
    /// <c>DeliveryService</c> keys dispatchers by protocol — so enabling SMTP delivers nothing for
    /// a supplier saved on the offered <c>email</c> channel. Letting it pass this gate would
    /// rebuild the original bug with a different pair of keys.</para>
    ///
    /// <para><b>Why refusing is safe.</b> Same trade the validator already makes for
    /// <c>DataProtection:EncryptionKey</c>: configuration does not change by itself, so this can
    /// only fire on a deploy, in front of the person deploying, who sees it within a minute. The
    /// alternative is discovered by a supplier who never received a purchase order.</para>
    /// </summary>
    private static void ValidateOutboundEmailTransport(
        IConfiguration configuration,
        ILogger logger,
        string environmentName,
        bool isProduction,
        string componentName)
    {
        if (!string.IsNullOrWhiteSpace(configuration[EmailProviderTokenKey]))
            return;

        // The declared-incapacity path. Loud, at Error, in EVERY environment — the Worker serves no
        // HTTP, so there is no readiness surface on which this could otherwise be observed, and a
        // Warning here would be indistinguishable from the optional-key noise this check replaced.
        if (configuration.GetValue<bool>(AllowNoEmailChannelKey, false))
        {
            logger.LogError(
                "{Component} startup ({Env}): {AllowKey}=true and {TokenKey} is not set — this deployment " +
                "CANNOT SEND EMAIL. Every supplier saved on the 'email' delivery channel will fail at " +
                "dispatch with 'Email delivery is not configured on this deployment', and every emailed " +
                "operator alert is dropped. This is a declared configuration, not a fault; unset " +
                "DELIVERY__ALLOWNOEMAILCHANNEL and set EMAIL__POSTMARK__SERVERTOKEN to restore email.",
                componentName, environmentName, AllowNoEmailChannelKey, EmailProviderTokenKey);
            return;
        }

        const string message =
            "Email:Postmark:ServerToken is not set, so this host has NO outbound email transport: every " +
            "purchase order on the 'email' delivery channel fails at dispatch, one supplier at a time, " +
            "on a process that started up looking healthy. Set EMAIL__POSTMARK__SERVERTOKEN. If this " +
            "deployment genuinely has no email channel, declare it with DELIVERY__ALLOWNOEMAILCHANNEL=true " +
            "— note that Delivery:EnableSmtp does NOT satisfy this, because the retired 'smtp' dispatcher " +
            "does not serve the offered 'email' channel.";

        if (isProduction)
            throw new StartupConfigurationException(
                $"{componentName} cannot start in Production: {message}",
                new[] { EmailProviderTokenKey });

        logger.LogWarning(
            "{Component} startup ({Env}): {Message} This will fail-fast in Production.",
            componentName, environmentName, message);
    }

    /// <summary>
    /// WP-21 — announce the EFFECTIVE value of <c>Connections:RevisionAuthority</c> at startup, on
    /// every host, in every environment.
    ///
    /// <para><b>Why here.</b> Both <c>Program.cs</c> files already call
    /// <see cref="Validate"/> before <c>Run()</c>, so riding this seam guarantees a new host cannot
    /// gain the announcement by accident and lose it by omission — and the Worker
    /// (<c>aware-amazement</c>) serves no HTTP at all, so a log line is the ONLY surface on which
    /// its value can be observed.</para>
    ///
    /// <para><b>Why the PARSED value.</b> "The key is present" was never the question. On
    /// 2026-07-27 an audit read a raw <c>appsettings.Development.json</c> and concluded production
    /// was off; the deployed value is <c>true</c> on both Railway services. Only a line stating the
    /// value that the resolver will actually act on ends that argument. Never logs the raw string —
    /// <see cref="EffectiveConnectionConfigResolver.IsEnabled"/> is the one reader, so the log and
    /// the behaviour cannot disagree.</para>
    /// </summary>
    private static void AnnounceRevisionAuthority(
        IConfiguration configuration, ILogger logger, string componentName)
    {
        var enabled = EffectiveConnectionConfigResolver.IsEnabled(configuration);

        logger.LogInformation(
            "{Component} startup: revision authority enabled={Enabled} (key '{Key}', env var '{EnvVar}'). "
            + "{Consequence}",
            componentName,
            enabled,
            EffectiveConnectionConfigResolver.FlagKey,
            EffectiveConnectionConfigResolver.EnvironmentVariableName,
            enabled
                ? "A pinned order is processed under the published revision it was ingested with."
                : "Every order is processed against the live mutable tables; a config edit changes how already-ingested orders are processed.");
    }

    /// <summary>
    /// Validates a PRESENT <c>Delivery:EncryptionKey</c> value. Returns a human-readable
    /// error when the key is insecure/invalid (not a 32-byte base64 string, or the
    /// all-zero placeholder), or <c>null</c> when acceptable. A null/blank key returns
    /// <c>null</c> here because absence is handled by the required-key check.
    /// </summary>
    public static string? ValidateEncryptionKey(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            return null;

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException)
        {
            return "Delivery:EncryptionKey is not valid base64; expected a 32-byte base64 string.";
        }

        if (key.Length != 32)
            return "Delivery:EncryptionKey must decode to exactly 32 bytes (AES-256).";

        if (Array.TrueForAll(key, b => b == 0))
            return "Delivery:EncryptionKey is the all-zero placeholder key — generate a real key " +
                   "(e.g. `openssl rand -base64 32`) and set it via the DELIVERY__ENCRYPTIONKEY environment variable.";

        return null;
    }

    /// <summary>
    /// Validates a PRESENT <c>Security:ApiKeyHashSecret</c> value. Returns a
    /// human-readable error when the secret is shorter than 16 UTF-8 characters
    /// (too short to provide meaningful HMAC entropy), or <c>null</c> when
    /// acceptable. A null/blank value returns <c>null</c> because absence is
    /// handled by the required-key check.
    /// </summary>
    public static string? ValidateApiKeyHashSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return null;

        if (secret.Length < 16)
            return "Security:ApiKeyHashSecret is too short (minimum 16 characters). " +
                   "Generate a strong secret (e.g. `openssl rand -base64 32`) and set it " +
                   "via the SECURITY__APIKEYHASHSECRET environment variable.";

        return null;
    }
}

/// <summary>
/// Thrown by <see cref="StartupConfigurationValidator"/> when one or more
/// required configuration keys are missing in Production. The
/// <see cref="MissingKeys"/> list is preserved so callers/log sinks can render
/// it cleanly.
/// </summary>
public sealed class StartupConfigurationException : InvalidOperationException
{
    public StartupConfigurationException(string message, IReadOnlyList<string> missingKeys)
        : base(message)
    {
        MissingKeys = missingKeys;
    }

    public IReadOnlyList<string> MissingKeys { get; }
}
