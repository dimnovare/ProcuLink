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

        // Fetch minimal order data for the org.  BuyerName is stored in CanonicalJson
        // (populated after parsing) rather than as a dedicated column on purchase_orders.
        // We materialise the projection here and correlate in memory so that EF Core's
        // InMemory provider (used in tests) and Postgres both work without raw SQL.
        var orders = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(o => o.OrgId == orgId)
            .Select(o => new { o.CanonicalJson, o.CreatedAt })
            .ToListAsync(ct);

        // Group orders by the BuyerName stored in their CanonicalJson.
        // Keys in canonical JSON may be "buyerName" (camelCase from OrderService) or
        // "BuyerName" (PascalCase written by older parsers).  Mirroring the same two-key
        // lookup used in OrderService.ListAsync (lines 656–659).
        var ordersByBuyerName = new Dictionary<string, (int Count, DateTime Latest)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var o in orders)
        {
            if (o.CanonicalJson is null) continue;
            try
            {
                var root = o.CanonicalJson.RootElement;
                string? buyerName = null;
                if (root.TryGetProperty("buyerName", out var el))
                    buyerName = el.GetString();
                else if (root.TryGetProperty("BuyerName", out var el2))
                    buyerName = el2.GetString();

                if (string.IsNullOrWhiteSpace(buyerName)) continue;

                if (ordersByBuyerName.TryGetValue(buyerName, out var existing))
                    ordersByBuyerName[buyerName] = (existing.Count + 1,
                        o.CreatedAt > existing.Latest ? o.CreatedAt : existing.Latest);
                else
                    ordersByBuyerName[buyerName] = (1, o.CreatedAt);
            }
            catch
            {
                // Malformed JSON — skip this order, same defensive pattern used elsewhere.
            }
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
