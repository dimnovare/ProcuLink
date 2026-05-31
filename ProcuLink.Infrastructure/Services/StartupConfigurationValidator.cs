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

        // Production hardening: Delivery:AllowPrivateNetworkTargets=true bypasses all SSRF
        // network-range protection for HTTP delivery. This flag exists only for localhost dev testing
        // and must never reach production.
        if (isProduction && configuration.GetValue<bool>("Delivery:AllowPrivateNetworkTargets", false))
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
