using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// Launch batch 7 — REVISION AUTHORITY. <b>ON in production</b> —
/// <c>Connections__RevisionAuthority = true</c> on both Railway services (<c>ProcuLink</c> and
/// <c>aware-amazement</c>), verified 2026-07-27 and re-verified 2026-07-31; the code default being
/// OFF says nothing about the deployed value. Read the effective value from
/// <c>GET /health/ready</c> (<c>revisionAuthority</c>) or a host's startup log, never from an
/// appsettings file.
///
/// <para>Resolves the effective configuration bundle for ONE
/// order: when the <c>Connections:RevisionAuthority</c> flag is ON and the order carries a
/// resolvable <c>ConnectionRevisionId</c> pin, the bundle is the pinned PUBLISHED revision's
/// immutable snapshot; in every other case (flag off, unpinned order, orphaned pin, lookup
/// failure) it is <see cref="EffectiveConnectionConfig.Live"/>, which tells the caller to keep
/// reading the live mutable tables — byte-for-byte the pre-batch-7 behaviour.</para>
///
/// <para>Consumed by the four processing services (parse-mapping + item-code resolution,
/// acceptance validation, output transform, delivery dispatch) so a pinned order is processed
/// under the SAME contract it was ingested with, regardless of later live-config edits.
/// The catalog is intentionally NOT part of this bundle — <c>CatalogMode</c> is "live" in V1
/// and catalog reads stay live at ingest.</para>
/// </summary>
public interface IEffectiveConnectionConfigResolver
{
    /// <summary>
    /// Resolve the effective bundle for an order pinned to <paramref name="connectionRevisionId"/>
    /// (null = unpinned). Org-scoped; never throws — any miss falls back to
    /// <see cref="EffectiveConnectionConfig.Live"/>.
    /// </summary>
    Task<EffectiveConnectionConfig> ResolveAsync(
        Guid orgId, Guid? connectionRevisionId, CancellationToken ct);
}

/// <summary>One item-code mapping from a pinned revision's snapshot (immutable copy of the row set published with the revision).</summary>
public sealed record EffectiveRevisionItemMapping(string BuyerItemCode, string SupplierItemCode);

/// <summary>
/// The effective configuration bundle driving one order's processing. Either the LIVE marker
/// (read the live mutable tables, exactly today's behaviour) or a snapshot of one pinned
/// published <see cref="SupplierConnectionRevision"/>.
///
/// <para>Granular-fallback contract: even for a revision bundle, a NULL/blank component means
/// "this revision snapshotted no value for that component" and each consumer falls back to its
/// existing live source for that component alone (logged) — a backfilled rev-1 with, say, a null
/// output mapping must keep behaving like today's fixed transformer.</para>
/// </summary>
public sealed record EffectiveConnectionConfig
{
    /// <summary>The live-tables marker: flag off / unpinned / orphan pin — keep today's reads.</summary>
    public static EffectiveConnectionConfig Live { get; } = new();

    /// <summary>True when a pinned published revision drives processing.</summary>
    public bool IsRevision => RevisionId is not null;

    /// <summary>The pinned revision id (null for the live bundle).</summary>
    public Guid? RevisionId { get; init; }

    /// <summary>Provenance marker: <c>"revision:{id}"</c> for a revision bundle, else <c>"live"</c>.</summary>
    public string Source => RevisionId is null ? "live" : $"revision:{RevisionId}";

    // ── Parse / input mapping ────────────────────────────────────────────────
    /// <summary>Snapshot of the serialized <c>PoMappingConfig</c> (SupplierPoMapping.ConfigJson). Null = none snapshotted.</summary>
    public string? InputMappingJson { get; init; }

    /// <summary>Item-code mapping snapshot. Empty = the revision snapshotted no item mappings.</summary>
    public IReadOnlyList<EffectiveRevisionItemMapping> ItemMappings { get; init; }
        = Array.Empty<EffectiveRevisionItemMapping>();

    // ── Validation binding ───────────────────────────────────────────────────
    public Guid? AcceptanceProfileId { get; init; }
    public int?  AcceptanceVersionNo { get; init; }

    // ── Output ───────────────────────────────────────────────────────────────
    /// <summary>Snapshot of the serialized <c>OutputMappingConfig</c>. Null = fixed transformer.</summary>
    public string? OutputMappingJson { get; init; }

    /// <summary>'xml' | 'csv' | 'cxml' | 'json' | 'ubl' | 'x12'. Null = caller's resolved format.</summary>
    public string? OutputFormat { get; init; }

    // ── Delivery channel ─────────────────────────────────────────────────────
    /// <summary>'http' | 'sftp' | 'ftp' | 'erp_erply' | 'erp_directo'. Null/blank = no channel snapshotted (live fallback).</summary>
    public string? DeliveryProtocol { get; init; }

    /// <summary>Non-secret delivery config jsonb snapshot.</summary>
    public string? DeliveryConfigJson { get; init; }

    public bool DeliveryAutoDeliver { get; init; }

    /// <summary>
    /// Authenticated-encrypted credential payload snapshot. The backfill / draft snapshot copies
    /// <c>SupplierDeliveryConfig.EncryptedCredentials</c> VERBATIM, so the existing
    /// <c>DeliveryEncryptionService.Decrypt</c> path applies unchanged.
    /// </summary>
    public string? CredentialsRef { get; init; }

    /// <summary>Builds the revision bundle from a loaded (org-scoped) revision row, item mappings included.</summary>
    public static EffectiveConnectionConfig FromRevision(SupplierConnectionRevision revision) => new()
    {
        RevisionId          = revision.Id,
        InputMappingJson    = revision.InputMappingJson,
        ItemMappings        = revision.ItemMappings
            .Select(m => new EffectiveRevisionItemMapping(m.BuyerItemCode, m.SupplierItemCode))
            .ToList(),
        AcceptanceProfileId = revision.AcceptanceProfileId,
        AcceptanceVersionNo = revision.AcceptanceVersionNo,
        OutputMappingJson   = revision.OutputMappingJson,
        OutputFormat        = revision.OutputFormat,
        DeliveryProtocol    = revision.DeliveryProtocol,
        DeliveryConfigJson  = revision.DeliveryConfigJson,
        DeliveryAutoDeliver = revision.DeliveryAutoDeliver,
        CredentialsRef      = revision.CredentialsRef,
    };
}
