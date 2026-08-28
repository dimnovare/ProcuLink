using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Api.Tests.Security;

/// <summary>
/// The key ring must survive a process restart.
///
/// <para>Production, 2026-08-28 16:08:27 UTC, three events:
/// <c>MissingMethodException: Cannot dynamically create an instance of type
/// 'AesGcmXmlDecryptor'. Reason: No parameterless constructor defined.</c></para>
///
/// <para>Nothing tested this before. Both halves of the pair had zero test
/// references, and a unit test that news up the decryptor directly would have
/// passed regardless — the failure is in how DataProtection ACTIVATES the type
/// it read out of the stored XML, which only a real round trip through the real
/// stack can reach. So this test builds the container twice over one repository,
/// which is what a restart is.</para>
/// </summary>
public class DataProtectionKeyRingRoundTripTests
{
    /// A 32-byte base64 key. Test-only, and it never leaves this file.
    private const string TestKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    /// <summary>Stands in for the Postgres-backed repository: same interface, same lifetime as the process.</summary>
    private sealed class MemoryXmlRepository : IXmlRepository
    {
        public List<XElement> Elements { get; } = new();
        public IReadOnlyCollection<XElement> GetAllElements() => Elements.AsReadOnly();
        public void StoreElement(XElement element, string friendlyName) => Elements.Add(element);
    }

    /// <summary>
    /// Exactly what ProcuLink.Api/Program.cs does.
    ///
    /// <para>It deliberately does NOT register the decryptor in the service
    /// collection. Program.cs used to, and the first version of this test copied
    /// that — the reproduction still failed, which is how the registration was
    /// shown to be inert on this path and why it is gone from both.</para>
    /// </summary>
    private static ServiceProvider BuildHost(IXmlRepository repository, IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddDataProtection().SetApplicationName("ProcuLink");
        services.Configure<KeyManagementOptions>(options =>
        {
            options.XmlRepository = repository;
            options.XmlEncryptor  = new AesGcmXmlEncryptor(configuration);
        });
        return services.BuildServiceProvider();
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataProtection:EncryptionKey"] = TestKey })
            .Build();

    [Fact]
    public void AProtectedValueSurvivesARestart()
    {
        var repository = new MemoryXmlRepository();
        var configuration = Config();

        // First boot: creating a protector mints a key, which the encryptor writes
        // to the repository — recording AesGcmXmlDecryptor as the type to read it back.
        string payload;
        using (var first = BuildHost(repository, configuration))
        {
            payload = first.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("test")
                .Protect("a value that must survive a deploy");
        }

        Assert.NotEmpty(repository.Elements);

        // Second boot over the same repository. This is the restart, and this is
        // where production throws.
        using var second = BuildHost(repository, configuration);
        var unprotected = second.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("test")
            .Unprotect(payload);

        Assert.Equal("a value that must survive a deploy", unprotected);
    }

    [Fact]
    public void TheStoredKeyIsActuallyEncryptedAtRest()
    {
        // Anti-vacuity. If the encryptor silently stopped running, the round trip
        // above would still pass — on plaintext keys. That would be a worse bug
        // than the one this file exists for.
        var repository = new MemoryXmlRepository();
        using var host = BuildHost(repository, Config());
        host.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("x");

        var stored = string.Concat(repository.Elements.Select(e => e.ToString()));

        Assert.Contains("aesGcmEncryptedKey", stored);
        Assert.DoesNotContain("<masterKey", stored);
    }
}
