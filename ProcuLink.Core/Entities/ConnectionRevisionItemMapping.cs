namespace ProcuLink.Core.Entities;

/// <summary>
/// A product-code mapping snapshotted into a <see cref="SupplierConnectionRevision"/>
/// (Group V1). Mirrors the fields of the loose <see cref="ItemMapping"/> row that was
/// current at draft-creation / backfill, so a published revision reproduces the exact
/// buyer→supplier code resolution it was published with — even if the live
/// <see cref="ItemMapping"/> rows are later edited.
/// </summary>
public class ConnectionRevisionItemMapping
{
    public Guid Id { get; set; }
    public Guid RevisionId { get; set; }
    public string BuyerItemCode { get; set; } = string.Empty;
    public string SupplierItemCode { get; set; } = string.Empty;
    /// <summary>Snapshot of ItemMapping.Confidence — a model score 0..1, or null when nothing
    /// scored the mapping this revision copied. Nullable for the same reason the live column is:
    /// defaulting an unscored mapping to a number here would reintroduce the fiction one layer on.</summary>
    public float? Confidence { get; set; }

    /// <summary>manual | imported | suggested (copied from the source ItemMapping).</summary>
    public string Source { get; set; } = "manual";

    public SupplierConnectionRevision Revision { get; set; } = null!;
}
