using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Stub DesadvService — full parsing is deferred pending EdiFabric licence.
/// CreateStubAsync stores the file and creates an ASN stub with status="received".
/// </summary>
public sealed class DesadvService : IDesadvService
{
    private readonly ProcuLinkDbContext  _db;
    private readonly IFileStorageService _storage;

    public DesadvService(ProcuLinkDbContext db, IFileStorageService storage)
    {
        _db      = db;
        _storage = storage;
    }

    public async Task<AdvanceShippingNoticeEntity> CreateStubAsync(
        Guid orgId, Guid? supplierId, Stream stream,
        string fileName, string contentType, CancellationToken ct)
    {
        var key = $"asns/{orgId}/{Guid.NewGuid()}_{fileName}";
        // IFileStorageService.UploadAsync signature: (Stream content, string key, string contentType, CancellationToken ct)
        await _storage.UploadAsync(stream, key, contentType, ct);

        var asn = new AdvanceShippingNoticeEntity
        {
            Id             = Guid.NewGuid(),
            OrganisationId = orgId,
            SupplierId     = supplierId,
            ShipmentId     = "PENDING",
            DespatchDate   = DateOnly.FromDateTime(DateTime.UtcNow),
            Status         = "received",
            SourceFileName = fileName,
            SourceFileKey  = key,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };

        _db.AdvanceShippingNotices.Add(asn);
        await _db.SaveChangesAsync(ct);
        return asn;
    }

    public async Task<AdvanceShippingNoticeEntity?> GetAsync(Guid orgId, Guid asnId, CancellationToken ct)
        => await _db.AdvanceShippingNotices
                    .Include(a => a.Packages).ThenInclude(p => p.Lines)
                    .Where(a => a.OrganisationId == orgId && a.Id == asnId)
                    .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<AdvanceShippingNoticeEntity>> ListAsync(Guid orgId, CancellationToken ct)
        => await _db.AdvanceShippingNotices
                    .Where(a => a.OrganisationId == orgId)
                    .OrderByDescending(a => a.CreatedAt)
                    .ToListAsync(ct);
}
