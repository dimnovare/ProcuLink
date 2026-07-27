namespace ProcuLink.Core.Entities;

public class Supplier
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>Soft-delete timestamp. Null = active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Optional code used by the sample-order path to mark the hidden <c>__sample__</c> supplier; null for real suppliers.</summary>
    public string? Code { get; set; }

    /// <summary>True for the hidden sample supplier created by the sample-order onboarding path.</summary>
    public bool IsSample { get; set; }

    // ── Identity (2026-07-25, founder ruling D1) ──────────────────────────────
    // A document carries who sent it — a VAT number, a registry code, an EDI/GLN address, a
    // sender domain. Until these existed there was nothing on OUR side to compare any of that
    // to, so supplier auto-detect could only ever match on a name. All optional: an org that
    // never fills them in loses the identity signals and keeps everything else.
    // Surfaced through the supplier API contract; the profile form for them is a separate FE chip.

    /// <summary>VAT / tax number, as the supplier writes it. Compared after normalisation, never raw.</summary>
    public string? VatNumber { get; set; }

    /// <summary>Company registration / registry code. Distinct from <see cref="VatNumber"/>, which some countries derive from it.</summary>
    public string? RegistrationNumber { get; set; }

    /// <summary>
    /// EDI routing identifier — a GLN, an ILN, a Peppol participant id, or a scheme-specific
    /// party code. One free-text field rather than one per scheme: the schemes are open-ended and
    /// the detector only ever asks "is this the same string?".
    /// </summary>
    public string? EdiCode { get; set; }

    /// <summary>
    /// Primary email domain this supplier sends from (e.g. "acme.com"), without scheme or "www.".
    /// Matched against the domain of an inbound order's sender.
    /// </summary>
    public string? PrimaryDomain { get; set; }

    // Navigation
    public Organisation Organisation { get; set; } = null!;
    public List<SupplierProfileEntity> SupplierProfiles { get; set; } = new();
    public List<PurchaseOrderEntity> PurchaseOrders { get; set; } = new();
    public List<ItemMapping> ItemMappings { get; set; } = new();
    public List<SupplierPoMapping> PoMappings { get; set; } = new();
    public List<SupplierDeliveryConfig> DeliveryConfigs { get; set; } = new();

    /// <summary>The supplier's product catalog — the authoritative set of real codes (optional).</summary>
    public List<SupplierProduct> Products { get; set; } = new();
}
