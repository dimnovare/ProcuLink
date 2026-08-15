using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Services;

/// <inheritdoc />
public sealed class CredentialBindingBackfillService : ICredentialBindingBackfillService
{
    private readonly ProcuLinkDbContext _db;
    private readonly DeliveryEncryptionService _encryption;
    private readonly ILogger<CredentialBindingBackfillService> _logger;

    public CredentialBindingBackfillService(
        ProcuLinkDbContext db,
        DeliveryEncryptionService encryption,
        ILogger<CredentialBindingBackfillService> logger)
    {
        _db = db;
        _encryption = encryption;
        _logger = logger;
    }

    /// <summary>Version byte of the pre-binding envelope.</summary>
    private const byte VersionLegacy = 1;

    public async Task<int> RebindLegacyCredentialsAsync(CancellationToken ct)
    {
        var rewritten = 0;

        rewritten += await RebindIntegrationSubscriptionsAsync(ct);
        rewritten += await RebindSftpIngressAsync(ct);
        rewritten += await RebindS3IngressAsync(ct);
        rewritten += await RebindEmailConfigsAsync(ct);
        rewritten += await RebindCatalogSourcesAsync(ct);
        rewritten += await RebindCxmlSecretsAsync(ct);

        if (rewritten > 0)
            await _db.SaveChangesAsync(ct);

        await ReportUnmigratableDeliveryCredentialsAsync(ct);

        return rewritten;
    }

    /// <summary>True when the stored blob is a readable envelope still at version 1.</summary>
    private static bool IsLegacy(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) return false;

        try
        {
            var bytes = Convert.FromBase64String(blob);
            return bytes.Length > 0 && bytes[0] == VersionLegacy;
        }
        catch (FormatException)
        {
            return false; // not an envelope at all — leave it alone rather than guess
        }
    }

    /// <summary>
    /// Whether a row is worth handing to <see cref="TryRebind"/> at all.
    ///
    /// <para>A version-1 blob always is. A version-2 blob normally is not — but during a key
    /// rotation it may still be encrypted under the retiring key, and only a decrypt can tell, so
    /// while <c>Delivery:PreviousEncryptionKey</c> is configured every readable envelope is
    /// attempted. With no rotation in progress this is exactly <see cref="IsLegacy"/> and the pass
    /// costs what it always did.</para>
    /// </summary>
    private bool NeedsRebindAttempt(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) return false;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(blob);
        }
        catch (FormatException)
        {
            return false; // not an envelope at all — leave it alone rather than guess
        }

        if (bytes.Length == 0) return false;
        return bytes[0] == VersionLegacy || _encryption.HasPreviousKey;
    }

    /// <summary>
    /// Decrypts a blob and re-encrypts it under <paramref name="scope"/> and the primary key.
    /// Returns null when the row needs no rewrite, and also when the blob cannot be read at all, so
    /// the caller can skip that row rather than fail the whole pass.
    ///
    /// <para>Reads with <see cref="LegacyEnvelopeAccess.PermitForMigration"/>: this is the one
    /// caller that must be able to read a version-1 envelope for a purpose whose production reads
    /// now refuse one. Without it the backfill could never migrate the columns it covers, because
    /// its own read would be the thing the downgrade guard rejected.</para>
    /// </summary>
    private string? TryRebind(string blob, CredentialScope scope, string rowDescription)
    {
        try
        {
            var read = _encryption.DecryptDetailed(blob, scope, LegacyEnvelopeAccess.PermitForMigration);

            // Already bound AND already under the primary key — nothing to do. Re-encrypting would
            // change the bytes for no gain, and this is what keeps the pass idempotent on every boot.
            if (!read.WasUnboundLegacyEnvelope && !read.WasEncryptedUnderPreviousKey)
                return null;

            return _encryption.Encrypt(read.Plaintext, scope);
        }
        // CredentialUnbindableException is Decrypt failing to read the blob. ArgumentException is
        // the re-Encrypt's scope.ToAssociatedData() rejecting a malformed scope (e.g. OrgId ==
        // Guid.Empty on an anomalous row). Both mean "this one row can't be migrated" — neither
        // should unwind the whole backfill pass and cost every other tenant its migration.
        catch (Exception ex) when (ex is CredentialUnbindableException or ArgumentException)
        {
            var reason = ex is CredentialUnbindableException unbindable
                ? unbindable.Reason.ToString()
                : ex.GetType().Name;
            _logger.LogWarning(ex,
                "Credential rebind: skipping {Row} — the stored blob could not be read ({Reason}).",
                rowDescription, reason);
            return null;
        }
    }

    private async Task<int> RebindIntegrationSubscriptionsAsync(CancellationToken ct)
    {
        var rows = await _db.IntegrationSubscriptions
            .Where(x => x.EncryptedSecret != null && x.EncryptedSecret != "")
            .ToListAsync(ct);

        var count = 0;
        foreach (var row in rows)
        {
            if (!NeedsRebindAttempt(row.EncryptedSecret)) continue;

            var scope = CredentialScope.ForSupplier(
                row.OrganisationId, CredentialPurpose.OrgIntegrationWebhookSecret, row.Id);
            var rebound = TryRebind(row.EncryptedSecret!, scope, $"integration subscription {row.Id}");
            if (rebound is null) continue;

            row.EncryptedSecret = rebound;
            count++;
        }

        return count;
    }

    private async Task<int> RebindSftpIngressAsync(CancellationToken ct)
    {
        var rows = await _db.SftpIngressConfigs.ToListAsync(ct);

        var count = 0;
        foreach (var row in rows)
        {
            if (!NeedsRebindAttempt(row.EncryptedPassword)) continue;

            var scope = CredentialScope.ForOrg(row.OrgId, CredentialPurpose.OrgIngressSftpPassword);
            var rebound = TryRebind(row.EncryptedPassword, scope, $"SFTP ingress config {row.Id}");
            if (rebound is null) continue;

            row.EncryptedPassword = rebound;
            count++;
        }

        return count;
    }

    private async Task<int> RebindS3IngressAsync(CancellationToken ct)
    {
        var rows = await _db.S3IngressConfigs.ToListAsync(ct);

        var count = 0;
        foreach (var row in rows)
        {
            if (!NeedsRebindAttempt(row.EncryptedSecretKey)) continue;

            var scope = CredentialScope.ForOrg(row.OrgId, CredentialPurpose.OrgIngressS3SecretKey);
            var rebound = TryRebind(row.EncryptedSecretKey, scope, $"S3 ingress config {row.Id}");
            if (rebound is null) continue;

            row.EncryptedSecretKey = rebound;
            count++;
        }

        return count;
    }

    // The IMAP password lives INSIDE Organisation.EmailConfigJson, so the whole record is
    // deserialized, the one field replaced, and the record re-serialized. Every other field is
    // carried through by the `with` expression.
    //
    // No SQL-side pre-filter here (unlike the other passes): EmailConfigJson is mapped
    // HasColumnType("jsonb") + IsRequired(), so a LINQ `!= ""` predicate forces Postgres to cast the
    // C# empty-string literal to jsonb — and '' is not valid JSON, so the query itself throws
    // 22P02 (invalid input syntax for type json) before it ever reaches row data. EF InMemory has no
    // such validation, so this was invisible until the real-Postgres test ran it. Every row is loaded
    // and the per-row IsLegacy(config.PasswordCiphertext) check below does the filtering instead —
    // the same shape RebindSftpIngressAsync/RebindS3IngressAsync/RebindCatalogSourcesAsync already use.
    private async Task<int> RebindEmailConfigsAsync(CancellationToken ct)
    {
        var rows = await _db.Organisations.ToListAsync(ct);

        var count = 0;
        foreach (var row in rows)
        {
            var config = EmailPollingConfig.FromJson(row.EmailConfigJson);
            if (!NeedsRebindAttempt(config.PasswordCiphertext)) continue;

            var scope = CredentialScope.ForOrg(row.Id, CredentialPurpose.OrgEmailImapPassword);
            var rebound = TryRebind(config.PasswordCiphertext!, scope, $"IMAP config for org {row.Id}");
            if (rebound is null) continue;

            row.EmailConfigJson = (config with { PasswordCiphertext = rebound }).ToJson();
            count++;
        }

        return count;
    }

    private async Task<int> RebindCatalogSourcesAsync(CancellationToken ct)
    {
        var rows = await _db.SupplierCatalogSources.ToListAsync(ct);

        var count = 0;
        foreach (var row in rows)
        {
            if (NeedsRebindAttempt(row.EncryptedPassword))
            {
                var scope = CredentialScope.ForSupplier(
                    row.OrgId, CredentialPurpose.SupplierCatalogPassword, row.Id);
                var rebound = TryRebind(row.EncryptedPassword!, scope, $"catalog source password {row.Id}");
                if (rebound is not null)
                {
                    row.EncryptedPassword = rebound;
                    count++;
                }
            }

            if (NeedsRebindAttempt(row.AuthConfigEncrypted))
            {
                var scope = CredentialScope.ForSupplier(
                    row.OrgId, CredentialPurpose.SupplierCatalogAuthConfig, row.Id);
                var rebound = TryRebind(row.AuthConfigEncrypted!, scope, $"catalog source auth config {row.Id}");
                if (rebound is not null)
                {
                    row.AuthConfigEncrypted = rebound;
                    count++;
                }
            }
        }

        return count;
    }

    // ONLY the cXML shared secret. EncryptedCredentials on this same row is deliberately excluded —
    // see the interface docs.
    private async Task<int> RebindCxmlSecretsAsync(CancellationToken ct)
    {
        var rows = await _db.SupplierDeliveryConfigs
            .Where(x => x.EncryptedCxmlSharedSecret != null && x.EncryptedCxmlSharedSecret != "")
            .ToListAsync(ct);

        var count = 0;
        foreach (var row in rows)
        {
            if (!NeedsRebindAttempt(row.EncryptedCxmlSharedSecret)) continue;

            var scope = CredentialScope.ForSupplier(
                row.OrgId, CredentialPurpose.SupplierDeliveryCxmlSecret, row.SupplierId);
            var rebound = TryRebind(
                row.EncryptedCxmlSharedSecret!, scope, $"cXML secret for supplier {row.SupplierId}");
            if (rebound is null) continue;

            row.EncryptedCxmlSharedSecret = rebound;
            count++;
        }

        return count;
    }

    /// <summary>
    /// Counts — and never rewrites — the delivery-credential blobs still in the unbound version-1
    /// envelope, on both the live config and the revision byte-copy.
    ///
    /// <para>These are the only credentials this service cannot migrate, and
    /// <c>CredentialPurpose.AllowsUnboundLegacyEnvelope</c> therefore still accepts version 1 for
    /// them. That residual is the whole remaining exposure, and until now nothing measured it: the
    /// exclusion was documented in a comment and invisible at runtime. Reporting it on every boot
    /// turns "some unknown number of portable ciphertexts" into a number that reaches zero when the
    /// last operator re-saves their delivery config — and a zero is the precondition for removing
    /// the exemption.</para>
    ///
    /// <para>Read-only and independently try/caught: a counting query must never be the reason a
    /// migration pass that already rewrote rows reports failure.</para>
    /// </summary>
    private async Task ReportUnmigratableDeliveryCredentialsAsync(CancellationToken ct)
    {
        try
        {
            var liveBlobs = await _db.SupplierDeliveryConfigs
                .Where(x => x.EncryptedCredentials != null && x.EncryptedCredentials != "")
                .Select(x => x.EncryptedCredentials)
                .ToListAsync(ct);

            var revisionBlobs = await _db.SupplierConnectionRevisions
                .Where(x => x.CredentialsRef != null && x.CredentialsRef != "")
                .Select(x => x.CredentialsRef)
                .ToListAsync(ct);

            var liveLegacy = liveBlobs.Count(IsLegacy);
            var revisionLegacy = revisionBlobs.Count(IsLegacy);

            if (liveLegacy == 0 && revisionLegacy == 0)
            {
                _logger.LogInformation(
                    "Credential binding: no delivery credentials remain in the unbound envelope.");
                return;
            }

            _logger.LogWarning(
                "Credential binding: {LiveCount} live delivery config(s) and {RevisionCount} pinned " +
                "revision copy(ies) are still in the unbound (version 1) envelope. These cannot be " +
                "migrated automatically — the revision copy is compared byte-for-byte and frozen on " +
                "published revisions — so they stay tenant-portable until an operator re-saves each " +
                "supplier's delivery config, which rewrites both sides together.",
                liveLegacy, revisionLegacy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Credential binding: could not count remaining unbound delivery credentials. " +
                "Rebinding itself was unaffected.");
        }
    }
}
