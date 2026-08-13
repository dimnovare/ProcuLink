using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Email;
using ProcuLink.TestSupport;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Email;

/// <summary>
/// The rules that decide which organisation an inbound email belongs to.
///
/// <para><b>The defect these exist for.</b> Inbound mail used to select its tenant by the
/// organisation's public <c>Slug</c> — a kebab-cased company name plus four hex characters, so
/// roughly 16 bits of randomness on top of a guessable stem. No credential was needed to exploit
/// it: the ordinary way to reach the inbound webhook is to send an email, and the mail relay
/// accepts mail from anyone, so guessing a slug put purchase orders into a stranger's inbox.</para>
///
/// <para><b>Which direction the next instance comes from.</b> Not from someone re-adding the slug
/// lookup — that is too obvious. It comes from a NEW fallback added in sympathy: "if the address
/// isn't found, try the slug", "if the org has only one address, use it", "if the token is empty,
/// use the default org". Every test below that asserts a REFUSAL is aimed at that instinct, and
/// <see cref="Resolve_UnknownToken_DoesNotFallBackToAnyOrganisation"/> is the one that would catch
/// it even if the fallback were spelled differently than any of these.</para>
/// </summary>
public sealed class InboundAddressServiceTests
{
    private const string HashSecret = InboundAddressTestHarness.TestHashSecret;

    private static ProcuLinkDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, string slug)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Test Org",
            Slug = slug,
            AccountStatus = AccountStatusConstants.Active,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    // ── 1. The property the whole table exists for ───────────────────────────

    [Fact]
    public async Task Resolve_AddressIssuedToOneOrg_NamesThatOrgAndNoOther()
    {
        await using var db = CreateDb();
        var mine = await SeedOrgAsync(db, "mine-a1b2");
        var theirs = await SeedOrgAsync(db, "theirs-c3d4");

        var service = InboundAddressTestHarness.Create(db);
        var minted = await service.MintPrimaryAsync(mine, "Primary", default);

        var lookup = await service.ResolveAsync(minted.Token, default);

        lookup.Status.Should().Be(InboundAddressLookupStatus.Resolved);
        lookup.OrgId.Should().Be(mine);
        lookup.OrgId.Should().NotBe(theirs,
            "the organisation follows from the credential presented, so an address issued to one " +
            "organisation can never name another");
    }

    /// <summary>
    /// The direct regression test for the audit finding. An organisation's slug is public and
    /// low-entropy; it must no longer open that organisation's inbox.
    /// </summary>
    [Fact]
    public async Task Resolve_OrganisationSlug_IsNotACredential()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "acme-a1b2");

        var service = InboundAddressTestHarness.Create(db);
        // The org has a real, live inbound address — it is a working tenant, not an unconfigured one.
        await service.MintPrimaryAsync(orgId, "Primary", default);

        var lookup = await service.ResolveAsync("acme-a1b2", default);

        lookup.Status.Should().Be(InboundAddressLookupStatus.NotFound,
            "the slug is published in the product and guessable in four hex characters; if it still " +
            "resolved, anyone who could guess it could file purchase orders into this organisation");
        lookup.OrgId.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_UnknownToken_DoesNotFallBackToAnyOrganisation()
    {
        await using var db = CreateDb();
        // A single organisation, which is exactly the shape that tempts a "there's only one, use it"
        // shortcut.
        await SeedOrgAsync(db, "only-org-9999");

        var service = InboundAddressTestHarness.Create(db);
        var lookup = await service.ResolveAsync("0123456789abcdef0123456789abcdef", default);

        lookup.Status.Should().Be(InboundAddressLookupStatus.NotFound);
        lookup.OrgId.Should().BeNull("being the only organisation is not a credential");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Resolve_EmptyToken_Refuses(string token)
    {
        await using var db = CreateDb();
        await SeedOrgAsync(db, "some-org-1111");

        var lookup = await InboundAddressTestHarness.Create(db).ResolveAsync(token, default);

        lookup.Status.Should().NotBe(InboundAddressLookupStatus.Resolved);
        lookup.OrgId.Should().BeNull();
    }

    // ── 2. Revocation, expiry, deactivation ──────────────────────────────────

    [Fact]
    public async Task Resolve_RevokedAddress_StopsWorking()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "revoke-me-2222");
        var service = InboundAddressTestHarness.Create(db);
        var minted = await service.MintPrimaryAsync(orgId, "Primary", default);

        (await service.ResolveAsync(minted.Token, default)).Status
            .Should().Be(InboundAddressLookupStatus.Resolved, "sanity: it worked before revocation");

        (await service.RevokeAsync(orgId, minted.Id, default)).Should().BeTrue();

        (await service.ResolveAsync(minted.Token, default)).Status
            .Should().Be(InboundAddressLookupStatus.NotFound,
                "revocation is the whole point of per-tenant credentials — it must take effect on " +
                "the next message");
    }

    [Fact]
    public async Task Resolve_ExpiredAddress_StopsWorking()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "expired-3333");
        await InboundAddressTestHarness.SeedAddressAsync(
            db, orgId, "expiredtoken0000000000000000000a",
            kind: InboundAddressKind.LegacySlug,
            expiresAt: DateTime.UtcNow.AddMinutes(-1));

        var lookup = await InboundAddressTestHarness.Create(db)
            .ResolveAsync("expiredtoken0000000000000000000a", default);

        lookup.Status.Should().Be(InboundAddressLookupStatus.NotFound,
            "the legacy slug overlap is bounded by this expiry; if an expired row still resolved, " +
            "the weak addressing scheme would never actually retire");
    }

    [Fact]
    public async Task Resolve_DeactivatedAddress_StopsWorking()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "inactive-4444");
        await InboundAddressTestHarness.SeedAddressAsync(
            db, orgId, "inactivetoken000000000000000000a", isActive: false);

        var lookup = await InboundAddressTestHarness.Create(db)
            .ResolveAsync("inactivetoken000000000000000000a", default);

        lookup.Status.Should().Be(InboundAddressLookupStatus.NotFound);
    }

    [Fact]
    public async Task Revoke_AnotherOrgsAddress_IsAMissNotARevocation()
    {
        await using var db = CreateDb();
        var mine = await SeedOrgAsync(db, "mine-5555");
        var theirs = await SeedOrgAsync(db, "theirs-6666");

        var service = InboundAddressTestHarness.Create(db);
        var theirAddress = await service.MintPrimaryAsync(theirs, "Primary", default);

        var revoked = await service.RevokeAsync(mine, theirAddress.Id, default);

        revoked.Should().BeFalse();
        (await service.ResolveAsync(theirAddress.Token, default)).Status
            .Should().Be(InboundAddressLookupStatus.Resolved,
                "one organisation must not be able to switch off another organisation's mail");
    }

    // ── 3. Fail closed on OUR failure, without dropping mail ─────────────────

    [Fact]
    public async Task Resolve_WithNoHashSecret_ReportsUnavailable_NotNotFound()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "secretless-7777");
        // Mint with a working configuration, then resolve with a broken one — the row exists, but
        // nothing can be recognised.
        await InboundAddressTestHarness.Create(db).MintPrimaryAsync(orgId, "Primary", default);

        var brokenConfig = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Delivery:EncryptionKey"] = InboundAddressTestHarness.TestEncryptionKey,
                ["Security:ApiKeyHashSecret"] = "",
            }).Build();

        var broken = new InboundAddressService(
            db, new DeliveryEncryptionService(brokenConfig), brokenConfig,
            NullLogger<InboundAddressService>.Instance);

        var lookup = await broken.ResolveAsync("0123456789abcdef0123456789abcdef", default);

        lookup.Status.Should().Be(InboundAddressLookupStatus.Unavailable,
            "a missing pepper means NO address is recognisable, which says nothing about this " +
            "message. Reporting NotFound would settle it as permanently un-routable and the mail " +
            "would be gone; Unavailable keeps the provider re-delivering until it is fixed");
    }

    [Fact]
    public void DefaultLookup_Refuses_SoAnUnassignedStructCannotNameATenant()
    {
        var uninitialised = default(InboundAddressLookup);

        uninitialised.Status.Should().Be(InboundAddressLookupStatus.Unavailable);
        uninitialised.Status.Should().NotBe(InboundAddressLookupStatus.Resolved,
            "a zero-initialised lookup is what a forgotten assignment produces; in this repository " +
            "unrecognised values have repeatedly fallen through to the favourable answer, and here " +
            "the favourable answer hands a stranger's mail to a tenant");
        uninitialised.OrgId.Should().BeNull();
    }

    // ── 4. Storage: hashed for lookup, bound for display ─────────────────────

    [Fact]
    public async Task MintedAddress_IsNotStoredInClearAnywhere()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "storage-8888");
        var minted = await InboundAddressTestHarness.Create(db).MintPrimaryAsync(orgId, "Primary", default);

        var row = await db.OrgInboundAddresses.AsNoTracking().SingleAsync(a => a.Id == minted.Id);

        row.TokenHash.Should().NotContain(minted.Token,
            "a read-only database copy must not yield working inbound addresses");
        row.EncryptedToken.Should().NotContain(minted.Token);
        row.TokenPrefix.Should().NotBe(minted.Token,
            "the prefix is a fragment for telling rows apart, never the whole credential");
        minted.Token.Should().StartWith(row.TokenPrefix);
    }

    [Fact]
    public async Task MintedToken_CarriesFullEntropy_NotAGuessableStem()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "acme-a1b2");
        var service = InboundAddressTestHarness.Create(db);

        var first = await service.MintPrimaryAsync(orgId, "Primary", default);
        var second = await service.MintPrimaryAsync(orgId, "Primary", default);

        // 16 bytes hex-encoded. The slug scheme this replaced had four hex characters of randomness
        // appended to a company name; the whole point is that there is no stem to guess.
        first.Token.Should().HaveLength(InboundAddressService.TokenBytes * 2);
        first.Token.Should().MatchRegex("^[0-9a-f]+$",
            "the router lower-cases every recipient, so the alphabet has to survive lower-casing");
        first.Token.Should().NotBe(second.Token);
        first.Token.Should().NotContain("acme");
    }

    [Fact]
    public async Task StoredCiphertext_IsBoundToItsOwnRow()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "binding-9999");
        var service = InboundAddressTestHarness.Create(db);

        var a = await service.MintPrimaryAsync(orgId, "Primary", default);
        var b = await service.MintPrimaryAsync(orgId, "Primary", default);

        var rowA = await db.OrgInboundAddresses.AsNoTracking().SingleAsync(x => x.Id == a.Id);
        var encryption = new DeliveryEncryptionService(InboundAddressTestHarness.Configuration());

        // Same org, same purpose, DIFFERENT row: the associated data differs, so the open must fail.
        var openUnderWrongRow = () => encryption.Decrypt(
            rowA.EncryptedToken,
            CredentialScope.ForSupplier(orgId, CredentialPurpose.OrgInboundEmailAddress, b.Id));

        openUnderWrongRow.Should().Throw<CredentialUnbindableException>(
            "row-scoped binding is what stops a ciphertext being lifted from one address row into " +
            "another");
    }

    [Fact]
    public async Task StoredCiphertext_DoesNotOpenUnderAnotherOrganisation()
    {
        await using var db = CreateDb();
        var mine = await SeedOrgAsync(db, "mine-1212");
        var theirs = await SeedOrgAsync(db, "theirs-3434");
        var minted = await InboundAddressTestHarness.Create(db).MintPrimaryAsync(mine, "Primary", default);

        var row = await db.OrgInboundAddresses.AsNoTracking().SingleAsync(x => x.Id == minted.Id);
        var encryption = new DeliveryEncryptionService(InboundAddressTestHarness.Configuration());

        var openAsOtherOrg = () => encryption.Decrypt(
            row.EncryptedToken,
            CredentialScope.ForSupplier(theirs, CredentialPurpose.OrgInboundEmailAddress, minted.Id));

        openAsOtherOrg.Should().Throw<CredentialUnbindableException>();
    }

    [Fact]
    public async Task List_ReturnsTheAddressBackToItsOwner()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "display-5656");
        var service = InboundAddressTestHarness.Create(db);
        var minted = await service.MintPrimaryAsync(orgId, "Primary", default);

        var listed = await service.ListAsync(orgId, default);

        // Recoverable on purpose: unlike an API key, an inbound address has to be handed to buyers,
        // so "shown once at creation" would not survive contact with support.
        listed.Should().ContainSingle().Which.Token.Should().Be(minted.Token);
    }

    [Fact]
    public async Task List_IsScopedToTheCallingOrganisation()
    {
        await using var db = CreateDb();
        var mine = await SeedOrgAsync(db, "mine-7878");
        var theirs = await SeedOrgAsync(db, "theirs-9090");
        var service = InboundAddressTestHarness.Create(db);
        await service.MintPrimaryAsync(mine, "Primary", default);
        await service.MintPrimaryAsync(theirs, "Primary", default);

        var listed = await service.ListAsync(mine, default);

        listed.Should().ContainSingle();
        listed.Should().OnlyContain(_ => true);
    }

    // ── 5. Backfill: keep existing mail working, on a clock ──────────────────

    [Fact]
    public async Task Backfill_GivesEveryOrgAPrimaryAndRegistersItsSlugWithAnExpiry()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "legacy-org-1234");
        var service = InboundAddressTestHarness.Create(db);

        await service.BackfillMissingAsync(default);

        var rows = await db.OrgInboundAddresses.AsNoTracking()
            .Where(a => a.OrganisationId == orgId).ToListAsync();

        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(r => r.Kind == InboundAddressKind.Primary)
            .Which.ExpiresAt.Should().BeNull("a minted address is the permanent one");

        var legacy = rows.Should().ContainSingle(r => r.Kind == InboundAddressKind.LegacySlug).Subject;
        legacy.ExpiresAt.Should().NotBeNull(
            "the guessable scheme has to retire itself; leaving it open-ended would mean it survives " +
            "as long as nobody remembers to end it");
        legacy.ExpiresAt.Should().BeAfter(DateTime.UtcNow);

        // The whole point of the overlap: mail already addressed to the slug keeps arriving.
        var lookup = await service.ResolveAsync("legacy-org-1234", default);
        lookup.Status.Should().Be(InboundAddressLookupStatus.Resolved);
        lookup.OrgId.Should().Be(orgId);
    }

    [Fact]
    public async Task Backfill_IsIdempotent_SoItCanRunOnEveryBoot()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "idempotent-5678");
        var service = InboundAddressTestHarness.Create(db);

        var first = await service.BackfillMissingAsync(default);
        var second = await service.BackfillMissingAsync(default);

        first.Should().Be(2);
        second.Should().Be(0, "a second boot must not mint a second address for the same org");
        (await db.OrgInboundAddresses.CountAsync(a => a.OrganisationId == orgId)).Should().Be(2);
    }

    [Fact]
    public async Task EnsurePrimary_MintsOnceAndOnlyOnce()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "ensure-2468");
        var service = InboundAddressTestHarness.Create(db);

        await service.EnsurePrimaryAsync(orgId, default);
        await service.EnsurePrimaryAsync(orgId, default);

        (await db.OrgInboundAddresses.CountAsync(a => a.OrganisationId == orgId)).Should().Be(1);
    }

    [Fact]
    public async Task EnsurePrimary_MintsAgainOnceTheOnlyAddressIsRevoked()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, "ensure-1357");
        var service = InboundAddressTestHarness.Create(db);

        await service.EnsurePrimaryAsync(orgId, default);
        var existing = await db.OrgInboundAddresses.AsNoTracking().SingleAsync();
        await service.RevokeAsync(orgId, existing.Id, default);

        await service.EnsurePrimaryAsync(orgId, default);

        var live = await db.OrgInboundAddresses.AsNoTracking()
            .CountAsync(a => a.OrganisationId == orgId && a.IsActive && a.RevokedAt == null);
        live.Should().Be(1, "an organisation that revoked its only address must be able to receive mail again");
    }
}
