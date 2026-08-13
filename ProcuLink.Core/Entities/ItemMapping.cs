namespace ProcuLink.Core.Entities;

public class ItemMapping
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SupplierId { get; set; }
    public string BuyerItemCode { get; set; } = string.Empty;
    public string SupplierItemCode { get; set; } = string.Empty;

    /// <summary>
    /// The model score behind this mapping, 0..1 — or <c>null</c> when nothing scored it, which
    /// is the case for every hand-typed and every bulk-imported mapping.
    /// </summary>
    /// <remarks>
    /// This was a non-nullable float written as <c>source == MappingSource.Manual ? 1.0f : 0.8f</c>
    /// — a two-valued literal under a column the supplier screen headed <b>Confidence</b>. A code
    /// an operator typed by hand rendered a green <b>100%</b>; a code loaded from their CSV import
    /// rendered a flat amber <b>80%</b>. Neither number measured anything: no model ever ran on
    /// either path, and because the column was non-nullable the screen's own "Not scored" branch
    /// could never be reached for live data.
    /// <para>Null is now the normal value, and <see cref="Source"/> is what says how the mapping
    /// got here. A number appears only when a real one exists — when a reviewer accepted an AI
    /// suggestion verbatim, the model's own confidence is carried in here rather than discarded.
    /// Never populate this with a placeholder to avoid a null.</para>
    /// </remarks>
    public float? Confidence { get; set; }

    /// <summary>manual | imported | suggested</summary>
    public string Source { get; set; } = "manual";

    /// <summary>
    /// Number of times this mapping has been saved or reaffirmed via manual
    /// resolution (UpsertAsync). Not incremented by automatic resolution.
    /// </summary>
    public int AppliedCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
}
