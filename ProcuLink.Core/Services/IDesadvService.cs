using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// One row of the ASN list. <paramref name="PackageCount"/> is the number of packages the ASN
/// carries — the "Packages" column on <c>/inbound/asns</c> in project-proculink, whose
/// <c>AsnDto.packageCount</c> read <c>undefined</c> until 2026-08-26 because the list projection
/// never counted them.
/// </summary>
public sealed record AsnListItem(
    Guid      Id,
    string    ShipmentId,
    string    Status,
    DateOnly  DespatchDate,
    string?   SourceFileName,
    DateTime  CreatedAt,
    int       PackageCount);

public interface IDesadvService
{
    Task<AdvanceShippingNoticeEntity> CreateStubAsync(
        Guid orgId, Guid? supplierId, Stream stream,
        string fileName, string contentType, CancellationToken ct);

    Task<IReadOnlyList<AsnListItem>> ListAsync(Guid orgId, CancellationToken ct);
}
