using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;

namespace ProcuLink.Api.Services;

/// <summary>
/// Internal sub-service of <see cref="OrderService"/> owning the transform-to-artifact
/// path (generate output document, upload, advance to ready_to_deliver). Method moved
/// verbatim from the original God-class; only the host type and the shared-helper call
/// site changed (audit W1/B1 decomposition).
///
/// <para><b>Idempotency:</b> <see cref="TransformAsync"/> atomically claims the order
/// (ready/transforming → transforming) before generating anything; a duplicated Hangfire
/// run or a concurrent enqueue on an already-transformed order returns a
/// <see cref="TransformResponse.Skipped"/> no-op instead of uploading a duplicate
/// artifact and re-triggering delivery.</para>
/// </summary>
internal sealed class OrderTransformService
{
    /// <summary>
    /// Serializer for the supplier-promoted output mapping's provenance digest descriptor. CamelCase
    /// matches how <c>PoMappingService</c> persists <c>SupplierPoMapping.ConfigJson</c>, so the
    /// digested text is a stable representation of the stored supplier mapping.
    /// </summary>
    private static readonly JsonSerializerOptions SupplierOutputSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Deserializer for a pinned revision's <c>output_mapping_json</c> snapshot (launch batch 7).
    /// Mirrors <c>ReplayService.DeserializeOutputConfig</c> so the live transform and the replay
    /// engine read the SAME snapshot identically.
    /// </summary>
    private static readonly JsonSerializerOptions RevisionOutputSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ProcuLinkDbContext              _db;
    private readonly IFileStorageService             _fileStorage;
    private readonly IEnumerable<ITransformService>  _transformers;
    private readonly ILogger<OrderService>           _logger;
    private readonly IPoMappingService               _poMappings;
    private readonly IEffectiveConnectionConfigResolver? _effectiveConfig;

    public OrderTransformService(
        ProcuLinkDbContext             db,
        IFileStorageService            fileStorage,
        IEnumerable<ITransformService> transformers,
        ILogger<OrderService>          logger,
        IPoMappingService              poMappings,
        IEffectiveConnectionConfigResolver? effectiveConfig = null)
    {
        _db           = db;
        _fileStorage  = fileStorage;
        _transformers = transformers;
        _logger       = logger;
        _poMappings   = poMappings;
        // Launch batch 7 — revision authority. Null (older positional ctors / unregistered
        // hosts) behaves exactly like flag-OFF: the live path drives everything.
        _effectiveConfig = effectiveConfig;
    }

    // ── TransformAsync ────────────────────────────────────────────────────────

    public async Task<Result<TransformResponse>> TransformAsync(
        Guid organisationId,
        Guid orderId,
        OutputFormat format,
        CancellationToken ct)
    {
        // Load with tracking — we will mutate status twice
        var entity = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Include(x => x.Supplier)
            .Include(x => x.SourceCapture)   // Phase 2: persisted token universe for SourceMap re-derive
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<TransformResponse>.Fail("Order not found.");

        // Pre-flight check: all lines must be resolved
        var unresolved = entity.Lines.Where(l => l.NeedsReview).Select(l => l.LineNumber).ToList();
        if (unresolved.Count > 0)
            return Result<TransformResponse>.Fail(
                $"Resolve all lines before transforming. Unresolved: {string.Join(", ", unresolved)}.");

        // ── Launch batch 7 — revision authority ────────────────────────────────
        // Resolve the pinned revision's effective bundle ONCE per transform. Live bundle when
        // the flag is off / the order is unpinned / the pin is orphaned — byte-identical to the
        // pre-batch-7 path in all of those cases.
        var effective = _effectiveConfig is null
            ? EffectiveConnectionConfig.Live
            : await _effectiveConfig.ResolveAsync(organisationId, entity.ConnectionRevisionId, ct);

        // Effective output format: [flag+pin] the revision's snapshotted format governs;
        // null/unparseable snapshot → the caller's format (which the controller already resolved
        // as explicit-request ?? live delivery-config format ?? safe default).
        var effectiveFormat = format;
        if (effective.IsRevision && !string.IsNullOrWhiteSpace(effective.OutputFormat))
        {
            if (Enum.TryParse<OutputFormat>(effective.OutputFormat, ignoreCase: true, out var revisionFormat))
            {
                if (revisionFormat != format)
                    _logger.LogInformation(
                        "Order {OrderId}: output format {RevisionFormat} taken from pinned {Source} (requested {Requested}).",
                        orderId, revisionFormat, effective.Source, format);
                effectiveFormat = revisionFormat;
            }
            else
            {
                _logger.LogWarning(
                    "Order {OrderId}: pinned {Source} output format '{Format}' is not recognised — using the requested format {Requested}.",
                    orderId, effective.Source, effective.OutputFormat, format);
            }
        }

        // heart-piece-flex flexible mapping: SIX transform modes, in precedence order.
        //   1. TEMPLATE MODE — order carries a non-blank whole-document OutputTemplate → render the
        //      ENTIRE document from that one Scriban template (ANY format, no fixed transformer needed).
        //   2. NATIVE OVERRIDE (CSV/JSON) — per-order output mapping + a format the override builder
        //      emits natively from the output-field rules.
        //   3. STRUCTURED OVERRIDE (XML/cXML/UBL/X12) — resolve the override's canonical-field changes
        //      into an EFFECTIVE entity, then hand it to the EXISTING fixed transform (no per-format
        //      re-implementation).
        //   4. REVISION-PINNED OUTPUT (launch batch 7, flag-gated) — the order is pinned to a
        //      published revision whose output_mapping_json snapshot is usable → wrap it as a
        //      synthetic override and reuse modes 2/3 verbatim. For a PINNED order the LIVE
        //      supplier-promoted mapping is intentionally NOT consulted (it is a mutable table and
        //      would break reproducibility); an unusable/null snapshot falls to the FIXED transformer.
        //   5. SUPPLIER-PROMOTED OUTPUT (launch batch 4A — unpinned/flag-off only) — the supplier
        //      carries a promoted PoMappingConfig.Output ("Save mappings for this supplier") →
        //      wrap it as a synthetic override and reuse modes 2/3 verbatim. The per-order
        //      override always stays the higher-priority seam.
        //   6. FIXED TRANSFORMER — the default; byte-for-byte identical to today when nothing above applies.
        // An override with only custom fields or an empty output config never diverts the transform.
        var mappingOverride = OrderMappingOverrideReader.Read(entity.CanonicalJson);
        var useTemplate     = OrderMappingOverrideReader.HasUsableTemplate(mappingOverride);
        var hasUsableOverride =
            !useTemplate
            && OrderMappingOverrideReader.HasUsableOutput(mappingOverride)
            && MappedTransformService.SupportsOverrideFormat(effectiveFormat);
        var useNativeOverride = hasUsableOverride && MappedTransformService.SupportsOverride(effectiveFormat);

        // Revision-pinned output vs supplier-promoted output — mutually exclusive by construction:
        // a pinned order consults ONLY its revision snapshot; an unpinned/flag-off order consults
        // ONLY the live supplier-promoted mapping (read ONCE per transform). Defensive: a missing /
        // malformed / unusable mapping on either side yields null and the fixed transformer stays
        // in control (logged, never a throw). supplierOutputJson is the camelCase serialization of
        // the promoted Output config, used for the provenance ConfigDigest below.
        OrderMappingOverride? revisionOverride   = null;
        OrderMappingOverride? supplierOverride   = null;
        string?               supplierOutputJson = null;
        if (!useTemplate && !hasUsableOverride && MappedTransformService.SupportsOverrideFormat(effectiveFormat))
        {
            if (effective.IsRevision)
                revisionOverride = TryBuildRevisionOutputOverride(effective, mappingOverride, orderId);
            else
                (supplierOverride, supplierOutputJson) =
                    await TryReadSupplierPromotedOutputAsync(organisationId, entity.SupplierId, mappingOverride, ct);
        }
        var useRevisionOutput  = revisionOverride is not null;
        var useRevisionNative  = useRevisionOutput && MappedTransformService.SupportsOverride(effectiveFormat);
        var useSupplierMapping = supplierOverride is not null;
        var useSupplierNative  = useSupplierMapping && MappedTransformService.SupportsOverride(effectiveFormat);

        // Locate the fixed transformer (Xml/Csv/Json/...). Required EXCEPT for template mode and the
        // native CSV/JSON override path; resolved up-front so a missing transformer fails before status
        // mutation. NOTE: the revision-pinned and supplier-promoted paths deliberately do NOT relax
        // this requirement — the fixed transformer must exist so the defensive fallback below is
        // always possible.
        var transformer = _transformers.FirstOrDefault(t => t.CanTransform(effectiveFormat));
        if (!useTemplate && !useNativeOverride && transformer is null)
            return Result<TransformResponse>.Fail($"No transform service registered for format '{effectiveFormat}'.");

        // ── Idempotency / concurrency guard ────────────────────────────────────
        // Atomically claim the order by flipping ready → transforming only while it is
        // still claimable (mirrors the parse path's "only parse while parsing" guard).
        // "transforming" is also claimable so a Hangfire retry can re-run a crashed
        // attempt; a COMPLETED transform (ready_to_deliver and beyond) affects 0 rows
        // and short-circuits below — re-running it would upload a duplicate artifact
        // and re-enqueue delivery, double-sending the same PO to the supplier.
        var claimedAt = DateTime.UtcNow;
        int claimed;
        if (_db.Database.IsRelational())
        {
            claimed = await _db.PurchaseOrders
                .Where(x => x.Id == orderId && x.OrgId == organisationId
                         && (x.Status == OrderStatusConstants.Ready
                          || x.Status == OrderStatusConstants.Transforming))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Status, OrderStatusConstants.Transforming)
                    .SetProperty(o => o.UpdatedAt, claimedAt), ct);
        }
        else
        {
            // EF InMemory test provider cannot translate ExecuteUpdateAsync — emulate the
            // same transition through the change tracker (tests are single-threaded there).
            claimed = entity.Status is OrderStatusConstants.Ready or OrderStatusConstants.Transforming ? 1 : 0;
            if (claimed == 1)
            {
                entity.Status    = OrderStatusConstants.Transforming;
                entity.UpdatedAt = claimedAt;
                await _db.SaveChangesAsync(ct);
            }
        }

        if (claimed == 0)
        {
            // Already transformed (or not in a transformable state): report the latest
            // existing artifact as a benign no-op so a duplicated job neither re-uploads
            // nor re-enqueues delivery.
            var existing = await _db.OutboundArtifacts.AsNoTracking()
                .Where(a => a.OrderId == orderId && a.OrgId == organisationId)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);

            _logger.LogInformation(
                "Order {OrderId} transform skipped (status={Status}, existing artifact={ArtifactId}) — already in flight or done.",
                orderId, entity.Status, existing?.Id);

            return Result<TransformResponse>.Ok(new TransformResponse(
                existing?.Id ?? Guid.Empty,
                existing?.Format ?? effectiveFormat.ToString().ToLowerInvariant(),
                existing?.CreatedAt ?? claimedAt,
                Skipped: true));
        }

        // Sync the tracked entity with the row just claimed (the ExecuteUpdateAsync above
        // bypasses the change tracker): both CURRENT and ORIGINAL values must say
        // 'transforming', otherwise the failure paths' revert to "ready" would diff as
        // a no-op against a stale original and never be written — stranding the order.
        entity.Status    = OrderStatusConstants.Transforming;
        entity.UpdatedAt = claimedAt;
        if (_db.Database.IsRelational())
        {
            var entry = _db.Entry(entity);
            entry.Property(x => x.Status).OriginalValue    = OrderStatusConstants.Transforming;
            entry.Property(x => x.UpdatedAt).OriginalValue = claimedAt;
        }

        // Generate the document.
        //  • Native CSV/JSON override → the override builder emits the document.
        //  • Structured-format override → resolve an effective entity (canonical-field overrides
        //    applied to the typed columns) and feed it to the EXISTING fixed transform.
        //  • Supplier-promoted output (no per-order override) → same two mechanisms, driven by the
        //    synthetic supplier override; any unexpected failure falls back to the fixed transform.
        //  • No override → the fixed transform on the original entity, byte-for-byte unchanged.
        //
        // Phase 2: rebuild the addressable source-token universe from the persisted capture so
        // SourceMap rules resolve at delivery time even after the source blob is purged
        // (SourceFilePurgedAt). Empty when there is no capture → byte-identical to the no-token path.
        var sourceTokens = ProcuLink.Transform.Output.SourceTokenSerialization
            .FromTokensJson(entity.SourceCapture?.TokensJson);

        // Phase 2: batch-load this supplier's catalog ONCE (org+supplier scoped, never cross-tenant)
        // so the {{ catalog.* }} template accessor resolves without an N+1. Empty dict when the
        // supplier has no catalog → byte-identical to the no-catalog path (empty catalog object).
        var catalogLookup = await OrderServiceShared.BuildCatalogLookupAsync(
            _db, organisationId, entity.SupplierId, ct);

        TransformResult transformResult;
        try
        {
            if (useTemplate)
            {
                transformResult = new ScribanTemplateTransformService().Build(entity, mappingOverride!, catalogLookup);
            }
            else if (useNativeOverride)
            {
                transformResult = new MappedTransformService().Build(entity, mappingOverride!, effectiveFormat, sourceTokens: sourceTokens);
            }
            else if (hasUsableOverride)
            {
                var effectiveEntity = EffectiveEntityResolver.Resolve(entity, mappingOverride!);
                transformResult = await transformer!.TransformAsync(effectiveEntity, effectiveFormat, ct);
            }
            else if (useRevisionOutput)
            {
                try
                {
                    transformResult = useRevisionNative
                        ? new MappedTransformService().Build(entity, revisionOverride!, effectiveFormat, sourceTokens: sourceTokens)
                        : await transformer!.TransformAsync(
                              EffectiveEntityResolver.Resolve(entity, revisionOverride!), effectiveFormat, ct);
                }
                catch (Exception ex) when (ex is not TransformValidationException and not TransformTemplateException)
                {
                    // Defensive: a pinned revision's output snapshot must never break delivery. Fall
                    // back to the fixed transformer and log — the provenance digest below then
                    // records the fixed marker, not the revision one.
                    _logger.LogWarning(ex,
                        "Pinned revision output mapping failed for order {OrderId} ({Source}); falling back to the fixed transformer.",
                        orderId, effective.Source);
                    useRevisionOutput = false;
                    transformResult = await transformer!.TransformAsync(entity, effectiveFormat, ct);
                }
            }
            else if (useSupplierMapping)
            {
                try
                {
                    transformResult = useSupplierNative
                        ? new MappedTransformService().Build(entity, supplierOverride!, effectiveFormat, sourceTokens: sourceTokens)
                        : await transformer!.TransformAsync(
                              EffectiveEntityResolver.Resolve(entity, supplierOverride!), effectiveFormat, ct);
                }
                catch (Exception ex) when (ex is not TransformValidationException and not TransformTemplateException)
                {
                    // Defensive: a promoted supplier mapping must never break delivery. Fall back to
                    // the fixed transformer (byte-identical to the pre-promotion behaviour) and log —
                    // the provenance digest below then records the fixed marker, not the supplier one.
                    _logger.LogWarning(ex,
                        "Supplier-promoted output mapping failed for order {OrderId} (supplier {SupplierId}); falling back to the fixed transformer.",
                        orderId, entity.SupplierId);
                    useSupplierMapping = false;
                    transformResult = await transformer!.TransformAsync(entity, effectiveFormat, ct);
                }
            }
            else
            {
                transformResult = await transformer!.TransformAsync(entity, effectiveFormat, ct);
            }
        }
        catch (TransformTemplateException ex)
        {
            // Broken template — revert status and surface the compile/render error. The order is
            // never delivered from a template that did not render.
            entity.Status    = "ready";
            entity.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Result<TransformResponse>.Fail(ex.Message);
        }
        catch (TransformValidationException ex)
        {
            // Revert status on validation failure
            entity.Status    = "ready";
            entity.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Result<TransformResponse>.Fail(ex.Message);
        }

        // Buffer the generated bytes once: the SAME byte sequence UploadAsync would have read
        // from the stream's current position is both uploaded and hashed for provenance, so the
        // stored artifact and its recorded SHA-256 can never diverge.
        byte[] artifactBytes;
        await using (var content = transformResult.Content)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            artifactBytes = buffer.ToArray();
        }

        // Upload artifact to R2
        var artifactId  = Guid.NewGuid();
        var artifactKey = $"{organisationId}/{orderId}/artifacts/{artifactId}{transformResult.FileExtension}";

        await _fileStorage.UploadAsync(
            new MemoryStream(artifactBytes, writable: false), artifactKey, transformResult.ContentType, ct);

        _logger.LogInformation("Uploaded artifact to R2: {Key}", artifactKey);

        // ── Provenance (best-effort; must NEVER fail the transform) ────────────────
        // ConfigDigest = SHA-256 of the EFFECTIVE config that drove this transform: the order's
        // per-order mappingOverride JSON (template / native / structured override modes), else
        // "revision:{revisionId}:{outputMappingJson}" when the pinned revision's output snapshot
        // drove it (launch batch 7), else "supplier:{outputJson}" when the supplier-promoted
        // output mapping drove it, else the marker "fixed:{format}" for the fixed-transformer
        // path — the prefixes keep the four sources distinguishable. ArtifactSha256 = SHA-256 of
        // the exact generated bytes. ConnectionRevisionId = the order's ingest-time pin.
        string? configDigest = null;
        string? artifactSha  = null;
        try
        {
            artifactSha = ProvenanceHash.TrySha256Hex(artifactBytes);
            var configDescriptor = (useTemplate || useNativeOverride || hasUsableOverride)
                ? OrderMappingOverrideReader.ReadRawJson(entity.CanonicalJson)
                : useRevisionOutput
                    ? $"revision:{effective.RevisionId}:{effective.OutputMappingJson}"
                    : useSupplierMapping
                        ? $"supplier:{supplierOutputJson}"
                        : $"fixed:{effectiveFormat.ToString().ToLowerInvariant()}";
            configDigest = ProvenanceHash.TrySha256HexUtf8(configDescriptor);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Provenance capture failed for order {OrderId} (non-fatal; artifact saved without digests).",
                orderId);
        }

        // Persist artifact row + update order status + audit — one SaveChanges
        var now      = DateTime.UtcNow;
        var artifact = new OutboundArtifact
        {
            Id                   = artifactId,
            OrderId              = orderId,
            OrgId                = organisationId,
            Format               = effectiveFormat.ToString().ToLowerInvariant(),
            FileKey              = artifactKey,
            CreatedAt            = now,
            ConnectionRevisionId = entity.ConnectionRevisionId,
            ConfigDigest         = configDigest,
            ArtifactSha256       = artifactSha,
        };

        _db.OutboundArtifacts.Add(artifact);

        entity.Status    = OrderStatusConstants.ReadyToDeliver;
        entity.UpdatedAt = now;

        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "Transformed", new
        {
            format     = artifact.Format,
            artifactId = artifactId,
            fileKey    = artifactKey
        }));

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Order {OrderId} transformed to {Format}, artifact {ArtifactId}",
            orderId, effectiveFormat, artifactId);

        return Result<TransformResponse>.Ok(new TransformResponse(artifactId, artifact.Format, now));
    }

    // ── Revision-pinned output mapping (launch batch 7) ───────────────────────

    /// <summary>
    /// Builds the synthetic override the PINNED revision's <c>output_mapping_json</c> snapshot
    /// would apply, reusing the existing override machinery verbatim (mirrors
    /// <c>ReplayService.BuildRevisionOverride</c>: the order's custom fields are preserved so
    /// output rules referencing them still resolve; the per-order SourceMap / template are NOT
    /// carried). Returns null — meaning "the FIXED transformer drives the output" — for a
    /// null/blank snapshot, an empty snapshot (no header AND no line rules; matches a backfilled
    /// rev-1), or a malformed snapshot (logged). Never throws.
    /// </summary>
    private OrderMappingOverride? TryBuildRevisionOutputOverride(
        EffectiveConnectionConfig effective, OrderMappingOverride? mappingOverride, Guid orderId)
    {
        if (string.IsNullOrWhiteSpace(effective.OutputMappingJson))
            return null;

        try
        {
            var output = JsonSerializer.Deserialize<OutputMappingConfig>(
                effective.OutputMappingJson, RevisionOutputSerializerOptions);

            if (output is null || (output.Header.Count == 0 && output.Lines.Count == 0))
                return null; // empty snapshot — the fixed transformer stays in control

            _logger.LogInformation(
                "Order {OrderId}: output mapping taken from pinned {Source}.", orderId, effective.Source);

            return new OrderMappingOverride
            {
                CustomFields = mappingOverride?.CustomFields ?? new List<CustomField>(),
                Output       = output,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Order {OrderId}: pinned {Source} output mapping is malformed — using the fixed transformer.",
                orderId, effective.Source);
            return null;
        }
    }

    // ── Supplier-promoted output mapping (launch batch 4A) ────────────────────

    /// <summary>
    /// Reads the supplier's reusable <see cref="PoMappingConfig"/> and, when it carries a USABLE
    /// promoted output mapping (<see cref="OrderMappingOverrideReader.HasUsablePromotedOutput"/>),
    /// wraps it as a synthetic per-order-shaped <see cref="OrderMappingOverride"/> so the EXISTING
    /// override machinery (native CSV/JSON builder + structured effective-entity resolver) is reused
    /// verbatim. The order's custom fields are preserved so promoted output rules referencing them
    /// still resolve; the per-order SourceMap / template are intentionally NOT carried over (mirrors
    /// <c>ReplayService.BuildRevisionOverride</c>). Defensive: any failure (e.g. malformed
    /// <c>SupplierPoMapping.ConfigJson</c>) logs a warning and returns null so the caller falls
    /// through to the fixed transformer — never a throw.
    /// </summary>
    private async Task<(OrderMappingOverride? Override, string? OutputJson)> TryReadSupplierPromotedOutputAsync(
        Guid organisationId, Guid supplierId, OrderMappingOverride? mappingOverride, CancellationToken ct)
    {
        try
        {
            var supplierConfig = await _poMappings.GetAsync(organisationId, supplierId, ct);
            if (!OrderMappingOverrideReader.HasUsablePromotedOutput(supplierConfig))
                return (null, null);

            var synthetic = new OrderMappingOverride
            {
                CustomFields = mappingOverride?.CustomFields ?? new List<CustomField>(),
                Output       = supplierConfig!.Output,
            };

            return (synthetic, JsonSerializer.Serialize(supplierConfig.Output, SupplierOutputSerializerOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read the supplier-promoted output mapping for supplier {SupplierId}; using the fixed transformer.",
                supplierId);
            return (null, null);
        }
    }
}
