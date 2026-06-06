namespace ProcuLink.Core.Entities;

/// <summary>
/// Per-organisation presentation flag for the primary order flow. The data model
/// is direction-agnostic (PurchaseOrderEntity stores BuyerName = issuer,
/// SupplierName = recipient); this flag only changes how the product frames that
/// flow for the org. It does NOT affect delivery routing, parsing, or the Supplier
/// entity.
/// </summary>
public enum OrderDirection
{
    /// <summary>We are the buyer sending POs out to suppliers (current/default behaviour).</summary>
    Outbound = 0,

    /// <summary>We are the supplier receiving customer POs.</summary>
    Inbound = 1,
}
