namespace ProcuLink.Transform.Output;

/// <summary>
/// Peppol BIS Billing 3.0 party details that are NOT part of the canonical
/// <see cref="ProcuLink.Core.Entities.InvoiceEntity"/> (which has no GLN / VAT /
/// endpoint columns and intentionally gets NO migration in Track A).
///
/// These identifiers (BT-34 seller endpoint, BT-49 buyer endpoint, BT-31/BT-48 VAT,
/// BT-27/BT-44 names) are supplied by the caller — e.g. from org/supplier settings
/// or a future config surface. The generator writes only what is provided here and
/// never fabricates an identifier; whatever is missing is reported by
/// <see cref="PeppolBisValidator"/> so the document's network-readiness is honest.
/// </summary>
public sealed class PeppolPartyOptions
{
    // ── Seller (AccountingSupplierParty) ────────────────────────────────────
    /// <summary>BT-27 — seller legal/trading name.</summary>
    public string? SellerName { get; init; }
    /// <summary>BT-34 — seller electronic address (Peppol participant ID), e.g. a GLN or VAT-based id.</summary>
    public string? SellerEndpointId { get; init; }
    /// <summary>EAS / ISO 6523 scheme code for <see cref="SellerEndpointId"/> (e.g. "0088" GLN, "9930" DE VAT, "0191" EE).</summary>
    public string? SellerEndpointScheme { get; init; }
    /// <summary>BT-31 — seller VAT identifier.</summary>
    public string? SellerVatId { get; init; }

    // ── Buyer (AccountingCustomerParty) ─────────────────────────────────────
    /// <summary>BT-44 — buyer legal/trading name.</summary>
    public string? BuyerName { get; init; }
    /// <summary>BT-49 — buyer electronic address (Peppol participant ID).</summary>
    public string? BuyerEndpointId { get; init; }
    /// <summary>EAS / ISO 6523 scheme code for <see cref="BuyerEndpointId"/>.</summary>
    public string? BuyerEndpointScheme { get; init; }
    /// <summary>BT-48 — buyer VAT identifier.</summary>
    public string? BuyerVatId { get; init; }
}
