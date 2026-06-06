using Microsoft.Extensions.Configuration;
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

    private static void ValidateProd(IConfiguration cfg) =>
        StartupConfigurationValidator.Validate(
            cfg, NullLogger.Instance, "Production",
            StartupConfigurationValidator.ApiRequiredKeys,
            StartupConfigurationValidator.OptionalKeys,
            "Api");

    [Fact]
    public void Validate_AllRequiredPresent_DoesNotThrow()
    {
        ValidateProd(AllValid()); // should not throw
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
}
