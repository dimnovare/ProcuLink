namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Result returned by <see cref="IPromoteMappingService.PromoteAsync"/>.
/// </summary>
/// <param name="SupplierId">The supplier whose inbound mapping was updated.</param>
/// <param name="HeaderFieldsPromoted">Number of header canonical-field rules written into the supplier mapping.</param>
/// <param name="LineFieldsPromoted">Number of line canonical-field rules written into the supplier mapping.</param>
/// <param name="SchemaFingerprintHash">
/// The order's schema-fingerprint hash at the time of promotion, or <c>null</c> when the order has
/// no fingerprint recorded (header-less format, or parsed before fingerprinting was wired).
///
/// NOTE (design decision): <see cref="PoMappingService"/> stores mappings at the
/// <em>supplier</em> level — there is no per-fingerprint storage bucket in v1. The hash is
/// surfaced here for observability/debugging but the promoted rules are written into the single
/// supplier-level <see cref="PoMappingConfig"/> so they apply to ALL future orders from that
/// supplier regardless of layout variation. A per-fingerprint override store (routing different
/// layouts to different <see cref="PoMappingConfig"/>s) is a Horizon 3 / Group Q capability.
/// </param>
public sealed record PromoteMappingResult(
    Guid SupplierId,
    int HeaderFieldsPromoted,
    int LineFieldsPromoted,
    string? SchemaFingerprintHash);

/// <summary>
/// Promotes the SourceMap stored inside a per-order <see cref="OrderMappingOverride"/> to the
/// supplier's reusable inbound <see cref="PoMappingConfig"/> so future orders from the same
/// supplier with the same source layout are auto-mapped without user intervention.
///
/// <para>Contract:</para>
/// <list type="bullet">
///   <item>Org-scoped — a cross-tenant or unknown order id returns <c>null</c> (caller maps to 404).</item>
///   <item>Idempotent — re-promoting the same override overwrites (never duplicates) the rules.</item>
///   <item>Additive — canonical fields NOT in the SourceMap are preserved in the existing mapping
///        (the upsert merges the promoted entries into whatever PoMappingConfig already exists).
///   </item>
///   <item>SourceMap only — the <see cref="OrderMappingOverride.Output"/> side (canonical→output
///        re-mapping) is not promoted because <see cref="PoMappingConfig"/> models the
///        <em>inbound</em> (source→canonical) direction only. Output-side persistence is a
///        separate future capability — see <c>TODO(output-promote)</c> comment in the
///        implementation.
///   </item>
/// </list>
/// </summary>
public interface IPromoteMappingService
{
    /// <summary>
    /// Reads the order's stored <see cref="OrderMappingOverride.SourceMap"/>, translates each
    /// entry into an equivalent <see cref="FieldMappingEntry"/>, merges them into the supplier's
    /// existing <see cref="PoMappingConfig"/> (or creates one), and persists via
    /// <see cref="IPoMappingService.UpsertAsync"/>.
    ///
    /// Returns <c>null</c> when the order does not exist for <paramref name="orgId"/> (caller
    /// maps to 404). Returns a result with zero promoted fields when the override has no SourceMap
    /// (idempotent no-op — the mapping is unchanged).
    /// </summary>
    Task<PromoteMappingResult?> PromoteAsync(
        Guid orgId, Guid orderId, CancellationToken ct);
}
