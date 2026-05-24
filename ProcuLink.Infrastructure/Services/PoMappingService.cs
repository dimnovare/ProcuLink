using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Infrastructure.Services;

public class PoMappingService : IPoMappingService
{
    private readonly ProcuLinkDbContext _db;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public PoMappingService(ProcuLinkDbContext db)
    {
        _db = db;
    }

    public async Task<PoMappingConfig?> GetAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default)
    {
        var entity = await _db.SupplierPoMappings
            .Where(x => x.OrgId == organisationId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        if (entity is null) return null;
        return JsonSerializer.Deserialize<PoMappingConfig>(entity.ConfigJson, _jsonOptions);
    }

    public async Task<PoMappingConfig> UpsertAsync(Guid organisationId, Guid supplierId, PoMappingConfig config, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(config, _jsonOptions);
        var now = DateTime.UtcNow;

        var entity = await _db.SupplierPoMappings
            .Where(x => x.OrgId == organisationId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
        {
            entity = new SupplierPoMapping
            {
                Id = Guid.NewGuid(),
                OrgId = organisationId,
                SupplierId = supplierId,
                ConfigJson = json,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.SupplierPoMappings.Add(entity);
        }
        else
        {
            entity.ConfigJson = json;
            entity.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return config;
    }

    public async Task DeleteAsync(Guid organisationId, Guid supplierId, CancellationToken ct = default)
    {
        var entity = await _db.SupplierPoMappings
            .Where(x => x.OrgId == organisationId && x.SupplierId == supplierId)
            .FirstOrDefaultAsync(ct);

        if (entity is not null)
        {
            _db.SupplierPoMappings.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }
}
