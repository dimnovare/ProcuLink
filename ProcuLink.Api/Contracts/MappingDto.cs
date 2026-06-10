namespace ProcuLink.Api.Contracts;

/// <summary>
/// Request to create or update a buyer-to-supplier item code mapping
/// </summary>
public class CreateMappingRequest
{
    public string BuyerItemCode { get; set; } = string.Empty;
    public string SupplierItemCode { get; set; } = string.Empty;
}

/// <summary>
/// Request to resolve missing supplier item codes on a purchase order.
/// Optionally carries corrected header fields (order date / buyer name / currency /
/// PO number / supplier name) so the review screen can persist those edits through the
/// same endpoint.
///
/// <para>
/// PO number and the document/display supplier name ARE editable (the founder
/// "PO number cannot be edited" bug). What stays read-only is which supplier the order
/// ROUTES to — that is controlled by the supplier picker, never by this body. Editing
/// <c>SupplierName</c> changes only the as-printed display value (the
/// <c>supplier_name</c> column), not <c>SupplierId</c>.
/// </para>
/// </summary>
public class ResolveRequest
{
    /// <summary>
    /// If true, save the resolutions as mappings for future auto-apply
    /// </summary>
    public bool SaveMappings { get; set; }

    /// <summary>
    /// List of line resolutions to apply
    /// </summary>
    public List<LineResolution> LineResolutions { get; set; } = new();

    /// <summary>
    /// Optional corrected order date, ISO <c>yyyy-MM-dd</c>. Null = no change.
    /// Must parse as a date (else 400).
    /// </summary>
    public string? OrderDate { get; set; }

    /// <summary>
    /// Optional corrected buyer name. Null or whitespace-only = no change.
    /// Trimmed before persisting. Written to BOTH the denormalised
    /// <c>buyer_name</c> column AND canonical_json so the read path stays consistent.
    /// </summary>
    public string? BuyerName { get; set; }

    /// <summary>
    /// Optional corrected currency, a 3-letter ISO alpha code (e.g. "EUR").
    /// Null = no change. Must be exactly 3 alpha characters (else 400);
    /// stored upper-cased.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Optional corrected purchase-order number. Null or whitespace-only = no change.
    /// Trimmed before persisting. Written to BOTH the <c>po_number</c> column AND
    /// canonical_json (the <c>poNumber</c> key) so the read and transform paths agree.
    /// </summary>
    public string? PoNumber { get; set; }

    /// <summary>
    /// Optional corrected document/display supplier name. Null or whitespace-only = no
    /// change. Trimmed before persisting. Written to the <c>supplier_name</c> column AND
    /// canonical_json. Does NOT change order routing — <c>SupplierId</c> is untouched.
    /// </summary>
    public string? SupplierName { get; set; }
}

/// <summary>
/// Resolution for a single line item
/// </summary>
public class LineResolution
{
    public int LineNumber { get; set; }
    public string SupplierItemCode { get; set; } = string.Empty;
}
