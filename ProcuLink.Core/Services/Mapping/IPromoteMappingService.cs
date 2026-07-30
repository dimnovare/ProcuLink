namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Result returned by <see cref="IPromoteMappingService.PromoteAsync"/>. Reports EXACTLY what was
/// written so the caller (and the operator) can never be left guessing whether the "Save mappings
/// for &lt;supplier&gt;" button did anything — the founder's "seems not to save or give any errors"
/// complaint. When nothing was promotable, <see cref="NothingToPromote"/> is true and
/// <see cref="Message"/> explains why, instead of a silent success-with-no-effect.
/// </summary>
/// <param name="SupplierId">The supplier whose reusable mapping was updated.</param>
/// <param name="HeaderFieldsPromoted">Number of INBOUND header canonical-field rules (SourceMap) written into the supplier mapping.</param>
/// <param name="LineFieldsPromoted">Number of INBOUND line canonical-field rules (SourceMap) written into the supplier mapping.</param>
/// <param name="OutputHeaderFieldsPromoted">Number of OUTPUT header rules promoted into the supplier mapping's reusable output config.</param>
/// <param name="OutputLineFieldsPromoted">Number of OUTPUT line rules promoted into the supplier mapping's reusable output config.</param>
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
/// <param name="Message">
/// Human-readable summary of what happened — a confirmation when something was promoted, or a
/// clear reason when there was nothing to promote (so the UI never shows an empty success).
/// </param>
public sealed record PromoteMappingResult(
    Guid SupplierId,
    int HeaderFieldsPromoted,
    int LineFieldsPromoted,
    int OutputHeaderFieldsPromoted,
    int OutputLineFieldsPromoted,
    string? SchemaFingerprintHash,
    string Message)
{
    /// <summary>
    /// True when a structured OUTPUT TREE — the layout drawn in the visual output designer — was
    /// saved onto the supplier's reusable mapping (WP-12). A tree carries structure rather than a
    /// countable list of field rules, so it is reported as a flag rather than a count; it still
    /// counts as a real promotion for <see cref="NothingToPromote"/>.
    /// </summary>
    public bool OutputTreePromoted { get; init; }

    /// <summary>Total inbound (SourceMap) field rules promoted.</summary>
    public int InboundFieldsPromoted => HeaderFieldsPromoted + LineFieldsPromoted;

    /// <summary>Total output field rules promoted.</summary>
    public int OutputFieldsPromoted => OutputHeaderFieldsPromoted + OutputLineFieldsPromoted;

    /// <summary>Total of every kind of rule promoted (inbound + output).</summary>
    public int TotalFieldsPromoted => InboundFieldsPromoted + OutputFieldsPromoted;

    /// <summary>
    /// True when the order carried no promotable mapping at all (no SourceMap, no output mapping and
    /// no output tree), so the supplier mapping was left unchanged. The caller surfaces
    /// <see cref="Message"/> as a clear "nothing to save" notice rather than a misleading success.
    /// </summary>
    public bool NothingToPromote => TotalFieldsPromoted == 0 && !OutputTreePromoted;
}

/// <summary>
/// Promotes a per-order <see cref="OrderMappingOverride"/> (the source→canonical SourceMap AND the
/// canonical→output mapping) to the supplier's reusable <see cref="PoMappingConfig"/> so future
/// orders from the same supplier with the same layout reuse the work without further user review.
///
/// <para>Contract:</para>
/// <list type="bullet">
///   <item>Org-scoped — a cross-tenant or unknown order id returns <c>null</c> (caller maps to 404).</item>
///   <item>Idempotent — re-promoting the same override overwrites (never duplicates) the rules.</item>
///   <item>Additive — canonical fields NOT in the override are preserved in the existing mapping
///        (the upsert merges the promoted entries into whatever PoMappingConfig already exists).
///   </item>
///   <item>Both sides — the inbound <see cref="OrderMappingOverride.SourceMap"/> is translated into
///        <see cref="PoMappingConfig.Header"/> / <see cref="PoMappingConfig.Lines"/>, and the
///        <see cref="OrderMappingOverride.Output"/> mapping is stored on
///        <see cref="PoMappingConfig.Output"/> (an additive JSONB field, no migration). Both are
///        counted in the returned <see cref="PromoteMappingResult"/>.
///   </item>
///   <item>Never a silent no-op — when the order carries no SourceMap AND no output mapping, the
///        supplier mapping is left untouched and the result reports
///        <see cref="PromoteMappingResult.NothingToPromote"/> = true with an explanatory
///        <see cref="PromoteMappingResult.Message"/>.
///   </item>
/// </list>
///
/// <para>Consumption (launch batch 4A):</para> the supplier-level <see cref="PoMappingConfig.Output"/>
/// is PERSISTED here AND CONSUMED by the transform path — when a future order from this supplier
/// carries no usable per-order template/output override, <c>OrderTransformService</c> (and the
/// mapping-override preview / replay current side) apply the promoted output mapping. The per-order
/// override always stays the higher-priority seam.
/// </summary>
public interface IPromoteMappingService
{
    /// <summary>
    /// Reads the order's stored <see cref="OrderMappingOverride"/>, translates the inbound SourceMap
    /// into <see cref="FieldMappingEntry"/> rules and copies the output mapping verbatim, merges both
    /// into the supplier's existing <see cref="PoMappingConfig"/> (or creates one), and persists via
    /// <see cref="IPoMappingService.UpsertAsync"/>.
    ///
    /// Returns <c>null</c> when the order does not exist for <paramref name="orgId"/> (caller maps to
    /// 404). Returns a result with <see cref="PromoteMappingResult.NothingToPromote"/> = true (and the
    /// supplier mapping unchanged) when the override has neither a SourceMap nor an output mapping.
    /// </summary>
    Task<PromoteMappingResult?> PromoteAsync(
        Guid orgId, Guid orderId, CancellationToken ct);

    /// <summary>
    /// UN-PROMOTE: removes the supplier's stored <see cref="PoMappingConfig.OutputTree"/>, leaving
    /// every other member of the config (inbound rules, flat output mapping) untouched. Returns
    /// <c>true</c> when a tree was present and removed, <c>false</c> when there was nothing to remove
    /// (idempotent — a second call is a no-op, not an error).
    ///
    /// <para>Exists because promotion used to be a ONE-WAY door: the merge preserved
    /// <c>existing.OutputTree</c> unconditionally, so a layout that could never render this
    /// supplier's document survived every subsequent promote with no way to take it back. Every
    /// promote now also re-checks and drops a stored tree that cannot deliver; this is the explicit
    /// version of the same thing, for an operator who simply wants their layout gone.</para>
    /// </summary>
    Task<bool> ClearPromotedOutputTreeAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}
