using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="IOrderMappingOverrideService"/>. The override is stored
/// under the <c>"mappingOverride"</c> key inside the order's existing <c>canonical_json</c> jsonb
/// (no new table). Writes go through <see cref="CanonicalJsonMerge.SetRawValue"/> so the override
/// sub-document never clobbers sibling keys (buyerName / enrichment provenance). Every query is
/// org-scoped — a cross-tenant order id reads as null / writes as false.
/// </summary>
public sealed class OrderMappingOverrideService : IOrderMappingOverrideService
{
    /// <summary>
    /// Serializer options for WRITING the override sub-document. CamelCase matches the rest of
    /// canonical_json and the frontend contract; nulls are omitted to keep the document tight.
    /// Reads go through <see cref="OrderMappingOverrideReader"/> so decode is identical everywhere.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ProcuLinkDbContext _db;

    public OrderMappingOverrideService(ProcuLinkDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<OrderMappingOverride?> GetAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        // Project only canonical_json — org-scoped — so a cross-tenant id simply yields no row.
        var canonical = await _db.PurchaseOrders
            .AsNoTracking()
            .Where(x => x.Id == orderId && x.OrgId == orgId)
            .Select(x => x.CanonicalJson)
            .FirstOrDefaultAsync(ct);

        return OrderMappingOverrideReader.Read(canonical);
    }

    /// <inheritdoc/>
    public async Task<bool> UpsertAsync(
        Guid orgId, Guid orderId, OrderMappingOverride @override, CancellationToken ct)
    {
        // Tracking load — we mutate canonical_json on the entity and SaveChanges.
        var entity = await _db.PurchaseOrders
            .Where(x => x.Id == orderId && x.OrgId == orgId)
            .FirstOrDefaultAsync(ct);

        if (entity is null) return false;

        var rawJson = JsonSerializer.Serialize(@override, SerializerOptions);

        // Merge the override under its key, preserving every other canonical_json property verbatim.
        entity.CanonicalJson = CanonicalJsonMerge.SetRawValue(
            entity.CanonicalJson, OrderMappingOverrideReader.CanonicalKey, rawJson);
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
