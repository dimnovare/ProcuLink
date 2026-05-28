using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF-backed <see cref="IIdempotencyService"/>. Reads and writes the
/// <c>idempotency_keys</c> table; the 24-hour window is enforced in
/// <see cref="TryGetExistingOrderIdAsync"/> by comparing against an injectable
/// clock so tests can pin time.
/// </summary>
public sealed class IdempotencyService : IIdempotencyService
{
    private readonly ProcuLinkDbContext _db;
    private readonly Func<DateTimeOffset> _clock;

    public IdempotencyService(ProcuLinkDbContext db)
        : this(db, () => DateTimeOffset.UtcNow, TimeSpan.FromHours(24))
    {
    }

    /// <summary>Test-only ctor: lets tests pin the clock and shrink the window.</summary>
    internal IdempotencyService(
        ProcuLinkDbContext db,
        Func<DateTimeOffset> clock,
        TimeSpan window)
    {
        _db = db;
        _clock = clock;
        IdempotencyWindow = window;
    }

    public TimeSpan IdempotencyWindow { get; }

    public async Task<Guid?> TryGetExistingOrderIdAsync(string key, Guid orgId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var row = await _db.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key && k.OrgId == orgId, ct);

        if (row is null) return null;

        var age = _clock() - row.CreatedAt;
        return age <= IdempotencyWindow ? row.OrderId : null;
    }

    public async Task BindAsync(string key, Guid orgId, Guid orderId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Idempotency key must not be blank.", nameof(key));

        var now = _clock();
        var row = await _db.IdempotencyKeys
            .FirstOrDefaultAsync(k => k.Key == key && k.OrgId == orgId, ct);

        if (row is null)
        {
            _db.IdempotencyKeys.Add(new IdempotencyKey
            {
                Key       = key,
                OrgId     = orgId,
                OrderId   = orderId,
                CreatedAt = now,
            });
        }
        else
        {
            row.OrderId   = orderId;
            row.CreatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }
}
