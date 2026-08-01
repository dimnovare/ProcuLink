using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Services.Ingress;

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

    public async Task<IdempotencyClaim> ClaimAsync(string key, Guid orgId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Idempotency key must not be blank.", nameof(key));

        var now = _clock();

        var existing = await _db.IdempotencyKeys
            .FirstOrDefaultAsync(k => k.Key == key && k.OrgId == orgId, ct);

        if (existing is not null)
        {
            // Inside the window this is a live claim — the caller is a replay or the loser of a
            // race, and must be handed the winner's order id, never create its own.
            if (now - existing.CreatedAt <= IdempotencyWindow)
                return new IdempotencyClaim(IsNew: false, OrderId: existing.OrderId);

            // Expired: take the row over with a fresh pre-generated id and report a new claim, so a
            // key reused a day later behaves like a first delivery rather than resurrecting a stale
            // order id.
            existing.OrderId   = Guid.NewGuid();
            existing.CreatedAt = now;
            await _db.SaveChangesAsync(ct);
            return new IdempotencyClaim(IsNew: true, OrderId: existing.OrderId);
        }

        // Pre-generate the order id and commit the claim BEFORE the order exists, so a crash in
        // between is resumable under this exact id rather than permanently blocking the key.
        var claim = new IdempotencyKey
        {
            Key       = key,
            OrgId     = orgId,
            OrderId   = Guid.NewGuid(),
            CreatedAt = now,
        };
        _db.IdempotencyKeys.Add(claim);

        try
        {
            await _db.SaveChangesAsync(ct);
            return new IdempotencyClaim(IsNew: true, OrderId: claim.OrderId);
        }
        catch (DbUpdateException ex) when (IngressDedupe.IsUniqueViolation(ex))
        {
            // A concurrent request won the (org_id, key) primary key. Detach so this context stays
            // usable, then re-read the winner's claim and hand back ITS order id — the loser must
            // report the winner's order, not an error and not a second order.
            _db.Entry(claim).State = EntityState.Detached;

            var winner = await _db.IdempotencyKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.Key == key && k.OrgId == orgId, ct);

            return winner is null
                // The winner's row vanished between the violation and this read (only a concurrent
                // erase does that). Treat the key as unclaimed so the caller creates rather than
                // returning an id nothing will ever fill.
                ? new IdempotencyClaim(IsNew: true, OrderId: Guid.NewGuid())
                : new IdempotencyClaim(IsNew: false, OrderId: winner.OrderId);
        }
    }
}
