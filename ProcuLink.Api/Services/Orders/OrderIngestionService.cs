using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Helpers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Transform.Mapping;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Services;

/// <summary>
/// Internal sub-service of <see cref="OrderService"/> owning the upload → parse →
/// persist ingestion path. Methods moved verbatim from the original God-class; only
/// the host type and shared-helper call sites changed (audit W1/B1 decomposition).
/// </summary>
internal sealed class OrderIngestionService
{
    private readonly ProcuLinkDbContext         _db;
    private readonly IFileStorageService        _fileStorage;
    private readonly OrderParserFactory         _parserFactory;
    private readonly IItemMappingService        _mappings;
    private readonly IPoMappingService          _poMappingService;
    private readonly IAiMappingService          _aiMappings;
    private readonly ILogger<OrderService>      _logger;
    private readonly IIntegrationTriggerService _integrationTrigger;
    private readonly IFormatDetector            _formatDetector;
    private readonly IStructuredOrderExtractor? _structuredExtractor;
    private readonly OrderServiceShared         _shared;
    private readonly IConnectionResolver        _connectionResolver;
    private readonly ICatalogRetrievalService   _catalogRetrieval;

    public OrderIngestionService(
        ProcuLinkDbContext         db,
        IFileStorageService        fileStorage,
        OrderParserFactory         parserFactory,
        IItemMappingService        mappings,
        IPoMappingService          poMappingService,
        IAiMappingService          aiMappings,
        ILogger<OrderService>      logger,
        IIntegrationTriggerService integrationTrigger,
        IFormatDetector            formatDetector,
        IStructuredOrderExtractor? structuredExtractor,
        OrderServiceShared         shared,
        ICatalogRetrievalService?  catalogRetrieval = null)
    {
        _db                  = db;
        _fileStorage         = fileStorage;
        _parserFactory       = parserFactory;
        _mappings            = mappings;
        _poMappingService    = poMappingService;
        _aiMappings          = aiMappings;
        _logger              = logger;
        _integrationTrigger  = integrationTrigger;
        _formatDetector      = formatDetector;
        _structuredExtractor = structuredExtractor;
        _shared              = shared;
        _connectionResolver  = new ConnectionResolver(db);
        // V10 — indexed catalog retrieval for large catalogs. Self-constructed from the same
        // DbContext (mirrors _connectionResolver); the optional param lets tests inject a fake.
        _catalogRetrieval    = catalogRetrieval ?? new CatalogRetrievalService(db);
    }

    // ── Group V1: pin the active connection revision at ingest ─────────────────
    // Resolved ONCE, at create. A null result (no connection / no active published
    // revision) means "fall back to live config" — exactly today's behaviour.
    private Task<Guid?> ResolveConnectionRevisionAsync(Guid orgId, Guid supplierId, CancellationToken ct)
        => _connectionResolver.ResolveActiveRevisionAsync(orgId, supplierId, ct);

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

        // V1: pin the supplier's active connection revision (null = fall back to live config).
        var connectionRevisionId = await ResolveConnectionRevisionAsync(organisationId, supplierId, ct);

        var entity = new PurchaseOrderEntity
        {
            Id           = orderId,
            OrgId        = organisationId,
            SupplierId   = supplierId,
            ConnectionRevisionId = connectionRevisionId,
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
        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "Created", new
        {
            sourceFileKey,
            lineCount       = lineEntities.Count,
            unresolvedCount = lineEntities.Count(l => l.NeedsReview),
            aiSuggestionCount,
            connectionRevisionId
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

        // V1: pin the supplier's active connection revision now, so the async parse job inherits it.
        var connectionRevisionId = await ResolveConnectionRevisionAsync(organisationId, supplierId, ct);

        var entity = new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = organisationId,
            SupplierId    = supplierId,
            Supplier      = supplier,
            ConnectionRevisionId = connectionRevisionId,
            PoNumber      = $"PO-{now:yyyyMMddHHmmss}",
            OrderDate     = DateOnly.FromDateTime(now),
            Currency      = "EUR",
            Status        = "parsing",
            SourceFileKey = sourceFileKey,
            CreatedAt     = now,
            UpdatedAt     = now,
        };

        _db.PurchaseOrders.Add(entity);
        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "Created", new
        {
            sourceFileKey,
            mode = "async",
            connectionRevisionId
        }));

        await _db.SaveChangesAsync(ct);

        await _shared.EmitPassportEventAsync(organisationId, orderId, "Upload", "Created",
            payload: new { source = sourceFileKey }, ct: ct);

        // ── Wave 4: fire order.created trigger ───────────────────────────────────
        // Awaited (not fire-and-forget): IntegrationTriggerService shares this scoped
        // DbContext; a detached task could outlive the request scope and hit a disposed
        // context, or race a concurrent query on _db.
        await _integrationTrigger.EnqueueAsync(
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
            UnitPrice:     l.UnitPrice,
            LineAmount:    l.LineAmount,
            TaxRate:       l.TaxRate,
            DeliveryDate:  l.DeliveryDate
        )).ToList();

        var aiCandidates  = await GetAiMappingCandidatesAsync(organisationId, supplierId, ct);
        var lineEntities  = await BuildLineEntitiesAsync(
            organisationId, supplierId, supplier.Name, parsedLines, aiCandidates, ct);

        var anyUnresolved     = lineEntities.Any(l => l.NeedsReview);
        var aiSuggestionCount = lineEntities.Count(l => !string.IsNullOrWhiteSpace(l.AiSuggestedSupplierItemCode));

        // Phase 4: same invoice-classification safety as the PDF path — an invoice
        // received via email/REST must not be silently treated as a deliverable PO.
        var documentType  = NormalizeDocumentType(order.DocumentType);
        var isInvoice     = documentType == "invoice";
        var supplierName  = string.IsNullOrWhiteSpace(order.SupplierName) ? null : order.SupplierName.Trim();
        var paymentTerms  = string.IsNullOrWhiteSpace(order.PaymentTerms) ? null : order.PaymentTerms.Trim();

        var orderId = Guid.NewGuid();
        var now     = DateTime.UtcNow;

        // Provenance tag stored on canonical JSON so the review UI can show
        // where the order came from. Mirrors the buyerName lookup pattern in
        // ListAsync — additional fields are surfaced via the same column.
        var canonicalPayload = new
        {
            source,
            buyerName    = order.BuyerName,
            poNumber     = order.PoNumber,
            orderDate    = order.OrderDate,
            currency     = order.Currency,
            supplierName,
            paymentTerms,
            documentType,
            subTotal     = order.SubTotal,
            taxTotal     = order.TaxTotal,
            grandTotal   = order.GrandTotal,
        };
        var canonicalJson = JsonDocument.Parse(JsonSerializer.Serialize(canonicalPayload));

        // V1: pin the supplier's active connection revision (null = fall back to live config).
        var connectionRevisionId = await ResolveConnectionRevisionAsync(organisationId, supplierId, ct);

        var entity = new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = organisationId,
            SupplierId    = supplierId,
            Supplier      = supplier,
            ConnectionRevisionId = connectionRevisionId,
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
            Status        = (anyUnresolved || isInvoice) ? "pending_review" : "ready",
            SourceFileKey = null,
            CanonicalJson = canonicalJson,
            CreatedAt     = now,
            UpdatedAt     = now,
            Lines         = lineEntities,
            // Phase 4 enrichment.
            SupplierName  = supplierName,
            SubTotal      = order.SubTotal,
            TaxTotal      = order.TaxTotal,
            GrandTotal    = order.GrandTotal,
            PaymentTerms  = paymentTerms,
            DocumentType  = documentType,
        };

        _db.PurchaseOrders.Add(entity);
        _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "Created", new
        {
            source,
            lineCount       = lineEntities.Count,
            unresolvedCount = lineEntities.Count(l => l.NeedsReview),
            aiSuggestionCount,
            documentType,
            classifiedAsInvoice = isInvoice,
            connectionRevisionId,
        }));
        if (isInvoice)
        {
            _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "ClassifiedAsInvoice", new
            {
                note = "This document looks like an invoice, not a purchase order — flagged for review before delivery.",
                grandTotal = order.GrandTotal,
            }));
        }

        await _db.SaveChangesAsync(ct);

        await _shared.EmitPassportEventAsync(organisationId, orderId, "Upload", "Created",
            payload: new { source }, ct: ct);

        // ── Wave 4: fire order.created trigger ───────────────────────────────────
        // Awaited (not fire-and-forget) — see CreateStubAsync for the shared-DbContext rationale.
        await _integrationTrigger.EnqueueAsync(
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
                    _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "ParseFailed",
                        new { error = ParseFailureExplain.ForUnsupportedFormat(extension), stage = "parse", detail = ex.Message }));
                    await _db.SaveChangesAsync(ct);
                    await _shared.EmitPassportEventAsync(organisationId, orderId, "Parse", "Failed",
                        payload: new { error = ParseFailureExplain.ForUnsupportedFormat(extension) }, ct: ct);
                    await _shared.SafeReconcileExceptionsAsync(organisationId, orderId, ct);
                    return Result<ParsedFileOutput>.Fail(ex.Message);
                }
            }

            buffer.Position = 0;
            ParsedOrder parsedOrder;
            IReadOnlyCollection<int> structuredReviewLineNumbers = Array.Empty<int>();
            try
            {
                if (extension == ".pdf")
                {
                    // Primary PDF path: text → LLM structured extraction, with a
                    // deterministic-parser fallback. See ParsePdfAsync.
                    (parsedOrder, structuredReviewLineNumbers) =
                        await ParsePdfAsync(buffer.ToArray(), organisationId, orderId, ct);
                }
                else if (poMapping is not null && extension == ".csv")
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
                _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "ParseFailed",
                    new { error = ParseFailureExplain.ForException(extension, ex), stage = "parse", detail = ex.Message }));
                await _db.SaveChangesAsync(ct);
                await _shared.EmitPassportEventAsync(organisationId, orderId, "Parse", "Failed",
                    payload: new { error = ex.Message }, ct: ct);
                await _shared.SafeReconcileExceptionsAsync(organisationId, orderId, ct);
                return Result<ParsedFileOutput>.Fail($"Could not parse file: {ex.Message}");
            }

            if (parsedOrder.Lines.Count == 0)
            {
                await SetOrderFailedAsync(orderId, organisationId, ct);
                _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "ParseFailed",
                    new { error = ParseFailureExplain.ForEmptyLines(extension), stage = "parse", detail = "0 lines parsed" }));
                await _db.SaveChangesAsync(ct);
                await _shared.EmitPassportEventAsync(organisationId, orderId, "Parse", "Failed",
                    payload: new { error = "0 lines parsed" }, ct: ct);
                await _shared.SafeReconcileExceptionsAsync(organisationId, orderId, ct);
                return Result<ParsedFileOutput>.Fail("File contains no line items.");
            }

            // Auto-resolve lines against item_mappings (batched), then one AI call for leftovers
            var supplierName = entity.Supplier?.Name
                               ?? await GetSupplierNameAsync(organisationId, entity.SupplierId, ct);
            var aiCandidates = await GetAiMappingCandidatesAsync(organisationId, entity.SupplierId, ct);
            var lineEntities = await BuildLineEntitiesAsync(
                organisationId, entity.SupplierId, supplierName, parsedOrder.Lines, aiCandidates, ct);

            // Overlay structured-extraction review flags so a numerically-suspect
            // line surfaces in /operations/exceptions rather than being delivered blind.
            OrderServiceShared.ApplyExtractionReviewFlags(lineEntities, structuredReviewLineNumbers);

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
            // Phase 4: an LLM doc-type of "invoice" forces review — an invoice arrived on
            // the PO path (there is no invoice routing here) and must not be silently
            // transformed and delivered as a purchase order.
            // Normalise the doc-type here too (defense-in-depth): the LLM extractor
            // already normalises, but a future IStructuredOrderExtractor must not be
            // able to bypass the invoice safety force with an odd-cased value.
            var newDocumentType = NormalizeDocumentType(parsedOrder.DocumentType);
            var isInvoice = newDocumentType == "invoice";
            var newStatus    = (anyUnresolved || isInvoice) ? "pending_review" : "ready";
            // Denormalise buyer name for SQL search (avoid JSON parse at query time).
            var newBuyerName = string.IsNullOrWhiteSpace(parsedOrder.BuyerName)
                                ? null
                                : parsedOrder.BuyerName.Trim();
            // Phase 4 enrichment header fields (nullable; only the LLM PDF path populates these).
            var newSupplierName = string.IsNullOrWhiteSpace(parsedOrder.SupplierName) ? null : parsedOrder.SupplierName.Trim();
            var newPaymentTerms = string.IsNullOrWhiteSpace(parsedOrder.PaymentTerms) ? null : parsedOrder.PaymentTerms.Trim();
            var newSubTotal   = parsedOrder.SubTotal;
            var newTaxTotal   = parsedOrder.TaxTotal;
            var newGrandTotal = parsedOrder.GrandTotal;

            var updated = await _db.PurchaseOrders
                .Where(o => o.Id == orderId && o.OrgId == organisationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.PoNumber,     newPoNumber)
                    .SetProperty(o => o.OrderDate,    newOrderDate)
                    .SetProperty(o => o.Currency,     newCurrency)
                    .SetProperty(o => o.Status,       newStatus)
                    .SetProperty(o => o.BuyerName,    newBuyerName)
                    .SetProperty(o => o.SupplierName, newSupplierName)
                    .SetProperty(o => o.SubTotal,     newSubTotal)
                    .SetProperty(o => o.TaxTotal,     newTaxTotal)
                    .SetProperty(o => o.GrandTotal,   newGrandTotal)
                    .SetProperty(o => o.PaymentTerms, newPaymentTerms)
                    .SetProperty(o => o.DocumentType, newDocumentType)
                    .SetProperty(o => o.UpdatedAt,    now), ct);

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
            _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "Parsed", new
            {
                lineCount       = lineEntities.Count,
                unresolvedCount = lineEntities.Count(l => l.NeedsReview),
                aiSuggestionCount,
                newStatus,
                documentType    = newDocumentType,
                classifiedAsInvoice = isInvoice,
            }));
            if (isInvoice)
            {
                _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "ClassifiedAsInvoice", new
                {
                    note = "This document looks like an invoice, not a purchase order — flagged for review before delivery.",
                    grandTotal = newGrandTotal,
                }));
            }

            await _db.SaveChangesAsync(ct);

            await _shared.EmitPassportEventAsync(organisationId, orderId, "Parse", "Succeeded",
                payload: new { lineCount = lineEntities.Count }, ct: ct);

            // Reflect changes on the in-memory entity so callers see the final state.
            entity.PoNumber  = newPoNumber;
            entity.OrderDate = newOrderDate;
            entity.Currency  = newCurrency;
            entity.Status    = newStatus;
            entity.BuyerName = newBuyerName;
            entity.SupplierName = newSupplierName;
            entity.SubTotal     = newSubTotal;
            entity.TaxTotal     = newTaxTotal;
            entity.GrandTotal   = newGrandTotal;
            entity.PaymentTerms = newPaymentTerms;
            entity.DocumentType = newDocumentType;
            entity.UpdatedAt = now;
            entity.Lines = lineEntities;

            _logger.LogInformation(
                "Order {OrderId} parsed: {LineCount} lines, {Unresolved} unresolved, status={Status}",
                orderId, lineEntities.Count, lineEntities.Count(l => l.NeedsReview), entity.Status);
        }

        await _shared.SafeReconcileExceptionsAsync(organisationId, orderId, ct);

        return Result<ParsedFileOutput>.Ok(
            new ParsedFileOutput(entity, detected?.ColumnHeaders, detected?.Format ?? "unknown"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Routes a PDF to the LLM structured extractor when one is available and it
    /// returns a confident order with lines; otherwise falls back to the
    /// deterministic <c>PdfOrderParser</c>. Returns the parsed order plus the set
    /// of line numbers the extractor flagged for review (empty for the fallback).
    /// </summary>
    internal async Task<(ParsedOrder parsed, IReadOnlyCollection<int> reviewLineNumbers)> ParsePdfAsync(
        byte[] bytes, Guid organisationId, Guid orderId, CancellationToken ct)
    {
        // No-egress orgs: never send PDF data to OpenAI. Use the deterministic parser,
        // whose OCR fallback (the self-hosted RapidOcrNet engine, when enabled) handles
        // scanned pages. Routed purely on the org flag — even if the engine isn't
        // deployed, a no-egress org's data still never leaves (scanned just fails safe).
        var selfHostedOcr = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.Id == organisationId)
            .Select(o => o.SelfHostedOcr)
            .FirstOrDefaultAsync(ct);

        if (selfHostedOcr)
        {
            _logger.LogInformation(
                "Order {OrderId}: org {OrgId} is no-egress — parsing PDF deterministically (self-hosted OCR for scanned pages).",
                orderId, organisationId);
            var noEgressParser = _parserFactory.GetParser(".pdf");
            using var noEgressStream = new MemoryStream(bytes);
            return (await noEgressParser.ParseAsync(noEgressStream, ct), Array.Empty<int>());
        }

        if (_structuredExtractor is { IsAvailable: true })
        {
            StructuredExtractionResult extraction;
            using (var pdfBuffer = new MemoryStream(bytes))
                extraction = await _structuredExtractor.ExtractAsync(
                    pdfBuffer, "application/pdf", organisationId, ct);

            if (extraction is { Success: true, Order: { Lines.Count: > 0 } extractedOrder })
            {
                _logger.LogInformation(
                    "Order {OrderId}: PDF parsed via structured extractor — {Lines} lines, {Review} flagged for review.",
                    orderId, extractedOrder.Lines.Count, extraction.ReviewLineNumbers.Count);
                return (MapExtractedToParsed(extractedOrder), extraction.ReviewLineNumbers);
            }

            _logger.LogInformation(
                "Order {OrderId}: structured PDF extraction unavailable/failed ({Reason}); falling back to deterministic parser.",
                orderId, extraction.FailureReason ?? "unknown");
        }

        var parser = _parserFactory.GetParser(".pdf");
        using var stream = new MemoryStream(bytes);
        var parsed = await parser.ParseAsync(stream, ct);
        return (parsed, Array.Empty<int>());
    }

    /// <summary>
    /// Projects a canonical <see cref="ExtractedOrder"/> (from the LLM PDF extractor,
    /// which lives in Core and cannot reference Transform) onto the Transform-layer
    /// <see cref="ParsedOrder"/> so the rest of the parse pipeline is unchanged.
    /// SupplierItemCode is intentionally never carried across — it is resolved
    /// downstream in <see cref="BuildLineEntitiesAsync"/>.
    /// </summary>
    /// <summary>
    /// Normalises a document-type to "purchase_order" | "invoice" | "other" (null if
    /// absent), regardless of which extractor produced it — so the invoice safety
    /// classification and the persisted value are canonical across every ingress path.
    /// Mirrors <c>OpenAiPdfOrderExtractor.NormalizeDocumentType</c>.
    /// </summary>
    internal static string? NormalizeDocumentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "invoice" => "invoice",
            "purchase_order" or "purchase order" or "po" or "order" => "purchase_order",
            _ => "other",
        };
    }

    private static ParsedOrder MapExtractedToParsed(ExtractedOrder o) =>
        new(
            o.PoNumber,
            o.OrderDate,
            o.BuyerName,
            o.Currency,
            o.Lines.Select(l => new ParsedOrderLine(
                l.LineNumber,
                l.BuyerItemCode,
                l.Description,
                l.Quantity,
                l.Unit,
                l.UnitPrice,
                LineAmount: l.LineAmount,
                TaxRate: l.TaxRate,
                DeliveryDate: l.DeliveryDate)).ToList(),
            SupplierName: o.SupplierName,
            SubTotal: o.SubTotal,
            TaxTotal: o.TaxTotal,
            GrandTotal: o.GrandTotal,
            PaymentTerms: o.PaymentTerms,
            DocumentType: o.DocumentType);

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

        // No-egress orgs: never send line data (buyer codes/descriptions) to OpenAI for
        // SKU mapping. This is the single chokepoint for AI suggestions across EVERY
        // ingress path (PDF/CSV/XLSX/email/REST), so gating it here keeps the no-egress
        // guarantee whole. Unresolved lines simply go to human review (the safe default).
        var noEgress = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.Id == organisationId)
            .Select(o => o.SelfHostedOcr)
            .FirstOrDefaultAsync(ct);

        if (unresolvedContexts.Count > 0 && !noEgress)
        {
            // Catalog grounding (Supplier Catalog P2): when the supplier has a product
            // catalog, retrieve the closest REAL products for the unresolved lines and pass
            // them as catalog candidates. The AI service then constrains the model to those
            // real codes and rejects any code outside them — a hallucinated code can never
            // surface. With no catalog this returns the original mapping candidates unchanged
            // (offer ⇔ works — today's free suggestion).
            var groundedCandidates = await BuildCatalogGroundedCandidatesAsync(
                organisationId, supplierId, unresolvedContexts, aiCandidates, ct);

            suggestions = await _aiMappings.SuggestSupplierItemCodesAsync(
                organisationId,
                supplierId,
                supplierName,
                unresolvedContexts,
                groundedCandidates,
                ct);
        }

        // Materialise entities from the in-memory dictionaries — no further I/O.
        var entities = new List<PurchaseOrderLineEntity>(lines.Count);
        foreach (var line in lines)
        {
            var supplierCode = ResolveFromMap(resolvedMap, line.BuyerItemCode);
            bool resolved = !string.IsNullOrWhiteSpace(supplierCode);

            // The parser flags a line whose quantity / unit price was ambiguous or
            // unparseable (e.g. scientific notation) so a silently-wrong number never
            // reaches a supplier. Force review even when the supplier code resolved, and
            // cap confidence so it surfaces — mirrors ApplyExtractionReviewFlags for PDFs.
            bool parserFlagged = line.NeedsReview;

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
                Confidence       = resolved ? (parserFlagged ? 0.5f : 1.0f) : 0.0f,
                NeedsReview      = !resolved || parserFlagged,
                AiSuggestedSupplierItemCode = suggestion?.SupplierItemCode,
                AiSuggestionConfidence = suggestion?.Confidence,
                AiSuggestionReason = suggestion?.Reason,
                AiSuggestionProvenance = suggestion?.Provenance,
                // Phase 4 enrichment (carried from the parsed line; null for parsers that don't emit it).
                LineAmount = line.LineAmount,
                TaxRate = line.TaxRate,
                DeliveryDate = line.DeliveryDate
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
        // Project to an intermediate first: AiMappingCandidate now has optional ctor params,
        // and an expression tree (EF Select) may not omit optional arguments (CS0854).
        var rows = await _db.ItemMappings
            .AsNoTracking()
            .Where(m => m.OrgId == organisationId && m.SupplierId == supplierId)
            .OrderByDescending(m => m.UpdatedAt)
            .Take(40)
            .Select(m => new { m.BuyerItemCode, m.SupplierItemCode })
            .ToListAsync(ct);

        return rows
            .Select(m => new AiMappingCandidate(
                m.BuyerItemCode,
                m.SupplierItemCode,
                $"existing mapping {m.BuyerItemCode} -> {m.SupplierItemCode}"))
            .ToList();
    }

    // ── Catalog grounding (Supplier Catalog P2) ───────────────────────────────────
    // The supplier's product catalog (supplier_products) is the authoritative set of REAL
    // codes the AI may suggest. We retrieve, per unresolved line, the most lexically-similar
    // catalog rows (dependency-free: ToLower().Contains + token overlap over Code/Name/Barcode
    // — NO pg_trgm, no new extension, no migration) and fold them into the AI candidate set as
    // catalog candidates. The AI service then constrains the model to those real codes and
    // rejects any code outside them. When the supplier has NO catalog, the original mapping
    // candidates are returned unchanged — behaviour is byte-for-byte today's (offer ⇔ works).

    /// <summary>Hard cap on catalog rows loaded for in-memory retrieval, to keep the read bounded.</summary>
    private const int CatalogRetrievalPoolCap = 2000;

    /// <summary>Top-K most-similar catalog rows considered per unresolved line.</summary>
    private const int CatalogCandidatesPerLine = 20;

    /// <summary>Overall cap on the candidate set sent to the model (mirrors the AI service's own Take(40)).</summary>
    private const int MaxCandidates = 40;

    private async Task<IReadOnlyList<AiMappingCandidate>> BuildCatalogGroundedCandidatesAsync(
        Guid organisationId,
        Guid supplierId,
        IReadOnlyList<AiMappingLineContext> unresolvedContexts,
        IReadOnlyList<AiMappingCandidate> mappingCandidates,
        CancellationToken ct)
    {
        // V10 — INDEXED retrieval for large catalogs. Baltic IT distributors carry tens of
        // thousands of SKUs; loading them all and scoring in-memory silently truncated at
        // CatalogRetrievalPoolCap. When the active catalog EXCEEDS that threshold (and the
        // provider is Postgres), retrieve the closest products with indexed exact + trigram
        // queries instead of loading the whole catalog. Below the threshold (today's customers),
        // OR on a non-Postgres provider / a translation failure, keep the byte-identical
        // in-memory lexical path — so existing behaviour never changes.
        if (await _catalogRetrieval.ShouldUseIndexedRetrievalAsync(
                organisationId, supplierId, CatalogRetrievalPoolCap, ct))
        {
            var queries = unresolvedContexts
                .Select(l => new CatalogRetrievalQuery(l.LineNumber, l.BuyerItemCode, l.Description))
                .ToList();

            var retrieved = await _catalogRetrieval.RetrieveCandidatesAsync(
                organisationId, supplierId, queries, CatalogCandidatesPerLine, MaxCandidates, ct);

            // null ⇒ indexed path unavailable (provider can't translate); fall through to in-memory.
            if (retrieved is not null)
            {
                // A large catalog with zero retrieved candidates still means "has catalog" — the
                // grounding intent (constrain the model to real codes) holds, so do NOT silently
                // fall back to free suggestion. Return only what was retrieved (+ supporting maps).
                var groundedIndexed = BuildGroundedFromProducts(retrieved, mappingCandidates);
                return groundedIndexed;
            }
        }

        // ── In-memory path (small catalogs, non-Postgres, or indexed fallback) ────────────────
        // One org+supplier-scoped read of the active catalog. Never cross-tenant.
        var catalog = await _db.SupplierProducts
            .AsNoTracking()
            .Where(p => p.OrgId == organisationId && p.SupplierId == supplierId && p.IsActive)
            .OrderBy(p => p.Code)
            .Take(CatalogRetrievalPoolCap)
            .Select(p => new SupplierProduct
            {
                Code = p.Code, Name = p.Name, Unit = p.Unit, Price = p.Price, Barcode = p.Barcode,
            })
            .ToListAsync(ct);

        // No catalog → unchanged behaviour: the original mapping candidates, free suggestion.
        if (catalog.Count == 0)
            return mappingCandidates;

        // Per-line lexical retrieval → deduped union of the closest real products (catalog-first).
        var union = new List<SupplierProduct>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in unresolvedContexts)
        {
            foreach (var product in RetrieveCatalogMatches(line, catalog, CatalogCandidatesPerLine))
            {
                var code = product.Code.Trim();
                if (string.IsNullOrEmpty(code) || !seen.Add(code)) continue;
                union.Add(product);
            }
        }

        return BuildGroundedFromProducts(union, mappingCandidates);
    }

    /// <summary>
    /// Folds a ranked, deduped set of real catalog products into the AI candidate set: catalog
    /// candidates first (ground truth), then past mappings as supporting evidence, capped to
    /// <see cref="MaxCandidates"/> so the AI service's own Take(40) never truncates the catalog.
    /// Shared by both the indexed (V10) and in-memory retrieval paths so the produced candidate
    /// shape / provenance copy is identical.
    /// </summary>
    private static IReadOnlyList<AiMappingCandidate> BuildGroundedFromProducts(
        IReadOnlyList<SupplierProduct> products,
        IReadOnlyList<AiMappingCandidate> mappingCandidates)
    {
        var grounded = new List<AiMappingCandidate>(MaxCandidates);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var product in products)
        {
            if (grounded.Count >= MaxCandidates) break;
            var code = (product.Code ?? string.Empty).Trim();
            if (code.Length == 0 || !seen.Add(code)) continue;

            grounded.Add(new AiMappingCandidate(
                BuyerItemCode: string.Empty,
                SupplierItemCode: code,
                Provenance: string.IsNullOrWhiteSpace(product.Name)
                    ? $"catalog product {code}"
                    : $"catalog product {code} — {product.Name!.Trim()}",
                IsCatalogProduct: true,
                Name: product.Name,
                Unit: product.Unit,
                Price: product.Price,
                Barcode: product.Barcode));
        }

        foreach (var m in mappingCandidates)
        {
            if (grounded.Count >= MaxCandidates) break;
            grounded.Add(m);
        }
        return grounded;
    }

    /// <summary>
    /// Scores catalog rows against one unresolved line by simple, dependency-free lexical
    /// signals over the line's buyer code + description vs each product's Code + Name + Barcode:
    /// exact code/barcode equality scores highest, then substring containment, then shared
    /// token overlap. Returns the top <paramref name="take"/> positively-scored rows. Pure +
    /// in-memory (no DB, no pg_trgm) so it is deterministic and unit-testable.
    /// </summary>
    private static IEnumerable<SupplierProduct> RetrieveCatalogMatches(
        AiMappingLineContext line, IReadOnlyList<SupplierProduct> catalog, int take)
    {
        var buyerCode = (line.BuyerItemCode ?? string.Empty).Trim();
        var buyerCodeLower = buyerCode.ToLowerInvariant();
        var queryText = $"{buyerCode} {line.Description}".Trim();
        var queryTokens = Tokenize(queryText);

        var scored = new List<(SupplierProduct Product, int Score)>(catalog.Count);
        foreach (var p in catalog)
        {
            var code = (p.Code ?? string.Empty).Trim();
            var codeLower = code.ToLowerInvariant();
            var barcode = (p.Barcode ?? string.Empty).Trim();
            var name = p.Name ?? string.Empty;

            var score = 0;

            // Strongest signals: exact code or exact barcode match against the buyer code.
            if (buyerCodeLower.Length > 0 && codeLower == buyerCodeLower) score += 100;
            if (buyerCodeLower.Length > 0 && barcode.Length > 0
                && string.Equals(barcode, buyerCode, StringComparison.OrdinalIgnoreCase)) score += 100;

            // Substring containment in either direction (code embedded in the line, or vice versa).
            if (buyerCodeLower.Length >= 3 && codeLower.Length >= 3
                && (codeLower.Contains(buyerCodeLower) || buyerCodeLower.Contains(codeLower))) score += 25;

            // Token overlap between the line text and the product code+name.
            var productTokens = Tokenize($"{code} {name}");
            if (queryTokens.Count > 0 && productTokens.Count > 0)
            {
                var overlap = queryTokens.Count(t => productTokens.Contains(t));
                score += overlap * 5;
            }

            if (score > 0) scored.Add((p, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Product.Code, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(s => s.Product);
    }

    private static readonly char[] TokenSeparators =
        { ' ', '\t', '\r', '\n', ',', ';', '.', '/', '\\', '-', '_', '(', ')', '[', ']', '{', '}', ':', '"', '\'', '#', '*', '+', '&' };

    /// <summary>Lowercase, punctuation-split token set; drops tokens shorter than 2 chars as noise.</summary>
    private static HashSet<string> Tokenize(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return set;
        foreach (var raw in text.ToLowerInvariant().Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length >= 2) set.Add(raw);
        }
        return set;
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
}
