using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Email;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// How the <see cref="CountingOrderService"/> test double behaves on a given stub-creation call —
/// lets the resume-on-conflict Postgres tests simulate a transient failure (throws, like a real
/// R2/DB blip), a permanent content failure (Result.Fail), or success.
/// </summary>
internal enum StubBehavior
{
    Succeed,
    FailPermanent,
    ThrowTransient,
}

/// <summary>
/// Thread-safe recording <see cref="IStubOrderCreator"/> for the claim-first / resume-on-conflict
/// pull-ingress Postgres tests. Counts stub-creation calls and, on success, SELF-COMMITS a minimal
/// <see cref="PurchaseOrderEntity"/> under the caller-supplied order id — find-or-create on that
/// primary key, exactly like the real service — so the ingress's order-existence check
/// (<c>purchase_orders</c> by id) sees committed orders and the tests can assert on the real
/// invariant: exactly ONE order per file. An optional behaviour hook injects transient/permanent
/// failures for the lost-order and boundedness proofs.
/// </summary>
internal sealed class CountingOrderService : IStubOrderCreator
{
    private readonly DbContextOptions<ProcuLinkDbContext> _options;
    private readonly Func<StubBehavior>? _behavior;
    private int _createStubCalls;
    private int _unroutedCalls;

    public CountingOrderService(DbContextOptions<ProcuLinkDbContext> options, Func<StubBehavior>? behavior = null)
    {
        _options = options;
        _behavior = behavior;
    }

    public int CreateStubCalls => Volatile.Read(ref _createStubCalls);
    public int UnroutedCalls   => Volatile.Read(ref _unroutedCalls);
    public int TotalCreates    => CreateStubCalls + UnroutedCalls;

    public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
        Guid organisationId, Guid supplierId, Guid orderId, Stream fileStream, string filename, string contentType, CancellationToken ct)
    {
        Interlocked.Increment(ref _createStubCalls);
        return HandleAsync(organisationId, orderId, ct);
    }

    public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
        Guid organisationId, Guid orderId, Stream fileStream, string filename, string contentType, CancellationToken ct)
    {
        Interlocked.Increment(ref _unroutedCalls);
        return HandleAsync(organisationId, orderId, ct);
    }

    private async Task<Result<PurchaseOrderEntity>> HandleAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        switch (_behavior?.Invoke() ?? StubBehavior.Succeed)
        {
            case StubBehavior.ThrowTransient:
                // Models an R2/Neon blip or a SIGTERM: the order is NOT committed and the exception
                // propagates so the poll fails and Hangfire retries (leaving the claim resume-able).
                throw new InvalidOperationException("transient stub-creation failure (test double)");
            case StubBehavior.FailPermanent:
                return Result<PurchaseOrderEntity>.Fail("permanently bad file (test double)");
        }

        // Succeed: self-commit a minimal order under the given id. Find-or-create on the primary key
        // so a resume that re-runs the same id never creates a second order (models the real service).
        await using var db = new ProcuLinkDbContext(_options);
        if (!await db.PurchaseOrders.AnyAsync(o => o.Id == orderId, ct))
        {
            db.PurchaseOrders.Add(new PurchaseOrderEntity
            {
                Id = orderId, OrgId = orgId, SupplierId = null,
                PoNumber = "PO-STUB", Currency = "EUR", Status = "parsing",
                OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* concurrent create of same PK — treat as already existing */ }
        }

        return Result<PurchaseOrderEntity>.Ok(new PurchaseOrderEntity
        {
            Id = orderId, OrgId = orgId, SupplierId = null, Status = "parsing",
            PoNumber = "PO-STUB", Currency = "EUR", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }
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
