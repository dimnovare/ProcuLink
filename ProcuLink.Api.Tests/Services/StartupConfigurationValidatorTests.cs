using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public class StartupConfigurationValidatorTests
{
    private static IConfiguration AllValid(params (string key, string value)[] overrides)
    {
        // A complete set of required keys with valid placeholder values
        var key32 = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());
        var d = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "x",
            ["Clerk:Authority"]                     = "x",
            ["Storage:R2AccountId"]                 = "x",
            ["Storage:R2AccessKeyId"]               = "x",
            ["Storage:R2SecretAccessKey"]            = "x",
            ["Storage:R2Endpoint"]                  = "x",
            ["Storage:R2BucketName"]                = "x",
            ["Stripe:SecretKey"]                    = "x",
            ["Stripe:WebhookSecret"]                = "x",
            ["Stripe:GrowthPriceId"]                = "x",
            ["Stripe:OperationsPriceId"]            = "x",
            ["Stripe:IntegrationPriceId"]           = "x",
            ["Stripe:DistributorPriceId"]           = "x",
            ["Delivery:EncryptionKey"]              = key32,
            ["Delivery:AllowPrivateNetworkTargets"] = "false",
            ["Security:ApiKeyHashSecret"]           = "a-sufficiently-long-secret-value-here",
            ["Frontend:Url"]                        = "https://app.proculink.com",
            ["DataProtection:EncryptionKey"]        = key32,
        };
        foreach (var (k, v) in overrides)
            d[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private static void ValidateProd(IConfiguration cfg, ILogger? logger = null) =>
        StartupConfigurationValidator.Validate(
            cfg, logger ?? NullLogger.Instance, "Production",
            StartupConfigurationValidator.ApiRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "Api");

    [Fact]
    public void Validate_AllRequiredPresent_DoesNotThrow()
    {
        var cfg = AllValid();
        var logger = new RecordingLogger();

        var act = () => ValidateProd(cfg, logger);

        // "Does not throw" IS the claim, so state it. Relying on the absence of an exception
        // makes the test report Passed for any reason at all — including Validate being
        // emptied out — and it stops holding the moment the call is wrapped or made async.
        act.Should().NotThrow(
            "a complete, valid Production configuration must let the host come up");

        // Cheap observable that the validator actually RAN rather than no-opping: WP-21 requires
        // the effective revision-authority value to be announced on every host in every
        // environment, and the announcement must agree with the one reader of the flag.
        var announcement = $"revision authority enabled={EffectiveConnectionConfigResolver.IsEnabled(cfg)}";
        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Information && e.Message.Contains(announcement),
            "the startup log is the only surface on which a host's revision-authority value can be read");

        // A green Production validation must be silent at Error level — the SSRF kill-switch
        // warning is the one thing that logs there, and this config has the flag off.
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error,
            "nothing in a fully-configured Production setup warrants an Error-level startup log");
    }

    [Fact]
    public void Validate_DistributorPriceId_Missing_Throws()
    {
        // Distributor is now a sold, self-serve tier, so its monthly price ID is a
        // required production key (like Growth/Operations/Integration) — a missing
        // value must fail-fast, not silently break Distributor Checkout.
        var cfg = AllValid(("Stripe:DistributorPriceId", ""));
        Assert.Throws<StartupConfigurationException>(() => ValidateProd(cfg));
    }

    [Fact]
    public void Validate_DataProtection_Key_Absent_Throws()
    {
        var cfg = AllValid(("DataProtection:EncryptionKey", ""));
        Assert.Throws<StartupConfigurationException>(() => ValidateProd(cfg));
    }

    [Fact]
    public void Validate_AllowPrivateNetworkTargets_True_Throws()
    {
        var cfg = AllValid(("Delivery:AllowPrivateNetworkTargets", "true"));
        Assert.Throws<StartupConfigurationException>(() => ValidateProd(cfg));
    }

    /// <summary>Minimal <see cref="ILogger"/> that records level + rendered message, so a
    /// "does not throw" test can also assert the decision the validator logged rather than
    /// only the fact that nothing escaped.</summary>
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
