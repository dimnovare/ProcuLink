namespace ProcuLink.Core.Entities;

/// <summary>The billed units of a document that was erased on request, kept so usage still counts it.</summary>
public class ErasedDocumentUnit
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public DateTime ReadAt { get; set; }
    public int BillingUnits { get; set; }
}
