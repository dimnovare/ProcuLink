using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Helpers;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Mapping;
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
    private readonly IOrderExceptionService       _exceptions;
    private readonly IPoMappingService           _poMappingService;
    private readonly IAiMappingService           _aiMappings;
    private readonly IEnumerable<ITransformService> _transformers;
    private readonly ILogger<OrderService>       _logger;

    private readonly IIntegrationTriggerService  _integrationTrigger;
    private readonly IFormatDetector             _formatDetector;

    public OrderService(
        ProcuLinkDbContext             db,
        IFileStorageService            fileStorage,
        OrderParserFactory             parserFactory,
        IItemMappingService            mappings,
        IOrderExceptionService         exceptions,
        IPoMappingService              poMappingService,
        IAiMappingService              aiMappings,
        IEnumerable<ITransformService> transformers,
        ILogger<OrderService>          logger,
        IIntegrationTriggerService     integrationTrigger,
        IFormatDetector                formatDetector)
    {
        _db                 = db;
        _fileStorage        = fileStorage;
        _parserFactory      = parserFactory;
        _mappings           = mappings;
        _exceptions         = exceptions;
        _poMappingService   = poMappingService;
        _aiMappings         = aiMappings;
        _transformers       = transformers;
        _logger             = logger;
        _integrationTrigger = integrationTrigger;
        _formatDetector     = formatDetector;
    }

    /// <summary>
    /// Best-effort exception reconciliation: exception generation is operational
    /// observability data and must never fail the parent order operation.
    /// </summary>
    private async Task SafeReconcileExceptionsAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        try
        {
            await _exceptions.ReconcileAsync(orgId, orderId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception reconcile failed for order {OrderId} (non-fatal).", orderId);
        }
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

        // 2. Buffer the stream first — we need it to (a) peek for content-based
        //    parser disambiguation (.xml UBL vs cXML, .txt EDIFACT) and
        //    (b) replay for upload + parse.
        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);

        if (buffer.Length == 0)
            return Result<PurchaseOrderEntity>.Fail("Uploaded file is empty.");

        // 3. Resolve parser using extension + content peek (stream-aware overload)
        IPurchaseOrderParser parser;
        try
        {
            buffer.Position = 0;
            parser = _parserFactory.GetParser(extension, buffer);
            buffer.Position = 0;
        }
        catch (UnsupportedFileFormatException ex)
        {
            return Result<PurchaseOrderEntity>.Fail(ex.Message);
        }

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

        // 6. Auto-resolve all lines against item_mappings, then batch-suggest for the
        //    leftovers — one IN query + at most one AI call for the whole order.
        var supplierName = await GetSupplierNameAsync(organisationId, supplierId, ct);
        var aiCandidates = await GetAiMappingCandidatesAsync(organisationId, supplierId, ct);
        var lineEntities = await BuildLineEntitiesAsync(
            organisationId, supplierId, supplierName, parsedOrder.Lines, aiCandidates, ct);

        bool anyUnresolved    = lineEntities.Any(l => l.NeedsReview);
        var  aiSuggestionCount = lineEntities.Count(l => !string.IsNullOrWhiteSpace(l.AiSuggestedSupplierItemCode));

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
            BuyerName    = string.IsNullOrWhiteSpace(parsedOrder.BuyerName)
                               ? null
                               : parsedOrder.BuyerName.Trim(),
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
            unresolvedCount = lineEntities.Count(l => l.NeedsReview),
            aiSuggestionCount
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

        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);

        if (buffer.Length == 0)
            return Result<PurchaseOrderEntity>.Fail("Uploaded file is empty.");

        // Validate parser exists before touching R2. Use the stream-aware factory
        // so ambiguous extensions (.xml, .edi, .txt) route to the real parser
        // by content rather than registration order.
        try
        {
            buffer.Position = 0;
            _parserFactory.GetParser(extension, buffer);
            buffer.Position = 0;
        }
        catch (UnsupportedFileFormatException ex)
        {
            return Result<PurchaseOrderEntity>.Fail(ex.Message);
        }

        // Upload raw file to R2
        var orderId       = Guid.NewGuid();
        var sourceFileKey = $"{organisationId}/{orderId}/{safeFilename}";

        buffer.Position = 0;
        await _fileStorage.UploadAsync(buffer, sourceFileKey, contentType, ct);
        _logger.LogInformation("Uploaded source file to R2: {Key}", sourceFileKey);

        // Load supplier so navigation property is set for MapToDto.
        // Scope to the caller's org — FindAsync would resolve by PK alone and let
        // a user in org A reference org B's supplier (cross-tenant injection).
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId && s.OrgId == organisationId, ct);
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

        await EmitPassportEventAsync(organisationId, orderId, "Upload", "Created",
            payload: new { source = sourceFileKey }, ct: ct);

        // ── Wave 4: fire order.created trigger ───────────────────────────────────
        _ = _integrationTrigger.EnqueueAsync(
            organisationId,
            "order.created",
            new
            {
                order_id        = entity.Id,
                status          = entity.Status,
                source_filename = safeFilename,
                created_at      = entity.CreatedAt,
            },
            ct);

        _logger.LogInformation(
            "Order stub {OrderId} created for org {OrgId}, status=parsing",
            orderId, organisationId);

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── CreateStubFromParsedOrderAsync ────────────────────────────────────────

    public async Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(
        Guid organisationId,
        Guid supplierId,
        ExtractedOrder order,
        string source,
        CancellationToken ct)
    {
        if (order is null || order.Lines is null || order.Lines.Count == 0)
            return Result<PurchaseOrderEntity>.Fail("Extracted order contains no line items.");

        // Scope to the caller's org — FindAsync would resolve by PK alone and let
        // a user in org A reference org B's supplier (cross-tenant injection).
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId && s.OrgId == organisationId, ct);
        if (supplier is null)
            return Result<PurchaseOrderEntity>.Fail("Supplier not found.");

        // Field-by-field map: ExtractedOrder → Transform.ParsedOrder so the
        // existing auto-resolve / AI-suggest path can be reused unchanged.
        var parsedLines = order.Lines.Select(l => new ParsedOrderLine(
            LineNumber:    l.LineNumber,
            BuyerItemCode: l.BuyerItemCode,
            Description:   l.Description,
            Quantity:      l.Quantity,
            Unit:          l.Unit,
            UnitPrice:     l.UnitPrice
        )).ToList();

        var aiCandidates  = await GetAiMappingCandidatesAsync(organisationId, supplierId, ct);
        var lineEntities  = await BuildLineEntitiesAsync(
            organisationId, supplierId, supplier.Name, parsedLines, aiCandidates, ct);

        var anyUnresolved     = lineEntities.Any(l => l.NeedsReview);
        var aiSuggestionCount = lineEntities.Count(l => !string.IsNullOrWhiteSpace(l.AiSuggestedSupplierItemCode));

        var orderId = Guid.NewGuid();
        var now     = DateTime.UtcNow;

        // Provenance tag stored on canonical JSON so the review UI can show
        // where the order came from. Mirrors the buyerName lookup pattern in
        // ListAsync — additional fields are surfaced via the same column.
        var canonicalPayload = new
        {
            source,
            buyerName = order.BuyerName,
            poNumber  = order.PoNumber,
            orderDate = order.OrderDate,
            currency  = order.Currency,
        };
        var canonicalJson = JsonDocument.Parse(JsonSerializer.Serialize(canonicalPayload));

        var entity = new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = organisationId,
            SupplierId    = supplierId,
            Supplier      = supplier,
            PoNumber      = string.IsNullOrWhiteSpace(order.PoNumber)
                                ? $"PO-{now:yyyyMMddHHmmss}"
                                : order.PoNumber!,
            BuyerName     = string.IsNullOrWhiteSpace(order.BuyerName)
                                ? null
                                : order.BuyerName.Trim(),
            OrderDate     = order.OrderDate.HasValue
                                ? DateOnly.FromDateTime(order.OrderDate.Value)
                                : DateOnly.FromDateTime(now),
            Currency      = order.Currency ?? "EUR",
            Status        = anyUnresolved ? "pending_review" : "ready",
            SourceFileKey = null,
            CanonicalJson = canonicalJson,
            CreatedAt     = now,
            UpdatedAt     = now,
            Lines         = lineEntities,
        };

        _db.PurchaseOrders.Add(entity);
        _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "Created", new
        {
            source,
            lineCount       = lineEntities.Count,
            unresolvedCount = lineEntities.Count(l => l.NeedsReview),
            aiSuggestionCount,
        }));

        await _db.SaveChangesAsync(ct);

        await EmitPassportEventAsync(organisationId, orderId, "Upload", "Created",
            payload: new { source }, ct: ct);

        // ── Wave 4: fire order.created trigger ───────────────────────────────────
        _ = _integrationTrigger.EnqueueAsync(
            organisationId,
            "order.created",
            new
            {
                order_id   = entity.Id,
                status     = entity.Status,
                source,
                created_at = entity.CreatedAt,
            },
            ct);

        _logger.LogInformation(
            "Order {OrderId} created from extracted payload (source={Source}) for org {OrgId}: {LineCount} lines, {Unresolved} unresolved, status={Status}",
            orderId, source, organisationId, lineEntities.Count,
            lineEntities.Count(l => l.NeedsReview), entity.Status);

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── ParseStoredFileAsync ──────────────────────────────────────────────────

    public async Task<Result<ParsedFileOutput>> ParseStoredFileAsync(
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
            return Result<ParsedFileOutput>.Fail("Order not found.");

        // Idempotency guard — only parse if still in "parsing" state
        if (entity.Status != "parsing")
        {
            _logger.LogInformation(
                "Order {OrderId} already processed (status={Status}), skipping parse",
                orderId, entity.Status);
            return Result<ParsedFileOutput>.Ok(new ParsedFileOutput(entity, null, "unknown"));
        }

        if (string.IsNullOrWhiteSpace(entity.SourceFileKey))
            return Result<ParsedFileOutput>.Fail("Order has no source file key.");

        // Download file from R2/local storage
        Stream fileStream;
        try
        {
            fileStream = await _fileStorage.DownloadAsync(entity.SourceFileKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download source file {Key}", entity.SourceFileKey);
            return Result<ParsedFileOutput>.Fail($"Could not download source file: {ex.Message}");
        }

        // Format + column-header metadata, captured while the buffer is in memory so the
        // caller (ParseOrderJob) can record the schema fingerprint without re-downloading.
        DetectedFormat? detected = null;

        await using (fileStream)
        {
            using var buffer = new MemoryStream();
            await fileStream.CopyToAsync(buffer, ct);
            buffer.Position = 0; // DetectAsync reads from the current position — rewind so it sees the content.

            // Detect format + column headers (fed to the schema-fingerprint recorder). Non-fatal.
            try { detected = await _formatDetector.DetectAsync(buffer, entity.SourceFileKey, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Format detection failed for order {OrderId} (non-fatal)", orderId); }

            var extension = Path.GetExtension(entity.SourceFileKey).ToLowerInvariant();

            var poMapping = await _poMappingService.GetAsync(organisationId, entity.SupplierId, ct);

            // Validate extension when no mapping template is available (fast-fail before R2 download already done)
            if (poMapping is null || extension != ".csv")
            {
                try { _parserFactory.GetParser(extension); }
                catch (UnsupportedFileFormatException ex)
                {
                    await SetOrderFailedAsync(orderId, organisationId, ct);
                    _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "ParseFailed",
                        new { error = ParseFailureExplain.ForUnsupportedFormat(extension), stage = "parse", detail = ex.Message }));
                    await _db.SaveChangesAsync(ct);
                    await EmitPassportEventAsync(organisationId, orderId, "Parse", "Failed",
                        payload: new { error = ParseFailureExplain.ForUnsupportedFormat(extension) }, ct: ct);
                    await SafeReconcileExceptionsAsync(organisationId, orderId, ct);
                    return Result<ParsedFileOutput>.Fail(ex.Message);
                }
            }

            buffer.Position = 0;
            ParsedOrder parsedOrder;
            try
            {
                if (poMapping is not null && extension == ".csv")
                {
                    parsedOrder = await ParseWithMappingTemplateAsync(buffer.ToArray(), poMapping, ct);
                }
                else
                {
                    var parser = _parserFactory.GetParser(extension, buffer);
                    buffer.Position = 0;
                    parsedOrder = await parser.ParseAsync(buffer, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse file for order {OrderId}", orderId);
                await SetOrderFailedAsync(orderId, organisationId, ct);
                _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "ParseFailed",
                    new { error = ParseFailureExplain.ForException(extension, ex), stage = "parse", detail = ex.Message }));
                await _db.SaveChangesAsync(ct);
                await EmitPassportEventAsync(organisationId, orderId, "Parse", "Failed",
                    payload: new { error = ex.Message }, ct: ct);
                await SafeReconcileExceptionsAsync(organisationId, orderId, ct);
                return Result<ParsedFileOutput>.Fail($"Could not parse file: {ex.Message}");
            }

            if (parsedOrder.Lines.Count == 0)
            {
                await SetOrderFailedAsync(orderId, organisationId, ct);
                _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "ParseFailed",
                    new { error = ParseFailureExplain.ForEmptyLines(extension), stage = "parse", detail = "0 lines parsed" }));
                await _db.SaveChangesAsync(ct);
                await EmitPassportEventAsync(organisationId, orderId, "Parse", "Failed",
                    payload: new { error = "0 lines parsed" }, ct: ct);
                await SafeReconcileExceptionsAsync(organisationId, orderId, ct);
                return Result<ParsedFileOutput>.Fail("File contains no line items.");
            }

            // Auto-resolve lines against item_mappings (batched), then one AI call for leftovers
            var supplierName = entity.Supplier?.Name
                               ?? await GetSupplierNameAsync(organisationId, entity.SupplierId, ct);
            var aiCandidates = await GetAiMappingCandidatesAsync(organisationId, entity.SupplierId, ct);
            var lineEntities = await BuildLineEntitiesAsync(
                organisationId, entity.SupplierId, supplierName, parsedOrder.Lines, aiCandidates, ct);

            bool anyUnresolved    = lineEntities.Any(l => l.NeedsReview);
            var  aiSuggestionCount = lineEntities.Count(l => !string.IsNullOrWhiteSpace(l.AiSuggestedSupplierItemCode));

            // ── Persist parsed results ────────────────────────────────────────
            // Use ExecuteUpdateAsync for the parent row so we bypass EF's change
            // tracker entirely.  This avoids DbUpdateConcurrencyException (0 rows
            // affected) that occurs when the long-running parse leaves the tracked
            // entity stale on Neon serverless / Npgsql 8 connection multiplexing.
            var now = DateTime.UtcNow;
            var newPoNumber = string.IsNullOrWhiteSpace(parsedOrder.PoNumber)
                                ? $"PO-{now:yyyyMMddHHmmss}"
                                : parsedOrder.PoNumber;
            var newOrderDate = parsedOrder.OrderDate.HasValue
                                ? DateOnly.FromDateTime(parsedOrder.OrderDate.Value)
                                : DateOnly.FromDateTime(now);
            var newCurrency  = parsedOrder.Currency ?? "EUR";
            var newStatus    = anyUnresolved ? "pending_review" : "ready";
            // Denormalise buyer name for SQL search (avoid JSON parse at query time).
            var newBuyerName = string.IsNullOrWhiteSpace(parsedOrder.BuyerName)
                                ? null
                                : parsedOrder.BuyerName.Trim();

            var updated = await _db.PurchaseOrders
                .Where(o => o.Id == orderId && o.OrgId == organisationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.PoNumber,   newPoNumber)
                    .SetProperty(o => o.OrderDate,  newOrderDate)
                    .SetProperty(o => o.Currency,   newCurrency)
                    .SetProperty(o => o.Status,     newStatus)
                    .SetProperty(o => o.BuyerName,  newBuyerName)
                    .SetProperty(o => o.UpdatedAt,  now), ct);

            if (updated == 0)
            {
                _logger.LogError(
                    "ParseStoredFileAsync: ExecuteUpdateAsync affected 0 rows for order {OrderId}", orderId);
                return Result<ParsedFileOutput>.Fail("Order could not be updated — not found or already deleted.");
            }

            // Set the FK on each line before inserting (EF relationship fixup
            // can't run because we detached the parent from tracking above).
            foreach (var line in lineEntities) line.OrderId = orderId;
            _db.PurchaseOrderLines.AddRange(lineEntities);
            _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "Parsed", new
            {
                lineCount       = lineEntities.Count,
                unresolvedCount = lineEntities.Count(l => l.NeedsReview),
                aiSuggestionCount,
                newStatus,
            }));

            await _db.SaveChangesAsync(ct);

            await EmitPassportEventAsync(organisationId, orderId, "Parse", "Succeeded",
                payload: new { lineCount = lineEntities.Count }, ct: ct);

            // Reflect changes on the in-memory entity so callers see the final state.
            entity.PoNumber  = newPoNumber;
            entity.OrderDate = newOrderDate;
            entity.Currency  = newCurrency;
            entity.Status    = newStatus;
            entity.BuyerName = newBuyerName;
            entity.UpdatedAt = now;
            entity.Lines = lineEntities;

            _logger.LogInformation(
                "Order {OrderId} parsed: {LineCount} lines, {Unresolved} unresolved, status={Status}",
                orderId, lineEntities.Count, lineEntities.Count(l => l.NeedsReview), entity.Status);
        }

        await SafeReconcileExceptionsAsync(organisationId, orderId, ct);

        return Result<ParsedFileOutput>.Ok(
            new ParsedFileOutput(entity, detected?.ColumnHeaders, detected?.Format ?? "unknown"));
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
        // Split the projection into two queries to avoid EF's MultipleCollectionInclude
        // issue: accessing e.Lines twice in a single SELECT generates a cartesian-product
        // JOIN that drops rows with 0 lines (stubs in "parsing" status).
        //
        // Step 1: fetch order + supplier name (LEFT JOIN — null-safe for deleted suppliers).
        // Step 2: fetch per-order line aggregates (count, unresolved, total value) in a second
        //         scalar query, then join in memory to avoid EF cartesian-product issues.
        var orders = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(x => x.OrgId == organisationId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(e => new
            {
                e.Id,
                e.PoNumber,
                SupplierName = e.Supplier != null ? e.Supplier.Name : "Unknown Supplier",
                e.OrderDate,
                e.Status,
                e.CreatedAt,
                e.Currency,
                e.SourceFileKey,
                e.CanonicalJson,
            })
            .ToListAsync(ct);

        if (orders.Count == 0)
            return Result<IReadOnlyList<PurchaseOrderSummary>>.Ok(Array.Empty<PurchaseOrderSummary>());

        var orderIds = orders.Select(o => o.Id).ToList();

        // Aggregate line counts and total order value in a single query.
        var lineCounts = await _db.PurchaseOrderLines
            .AsNoTracking()
            .Where(l => orderIds.Contains(l.OrderId))
            .GroupBy(l => l.OrderId)
            .Select(g => new
            {
                OrderId    = g.Key,
                Total      = g.Count(),
                Unresolved = g.Count(l => l.NeedsReview),
                TotalValue = g.Sum(l => l.Quantity * l.UnitPrice),
            })
            .ToDictionaryAsync(g => g.OrderId, ct);

        var summaries = orders.Select(o =>
        {
            lineCounts.TryGetValue(o.Id, out var lc);

            // Extract buyer name from canonical JSON (populated after parsing).
            string? buyerName = null;
            if (o.CanonicalJson != null)
            {
                try
                {
                    var root = o.CanonicalJson.RootElement;
                    if (root.TryGetProperty("buyerName", out var el))
                        buyerName = el.GetString();
                    else if (root.TryGetProperty("BuyerName", out var el2))
                        buyerName = el2.GetString();
                }
                catch { /* malformed JSON — ignore */ }
            }

            // Derive a short format label from the source file extension.
            string? sourceFormat = null;
            if (!string.IsNullOrEmpty(o.SourceFileKey))
            {
                var ext = System.IO.Path.GetExtension(o.SourceFileKey).TrimStart('.').ToLowerInvariant();
                sourceFormat = ext switch
                {
                    "pdf"          => "pdf",
                    "csv"          => "csv",
                    "xlsx" or "xls"=> "xlsx",
                    "xml" or "cxml"=> "cxml",
                    "edi" or "x12" => "edi",
                    _              => null,
                };
            }

            return new PurchaseOrderSummary(
                o.Id,
                o.PoNumber,
                o.SupplierName,
                buyerName,
                o.OrderDate,
                o.Status,
                lc?.Total      ?? 0,
                lc?.Unresolved ?? 0,
                lc?.TotalValue ?? 0m,
                o.Currency,
                sourceFormat,
                o.CreatedAt);
        }).ToList();

        return Result<IReadOnlyList<PurchaseOrderSummary>>.Ok(summaries);
    }

    // ── ListPagedAsync ────────────────────────────────────────────────────────

    public async Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(
        Guid      organisationId,
        int       page,
        int       pageSize,
        string?   status,
        Guid?     supplierId,
        string?   search,
        DateTime? dateFrom,
        DateTime? dateTo,
        CancellationToken ct)
    {
        // ── Step 1: build base query with SQL-filterable predicates ────────────
        // Org scope is mandatory on every query — never omit.
        var baseQuery = _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == organisationId);

        if (!string.IsNullOrWhiteSpace(status))
            baseQuery = baseQuery.Where(o => o.Status == status);

        if (supplierId.HasValue)
            baseQuery = baseQuery.Where(o => o.SupplierId == supplierId.Value);

        if (dateFrom.HasValue)
            baseQuery = baseQuery.Where(o => o.CreatedAt >= dateFrom.Value);

        if (dateTo.HasValue)
            baseQuery = baseQuery.Where(o => o.CreatedAt <= dateTo.Value);

        // ── Step 2 (new): SQL-native search predicate ────────────────────────────
        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            baseQuery = baseQuery.Where(o =>
                EF.Functions.ILike(o.PoNumber, $"%{trimmedSearch}%") ||
                EF.Functions.ILike(o.Supplier!.Name, $"%{trimmedSearch}%") ||
                (o.BuyerName != null && EF.Functions.ILike(o.BuyerName, $"%{trimmedSearch}%")));
        }

        // ── Step 3: total count from SQL (no full-table load) ──────────────────
        var totalCount = await baseQuery.CountAsync(ct);
        if (totalCount == 0)
            return Result<(IReadOnlyList<PurchaseOrderSummary>, int)>.Ok(
                (Array.Empty<PurchaseOrderSummary>(), 0));

        // ── Step 4: paginate in SQL, select minimal columns ────────────────────
        var skip  = (page - 1) * pageSize;
        var paged = await baseQuery
            .OrderByDescending(o => o.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .Select(o => new
            {
                o.Id,
                o.PoNumber,
                SupplierName = o.Supplier != null ? o.Supplier.Name : "Unknown Supplier",
                o.BuyerName,
                o.OrderDate,
                o.Status,
                o.CreatedAt,
                o.Currency,
                o.SourceFileKey,
            })
            .ToListAsync(ct);

        // ── Step 5: aggregate line counts for the page subset only ────────────
        var pagedIds = paged.Select(o => o.Id).ToList();

        var lineCounts = await _db.PurchaseOrderLines
            .AsNoTracking()
            .Where(l => pagedIds.Contains(l.OrderId))
            .GroupBy(l => l.OrderId)
            .Select(g => new
            {
                OrderId    = g.Key,
                Total      = g.Count(),
                Unresolved = g.Count(l => l.NeedsReview),
                TotalValue = g.Sum(l => l.Quantity * l.UnitPrice),
            })
            .ToDictionaryAsync(g => g.OrderId, ct);

        // ── Step 6: project to PurchaseOrderSummary ───────────────────────────
        var summaries = paged.Select(o =>
        {
            lineCounts.TryGetValue(o.Id, out var lc);

            string? sourceFormat = null;
            if (!string.IsNullOrEmpty(o.SourceFileKey))
            {
                var ext = System.IO.Path.GetExtension(o.SourceFileKey).TrimStart('.').ToLowerInvariant();
                sourceFormat = ext switch
                {
                    "pdf"           => "pdf",
                    "csv"           => "csv",
                    "xlsx" or "xls" => "xlsx",
                    "xml" or "cxml" => "cxml",
                    "edi" or "x12"  => "edi",
                    _               => null,
                };
            }

            return new PurchaseOrderSummary(
                o.Id,
                o.PoNumber,
                o.SupplierName,
                o.BuyerName,
                o.OrderDate,
                o.Status,
                lc?.Total      ?? 0,
                lc?.Unresolved ?? 0,
                lc?.TotalValue ?? 0m,
                o.Currency,
                sourceFormat,
                o.CreatedAt);
        }).ToList();

        return Result<(IReadOnlyList<PurchaseOrderSummary>, int)>.Ok(
            ((IReadOnlyList<PurchaseOrderSummary>)summaries, totalCount));
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

        entity.Status    = OrderStatusConstants.ReadyToDeliver;
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

        // Wrap all writes in a transaction so mapping corrections, order status,
        // audit event, and passport event commit atomically (relational providers only;
        // the InMemory test provider does not support transactions).
        await using var tx = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        // Apply resolutions
        foreach (var res in resolutions)
        {
            var line             = entity.Lines.First(l => l.LineNumber == res.LineNumber);
            line.SupplierItemCode = res.SupplierItemCode.Trim();
            line.NeedsReview     = false;
            line.Confidence      = 1.0f;
            line.AiSuggestedSupplierItemCode = null;
            line.AiSuggestionConfidence = null;
            line.AiSuggestionReason = null;
            line.AiSuggestionProvenance = null;

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

        await EmitPassportEventAsync(organisationId, orderId, "Map", "Corrected",
            actorType: "user",
            payload: new { linesResolved = resolutions.Count, savedMappings = saveMappings }, ct: ct);

        if (tx is not null)
            await tx.CommitAsync(ct);

        await SafeReconcileExceptionsAsync(organisationId, orderId, ct);

        _logger.LogInformation(
            "Order {OrderId} resolved: {Count} lines, saveMappings={Save}, status={Status}",
            orderId, resolutions.Count, saveMappings, entity.Status);

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── MarkRejectedAsync ─────────────────────────────────────────────────────

    public async Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(
        Guid organisationId,
        Guid orderId,
        string reason,
        CancellationToken ct)
    {
        var entity = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Include(x => x.OutboundArtifacts)
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<PurchaseOrderEntity>.Fail("Order not found.");

        var now = DateTime.UtcNow;
        entity.Status    = OrderStatusConstants.RejectedBySupplier;
        entity.UpdatedAt = now;

        // Write the rejection reason onto the most-recent delivery attempt for this
        // order (if one exists), so the audit trail shows it in context.
        var latestAttempt = await _db.DeliveryAttempts
            .Where(a => a.OrderId == orderId && a.OrgId == organisationId)
            .OrderByDescending(a => a.AttemptedAt)
            .FirstOrDefaultAsync(ct);

        if (latestAttempt is not null)
        {
            latestAttempt.RejectionReason = reason;
        }

        _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "MarkedRejected", new
        {
            reason,
            markedAt = now,
        }));

        await _db.SaveChangesAsync(ct);

        await SafeReconcileExceptionsAsync(organisationId, orderId, ct);

        _logger.LogInformation(
            "Order {OrderId} (org {OrgId}) manually marked as rejected. Reason: {Reason}",
            orderId, organisationId, reason);

        return Result<PurchaseOrderEntity>.Ok(entity);
    }

    // ── AcceptAiSuggestionsAsync ──────────────────────────────────────────────

    public async Task<Result<int>> AcceptAiSuggestionsAsync(
        Guid organisationId,
        Guid orderId,
        double minConfidence,
        CancellationToken ct)
    {
        // Load with tracking so EF picks up property changes on the line entities
        var entity = await _db.PurchaseOrders
            .Include(x => x.Lines)
            .Where(x => x.Id == orderId && x.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            return Result<int>.Fail("Order not found.");

        var acceptedCount = 0;

        foreach (var line in entity.Lines)
        {
            if (!line.NeedsReview) continue;
            if (string.IsNullOrWhiteSpace(line.AiSuggestedSupplierItemCode)) continue;
            if ((line.AiSuggestionConfidence ?? 0.0) < minConfidence) continue;

            line.SupplierItemCode           = line.AiSuggestedSupplierItemCode;
            line.Confidence                 = (float)(line.AiSuggestionConfidence ?? line.Confidence);
            line.NeedsReview                = false;
            line.AiSuggestedSupplierItemCode = null;
            line.AiSuggestionConfidence     = null;
            line.AiSuggestionReason         = null;
            line.AiSuggestionProvenance     = null;

            acceptedCount++;
        }

        // Recompute order status
        entity.Status    = entity.Lines.Any(l => l.NeedsReview)
                               ? OrderStatusConstants.PendingReview
                               : OrderStatusConstants.Ready;
        entity.UpdatedAt = DateTime.UtcNow;

        _db.AuditEvents.Add(BuildAuditEvent(organisationId, orderId, "AiSuggestionsBulkAccepted", new
        {
            acceptedCount,
            minConfidence,
            newStatus = entity.Status
        }));

        await _db.SaveChangesAsync(ct);

        await EmitPassportEventAsync(organisationId, orderId, "Map", "AiAccepted",
            actorType: "ai",
            payload: new { accepted = acceptedCount }, ct: ct);

        await SafeReconcileExceptionsAsync(organisationId, orderId, ct);

        _logger.LogInformation(
            "Order {OrderId}: {Count} AI suggestions bulk-accepted (minConfidence={Min}), status={Status}",
            orderId, acceptedCount, minConfidence, entity.Status);

        return Result<int>.Ok(acceptedCount);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds line entities for a whole parsed order in two batched passes instead of
    /// per-line round-trips:
    ///  1. one org+supplier-scoped <c>IN</c> query resolves every buyer item code;
    ///  2. one structured-output AI call suggests codes for the lines still unresolved.
    /// Per-line field mapping and NeedsReview semantics are identical to the previous
    /// one-line-at-a-time path.
    /// </summary>
    private async Task<List<PurchaseOrderLineEntity>> BuildLineEntitiesAsync(
        Guid organisationId,
        Guid supplierId,
        string supplierName,
        IReadOnlyList<ParsedOrderLine> lines,
        IReadOnlyList<AiMappingCandidate> aiCandidates,
        CancellationToken ct)
    {
        // Pass 1 — deterministic resolve for every line in a single query.
        var resolvedMap = await _mappings.ResolveManyAsync(
            organisationId,
            supplierId,
            lines.Select(l => l.BuyerItemCode),
            ct);

        // Lookup helper mirroring ResolveManyAsync's trimmed, case-sensitive keys.
        static string? ResolveFromMap(IReadOnlyDictionary<string, string?> map, string? buyerItemCode)
        {
            if (string.IsNullOrWhiteSpace(buyerItemCode)) return null;
            return map.TryGetValue(buyerItemCode.Trim(), out var code) ? code : null;
        }

        // Pass 2 — gather the still-unresolved lines and make ONE batched AI call.
        var unresolvedContexts = lines
            .Where(l => string.IsNullOrWhiteSpace(ResolveFromMap(resolvedMap, l.BuyerItemCode)))
            .Select(l => new AiMappingLineContext(
                l.LineNumber,
                l.BuyerItemCode,
                l.Description,
                l.Quantity,
                l.Unit))
            .ToList();

        IReadOnlyDictionary<int, AiMappingSuggestion> suggestions =
            new Dictionary<int, AiMappingSuggestion>();

        if (unresolvedContexts.Count > 0)
        {
            suggestions = await _aiMappings.SuggestSupplierItemCodesAsync(
                organisationId,
                supplierId,
                supplierName,
                unresolvedContexts,
                aiCandidates,
                ct);
        }

        // Materialise entities from the in-memory dictionaries — no further I/O.
        var entities = new List<PurchaseOrderLineEntity>(lines.Count);
        foreach (var line in lines)
        {
            var supplierCode = ResolveFromMap(resolvedMap, line.BuyerItemCode);
            bool resolved = !string.IsNullOrWhiteSpace(supplierCode);

            AiMappingSuggestion? suggestion = null;
            if (!resolved)
                suggestions.TryGetValue(line.LineNumber, out suggestion);

            entities.Add(new PurchaseOrderLineEntity
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
                NeedsReview      = !resolved,
                AiSuggestedSupplierItemCode = suggestion?.SupplierItemCode,
                AiSuggestionConfidence = suggestion?.Confidence,
                AiSuggestionReason = suggestion?.Reason,
                AiSuggestionProvenance = suggestion?.Provenance
            });
        }

        return entities;
    }

    private async Task<string> GetSupplierNameAsync(
        Guid organisationId,
        Guid supplierId,
        CancellationToken ct)
    {
        return await _db.Suppliers
            .AsNoTracking()
            .Where(s => s.OrgId == organisationId && s.Id == supplierId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;
    }

    private async Task<IReadOnlyList<AiMappingCandidate>> GetAiMappingCandidatesAsync(
        Guid organisationId,
        Guid supplierId,
        CancellationToken ct)
    {
        return await _db.ItemMappings
            .AsNoTracking()
            .Where(m => m.OrgId == organisationId && m.SupplierId == supplierId)
            .OrderByDescending(m => m.UpdatedAt)
            .Take(40)
            .Select(m => new AiMappingCandidate(
                m.BuyerItemCode,
                m.SupplierItemCode,
                $"existing mapping {m.BuyerItemCode} -> {m.SupplierItemCode}"))
            .ToListAsync(ct);
    }

    private static Task<ParsedOrder> ParseWithMappingTemplateAsync(
        byte[] buffer, PoMappingConfig config, CancellationToken ct)
    {
        using var stream = new MemoryStream(buffer);
        using var reader = new StreamReader(stream);

        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord    = config.HasHeaderRecord,
            Delimiter          = config.Separator,
            PrepareHeaderForMatch = args => args.Header?.ToLowerInvariant().Trim() ?? string.Empty,
            MissingFieldFound  = null!,
            BadDataFound       = null!,
        };

        using var csv = new CsvReader(reader, csvConfig);
        if (config.HasHeaderRecord)
        {
            csv.Read();
            csv.ReadHeader();
        }
        var headers = csv.HeaderRecord ?? Array.Empty<string>();

        var allRows = new List<Dictionary<string, string>>();
        while (csv.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers)
                row[h] = csv.GetField(h) ?? string.Empty;
            allRows.Add(row);
        }

        // For flat PO CSVs: first row provides header-section values, all rows provide lines
        var headerRow = allRows.Count > 0
            ? (IReadOnlyDictionary<string, string>)allRows[0]
            : new Dictionary<string, string>();
        var lineRows = allRows.Cast<IReadOnlyDictionary<string, string>>().ToList();

        var mapped = PoMappingEngine.Apply(headerRow, lineRows, config);

        DateTime? orderDate = null;
        if (mapped.OrderDate is not null && DateTime.TryParse(mapped.OrderDate, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            orderDate = d;

        var lines = mapped.Lines.Select((l, i) => new ParsedOrderLine(
            LineNumber:    int.TryParse(l.LineNumber, out var ln) ? ln : (i + 1),
            BuyerItemCode: l.BuyerItemCode ?? string.Empty,
            Description:   l.Description,
            Quantity:      decimal.TryParse(l.Quantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) ? qty : 0,
            Unit:          l.Unit,
            UnitPrice:     decimal.TryParse(l.UnitPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var up) ? up : null
        )).ToList();

        return Task.FromResult(new ParsedOrder(
            PoNumber:  mapped.PoNumber ?? string.Empty,
            OrderDate: orderDate,
            BuyerName: mapped.BuyerName,
            Currency:  mapped.Currency,
            Lines:     lines
        ));
    }

    /// <summary>
    /// Sets order status to "failed". Loads the row and persists via the change
    /// tracker so this works on both the relational provider and the EF InMemory
    /// test provider (ExecuteUpdateAsync is not translatable on InMemory). The row
    /// is re-loaded at the moment of failure, so it is not stale.
    /// </summary>
    private async Task SetOrderFailedAsync(Guid orderId, Guid organisationId, CancellationToken ct)
    {
        var entity = await _db.PurchaseOrders
            .Where(o => o.Id == orderId && o.OrgId == organisationId)
            .FirstOrDefaultAsync(ct);
        if (entity is null) return;

        entity.Status    = "failed";
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

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

    private async Task EmitPassportEventAsync(
        Guid orgId, Guid orderId,
        string stage, string eventType,
        string actorType = "system", string? actorId = null,
        object? payload = null,
        CancellationToken ct = default)
    {
        _db.PoPassportEvents.Add(new PoPassportEvent
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            OrderId    = orderId,
            Stage      = stage,
            EventType  = eventType,
            ActorType  = actorType,
            ActorId    = actorId,
            Payload    = payload is null ? null : JsonSerializer.Serialize(payload),
            OccurredAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }
}
