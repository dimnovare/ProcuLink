using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Hardening regression tests for the Production encryption-key validation.
///
/// Background (P0): Delivery:EncryptionKey shipped as 32 all-zero bytes in
/// appsettings, and the startup validator only checked for a MISSING key — an
/// all-zero key passed. A prod env that failed to override it would encrypt all
/// supplier credentials with a publicly-known key. The validator now rejects a
/// present-but-insecure key in Production while preserving dev "just works" ergonomics.
/// </summary>
public class StartupConfigurationValidatorTests
{
    private static readonly string AllZeroKey = Convert.ToBase64String(new byte[32]);
    private static readonly string ValidKey = Convert.ToBase64String(SequentialBytes(32));

    // ── ValidateEncryptionKey (pure helper) ──────────────────────────────────

    [Fact]
    public void ValidateEncryptionKey_AllZero_ReturnsError()
    {
        var error = StartupConfigurationValidator.ValidateEncryptionKey(AllZeroKey);
        Assert.NotNull(error);
        Assert.Contains("all-zero", error);
    }

    [Fact]
    public void ValidateEncryptionKey_ValidRandom_ReturnsNull()
    {
        Assert.Null(StartupConfigurationValidator.ValidateEncryptionKey(ValidKey));
    }

    [Fact]
    public void ValidateEncryptionKey_NotBase64_ReturnsError()
    {
        Assert.NotNull(StartupConfigurationValidator.ValidateEncryptionKey("not-base64-!!!"));
    }

    [Fact]
    public void ValidateEncryptionKey_WrongLength_ReturnsError()
    {
        var error = StartupConfigurationValidator.ValidateEncryptionKey(Convert.ToBase64String(new byte[16]));
        Assert.NotNull(error);
        Assert.Contains("32 bytes", error);
    }

    [Fact]
    public void ValidateEncryptionKey_Blank_ReturnsNull_AbsenceHandledElsewhere()
    {
        Assert.Null(StartupConfigurationValidator.ValidateEncryptionKey(null));
        Assert.Null(StartupConfigurationValidator.ValidateEncryptionKey("   "));
    }

    // ── Validate (end-to-end) ────────────────────────────────────────────────

    [Fact]
    public void Validate_Production_AllZeroKey_Throws()
    {
        var ex = Assert.Throws<StartupConfigurationException>(() =>
            StartupConfigurationValidator.Validate(
                BuildConfig(AllZeroKey), NullLogger.Instance, "Production",
                StartupConfigurationValidator.ApiRequiredKeys,
                StartupConfigurationValidator.OptionalKeys,
                "ProcuLink.Api"));

        Assert.Contains("Delivery:EncryptionKey", ex.MissingKeys);
        Assert.Contains("all-zero", ex.Message);
    }

    [Fact]
    public void Validate_Production_ValidKey_DoesNotThrow()
    {
        var config = BuildConfig(ValidKey);
        var logger = new RecordingLogger();

        var act = () => StartupConfigurationValidator.Validate(
            config, logger, "Production",
            StartupConfigurationValidator.ApiRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "ProcuLink.Api");

        // "Does not throw" IS the claim, so state it. "Reaching here = pass" reported Passed
        // for any reason at all — including Validate being emptied of every check above — and
        // it stops holding the moment the call is wrapped or made async.
        act.Should().NotThrow(
            "a real 32-byte key is exactly what Production asks for; rejecting it would ground "
            + "every deploy that did the right thing");

        // The other half of "did not throw": it did not throw because it PASSED, not because it
        // never looked. Validate always announces the effective revision-authority value, and the
        // announcement must agree with the single reader of the flag.
        var announcement = $"revision authority enabled={EffectiveConnectionConfigResolver.IsEnabled(config)}";
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains(announcement));

        // A clean Production config logs nothing at Error — that level is reserved for the
        // SSRF kill-switch, which is off here.
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void Validate_Development_AllZeroKey_DoesNotThrow()
    {
        // Dev keeps its "just works without secrets" ergonomics — the all-zero
        // rejection only fires in Production.
        var config = BuildConfig(AllZeroKey);
        var logger = new RecordingLogger();

        // The key really is the insecure one; it is the ENVIRONMENT that spares it, not the key
        // being acceptable. Without this the test would still pass if the all-zero detection were
        // deleted outright, which is the P0 this file exists to prevent.
        Assert.NotNull(StartupConfigurationValidator.ValidateEncryptionKey(AllZeroKey));

        var act = () => StartupConfigurationValidator.Validate(
            config, logger, "Development",
            StartupConfigurationValidator.ApiRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "ProcuLink.Api");

        act.Should().NotThrow(
            "a local dev host must still start on the placeholder key — hard-failing here would "
            + "force every developer to mint secrets before running the app");

        // Validate ran to completion rather than bailing early: the announcement is emitted on
        // every host in every environment, and its value matches the one reader of the flag.
        var announcement = $"revision authority enabled={EffectiveConnectionConfigResolver.IsEnabled(config)}";
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information && e.Message.Contains(announcement));

        // Development warns about unset optional keys, but nothing here is Error-worthy.
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    // ── M5 (SEC-1): AllowPrivateNetworkTargets loud startup warning ──────────

    [Fact]
    public void Validate_FlagTrue_Development_LogsLoudError_DoesNotThrow()
    {
        var logger = new RecordingLogger();
        var config = BuildConfigWith(ValidKey, allowPrivateNetworkTargets: true);

        // Development: the existing hard-fail does not apply, but the loud M5 warning must fire.
        StartupConfigurationValidator.Validate(
            config, logger, "Development",
            StartupConfigurationValidator.ApiRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "ProcuLink.Api");

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("AllowPrivateNetworkTargets"));
    }

    [Fact]
    public void Validate_FlagFalse_Development_DoesNotLogTheWarning()
    {
        var logger = new RecordingLogger();
        var config = BuildConfigWith(ValidKey, allowPrivateNetworkTargets: false);

        StartupConfigurationValidator.Validate(
            config, logger, "Development",
            StartupConfigurationValidator.ApiRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "ProcuLink.Api");

        Assert.DoesNotContain(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("AllowPrivateNetworkTargets"));
    }

    [Fact]
    public void Validate_FlagTrue_Production_StillThrows_AndLogsLoudErrorFirst()
    {
        var logger = new RecordingLogger();
        var config = BuildConfigWith(ValidKey, allowPrivateNetworkTargets: true);

        // The pre-existing Production hard-fail is intentionally LEFT INTACT by SEC-1.
        var ex = Assert.Throws<StartupConfigurationException>(() =>
            StartupConfigurationValidator.Validate(
                config, logger, "Production",
                StartupConfigurationValidator.ApiRequiredKeys,
                StartupConfigurationValidator.OptionalKeys,
                "ProcuLink.Api"));

        Assert.Contains("Delivery:AllowPrivateNetworkTargets", ex.MissingKeys);
        // The loud Error log fires before the throw (so it reaches the log sink + Sentry).
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("AllowPrivateNetworkTargets"));
    }

    private static IConfiguration BuildConfigWith(string encryptionKey, bool allowPrivateNetworkTargets)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var k in StartupConfigurationValidator.ApiRequiredKeys)
            dict[k] = "configured-value";
        dict["Delivery:EncryptionKey"] = encryptionKey;
        dict["DataProtection:EncryptionKey"] = ValidKey;
        // Also not in ApiRequiredKeys, and also required in Production: a host with no outbound
        // email transport is refused (ValidateOutboundEmailTransport). These fixtures exist to
        // isolate the ENCRYPTION-KEY rules, so they carry a token and never trip that one.
        dict[StartupConfigurationValidator.EmailProviderTokenKey] = "pm-token";
        dict["Delivery:AllowPrivateNetworkTargets"] = allowPrivateNetworkTargets ? "true" : "false";
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    /// <summary>Minimal ILogger that records level + rendered message for assertions.</summary>
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

    private static IConfiguration BuildConfig(string encryptionKey)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var k in StartupConfigurationValidator.ApiRequiredKeys)
            dict[k] = "configured-value";
        dict["Delivery:EncryptionKey"] = encryptionKey;
        // DataProtection:EncryptionKey is not in ApiRequiredKeys (it is a separate
        // production-hardening guard), but Production validation requires it to be set.
        dict["DataProtection:EncryptionKey"] = ValidKey;
        // Also not in ApiRequiredKeys, and also required in Production: a host with no outbound
        // email transport is refused (ValidateOutboundEmailTransport). These fixtures exist to
        // isolate the ENCRYPTION-KEY rules, so they carry a token and never trip that one.
        dict[StartupConfigurationValidator.EmailProviderTokenKey] = "pm-token";
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static byte[] SequentialBytes(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i + 1);
        return b;
    }
}
