using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
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
    private readonly ProcuLinkDbContext              _db;
    private readonly IFileStorageService             _fileStorage;
    private readonly IEnumerable<ITransformService>  _transformers;
    private readonly ILogger<OrderService>           _logger;

    public OrderTransformService(
        ProcuLinkDbContext             db,
        IFileStorageService            fileStorage,
        IEnumerable<ITransformService> transformers,
        ILogger<OrderService>          logger)
    {
        _db           = db;
        _fileStorage  = fileStorage;
        _transformers = transformers;
        _logger       = logger;
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

        // heart-piece-flex Phase 2/3: apply the per-order override whenever this order carries a usable
        // per-order output mapping AND the format is one an override can influence.
        //  • CSV/JSON  → the override builder emits the document natively from the output-field rules.
        //  • XML/cXML/UBL/X12 → the override's canonical-field changes are resolved into an EFFECTIVE
        //    entity, then handed to the EXISTING fixed transform (no per-format re-implementation).
        // Otherwise fall through to the EXISTING fixed transformer, unchanged — so the default (no
        // override) path is byte-for-byte identical to today. An override with only custom fields or an
        // empty output config never diverts the transform.
        var mappingOverride = OrderMappingOverrideReader.Read(entity.CanonicalJson);
        var hasUsableOverride =
            OrderMappingOverrideReader.HasUsableOutput(mappingOverride)
            && MappedTransformService.SupportsOverrideFormat(format);

        // CSV/JSON are emitted natively by the override builder; the structured formats reuse the fixed
        // transform fed an effective entity.
        var useNativeOverride =
            hasUsableOverride && MappedTransformService.SupportsOverride(format);

        // Locate the correct fixed transformer (Xml/Csv/Json/...). Always required EXCEPT for the native
        // CSV/JSON override path; we still resolve it up-front so a missing transformer fails before
        // status mutation.
        var transformer = _transformers.FirstOrDefault(t => t.CanTransform(format));
        if (!useNativeOverride && transformer is null)
            return Result<TransformResponse>.Fail($"No transform service registered for format '{format}'.");

        // Mark as transforming so the UI can show a spinner
        entity.Status    = "transforming";
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Generate the document.
        //  • Native CSV/JSON override → the override builder emits the document.
        //  • Structured-format override → resolve an effective entity (canonical-field overrides
        //    applied to the typed columns) and feed it to the EXISTING fixed transform.
        //  • No override → the fixed transform on the original entity, byte-for-byte unchanged.
        TransformResult transformResult;
        try
        {
            if (useNativeOverride)
            {
                transformResult = new MappedTransformService().Build(entity, mappingOverride!, format);
            }
            else if (hasUsableOverride)
            {
                var effective = EffectiveEntityResolver.Resolve(entity, mappingOverride!);
                transformResult = await transformer!.TransformAsync(effective, format, ct);
            }
            else
            {
                transformResult = await transformer!.TransformAsync(entity, format, ct);
            }
        }
        catch (TransformValidationException ex)
        {
            // Revert status on validation failure
            entity.Status    = "ready";
            entity.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return Result<TransformResponse>.Fail(ex.Message);
        }

        // Upload artifact to R2
        var artifactId  = Guid.NewGuid();
        var artifactKey = $"{organisationId}/{orderId}/artifacts/{artifactId}{transformResult.FileExtension}";

        await _fileStorage.UploadAsync(
            transformResult.Content, artifactKey, transformResult.ContentType, ct);

        _logger.LogInformation("Uploaded artifact to R2: {Key}", artifactKey);

        // Persist artifact row + update order status + audit — one SaveChanges
        var now      = DateTime.UtcNow;
        var artifact = new OutboundArtifact
        {
            Id        = artifactId,
            OrderId   = orderId,
            OrgId     = organisationId,
            Format    = format.ToString().ToLowerInvariant(),
            FileKey   = artifactKey,
            CreatedAt = now
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
}
