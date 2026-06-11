using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;

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
///   output (re-derived deterministically from the order's per-order <c>mappingOverride</c> / fixed
///   transformer). A failure to render the revision side surfaces as <c>OutputError</c>, never a crash.</description></item>
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

    public ReplayService(
        ProcuLinkDbContext             db,
        IEnumerable<ITransformService> transformers,
        IPoMappingService?             poMappings = null)
    {
        _db           = db;
        _transformers = transformers;
        // Optional so existing positional constructions stay valid; the concrete service only
        // needs the same DbContext. Used so the CURRENT side of a replay diff mirrors the live
        // transform's supplier-promoted-output fallback (launch batch 4A).
        _poMappings   = poMappings ?? new ProcuLink.Infrastructure.Services.PoMappingService(db);
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
        var draftOutputConfig = DeserializeOutputConfig(revision.OutputMappingJson);
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
        try
        {
            var supplierConfig = await _poMappings.GetAsync(orgId, revision.SupplierId, ct);
            if (OrderMappingOverrideReader.HasUsablePromotedOutput(supplierConfig))
                supplierPromotedOutput = supplierConfig!.Output;
        }
        catch
        {
            // A malformed supplier mapping must never abort a replay — fall back to fixed.
        }

        var now = DateTime.UtcNow;
        var diffs = new List<ReplayOrderDiffDto>(orders.Count);
        foreach (var order in orders)
            diffs.Add(BuildDiff(order, draftOutputConfig, draftFormat, currentFormat, currentProfile, draftProfile, now, supplierPromotedOutput));

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
        OutputFormat draftFormat,
        OutputFormat currentFormat,
        SupplierAcceptanceProfile? currentProfile,
        SupplierAcceptanceProfile? draftProfile,
        DateTime now,
        OutputMappingConfig? supplierPromotedOutput)
    {
        // ── Output side ────────────────────────────────────────────────────────
        // CURRENT: the order's own per-order override, ELSE the supplier-promoted output mapping
        // (launch batch 4A — exactly the live transform's priority), ELSE the fixed transformer —
        // at the current format.
        var currentOverride  = OrderMappingOverrideReader.Read(order.CanonicalJson);
        var currentEffective = ResolveCurrentEffectiveOverride(currentOverride, supplierPromotedOutput);
        var current = Render(order, currentEffective, currentFormat);

        // DRAFT/replayed: the revision's output config wrapped as an override, at the revision's format.
        // The order's existing CustomFields are preserved so custom-field references in the revision's
        // output rules still resolve; only the OUTPUT mapping + format come from the revision.
        var draftOverride = BuildRevisionOverride(currentOverride, draftOutputConfig);
        var draft = Render(order, draftOverride, draftFormat);

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
    private RenderResult Render(PurchaseOrderEntity order, OrderMappingOverride? @override, OutputFormat format)
    {
        try
        {
            var useTemplate = OrderMappingOverrideReader.HasUsableTemplate(@override);
            var hasUsableOverride =
                !useTemplate
                && OrderMappingOverrideReader.HasUsableOutput(@override)
                && MappedTransformService.SupportsOverrideFormat(format);
            var useNativeOverride = hasUsableOverride && MappedTransformService.SupportsOverride(format);

            var transformer = _transformers.FirstOrDefault(t => t.CanTransform(format));
            if (!useTemplate && !useNativeOverride && transformer is null)
                return RenderResult.Failure($"No transform service registered for format '{format}'.");

            TransformResult result;
            if (useTemplate)
            {
                result = new ScribanTemplateTransformService().Build(order, @override!);
            }
            else if (useNativeOverride)
            {
                result = new MappedTransformService().Build(order, @override!, format);
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
        OrderMappingOverride? currentOverride, OutputMappingConfig? supplierPromotedOutput)
    {
        if (supplierPromotedOutput is null
            || OrderMappingOverrideReader.HasUsableTemplate(currentOverride)
            || OrderMappingOverrideReader.HasUsableOutput(currentOverride))
            return currentOverride;

        return new OrderMappingOverride
        {
            CustomFields = currentOverride?.CustomFields ?? new List<CustomField>(),
            Output       = supplierPromotedOutput,
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
    /// </summary>
    private static OrderMappingOverride? BuildRevisionOverride(
        OrderMappingOverride? currentOverride, OutputMappingConfig? draftOutputConfig)
    {
        if (draftOutputConfig is null
            || (draftOutputConfig.Header.Count == 0 && draftOutputConfig.Lines.Count == 0))
            return null;

        return new OrderMappingOverride
        {
            CustomFields = currentOverride?.CustomFields ?? new List<CustomField>(),
            Output       = draftOutputConfig,
        };
    }

    private static OutputMappingConfig? DeserializeOutputConfig(string? json)
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
}
