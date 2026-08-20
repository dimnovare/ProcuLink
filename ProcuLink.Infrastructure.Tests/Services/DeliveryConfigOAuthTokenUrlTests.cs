using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// Transport security for the OAuth token URL stored INSIDE the encrypted delivery credentials —
/// the one outbound URL the save path never looked at.
///
/// <para><b>The gap this closes.</b> <c>ValidateTransportSecurity</c> inspects the transport
/// <c>url</c> in <c>config_json</c>, and the catalog save path inspects its own OAuth
/// <c>tokenUrl</c> — but for delivery, <c>tokenUrl</c> lives in <c>CredentialsJson</c>, which
/// <c>UpsertAsync</c> encrypted with no URL inspection at all. At send time
/// <c>HttpAuthApplier</c> runs only the SSRF guard, which deliberately allows plain http. So a
/// customer could save <c>http://…/oauth/token</c> and the client-credentials exchange would POST
/// <c>client_id</c> AND <c>client_secret</c> as a cleartext form body — leaking the secret itself,
/// not merely the endpoint it protects.</para>
///
/// <para><b>Both directions.</b> Modelled on <see cref="DeliveryConfigTransportSecurityTests"/>:
/// every refusal is paired with the save it must not break — https token URLs, loopback http
/// token URLs (the local dev loop and e2e suites), credentials with no token URL at all, and
/// protocols whose credentials never carry one.</para>
/// </summary>
public class DeliveryConfigOAuthTokenUrlTests
{
    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DeliveryConfigServiceTests.DeliveryConfigTestDbContext(options);
    }

    private static DeliveryConfigService CreateService(ProcuLinkDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();
        return new DeliveryConfigService(db, new DeliveryEncryptionService(config));
    }

    private static Task<DeliveryConfigResponse> SaveAsync(
        DeliveryConfigService service, Guid orgId, Guid supplierId, string? credentialsJson,
        string protocol = DeliveryProtocolConstants.Http,
        string configJson = "{\"url\":\"https://supplier.example/orders\"}") =>
        service.UpsertAsync(
            orgId, supplierId,
            new UpsertDeliveryConfigRequest(protocol, false, configJson, credentialsJson),
            default);

    // ── Refusals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The verbatim defect: an https delivery endpoint with a cleartext OAuth token endpoint.
    /// The PO would travel encrypted while the credentials that authenticate it travel in clear.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_HttpTokenUrlOnANonLoopbackHost_IsRefusedAndSavesNothing()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            """{"type":"oauth2_client_credentials","tokenUrl":"http://auth.supplier.example/oauth/token","clientId":"cid","clientSecret":"hunter2"}""");

        var thrown = await act.Should().ThrowAsync<OutboundUrlPolicyException>();
        thrown.And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);
        thrown.And.Message.Should().Contain("https://");
        thrown.And.Message.Should().NotContain("hunter2");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// <c>HttpAuthApplier</c> reads the token URL off a <c>JsonElement</c>, and a validator that
    /// bets on which of a repeated key's values the reader binds is a validator that can be
    /// disagreed with. Same rule as the config_json <c>url</c> walk: EVERY tokenUrl-keyed value is
    /// judged, in both orders.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"oauth2_client_credentials","tokenUrl":"https://ok.example/token","tokenUrl":"http://evil.example/token","clientId":"c","clientSecret":"s"}""")]
    [InlineData("""{"type":"oauth2_client_credentials","tokenUrl":"http://evil.example/token","tokenUrl":"https://ok.example/token","clientId":"c","clientSecret":"s"}""")]
    public async Task UpsertAsync_ARepeatedTokenUrlKey_CannotSmuggleACleartextEndpointPast(string credentialsJson)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), credentialsJson);

        (await act.Should().ThrowAsync<OutboundUrlPolicyException>())
            .And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorInsecureTransport);

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// A token URL with a userinfo component is the other thing the shared policy refuses: it
    /// would put a second credential in cleartext next to the one being fetched. The refusal must
    /// not echo it.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_CredentialsEmbeddedInTheTokenUrl_AreRefusedWithoutEcho()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            """{"type":"oauth2_client_credentials","tokenUrl":"https://user:hunter2@auth.supplier.example/token","clientId":"c","clientSecret":"s"}""");

        var thrown = await act.Should().ThrowAsync<OutboundUrlPolicyException>();
        thrown.And.ErrorCode.Should().Be(OutboundUrlPolicy.ErrorCredentialsInUrl);
        thrown.And.Message.Should().NotContain("hunter2");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpsertAsync_ARefusedTokenUrl_DoesNotOverwriteAWorkingConfig()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await SaveAsync(service, orgId, supplierId,
            """{"type":"oauth2_client_credentials","tokenUrl":"https://auth.supplier.example/token","clientId":"c","clientSecret":"old"}""");

        var act = () => SaveAsync(service, orgId, supplierId,
            """{"type":"oauth2_client_credentials","tokenUrl":"http://auth.supplier.example/token","clientId":"c","clientSecret":"new"}""");
        await act.Should().ThrowAsync<OutboundUrlPolicyException>();

        var row = await db.SupplierDeliveryConfigs.SingleAsync();
        row.EncryptedCredentials.Should().NotBeNullOrEmpty("the working credentials must survive the refused save");
    }

    // ── Allowances (a rule that refused every save would pass a refusal-only suite) ──

    [Fact]
    public async Task UpsertAsync_HttpsTokenUrl_IsSaved()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            """{"type":"oauth2_client_credentials","tokenUrl":"https://auth.supplier.example/oauth/token","clientId":"c","clientSecret":"s"}""");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// Mirrors <see cref="OutboundUrlPolicy"/>'s loopback exemption: the local dev loop and the
    /// e2e suites stand up loopback token listeners, and that traffic never leaves the machine.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:53412/token")]
    [InlineData("http://localhost:5223/oauth/token")]
    [InlineData("http://[::1]:9000/token")]
    public async Task UpsertAsync_PlainHttpTokenUrlOnLoopback_IsSaved(string tokenUrl)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            $$"""{"type":"oauth2_client_credentials","tokenUrl":"{{tokenUrl}}","clientId":"c","clientSecret":"s"}""");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// Credentials that carry no token URL — every non-OAuth auth type, and every host-based
    /// protocol's credential blob — must be untouched by this rule.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"bearer","token":"tok-abc"}""")]
    [InlineData("""{"type":"basic","username":"u","password":"p"}""")]
    [InlineData("""{"type":"apikey","header":"X-Api-Key","value":"k"}""")]
    [InlineData(null)]
    public async Task UpsertAsync_CredentialsWithoutATokenUrl_AreUnaffected(string? credentialsJson)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), credentialsJson);

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// A blank tokenUrl is not a network target: today it saves and fails at send time with the
    /// enumerated "OAuth token URL is missing" message. Turning that into a save-time refusal
    /// would be a separate behaviour change from the one this guard is for.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_ABlankTokenUrl_StillSaves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            """{"type":"oauth2_client_credentials","tokenUrl":"","clientId":"c","clientSecret":"s"}""");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// SFTP credentials never carry a tokenUrl, but a rule scoped to a protocol list goes stale in
    /// one direction — so the check is scope-free and this pins that host-based saves keep working.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_HostBasedProtocolCredentials_AreUnaffected()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            """{"password":"sftp-pass"}""",
            DeliveryProtocolConstants.Sftp,
            "{\"host\":\"files.supplier.example\",\"port\":22,\"remotePath\":\"/in\"}");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }
}
