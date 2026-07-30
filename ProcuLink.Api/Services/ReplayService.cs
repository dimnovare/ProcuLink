using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Services;

/// <summary>
/// Group V2 — REPLAY / impact testing. Runs historical orders through a connection revision
/// (typically a DRAFT being evaluated before publish, but any read-only revision works) and returns
/// a DIFF per order vs. the order's CURRENT result, so an operator can answer "if I publish this
/// revision, what changes?" BEFORE publishing.
///
/// <para><b>Non-mutating + never delivers.</b> Replay re-runs the EXISTING transform engine
/// (<see cref="MappedTransformService"/> / <see cref="EffectiveEntityResolver"/> /
/// <see cref="ScribanTemplateTransformService"/> / the fixed <see cref="ITransformService"/>s) and the
/// EXISTING validation engine (<see cref="SupplierAcceptanceService.EvaluateProfile"/>) entirely
/// IN-MEMORY. It writes no orders, no artifacts, no validation rows; it never uploads to storage and
/// never calls a delivery dispatcher. Loaded entities are AsNoTracking; rendering operates on detached
/// clones so a replayed order is never accidentally persisted.</para>
///
/// <para><b>Bounded.</b> The order count per call is capped at <see cref="MaxOrders"/>. An explicit id
/// list is truncated to the cap; an empty list resolves to the most recent N (also capped) orders for
/// the revision's supplier.</para>
///
/// <para><b>What is compared.</b>
/// <list type="bullet">
///   <item><description>Output text — the revision's would-be output vs. the order's CURRENT would-be
///   output (re-derived deterministically with the live transform's effective priority: per-order
///   <c>mappingOverride</c> → pinned revision output [flag on + pinned, read-parity] / supplier-promoted
///   output [unpinned] → fixed transformer). A failure to render the revision side surfaces as
///   <c>OutputError</c>, never a crash.</description></item>
///   <item><description>Effective canonical values — per-field header/line value changes the revision's
///   mapping would introduce.</description></item>
///   <item><description>Validation — pass/fail under the order's current active profile vs. the
///   revision's BOUND acceptance profile, reusing the acceptance evaluator (no re-implementation).</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class ReplayService : IReplayService
{
    /// <summary>Hard cap on orders replayed per call (bounded + safe).</summary>
    public const int MaxOrders = 50;

    /// <summary>Default output format used when neither the revision nor the supplier's active config names one.</summary>
    private const OutputFormat DefaultFormat = OutputFormat.Csv;

    private readonly ProcuLinkDbContext             _db;
    private readonly IEnumerable<ITransformService> _transformers;
    private readonly IPoMappingService              _poMappings;
    private readonly IEffectiveConnectionConfigResolver? _effectiveConfig;
    private readonly IFileStorageService?           _fileStorage;
    private readonly OrderParserFactory?            _parserFactory;
    private readonly IItemMappingService            _itemMappings;

    public ReplayService(
        ProcuLinkDbContext             db,
        IEnumerable<ITransformService> transformers,
        IPoMappingService?             poMappings = null,
        IEffectiveConnectionConfigResolver? effectiveConfig = null,
        IFileStorageService?           fileStorage = null,
        OrderParserFactory?            parserFactory = null,
        IItemMappingService?           itemMappings = null)
    {
        _db           = db;
        _transformers = transformers;
        // Optional so existing positional constructions stay valid; the concrete service only
        // needs the same DbContext. Used so the CURRENT side of a replay diff mirrors the live
        // transform's supplier-promoted-output fallback (launch batch 4A).
        _poMappings   = poMappings ?? new ProcuLink.Infrastructure.Services.PoMappingService(db);
        // Read-parity (launch batch 7 follow-up) — revision authority for the CURRENT side: when the
        // Connections:RevisionAuthority flag is ON and a replayed order is PINNED, the current side
        // of the diff must mirror the runtime priority (per-order override → pinned revision output →
        // fixed; supplier-promoted NOT consulted). Null (older positional ctors) = flag-OFF behaviour.
        _effectiveConfig = effectiveConfig;
        // Replay flip A — parse-from-source leg dependencies. Both are optional so older positional
        // constructions stay valid; when either is missing and a parse leg is requested, the leg is
        // recorded as skipped-with-note per order (never a throw). The file download goes through
        // IFileStorageService.DownloadAsync EXACTLY (the R2 pre-signed-URL path).
        _fileStorage   = fileStorage;
        _parserFactory = parserFactory;
        // Live item-mapping fallback for the parse leg when the revision snapshotted NO item
        // mappings — mirrors the runtime contract in OrderIngestionService.BuildLineEntitiesAsync
        // (empty snapshot ⇒ live table). Same self-construction pattern as _poMappings.
        _itemMappings  = itemMappings ?? new ProcuLink.Infrastructure.Services.ItemMappingService(db);
    }

    public async Task<ReplayResponse?> ReplayAsync(
        Guid orgId, Guid connectionId, Guid revisionId, ReplayRequest request, CancellationToken ct)
    {
        // Resolve the revision being replayed (org-scoped). Published/archived revisions can be
        // replayed read-only too — there is no status gate here (replay never mutates anything).
        var revision = await _db.SupplierConnectionRevisions
            .AsNoTracking()
            .Include(r => r.ItemMappings)
            .Where(r => r.OrgId == orgId && r.ConnectionId == connectionId && r.Id == revisionId)
            .FirstOrDefaultAsync(ct);
        if (revision is null) return null;

        // The connection (for cross-checking it belongs to this org + to resolve the supplier's
        // current active revision for the "current output format" side of the diff).
        var connection = await _db.SupplierConnections
            .AsNoTracking()
            .Where(c => c.OrgId == orgId && c.Id == connectionId)
            .FirstOrDefaultAsync(ct);
        if (connection is null) return null;

        var orders = await LoadOrdersAsync(orgId, revision.SupplierId, request, ct);

        // The revision's output config + format (the "draft" side).
        //
        // WP-12: the STRUCTURED output tree rides inside InputMappingJson (a byte-identical snapshot of
        // the whole PoMappingConfig), not in OutputMappingJson. Reading only the latter meant a
        // connection whose entire output IS a designed layout replayed as the FIXED document on both
        // sides — the diff screen answered "nothing changes" for a revision that changes everything,
        // and OrderTransformService's own comment promising that replay reads the same snapshot
        // identically stopped being true.
        var draftOutputConfig = DeserializeOutputConfig(revision.OutputMappingJson);
        var draftOutputTree   = DeserializeSnapshotTree(revision.InputMappingJson);
        var draftFormat       = ParseFormat(revision.OutputFormat) ?? DefaultFormat;

        // The order's CURRENT output format: prefer the connection's active published revision's format,
        // else the same default. (The current OUTPUT MAPPING is the order's own per-order override.)
        var currentFormat = await ResolveCurrentFormatAsync(orgId, connection, ct) ?? DefaultFormat;

        // The revision's BOUND acceptance profile (the "draft" validation side). Bind by id; never copy.
        var draftProfile = await LoadProfileAsync(orgId, revision.AcceptanceProfileId, ct);

        // The order's CURRENT active acceptance profile (supplier-level), loaded once. The connection is
        // per-supplier, so all replayed orders compare against the same supplier's active profile. Mirrors
        // SupplierAcceptanceService.GetActiveAsync so the "current validation" side matches what
        // ValidateOrderAsync would persist today.
        var currentProfile = await LoadActiveProfileAsync(orgId, revision.SupplierId, ct);

        // launch batch 4A — the CURRENT side must mirror the live transform's effective priority:
        // per-order override → supplier-promoted output (PoMappingConfig.Output) → fixed transformer.
        // Loaded ONCE per replay; defensive — a missing/malformed/unusable supplier mapping yields
        // null and the current side keeps its existing (per-order override / fixed) behaviour.
        OutputMappingConfig? supplierPromotedOutput = null;
        OutputNodeTemplate?  supplierPromotedTree   = null;
        try
        {
            var supplierConfig = await _poMappings.GetAsync(orgId, revision.SupplierId, ct);
            if (OrderMappingOverrideReader.HasUsablePromotedOutput(supplierConfig))
                supplierPromotedOutput = supplierConfig!.Output;
            // WP-12: the promoted TREE drives live delivery too, so the current side must see it or
            // the diff invents a change that is not real.
            if (OrderMappingOverrideReader.HasUsablePromotedOutputTree(supplierConfig))
                supplierPromotedTree = supplierConfig!.OutputTree;
        }
        catch
        {
            // A malformed supplier mapping must never abort a replay — fall back to fixed.
        }

        // ── Replay flip A — parse-from-source leg context (resolved ONCE per replay) ─────────
        // The DRAFT revision's parse mapping with the SAME priority the live parse applies
        // (OrderIngestionService.ParseStoredFileAsync): a USABLE input-mapping snapshot governs;
        // a null/blank/empty/malformed snapshot falls back to the LIVE supplier PO mapping.
        // The item-mapping snapshot is the revision's own child rows (already Include'd above).
        PoMappingConfig? parseLegMapping = null;
        IReadOnlyList<EffectiveRevisionItemMapping> parseLegItemSnapshot = Array.Empty<EffectiveRevisionItemMapping>();
        if (request.IncludeParseLeg)
        {
            parseLegMapping = OrderIngestionService.TryDeserializeSnapshotPoMapping(revision.InputMappingJson).Config;
            if (parseLegMapping is null)
            {
                try { parseLegMapping = await _poMappings.GetAsync(orgId, revision.SupplierId, ct); }
                catch { /* a malformed live mapping must never abort a replay — parse without a template */ }
            }
            parseLegItemSnapshot = revision.ItemMappings
                .Select(m => new EffectiveRevisionItemMapping(m.BuyerItemCode, m.SupplierItemCode))
                .ToList();
        }

        var now = DateTime.UtcNow;

        // D7 — replay must see the SAME value universe delivery sees. OrderTransformService batch-loads
        // the supplier's catalog once per transform and re-derives the source-token set from the order's
        // persisted SourceCapture; replay emitted trees with neither, so every leaf bound to a source
        // token (F-1) or a catalog field rendered EMPTY. Because BOTH replay sides were equally blind, a
        // revision that changed only token-bound leaves reported "nothing changes" — the exact failure
        // this engine exists to catch. Loaded once here: replay is scoped to ONE supplier.
        var catalogLookup = await OrderServiceShared.BuildCatalogLookupAsync(
            _db, orgId, revision.SupplierId, ct);

        var diffs = new List<ReplayOrderDiffDto>(orders.Count);
        foreach (var order in orders)
        {
            // Read-parity: resolve the order's pinned-revision bundle through the SAME resolver the
            // runtime uses (flag off / unpinned / orphan pin / null resolver = Live, byte-identical).
            // Per-order because each replayed order may be pinned to a different revision.
            var effective = _effectiveConfig is null
                ? EffectiveConnectionConfig.Live
                : await _effectiveConfig.ResolveAsync(orgId, order.ConnectionRevisionId, ct);
            var diff = BuildDiff(order, draftOutputConfig, draftOutputTree, draftFormat, currentFormat,
                                 currentProfile, draftProfile, now, supplierPromotedOutput, supplierPromotedTree, effective,
                                 catalogLookup);

            // Opt-in parse-from-source leg: default-off keeps the existing contract byte-identical.
            if (request.IncludeParseLeg)
                diff = diff with
                {
                    ParseLeg = await BuildParseLegAsync(
                        order, parseLegMapping, parseLegItemSnapshot, orgId, revision.SupplierId, ct),
                };

            diffs.Add(diff);
        }

        return new ReplayResponse(
            connectionId, revisionId, revision.VersionNo, revision.Status, diffs.Count, diffs);
    }

    // ── Order loading (bounded, org-scoped, no-tracking) ──────────────────────

    private async Task<List<PurchaseOrderEntity>> LoadOrdersAsync(
        Guid orgId, Guid supplierId, ReplayRequest request, CancellationToken ct)
    {
        if (request.OrderIds is { Count: > 0 } ids)
        {
            // Explicit ids — org-scoped, capped. (Ids outside the org / supplier simply don't match.)
            var capped = ids.Distinct().Take(MaxOrders).ToList();
            var loaded = await _db.PurchaseOrders
                .AsNoTracking()
                .Include(o => o.Lines)
                .Include(o => o.Supplier)
                .Include(o => o.Parties)        // Phase 2 (D slice): not_label / vat_format
                .Include(o => o.SourceCapture)  // Phase 2 (D slice): date_sanity raw printed date
                .Where(o => o.OrgId == orgId && capped.Contains(o.Id))
                .ToListAsync(ct);
            // Preserve the caller's id order for a stable diff list.
            var byId = loaded.ToDictionary(o => o.Id);
            return capped.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        // Recent window for the revision's supplier (most-recent first), capped.
        var limit = Math.Clamp(request.RecentLimit <= 0 ? 20 : request.RecentLimit, 1, MaxOrders);
        return await _db.PurchaseOrders
            .AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Supplier)
            .Include(o => o.Parties)        // Phase 2 (D slice): not_label / vat_format
            .Include(o => o.SourceCapture)  // Phase 2 (D slice): date_sanity raw printed date
            .Where(o => o.OrgId == orgId && o.SupplierId == supplierId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    private async Task<OutputFormat?> ResolveCurrentFormatAsync(
        Guid orgId, SupplierConnection connection, CancellationToken ct)
    {
        if (connection.ActiveRevisionId is null) return null;
        var fmt = await _db.SupplierConnectionRevisions
            .AsNoTracking()
            .Where(r => r.OrgId == orgId && r.Id == connection.ActiveRevisionId)
            .Select(r => r.OutputFormat)
            .FirstOrDefaultAsync(ct);
        return ParseFormat(fmt);
    }

    private async Task<SupplierAcceptanceProfile?> LoadProfileAsync(Guid orgId, Guid? profileId, CancellationToken ct)
    {
        if (profileId is null) return null;
        return await _db.SupplierAcceptanceProfiles
            .AsNoTracking()
            .Include(p => p.Rules)
            .Where(p => p.OrgId == orgId && p.Id == profileId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The supplier's current active acceptance profile (the "current validation" side), loaded once
    /// no-tracking. Mirrors <see cref="SupplierAcceptanceService.GetActiveAsync"/> so the current side
    /// matches what <c>ValidateOrderAsync</c> would persist today.
    /// </summary>
    private async Task<SupplierAcceptanceProfile?> LoadActiveProfileAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        await _db.SupplierAcceptanceProfiles
            .AsNoTracking()
            .Include(p => p.Rules)
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.Status == "active")
            .FirstOrDefaultAsync(ct);

    // ── Per-order diff ────────────────────────────────────────────────────────

    private ReplayOrderDiffDto BuildDiff(
        PurchaseOrderEntity order,
        OutputMappingConfig? draftOutputConfig,
        OutputNodeTemplate? draftOutputTree,
        OutputFormat draftFormat,
        OutputFormat currentFormat,
        SupplierAcceptanceProfile? currentProfile,
        SupplierAcceptanceProfile? draftProfile,
        DateTime now,
        OutputMappingConfig? supplierPromotedOutput,
        OutputNodeTemplate? supplierPromotedTree,
        EffectiveConnectionConfig effective,
        IReadOnlyDictionary<string, SupplierProduct> catalogLookup)
    {
        // The order's own addressable source-token universe (D7), rebuilt from the persisted capture
        // exactly as OrderTransformService does — same helper, same JSON, so the two cannot diverge.
        var sourceTokens = SourceTokenSerialization.FromTokensJson(order.SourceCapture?.TokensJson);
        // ── Output side ────────────────────────────────────────────────────────
        // CURRENT: mirrors the live transform's effective priority so the diff compares against what
        // delivery ACTUALLY produces today.
        //   • PINNED (flag on, read-parity): per-order override → the PINNED revision's output
        //     snapshot → fixed transformer, at the PINNED revision's snapshotted format. The live
        //     supplier-promoted mapping is intentionally NOT consulted (mutable table — exactly
        //     OrderTransformService's reproducibility rule for a pinned order).
        //   • LIVE (flag off / unpinned / orphan pin): the order's own per-order override, ELSE the
        //     supplier-promoted output mapping (launch batch 4A), ELSE the fixed transformer — at
        //     the current format. Byte-identical to the pre-read-parity behaviour.
        var currentOverride   = OrderMappingOverrideReader.Read(order.CanonicalJson);
        var currentSideFormat = currentFormat;
        OrderMappingOverride? currentEffective;
        if (effective.IsRevision)
        {
            currentEffective =
                currentOverride?.OutputTree is not null
                || OrderMappingOverrideReader.HasUsableTemplate(currentOverride)
                || OrderMappingOverrideReader.HasUsableOutput(currentOverride)
                    ? currentOverride
                    // Unusable/null revision snapshot → the per-order override unchanged (a
                    // custom-fields-only override still falls through to the fixed transformer).
                    // Both halves of the bundle are read, exactly as the transform reads them.
                    : BuildRevisionOverride(
                          currentOverride,
                          DeserializeOutputConfig(effective.OutputMappingJson),
                          DeserializeSnapshotTree(effective.InputMappingJson))
                      ?? currentOverride;
            currentSideFormat = ParseFormat(effective.OutputFormat) ?? currentFormat;
        }
        else
        {
            currentEffective = ResolveCurrentEffectiveOverride(currentOverride, supplierPromotedOutput, supplierPromotedTree);
        }
        var current = Render(order, currentEffective, currentSideFormat, sourceTokens, catalogLookup);

        // DRAFT/replayed: the revision's output config wrapped as an override, at the revision's format.
        // The order's existing CustomFields are preserved so custom-field references in the revision's
        // output rules still resolve; only the OUTPUT mapping + format come from the revision.
        var draftOverride = BuildRevisionOverride(currentOverride, draftOutputConfig, draftOutputTree);
        var draft = Render(order, draftOverride, draftFormat, sourceTokens, catalogLookup);

        var outputChanged = current.Ok && draft.Ok && !string.Equals(current.Text, draft.Text, StringComparison.Ordinal);

        // ── Effective-value diff (header + line) ─────────────────────────────────
        var effectiveChanges = BuildEffectiveValueChanges(order, currentEffective, draftOverride);

        // ── Validation side (reuse SupplierAcceptanceService.EvaluateProfile) ────
        var currentResults = SupplierAcceptanceService.EvaluateProfile(order.OrgId, order.Id, currentProfile, order, now);
        var draftResults   = SupplierAcceptanceService.EvaluateProfile(order.OrgId, order.Id, draftProfile, order, now);

        var currentSummary = Summarise(currentResults, currentProfile is not null);
        var draftSummary   = Summarise(draftResults, draftProfile is not null);
        var flips          = BuildValidationFlips(currentResults, draftResults);
        var validationChanged = currentSummary.Passed != draftSummary.Passed || flips.Count > 0;

        return new ReplayOrderDiffDto(
            order.Id, order.PoNumber, draftFormat.ToString(),
            outputChanged, current.Text, draft.Text, draft.Error,
            effectiveChanges,
            validationChanged, currentSummary, draftSummary, flips);
    }

    // ── Output rendering (in-memory; mirrors OrderTransformService precedence, never writes) ─────

    private readonly record struct RenderResult(bool Ok, string? Text, string? Error)
    {
        public static RenderResult Success(string text) => new(true, text, null);
        public static RenderResult Failure(string error) => new(false, null, error);
    }

    /// <summary>
    /// Render an order to output text using the EXACT same four-mode precedence as
    /// <see cref="OrderTransformService"/> (template → native CSV/JSON override → structured override
    /// via <see cref="EffectiveEntityResolver"/> → fixed transformer), but in-memory: the result stream
    /// is read to a string and discarded; nothing is uploaded or persisted. Any failure (unresolved
    /// lines, broken template, unsupported format) is returned as a <see cref="RenderResult"/> error so
    /// replay never throws on a single bad order.
    /// </summary>
    private RenderResult Render(
        PurchaseOrderEntity order, OrderMappingOverride? @override, OutputFormat format,
        IReadOnlyList<ProcuLink.Transform.Tokenizing.SourceToken> sourceTokens,
        IReadOnlyDictionary<string, SupplierProduct> catalogLookup)
    {
        try
        {
            // WP-12: the structured tree is the HIGHEST-precedence mode, exactly as in the transform,
            // and RENDERABILITY is asked of the same shared predicate the emitter dispatches on — cXML/X12
            // carry only envelope identity, UBL/Peppol/X12-850/EDIFACT carry nothing, and handing either
            // to the emitter throws.
            //
            // Format EQUALITY is deliberately NOT checked here, unlike the delivery path: replay writes
            // no artifact row and announces no content type, and its own notion of "the format" falls
            // back to a DEFAULT when neither the revision nor the active config names one — so equality
            // here would silently drop a layout on a guess. The two places where a mismatch can actually
            // reach a supplier (promote, and the transform's adoption gate) refuse it there.
            var tree = @override?.OutputTree;
            var useOutputNode = OrderMappingOverrideReader.CanRenderTree(tree);
            var useTemplate = !useOutputNode && OrderMappingOverrideReader.HasUsableTemplate(@override);
            var hasUsableOverride =
                !useOutputNode
                && !useTemplate
                && OrderMappingOverrideReader.HasUsableOutput(@override)
                && MappedTransformService.SupportsOverrideFormat(format);
            var useNativeOverride = hasUsableOverride && MappedTransformService.SupportsOverride(format);

            var transformer = _transformers.FirstOrDefault(t => t.CanTransform(format));
            if (!useOutputNode && !useTemplate && !useNativeOverride && transformer is null)
                return RenderResult.Failure($"No transform service registered for format '{format}'.");

            TransformResult result;
            if (useOutputNode)
            {
                result = new OutputTemplateEmitter().Emit(tree!, order, @override!, sourceTokens, catalogLookup);
            }
            else if (useTemplate)
            {
                result = new ScribanTemplateTransformService().Build(order, @override!, catalogLookup);
            }
            else if (useNativeOverride)
            {
                result = new MappedTransformService().Build(
                    order, @override!, format, sourceTokens: sourceTokens, catalogLookup: catalogLookup);
            }
            else if (hasUsableOverride)
            {
                var effective = EffectiveEntityResolver.Resolve(order, @override!);
                result = transformer!.TransformAsync(effective, format, CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                result = transformer!.TransformAsync(order, format, CancellationToken.None).GetAwaiter().GetResult();
            }

            return RenderResult.Success(ReadToString(result.Content));
        }
        catch (TransformValidationException ex)
        {
            return RenderResult.Failure(ex.Message);
        }
        catch (TransformTemplateException ex)
        {
            return RenderResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            // Defence-in-depth: a single malformed order must never abort the whole replay.
            return RenderResult.Failure(ex.Message);
        }
    }

    private static string ReadToString(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        return reader.ReadToEnd();
    }

    // ── Building the current-side effective override (launch batch 4A) ────────

    /// <summary>
    /// Resolves the override that drives the CURRENT side of the diff with the SAME priority the
    /// live transform applies: the per-order override when it carries a usable template or output
    /// mapping; ELSE a synthetic override wrapping the supplier-promoted output (custom fields
    /// preserved, SourceMap/template intentionally not carried — mirrors
    /// <see cref="BuildRevisionOverride"/>); ELSE the per-order override unchanged (so a
    /// custom-fields-only / SourceMap-only override still falls through to the fixed transformer
    /// exactly as before).
    /// </summary>
    private static OrderMappingOverride? ResolveCurrentEffectiveOverride(
        OrderMappingOverride? currentOverride,
        OutputMappingConfig? supplierPromotedOutput,
        OutputNodeTemplate? supplierPromotedTree)
    {
        // The per-order seam wins as a WHOLE — including a per-order TREE, which the live transform
        // treats as the highest-precedence mode of all.
        if (currentOverride?.OutputTree is not null
            || OrderMappingOverrideReader.HasUsableTemplate(currentOverride)
            || OrderMappingOverrideReader.HasUsableOutput(currentOverride))
            return currentOverride;

        if (supplierPromotedOutput is null && supplierPromotedTree is null)
            return currentOverride;

        return new OrderMappingOverride
        {
            CustomFields = currentOverride?.CustomFields ?? new List<CustomField>(),
            Output       = supplierPromotedOutput,
            OutputTree   = supplierPromotedTree,
        };
    }

    // ── Building the revision-side override ───────────────────────────────────

    /// <summary>
    /// Build the override the REVISION would apply: the revision's output mapping (deserialized from
    /// <c>output_mapping_json</c>) carried as <see cref="OrderMappingOverride.Output"/>, while keeping
    /// the order's existing custom fields (so custom-field references in the revision's rules resolve).
    /// The order's per-order template/SourceMap is intentionally NOT carried over — the revision defines
    /// the field-by-field output. Returns null when the revision has no usable output config, so the
    /// fixed transformer drives the draft side (matching a backfilled rev-1 with null output mapping).
    /// Internal (not private) so the read-parity surfaces in <c>OrdersController</c> (mapping-override
    /// preview + conformance) wrap a PINNED revision's snapshot with the SAME logic — no second copy.
    /// </summary>
    /// <param name="draftOutputTree">
    /// WP-12 — the revision's structured output tree, snapshotted inside <c>InputMappingJson</c>
    /// (read via <see cref="DeserializeSnapshotTree"/>). Optional so the pre-WP-12 two-argument call
    /// sites stay byte-identical. Carried ALONGSIDE the flat config, never instead of it: which one
    /// drives is decided at render time, mirroring the transform (a cXML/X12 tree never does).
    /// </param>
    internal static OrderMappingOverride? BuildRevisionOverride(
        OrderMappingOverride? currentOverride,
        OutputMappingConfig? draftOutputConfig,
        OutputNodeTemplate? draftOutputTree = null)
    {
        var hasFlat = draftOutputConfig is not null
                      && (draftOutputConfig.Header.Count > 0 || draftOutputConfig.Lines.Count > 0);

        if (!hasFlat && draftOutputTree is null)
            return null;

        return new OrderMappingOverride
        {
            CustomFields = currentOverride?.CustomFields ?? new List<CustomField>(),
            Output       = hasFlat ? draftOutputConfig : null,
            OutputTree   = draftOutputTree,
        };
    }

    /// <summary>
    /// Reads the promoted <see cref="PoMappingConfig.OutputTree"/> out of a revision's
    /// <c>input_mapping_json</c> snapshot (WP-12) — the SAME read
    /// <c>OrderTransformService.TryReadPinnedOutputTree</c> performs, so the replay engine and the
    /// live transform agree on what a revision publishes. Returns null for a blank snapshot, a
    /// snapshot with no tree, an unusable tree, or a malformed snapshot. Never throws.
    /// </summary>
    internal static OutputNodeTemplate? DeserializeSnapshotTree(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var config = JsonSerializer.Deserialize<PoMappingConfig>(json, SerializerOptions);
            return OrderMappingOverrideReader.HasUsablePromotedOutputTree(config) ? config!.OutputTree : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Deserializes a revision's <c>output_mapping_json</c> snapshot (camelCase, case-insensitive;
    /// null on blank/malformed — never throws). Internal so the read-parity surfaces in
    /// <c>OrdersController</c> read the SAME snapshot identically (mirrors
    /// <c>OrderTransformService.RevisionOutputSerializerOptions</c>).
    /// </summary>
    internal static OutputMappingConfig? DeserializeOutputConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<OutputMappingConfig>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static OutputFormat? ParseFormat(string? format) =>
        Enum.TryParse<OutputFormat>(format, ignoreCase: true, out var f) ? f : null;

    // ── Effective-value diff ──────────────────────────────────────────────────

    /// <summary>
    /// Compute the per-field effective canonical values under the current override vs. the revision's
    /// override (both resolved through <see cref="EffectiveEntityResolver"/>), and emit a change row for
    /// every header/line field whose value differs. This shows the operator exactly which canonical
    /// values the new mapping would alter, before any output is produced.
    /// </summary>
    private static IReadOnlyList<ReplayFieldChangeDto> BuildEffectiveValueChanges(
        PurchaseOrderEntity order, OrderMappingOverride? currentOverride, OrderMappingOverride? draftOverride)
    {
        var currentEntity = currentOverride is null ? order : EffectiveEntityResolver.Resolve(order, currentOverride);
        var draftEntity   = draftOverride   is null ? order : EffectiveEntityResolver.Resolve(order, draftOverride);

        var changes = new List<ReplayFieldChangeDto>();

        AddHeaderChange(changes, "PoNumber",     currentEntity.PoNumber,     draftEntity.PoNumber);
        AddHeaderChange(changes, "Currency",     currentEntity.Currency,     draftEntity.Currency);
        AddHeaderChange(changes, "BuyerName",    currentEntity.BuyerName,    draftEntity.BuyerName);
        AddHeaderChange(changes, "SupplierName", currentEntity.SupplierName, draftEntity.SupplierName);
        AddHeaderChange(changes, "OrderDate",    currentEntity.OrderDate.ToString("O"), draftEntity.OrderDate.ToString("O"));

        // Lines are matched by line number (both clones preserve the source line set + numbers).
        var draftLinesByNo = draftEntity.Lines.ToDictionary(l => l.LineNumber);
        foreach (var cur in currentEntity.Lines)
        {
            if (!draftLinesByNo.TryGetValue(cur.LineNumber, out var dft)) continue;
            AddLineChange(changes, cur.LineNumber, "BuyerItemCode",    cur.BuyerItemCode,    dft.BuyerItemCode);
            AddLineChange(changes, cur.LineNumber, "SupplierItemCode", cur.SupplierItemCode, dft.SupplierItemCode);
            AddLineChange(changes, cur.LineNumber, "Description",      cur.Description,      dft.Description);
            AddLineChange(changes, cur.LineNumber, "Unit",            cur.Unit,             dft.Unit);
            AddLineChange(changes, cur.LineNumber, "Quantity",        Fmt(cur.Quantity),    Fmt(dft.Quantity));
            AddLineChange(changes, cur.LineNumber, "UnitPrice",       Fmt(cur.UnitPrice),   Fmt(dft.UnitPrice));
        }

        return changes;
    }

    private static void AddHeaderChange(List<ReplayFieldChangeDto> sink, string field, string? cur, string? dft)
    {
        if (!string.Equals(cur, dft, StringComparison.Ordinal))
            sink.Add(new ReplayFieldChangeDto("header", null, field, cur, dft));
    }

    private static void AddLineChange(List<ReplayFieldChangeDto> sink, int lineNo, string field, string? cur, string? dft)
    {
        if (!string.Equals(cur, dft, StringComparison.Ordinal))
            sink.Add(new ReplayFieldChangeDto("line", lineNo, field, cur, dft));
    }

    private static string Fmt(decimal d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ── Validation summary + flips ────────────────────────────────────────────

    private static ReplayValidationSummaryDto Summarise(IReadOnlyList<OrderValidationResult> results, bool hasProfile)
    {
        var pass = results.Count(r => r.Status == "pass");
        var fail = results.Count(r => r.Status == "fail");
        return new ReplayValidationSummaryDto(Passed: fail == 0, PassCount: pass, FailCount: fail, HasProfile: hasProfile);
    }

    /// <summary>
    /// Per-rule status flips between current and draft validation. Keyed by (code, lineNumber); a rule
    /// present on only one side is reported with a null status on the missing side.
    /// </summary>
    private static IReadOnlyList<ReplayValidationFlipDto> BuildValidationFlips(
        IReadOnlyList<OrderValidationResult> current, IReadOnlyList<OrderValidationResult> draft)
    {
        static string Key(OrderValidationResult r) => $"{r.Code}|{r.LineNumber}";

        var currentByKey = current.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var draftByKey   = draft.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());

        var flips = new List<ReplayValidationFlipDto>();
        foreach (var key in currentByKey.Keys.Union(draftByKey.Keys))
        {
            currentByKey.TryGetValue(key, out var c);
            draftByKey.TryGetValue(key, out var d);

            var curStatus = c?.Status;
            var dftStatus = d?.Status;
            if (string.Equals(curStatus, dftStatus, StringComparison.Ordinal)) continue;

            var representative = d ?? c!;
            flips.Add(new ReplayValidationFlipDto(
                representative.Code, representative.LineNumber, curStatus, dftStatus, representative.Message));
        }
        // Stable order: line number then code.
        return flips
            .OrderBy(f => f.LineNumber ?? -1)
            .ThenBy(f => f.Code, StringComparer.Ordinal)
            .ToList();
    }

    // ── Replay flip A — parse-from-source leg ─────────────────────────────────
    // Completes "published revision = tested contract": the revision's InputMappingJson +
    // item-mapping snapshot are exercised against the order's REAL stored source file, entirely
    // in memory. Never mutates (no order/line writes, no uploads); never throws — every failure
    // is recorded as a per-order note. PDF sources are SKIPPED (the primary PDF path is the
    // AI extractor — expensive + external; deterministic formats only re-parse here).

    /// <summary>Skip-note factory: leg not run for this order (no file / AI source / host without storage).</summary>
    private static ReplayParseLegDto ParseLegSkipped(string note) => new(
        "skipped", note, ParseChanged: false,
        HeaderChanges: Array.Empty<ReplayFieldChangeDto>(), LineCountChanged: false,
        CurrentLineCount: null, ReParsedLineCount: null,
        LinesNewlyFlagged: Array.Empty<int>(), LinesNewlyUnflagged: Array.Empty<int>(),
        CodesNewlyResolved: Array.Empty<ReplayParseCodeChangeDto>(),
        CodesNewlyUnresolved: Array.Empty<ReplayParseCodeChangeDto>(),
        CodesResolvedDifferently: Array.Empty<ReplayParseCodeChangeDto>());

    /// <summary>Failure-note factory: the leg ran but could not complete (download/parse error) — recorded, never thrown.</summary>
    private static ReplayParseLegDto ParseLegFailed(string note) => new(
        "failed", note, ParseChanged: false,
        HeaderChanges: Array.Empty<ReplayFieldChangeDto>(), LineCountChanged: false,
        CurrentLineCount: null, ReParsedLineCount: null,
        LinesNewlyFlagged: Array.Empty<int>(), LinesNewlyUnflagged: Array.Empty<int>(),
        CodesNewlyResolved: Array.Empty<ReplayParseCodeChangeDto>(),
        CodesNewlyUnresolved: Array.Empty<ReplayParseCodeChangeDto>(),
        CodesResolvedDifferently: Array.Empty<ReplayParseCodeChangeDto>());

    /// <summary>
    /// Runs the parse-from-source leg for ONE order: download the raw stored file (via the
    /// existing <see cref="IFileStorageService.DownloadAsync"/> — the R2 pre-signed-URL path),
    /// re-parse it in memory under <paramref name="parseMapping"/> with EXACTLY the routing
    /// <c>OrderIngestionService.ParseStoredFileAsync</c> uses (mapping template for .csv, else
    /// the content-aware parser factory), resolve item codes against the revision's snapshot
    /// (live-table fallback when the snapshot is empty — the runtime contract), and diff the
    /// result against the order's CURRENT stored canonical. Defensive end to end: any failure
    /// becomes a per-order note.
    /// </summary>
    private async Task<ReplayParseLegDto> BuildParseLegAsync(
        PurchaseOrderEntity order,
        PoMappingConfig? parseMapping,
        IReadOnlyList<EffectiveRevisionItemMapping> itemSnapshot,
        Guid orgId,
        Guid supplierId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(order.SourceFileKey))
            return ParseLegSkipped("No stored source file for this order (API/email ingress) — parse leg skipped.");

        var extension = Path.GetExtension(order.SourceFileKey).ToLowerInvariant();
        if (extension == ".pdf")
            return ParseLegSkipped("AI-extracted source, parse leg skipped.");

        if (_fileStorage is null || _parserFactory is null)
            return ParseLegSkipped("File storage / parser routing is not available to this replay host — parse leg skipped.");

        // Download raw bytes — reuse IFileStorageService.DownloadAsync exactly (R2 SDK-signing gotcha).
        byte[] bytes;
        try
        {
            await using var download = await _fileStorage.DownloadAsync(order.SourceFileKey, ct);
            using var buffer = new MemoryStream();
            await download.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        catch (Exception ex)
        {
            return ParseLegFailed($"Could not download the source file: {ex.Message}");
        }

        if (bytes.Length == 0)
            return ParseLegFailed("The stored source file is empty.");

        // Re-parse in memory under the revision's input mapping — same routing as the live parse:
        // a usable mapping template drives .csv; everything else goes through the content-aware
        // parser factory (.xlsx/.xml/.edi/.txt/.x12/... — deterministic parsers only; PDF excluded above).
        ParsedOrder parsed;
        try
        {
            if (parseMapping is not null && extension == ".csv")
            {
                parsed = await OrderIngestionService.ParseWithMappingTemplateAsync(bytes, parseMapping, ct);
            }
            else
            {
                using var stream = new MemoryStream(bytes);
                var parser = _parserFactory.GetParser(extension, stream);
                stream.Position = 0;
                parsed = await parser.ParseAsync(stream, ct);
            }
        }
        catch (Exception ex)
        {
            return ParseLegFailed($"Re-parse failed under this revision's input mapping: {ex.Message}");
        }

        if (parsed.Lines.Count == 0)
            return ParseLegFailed("Re-parse produced no line items under this revision's input mapping.");

        // Item-code resolution: the revision's snapshot governs when non-empty
        // (OrderIngestionService.ResolveFromSnapshot); an EMPTY snapshot falls back to the live item
        // mappings, mirroring BuildLineEntitiesAsync's runtime contract.
        //
        // Both branches key the returned dictionary Ordinal on the TRIMMED code the line carries,
        // and both MATCH stored rows case-insensitively under ItemCodeComparison — including the
        // same exact-case-wins tie-break when a supplier holds case-variant rows. That agreement is
        // what makes this a replay rather than a re-derivation: the two paths must pick the same
        // row, not merely both "resolve". Asserted in ItemMappingCaseParityTests.
        IReadOnlyDictionary<string, string?> resolvedMap;
        try
        {
            resolvedMap = itemSnapshot.Count > 0
                ? OrderIngestionService.ResolveFromSnapshot(itemSnapshot, parsed.Lines)
                : await _itemMappings.ResolveManyAsync(
                    orgId, supplierId, parsed.Lines.Select(l => l.BuyerItemCode), ct);
        }
        catch (Exception ex)
        {
            return ParseLegFailed($"Item-code resolution failed: {ex.Message}");
        }

        return BuildParseDiff(order, parsed, resolvedMap);
    }

    /// <summary>
    /// Pure diff of a re-parsed order against the CURRENT stored canonical: header fields that
    /// would change, line-count change, lines that would newly flag/unflag for review (mirrors
    /// <c>BuildLineEntitiesAsync</c>'s <c>NeedsReview = !resolved || parserFlagged</c>), and
    /// per-line code-resolution changes. Differences are informational, never failures.
    /// </summary>
    internal static ReplayParseLegDto BuildParseDiff(
        PurchaseOrderEntity order,
        ParsedOrder parsed,
        IReadOnlyDictionary<string, string?> resolvedMap)
    {
        // Header diff. Values are normalised the way the live parse persists them; fields the
        // parse would runtime-generate when absent (blank PO number → "PO-{timestamp}", missing
        // order date → today) are only compared when the re-parse produced a definite value.
        var headerChanges = new List<ReplayFieldChangeDto>();
        if (!string.IsNullOrWhiteSpace(parsed.PoNumber))
            AddHeaderChange(headerChanges, "PoNumber", order.PoNumber, parsed.PoNumber);
        var newBuyerName = string.IsNullOrWhiteSpace(parsed.BuyerName) ? null : parsed.BuyerName.Trim();
        AddHeaderChange(headerChanges, "BuyerName", order.BuyerName, newBuyerName);
        AddHeaderChange(headerChanges, "Currency", order.Currency, parsed.Currency ?? "EUR");
        if (parsed.OrderDate.HasValue)
            AddHeaderChange(headerChanges, "OrderDate",
                order.OrderDate.ToString("O"),
                DateOnly.FromDateTime(parsed.OrderDate.Value).ToString("O"));

        // Line-level diff, matched by line number (defensive grouping — duplicates never throw).
        var currentByNo = order.Lines
            .GroupBy(l => l.LineNumber)
            .ToDictionary(g => g.Key, g => g.First());

        var newlyFlagged    = new List<int>();
        var newlyUnflagged  = new List<int>();
        var newlyResolved   = new List<ReplayParseCodeChangeDto>();
        var newlyUnresolved = new List<ReplayParseCodeChangeDto>();
        var resolvedDifferently = new List<ReplayParseCodeChangeDto>();

        foreach (var line in parsed.Lines.OrderBy(l => l.LineNumber))
        {
            // Lines that only exist on one side are covered by the line-count diff.
            if (!currentByNo.TryGetValue(line.LineNumber, out var current)) continue;

            var code     = ResolveCodeFromMap(resolvedMap, line.BuyerItemCode);
            var resolved = !string.IsNullOrWhiteSpace(code);

            // Mirrors BuildLineEntitiesAsync: a line needs review when its code is unresolved OR
            // the parser itself flagged an ambiguous number.
            var wouldFlag        = !resolved || line.NeedsReview;
            var currentlyFlagged = current.NeedsReview;
            if (wouldFlag && !currentlyFlagged) newlyFlagged.Add(line.LineNumber);
            if (!wouldFlag && currentlyFlagged) newlyUnflagged.Add(line.LineNumber);

            var currentCode     = current.SupplierItemCode;
            var currentResolved = !string.IsNullOrWhiteSpace(currentCode);
            if (resolved && !currentResolved)
                newlyResolved.Add(new ReplayParseCodeChangeDto(line.LineNumber, line.BuyerItemCode, currentCode, code));
            else if (!resolved && currentResolved)
                newlyUnresolved.Add(new ReplayParseCodeChangeDto(line.LineNumber, line.BuyerItemCode, currentCode, null));
            else if (resolved && currentResolved && !string.Equals(currentCode, code, StringComparison.Ordinal))
                resolvedDifferently.Add(new ReplayParseCodeChangeDto(line.LineNumber, line.BuyerItemCode, currentCode, code));
        }

        var lineCountChanged = parsed.Lines.Count != order.Lines.Count;
        var parseChanged = headerChanges.Count > 0 || lineCountChanged
            || newlyFlagged.Count > 0 || newlyUnflagged.Count > 0
            || newlyResolved.Count > 0 || newlyUnresolved.Count > 0
            || resolvedDifferently.Count > 0;

        return new ReplayParseLegDto(
            "reparsed", Note: null, ParseChanged: parseChanged,
            HeaderChanges: headerChanges,
            LineCountChanged: lineCountChanged,
            CurrentLineCount: order.Lines.Count,
            ReParsedLineCount: parsed.Lines.Count,
            LinesNewlyFlagged: newlyFlagged,
            LinesNewlyUnflagged: newlyUnflagged,
            CodesNewlyResolved: newlyResolved,
            CodesNewlyUnresolved: newlyUnresolved,
            CodesResolvedDifferently: resolvedDifferently);
    }

    /// <summary>
    /// Trimmed-key lookup mirroring <c>ItemMappingService.ResolveManyAsync</c> /
    /// <c>ResolveFromSnapshot</c> keying: both build Ordinal dictionaries keyed by the exact
    /// trimmed code the line carries, so this must NOT fold case itself — doing so here would
    /// answer a "b-1" line from a "B-1" key and report a code change that never happened.
    /// </summary>
    private static string? ResolveCodeFromMap(IReadOnlyDictionary<string, string?> map, string? buyerItemCode)
    {
        if (string.IsNullOrWhiteSpace(buyerItemCode)) return null;
        return map.TryGetValue(buyerItemCode.Trim(), out var code) ? code : null;
    }
}
