using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// The cleartext invariant on <c>SupplierDeliveryConfig.ConfigJson</c>, enforced at the live
/// delivery-config write path.
///
/// <para><b>The defect.</b> The column is cleartext by design and its doc comment says no credential
/// may ever be written into it — every secret belongs AES-GCM encrypted in
/// <c>EncryptedCredentials</c>. The HTTP channel's extra-headers map broke that in prose only: an
/// operator typing <c>Authorization: Bearer …</c> had the token stored in clear, returned by GET,
/// and copied into every connection-revision snapshot.</para>
///
/// <para><b>Why the grandfather.</b> The delivery editor has no headers field — <c>headers</c> is an
/// unmanaged key carried through every save untouched — so a flat refusal would block an operator
/// from changing a timeout with no UI anywhere to remove the header. An identical round-trip is
/// therefore not a write; adding or rotating one is.</para>
/// </summary>
public class DeliveryConfigCredentialHeaderTests
{
    private const string Token = "t0ps3cret";
    private static readonly string WithToken =
        $$$"""{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer {{{Token}}}"}}""";
    private const string Clean =
        """{"url":"https://supplier.example/orders","headers":{"Content-Type":"application/xml"}}""";

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
        DeliveryConfigService service, Guid orgId, Guid supplierId, string configJson) =>
        service.UpsertAsync(
            orgId, supplierId,
            new UpsertDeliveryConfigRequest(DeliveryProtocolConstants.Http, false, configJson, null),
            default);

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_WithAnAuthorizationHeader_IsRefusedAndSavesNothing()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), WithToken);

        var thrown = await act.Should().ThrowAsync<CredentialHeaderInConfigException>();
        // Code is a const (implicitly static) on the exception type, so it is qualified by type
        // rather than through the instance — the value under test is the same either way.
        CredentialHeaderInConfigException.Code.Should().Be("credential_header_in_delivery_config");
        thrown.And.HeaderNames.Should().Equal("Authorization");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// The refusal is asserted BEFORE the message is checked for the token. Asserting only that the
    /// message hides it would pass vacuously the moment the guard stopped refusing at all.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_RefusalMessage_NamesTheHeaderAndNeverTheToken()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var act = () => SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(), WithToken);

        var thrown = await act.Should().ThrowAsync<CredentialHeaderInConfigException>();

        thrown.And.PolicyMessage.Should().Contain("'Authorization'");
        thrown.And.PolicyMessage.Should().NotContain(Token);
        thrown.And.Message.Should().NotContain(Token);
    }

    [Fact]
    public async Task UpsertAsync_AddingACredentialHeaderToAnExistingConfig_IsRefused()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        await SaveAsync(service, orgId, supplierId, Clean);

        var act = () => SaveAsync(service, orgId, supplierId, WithToken);

        (await act.Should().ThrowAsync<CredentialHeaderInConfigException>())
            .And.HeaderNames.Should().Equal("Authorization");
    }

    // ── Allowances (a rule that refused everything would pass a refusal-only suite) ──

    [Fact]
    public async Task UpsertAsync_WithOrdinaryHeaders_Saves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        // X-Idempotency-Key and X-Auth-Email are the classifier's precision line, not decoration:
        // they are the two headers the segment rule would refuse if it took bare `key` or bare
        // `auth`, and a false refusal here is a save the operator cannot make and cannot work
        // around. Every other test reaches them through the predicate only — this is the one place
        // they travel the whole UpsertAsync write path.
        var saved = await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            """{"url":"https://supplier.example/orders","headers":{"Content-Type":"application/xml","X-Correlation-Id":"abc","X-Supplier-Account":"ACME-4417","X-Idempotency-Key":"9f2c","X-Auth-Email":"ops@supplier.example"}}""");

        saved.ConfigJson.Should().Contain("X-Supplier-Account");
        saved.ConfigJson.Should().Contain("X-Idempotency-Key");
        saved.ConfigJson.Should().Contain("X-Auth-Email");
        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpsertAsync_WithNoHeadersAtAll_Saves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await SaveAsync(service, Guid.NewGuid(), Guid.NewGuid(),
            """{"url":"https://supplier.example/orders","timeoutSeconds":30}""");

        (await db.SupplierDeliveryConfigs.CountAsync()).Should().Be(1);
    }

    // ── Grandfathering at the service level ──────────────────────────────────

    /// <summary>
    /// Writes the row straight to the database, bypassing the service — the only way a config the
    /// rule now refuses can exist, i.e. one saved before enforcement did.
    /// </summary>
    private static async Task SeedLegacyConfigAsync(
        ProcuLinkDbContext db, Guid orgId, Guid supplierId, string configJson)
    {
        db.SupplierDeliveryConfigs.Add(new ProcuLink.Core.Entities.SupplierDeliveryConfig
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            SupplierId = supplierId,
            Protocol = DeliveryProtocolConstants.Http,
            ConfigJson = configJson,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            UpdatedAt = DateTime.UtcNow.AddDays(-30),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UpsertAsync_UnchangedRoundTripOfALegacyHeader_StillSaves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId, WithToken);

        var saved = await SaveAsync(service, orgId, supplierId, WithToken);

        saved.Should().NotBeNull();
        // The name promises the header survived the round-trip, not just that nothing threw.
        saved.ConfigJson.Should().Contain("Authorization");
    }

    /// <summary>
    /// The realistic migration case: an operator changes the timeout on a supplier whose config
    /// predates enforcement. That must not be blocked — there is no UI to remove the header.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_AnUnrelatedEditBesideALegacyHeader_StillSaves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId, WithToken);

        var saved = await SaveAsync(service, orgId, supplierId,
            $$$"""{"url":"https://supplier.example/orders","timeoutSeconds":90,"headers":{"Authorization":"Bearer {{{Token}}}"}}""");

        saved.ConfigJson.Should().Contain("90");
    }

    [Fact]
    public async Task UpsertAsync_RotatingALegacyHeaderValue_IsRefused()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId, WithToken);

        var act = () => SaveAsync(service, orgId, supplierId,
            """{"url":"https://supplier.example/orders","headers":{"Authorization":"Bearer rotated-value"}}""");

        (await act.Should().ThrowAsync<CredentialHeaderInConfigException>())
            .And.HeaderNames.Should().Equal("Authorization");
    }

    [Fact]
    public async Task UpsertAsync_RemovingALegacyHeader_Saves()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId, WithToken);

        var saved = await SaveAsync(service, orgId, supplierId, Clean);

        saved.ConfigJson.Should().NotContain("Authorization");
    }

    // ── The invariant, verified against the database ─────────────────────────

    /// <summary>
    /// The documented invariant is that the token is not in the column. Read it back and check,
    /// rather than trusting the refusal — with a positive control saved BEFORE that read, so the
    /// table is non-empty and the check is not vacuous in either direction.
    /// </summary>
    [Fact]
    public async Task ARefusedCredentialHeader_IsNowhereInThePersistedConfigJson()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var act = () => SaveAsync(service, orgId, supplierId, WithToken);
        await act.Should().ThrowAsync<CredentialHeaderInConfigException>();

        // Positive control, saved BEFORE the read below: the same endpoint really does persist an
        // ordinary header, so the table this test reads back is non-empty. The refused save above
        // left zero rows on its own, and FluentAssertions' OnlyContain deliberately fails on an
        // empty collection (no vacuous pass) — which is why this uses NotContain instead, but an
        // empty table would still make the assertion below check nothing. Saving a real row first
        // means it is genuinely exercised in both directions: it would catch the token leaking into
        // this (or any other) row, not merely fail to find rows to inspect.
        var saved = await SaveAsync(service, orgId, supplierId, Clean);
        saved.ConfigJson.Should().Contain("Content-Type");

        (await db.SupplierDeliveryConfigs.AsNoTracking().ToListAsync())
            .Should().NotContain(c => c.ConfigJson.Contains(Token));
    }

    // ── The read surface ─────────────────────────────────────────────────────

    /// <summary>
    /// How an operator whose config predates enforcement finds out. The frontend already renders
    /// this field, so reusing it rather than adding a sibling is what puts the instruction in front
    /// of them at all.
    /// </summary>
    [Fact]
    public async Task GetAsync_ALegacyCredentialHeader_IsReportedWithoutTheToken()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SeedLegacyConfigAsync(db, orgId, supplierId, WithToken);

        var fetched = await service.GetAsync(orgId, supplierId, default);

        fetched!.InsecureTransportWarning.Should().NotBeNullOrWhiteSpace();
        fetched.InsecureTransportWarning.Should().Contain("'Authorization'");
        fetched.InsecureTransportWarning.Should().NotContain(Token);
    }

    [Fact]
    public async Task GetAsync_ACleanConfig_HasNoWarning()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        await SaveAsync(service, orgId, supplierId, Clean);

        (await service.GetAsync(orgId, supplierId, default))!
            .InsecureTransportWarning.Should().BeNull();
    }

    /// <summary>
    /// A config that is BOTH cleartext and credential-bearing reports both faults, because fixing
    /// one does not fix the other and an operator told only about the URL would leave the token in
    /// place.
    /// </summary>
    [Fact]
    public async Task GetAsync_BothFaults_AreBothReported()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        // $$$ / {{{...}}}: $$ / {{...}} does not compile here (CS9007) — the literal "}}" closing
        // the headers object and then the outer object immediately follows the interpolation's own
        // closing "}}", producing a same-length brace run the 2-brace delimiter cannot disambiguate.
        // Same fix as WithToken above.
        await SeedLegacyConfigAsync(db, orgId, supplierId,
            $$$"""{"url":"http://supplier.example/orders","headers":{"Authorization":"Bearer {{{Token}}}"}}""");

        var warning = (await service.GetAsync(orgId, supplierId, default))!.InsecureTransportWarning;

        warning.Should().Contain("https://", "the transport fault must still be reported");
        warning.Should().Contain("'Authorization'", "the credential-header fault must be reported too");
        warning.Should().NotContain(Token);
    }
}
