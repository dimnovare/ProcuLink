using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// Thread-safe recording <see cref="IOrderService"/> for the claim-first pull-ingress
/// concurrency tests. Counts stub-creation calls (routed + unrouted) across concurrent polls
/// and returns a lightweight in-memory stub (it does NOT persist a PurchaseOrder — the tests
/// assert on the ledger's unique-index guarantee + this call count, which together prove that
/// no duplicate order was created).
/// </summary>
internal sealed class CountingOrderService : IOrderService
{
    private int _createStubCalls;
    private int _unroutedCalls;

    public int CreateStubCalls => Volatile.Read(ref _createStubCalls);
    public int UnroutedCalls   => Volatile.Read(ref _unroutedCalls);
    public int TotalCreates    => CreateStubCalls + UnroutedCalls;

    public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
        Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType, CancellationToken ct)
    {
        Interlocked.Increment(ref _createStubCalls);
        return Task.FromResult(Result<PurchaseOrderEntity>.Ok(Stub(organisationId, supplierId)));
    }

    public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
        Guid organisationId, Stream fileStream, string filename, string contentType, CancellationToken ct)
    {
        Interlocked.Increment(ref _unroutedCalls);
        return Task.FromResult(Result<PurchaseOrderEntity>.Ok(Stub(organisationId, null)));
    }

    private static PurchaseOrderEntity Stub(Guid orgId, Guid? supplierId) => new()
    {
        Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId, Status = "parsing",
        PoNumber = "PO-STUB", Currency = "EUR", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid o, Guid s, Stream f, string fn, string ct2, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(Guid o, Guid s, ExtractedOrder order, string source, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Result<ParsedFileOutput>> ParseStoredFileAsync(Guid o, Guid id, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Result<PurchaseOrderEntity>> GetByIdAsync(Guid o, Guid id, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(Guid o, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(Guid organisationId, int page, int pageSize, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListWindowAsync(Guid organisationId, int skip, int take, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Result<TransformResponse>> TransformAsync(Guid o, Guid id, OutputFormat fmt, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Result<DownloadUrl>> GetDownloadUrlAsync(Guid o, Guid id, Guid aid, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Result<PurchaseOrderEntity>> ResolveAsync(Guid o, Guid id, IReadOnlyList<LineResolution> r, bool s, CancellationToken ct, ResolveHeaderFields? header = null)
        => throw new NotImplementedException();
    public Task<Result<int>> AcceptAiSuggestionsAsync(Guid o, Guid id, double minConfidence, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(Guid organisationId, Guid orderId, string reason, CancellationToken ct)
        => throw new NotImplementedException();
}

/// <summary>Thread-safe counting <see cref="IParseJobEnqueuer"/> for the SFTP/S3 ingress tests.</summary>
internal sealed class CountingParseEnqueuer : IParseJobEnqueuer
{
    private int _count;
    public int Count => Volatile.Read(ref _count);

    public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
    {
        Interlocked.Increment(ref _count);
        return Task.CompletedTask;
    }
}
