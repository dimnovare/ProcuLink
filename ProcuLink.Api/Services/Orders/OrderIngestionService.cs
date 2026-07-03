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
    private readonly ProcuLink.Transform.Tokenizing.ISourceTokenizer _tokenizer;
    private readonly IStructuredOrderExtractor? _structuredExtractor;
    private readonly OrderServiceShared         _shared;
    private readonly IConnectionResolver        _connectionResolver;
    private readonly ICatalogRetrievalService   _catalogRetrieval;
    private readonly IEffectiveConnectionConfigResolver? _effectiveConfig;
    // T4 — optional external web/product-code grounding for residual unresolved lines, plus the
    // per-org AI usage cap used to pre-flight-gate it. Both null on the older positional test
    // ctors / hosts that don't register them → web grounding is simply off (today's behaviour).
    private readonly IProductCodeSearch?         _productCodeSearch;
    private readonly IAiUsageTracker?            _aiUsage;

    /// <summary>
    /// Minimum confidence for an AI / fuzzy-catalog supplier-code suggestion to be surfaced on a
    /// line. Below this floor the suggestion is dropped (AiSuggested* left null) so the reviewer
    /// sees "no confident match — enter manually" rather than an unrelated catalog code. Does NOT
    /// apply to the source-manufacturer-part-number suggestion, which is a real code stated in the
    /// document and is emitted at 0.95.
    /// </summary>
    private const float AiSuggestionConfidenceFloor = 0.65f;

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
        ProcuLink.Transform.Tokenizing.ISourceTokenizer tokenizer,
        IStructuredOrderExtractor? structuredExtractor,
        OrderServiceShared         shared,
        ICatalogRetrievalService?  catalogRetrieval = null,
        IEffectiveConnectionConfigResolver? effectiveConfig = null,
        IProductCodeSearch?        productCodeSearch = null,
        IAiUsageTracker?           aiUsage = null)
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
        _tokenizer           = tokenizer;
        _structuredExtractor = structuredExtractor;
        _shared              = shared;
        _connectionResolver  = new ConnectionResolver(db);
        // V10 — indexed catalog retrieval for large catalogs. Self-constructed from the same
        // DbContext (mirrors _connectionResolver); the optional param lets tests inject a fake.
        _catalogRetrieval    = catalogRetrieval ?? new CatalogRetrievalService(db);
        // Launch batch 7 — revision authority. Null (older positional test ctors / hosts that
        // don't register it) behaves exactly like flag-OFF: the live tables drive everything.
        _effectiveConfig     = effectiveConfig;
        _productCodeSearch   = productCodeSearch;
        _aiUsage             = aiUsage;
    }

    /// <summary>
    /// Launch batch 7 — the effective config bundle for one order: the pinned revision snapshot
    /// when the <c>Connections:RevisionAuthority</c> flag is ON and the pin resolves, else the
    /// live-tables bundle (byte-identical pre-batch-7 behaviour). Never throws.
    /// </summary>
    private async Task<EffectiveConnectionConfig> ResolveEffectiveConfigAsync(
        Guid orgId, Guid? connectionRevisionId, CancellationToken ct)
        => _effectiveConfig is null
            ? EffectiveConnectionConfig.Live
            : await _effectiveConfig.ResolveAsync(orgId, connectionRevisionId, ct);

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

        // Workshop P0 — lossless source capture: tokenise the bytes already in hand so the
        // Order Workshop's "received fields" pane can show EVERY source field, not just the ones the
        // canonical model promoted. Inline nav (change-tracker path) so the single SaveChanges below
        // persists it. Best-effort: tokeniser failure / unsupported format yields a null capture and
        // never fails the ingest (PDF and other no-token formats fall through to no capture here —
        // the structured-extraction ingest path supplies the raw_fields bag for those).
        var sourceCapture = await BuildSourceCaptureFromBytesAsync(
            buffer.ToArray(), extension, organisationId, now, ct);

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
            Lines        = lineEntities,
            SourceCapture = sourceCapture
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
        Guid? supplierId,
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
        // DeletedAt == null: never route a NEW order to a soft-deleted supplier
        // (it has disappeared from every list/picker; importing against it would
        // silently revive a destination the operator removed).
        // Phase 1 routing: supplierId may be null — the file arrived on a content-routed channel
        // with no known supplier. Validate only when a supplier was supplied; a null supplier
        // parks the order 'unrouted' (the parse job sets that status) until one is assigned.
        Supplier? supplier = null;
        if (supplierId is { } sid)
        {
            supplier = await _db.Suppliers
                .FirstOrDefaultAsync(s => s.Id == sid && s.OrgId == organisationId && s.DeletedAt == null, ct);
            if (supplier is null)
                return Result<PurchaseOrderEntity>.Fail("Supplier not found.");
        }

        // Create stub order — no lines yet, status = "parsing"
        var now = DateTime.UtcNow;

        // V1: pin the supplier's active connection revision now, so the async parse job inherits it.
        // No supplier => no revision to pin (assign-supplier pins it when a supplier is chosen).
        var connectionRevisionId = supplierId is { } sidRev
            ? await ResolveConnectionRevisionAsync(organisationId, sidRev, ct)
            : (Guid?)null;

        var entity = new PurchaseOrderEntity
        {
            Id            = orderId,
            OrgId         = organisationId,
            SupplierId    = supplierId,
            Supplier      = supplier!,
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
        // DeletedAt == null: a soft-deleted supplier must not receive new orders
        // through the structured-extraction (PDF/AI) ingest path either.
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId && s.OrgId == organisationId && s.DeletedAt == null, ct);
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
            TaxAmount:     l.TaxAmount,
            DeliveryDate:  l.DeliveryDate,
            // Phase 1 lossless capture — carry the new line fields so BuildLineEntitiesAsync persists them.
            ManufacturerPartNumber: l.ManufacturerPartNumber,
            CustomerPartNumber:     l.CustomerPartNumber,
            DiscountPercent:        l.DiscountPercent,
            Unspsc:                 l.Unspsc,
            Recipient:              l.Recipient,
            ContractNumber:         l.ContractNumber,
            NetAmount:              l.NetAmount
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

        // Denormalise the shipTo / billTo addresses onto the flat cXML address columns (the cXML
        // writer reads the PurchaseOrderEntity directly, not the OrderParty rows). Mirrors the
        // Contact* precedent; the party rows below remain the lossless source of truth.
        var shipParty = PartyOf(order.Parties, "shipTo");
        var billParty = PartyOf(order.Parties, "billTo");

        // Provenance tag stored on canonical JSON so the review UI can show
        // where the order came from. Mirrors the buyerName lookup pattern in
        // ListAsync — additional fields are surfaced via the same column.
        var canonicalPayload = new
        {
            source,
            buyerName              = order.BuyerName,
            poNumber               = order.PoNumber,
            orderDate              = order.OrderDate,
            currency               = order.Currency,
            supplierName,
            paymentTerms,
            documentType,
            subTotal               = order.SubTotal,
            taxTotal               = order.TaxTotal,
            grandTotal             = order.GrandTotal,
            // V5: requested delivery date (null when absent — omitted from JSON by serialiser's default).
            requestedDeliveryDate  = order.RequestedDeliveryDate,
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
            // Buyer tax id (drives the cXML From/Identity; null → configured/legacy From unchanged).
            BuyerTaxId    = string.IsNullOrWhiteSpace(order.BuyerTaxId) ? null : order.BuyerTaxId.Trim(),
            // Same uncaptured-0 normalization as GrandTotal below (SubTotal), plus a negative-tax
            // guard — see NormalizeExtractedSubTotal / NormalizeExtractedTaxTotal.
            SubTotal      = NormalizeExtractedSubTotal(order.SubTotal),
            TaxTotal      = NormalizeExtractedTaxTotal(order.TaxTotal),
            // Normalize an uncaptured/non-positive extracted grand total to NULL so downstream
            // derivation (sum Qty*UnitPrice) takes over — a stored 0 would otherwise be emitted
            // verbatim into delivered supplier documents. See NormalizeExtractedGrandTotal.
            GrandTotal    = NormalizeExtractedGrandTotal(order.GrandTotal),
            PaymentTerms  = paymentTerms,
            DocumentType  = documentType,
            // V5: header-level requested delivery date — real persisted column (requested_delivery_date).
            RequestedDeliveryDate = order.RequestedDeliveryDate,
            // Phase 1 lossless capture — header terms/contact, parties, and the raw bag are nav rows
            // attached inline so a single SaveChanges below persists them (no separate insert).
            ContactName    = order.ContactName,
            ContactEmail   = order.ContactEmail,
            ContactPhone   = order.ContactPhone,
            Incoterms      = order.Incoterms,
            ShippingMethod = order.ShippingMethod,
            BuyerOrderRef  = order.BuyerOrderRef,
            // cXML address blocks denormalised from the shipTo / billTo parties (DeliverTo ← party contact name).
            ShipToName       = shipParty?.Name,
            ShipToDeliverTo  = shipParty?.ContactName,
            ShipToStreet     = shipParty?.Street,
            ShipToCity       = shipParty?.City,
            ShipToPostalCode = shipParty?.PostalCode,
            ShipToCountry    = shipParty?.Country,
            ShipToEmail      = shipParty?.Email,
            ShipToPhone      = shipParty?.Phone,
            BillToName       = billParty?.Name,
            BillToDeliverTo  = billParty?.ContactName,
            BillToStreet     = billParty?.Street,
            BillToCity       = billParty?.City,
            BillToPostalCode = billParty?.PostalCode,
            BillToCountry    = billParty?.Country,
            BillToEmail      = billParty?.Email,
            BillToPhone      = billParty?.Phone,
            Parties        = (order.Parties ?? Array.Empty<ExtractedParty>()).Select(p => new OrderParty
            {
                Id = Guid.NewGuid(), OrgId = organisationId, Role = p.Role,
                Name = p.Name, Street = p.Street, City = p.City, PostalCode = p.PostalCode,
                Country = p.Country, Vat = p.Vat, RegNr = p.RegNr, EdiCode = p.EdiCode,
                Reference = p.Reference, ContactName = p.ContactName, Email = p.Email, Phone = p.Phone,
            }).ToList(),
            SourceCapture  = BuildSourceCapture(order.RawFields, organisationId, "pdf", now),
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

            // ── Launch batch 7 — revision authority ───────────────────────────
            // Resolve the pinned revision's effective bundle ONCE for this parse. The pin was
            // stamped at ingest (CreateStubAsync) and persists across Hangfire retries, so a
            // re-run of this job sees the SAME revision (idempotent + reproducible). Live
            // bundle when the flag is off / order unpinned / pin orphaned.
            var effective = await ResolveEffectiveConfigAsync(organisationId, entity.ConnectionRevisionId, ct);

            // Parse mapping: a usable revision snapshot governs; a null/empty/malformed
            // snapshot falls back to the LIVE supplier PO mapping (parse must never brick).
            var poMapping = ResolveSnapshotPoMapping(effective, orderId)
                            ?? await _poMappingService.GetAsync(organisationId, entity.SupplierId ?? Guid.Empty, ct);

            if (IsEmptyTemplate(poMapping))
                poMapping = null;

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
            IReadOnlyDictionary<int, string>? structuredReviewReasons = null;
            string? pdfExtractionFailureReason = null;
            try
            {
                if (extension == ".pdf")
                {
                    // Primary PDF path: text → LLM structured extraction, with a
                    // deterministic-parser fallback. See ParsePdfAsync.
                    (parsedOrder, structuredReviewLineNumbers, structuredReviewReasons, pdfExtractionFailureReason) =
                        await ParsePdfAsync(buffer.ToArray(), organisationId, orderId, ct);
                }
                else if (extension == ".xlsx")
                {
                    // Primary XLSX path: render the workbook to faithful text → SAME LLM
                    // structured extraction the PDF path uses, with the deterministic
                    // XlsxOrderParser as the fallback. Real-world labelled/sectioned PO
                    // workbooks (PurchaseOrderNr/Currency + a Lines section) defeat the
                    // row1=headers deterministic parser, so the LLM is preferred when
                    // available. See ParseXlsxAsync.
                    (parsedOrder, structuredReviewLineNumbers, structuredReviewReasons, pdfExtractionFailureReason) =
                        await ParseXlsxAsync(buffer.ToArray(), organisationId, orderId, ct);
                }
                else if (poMapping is not null && extension == ".csv")
                {
                    parsedOrder = await ParseWithMappingTemplateAsync(buffer.ToArray(), poMapping, ct);

                    // Self-heal a FORMAT-MISMATCHED template. If the supplier's PO-mapping
                    // template maps source fields the uploaded CSV simply doesn't have (e.g. a
                    // cXML/XPath template applied to a flat CSV), every field resolves blank and
                    // the order would land with phantom empty lines that silently slip past the
                    // 0-lines guard below — worse than no template at all. Fall back to the
                    // deterministic alias CSV parser, which recovers the real data (and still
                    // fails loudly via the 0-lines guard if the file genuinely has no rows).
                    if (IsDegenerateParse(parsedOrder))
                    {
                        _logger.LogWarning(
                            "Order {OrderId}: PO-mapping template produced an all-empty parse for a CSV — " +
                            "template fields do not match the file. Falling back to the default CSV parser.",
                            orderId);
                        buffer.Position = 0;
                        var fallbackParser = _parserFactory.GetParser(extension, buffer);
                        buffer.Position = 0;
                        parsedOrder = await fallbackParser.ParseAsync(buffer, ct);
                    }
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
                // When the per-org AI usage cap blocked the LLM extractor, the regex
                // fallback yielding 0 lines is a consequence of the cap — say so honestly
                // instead of the misleading "scanned or image-only" PDF copy.
                var aiCapReached = extension == ".pdf"
                    && pdfExtractionFailureReason == StructuredExtractionResult.UsageCapFailureReason;
                var emptyLinesError = aiCapReached
                    ? ParseFailureExplain.ForAiCapReached()
                    : ParseFailureExplain.ForEmptyLines(extension);
                _db.AuditEvents.Add(OrderServiceShared.BuildAuditEvent(organisationId, orderId, "ParseFailed",
                    new { error = emptyLinesError, stage = "parse", detail = "0 lines parsed" }));
                await _db.SaveChangesAsync(ct);
                await _shared.EmitPassportEventAsync(organisationId, orderId, "Parse", "Failed",
                    payload: new { error = aiCapReached ? emptyLinesError : "0 lines parsed" }, ct: ct);
                await _shared.SafeReconcileExceptionsAsync(organisationId, orderId, ct);
                return Result<ParsedFileOutput>.Fail(
                    aiCapReached ? emptyLinesError : "File contains no line items.");
            }

            // Auto-resolve lines against item_mappings (batched), then one AI call for leftovers.
            // Revision authority: a pinned revision with a NON-EMPTY item-mapping snapshot is the
            // exact-match dictionary (codes outside it go to review, as an unmapped code does
            // today); an EMPTY snapshot falls back to the live item mappings. AI suggestions stay
            // candidates-from-live + suggestions-only (human review) — they never auto-resolve.
            IReadOnlyList<EffectiveRevisionItemMapping>? itemMappingSnapshot = null;
            if (effective.IsRevision)
            {
                if (effective.ItemMappings.Count > 0)
                {
                    itemMappingSnapshot = effective.ItemMappings;
                    _logger.LogInformation(
                        "Order {OrderId}: resolving item codes from pinned {Source} snapshot ({Count} mappings).",
                        orderId, effective.Source, effective.ItemMappings.Count);
                }
                else
                {
                    _logger.LogInformation(
                        "Order {OrderId}: pinned {Source} has no item-mapping snapshot — resolving from live item mappings.",
                        orderId, effective.Source);
                }
            }

            var supplierName = entity.Supplier?.Name
                               ?? await GetSupplierNameAsync(organisationId, entity.SupplierId ?? Guid.Empty, ct);
            var aiCandidates = await GetAiMappingCandidatesAsync(organisationId, entity.SupplierId ?? Guid.Empty, ct);
            var lineEntities = await BuildLineEntitiesAsync(
                organisationId, entity.SupplierId ?? Guid.Empty, supplierName, parsedOrder.Lines, aiCandidates, ct,
                itemMappingSnapshot);

            // Overlay structured-extraction review flags so a numerically-suspect
            // line surfaces in /operations/exceptions rather than being delivered blind.
            OrderServiceShared.ApplyExtractionReviewFlags(
                lineEntities, structuredReviewLineNumbers, structuredReviewReasons);

            bool anyUnresolved    = lineEntities.Any(l => l.NeedsReview);
            var  aiSuggestionCount = lineEntities.Count(l => !string.IsNullOrWhiteSpace(l.AiSuggestedSupplierItemCode));

            // ── Persist parsed results ────────────────────────────────────────
            // Use ExecuteUpdateAsync for the parent row so we bypass EF's change
            // tracker entirely.  This avoids DbUpdateConcurrencyException (0 rows
            // affected) that occurs when the long-running parse leaves the tracked
            // entity stale on Neon serverless / Npgsql 8 connection multiplexing.
            //
            // ── INVARIANT (source-of-truth): typed columns, NOT canonical_json ──
            // The async parse path writes the TYPED COLUMNS (BuyerName, PoNumber,
            // OrderDate, Currency, SupplierName, SubTotal/TaxTotal/GrandTotal,
            // PaymentTerms, DocumentType, RequestedDeliveryDate, …) and DELIBERATELY
            // does NOT write canonical_json. The typed columns are the single source
            // of truth for this order's header. canonical_json is only ever populated
            // by the SYNC ingress paths (CreateStubFromParsedOrderAsync) and is treated
            // strictly as a LEGACY FALLBACK by readers (e.g. OrdersController.ExtractBuyerName,
            // which reads the column first and only consults canonical_json when the
            // column is null). Any NEW reader of header data MUST read the typed column
            // first and use canonical_json only as a null-fallback — never JSON-first —
            // or it will silently disagree with every async-parsed order. A guard test
            // (AsyncParseColumnFirstContractTests) pins this so a JSON-first reader
            // breaks CI. If you ever start writing canonical_json here too, keep the two
            // mutually consistent (see OrderResolutionService, which mirrors header edits
            // into both) and update that test.
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
            // Phase 1 routing: a supplier-less order is PARKED 'unrouted' after extraction. Its
            // header + parties + (unresolved) lines are persisted below so the triage queue shows
            // what arrived, but it cannot reach 'ready' — there is no supplier to resolve item codes
            // against. POST /orders/{id}/assign-supplier sets a supplier and re-enqueues this parse,
            // which then resolves normally. (Overrides the line above only when no supplier is set.)
            if (entity.SupplierId is null)
                newStatus = OrderStatusConstants.Unrouted;
            // Denormalise buyer name for SQL search (avoid JSON parse at query time).
            var newBuyerName = string.IsNullOrWhiteSpace(parsedOrder.BuyerName)
                                ? null
                                : parsedOrder.BuyerName.Trim();
            // Phase 4 enrichment header fields (nullable; only the LLM PDF path populates these).
            var newSupplierName = string.IsNullOrWhiteSpace(parsedOrder.SupplierName) ? null : parsedOrder.SupplierName.Trim();
            var newPaymentTerms = string.IsNullOrWhiteSpace(parsedOrder.PaymentTerms) ? null : parsedOrder.PaymentTerms.Trim();
            // Normalize uncaptured/non-positive SubTotal (mirrors GrandTotal) + scrub negative tax.
            var newSubTotal   = NormalizeExtractedSubTotal(parsedOrder.SubTotal);
            var newTaxTotal   = NormalizeExtractedTaxTotal(parsedOrder.TaxTotal);
            // Normalize an uncaptured/non-positive extracted grand total to NULL (see the sync
            // ingest seam above and NormalizeExtractedGrandTotal). This value flows to BOTH the
            // ExecuteUpdateAsync below and the InMemory entity-setter fallback further down.
            var newGrandTotal = NormalizeExtractedGrandTotal(parsedOrder.GrandTotal);
            // V5: header-level requested delivery date — real persisted column (requested_delivery_date).
            var newRequestedDeliveryDate = parsedOrder.RequestedDeliveryDate;
            // Phase 1 lossless capture header fields (nullable; only the LLM/email path populates these).
            var newContactName    = parsedOrder.ContactName;
            var newContactEmail   = parsedOrder.ContactEmail;
            var newContactPhone   = parsedOrder.ContactPhone;
            var newIncoterms      = parsedOrder.Incoterms;
            var newShippingMethod = parsedOrder.ShippingMethod;
            var newBuyerOrderRef  = parsedOrder.BuyerOrderRef;
            // Buyer tax id (drives the cXML From/Identity; null → configured/legacy From unchanged).
            var newBuyerTaxId     = string.IsNullOrWhiteSpace(parsedOrder.BuyerTaxId) ? null : parsedOrder.BuyerTaxId.Trim();
            // cXML address blocks denormalised from the shipTo / billTo parties (DeliverTo ← party
            // contact name). The cXML writer reads these flat columns directly; the OrderParty rows
            // (written below) remain the lossless source of truth.
            var shipParty = PartyOf(parsedOrder.Parties, "shipTo");
            var billParty = PartyOf(parsedOrder.Parties, "billTo");
            var newShipToName       = shipParty?.Name;
            var newShipToDeliverTo  = shipParty?.ContactName;
            var newShipToStreet     = shipParty?.Street;
            var newShipToCity       = shipParty?.City;
            var newShipToPostalCode = shipParty?.PostalCode;
            var newShipToCountry    = shipParty?.Country;
            var newShipToEmail      = shipParty?.Email;
            var newShipToPhone      = shipParty?.Phone;
            var newBillToName       = billParty?.Name;
            var newBillToDeliverTo  = billParty?.ContactName;
            var newBillToStreet     = billParty?.Street;
            var newBillToCity       = billParty?.City;
            var newBillToPostalCode = billParty?.PostalCode;
            var newBillToCountry    = billParty?.Country;
            var newBillToEmail      = billParty?.Email;
            var newBillToPhone      = billParty?.Phone;

            // ── Atomic persist (all-or-nothing) ───────────────────────────────
            // The status flip below is an ExecuteUpdateAsync — it auto-commits its own SQL the
            // instant it runs, independently of the SaveChangesAsync that writes the child rows
            // (lines, parties, SourceCapture). The two child-row ExecuteDeleteAsync calls
            // (parties + capture, the latter inside UpsertSourceCaptureAsync) auto-commit too.
            // Without a wrapping transaction a crash anywhere between the status flip and the
            // final SaveChanges would leave a "ready"/"pending_review" order with NO
            // lines/parties/capture — and the status != "parsing" re-entry guard at the top of
            // this method would then block any Hangfire retry from backfilling them (a
            // permanently half-written order). One explicit transaction makes the whole block
            // all-or-nothing: ExecuteUpdate/ExecuteDelete enlist in the current transaction on
            // Npgsql, so on failure they roll back WITH the SaveChanges and the order is left
            // exactly "parsing" — cleanly re-parseable. (Npgsql-only, like the bulk ops
            // themselves; EF InMemory can translate none of this and throws here, which the
            // InMemory-tolerant parse tests already swallow.)
            await using var persistTx = await _db.Database.BeginTransactionAsync(ct);

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
                    .SetProperty(o => o.RequestedDeliveryDate, newRequestedDeliveryDate)
                    // Phase 1 lossless capture header columns.
                    .SetProperty(o => o.ContactName,    newContactName)
                    .SetProperty(o => o.ContactEmail,   newContactEmail)
                    .SetProperty(o => o.ContactPhone,   newContactPhone)
                    .SetProperty(o => o.Incoterms,      newIncoterms)
                    .SetProperty(o => o.ShippingMethod, newShippingMethod)
                    .SetProperty(o => o.BuyerOrderRef,  newBuyerOrderRef)
                    .SetProperty(o => o.BuyerTaxId,     newBuyerTaxId)
                    // cXML address blocks (denormalised shipTo / billTo).
                    .SetProperty(o => o.ShipToName,       newShipToName)
                    .SetProperty(o => o.ShipToDeliverTo,  newShipToDeliverTo)
                    .SetProperty(o => o.ShipToStreet,     newShipToStreet)
                    .SetProperty(o => o.ShipToCity,       newShipToCity)
                    .SetProperty(o => o.ShipToPostalCode, newShipToPostalCode)
                    .SetProperty(o => o.ShipToCountry,    newShipToCountry)
                    .SetProperty(o => o.ShipToEmail,      newShipToEmail)
                    .SetProperty(o => o.ShipToPhone,      newShipToPhone)
                    .SetProperty(o => o.BillToName,       newBillToName)
                    .SetProperty(o => o.BillToDeliverTo,  newBillToDeliverTo)
                    .SetProperty(o => o.BillToStreet,     newBillToStreet)
                    .SetProperty(o => o.BillToCity,       newBillToCity)
                    .SetProperty(o => o.BillToPostalCode, newBillToPostalCode)
                    .SetProperty(o => o.BillToCountry,    newBillToCountry)
                    .SetProperty(o => o.BillToEmail,      newBillToEmail)
                    .SetProperty(o => o.BillToPhone,      newBillToPhone)
                    .SetProperty(o => o.UpdatedAt,    now), ct);

            if (updated == 0)
            {
                _logger.LogError(
                    "ParseStoredFileAsync: ExecuteUpdateAsync affected 0 rows for order {OrderId}", orderId);
                return Result<ParsedFileOutput>.Fail("Order could not be updated — not found or already deleted.");
            }

            // Idempotent line persist (delete-then-insert, mirroring the parties replace below):
            // clear any lines from a PRIOR parse of this order before inserting the fresh set.
            // The normal flow parses each order exactly once (the status!="parsing" guard blocks
            // re-entry), but routing's assign-supplier flips an 'unrouted' order back to 'parsing'
            // and re-parses it — without this, the unrouted hold's lines would be DUPLICATED on
            // the resolving re-parse. ExecuteDeleteAsync is Npgsql-only, like the rest of this block.
            await _db.PurchaseOrderLines
                .Where(l => l.OrderId == orderId)
                .ExecuteDeleteAsync(ct);

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

            // ── Phase 1 lossless capture: child rows ──────────────────────────
            // Parties + SourceCapture are child rows (not SetProperty-able). Replace
            // any existing rows for this order first so a Hangfire retry of this job is
            // idempotent (delete-then-insert) rather than duplicating. ExecuteDeleteAsync
            // is Npgsql-only — this whole async path already uses it (ExecuteUpdateAsync
            // above), so it is Postgres-only by design.
            await _db.OrderParties
                .Where(p => p.OrderId == orderId && p.OrgId == organisationId)
                .ExecuteDeleteAsync(ct);
            if (parsedOrder.Parties is { Count: > 0 })
            {
                _db.OrderParties.AddRange(parsedOrder.Parties.Select(p => new OrderParty
                {
                    Id = Guid.NewGuid(), OrderId = orderId, OrgId = organisationId, Role = p.Role,
                    Name = p.Name, Street = p.Street, City = p.City, PostalCode = p.PostalCode,
                    Country = p.Country, Vat = p.Vat, RegNr = p.RegNr, EdiCode = p.EdiCode,
                    Reference = p.Reference, ContactName = p.ContactName, Email = p.Email, Phone = p.Phone,
                }));
            }
            // Raw bag: for structured formats (CSV/XLSX/XML/cXML/EDI/X12) capture the FULL
            // source-token set so unmapped source columns/elements survive into source_captures
            // (UpsertSourceCaptureAsync prefers a non-empty token list over parsedOrder.RawFields).
            // The PDF/email path supplies no tokens here and falls through to raw_fields as before.
            // Tokenisation is best-effort: a tokenizer failure must NOT fail the parse — log and
            // continue, leaving SourceCapture to fall back to raw_fields/null.
            IReadOnlyList<ProcuLink.Transform.Tokenizing.SourceToken>? sourceTokens = null;
            if (extension is ".csv" or ".xlsx" or ".xml" or ".cxml" or ".edi" or ".x12" or ".json")
            {
                try
                {
                    buffer.Position = 0;
                    sourceTokens = await _tokenizer.TokenizeAsync(buffer.ToArray(), extension, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Source tokenization failed for order {OrderId} (non-fatal)", orderId);
                }
            }
            // Format derived from the detected format, falling back to the source file extension.
            var capturedFormat = detected?.Format
                ?? Path.GetExtension(entity.SourceFileKey).TrimStart('.');
            await UpsertSourceCaptureAsync(
                orderId, organisationId, capturedFormat,
                tokens: sourceTokens, parsedOrder, rawText: null, now, ct);

            await _db.SaveChangesAsync(ct);
            // Commit the status flip + lines + parties + SourceCapture as one unit. Everything
            // below (passport emit, in-memory entity reflection) is a post-persist side effect.
            await persistTx.CommitAsync(ct);

            await _shared.EmitPassportEventAsync(organisationId, orderId, "Parse", "Succeeded",
                payload: new { lineCount = lineEntities.Count }, ct: ct);

            // Reflect changes on the in-memory entity so callers see the final state.
            entity.PoNumber  = newPoNumber;
            entity.OrderDate = newOrderDate;
            entity.Currency  = newCurrency;
            entity.Status    = newStatus;
            entity.BuyerName = newBuyerName;
            entity.SupplierName          = newSupplierName;
            entity.SubTotal              = newSubTotal;
            entity.TaxTotal              = newTaxTotal;
            entity.GrandTotal            = newGrandTotal;
            entity.PaymentTerms          = newPaymentTerms;
            entity.DocumentType          = newDocumentType;
            // V5: header-level requested delivery date — persisted via ExecuteUpdateAsync above.
            entity.RequestedDeliveryDate = newRequestedDeliveryDate;
            // Phase 1 lossless capture header columns — reflect so an immediate transform sees them.
            entity.ContactName    = newContactName;
            entity.ContactEmail   = newContactEmail;
            entity.ContactPhone   = newContactPhone;
            entity.Incoterms      = newIncoterms;
            entity.ShippingMethod = newShippingMethod;
            entity.BuyerOrderRef  = newBuyerOrderRef;
            entity.BuyerTaxId     = newBuyerTaxId;
            // cXML address blocks — reflect so an immediate (same-request) cXML transform emits them.
            entity.ShipToName       = newShipToName;
            entity.ShipToDeliverTo  = newShipToDeliverTo;
            entity.ShipToStreet     = newShipToStreet;
            entity.ShipToCity       = newShipToCity;
            entity.ShipToPostalCode = newShipToPostalCode;
            entity.ShipToCountry    = newShipToCountry;
            entity.ShipToEmail      = newShipToEmail;
            entity.ShipToPhone      = newShipToPhone;
            entity.BillToName       = newBillToName;
            entity.BillToDeliverTo  = newBillToDeliverTo;
            entity.BillToStreet     = newBillToStreet;
            entity.BillToCity       = newBillToCity;
            entity.BillToPostalCode = newBillToPostalCode;
            entity.BillToCountry    = newBillToCountry;
            entity.BillToEmail      = newBillToEmail;
            entity.BillToPhone      = newBillToPhone;
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
    /// An EMPTY template (no header rules AND no line rules) can never produce a line with
    /// content — template-parsing under it silently blanks every field of every CSV upload
    /// for that supplier. Treated as "no mapping" so the generic CSV parser (header aliases)
    /// takes over instead.
    /// </summary>
    internal static bool IsEmptyTemplate(PoMappingConfig? poMapping) =>
        poMapping is not null && poMapping.Header.Count == 0 && poMapping.Lines.Count == 0;

    /// <summary>
    /// True when a parse produced rows but NOTHING usable — no PO number and every line
    /// completely blank (no buyer code, no description, zero quantity AND zero/absent unit
    /// price). This is the signature of a PO-mapping template whose source fields don't
    /// exist in the uploaded file — e.g. a cXML/XPath template applied to a flat CSV: every
    /// field resolves to blank and the order lands with phantom empty lines that slip past
    /// the <c>Lines.Count == 0</c> guard. A genuinely empty file yields 0 lines (caught
    /// there); this catches the "lines present, all empty" format-mismatch instead.
    /// </summary>
    internal static bool IsDegenerateParse(ParsedOrder o) =>
        o.Lines.Count > 0
        && string.IsNullOrWhiteSpace(o.PoNumber)
        && o.Lines.All(l =>
            string.IsNullOrWhiteSpace(l.BuyerItemCode)
            && string.IsNullOrWhiteSpace(l.Description)
            && l.Quantity == 0m
            && (l.UnitPrice ?? 0m) == 0m);

    /// <summary>
    /// Routes a PDF to the LLM structured extractor when one is available and it
    /// returns a confident order with lines; otherwise falls back to the
    /// deterministic <c>PdfOrderParser</c>. Returns the parsed order plus the set
    /// of line numbers the extractor flagged for review (empty for the fallback),
    /// the optional per-line "why flagged" reasons (null for the fallback), and —
    /// when the extractor was AVAILABLE but extraction failed — its failure reason,
    /// so the caller can explain an empty fallback result honestly (e.g. the per-org
    /// AI usage cap). Null when the extractor is unavailable or extraction succeeded.
    /// </summary>
    internal async Task<(ParsedOrder parsed,
                         IReadOnlyCollection<int> reviewLineNumbers,
                         IReadOnlyDictionary<int, string>? reviewReasons,
                         string? extractionFailureReason)> ParsePdfAsync(
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
            return (await noEgressParser.ParseAsync(noEgressStream, ct), Array.Empty<int>(), null, null);
        }

        string? extractionFailureReason = null;
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
                return (MapExtractedToParsed(extractedOrder), extraction.ReviewLineNumbers, extraction.ReviewReasons, null);
            }

            extractionFailureReason = extraction.FailureReason;
            _logger.LogInformation(
                "Order {OrderId}: structured PDF extraction unavailable/failed ({Reason}); falling back to deterministic parser.",
                orderId, extraction.FailureReason ?? "unknown");
        }

        var parser = _parserFactory.GetParser(".pdf");
        using var stream = new MemoryStream(bytes);
        var parsed = await parser.ParseAsync(stream, ct);
        return (parsed, Array.Empty<int>(), null, extractionFailureReason);
    }

    /// <summary>
    /// Routes an XLSX to the LLM structured extractor when one is available — by
    /// rendering the workbook to faithful text (<see cref="XlsxTextExtractor"/>) and
    /// running it through the SAME text-source extraction the PDF text path uses —
    /// otherwise falls back to the deterministic <c>XlsxOrderParser</c>. This is the
    /// XLSX analogue of <see cref="ParsePdfAsync"/>: real-world labelled/sectioned PO
    /// workbooks (PurchaseOrderNr / Currency header cells + a Lines section) extract
    /// ZERO line data through the row1=headers deterministic parser, so the LLM is
    /// preferred when available. Returns the parsed order plus the extractor's
    /// review-line flags (empty for the fallback), the per-line reasons (null for the
    /// fallback), and the extractor's failure reason when it was AVAILABLE but failed
    /// (so the caller can explain an empty result honestly, e.g. the AI usage cap).
    /// Never sends a no-egress org's data to OpenAI — same gate as the PDF path.
    /// </summary>
    internal async Task<(ParsedOrder parsed,
                         IReadOnlyCollection<int> reviewLineNumbers,
                         IReadOnlyDictionary<int, string>? reviewReasons,
                         string? extractionFailureReason)> ParseXlsxAsync(
        byte[] bytes, Guid organisationId, Guid orderId, CancellationToken ct)
    {
        // No-egress orgs: never send workbook data to OpenAI — deterministic parser only.
        var selfHostedOcr = await _db.Organisations
            .AsNoTracking()
            .Where(o => o.Id == organisationId)
            .Select(o => o.SelfHostedOcr)
            .FirstOrDefaultAsync(ct);

        if (selfHostedOcr)
        {
            _logger.LogInformation(
                "Order {OrderId}: org {OrgId} is no-egress — parsing XLSX deterministically.",
                orderId, organisationId);
            return (await ParseXlsxDeterministicAsync(bytes, ct), Array.Empty<int>(), null, null);
        }

        string? extractionFailureReason = null;
        if (_structuredExtractor is { IsAvailable: true })
        {
            // Render the workbook to faithful text. A render failure (corrupt/unreadable
            // workbook) is non-fatal here: fall through to the deterministic parser, which
            // surfaces the honest error via the 0-lines / parse-failure path.
            string? sourceText = null;
            try
            {
                using var renderStream = new MemoryStream(bytes);
                sourceText = XlsxTextExtractor.ExtractText(renderStream);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Order {OrderId}: could not render XLSX to text for structured extraction; falling back to deterministic parser.",
                    orderId);
            }

            if (!string.IsNullOrWhiteSpace(sourceText))
            {
                var extraction = await _structuredExtractor.ExtractFromTextAsync(sourceText, organisationId, ct);

                if (extraction is { Success: true, Order: { Lines.Count: > 0 } extractedOrder })
                {
                    _logger.LogInformation(
                        "Order {OrderId}: XLSX parsed via structured extractor — {Lines} lines, {Review} flagged for review.",
                        orderId, extractedOrder.Lines.Count, extraction.ReviewLineNumbers.Count);
                    return (MapExtractedToParsed(extractedOrder), extraction.ReviewLineNumbers, extraction.ReviewReasons, null);
                }

                extractionFailureReason = extraction.FailureReason;
                _logger.LogInformation(
                    "Order {OrderId}: structured XLSX extraction failed ({Reason}); falling back to deterministic parser.",
                    orderId, extraction.FailureReason ?? "unknown");
            }
        }

        return (await ParseXlsxDeterministicAsync(bytes, ct), Array.Empty<int>(), null, extractionFailureReason);
    }

    /// <summary>Deterministic <c>XlsxOrderParser</c> fallback over the in-memory bytes.</summary>
    private async Task<ParsedOrder> ParseXlsxDeterministicAsync(byte[] bytes, CancellationToken ct)
    {
        var parser = _parserFactory.GetParser(".xlsx");
        using var stream = new MemoryStream(bytes);
        return await parser.ParseAsync(stream, ct);
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

    /// <summary>
    /// Normalises an extracted header grand total to NULL when it was not genuinely captured.
    ///
    /// PDF/XLSX extraction can emit <c>0</c> (the LLM's default) — or a bogus non-positive value —
    /// when it cannot read an order total. A persisted <c>0</c> is NOT harmless: downstream
    /// <c>MappedTransformService.DeriveGrandTotal</c> derives <c>sum(Qty*UnitPrice)</c> ONLY when
    /// the stored value is NULL, so a stored <c>0</c> is emitted verbatim into delivered supplier
    /// documents (wrong data) and drove the "€ 0.00" display bug (FE fix cf0ad05). Collapsing any
    /// non-positive extracted total to NULL hands the total to line-sum derivation instead.
    ///
    /// A genuine zero-value order (all lines price to 0) is handled sanely: it becomes NULL here,
    /// then derives back to 0 downstream — same emitted total, no special case. Only the reported
    /// GrandTotal field is normalised; SubTotal has the same latent pattern but is out of scope
    /// for this fix (TaxTotal is unaffected — it derives to 0 whether stored 0 or NULL).
    /// </summary>
    internal static decimal? NormalizeExtractedGrandTotal(decimal? extracted) =>
        extracted is > 0m ? extracted : null;

    /// <summary>
    /// SubTotal companion to <see cref="NormalizeExtractedGrandTotal"/> — same latent bug the
    /// GrandTotal fix (#19) deferred. An uncaptured/non-positive extracted SubTotal is collapsed to
    /// NULL so <c>MappedTransformService.DeriveSubTotal</c> takes over (sum Qty*UnitPrice); a stored
    /// 0 would otherwise be delivered verbatim as the SubTotal header field. A genuine zero-value
    /// order becomes NULL then derives back to 0 downstream — same emitted total, no special case.
    /// </summary>
    internal static decimal? NormalizeExtractedSubTotal(decimal? extracted) =>
        extracted is > 0m ? extracted : null;

    /// <summary>
    /// TaxTotal guard: a NEGATIVE extracted tax is never legitimate → NULL. Unlike Sub/Grand, a
    /// stated <b>0</b> tax IS legitimate (tax-free / intra-EU reverse charge) and is preserved —
    /// nulling it would flip a genuine "0.00" tax to an empty field in the Scriban output path
    /// (<c>NumberOrEmpty</c>). Only bogus negatives are scrubbed.
    /// </summary>
    internal static decimal? NormalizeExtractedTaxTotal(decimal? extracted) =>
        extracted is < 0m ? null : extracted;

    /// <summary>Test-only seam onto <see cref="MapExtractedToParsed"/> (which is private static).</summary>
    internal static ParsedOrder MapExtractedToParsedForTest(ExtractedOrder o) => MapExtractedToParsed(o);

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
                TaxAmount: l.TaxAmount,
                DeliveryDate: l.DeliveryDate,
                // Phase 1 lossless capture (additive).
                ManufacturerPartNumber: l.ManufacturerPartNumber,
                CustomerPartNumber: l.CustomerPartNumber,
                DiscountPercent: l.DiscountPercent,
                Unspsc: l.Unspsc,
                Recipient: l.Recipient,
                ContractNumber: l.ContractNumber,
                NetAmount: l.NetAmount)).ToList(),
            SupplierName: o.SupplierName,
            BuyerTaxId: o.BuyerTaxId,
            SubTotal: o.SubTotal,
            TaxTotal: o.TaxTotal,
            GrandTotal: o.GrandTotal,
            PaymentTerms: o.PaymentTerms,
            DocumentType: o.DocumentType,
            // V5: propagate header-level requested delivery date.
            RequestedDeliveryDate: o.RequestedDeliveryDate,
            // Phase 1 lossless capture: parties, contact, incoterms, shipping, buyer ref, raw bag.
            Parties: o.Parties?.Select(p => new ParsedParty(
                p.Role, p.Name, p.Street, p.City, p.PostalCode, p.Country, p.Vat,
                p.RegNr, p.EdiCode, p.Reference, p.ContactName, p.Email, p.Phone)).ToList(),
            ContactName: o.ContactName,
            ContactEmail: o.ContactEmail,
            ContactPhone: o.ContactPhone,
            Incoterms: o.Incoterms,
            ShippingMethod: o.ShippingMethod,
            BuyerOrderRef: o.BuyerOrderRef,
            RawFields: o.RawFields?.Select(f => new ParsedRawField(f.Label, f.Value)).ToList());

    internal async Task<List<PurchaseOrderLineEntity>> BuildLineEntitiesAsync(
        Guid organisationId,
        Guid supplierId,
        string supplierName,
        IReadOnlyList<ParsedOrderLine> lines,
        IReadOnlyList<AiMappingCandidate> aiCandidates,
        CancellationToken ct,
        IReadOnlyList<EffectiveRevisionItemMapping>? itemMappingSnapshot = null)
    {
        // Pass 1 — deterministic resolve for every line. Either the pinned revision's
        // item-mapping SNAPSHOT (launch batch 7 — exact-match, in-memory, mirrors
        // ResolveManyAsync's trimmed Ordinal keying) or the live table in a single query.
        var resolvedMap = itemMappingSnapshot is not null
            ? ResolveFromSnapshot(itemMappingSnapshot, lines)
            : await _mappings.ResolveManyAsync(
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
            // T4 — lines that already state a source manufacturer part number get the 0.95
            // source suggestion below; exclude them from the (last-resort) web product search.
            var sourceMpnLineNumbers = lines
                .Where(l => !string.IsNullOrWhiteSpace(l.ManufacturerPartNumber))
                .Select(l => l.LineNumber)
                .ToHashSet();

            var groundedCandidates = await BuildCatalogGroundedCandidatesAsync(
                organisationId, supplierId, unresolvedContexts, aiCandidates, sourceMpnLineNumbers, ct);

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
            {
                // Prefer a REAL code the source document already states — e.g. a cXML
                // <ManufacturerPartID> like "REDACTED-ORDER-DATA" (a genuine Apple part number) — over a
                // fuzzy catalog guess. The founder's complaint was that the resolver surfaced a
                // random catalog code (e.g. "ACME-JSON-2 @ 90%") for a clearly-identified product
                // while the manufacturer part number sat unused in the source. This is a suggestion
                // only (the line still needs review / a one-click accept); it never auto-resolves,
                // and it is byte-identical for sources that carry no manufacturer part number.
                if (!string.IsNullOrWhiteSpace(line.ManufacturerPartNumber))
                {
                    suggestion = new AiMappingSuggestion(
                        SupplierItemCode: line.ManufacturerPartNumber!.Trim(),
                        Confidence:       0.95f,
                        Reason:           "Manufacturer part number is stated in the source document.",
                        Provenance:       "source document: manufacturer part number");
                }
                else
                {
                    suggestions.TryGetValue(line.LineNumber, out suggestion);

                    // Confidence floor for AI / fuzzy-catalog suggestions: a weak fuzzy match
                    // (e.g. "ACME-JSON-2" @ 0.60 for a Yubico YubiKey) is worse than no suggestion —
                    // it misleads the reviewer into accepting an unrelated code. Below the floor we
                    // DROP it so the UI shows "no confident match — enter manually" instead. This
                    // applies ONLY to the AI/fuzzy-catalog path; the source-manufacturer-part-number
                    // branch above is a real code stated in the document and stays at 0.95.
                    if (suggestion is not null && suggestion.Confidence < AiSuggestionConfidenceFloor)
                        suggestion = null;
                }
            }

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
                // P2 hardening: persist WHY the line was flagged so the review UI can say so.
                ReviewReason     = ComposeReviewReason(resolved, parserFlagged, line),
                AiSuggestedSupplierItemCode = suggestion?.SupplierItemCode,
                AiSuggestionConfidence = suggestion?.Confidence,
                AiSuggestionReason = suggestion?.Reason,
                AiSuggestionProvenance = suggestion?.Provenance,
                // Phase 4 enrichment (carried from the parsed line; null for parsers that don't emit it).
                LineAmount = line.LineAmount,
                TaxRate = line.TaxRate,
                TaxAmount = line.TaxAmount,
                DeliveryDate = line.DeliveryDate,
                // Phase 1 lossless capture (carried from the parsed line; null for parsers that don't emit it).
                ManufacturerPartNumber = line.ManufacturerPartNumber,
                CustomerPartNumber     = line.CustomerPartNumber,
                DiscountPercent        = line.DiscountPercent,
                Unspsc                 = line.Unspsc,
                Recipient              = line.Recipient,
                ContractNumber         = line.ContractNumber,
                NetAmount              = line.NetAmount,
            });
        }

        return entities;
    }

    // ── Phase 1 lossless capture — SourceCapture helpers ───────────────────────

    /// <summary>
    /// Workshop P0 — tokenises raw source bytes (CSV/XLSX/XML/cXML/EDI/X12/JSON) into a lossless
    /// <see cref="SourceCapture"/> nav row so the SYNC file-ingest path
    /// (<see cref="CreateFromFileAsync"/>) persists EVERY addressable source field, not just the
    /// canonical-promoted ones. Inline nav (one SaveChanges persists it). Mirrors the async path's
    /// token bag shape (id/label/value/group) so <c>SourceTokenSerialization.FromTokensJson</c> reads
    /// it back identically. Best-effort: an unsupported format or a tokeniser failure returns null —
    /// the ingest is NEVER failed by capture. Pure aside from the (no-throw) tokenise call.
    /// </summary>
    private async Task<SourceCapture?> BuildSourceCaptureFromBytesAsync(
        byte[] bytes, string? extension, Guid orgId, DateTime now, CancellationToken ct)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (ext is not (".csv" or ".xlsx" or ".xml" or ".cxml" or ".edi" or ".x12" or ".json"))
            return null;

        IReadOnlyList<ProcuLink.Transform.Tokenizing.SourceToken> tokens;
        try
        {
            tokens = await _tokenizer.TokenizeAsync(bytes, ext, ct);
        }
        catch (Exception exTok)
        {
            _logger.LogWarning(exTok,
                "Sync-ingest source tokenization failed for org {OrgId} (non-fatal — no capture written).", orgId);
            return null;
        }

        if (tokens is not { Count: > 0 }) return null;

        var bag = tokens.Select(t => new { id = t.Id, label = t.Label, value = t.Value, group = t.Group });
        return new SourceCapture
        {
            Id         = Guid.NewGuid(),
            OrgId      = orgId,
            Format     = ext.TrimStart('.'),
            CapturedAt = now,
            TokensJson = JsonDocument.Parse(JsonSerializer.Serialize(bag)),
        };
    }

    /// <summary>
    /// First party whose <c>Role</c> matches <paramref name="role"/> (case-insensitive — the
    /// canonical roles are "shipTo" / "billTo"), or null when none. Used to denormalise the
    /// shipTo / billTo address onto the flat cXML address columns. Transform-layer overload.
    /// </summary>
    private static ParsedParty? PartyOf(IReadOnlyList<ParsedParty>? parties, string role) =>
        parties?.FirstOrDefault(p => string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase));

    /// <summary>Core-layer (LLM/email) overload of <see cref="PartyOf(IReadOnlyList{ParsedParty}?,string)"/>.</summary>
    private static ExtractedParty? PartyOf(IReadOnlyList<ExtractedParty>? parties, string role) =>
        parties?.FirstOrDefault(p => string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds an inline <see cref="SourceCapture"/> nav row from an LLM/email raw-fields bag
    /// (label+value pairs the canonical model had no slot for), or null when there are none.
    /// Used on the SYNC ingest path where the parent is attached fresh and a single
    /// SaveChanges persists the nav. Pure — no DB access.
    /// </summary>
    private static SourceCapture? BuildSourceCapture(
        IEnumerable<ExtractedRawField>? rawFields, Guid orgId, string format, DateTime now)
    {
        var bag = rawFields?
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => new { label = f.Label, value = f.Value })
            .ToList();
        if (bag is not { Count: > 0 }) return null;

        return new SourceCapture
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Format = string.IsNullOrWhiteSpace(format) ? "unknown" : format.ToLowerInvariant(),
            CapturedAt = now,
            TokensJson = JsonDocument.Parse(JsonSerializer.Serialize(bag)),
        };
    }

    /// <summary>
    /// Idempotently replaces the one-per-order <see cref="SourceCapture"/> raw bag on the ASYNC
    /// ingest path (delete-then-insert so a Hangfire retry doesn't duplicate). Prefers the full
    /// structured token set; else falls back to the LLM/email <c>RawFields</c> bag. Writes nothing
    /// when there is neither a bag nor raw text. ExecuteDeleteAsync is Npgsql-only — fine, this
    /// whole async path is already Postgres-only.
    /// </summary>
    private async Task UpsertSourceCaptureAsync(
        Guid orderId, Guid organisationId, string? format,
        IReadOnlyList<ProcuLink.Transform.Tokenizing.SourceToken>? tokens,
        ParsedOrder parsedOrder, string? rawText, DateTime now, CancellationToken ct)
    {
        await _db.SourceCaptures
            .Where(s => s.OrderId == orderId && s.OrgId == organisationId)
            .ExecuteDeleteAsync(ct);

        object? bag = tokens is { Count: > 0 }
            ? tokens.Select(t => new { id = t.Id, label = t.Label, value = t.Value, group = t.Group })
            : (parsedOrder.RawFields is { Count: > 0 }
                ? parsedOrder.RawFields.Select(f => new { label = f.Label, value = f.Value })
                : null);

        if (bag is null && string.IsNullOrWhiteSpace(rawText)) return;

        _db.SourceCaptures.Add(new SourceCapture
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            OrgId = organisationId,
            Format = string.IsNullOrWhiteSpace(format) ? "unknown" : format.ToLowerInvariant(),
            CapturedAt = now,
            RawText = rawText,
            TokensJson = bag is null ? null : JsonDocument.Parse(JsonSerializer.Serialize(bag)),
        });
    }

    /// <summary>
    /// Short "why was this flagged" string written at parse time (P2 hardening).
    /// Combines the unresolved-code reason with the parser's own numeric-ambiguity
    /// reason when both apply; null when the line was not flagged here. (The
    /// structured-extraction overlay adds its own reasons afterwards via
    /// <see cref="OrderServiceShared.ApplyExtractionReviewFlags"/>.)
    /// </summary>
    internal static string? ComposeReviewReason(bool resolved, bool parserFlagged, ParsedOrderLine line)
    {
        if (resolved && !parserFlagged) return null;

        var parts = new List<string>(2);
        if (!resolved)
        {
            parts.Add(string.IsNullOrWhiteSpace(line.BuyerItemCode)
                ? "The line has no buyer item code to resolve against supplier mappings."
                : $"No supplier item code mapping was found for buyer code '{line.BuyerItemCode.Trim()}'.");
        }
        if (parserFlagged)
        {
            parts.Add(line.ReviewReason
                ?? "A quantity or unit price could not be read unambiguously from the source file.");
        }
        return string.Join(" ", parts);
    }

    // ── Launch batch 7 — revision-authority snapshot helpers ───────────────────

    /// <summary>Mirrors <c>PoMappingService</c>'s serializer so a snapshotted ConfigJson round-trips identically.</summary>
    private static readonly JsonSerializerOptions SnapshotPoMappingSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>How a revision's input-mapping snapshot failed to yield a usable <see cref="PoMappingConfig"/> (replay flip A shared helper).</summary>
    internal enum SnapshotPoMappingProblem { None, NotSnapshotted, Empty, Malformed }

    /// <summary>
    /// Replay flip A — the SHARED deserialize for a revision's <c>input_mapping_json</c> snapshot,
    /// used by both the live parse path (<see cref="ResolveSnapshotPoMapping"/>) and the replay
    /// parse-from-source leg (<c>ReplayService</c>) so there is exactly ONE reading of the snapshot.
    /// Returns the config only when USABLE (at least one header or line rule); otherwise null plus
    /// the reason (and the <see cref="JsonException"/> for malformed JSON). Never throws.
    /// </summary>
    internal static (PoMappingConfig? Config, SnapshotPoMappingProblem Problem, JsonException? Error)
        TryDeserializeSnapshotPoMapping(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, SnapshotPoMappingProblem.NotSnapshotted, null);

        try
        {
            var config = JsonSerializer.Deserialize<PoMappingConfig>(json, SnapshotPoMappingSerializerOptions);
            if (config is null || (config.Header.Count == 0 && config.Lines.Count == 0))
                return (null, SnapshotPoMappingProblem.Empty, null);
            return (config, SnapshotPoMappingProblem.None, null);
        }
        catch (JsonException ex)
        {
            return (null, SnapshotPoMappingProblem.Malformed, ex);
        }
    }

    /// <summary>
    /// The parse-mapping config snapshotted into the pinned revision, when USABLE (at least one
    /// header or line rule). Returns null — meaning "fall back to the LIVE supplier PO mapping" —
    /// for the live bundle, a null/blank snapshot, an empty snapshot, or a malformed snapshot
    /// (logged). Parse must never brick on a bad snapshot.
    /// </summary>
    internal PoMappingConfig? ResolveSnapshotPoMapping(EffectiveConnectionConfig effective, Guid orderId)
    {
        if (!effective.IsRevision || string.IsNullOrWhiteSpace(effective.InputMappingJson))
            return null;

        var (config, problem, error) = TryDeserializeSnapshotPoMapping(effective.InputMappingJson);
        switch (problem)
        {
            case SnapshotPoMappingProblem.Empty:
                _logger.LogInformation(
                    "Order {OrderId}: pinned {Source} input mapping is empty — using the live PO mapping.",
                    orderId, effective.Source);
                return null;
            case SnapshotPoMappingProblem.Malformed:
                _logger.LogWarning(error,
                    "Order {OrderId}: pinned {Source} input mapping is malformed — using the live PO mapping.",
                    orderId, effective.Source);
                return null;
        }

        _logger.LogInformation(
            "Order {OrderId}: parse mapping taken from pinned {Source}.", orderId, effective.Source);
        return config;
    }

    /// <summary>
    /// Exact-match resolution against the pinned revision's item-mapping snapshot. Mirrors
    /// <c>ItemMappingService.ResolveManyAsync</c> semantics precisely: the returned dictionary is
    /// keyed by the TRIMMED buyer item code (Ordinal), is total over the non-blank input set
    /// (missing mapping ⇒ null value ⇒ the line flows to review exactly as today), and matches
    /// snapshot rows case-sensitively against the trimmed requested codes (the last duplicate
    /// snapshot row wins, like the live resolver's row loop).
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> ResolveFromSnapshot(
        IReadOnlyList<EffectiveRevisionItemMapping> snapshot,
        IReadOnlyList<ParsedOrderLine> lines)
    {
        var requested = lines
            .Select(l => l.BuyerItemCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, string?>(requested.Count, StringComparer.Ordinal);
        foreach (var code in requested)
            result[code] = null;

        foreach (var mapping in snapshot)
        {
            // Only overwrite a key that was actually requested (case-sensitive) — same guard
            // as the live resolver.
            if (result.ContainsKey(mapping.BuyerItemCode))
                result[mapping.BuyerItemCode] = mapping.SupplierItemCode;
        }

        return result;
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

    /// <summary>
    /// T4 — hard cap on how many residual lines trigger a (billable, seconds-long) external web
    /// product search per order, so a large catalog-less PO can't fan out into dozens of calls.
    /// </summary>
    private const int WebSearchLineCap = 5;

    private async Task<IReadOnlyList<AiMappingCandidate>> BuildCatalogGroundedCandidatesAsync(
        Guid organisationId,
        Guid supplierId,
        IReadOnlyList<AiMappingLineContext> unresolvedContexts,
        IReadOnlyList<AiMappingCandidate> mappingCandidates,
        IReadOnlySet<int> sourceMpnLineNumbers,
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

        // No catalog → today's free suggestion, PLUS (T4) optional last-resort web product-code
        // grounding for residual lines (a description, no source MPN). Web grounding is additive
        // ONLY when there is no authoritative catalog — matching founder intent. (With a catalog
        // the allow-list would reject any non-catalog code anyway, so we don't even spend a call.)
        if (catalog.Count == 0)
        {
            var webCandidates = await BuildWebSearchCandidatesAsync(
                organisationId, unresolvedContexts, sourceMpnLineNumbers, ct);
            return webCandidates.Count == 0
                ? mappingCandidates
                : mappingCandidates.Concat(webCandidates).ToList();
        }

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

    // ── T4: external web/product-code grounding (last resort, no catalog) ──────────
    // Runs ONLY from the no-catalog branch above, for residual lines that carry a human
    // description but no source manufacturer part number, capped at WebSearchLineCap. A hit is
    // folded in as a non-catalog candidate (IsCatalogProduct=false) labelled "web product search
    // (unverified)" — so the line stays NeedsReview and the catalog allow-list still rejects web
    // codes for any supplier that DOES have a catalog. Default deploy is unaffected: the searcher
    // no-ops unless its feature flag + key are set, and it is null on the older positional ctors.

    /// <summary>
    /// Pure selection of which residual lines are eligible for an external web product search:
    /// those with a non-blank <see cref="AiMappingLineContext.Description"/> that do NOT already
    /// carry a source manufacturer part number (their line number is absent from
    /// <paramref name="sourceMpnLineNumbers"/>), taking at most <paramref name="cap"/> in order.
    /// Side-effect free so the gating is unit-testable without a network call.
    /// </summary>
    internal static IReadOnlyList<AiMappingLineContext> SelectWebSearchResidualLines(
        IReadOnlyList<AiMappingLineContext> unresolvedContexts,
        IReadOnlySet<int> sourceMpnLineNumbers,
        int cap) =>
        unresolvedContexts
            .Where(l => !string.IsNullOrWhiteSpace(l.Description))
            .Where(l => !sourceMpnLineNumbers.Contains(l.LineNumber))
            .Take(cap)
            .ToList();

    private async Task<IReadOnlyList<AiMappingCandidate>> BuildWebSearchCandidatesAsync(
        Guid organisationId,
        IReadOnlyList<AiMappingLineContext> unresolvedContexts,
        IReadOnlySet<int> sourceMpnLineNumbers,
        CancellationToken ct)
    {
        // Off unless a searcher is wired (it self-no-ops unless its flag + key are configured).
        if (_productCodeSearch is null) return Array.Empty<AiMappingCandidate>();

        var residual = SelectWebSearchResidualLines(unresolvedContexts, sourceMpnLineNumbers, WebSearchLineCap);
        if (residual.Count == 0) return Array.Empty<AiMappingCandidate>();

        // Pre-flight per-org monthly AI cap: never start a billable web search for an org that has
        // already exhausted its budget. The IProductCodeSearch contract carries no org id, so the
        // cap is enforced here (where the org is known). Fail SAFE on a cap-check error — skip
        // rather than silently bypass. With no tracker wired (older ctors) there is no cap to read,
        // but those code paths also pass a null searcher, so web search never runs there anyway.
        if (_aiUsage is not null)
        {
            try
            {
                if (await _aiUsage.IsAtOrOverLimitAsync(organisationId, ct))
                {
                    _logger.LogInformation(
                        "Web product search skipped — org {OrgId} reached its monthly AI token limit.", organisationId);
                    return Array.Empty<AiMappingCandidate>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AI cap check failed for org {OrgId}; skipping web product search to be safe.", organisationId);
                return Array.Empty<AiMappingCandidate>();
            }
        }

        var candidates = new List<AiMappingCandidate>(residual.Count);
        foreach (var line in residual)
        {
            ct.ThrowIfCancellationRequested();
            var match = await _productCodeSearch.FindPartNumberAsync(line.Description!, brandHint: null, ct);
            if (match is null || string.IsNullOrWhiteSpace(match.PartNumber)) continue;

            var url = string.IsNullOrWhiteSpace(match.SourceUrl) ? "unverified" : match.SourceUrl!.Trim();
            candidates.Add(new AiMappingCandidate(
                BuyerItemCode:    line.BuyerItemCode ?? string.Empty,
                SupplierItemCode: match.PartNumber.Trim(),
                Provenance:       $"web product search (unverified): {url}",
                IsCatalogProduct: false,
                Name:             match.Title));
        }
        return candidates;
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

    /// <summary>
    /// CSV parse through a supplier/revision PO mapping template. Internal (was private) so the
    /// replay parse-from-source leg (<c>ReplayService</c> — replay flip A) re-parses with EXACTLY
    /// the routing <see cref="ParseStoredFileAsync"/> uses; body unchanged.
    /// </summary>
    internal static Task<ParsedOrder> ParseWithMappingTemplateAsync(
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

        // Read quantity/unit price through the SAME locale-aware reader the direct CSV
        // path uses (CsvOrderParser → NumberParsing), with a ';' template separator as the
        // European locale signal (',' is the decimal mark there). The previous
        // NumberStyles.Any + InvariantCulture read EU "73,22" as 7322 (100× over) and
        // "1.234,56" as null/0, both silently; now an ambiguous/unparseable token flags the
        // line NeedsReview so a coordinator checks instead of a wrong value shipping.
        var european = config.Separator == ";";
        var lines = mapped.Lines.Select((l, i) =>
        {
            var (qty,   qtyAmbiguous)   = NumberParsing.TryParseFlexibleDecimal(l.Quantity,  european);
            var (price, priceAmbiguous) = NumberParsing.TryParseFlexibleDecimal(l.UnitPrice, european);
            return new ParsedOrderLine(
                LineNumber:    int.TryParse(l.LineNumber, out var ln) ? ln : (i + 1),
                BuyerItemCode: l.BuyerItemCode ?? string.Empty,
                Description:   l.Description,
                Quantity:      qty ?? 0m,
                Unit:          l.Unit,
                UnitPrice:     price,
                NeedsReview:   qtyAmbiguous || priceAmbiguous,
                ReviewReason:  NumberParsing.BuildAmbiguityReason(qtyAmbiguous, priceAmbiguous));
        }).ToList();

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
