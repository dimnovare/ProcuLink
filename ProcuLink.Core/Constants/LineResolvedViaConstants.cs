namespace ProcuLink.Core.Constants;

/// <summary>
/// Where a line's <c>SupplierItemCode</c> came from (<c>PurchaseOrderLineEntity.ResolvedVia</c>).
/// Null means unresolved, or a path that records no origin (invoices). Only a saved-mapping hit may
/// be shown to a person as "learned from a previous order".
/// </summary>
public static class LineResolvedViaConstants
{
    public const string SavedMapping = "saved_mapping";
    public const string Manual = "manual";
    public const string Ai = "ai";
}
