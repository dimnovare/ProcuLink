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
        "Alerting:Email:To",
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
        string componentName)
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
