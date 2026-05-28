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
    /// delivery encryption, and the public frontend URL are all hard prerequisites.
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
        "Delivery:EncryptionKey",
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
