using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a valid cXML 1.2.024 PurchaseOrder document from a fully-resolved order entity.
///
/// Output format:
/// <code>
/// &lt;?xml version="1.0" encoding="UTF-8"?&gt;
/// &lt;cXML payloadID="{guid}@proculink" timestamp="{ISO-8601}" xml:lang="en-US"&gt;
///   &lt;Header&gt;
///     &lt;From&gt;&lt;Credential domain="OrgId"&gt;&lt;Identity&gt;{orgId}&lt;/Identity&gt;&lt;/Credential&gt;&lt;/From&gt;
///     &lt;To&gt;&lt;Credential domain="SupplierId"&gt;&lt;Identity&gt;{supplierId}&lt;/Identity&gt;&lt;/Credential&gt;&lt;/To&gt;
///     &lt;Sender&gt;&lt;Credential domain="NetworkUserId"&gt;&lt;Identity&gt;proculink&lt;/Identity&gt;&lt;/Credential&gt;&lt;UserAgent&gt;ProcuLink/1.0&lt;/UserAgent&gt;&lt;/Sender&gt;
///   &lt;/Header&gt;
///   &lt;Request deploymentMode="production"&gt;
///     &lt;OrderRequest&gt;
///       &lt;OrderRequestHeader orderID="{poNumber}" orderDate="{orderDate}" type="new"&gt;
///         &lt;Total&gt;&lt;Money currency="{currency}"&gt;{total}&lt;/Money&gt;&lt;/Total&gt;
///       &lt;/OrderRequestHeader&gt;
///       &lt;ItemOut quantity="{qty}" lineNumber="{n}"&gt;
///         &lt;ItemID&gt;&lt;SupplierPartID&gt;{supplierItemCode}&lt;/SupplierPartID&gt;&lt;/ItemID&gt;
///         &lt;ItemDetail&gt;
///           &lt;UnitPrice&gt;&lt;Money currency="{currency}"&gt;{unitPrice}&lt;/Money&gt;&lt;/UnitPrice&gt;
///           &lt;Description xml:lang="en"&gt;{description}&lt;/Description&gt;
///           &lt;UnitOfMeasure&gt;{unit}&lt;/UnitOfMeasure&gt;
///         &lt;/ItemDetail&gt;
///       &lt;/ItemOut&gt;
///     &lt;/OrderRequest&gt;
///   &lt;/Request&gt;
/// &lt;/cXML&gt;
/// </code>
///
/// Requires <see cref="BillingFeature.Cxml"/>; enforcement is at the controller/service level.
///
/// <para><b>Network credentials:</b> the <c>From</c> / <c>To</c> / <c>Sender</c> credentials are
/// configurable per supplier connection via <paramref name="cxmlCredentials"/>. When a credential
/// is configured the Header carries the supplier's REAL cXML network identity (e.g. a Coupa
/// <c>NetworkId</c> such as <c>REDACTED-NETWORK-ID</c> / <c>REDACTED-NETWORK-ID</c>) plus, for the Sender, a
/// <c>&lt;SharedSecret&gt;</c>. When it is not configured (null config or a blank identity) the
/// credential falls back to the legacy GUID identity shown above, so an unconfigured supplier is
/// byte-identical to the pre-feature output.</para>
/// </summary>
public sealed class CxmlTransformService : ITransformService
{
    private static readonly XNamespace Xml = "http://www.w3.org/XML/1998/namespace";

    /// <summary>Default cXML credential domain used when an operator configures an identity but
    /// leaves the domain blank — <c>NetworkId</c> is the most common real-world cXML domain.</summary>
    private const string DefaultConfiguredDomain = "NetworkId";

    public bool CanTransform(OutputFormat format) => format == OutputFormat.CXml;

    /// <summary>
    /// WS-12 overload: drive the From/To/Sender party identity from a per-connection
    /// <see cref="EnvelopeConfig"/> (its <see cref="CxmlEnvelope"/>). The envelope is mapped onto
    /// the existing <see cref="CxmlCredentialConfig"/> path, so it reuses the same per-credential
    /// fallback machinery: a null envelope (or null <see cref="EnvelopeConfig.Cxml"/>) yields the
    /// legacy <c>OrgId</c>/<c>SupplierId</c>/<c>NetworkUserId</c> GUID identities, BYTE-FOR-BYTE
    /// identical to the pre-WS-12 output. The shared secret is not part of the envelope identity
    /// (it is a credential reference, never inline) so no <c>&lt;SharedSecret&gt;</c> is emitted on
    /// this path.
    /// </summary>
    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct,
        EnvelopeConfig? envelope)
        => TransformAsync(order, format, ct, ToCredentialConfig(envelope?.Cxml));

    /// <summary>
    /// Maps the <see cref="CxmlEnvelope"/> identity (From/To/Sender domain+identity) onto the
    /// transform's <see cref="CxmlCredentialConfig"/>. Returns null when the envelope is absent so
    /// the legacy GUID-identity path stays byte-identical. The shared secret is never carried on the
    /// envelope, so <see cref="CxmlCredentialConfig.SenderSharedSecret"/> is always null here.
    /// </summary>
    private static CxmlCredentialConfig? ToCredentialConfig(CxmlEnvelope? env)
    {
        if (env is null) return null;
        return new CxmlCredentialConfig(
            FromDomain:         env.FromDomain,
            FromIdentity:       env.FromIdentity,
            ToDomain:           env.ToDomain,
            ToIdentity:         env.ToIdentity,
            SenderDomain:       env.SenderDomain,
            SenderIdentity:     env.SenderIdentity,
            SenderSharedSecret: null)
        {
            // Carry the per-revision DTD onto the credential path so the envelope-only call emits the
            // configured DOCTYPE. Null/blank → no DOCTYPE (byte-identical).
            DtdSystemId = env.DtdSystemId,
            DtdPublicId = env.DtdPublicId,
        };
    }

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct,
        CxmlCredentialConfig? cxmlCredentials = null)
    {
        // Existing review guard + format-required-field checks. cXML carries the line
        // code in an OPTIONAL SupplierPartID element, so a missing code is not a hard
        // structural failure (the supplier-item-code / review guard still covers it);
        // a missing / zero unit price is flagged so a €0 document never delivers blind.
        OutputFieldValidator.ValidateEntity(order, format);

        var payloadId = $"{Guid.NewGuid():N}@proculink";
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var currency  = string.IsNullOrWhiteSpace(order.Currency) ? "EUR" : order.Currency;

        var totalAmount = order.Lines.Sum(l => l.Quantity * l.UnitPrice)
                              .ToString("F2", CultureInfo.InvariantCulture);

        // ── Configurable DOCTYPE (T7) ─────────────────────────────────────────
        // When the supplier configured a cXML DTD, prepend an <!DOCTYPE cXML …> node BEFORE the root
        // (XDocument.ToString — the serialization path used at the bottom — renders it after the
        // <?xml?> declaration, verified by characterization tests). A null/blank DtdSystemId yields NO
        // DocumentType node, so an unconfigured supplier is BYTE-IDENTICAL to the pre-feature output.
        // PUBLIC form when a public id is also set; SYSTEM form when only the system id is set.
        var docType = BuildDocumentType(cxmlCredentials);

        var rootElement =
            new XElement("cXML",
                new XAttribute("payloadID", payloadId),
                new XAttribute("timestamp",  timestamp),
                new XAttribute(Xml + "lang", "en-US"),

                // ── Header ────────────────────────────────────────────────
                // From / To / Sender credentials come from the supplier's configured cXML
                // network credentials when present, falling back per-credential to the legacy
                // OrgId / SupplierId / NetworkUserId GUID identities (byte-identical when unset).
                new XElement("Header",
                    BuildFrom(order, cxmlCredentials),
                    BuildCredentialBlock("To",
                        cxmlCredentials?.ToDomain, cxmlCredentials?.ToIdentity,
                        legacyDomain: "SupplierId", legacyIdentity: order.SupplierId.ToString()),
                    BuildSender(cxmlCredentials)
                ),

                // ── Request ───────────────────────────────────────────────
                new XElement("Request",
                    new XAttribute("deploymentMode", "production"),

                    new XElement("OrderRequest",

                        // OrderRequestHeader. orderDate is an ISO-8601 dateTime (a DateOnly renders
                        // at midnight); orderVersion="1" is constant for a freshly-issued order.
                        // ShipTo/BillTo/Contact follow <Total> and are null-gated — when the order
                        // carries no address data the helpers return null (ignored by XElement),
                        // keeping the header byte-identical to the pre-feature output.
                        new XElement("OrderRequestHeader",
                            new XAttribute("orderID",     order.PoNumber),
                            new XAttribute("orderDate",
                                order.OrderDate.ToDateTime(TimeOnly.MinValue)
                                    .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                            new XAttribute("type",        "new"),
                            new XAttribute("orderVersion", "1"),
                            new XElement("Total",
                                new XElement("Money",
                                    new XAttribute("currency", currency),
                                    totalAmount)),
                            BuildShipTo(order),
                            BuildBillTo(order),
                            BuildContact(order)),

                        // One ItemOut per line
                        order.Lines
                             .OrderBy(l => l.LineNumber)
                             .Select(l => BuildItemOut(l, currency))
                    )
                )
            );

        // Assemble the document: declaration, then the OPTIONAL DOCTYPE node (null → omitted), then
        // the root. Passing a null content item to XDocument is ignored, so an unconfigured supplier
        // produces the exact same node set — and bytes — as before.
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            docType,
            rootElement);

        var bytes  = Encoding.UTF8.GetBytes(doc.Declaration + Environment.NewLine + doc.ToString());
        var stream = new MemoryStream(bytes);

        return Task.FromResult(new TransformResult(
            Content:       stream,
            ContentType:   "application/xml",
            FileExtension: ".cxml"
        ));
    }

    // ── DOCTYPE (T7) ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the optional cXML <c>&lt;!DOCTYPE&gt;</c> node from the configured DTD identifiers, or
    /// returns null when no DTD is configured (→ no DocumentType node → byte-identical output).
    /// A blank <see cref="CxmlCredentialConfig.DtdSystemId"/> is the "unset" signal — the public id
    /// alone never emits a DOCTYPE. Values are trimmed. SYSTEM form when only the system id is set;
    /// PUBLIC form when a public id is also set (XLinq renders PUBLIC iff a non-null public id is
    /// supplied).
    /// </summary>
    private static XDocumentType? BuildDocumentType(CxmlCredentialConfig? cfg)
    {
        var systemId = cfg?.DtdSystemId?.Trim();
        if (string.IsNullOrWhiteSpace(systemId))
            return null;

        var publicId = cfg?.DtdPublicId?.Trim();
        publicId = string.IsNullOrWhiteSpace(publicId) ? null : publicId;

        // DEFENSIVE (must never strand an order): the external identifiers are written VERBATIM into
        // the `<!DOCTYPE cXML …>` declaration — XLinq does NOT escape them. A value carrying a quote,
        // an angle bracket, or a control/newline character would close the literal early (malformed,
        // unparseable cXML) or throw at serialization, which OrderTransformService's cXML branch does
        // not catch → the order strands in `transforming`. A real DTD URI / public id never contains
        // these; reject them at config-save time AND, as the backstop here, SKIP the DOCTYPE (deliver
        // valid cXML without it) rather than emit a broken one or throw.
        if (!IsValidDtdExternalId(systemId) || (publicId is not null && !IsValidDtdExternalId(publicId)))
            return null;

        return new XDocumentType("cXML", publicId, systemId, null);
    }

    /// <summary>
    /// True when <paramref name="value"/> is safe to write verbatim into a <c>&lt;!DOCTYPE&gt;</c>
    /// external identifier. An XML SystemLiteral cannot contain its delimiter quote and a PubidLiteral
    /// has an even tighter set; we conservatively reject BOTH quote styles, angle brackets, and any
    /// control/newline character — the only ways a free-text DTD value could break (or throw on) the
    /// DOCTYPE declaration. A legitimate DTD URI / FPI never contains these.
    /// </summary>
    private static bool IsValidDtdExternalId(string value) =>
        !value.Any(c => c is '"' or '\'' or '<' or '>' || char.IsControl(c));

    // ── Header credential helpers ─────────────────────────────────────────────

    /// <summary>
    /// Builds the <c>&lt;From&gt;</c> credential. The cXML <c>From/Identity</c> is the BUYER's
    /// identity, so when the order carries a <see cref="PurchaseOrderEntity.BuyerTaxId"/> it becomes
    /// the From identity — KEEPING the configured From domain (or defaulting to
    /// <see cref="DefaultConfiguredDomain"/> when none is configured). This stops a PO from one buyer
    /// inheriting the supplier connection's configured From VatNr (the Gjensidige/REDACTED-PARTY bug). When
    /// there is NO buyer tax id the result is byte-identical to the pre-feature output: the configured
    /// From credential when set, else the legacy <c>OrgId</c>/orgId GUID identity.
    /// </summary>
    private static XElement BuildFrom(PurchaseOrderEntity order, CxmlCredentialConfig? cfg)
    {
        var buyerTaxId = order.BuyerTaxId?.Trim();
        if (string.IsNullOrWhiteSpace(buyerTaxId))
        {
            // Unchanged legacy/configured path — byte-identical to before.
            return BuildCredentialBlock("From",
                cfg?.FromDomain, cfg?.FromIdentity,
                legacyDomain: "OrgId", legacyIdentity: order.OrgId.ToString());
        }

        // Buyer tax id wins as the From identity; keep the configured From domain, defaulting to
        // NetworkId (a tax id is a network identity, never the internal OrgId domain).
        var domain = string.IsNullOrWhiteSpace(cfg?.FromDomain) ? DefaultConfiguredDomain : cfg!.FromDomain.Trim();
        return new XElement("From",
            new XElement("Credential",
                new XAttribute("domain", domain),
                new XElement("Identity", buyerTaxId)));
    }

    /// <summary>
    /// Builds a <c>&lt;From&gt;</c> / <c>&lt;To&gt;</c> block wrapping a single
    /// <c>&lt;Credential domain="…"&gt;&lt;Identity&gt;…&lt;/Identity&gt;&lt;/Credential&gt;</c>,
    /// using the configured domain/identity when an identity is set and the legacy default otherwise.
    /// </summary>
    private static XElement BuildCredentialBlock(
        string wrapper, string? configDomain, string? configIdentity,
        string legacyDomain, string legacyIdentity)
    {
        var (domain, identity) = ResolveCredential(configDomain, configIdentity, legacyDomain, legacyIdentity);
        return new XElement(wrapper,
            new XElement("Credential",
                new XAttribute("domain", domain),
                new XElement("Identity", identity)));
    }

    /// <summary>
    /// Builds the <c>&lt;Sender&gt;</c> block. Beyond the domain/identity resolution shared with
    /// From/To, the Sender carries an optional <c>&lt;SharedSecret&gt;</c> — emitted ONLY when a
    /// secret is configured, so the legacy default sender (no secret) stays byte-identical.
    /// </summary>
    private static XElement BuildSender(CxmlCredentialConfig? cfg)
    {
        var (domain, identity) = ResolveCredential(
            cfg?.SenderDomain, cfg?.SenderIdentity, legacyDomain: "NetworkUserId", legacyIdentity: "proculink");

        var credential = new XElement("Credential",
            new XAttribute("domain", domain),
            new XElement("Identity", identity));

        if (!string.IsNullOrWhiteSpace(cfg?.SenderSharedSecret))
            credential.Add(new XElement("SharedSecret", cfg!.SenderSharedSecret));

        return new XElement("Sender",
            credential,
            new XElement("UserAgent", "ProcuLink/1.0"));
    }

    /// <summary>
    /// Per-credential fallback rule: a CONFIGURED non-blank identity switches the whole credential
    /// to the configured domain + identity (domain defaults to <see cref="DefaultConfiguredDomain"/>
    /// when the operator left it blank). A null/blank configured identity keeps the legacy default
    /// entirely, which is what makes an unconfigured supplier byte-identical to the pre-feature output.
    /// </summary>
    private static (string Domain, string Identity) ResolveCredential(
        string? configDomain, string? configIdentity, string legacyDomain, string legacyIdentity)
    {
        if (string.IsNullOrWhiteSpace(configIdentity))
            return (legacyDomain, legacyIdentity);

        var domain = string.IsNullOrWhiteSpace(configDomain) ? DefaultConfiguredDomain : configDomain.Trim();
        return (domain, configIdentity.Trim());
    }

    // ── Address blocks (ShipTo / BillTo / Contact) ─────────────────────────────

    private static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// <c>&lt;ShipTo&gt;</c> wrapping the ship-to address, or null when there is no ship-to name
    /// (→ no node → byte-identical output for orders without a ship-to address). A null returned
    /// here is ignored by the parent <c>OrderRequestHeader</c> XElement (XLinq drops null content).
    /// Gated on the NAME specifically: the cXML 1.2 DTD declares <c>Name+</c> (required) inside
    /// <c>Address</c>, so a name-less address can never be a valid block — we drop it rather than
    /// emit a DTD-invalid Name-less <c>&lt;Address&gt;</c>. (Realistic POs always carry a ship-to name.)
    /// </summary>
    private static XElement? BuildShipTo(PurchaseOrderEntity o)
    {
        if (IsBlank(o.ShipToName))
            return null;

        return new XElement("ShipTo",
            BuildAddress(o.ShipToName, o.ShipToDeliverTo, o.ShipToStreet, o.ShipToCity,
                o.ShipToPostalCode, o.ShipToCountry, o.ShipToEmail, o.ShipToPhone));
    }

    /// <summary>
    /// <c>&lt;BillTo&gt;</c> wrapping the bill-to address, or null when there is no bill-to name
    /// (→ no node → byte-identical for orders without a bill-to address). Gated on the NAME for the
    /// same DTD reason as <see cref="BuildShipTo"/> (<c>Address</c> requires <c>Name+</c>).
    /// </summary>
    private static XElement? BuildBillTo(PurchaseOrderEntity o)
    {
        if (IsBlank(o.BillToName))
            return null;

        return new XElement("BillTo",
            BuildAddress(o.BillToName, o.BillToDeliverTo, o.BillToStreet, o.BillToCity,
                o.BillToPostalCode, o.BillToCountry, o.BillToEmail, o.BillToPhone));
    }

    /// <summary>
    /// Builds the cXML <c>&lt;Address&gt;</c> sub-tree: a null-gated <c>&lt;Name xml:lang="en"&gt;</c>,
    /// a <c>&lt;PostalAddress&gt;</c> with null-gated DeliverTo/Street/City/PostalCode/Country, an
    /// ALWAYS-emitted <c>&lt;Email&gt;</c> (empty when blank — matches the MapForce reference output's
    /// <c>&lt;Email/&gt;</c>), and a null-gated <c>&lt;Phone&gt;&lt;TelephoneNumber&gt;&lt;Number&gt;</c>.
    /// </summary>
    private static XElement BuildAddress(
        string? name, string? deliverTo, string? street, string? city,
        string? postal, string? country, string? email, string? phone)
    {
        return new XElement("Address",
            IsBlank(name) ? null
                : new XElement("Name", new XAttribute(Xml + "lang", "en"), name),
            new XElement("PostalAddress",
                IsBlank(deliverTo) ? null : new XElement("DeliverTo", deliverTo),
                IsBlank(street)    ? null : new XElement("Street",    street),
                IsBlank(city)      ? null : new XElement("City",      city),
                IsBlank(postal)    ? null : new XElement("PostalCode", postal),
                IsBlank(country)   ? null : new XElement("Country",   country)),
            new XElement("Email", email ?? string.Empty),
            IsBlank(phone) ? null
                : new XElement("Phone",
                    new XElement("TelephoneNumber",
                        new XElement("Number", phone))));
    }

    /// <summary>
    /// <c>&lt;Contact&gt;</c> with the order's ordering-contact name/email/phone, or null when all
    /// three are blank (→ no node → byte-identical for orders with no contact).
    /// </summary>
    private static XElement? BuildContact(PurchaseOrderEntity o)
    {
        if (IsBlank(o.ContactName) && IsBlank(o.ContactEmail) && IsBlank(o.ContactPhone))
            return null;

        return new XElement("Contact",
            IsBlank(o.ContactName) ? null
                : new XElement("Name", new XAttribute(Xml + "lang", "en"), o.ContactName),
            IsBlank(o.ContactEmail) ? null : new XElement("Email", o.ContactEmail),
            IsBlank(o.ContactPhone) ? null
                : new XElement("Phone",
                    new XElement("TelephoneNumber",
                        new XElement("Number", o.ContactPhone))));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static XElement BuildItemOut(PurchaseOrderLineEntity line, string currency)
    {
        var unitPriceStr = line.UnitPrice.ToString("F2", CultureInfo.InvariantCulture);
        var quantityStr  = line.Quantity.ToString(CultureInfo.InvariantCulture);

        return new XElement("ItemOut",
            new XAttribute("quantity",   quantityStr),
            new XAttribute("lineNumber", line.LineNumber),

            new XElement("ItemID",
                new XElement("SupplierPartID", line.SupplierItemCode ?? string.Empty)),

            new XElement("ItemDetail",
                new XElement("UnitPrice",
                    new XElement("Money",
                        new XAttribute("currency", currency),
                        unitPriceStr)),
                new XElement("Description",
                    new XAttribute(Xml + "lang", "en"),
                    line.Description ?? string.Empty),
                new XElement("UnitOfMeasure",
                    line.Unit ?? string.Empty)),

            // Per-line tax (null-gated): in cXML 1.2 <Tax> is a child of <ItemOut> — a SIBLING of
            // <ItemDetail>, NOT nested inside it (ItemDetail's content model has no Tax). Emit it only
            // when a tax AMOUNT was captured; no amount → null → ignored by XLinq → byte-identical.
            BuildLineTax(line, currency));
    }

    /// <summary>
    /// Builds the optional cXML <c>&lt;Tax&gt;</c> sub-block for a line: a <c>&lt;Money&gt;</c> carrying
    /// the captured VAT amount, plus a <c>&lt;Description&gt;</c> stating the rate when known. Returns
    /// null when no tax AMOUNT was captured (→ no node → byte-identical to the pre-feature output).
    /// In cXML 1.2 <c>Tax</c> (Money + Description) is a child of <c>ItemOut</c>, emitted after
    /// <c>ItemDetail</c> — see the call site; it is NOT a member of ItemDetail's content model.
    /// </summary>
    private static XElement? BuildLineTax(PurchaseOrderLineEntity line, string currency)
    {
        if (line.TaxAmount is not { } taxAmount)
            return null;

        var description = line.TaxRate is { } rate
            ? $"VAT {rate.ToString("0.##", CultureInfo.InvariantCulture)}%"
            : "Tax";

        return new XElement("Tax",
            new XElement("Money",
                new XAttribute("currency", currency),
                taxAmount.ToString("F2", CultureInfo.InvariantCulture)),
            new XElement("Description",
                new XAttribute(Xml + "lang", "en"),
                description));
    }
}
