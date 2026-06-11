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

    private readonly ProcuLinkDbContext              _db;
    private readonly IFileStorageService             _fileStorage;
    private readonly IEnumerable<ITransformService>  _transformers;
    private readonly ILogger<OrderService>           _logger;
    private readonly IPoMappingService               _poMappings;

    public OrderTransformService(
        ProcuLinkDbContext             db,
        IFileStorageService            fileStorage,
        IEnumerable<ITransformService> transformers,
        ILogger<OrderService>          logger,
        IPoMappingService              poMappings)
    {
        _db           = db;
        _fileStorage  = fileStorage;
        _transformers = transformers;
        _logger       = logger;
        _poMappings   = poMappings;
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
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<TransformResponse>.Fail("Order not found.");

        // Pre-flight check: all lines must be resolved
        var unresolved = entity.Lines.Where(l => l.NeedsReview).Select(l => l.LineNumber).ToList();
        if (unresolved.Count > 0)
            return Result<TransformResponse>.Fail(
                $"Resolve all lines before transforming. Unresolved: {string.Join(", ", unresolved)}.");

        // heart-piece-flex flexible mapping: FIVE transform modes, in precedence order.
        //   1. TEMPLATE MODE — order carries a non-blank whole-document OutputTemplate → render the
        //      ENTIRE document from that one Scriban template (ANY format, no fixed transformer needed).
        //   2. NATIVE OVERRIDE (CSV/JSON) — per-order output mapping + a format the override builder
        //      emits natively from the output-field rules.
        //   3. STRUCTURED OVERRIDE (XML/cXML/UBL/X12) — resolve the override's canonical-field changes
        //      into an EFFECTIVE entity, then hand it to the EXISTING fixed transform (no per-format
        //      re-implementation).
        //   4. SUPPLIER-PROMOTED OUTPUT (launch batch 4A) — no usable per-order template/output, but
        //      the supplier carries a promoted PoMappingConfig.Output ("Save mappings for this
        //      supplier") → wrap it as a synthetic override and reuse modes 2/3 verbatim. The
        //      per-order override always stays the higher-priority seam.
        //   5. FIXED TRANSFORMER — the default; byte-for-byte identical to today when neither a
        //      per-order override nor a promoted supplier output mapping is set.
        // An override with only custom fields or an empty output config never diverts the transform.
        var mappingOverride = OrderMappingOverrideReader.Read(entity.CanonicalJson);
        var useTemplate     = OrderMappingOverrideReader.HasUsableTemplate(mappingOverride);
        var hasUsableOverride =
            !useTemplate
            && OrderMappingOverrideReader.HasUsableOutput(mappingOverride)
            && MappedTransformService.SupportsOverrideFormat(format);
        var useNativeOverride = hasUsableOverride && MappedTransformService.SupportsOverride(format);

        // Supplier-promoted output mapping — consulted ONLY when no per-order template/output drives
        // the transform AND the format is one an override can influence (read ONCE per transform).
        // Defensive: a missing / malformed / unusable supplier mapping yields null and the fixed
        // transformer stays in control (logged, never a throw). supplierOutputJson is the camelCase
        // serialization of the promoted Output config, used for the provenance ConfigDigest below.
        OrderMappingOverride? supplierOverride   = null;
        string?               supplierOutputJson = null;
        if (!useTemplate && !hasUsableOverride && MappedTransformService.SupportsOverrideFormat(format))
            (supplierOverride, supplierOutputJson) =
                await TryReadSupplierPromotedOutputAsync(organisationId, entity.SupplierId, mappingOverride, ct);
        var useSupplierMapping = supplierOverride is not null;
        var useSupplierNative  = useSupplierMapping && MappedTransformService.SupportsOverride(format);

        // Locate the fixed transformer (Xml/Csv/Json/...). Required EXCEPT for template mode and the
        // native CSV/JSON override path; resolved up-front so a missing transformer fails before status
        // mutation. NOTE: the supplier-promoted path deliberately does NOT relax this requirement —
        // the fixed transformer must exist so the defensive fallback below is always possible.
        var transformer = _transformers.FirstOrDefault(t => t.CanTransform(format));
        if (!useTemplate && !useNativeOverride && transformer is null)
            return Result<TransformResponse>.Fail($"No transform service registered for format '{format}'.");

        // Mark as transforming so the UI can show a spinner
        entity.Status    = "transforming";
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Generate the document.
        //  • Native CSV/JSON override → the override builder emits the document.
        //  • Structured-format override → resolve an effective entity (canonical-field overrides
        //    applied to the typed columns) and feed it to the EXISTING fixed transform.
        //  • Supplier-promoted output (no per-order override) → same two mechanisms, driven by the
        //    synthetic supplier override; any unexpected failure falls back to the fixed transform.
        //  • No override → the fixed transform on the original entity, byte-for-byte unchanged.
        TransformResult transformResult;
        try
        {
            if (useTemplate)
            {
                transformResult = new ScribanTemplateTransformService().Build(entity, mappingOverride!);
            }
            else if (useNativeOverride)
            {
                transformResult = new MappedTransformService().Build(entity, mappingOverride!, format);
            }
            else if (hasUsableOverride)
            {
                var effective = EffectiveEntityResolver.Resolve(entity, mappingOverride!);
                transformResult = await transformer!.TransformAsync(effective, format, ct);
            }
            else if (useSupplierMapping)
            {
                try
                {
                    transformResult = useSupplierNative
                        ? new MappedTransformService().Build(entity, supplierOverride!, format)
                        : await transformer!.TransformAsync(
                              EffectiveEntityResolver.Resolve(entity, supplierOverride!), format, ct);
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
                    transformResult = await transformer!.TransformAsync(entity, format, ct);
                }
            }
            else
            {
                transformResult = await transformer!.TransformAsync(entity, format, ct);
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
        // "supplier:{outputJson}" when the supplier-promoted output mapping drove it, else the
        // marker "fixed:{format}" for the fixed-transformer path — the prefixes keep the three
        // sources distinguishable. ArtifactSha256 = SHA-256 of the exact generated bytes.
        // ConnectionRevisionId = the order's ingest-time pin.
        string? configDigest = null;
        string? artifactSha  = null;
        try
        {
            artifactSha = ProvenanceHash.TrySha256Hex(artifactBytes);
            var configDescriptor = (useTemplate || useNativeOverride || hasUsableOverride)
                ? OrderMappingOverrideReader.ReadRawJson(entity.CanonicalJson)
                : useSupplierMapping
                    ? $"supplier:{supplierOutputJson}"
                    : $"fixed:{format.ToString().ToLowerInvariant()}";
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
            Format               = format.ToString().ToLowerInvariant(),
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
            orderId, format, artifactId);

        return Result<TransformResponse>.Ok(new TransformResponse(artifactId, artifact.Format, now));
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
