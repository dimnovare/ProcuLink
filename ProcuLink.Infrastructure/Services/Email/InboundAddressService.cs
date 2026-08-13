using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Security;

namespace ProcuLink.Infrastructure.Services.Email;

/// <summary>
/// Issues and resolves the per-organisation inbound-email addresses that authorise delivery into an
/// organisation.
///
/// <para><b>What this replaced, and why.</b> Inbound mail used to pick its tenant by the
/// organisation's public <c>Slug</c>: <c>{slug}@orders.proculink.eu</c>, where the slug is a
/// kebab-cased company name plus four hex characters. That is roughly 16 bits of randomness on top
/// of a guessable stem, and — this is the part that made it urgent — exploiting it needed no
/// credential at all. The shared webhook token guards the HTTP endpoint, but the ordinary way to
/// reach that endpoint is to send an email, and the mail relay accepts mail from anybody. Guessing
/// a slug was therefore enough to put purchase orders into a stranger's inbox. Resolution now runs
/// against this table, whose tokens are 128 bits of CSPRNG output, so naming a tenant requires
/// holding that tenant's credential.</para>
///
/// <para><b>Why the address has to be the credential.</b> The relay fans every message for the whole
/// domain into ONE webhook URL, does not sign inbound webhooks, and cannot attach a custom header
/// (<c>docs/infra/postmark-inbound-verify-worker/README.md</c>). The recipient address is the only
/// field that differs per tenant, so it is the only place per-tenant proof can live. This is the
/// same shape as the "capability URL" every inbound-email product uses, and it carries the same
/// caveat, stated plainly: an email address is disclosed more readily than a header token, which is
/// why these are revocable, rotatable, individually expiring, and grant ingest only.</para>
/// </summary>
public sealed class InboundAddressService : IInboundAddressService
{
    /// <summary>
    /// Bytes of CSPRNG entropy per minted token. 16 bytes = 128 bits, hex-encoded to 32 lowercase
    /// characters.
    /// </summary>
    public const int TokenBytes = 16;

    /// <summary>
    /// Prefixed onto the token before hashing so this namespace can never collide with
    /// <c>TenantApiKey.KeyHash</c>, which is HMAC'd with the same server secret. Without it, a
    /// value that happened to be valid in one namespace would be valid in the other.
    ///
    /// <para>Part of the stored format: change this string and every existing row stops
    /// resolving.</para>
    /// </summary>
    public const string HashDomain = "proculink.inbound.address.v1:";

    /// <summary>Characters of the token kept in the clear for list UIs and log correlation.</summary>
    private const int PrefixLength = 6;

    /// <summary>
    /// Default overlap, in days, granted to a backfilled legacy slug address. Long enough for an
    /// operator to hand every buyer the new address, short enough that the guessable scheme does
    /// not outlive the memory of the migration. Override with
    /// <c>Inbound:LegacyAddressGraceDays</c>.
    /// </summary>
    private const int DefaultLegacyGraceDays = 90;

    /// <summary>Minimum interval between <c>LastUsedAt</c> writes, mirroring <c>ApiKeyAuthHandler</c>.</summary>
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromMinutes(5);

    private readonly ProcuLinkDbContext _db;
    private readonly DeliveryEncryptionService _encryption;
    private readonly IConfiguration _config;
    private readonly ILogger<InboundAddressService> _logger;

    public InboundAddressService(
        ProcuLinkDbContext db,
        DeliveryEncryptionService encryption,
        IConfiguration config,
        ILogger<InboundAddressService> logger)
    {
        _db = db;
        _encryption = encryption;
        _config = config;
        _logger = logger;
    }

    // ── Resolution ───────────────────────────────────────────────────────────

    public async Task<InboundAddressLookup> ResolveAsync(string addressToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(addressToken))
            return InboundAddressLookup.NotFound();

        // A missing hash secret means NOTHING can be recognised — every tenant's mail would look
        // unknown. Reporting that as "not found" would tell the provider to stop retrying and the
        // mail would be gone; reporting it as Unavailable keeps the retry window open while an
        // operator fixes the configuration. This is the branch that decides whether a bad deploy
        // costs a delay or costs purchase orders.
        var secret = HashSecret();
        if (secret is null)
        {
            _logger.LogError(
                "Inbound address lookup unavailable: Security:ApiKeyHashSecret is not configured, so no " +
                "inbound address can be recognised. Inbound mail is being deferred, not dropped.");
            return InboundAddressLookup.Unavailable();
        }

        var hash = ComputeHash(addressToken, secret);
        var now = DateTime.UtcNow;

        // Fail-closed predicate. Every clause is a reason an address must STOP working, and they are
        // evaluated in the database so a revoked or expired row can never be resurrected by a bug in
        // caller-side filtering.
        var match = await _db.OrgInboundAddresses
            .AsNoTracking()
            .Where(a => a.TokenHash == hash
                        && a.IsActive
                        && a.RevokedAt == null
                        && (a.ExpiresAt == null || a.ExpiresAt > now))
            .Select(a => new { a.Id, a.OrganisationId, a.LastUsedAt })
            .FirstOrDefaultAsync(ct);

        if (match is null)
            return InboundAddressLookup.NotFound();

        if (match.LastUsedAt is null || now - match.LastUsedAt.Value >= LastUsedThrottle)
            await TouchLastUsedAsync(match.Id, now, ct);

        return InboundAddressLookup.Found(match.OrganisationId, match.Id);
    }

    /// <summary>
    /// Best-effort "last seen" stamp. Never allowed to fail a resolution: an address that works is
    /// not made to stop working because a bookkeeping write lost a race.
    /// </summary>
    private async Task TouchLastUsedAsync(Guid addressId, DateTime usedAt, CancellationToken ct)
    {
        try
        {
            await _db.OrgInboundAddresses
                .Where(a => a.Id == addressId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastUsedAt, usedAt), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Non-critical: failed to update LastUsedAt for inbound address {AddressId}.", addressId);
        }
    }

    // ── Minting ──────────────────────────────────────────────────────────────

    public async Task<MintedInboundAddress> MintPrimaryAsync(Guid orgId, string label, CancellationToken ct)
    {
        var secret = HashSecret()
            ?? throw new InvalidOperationException(
                "Security:ApiKeyHashSecret is not configured — an inbound address minted now could " +
                "never be resolved. Set SECURITY__APIKEYHASHSECRET before issuing addresses.");

        var token = NewToken();
        var entity = BuildRow(orgId, token, secret, InboundAddressKind.Primary, label, expiresAt: null);

        _db.OrgInboundAddresses.Add(entity);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Minted inbound address {AddressId} ({Prefix}…) for org {OrgId}.",
            entity.Id, entity.TokenPrefix, orgId);

        return new MintedInboundAddress(entity.Id, token);
    }

    public async Task EnsurePrimaryAsync(Guid orgId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var hasLivePrimary = await _db.OrgInboundAddresses
            .AsNoTracking()
            .AnyAsync(a => a.OrganisationId == orgId
                           && a.Kind == InboundAddressKind.Primary
                           && a.IsActive
                           && a.RevokedAt == null
                           && (a.ExpiresAt == null || a.ExpiresAt > now), ct);

        if (!hasLivePrimary)
            await MintPrimaryAsync(orgId, "Primary", ct);
    }

    /// <summary>
    /// Builds a row from a plaintext token: hashes it for lookup and encrypts it for later display,
    /// binding the ciphertext to (org, purpose, this row's id).
    /// </summary>
    private OrgInboundAddress BuildRow(
        Guid orgId, string token, string secret, string kind, string label, DateTime? expiresAt)
    {
        // The row id is generated FIRST because it is part of the associated data the ciphertext is
        // bound to — a blob from one address row cannot be replayed into another.
        var id = Guid.NewGuid();

        return new OrgInboundAddress
        {
            Id = id,
            OrganisationId = orgId,
            TokenHash = ComputeHash(token, secret),
            EncryptedToken = _encryption.Encrypt(
                token,
                CredentialScope.ForSupplier(orgId, CredentialPurpose.OrgInboundEmailAddress, id)),
            TokenPrefix = token[..Math.Min(PrefixLength, token.Length)],
            Kind = kind,
            Label = label,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
        };
    }

    private static string NewToken()
    {
        var bytes = new byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        // Hex, not base64url: the router lower-cases every recipient before lookup (mail hosts are
        // case-insensitive and relays do rewrite case), so a case-sensitive alphabet would silently
        // throw away entropy. Hex survives lower-casing intact.
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── Backfill ─────────────────────────────────────────────────────────────

    public async Task<int> BackfillMissingAsync(CancellationToken ct)
    {
        var secret = HashSecret();
        if (secret is null)
        {
            _logger.LogError(
                "Inbound address backfill skipped: Security:ApiKeyHashSecret is not configured. " +
                "Existing inbound addresses cannot be registered until it is set.");
            return 0;
        }

        var legacyExpiry = DateTime.UtcNow.AddDays(LegacyGraceDays());
        var inserted = 0;

        var orgs = await _db.Organisations
            .AsNoTracking()
            .Select(o => new { o.Id, o.Slug })
            .ToListAsync(ct);

        foreach (var org in orgs)
        {
            // Per-org try/catch: one organisation with an odd slug must not abort the pass for every
            // other organisation. Same shape as the credential-rebind backfill.
            try
            {
                var existing = await _db.OrgInboundAddresses
                    .AsNoTracking()
                    .Where(a => a.OrganisationId == org.Id)
                    .Select(a => a.Kind)
                    .ToListAsync(ct);

                if (!existing.Contains(InboundAddressKind.Primary))
                {
                    _db.OrgInboundAddresses.Add(BuildRow(
                        org.Id, NewToken(), secret, InboundAddressKind.Primary,
                        "Primary", expiresAt: null));
                    inserted++;
                }

                // The slug address is what buyers already have in their address books, so it is
                // registered rather than cut off — but with a hard expiry, so the weak scheme
                // retires itself rather than depending on anyone remembering to end it.
                if (!existing.Contains(InboundAddressKind.LegacySlug)
                    && !string.IsNullOrWhiteSpace(org.Slug))
                {
                    var slugToken = org.Slug.Trim().ToLowerInvariant();
                    var slugHash = ComputeHash(slugToken, secret);
                    var slugTaken = await _db.OrgInboundAddresses
                        .AsNoTracking()
                        .AnyAsync(a => a.TokenHash == slugHash, ct);

                    if (!slugTaken)
                    {
                        _db.OrgInboundAddresses.Add(BuildRow(
                            org.Id, slugToken, secret, InboundAddressKind.LegacySlug,
                            "Legacy slug address (retiring)", legacyExpiry));
                        inserted++;
                    }
                }

                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Inbound address backfill failed for org {OrgId}; continuing with the rest.", org.Id);
                // Drop the failed adds so the next org's SaveChanges is not poisoned by them.
                foreach (var entry in _db.ChangeTracker.Entries<OrgInboundAddress>().ToList())
                    entry.State = EntityState.Detached;
            }
        }

        return inserted;
    }

    // ── Read / revoke ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<InboundAddressView>> ListAsync(Guid orgId, CancellationToken ct)
    {
        var rows = await _db.OrgInboundAddresses
            .AsNoTracking()
            .Where(a => a.OrganisationId == orgId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        return rows.Select(a => new InboundAddressView(
            a.Id,
            a.Kind,
            a.Label,
            DecryptForDisplay(a),
            a.TokenPrefix,
            a.IsActive,
            a.CreatedAt,
            a.ExpiresAt,
            a.RevokedAt,
            a.LastUsedAt)).ToList();
    }

    /// <summary>
    /// Opens the stored ciphertext for display, degrading to null rather than throwing. A row whose
    /// blob cannot be opened is still listed: it may well be resolving mail right now, and an
    /// operator who cannot see it cannot revoke it.
    /// </summary>
    private string? DecryptForDisplay(OrgInboundAddress row)
    {
        if (string.IsNullOrWhiteSpace(row.EncryptedToken))
            return null;

        try
        {
            return _encryption.Decrypt(
                row.EncryptedToken,
                CredentialScope.ForSupplier(
                    row.OrganisationId, CredentialPurpose.OrgInboundEmailAddress, row.Id));
        }
        catch (CredentialUnbindableException ex)
        {
            _logger.LogWarning(
                "Inbound address {AddressId} could not be decrypted for display ({Reason}); " +
                "listing it without the address so it can still be revoked.",
                row.Id, ex.Reason);
            return null;
        }
    }

    public async Task<bool> RevokeAsync(Guid orgId, Guid addressId, CancellationToken ct)
    {
        // Org-scoped by predicate as well as by query filter: an id from another organisation is a
        // miss, never a revocation. A tracked read-then-save rather than ExecuteUpdate, so the same
        // code path is exercised by the EF-InMemory tests that prove cross-org revocation fails.
        var row = await _db.OrgInboundAddresses
            .FirstOrDefaultAsync(a => a.Id == addressId
                                      && a.OrganisationId == orgId
                                      && a.RevokedAt == null, ct);
        if (row is null)
            return false;

        row.IsActive = false;
        row.RevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Inbound address {AddressId} revoked for org {OrgId}.", addressId, orgId);
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// HMAC-SHA256 of the domain-separated token, keyed with the server-side secret — the same
    /// construction <c>TenantApiKey.KeyHash</c> uses. Read-only database access does not yield
    /// working addresses without that secret.
    /// </summary>
    public static string ComputeHash(string token, string serverSecret) =>
        ApiKeyHasher.ComputeHash(HashDomain + token.Trim().ToLowerInvariant(), serverSecret);

    /// <summary>Null when unset — callers must treat that as "cannot resolve", never as "no match".</summary>
    private string? HashSecret()
    {
        var secret = _config["Security:ApiKeyHashSecret"];
        return string.IsNullOrWhiteSpace(secret) ? null : secret;
    }

    private int LegacyGraceDays()
    {
        var raw = _config["Inbound:LegacyAddressGraceDays"];
        return int.TryParse(raw, out var days) && days > 0 ? days : DefaultLegacyGraceDays;
    }
}
