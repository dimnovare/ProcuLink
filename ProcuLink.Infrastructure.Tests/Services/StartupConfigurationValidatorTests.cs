using Microsoft.Extensions.Configuration;
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
        StartupConfigurationValidator.Validate(
            BuildConfig(ValidKey), NullLogger.Instance, "Production",
            StartupConfigurationValidator.ApiRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "ProcuLink.Api");
        // reaching here = no throw = pass
    }

    [Fact]
    public void Validate_Development_AllZeroKey_DoesNotThrow()
    {
        // Dev keeps its "just works without secrets" ergonomics — the all-zero
        // rejection only fires in Production.
        StartupConfigurationValidator.Validate(
            BuildConfig(AllZeroKey), NullLogger.Instance, "Development",
            StartupConfigurationValidator.ApiRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "ProcuLink.Api");
    }

    private static IConfiguration BuildConfig(string encryptionKey)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var k in StartupConfigurationValidator.ApiRequiredKeys)
            dict[k] = "configured-value";
        dict["Delivery:EncryptionKey"] = encryptionKey;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static byte[] SequentialBytes(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i + 1);
        return b;
    }
}
