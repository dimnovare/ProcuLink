using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Tests.Integration;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Documents;

/// <summary>
/// The document-engine API host: the real app on EF InMemory + Hangfire InMemory, authenticated by
/// <see cref="RbacTestAuthHandler"/> headers. Orgs A and B have the engine on; Off does not.
/// Extend with ConfigureTestServices in the task that needs a fake.
/// </summary>
public sealed class DocumentEngineTestFactory : WebApplicationFactory<Program>
{
    public static readonly Guid OrgA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    public static readonly Guid OrgB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    public static readonly Guid OrgOff = Guid.Parse("c0000000-0000-0000-0000-0000000000c0");

    private static string ClerkId(Guid org) => $"org_DOC_{org.ToString("N")[..4]}";

    private readonly string _dbName = Guid.NewGuid().ToString("N");

    /// <summary>Replaces <see cref="IFileStorageService"/>; tests inspect the uploads.</summary>
    public Mock<IFileStorageService> Storage { get; } = new();

    /// <summary>Replaces <see cref="ProcuLink.Core.Services.Ai.IAiMappingService"/>; unset, its calls return null (no default setup).</summary>
    public Mock<ProcuLink.Core.Services.Ai.IAiMappingService> Ai { get; } = new();

    /// <summary>Set before the host first boots to run on Postgres, for the claim-first ledger (EF InMemory has neither unique indexes nor ExecuteUpdate).</summary>
    public string? PostgresConnection { get; init; }

    /// <summary>Replaces <see cref="ProcuLink.Core.Documents.IOutputDesigner"/>; tests set up the AI matches.</summary>
    public Mock<ProcuLink.Core.Documents.IOutputDesigner> Designer { get; } = new();

    /// <summary>Replaces <see cref="IBackgroundJobClient"/>; tests count the jobs created.</summary>
    public Mock<IBackgroundJobClient> Jobs { get; } = new();

    /// <summary>The only <see cref="ProcuLink.Api.Services.Documents.Intake.IPickupSource"/> (kind sftp) in this host.</summary>
    public Mock<ProcuLink.Api.Services.Documents.Intake.IPickupSource> Pickup { get; } = new();

    public Mock<ProcuLink.Api.Services.Documents.Intake.IPickupSession> PickupSession { get; } = new();

    /// <summary>The only <see cref="ProcuLink.Core.Services.Delivery.IDeliveryDispatcher"/> (protocol http) in this host; records what a destination test sends.</summary>
    public Mock<ProcuLink.Core.Services.Delivery.IDeliveryDispatcher> Dispatcher { get; } = new();

    public int Enqueued<T>() =>
        Jobs.Invocations.Count(i => i.Method.Name == nameof(IBackgroundJobClient.Create)
                                    && ((Job)i.Arguments[0]).Type == typeof(T));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delivery:EncryptionKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE=",
            ["ConnectionStrings:DefaultConnection"] = "Host=test;Database=test",
            ["Clerk:Authority"] = "https://test.clerk.accounts.dev",
            ["Hangfire:UseInMemory"] = "true",
            ["Sentry:Dsn"] = "",
            ["Storage:R2AccessKeyId"] = "",
            ["Frontend:Url"] = "http://localhost:3000",
            ["Security:ApiKeyHashSecret"] = "test-api-key-hash-secret-not-a-real-one",
            ["DocumentEngine:EnabledOrgIds:0"] = OrgA.ToString(),
            ["DocumentEngine:EnabledOrgIds:1"] = OrgB.ToString(),
            ["DocumentEngine:LookupSuggestionOrgIds:0"] = OrgA.ToString(),
            ["DocumentEngine:ScanOrgIds:0"] = OrgB.ToString(),
            ["DocumentEngine:FormatOrgIds:0"] = OrgB.ToString(),
            ["DocumentEngine:DocsOutputOrgIds:0"] = OrgA.ToString(),
        }));

        builder.ConfigureServices(services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(DbContextOptions<ProcuLinkDbContext>)
                                               || d.ServiceType == typeof(ProcuLinkDbContext)).ToList())
                services.Remove(d);
            services.AddDbContext<ProcuLinkDbContext>(o => { if (PostgresConnection is null) o.UseInMemoryDatabase(_dbName); else o.UseNpgsql(PostgresConnection); });

            foreach (var d in services.Where(d => d.ServiceType.FullName?.StartsWith("Hangfire") == true).ToList())
                services.Remove(d);
            services.AddHangfire(cfg => cfg
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseInMemoryStorage());

            Storage.Setup(s => s.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Stream _, string key, string _, CancellationToken _) => key);
            Jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");
            Pickup.SetupGet(p => p.Kind).Returns(ProcuLink.Core.Documents.IntakeKind.Sftp);
            foreach (var d in services.Where(d => d.ServiceType == typeof(ProcuLink.Api.Services.Documents.Intake.IPickupSource)).ToList())
                services.Remove(d);
            services.AddSingleton(Pickup.Object);
            Dispatcher.SetupGet(x => x.Protocol).Returns("http");
            foreach (var d in services.Where(d => d.ServiceType == typeof(ProcuLink.Core.Services.Delivery.IDeliveryDispatcher)).ToList())
                services.Remove(d);
            services.AddSingleton(Dispatcher.Object);
            foreach (var d in services.Where(d => d.ServiceType == typeof(ProcuLink.Core.Services.Ai.IAiMappingService)).ToList())
                services.Remove(d);
            services.AddSingleton(Ai.Object);
            foreach (var d in services.Where(d => d.ServiceType == typeof(ProcuLink.Core.Documents.IOutputDesigner)).ToList())
                services.Remove(d);
            services.AddSingleton(Designer.Object);
            services.AddSingleton(Storage.Object);
            services.AddSingleton(Jobs.Object);

            foreach (var d in services.Where(d => d.ServiceType.FullName?.Contains("Authentication") == true
                                               || d.ServiceType.FullName?.Contains("JwtBearer") == true).ToList())
                services.Remove(d);
            services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = RbacTestAuthHandler.SchemeName;
                    o.DefaultChallengeScheme = RbacTestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, RbacTestAuthHandler>(RbacTestAuthHandler.SchemeName, _ => { })
                .AddScheme<ProcuLink.Api.Auth.ApiKeyAuthOptions, ProcuLink.Api.Auth.ApiKeyAuthHandler>("ApiKey", _ => { });
        });
    }

    /// <summary>A client acting as an admin of <paramref name="org"/>; the organisation is created on first use.</summary>
    public HttpClient ClientFor(Guid org)
    {
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            if (!db.Organisations.Any(o => o.Id == org))
            {
                db.Organisations.Add(new Organisation
                {
                    Id = org, ClerkOrgId = ClerkId(org), Name = ClerkId(org), Slug = ClerkId(org).ToLowerInvariant(),
                    Plan = "growth", AccountStatus = "active", CreatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
            }
        }

        var client = CreateClient();
        client.DefaultRequestHeaders.Add(RbacTestAuthHandler.OrgHeader, ClerkId(org));
        client.DefaultRequestHeaders.Add(RbacTestAuthHandler.RoleHeader, "org:admin");
        client.DefaultRequestHeaders.Add(RbacTestAuthHandler.SubHeader, "user_DOC_TEST");
        return client;
    }

    /// <summary>A client holding an org API key (the real ApiKey handler), as a customer's own system would.</summary>
    public HttpClient ApiKeyClientFor(Guid org)
    {
        ClientFor(org).Dispose();
        var raw = "plk_" + Guid.NewGuid().ToString("N");
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcuLinkDbContext>();
            db.TenantApiKeys.Add(new TenantApiKey
            {
                Id = Guid.NewGuid(), OrganisationId = org, Label = "Test", KeyPrefix = raw[..8], IsActive = true,
                KeyHash = ProcuLink.Core.Security.ApiKeyHasher.ComputeHash(raw, "test-api-key-hash-secret-not-a-real-one"),
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-ProcuLink-Key", raw);
        return client;
    }
}
