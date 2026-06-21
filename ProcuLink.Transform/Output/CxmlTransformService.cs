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
                    BuildCredentialBlock("From",
                        cxmlCredentials?.FromDomain, cxmlCredentials?.FromIdentity,
                        legacyDomain: "OrgId", legacyIdentity: order.OrgId.ToString()),
                    BuildCredentialBlock("To",
                        cxmlCredentials?.ToDomain, cxmlCredentials?.ToIdentity,
                        legacyDomain: "SupplierId", legacyIdentity: order.SupplierId.ToString()),
                    BuildSender(cxmlCredentials)
                ),

                // ── Request ───────────────────────────────────────────────
                new XElement("Request",
                    new XAttribute("deploymentMode", "production"),

                    new XElement("OrderRequest",

                        // OrderRequestHeader
                        new XElement("OrderRequestHeader",
                            new XAttribute("orderID",   order.PoNumber),
                            new XAttribute("orderDate", order.OrderDate.ToString("yyyy-MM-dd")),
                            new XAttribute("type",      "new"),
                            new XElement("Total",
                                new XElement("Money",
                                    new XAttribute("currency", currency),
                                    totalAmount))),

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
                    line.Unit ?? string.Empty)));
    }
}
