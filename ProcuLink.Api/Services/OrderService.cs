using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Helpers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Services;

/// <summary>
/// Orchestrates the upload → parse → resolve → persist lifecycle for Phase 2.
/// Lives in the Api project so it can access both Infrastructure (DbContext) and
/// Transform (OrderParserFactory) without creating a circular project reference.
/// </summary>
public sealed class OrderService : IOrderService
{
    private readonly ProcuLinkDbContext          _db;
    private readonly IFileStorageService         _fileStorage;
    private readonly OrderParserFactory          _parserFactory;
    private readonly IItemMappingService         _mappings;
    private readonly IEnumerable<ITransformService> _transformers;
    private readonly ILogger<OrderService>       _logger;

    public OrderService(
        ProcuLinkDbContext             db,
        IFileStorageService            fileStorage,
        OrderParserFactory             parserFactory,
        IItemMappingService            mappings,
        IEnumerable<ITransformService> transformers,
        ILogger<OrderService>          logger)
    {
        _db           = db;
        _fileStorage  = fileStorage;
        _parserFactory = parserFactory;
        _mappings     = mappings;
        _transformers = transformers;
        _logger       = logger;
    }

    // ── CreateFromFileAsync ───────────────────────────────────────────────────

    public async Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(
        Guid organisationId,
        Guid supplierId,
        Stream fileStream,
        string filename,
        string contentType,
        CancellationToken ct)
    {
        // 1. Sanitise filename — prevent path traversal, null bytes, etc.
        var safeFilename = FileNameSanitiser.Sanitise(filename);
        var extension    = FileNameSanitiser.GetExtension(filename);

        // 2. Resolve parser early so we fail fast before hitting R2
        IPurchaseOrderParser parser;
        try
        {
            parser = _parserFactory.GetParser(extension);
        }
        catch (UnsupportedFileFormatException ex)
        {
            return Result<PurchaseOrderEntity>.Fail(ex.Message);
        }

        // 3. Buffer the stream — we need two passes (upload + parse)
        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);

        if (buffer.Length == 0)
            return Result<PurchaseOrderEntity>.Fail("Uploaded file is empty.");

        // 4. Upload raw file to R2
        var orderId       = Guid.NewGuid();
        var sourceFileKey = $"{organisationId}/{orderId}/{safeFilename}";

        buffer.Position = 0;
        await _fileStorage.UploadAsync(buffer, sourceFileKey, contentType, ct);
        _logger.LogInformation("Uploaded source file to R2: {Key}", sourceFileKey);

        // 5. Parse
        buffer.Position = 0;
        ParsedOrder parsedOrder;
        try
        {
            parsedOrder = await parser.ParseAsync(buffer, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse uploaded file {Filename}", safeFilename);
            return Result<PurchaseOrderEntity>.Fail($"Could not parse file: {ex.Message}");
        }

        if (parsedOrder.Lines.Count == 0)
            return Result<PurchaseOrderEntity>.Fail("File contains no line items.");

        // 6. Auto-resolve each line against the item_mappings table
        var lineEntities   = new List<PurchaseOrderLineEntity>(parsedOrder.Lines.Count);
        bool anyUnresolved = false;

        foreach (var line in parsedOrder.Lines)
        {
            var supplierCode = await _mappings.ResolveAsync(
                organisationId, supplierId, line.BuyerItemCode, ct);

            bool resolved = !string.IsNullOrWhiteSpace(supplierCode);
            if (!resolved) anyUnresolved = true;

            lineEntities.Add(new PurchaseOrderLineEntity
            {
                Id               = Guid.NewGuid(),
                LineNumber       = line.LineNumber,
                BuyerItemCode    = line.BuyerItemCode,
                SupplierItemCode = supplierCode,
                Description      = line.Description,
                Quantity         = line.Quantity,
                Unit             = line.Unit,
                UnitPrice        = line.UnitPrice ?? 0m,
                Confidence       = resolved ? 1.0f : 0.0f,
                NeedsReview      = !resolved
            });
        }

        // 7. Build the order entity
        var now = DateTime.UtcNow;

        var entity = new PurchaseOrderEntity
        {
            Id           = orderId,
            OrgId        = organisationId,
            SupplierId   = supplierId,
            PoNumber     = string.IsNullOrWhiteSpace(parsedOrder.PoNumber)
                               ? $"PO-{now:yyyyMMddHHmmss}"
                               : parsedOrder.PoNumber,
            OrderDate    = parsedOrder.OrderDate.HasValue
                               ? DateOnly.FromDateTime(parsedOrder.OrderDate.Value)
                               : DateOnly.FromDateTime(now),
            Currency     = parsedOrder.Currency ?? "EUR",
            Status       = anyUnresolved ? "pending_review" : "ready",
            SourceFileKey = sourceFileKey,
            CreatedAt    = now,
            UpdatedAt    = now,
            Lines        = lineEntities
        };

        // 8. Persist order + audit event in a single transaction
        _db.PurchaseOrders.Add(entity);
        _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "Created", new
        {
            sourceFileKey,
            lineCount       = lineEntities.Count,
            unresolvedCount = lineEntities.Count(l => l.NeedsReview)
        }));

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Order {OrderId} created for org {OrgId}: {LineCount} lines, {Unresolved} unresolved, status={Status}",
            orderId, organisationId, lineEntities.Count,
            lineEntities.Count(l => l.NeedsReview), entity.Status);

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── CreateStubAsync ───────────────────────────────────────────────────────

    public async Task<Result<PurchaseOrderEntity>> CreateStubAsync(
        Guid organisationId,
        Guid supplierId,
        Stream fileStream,
        string filename,
        string contentType,
        CancellationToken ct)
    {
        var safeFilename = FileNameSanitiser.Sanitise(filename);
        var extension    = FileNameSanitiser.GetExtension(filename);

        // Validate parser exists before touching R2
        try { _parserFactory.GetParser(extension); }
        catch (UnsupportedFileFormatException ex) { return Result<PurchaseOrderEntity>.Fail(ex.Message); }

        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);

        if (buffer.Length == 0)
            return Result<PurchaseOrderEntity>.Fail("Uploaded file is empty.");

        // Upload raw file to R2
        var orderId       = Guid.NewGuid();
        var sourceFileKey = $"{organisationId}/{orderId}/{safeFilename}";

        buffer.Position = 0;
        await _fileStorage.UploadAsync(buffer, sourceFileKey, contentType, ct);
        _logger.LogInformation("Uploaded source file to R2: {Key}", sourceFileKey);

        // Load supplier so navigation property is set for MapToDto
        var supplier = await _db.Suppliers.FindAsync(new object[] { supplierId }, ct);
        if (supplier is null)
            return Result<PurchaseOrderEntity>.Fail("Supplier not found.");

        // Create stub order — no lines yet, status = "parsing"
        var now = DateTime.UtcNow;
        var entity = new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = organisationId,
            SupplierId    = supplierId,
            Supplier      = supplier,
            PoNumber      = $"PO-{now:yyyyMMddHHmmss}",
            OrderDate     = DateOnly.FromDateTime(now),
            Currency      = "EUR",
            Status        = "parsing",
            SourceFileKey = sourceFileKey,
            CreatedAt     = now,
            UpdatedAt     = now,
        };

        _db.PurchaseOrders.Add(entity);
        _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "Created", new
        {
            sourceFileKey,
            mode = "async"
        }));

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Order stub {OrderId} created for org {OrgId}, status=parsing",
            orderId, organisationId);

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── ParseStoredFileAsync ──────────────────────────────────────────────────

    public async Task<Result<PurchaseOrderEntity>> ParseStoredFileAsync(
        Guid organisationId,
        Guid orderId,
        CancellationToken ct)
    {
        var entity = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Include(x => x.Supplier)
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<PurchaseOrderEntity>.Fail("Order not found.");

        // Idempotency guard — only parse if still in "parsing" state
        if (entity.Status != "parsing")
        {
            _logger.LogInformation(
                "Order {OrderId} already processed (status={Status}), skipping parse",
                orderId, entity.Status);
            return Result<PurchaseOrderEntity>.Ok(entity);
        }

        if (string.IsNullOrWhiteSpace(entity.SourceFileKey))
            return Result<PurchaseOrderEntity>.Fail("Order has no source file key.");

        // Download file from R2/local storage
        Stream fileStream;
        try
        {
            fileStream = await _fileStorage.DownloadAsync(entity.SourceFileKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download source file {Key}", entity.SourceFileKey);
            return Result<PurchaseOrderEntity>.Fail($"Could not download source file: {ex.Message}");
        }

        await using (fileStream)
        {
            using var buffer = new MemoryStream();
            await fileStream.CopyToAsync(buffer, ct);

            var extension = Path.GetExtension(entity.SourceFileKey);
            IPurchaseOrderParser parser;
            try { parser = _parserFactory.GetParser(extension); }
            catch (UnsupportedFileFormatException ex)
            {
                entity.Status    = "failed";
                entity.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return Result<PurchaseOrderEntity>.Fail(ex.Message);
            }

            buffer.Position = 0;
            ParsedOrder parsedOrder;
            try { parsedOrder = await parser.ParseAsync(buffer, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse file for order {OrderId}", orderId);
                entity.Status    = "failed";
                entity.UpdatedAt = DateTime.UtcNow;
                _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "ParseFailed",
                    new { error = ex.Message }));
                await _db.SaveChangesAsync(ct);
                return Result<PurchaseOrderEntity>.Fail($"Could not parse file: {ex.Message}");
            }

            if (parsedOrder.Lines.Count == 0)
            {
                entity.Status    = "failed";
                entity.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return Result<PurchaseOrderEntity>.Fail("File contains no line items.");
            }

            // Auto-resolve lines against item_mappings
            var lineEntities   = new List<PurchaseOrderLineEntity>(parsedOrder.Lines.Count);
            bool anyUnresolved = false;

            foreach (var line in parsedOrder.Lines)
            {
                var supplierCode = await _mappings.ResolveAsync(
                    organisationId, entity.SupplierId, line.BuyerItemCode, ct);

                bool resolved = !string.IsNullOrWhiteSpace(supplierCode);
                if (!resolved) anyUnresolved = true;

                lineEntities.Add(new PurchaseOrderLineEntity
                {
                    Id               = Guid.NewGuid(),
                    LineNumber       = line.LineNumber,
                    BuyerItemCode    = line.BuyerItemCode,
                    SupplierItemCode = supplierCode,
                    Description      = line.Description,
                    Quantity         = line.Quantity,
                    Unit             = line.Unit,
                    UnitPrice        = line.UnitPrice ?? 0m,
                    Confidence       = resolved ? 1.0f : 0.0f,
                    NeedsReview      = !resolved
                });
            }

            // Update order with parsed data
            var now = DateTime.UtcNow;
            entity.PoNumber   = string.IsNullOrWhiteSpace(parsedOrder.PoNumber)
                                    ? $"PO-{now:yyyyMMddHHmmss}"
                                    : parsedOrder.PoNumber;
            entity.OrderDate  = parsedOrder.OrderDate.HasValue
                                    ? DateOnly.FromDateTime(parsedOrder.OrderDate.Value)
                                    : DateOnly.FromDateTime(now);
            entity.Currency   = parsedOrder.Currency ?? "EUR";
            entity.Status     = anyUnresolved ? "pending_review" : "ready";
            entity.UpdatedAt  = now;
            entity.Lines.AddRange(lineEntities);

            _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "Parsed", new
            {
                lineCount       = lineEntities.Count,
                unresolvedCount = lineEntities.Count(l => l.NeedsReview),
                newStatus       = entity.Status
            }));

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Order {OrderId} parsed: {LineCount} lines, {Unresolved} unresolved, status={Status}",
                orderId, lineEntities.Count, lineEntities.Count(l => l.NeedsReview), entity.Status);
        }

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    public async Task<Result<PurchaseOrderEntity>> GetByIdAsync(
        Guid organisationId, Guid orderId, CancellationToken ct)
    {
        var entity = await _db.PurchaseOrders
            .AsNoTracking()
            .Include(x => x.Lines)
            .Include(x => x.Supplier)
            .Include(x => x.OutboundArtifacts)
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<PurchaseOrderEntity>.Fail("Order not found.");

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── ListAsync ─────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(
        Guid organisationId, CancellationToken ct)
    {
        // Projection to avoid loading full line data — EF translates this to SQL
        var summaries = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(x => x.OrgId == organisationId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(e => new PurchaseOrderSummary(
                e.Id,
                e.PoNumber,
                e.Supplier.Name,
                e.OrderDate,
                e.Status,
                e.Lines.Count,
                e.Lines.Count(l => l.NeedsReview),
                e.CreatedAt))
            .ToListAsync(ct);

        return Result<IReadOnlyList<PurchaseOrderSummary>>.Ok(summaries);
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

        // Locate the correct transformer (Xml or Csv)
        var transformer = _transformers.FirstOrDefault(t => t.CanTransform(format));
        if (transformer is null)
            return Result<TransformResponse>.Fail($"No transform service registered for format '{format}'.");

        // Mark as transforming so the UI can show a spinner
        entity.Status    = "transforming";
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Generate the document
        TransformResult transformResult;
        try
        {
            transformResult = await transformer.TransformAsync(entity, format, ct);
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

        entity.Status    = "delivered";
        entity.UpdatedAt = now;

        _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "Transformed", new
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

    // ── GetDownloadUrlAsync ───────────────────────────────────────────────────

    public async Task<Result<DownloadUrl>> GetDownloadUrlAsync(
        Guid organisationId,
        Guid orderId,
        Guid artifactId,
        CancellationToken ct)
    {
        // Scope the lookup to the org via the order — prevents cross-tenant access
        var artifact = await _db.OutboundArtifacts
            .AsNoTracking()
            .Where(a => a.Id == artifactId
                     && a.OrderId == orderId
                     && a.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (artifact is null)
            return Result<DownloadUrl>.Fail("Artifact not found.");

        var expiry    = TimeSpan.FromMinutes(15);
        var url       = await _fileStorage.GetSignedDownloadUrlAsync(artifact.FileKey, expiry, ct);
        var expiresAt = DateTime.UtcNow + expiry;

        return Result<DownloadUrl>.Ok(new DownloadUrl(url, expiresAt));
    }

    // ── ResolveAsync ──────────────────────────────────────────────────────────

    public async Task<Result<PurchaseOrderEntity>> ResolveAsync(
        Guid organisationId,
        Guid orderId,
        IReadOnlyList<LineResolution> resolutions,
        bool saveMappings,
        CancellationToken ct)
    {
        // Load with tracking so EF picks up property changes on the line entities
        var entity = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Include(x => x.Supplier)
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<PurchaseOrderEntity>.Fail("Order not found.");

        // Validate all resolutions before mutating anything
        foreach (var res in resolutions)
        {
            if (string.IsNullOrWhiteSpace(res.SupplierItemCode))
                return Result<PurchaseOrderEntity>.Fail(
                    $"SupplierItemCode is required for line {res.LineNumber}.");

            if (!entity.Lines.Any(l => l.LineNumber == res.LineNumber))
                return Result<PurchaseOrderEntity>.Fail(
                    $"Line {res.LineNumber} does not exist in this order.");
        }

        // Apply resolutions
        foreach (var res in resolutions)
        {
            var line             = entity.Lines.First(l => l.LineNumber == res.LineNumber);
            line.SupplierItemCode = res.SupplierItemCode.Trim();
            line.NeedsReview     = false;
            line.Confidence      = 1.0f;

            // Persist the mapping so future uploads auto-resolve it
            if (saveMappings && !string.IsNullOrWhiteSpace(line.BuyerItemCode))
            {
                await _mappings.UpsertAsync(
                    organisationId, entity.SupplierId,
                    line.BuyerItemCode, line.SupplierItemCode,
                    MappingSource.Manual, ct);
            }
        }

        // Recompute order status
        entity.Status    = entity.Lines.Any(l => l.NeedsReview) ? "pending_review" : "ready";
        entity.UpdatedAt = DateTime.UtcNow;

        _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "Resolved", new
        {
            lineCount    = resolutions.Count,
            savedMappings = saveMappings,
            newStatus    = entity.Status
        }));

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Order {OrderId} resolved: {Count} lines, saveMappings={Save}, status={Status}",
            orderId, resolutions.Count, saveMappings, entity.Status);

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AuditEvent BuildAuditEvent(Guid orgId, Guid entityId, string action, object payload) =>
        new()
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            EntityType = "Order",
            EntityId   = entityId,
            Action     = action,
            Payload    = JsonDocument.Parse(JsonSerializer.Serialize(payload)),
            CreatedAt  = DateTime.UtcNow
        };
}
