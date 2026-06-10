using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Services;

/// <summary>
/// Orchestrates the upload → parse → resolve → persist lifecycle for Phase 2.
/// Lives in the Api project so it can access both Infrastructure (DbContext) and
/// Transform (OrderParserFactory) without creating a circular project reference.
///
/// Internal-facade decomposition (audit W1/B1): this class keeps its public 12-dependency
/// constructor and its <see cref="IOrderService"/> surface unchanged, but the method bodies
/// now live in four cohesive internal sub-services constructed in the ctor below. Each public
/// method delegates one-to-one to the matching sub-service; behaviour is unchanged.
/// </summary>
public sealed class OrderService : IOrderService
{
    private readonly OrderIngestionService  _ingestion;
    private readonly OrderQueryService      _query;
    private readonly OrderResolutionService _resolution;
    private readonly OrderTransformService  _transform;

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
        IFormatDetector                formatDetector,
        IStructuredOrderExtractor?     structuredExtractor = null,
        IAiSuggestionDecisionService?  aiDecisions = null,
        ICatalogRetrievalService?      catalogRetrieval = null)
    {
        // Shared helpers (best-effort exception reconcile, passport events, audit-event
        // builder, extraction-review flagging) used by more than one sub-service.
        var shared = new OrderServiceShared(db, exceptions, logger);

        _ingestion = new OrderIngestionService(
            db, fileStorage, parserFactory, mappings, poMappingService, aiMappings,
            logger, integrationTrigger, formatDetector, structuredExtractor, shared,
            catalogRetrieval);

        _query = new OrderQueryService(db, fileStorage);

        // Decision-history recorder. When the DI container supplies one we use it; otherwise
        // (older positional test ctors) we build the concrete recorder against the same DbContext
        // so the resolve/accept paths still persist durable history.
        var decisions = aiDecisions
            ?? new ProcuLink.Infrastructure.Services.AiSuggestionDecisionService(db);

        _resolution = new OrderResolutionService(db, mappings, logger, shared, decisions);

        _transform = new OrderTransformService(db, fileStorage, transformers, logger);
    }

    // ── Ingestion ─────────────────────────────────────────────────────────────

    public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(
        Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType, CancellationToken ct)
        => _ingestion.CreateFromFileAsync(organisationId, supplierId, fileStream, filename, contentType, ct);

    public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
        Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType, CancellationToken ct)
        => _ingestion.CreateStubAsync(organisationId, supplierId, fileStream, filename, contentType, ct);

    public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(
        Guid organisationId, Guid supplierId, ExtractedOrder order, string source, CancellationToken ct)
        => _ingestion.CreateStubFromParsedOrderAsync(organisationId, supplierId, order, source, ct);

    public Task<Result<ParsedFileOutput>> ParseStoredFileAsync(
        Guid organisationId, Guid orderId, CancellationToken ct)
        => _ingestion.ParseStoredFileAsync(organisationId, orderId, ct);

    // ── Query ─────────────────────────────────────────────────────────────────

    public Task<Result<PurchaseOrderEntity>> GetByIdAsync(
        Guid organisationId, Guid orderId, CancellationToken ct)
        => _query.GetByIdAsync(organisationId, orderId, ct);

    public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(
        Guid organisationId, int page, int pageSize, string? status, Guid? supplierId,
        string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
        => _query.ListPagedAsync(organisationId, page, pageSize, status, supplierId, search, dateFrom, dateTo, ct);

    public Task<Result<DownloadUrl>> GetDownloadUrlAsync(
        Guid organisationId, Guid orderId, Guid artifactId, CancellationToken ct)
        => _query.GetDownloadUrlAsync(organisationId, orderId, artifactId, ct);

    // ── Resolution ────────────────────────────────────────────────────────────

    public Task<Result<PurchaseOrderEntity>> ResolveAsync(
        Guid organisationId, Guid orderId, IReadOnlyList<LineResolution> resolutions,
        bool saveMappings, CancellationToken ct, ResolveHeaderFields? header = null)
        => _resolution.ResolveAsync(organisationId, orderId, resolutions, saveMappings, ct, header);

    public Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(
        Guid organisationId, Guid orderId, string reason, CancellationToken ct)
        => _resolution.MarkRejectedAsync(organisationId, orderId, reason, ct);

    public Task<Result<int>> AcceptAiSuggestionsAsync(
        Guid organisationId, Guid orderId, double minConfidence, CancellationToken ct)
        => _resolution.AcceptAiSuggestionsAsync(organisationId, orderId, minConfidence, ct);

    // ── Transform ─────────────────────────────────────────────────────────────

    public Task<Result<TransformResponse>> TransformAsync(
        Guid organisationId, Guid orderId, OutputFormat format, CancellationToken ct)
        => _transform.TransformAsync(organisationId, orderId, format, ct);

    // ── Internal seams preserved for direct unit testing ──────────────────────
    // These keep the existing OrderServicePdfRoutingTests green: it calls
    // svc.ParsePdfAsync(...) on an OrderService instance and OrderService.ApplyExtractionReviewFlags(...)
    // statically. Both forward to where the logic now lives.

    internal Task<(ParsedOrder parsed, IReadOnlyCollection<int> reviewLineNumbers)> ParsePdfAsync(
        byte[] bytes, Guid organisationId, Guid orderId, CancellationToken ct)
        => _ingestion.ParsePdfAsync(bytes, organisationId, orderId, ct);

    internal static void ApplyExtractionReviewFlags(
        IReadOnlyList<PurchaseOrderLineEntity> lines,
        IReadOnlyCollection<int> reviewLineNumbers)
        => OrderServiceShared.ApplyExtractionReviewFlags(lines, reviewLineNumbers);
}
