namespace ProcuLink.Core.Services;

/// <summary>
/// A single buyer→supplier code resolution submitted by the user in the review UI.
/// </summary>
public record LineResolution(int LineNumber, string SupplierItemCode);

/// <summary>
/// Optional header-field corrections submitted alongside line resolutions from the
/// review screen. Every field is nullable and means "no change" when null —
/// each is applied independently.
///
/// <para>
/// PO number and the document/display supplier name ARE editable here (the founder
/// "PO number cannot be edited" bug). What is NOT editable is which supplier the order
/// ROUTES to — that stays controlled by the supplier picker (<c>SupplierId</c>), never
/// by this header edit. <see cref="SupplierName"/> is the as-printed / display value
/// (the <c>purchase_orders.supplier_name</c> column), distinct from the resolved
/// <c>Supplier.Name</c> used for routing.
/// </para>
/// </summary>
/// <param name="OrderDate">
/// Corrected order date, or null for no change. The controller validates/parses the
/// raw ISO string and passes a parsed <see cref="DateOnly"/> through.
/// </param>
/// <param name="BuyerName">
/// Corrected, already-trimmed buyer name, or null for no change. The controller
/// treats whitespace-only input as no-change (passes null).
/// </param>
/// <param name="Currency">
/// Corrected currency as a validated, upper-cased 3-letter alpha code, or null for
/// no change.
/// </param>
/// <param name="PoNumber">
/// Corrected, already-trimmed purchase-order number, or null for no change. The
/// controller treats whitespace-only input as no-change (passes null). Written to BOTH
/// the <c>po_number</c> column AND canonical_json (the <c>poNumber</c> key) so the read
/// and transform paths stay consistent.
/// </param>
/// <param name="SupplierName">
/// Corrected, already-trimmed document/display supplier name, or null for no change.
/// Written to the <c>supplier_name</c> column AND canonical_json. Does NOT change order
/// routing — <c>SupplierId</c> is untouched.
/// </param>
public record ResolveHeaderFields(
    DateOnly? OrderDate    = null,
    string?   BuyerName    = null,
    string?   Currency     = null,
    string?   PoNumber     = null,
    string?   SupplierName = null)
{
    /// <summary>True when at least one header field carries a change.</summary>
    public bool HasAnyChange => OrderDate.HasValue
        || !string.IsNullOrWhiteSpace(BuyerName)
        || !string.IsNullOrWhiteSpace(Currency)
        || !string.IsNullOrWhiteSpace(PoNumber)
        || !string.IsNullOrWhiteSpace(SupplierName);
}
