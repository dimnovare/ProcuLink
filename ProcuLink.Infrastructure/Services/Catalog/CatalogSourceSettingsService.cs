using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure.Jobs;

namespace ProcuLink.Infrastructure.Services.Catalog;

/// <summary>
/// CRUD for supplier catalog pull sources. Mirrors <see cref="PullIngressSettingsService"/>:
/// secrets are encrypted via <see cref="DeliveryEncryptionService"/> (AES-256-GCM,
/// write-only) and responses are masked. The enable-transition enqueue replaces the cut
/// sync-now endpoint, guarded against flooding by the M1-residual dedupe rule.
/// </summary>
public sealed class CatalogSourceSettingsService : ICatalogSourceSettingsService
{
    private const string Mask = "********";

    /// <summary>M1 residual: don't enqueue an enable-transition sync when one ran this recently.</summary>
    private static readonly TimeSpan EnqueueDedupeWindow = TimeSpan.FromMinutes(5);

    private readonly ProcuLinkDbContext _db;
    private readonly DeliveryEncryptionService _encryption;
    private readonly IBackgroundJobClient _jobs;

    public CatalogSourceSettingsService(
        ProcuLinkDbContext db,
        DeliveryEncryptionService encryption,
        IBackgroundJobClient jobs)
    {
        _db = db;
        _encryption = encryption;
        _jobs = jobs;
    }

    public async Task<CatalogSourceResponse?> GetAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var source = await _db.SupplierCatalogSources
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.SupplierId == supplierId, ct);

        return source is null ? null : ToResponse(source);
    }

    public async Task<CatalogSourceUpsertResult> UpsertAsync(
        Guid orgId, Guid supplierId, UpsertCatalogSourceRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var source = await _db.SupplierCatalogSources
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.SupplierId == supplierId, ct);

        // Captured BEFORE mutation — drives the false→true enable-transition detection
        // and the M1-residual dedupe guard.
        var wasEnabled      = source?.IsEnabled ?? false;
        var priorStatus     = source?.LastSyncStatus;
        var priorLastSyncAt = source?.LastSyncAt;

        if (source is null)
        {
            source = new SupplierCatalogSource
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                SupplierId = supplierId,
                CreatedAt = now,
            };
            _db.SupplierCatalogSources.Add(source);
        }

        var protocol = request.Protocol.Trim().ToLowerInvariant();
        var isHttp = protocol is "http" or "https";
        var isVendor = protocol is "aes2fa"; // vendor-fetcher protocols (URL + encrypted vendor creds)
        var isUrlBased = isHttp || isVendor;

        source.Protocol = protocol;
        source.FileFormat = string.IsNullOrWhiteSpace(request.FileFormat) ? "auto" : request.FileFormat.Trim().ToLowerInvariant();
        source.SyncIntervalHours = Math.Clamp(request.SyncIntervalHours ?? source.SyncIntervalHours, 1, 336);
        source.IsEnabled = request.IsEnabled;

        // Per-source column mapping (D3) — not a secret; null = keep, empty = clear, value = set.
        if (request.ColumnMapping is not null)
            source.ColumnMappingJson = NormalizeColumnMapping(request.ColumnMapping);

        if (isVendor)
        {
            // Vendor fetcher (e.g. aes2fa): URL + encrypted vendor-credential envelope. Clears
            // the file-channel columns and the http auth-method (auth is inside the vendor creds).
            source.Url = request.Url?.Trim();
            source.Host = string.Empty;
            source.Port = 0;
            source.Username = null;
            source.RemotePath = string.Empty;
            source.EncryptedPassword = null;
            // Switching off sftp leaves the pin naming a server this source no longer talks to.
            source.HostKeyFingerprints = null;
            source.HttpMethod = "GET";
            source.AuthMethod = null;

            // Write-only vendor creds: null = keep, no-usable-fields = clear, value = re-encrypt.
            var vendorJson = BuildVendorConfigJson(protocol, request.VendorConfig);
            if (vendorJson is not null)
                source.AuthConfigEncrypted = vendorJson.Length == 0
                    ? null
                    : _encryption.Encrypt(vendorJson, CredentialScope.ForSupplier(
                        source.OrgId, CredentialPurpose.SupplierCatalogAuthConfig, source.Id));
        }
        else if (isHttp)
        {
            // http/https: the full URL carries scheme+host+path+query; host/port/username/
            // remote-path are unused (cleared so a protocol switch leaves no stale values).
            source.Url = request.Url?.Trim();
            source.Host = string.Empty;
            source.Port = 0;
            source.Username = null;
            source.RemotePath = string.Empty;
            source.EncryptedPassword = null;
            // Switching off sftp leaves the pin naming a server this source no longer talks to.
            source.HostKeyFingerprints = null;
            source.HttpMethod = "GET"; // v2: GET only (POST-with-body out of scope).
            source.AuthMethod = NormalizeAuthMethod(request.AuthMethod);

            // Write-only auth-config secret: null = keep, cleared = clear, value = re-encrypt.
            var authJson = BuildAuthConfigJson(source.AuthMethod, request.AuthConfig);
            if (authJson is not null)
            {
                source.AuthConfigEncrypted = authJson.Length == 0
                    ? null
                    : _encryption.Encrypt(authJson, CredentialScope.ForSupplier(
                        source.OrgId, CredentialPurpose.SupplierCatalogAuthConfig, source.Id));
            }
            // 'none' carries no secret — drop any previously stored config.
            if (source.AuthMethod == "none")
                source.AuthConfigEncrypted = null;
        }
        else
        {
            // sftp/ftp/ftps: clear the http columns so a protocol switch leaves no stale state.
            var newHost = request.Host.Trim();
            var newPort = request.Port > 0 ? request.Port : (protocol == "sftp" ? 22 : 21);

            // A host-key pin names a SERVER. Repointing the source at a different host or port — or
            // off sftp entirely — makes the stored fingerprint describe something this source no
            // longer talks to, and leaving it there would refuse the new server forever with a
            // message about a rebuild that never happened. Cleared BEFORE the request's own
            // explicit value is applied below, so an operator supplying both still wins.
            if (protocol != "sftp"
                || !string.Equals(source.Host, newHost, StringComparison.OrdinalIgnoreCase)
                || source.Port != newPort)
            {
                source.HostKeyFingerprints = null;
            }

            source.Host = newHost;
            source.Port = newPort;
            source.Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
            source.RemotePath = request.RemotePath.Trim();
            source.Url = null;
            source.AuthMethod = null;
            source.AuthConfigEncrypted = null;
            source.HttpMethod = null;

            // Write-only secret semantics (precedent PullIngressSettingsService): null = keep,
            // "" = clear, value = re-encrypt. Plaintext is never stored or echoed back.
            if (request.Password is not null)
            {
                source.EncryptedPassword = string.IsNullOrWhiteSpace(request.Password)
                    ? null
                    : _encryption.Encrypt(request.Password, CredentialScope.ForSupplier(
                        source.OrgId, CredentialPurpose.SupplierCatalogPassword, source.Id));
            }

            // Host-key pin: null = keep what the last successful sync recorded (a client that has
            // never heard of host keys must not silently un-pin the source), "" = the operator
            // deliberately re-trusting after a server rebuild.
            if (request.HostKeyFingerprints is not null)
            {
                source.HostKeyFingerprints = SshHostKeyPolicy.Serialise(
                    SshHostKeyPolicy.Parse(request.HostKeyFingerprints));
            }
        }

        source.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        // Enable-transition enqueue (replaces sync-now): only on false→true, and no-op when
        // a sync is already running or one happened within the dedupe window (M1 residual).
        var enqueued = false;
        if (request.IsEnabled && !wasEnabled)
        {
            var recentlySynced = priorLastSyncAt is not null && now - priorLastSyncAt.Value < EnqueueDedupeWindow;
            if (priorStatus != "running" && !recentlySynced)
            {
                var sourceId = source.Id;
                _jobs.Enqueue<CatalogSyncSourceJob>(j => j.ExecuteAsync(orgId, sourceId, CancellationToken.None));
                enqueued = true;
            }
        }

        return new CatalogSourceUpsertResult(ToResponse(source), enqueued);
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct)
    {
        var source = await _db.SupplierCatalogSources
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.SupplierId == supplierId, ct);
        if (source is null) return false;

        _db.SupplierCatalogSources.Remove(source);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static CatalogSourceResponse ToResponse(SupplierCatalogSource s)
    {
        var hasPassword = !string.IsNullOrWhiteSpace(s.EncryptedPassword);
        var hasAuthConfig = !string.IsNullOrWhiteSpace(s.AuthConfigEncrypted);
        return new CatalogSourceResponse(
            s.Id, s.Protocol, s.Host, s.Port, s.Username,
            hasPassword, hasPassword ? Mask : null,
            s.RemotePath, s.FileFormat, s.SyncIntervalHours, s.IsEnabled,
            s.LastSyncAt, s.LastSyncStatus, s.LastSyncError,
            s.LastSyncCreated, s.LastSyncUpdated, s.LastSyncSkipped,
            s.UpdatedAt,
            // http/https: surface the URL (no secrets) + the auth method + whether auth secrets
            // are stored. The ciphertext (AuthConfigEncrypted) and any plaintext NEVER leave here.
            s.Url, s.AuthMethod, hasAuthConfig, s.HttpMethod,
            // Column mapping is NOT a secret — echoed back so the editor can show/edit it.
            DecodeMapping(s.ColumnMappingJson),
            // Neither is a host-key fingerprint, and an operator cannot judge a changed one without
            // seeing what it is being compared against.
            s.HostKeyFingerprints);
    }

    private static IReadOnlyDictionary<string, string>? DecodeMapping(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Normalizes the http auth method; unknown/empty → 'none'.</summary>
    private static string NormalizeAuthMethod(string? method)
    {
        var m = method?.Trim().ToLowerInvariant();
        return m is "apikey" or "bearer" or "basic" or "oauth2_client_credentials" ? m : "none";
    }

    /// <summary>
    /// Builds the canonical auth-config JSON (the SAME shape <c>HttpAuthApplier</c> reads:
    /// a <c>type</c> discriminator plus the method's fields) from the PUT body, or returns:
    ///  • <c>null</c> when no <see cref="CatalogHttpAuthConfig"/> was supplied AND the method
    ///    is not 'none' → KEEP the stored secret;
    ///  • <c>""</c> when the secret should be CLEARED (method 'none', or the supplied config has
    ///    no usable secret for the method) → the caller clears the ciphertext;
    ///  • a JSON string to ENCRYPT otherwise.
    /// Plaintext is never stored or echoed back.
    /// </summary>
    private static string? BuildAuthConfigJson(string authMethod, CatalogHttpAuthConfig? cfg)
    {
        if (authMethod == "none")
            return string.Empty; // no secret for this method — clear any stored config

        if (cfg is null)
            return null; // not provided → keep whatever is stored (write-only "null = keep")

        // Project only the fields the chosen method uses into the canonical applier shape.
        object? payload = authMethod switch
        {
            "apikey" when NotBlank(cfg.ApiKeyHeader) && cfg.ApiKeyValue is not null =>
                new { type = "apikey", header = cfg.ApiKeyHeader!.Trim(), value = cfg.ApiKeyValue },
            "bearer" when NotBlank(cfg.BearerToken) =>
                new { type = "bearer", token = cfg.BearerToken },
            "basic" when cfg.BasicUsername is not null || cfg.BasicPassword is not null =>
                new { type = "basic", username = cfg.BasicUsername ?? string.Empty, password = cfg.BasicPassword ?? string.Empty },
            "oauth2_client_credentials" when NotBlank(cfg.TokenUrl) =>
                new
                {
                    type = "oauth2_client_credentials",
                    tokenUrl = cfg.TokenUrl!.Trim(),
                    clientId = cfg.ClientId ?? string.Empty,
                    clientSecret = cfg.ClientSecret ?? string.Empty,
                    scope = cfg.Scope ?? string.Empty,
                },
            _ => null,
        };

        // A config object that carries no usable secret for the method clears the stored one.
        return payload is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(payload);

        static bool NotBlank(string? v) => !string.IsNullOrWhiteSpace(v);
    }

    /// <summary>
    /// Canonical catalog fields a mapping value may target. Derived from the parser's own list
    /// rather than restated here: this used to be a hand-copied duplicate, so a field added to
    /// the parser was silently DROPPED from any saved column mapping until someone remembered to
    /// update it too — with no error to notice.
    /// </summary>
    private static readonly HashSet<string> CanonicalFields =
        new(ProcuLink.Transform.Catalog.SupplierCatalogFileParser.CanonicalFields, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes a per-source column mapping (plan 2026-07-02 D3) to storable JSON: trims keys,
    /// keeps only canonical targets plus the recognized directives (<c>__noheader__</c>,
    /// <c>__encoding__</c>), caps at 100 entries. Returns <c>""</c> (clear) when nothing usable
    /// remains, else the serialized JSON. Not a secret.
    /// </summary>
    private static string NormalizeColumnMapping(IReadOnlyDictionary<string, string> mapping)
    {
        var clean = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, rawVal) in mapping)
        {
            if (clean.Count >= 100) break;
            var key = rawKey?.Trim();
            var val = rawVal?.Trim();
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(val)) continue;

            if (key is "__noheader__" or "__encoding__") { clean[key] = val!; continue; }
            if (CanonicalFields.Contains(val!)) clean[key!] = val!.ToLowerInvariant();
        }
        return clean.Count == 0 ? string.Empty : System.Text.Json.JsonSerializer.Serialize(clean);
    }

    /// <summary>
    /// Builds the write-only vendor-credential JSON for a vendor-fetcher protocol (plan 2026-07-02
    /// D4). null = keep stored, "" = clear (no usable fields), value = re-encrypt. Carries a
    /// <c>type</c> discriminator so the fetcher can validate the envelope shape.
    /// </summary>
    private static string? BuildVendorConfigJson(string protocol, CatalogVendorConfig? cfg)
    {
        if (cfg is null) return null; // keep whatever is stored

        if (protocol == "aes2fa")
        {
            var complete = !string.IsNullOrWhiteSpace(cfg.CustomerId)
                && !string.IsNullOrWhiteSpace(cfg.ConsumerKey)
                && !string.IsNullOrWhiteSpace(cfg.ConsumerSecret)
                && !string.IsNullOrWhiteSpace(cfg.AccessTokenKey);
            if (!complete) return string.Empty; // partial → clear

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "aes2fa_signed",
                customerId = cfg.CustomerId!.Trim(),
                consumerKey = cfg.ConsumerKey!.Trim(),
                consumerSecret = cfg.ConsumerSecret!.Trim(),
                accessTokenKey = cfg.AccessTokenKey!.Trim(),
                currency = string.IsNullOrWhiteSpace(cfg.Currency) ? "EUR" : cfg.Currency!.Trim(),
            });
        }

        return string.Empty; // unknown vendor protocol → no secret
    }
}
