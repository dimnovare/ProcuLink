namespace ProcuLink.Core.Entities;

/// <summary>
/// Ledger of a file, message or request already taken in. The unique <c>(ScopeId, ItemKey)</c> index is
/// the guarantee that the same item becomes one document. <see cref="ScopeId"/> is an intake id, or a flow
/// id for the file API; it has no foreign key.
/// </summary>
public class FlowIntakeItem
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid ScopeId { get; set; }
    public string ItemKey { get; set; } = "";
    /// <summary>Null while claimed and unfinished; <c>Guid.Empty</c> when the engine refused the item.</summary>
    public Guid? DocumentId { get; set; }
    public DateTime CreatedAt { get; set; }
}
