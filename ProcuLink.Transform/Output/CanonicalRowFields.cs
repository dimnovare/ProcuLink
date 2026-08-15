using ProcuLink.Core.Entities;

namespace ProcuLink.Transform.Output;

/// <summary>
/// The DECLARED canonical field names a custom output may bind, and — just as importantly — the
/// explicit, reasoned list of entity properties that are deliberately NOT bindable.
///
/// <para><b>Why this type exists.</b> Before WP-14 the bindable set lived nowhere but inside the
/// dictionary literals in <see cref="MappedTransformService.BuildHeaderRow"/> /
/// <see cref="MappedTransformService.BuildLineRow"/>. It had drifted to 10 header + 11 line names
/// while <see cref="PurchaseOrderEntity"/> carried 33 business fields and
/// <see cref="PurchaseOrderLineEntity"/> 19 — so no ShipTo, BillTo, Contact, Incoterms, BuyerTaxId,
/// ManufacturerPartNumber, Unspsc, DiscountPercent or TaxAmount could be emitted by ANY custom
/// output. For a physical purchase order ship-to is not optional, so an operator simply could not
/// build a supplier's required file. Nothing failed loudly; the names were just absent.</para>
///
/// <para><b>The regression guard.</b> <c>CanonicalRowFieldsCompletenessTests</c> reflects over both
/// entity types and fails when a property has NEITHER a row key NOR an entry in the exclusion
/// dictionaries below. A new column therefore cannot be added to the data model without a
/// deliberate, written decision about whether an output can bind it. The exclusion dictionaries
/// carry a REASON per field, not a bare name — a bare name would let "we forgot" masquerade as
/// "we decided".</para>
///
/// <para><b>Naming rule.</b> A row key is spelled EXACTLY like the entity property it surfaces.
/// The completeness test depends on that identity, so a key that renames its field would read as a
/// missing field and fail. <see cref="DerivedLineKeys"/> is the sole set of keys with no backing
/// property.</para>
///
/// <para><b>This list is DECLARED, not derived.</b> It is written independently of the row
/// builders on purpose: <c>CanonicalRowFieldsCompletenessTests</c> asserts the two agree exactly,
/// so a change to either one alone fails. Deriving one from the other would make that assertion
/// vacuous.</para>
///
/// <para>Mirrored in the frontend picker at <c>src/lib/api/types.ts</c>
/// (<c>CANONICAL_HEADER_FIELDS</c> / <c>CANONICAL_LINE_FIELDS</c>), which is locked by its own
/// test against the same names.</para>
/// </summary>
public static class CanonicalRowFields
{
    /// <summary>
    /// Header-scope keys present in every header row bag, in emission order. Each name is a
    /// <see cref="PurchaseOrderEntity"/> property; a null/absent value renders as an empty string,
    /// never as a literal "null".
    /// </summary>
    public static readonly IReadOnlyList<string> Header = new[]
    {
        // ── identity + document basics ──
        "PoNumber",
        "OrderDate",
        "BuyerName",
        "Currency",
        "SupplierName",
        // ── money (stated value preferred, derived when the parser stated none) ──
        "SubTotal",
        "TaxTotal",
        "GrandTotal",
        "PaymentTerms",
        "RequestedDeliveryDate",
        // ── WP-14: buyer identity + references ──
        "BuyerOrderRef",
        "BuyerTaxId",
        // ── WP-14: ordering contact ──
        "ContactName",
        "ContactEmail",
        "ContactPhone",
        // ── WP-14: shipping terms ──
        "Incoterms",
        "ShippingMethod",
        // ── WP-14: ship-to address block ──
        "ShipToName",
        "ShipToDeliverTo",
        "ShipToStreet",
        "ShipToCity",
        "ShipToPostalCode",
        "ShipToCountry",
        "ShipToEmail",
        "ShipToPhone",
        // ── WP-14: bill-to address block ──
        "BillToName",
        "BillToDeliverTo",
        "BillToStreet",
        "BillToCity",
        "BillToPostalCode",
        "BillToCountry",
        "BillToEmail",
        "BillToPhone",
    };

    /// <summary>
    /// Line-scope keys added on top of <see cref="Header"/> for a line row bag (a line bag carries
    /// the header keys too, so a line rule can reference order-level values). Every name except
    /// those in <see cref="DerivedLineKeys"/> is a <see cref="PurchaseOrderLineEntity"/> property.
    /// </summary>
    public static readonly IReadOnlyList<string> Line = new[]
    {
        // ── identity + quantities ──
        "LineNumber",
        "BuyerItemCode",
        "SupplierItemCode",
        "Description",
        "Quantity",
        "Unit",
        "UnitPrice",
        "LineTotal",
        // ── money ──
        "LineAmount",
        "TaxRate",
        "DeliveryDate",
        // ── WP-14: per-line tax + discount + net ──
        "TaxAmount",
        "DiscountPercent",
        "NetAmount",
        // ── WP-14: product identity ──
        "ManufacturerPartNumber",
        "ManufacturerName",
        "CustomerPartNumber",
        "Unspsc",
        // ── WP-14: line-level routing / contract references ──
        "Recipient",
        "ContractNumber",
    };

    /// <summary>
    /// Line keys that are COMPUTED rather than read from a column, so the completeness test does
    /// not look for a backing property. <c>LineTotal</c> is quantity × unit price, overflow-guarded;
    /// it predates WP-14 and is kept because outputs already bind it.
    /// </summary>
    public static readonly IReadOnlySet<string> DerivedLineKeys =
        new HashSet<string>(StringComparer.Ordinal) { "LineTotal" };

    /// <summary>
    /// <see cref="PurchaseOrderEntity"/> properties deliberately NOT bindable by a custom output,
    /// each with the reason. Infrastructure, tenancy, audit, pipeline state and navigation — never
    /// a value a counterparty's document should carry.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ExcludedHeaderFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Id"] =
                "Infrastructure: internal surrogate key. Meaningless to a counterparty and a "
                + "tenant-correlatable identifier we do not put in outbound documents.",
            ["OrgId"] =
                "Tenancy: the owning organisation. Emitting it would leak our tenant boundary into "
                + "a supplier's file.",
            ["SupplierId"] =
                "Infrastructure: internal FK to the resolved supplier. The supplier's own name is "
                + "bindable as SupplierName; the surrogate id is not addressable by anyone else.",
            ["Status"] =
                "Pipeline state (pending_parse/parsing/ready/delivering/...). ProcuLink-internal "
                + "vocabulary describing OUR processing, not a property of the purchase order.",
            ["SourceFileKey"] =
                "Storage: the R2 object key of the uploaded source file. An internal blob path.",
            ["CanonicalJson"] =
                "Infrastructure: the whole parsed document as a JSON blob, not a scalar field. The "
                + "individual values it holds are surfaced as their own typed columns/keys.",
            ["CreatedAt"] =
                "Audit timestamp: when WE ingested the row. Not the order's date — OrderDate is.",
            ["UpdatedAt"] =
                "Audit timestamp: when WE last touched the row.",
            ["IsSample"] =
                "Internal flag marking the onboarding sample order (excluded from billing quota).",
            ["RequeueCount"] =
                "Internal parse/transform re-drive counter used by the stuck-order sweep.",
            ["DeliveryRequeueCount"] =
                "Internal delivery-phase re-drive counter used by the stuck-delivery sweep.",
            ["PoNumberNormalized"] =
                "Internal comparison key for duplicate-PO-number detection: PoNumber trimmed and "
                + "upper-cased, or null when the order carries only a minted placeholder. The "
                + "supplier-facing value is PoNumber, which is bindable and keeps its original "
                + "casing; emitting the folded copy would put a value in the document that the "
                + "buyer never wrote, and null-for-placeholder is a ProcuLink processing fact, "
                + "not a property of the purchase order.",
            ["SchemaFingerprintHash"] =
                "Internal column-layout hash powering fingerprint learning and the idempotency "
                + "guard for parse-time fingerprinting.",
            ["InboundSenderDomain"] =
                "Privacy-governed routing evidence (founder ruling D2): the domain an inbound email "
                + "arrived from, scrubbed after 12 months. It is evidence about how the order "
                + "reached us, never a field of the order — and it must not be copied into a "
                + "document that outlives the retention clock.",
            ["InboundSenderDomainCapturedAt"] =
                "The retention clock for InboundSenderDomain; bookkeeping for the scrub sweep.",
            ["DeliveryDueAt"] =
                "Internal SLA timer: when OUR delivery must be confirmed. Not the buyer's requested "
                + "date — RequestedDeliveryDate is that, and it is bindable.",
            ["SlaBreached"] =
                "Internal SLA flag set by our own sweep. An operator signal, not document content.",
            ["HeldFromStatus"] =
                "Internal billing-hold bookkeeping: the status to restore when a hold is released. "
                + "Stale by design outside the delivery_held status.",
            ["DocumentType"] =
                "The AI extractor's classification (purchase_order/invoice/other) used to flag a "
                + "misrouted document for review. ProcuLink-internal vocabulary and a MACHINE GUESS "
                + "— emitting it would put an unvalidated snake_case token into a supplier's file. "
                + "Output formats that need a document-type code derive it from the format itself.",
            ["SourceFilePurgedAt"] =
                "Retention bookkeeping: when the source blob was deleted per the org's policy.",
            ["ConnectionRevisionId"] =
                "Infrastructure: the pinned SupplierConnectionRevision that drove this order, for "
                + "reproducibility and replay. An internal version pointer.",

            // ── navigation properties ──
            ["Organisation"] =
                "Navigation property (not a scalar). Tenancy object.",
            ["Supplier"] =
                "Navigation property (not a scalar). Its Name is surfaced through the SupplierName "
                + "key, which prefers the resolved supplier over the printed one.",
            ["Lines"] =
                "Navigation property (not a scalar). Line values are bindable in LINE scope — see "
                + "the Line list.",
            ["OutboundArtifacts"] =
                "Navigation property (not a scalar). The documents WE produced from this order.",
            ["DeliveryAttempts"] =
                "Navigation property (not a scalar). Our delivery audit trail.",
            ["Parties"] =
                "Navigation property (not a scalar). The ship-to/bill-to party rows are denormalised "
                + "onto the flat ShipTo*/BillTo* columns, which ARE bindable.",
            ["SourceCapture"] =
                "Navigation property (not a scalar). Raw ingest capture for replay/provenance.",
        };

    /// <summary>
    /// <see cref="PurchaseOrderLineEntity"/> properties deliberately NOT bindable by a custom
    /// output, each with the reason. Review state and AI-suggestion scratch space — all of it
    /// describes OUR confidence in the line, not the line.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ExcludedLineFields =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Id"] =
                "Infrastructure: internal surrogate key.",
            ["OrderId"] =
                "Infrastructure: internal FK to the parent order.",
            ["Confidence"] =
                "Parse/AI confidence score (0..1) for this line's resolution. Describes how sure WE "
                + "are, not what the line says. A supplier reading 0.82 in a PO learns nothing true.",
            ["NeedsReview"] =
                "Internal review flag. An order with any line still flagged is BLOCKED from "
                + "delivery, so a delivered document can never legitimately carry it.",
            ["ReviewReason"] =
                "Internal operator-facing explanation of why the line was flagged. Cleared when a "
                + "human resolves the line.",
            ["AiSuggestedSupplierItemCode"] =
                "Transient AI suggestion awaiting a human decision; nulled the moment the line is "
                + "resolved. Emitting a SUGGESTION as if it were the resolved code is exactly the "
                + "wrong-code-to-the-supplier failure the review step exists to prevent.",
            ["AiSuggestionConfidence"] =
                "Confidence attached to the transient AI suggestion above; nulled on resolve.",
            ["AiSuggestionReason"] =
                "Free-text rationale for the transient AI suggestion; nulled on resolve.",
            ["AiSuggestionProvenance"] =
                "Which evidence produced the transient AI suggestion (catalog/mapping/web); nulled "
                + "on resolve.",

            // ── navigation properties ──
            ["Order"] =
                "Navigation property (not a scalar). Header values are already merged into every "
                + "line bag, so a line rule can bind them directly.",
        };
}
