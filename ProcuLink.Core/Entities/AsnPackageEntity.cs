namespace ProcuLink.Core.Entities;

public class AsnPackageEntity
{
    public Guid   Id                          { get; set; }
    public Guid   AdvanceShippingNoticeId     { get; set; }
    public Guid   OrganisationId              { get; set; }

    public string PackageId                   { get; set; } = string.Empty;
    public string? Sscc                       { get; set; }

    // Navigation
    public AdvanceShippingNoticeEntity? Asn   { get; set; }
    public List<AsnPackageLineEntity>   Lines { get; set; } = new();
}
