using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class BuyerService : IBuyerService
{
    private readonly ProcuLinkDbContext _db;

    public BuyerService(ProcuLinkDbContext db) { _db = db; }

    public async Task<IReadOnlyList<BuyerSummary>> ListAsync(Guid orgId, CancellationToken ct)
    {
        var buyers = await _db.Buyers
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.DeletedAt == null)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);

        // Aggregate order counts per buyer from the denormalised buyer_name column
        // (indexed via IX_purchase_orders_org_id_buyer_name). The GROUP BY runs in
        // SQL — no CanonicalJson blobs are loaded.
        var orderStats = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == orgId && o.BuyerName != null)
            .GroupBy(o => o.BuyerName!)
            .Select(g => new { Name = g.Key, Count = g.Count(), Latest = g.Max(o => o.CreatedAt) })
            .ToListAsync(ct);

        // SQL grouping is case-sensitive but buyer matching below is OrdinalIgnoreCase,
        // so merge groups that differ only by case before the lookup.
        var ordersByBuyerName = new Dictionary<string, (int Count, DateTime Latest)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var s in orderStats)
        {
            if (ordersByBuyerName.TryGetValue(s.Name, out var existing))
                ordersByBuyerName[s.Name] = (existing.Count + s.Count,
                    s.Latest > existing.Latest ? s.Latest : existing.Latest);
            else
                ordersByBuyerName[s.Name] = (s.Count, s.Latest);
        }

        var now = DateTime.UtcNow;
        return buyers
            .Select(b =>
            {
                ordersByBuyerName.TryGetValue(b.Name, out var stats);

                // LastOrderAge: human-readable relative age of the most-recent order for
                // this buyer, or null if no orders have been parsed for them yet.
                string? lastOrderAge = null;
                if (stats.Count > 0)
                {
                    var age = now - stats.Latest;
                    lastOrderAge = age.TotalDays >= 1
                        ? $"{(int)age.TotalDays}d ago"
                        : age.TotalHours >= 1
                            ? $"{(int)age.TotalHours}h ago"
                            : "just now";
                }

                return new BuyerSummary(
                    Id:           b.Id,
                    Name:         b.Name,
                    Code:         b.Code,
                    OrderCount:   stats.Count,
                    LastOrderAge: lastOrderAge,
                    Formats:      Array.Empty<string>());
            })
            .ToList();
    }

    public async Task<Buyer> CreateAsync(Guid orgId, string name, string code, CancellationToken ct)
    {
        var buyer = new Buyer
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Name      = name.Trim(),
            Code      = code.Trim().ToUpperInvariant(),
            CreatedAt = DateTime.UtcNow,
        };

        _db.Buyers.Add(buyer);
        await _db.SaveChangesAsync(ct);
        return buyer;
    }

    public async Task<Buyer?> UpdateAsync(Guid orgId, Guid id, string name, string code, CancellationToken ct)
    {
        var buyer = await _db.Buyers
            .FirstOrDefaultAsync(b => b.Id == id && b.OrgId == orgId && b.DeletedAt == null, ct);

        if (buyer is null) return null;

        buyer.Name = name.Trim();
        buyer.Code = code.Trim().ToUpperInvariant();
        await _db.SaveChangesAsync(ct);
        return buyer;
    }

    public async Task<bool> DeleteAsync(Guid orgId, Guid id, CancellationToken ct)
    {
        var buyer = await _db.Buyers
            .FirstOrDefaultAsync(b => b.Id == id && b.OrgId == orgId && b.DeletedAt == null, ct);

        if (buyer is null) return false;

        buyer.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
