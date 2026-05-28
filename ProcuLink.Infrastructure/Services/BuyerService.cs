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

        // TODO: enrich OrderCount, LastOrderAge, Formats from PurchaseOrders once
        //       the orders schema makes buyer-to-order correlation trivial (buyer code in canonical JSON).
        return buyers
            .Select(b => new BuyerSummary(
                Id:           b.Id,
                Name:         b.Name,
                Code:         b.Code,
                OrderCount:   0,
                LastOrderAge: null,
                Formats:      Array.Empty<string>()))
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
