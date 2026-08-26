using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Stub DesadvService — full parsing is deferred pending EdiFabric licence.
/// CreateStubAsync stores the file and creates an ASN stub with status="received".
///
/// <para>CreateStubAsync has NO caller today, and did not have one before <c>POST /api/asns/upload</c>
/// was deleted 2026-08-26 either — that endpoint refused with 501 without ever reaching this class.
/// It is kept as the storage half of deferred ASN ingestion, alongside <c>EdifactDesadvParser</c> and
/// <c>DesadvParserFactory</c>, which are dormant for the same licence reason. Only <c>ListAsync</c>
/// is live, behind <c>GET /api/asns</c>.</para>
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

    /// <summary>
    /// The ASN list, with each ASN's package count.
    ///
    /// <para>The count is a correlated subquery over <c>AsnPackages</c> (one SQL COUNT per row, no
    /// package rows loaded), and it is scoped by <c>OrganisationId</c> as well as by ASN — never
    /// query a tenant table without the org scope, even when the parent row is already scoped.</para>
    /// </summary>
    public async Task<IReadOnlyList<AsnListItem>> ListAsync(Guid orgId, CancellationToken ct)
        => await _db.AdvanceShippingNotices
                    .Where(a => a.OrganisationId == orgId)
                    .OrderByDescending(a => a.CreatedAt)
                    .Select(a => new AsnListItem(
                        a.Id,
                        a.ShipmentId,
                        a.Status,
                        a.DespatchDate,
                        a.SourceFileName,
                        a.CreatedAt,
                        _db.AsnPackages.Count(p =>
                            p.OrganisationId == orgId && p.AdvanceShippingNoticeId == a.Id)))
                    .ToListAsync(ct);
}
