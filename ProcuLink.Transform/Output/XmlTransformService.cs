using System.Text;
using System.Xml.Linq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// Generates a PurchaseOrder XML document from a fully-resolved order entity.
/// Output schema:
/// <code>
/// &lt;PurchaseOrder&gt;
///   &lt;Header&gt;
///     &lt;PoNumber/&gt; &lt;OrderDate/&gt; &lt;Currency/&gt; &lt;SupplierName/&gt;
///     &lt;ShipTo&gt;…&lt;/ShipTo&gt; &lt;BillTo&gt;…&lt;/BillTo&gt; &lt;Contact&gt;…&lt;/Contact&gt;   (optional)
///   &lt;/Header&gt;
///   &lt;Lines&gt;
///     &lt;Line&gt;
///       &lt;LineNumber/&gt; &lt;SupplierItemCode/&gt; &lt;Description/&gt;
///       &lt;Quantity/&gt; &lt;Unit/&gt; &lt;UnitPrice/&gt; &lt;LineTotal/&gt;
///     &lt;/Line&gt;
///   &lt;/Lines&gt;
/// &lt;/PurchaseOrder&gt;
/// </code>
/// <para>ShipTo / BillTo / Contact carry the canonical address + ordering-contact fields the other
/// output formats already emit. Each block is gated on its NAME (Contact on any of name/email/phone),
/// so an order with no address data emits NONE of them and stays BYTE-IDENTICAL to the pre-feature
/// output (a null returned by the builder is dropped by the parent XElement). The buyer remains
/// name-only everywhere (the canonical model carries no buyer postal address).</para>
/// </summary>
public sealed class XmlTransformService : ITransformService
{
    public bool CanTransform(OutputFormat format) => format == OutputFormat.Xml;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct,
        CxmlCredentialConfig? cxmlCredentials = null) // not used: generic XML has no cXML Header
    {
        ValidateOrder(order);
        // B1: same price>0 / qty>0 output invariant as the fixed CSV/JSON transforms — for a fixed
        // XML transform the entity's canonical columns ARE the emitted bytes (override/template/
        // OutputNode paths emit elsewhere and are intentionally not guarded here).
        OutputFieldValidator.ValidateEntity(order, format);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("PurchaseOrder",
                new XElement("Header",
                    new XElement("PoNumber",     order.PoNumber),
                    new XElement("OrderDate",    order.OrderDate.ToString("yyyy-MM-dd")),
                    new XElement("Currency",     order.Currency),
                    new XElement("SupplierName", order.Supplier?.Name ?? string.Empty),
                    // Null-gated address + contact blocks (dropped by XElement when null).
                    BuildAddress("ShipTo", order.ShipToName, order.ShipToDeliverTo, order.ShipToStreet,
                        order.ShipToCity, order.ShipToPostalCode, order.ShipToCountry,
                        order.ShipToEmail, order.ShipToPhone),
                    BuildAddress("BillTo", order.BillToName, order.BillToDeliverTo, order.BillToStreet,
                        order.BillToCity, order.BillToPostalCode, order.BillToCountry,
                        order.BillToEmail, order.BillToPhone),
                    BuildContact(order)
                ),
                new XElement("Lines",
                    order.Lines
                        .OrderBy(l => l.LineNumber)
                        .Select(l => new XElement("Line",
                            new XElement("LineNumber",       l.LineNumber),
                            new XElement("SupplierItemCode", l.SupplierItemCode ?? string.Empty),
                            new XElement("Description",      l.Description ?? string.Empty),
                            new XElement("Quantity",         l.Quantity),
                            new XElement("Unit",             l.Unit ?? string.Empty),
                            new XElement("UnitPrice",        l.UnitPrice),
                            new XElement("LineTotal",        l.Quantity * l.UnitPrice)
                        ))
                )
            )
        );

        var bytes  = Encoding.UTF8.GetBytes(doc.Declaration + Environment.NewLine + doc.ToString());
        var stream = new MemoryStream(bytes);

        return Task.FromResult(new TransformResult(
            Content:       stream,
            ContentType:   "application/xml",
            FileExtension: ".xml"
        ));
    }

    private static void ValidateOrder(PurchaseOrderEntity order)
    {
        var unresolved = order.Lines
            .Where(l => l.NeedsReview || string.IsNullOrWhiteSpace(l.SupplierItemCode))
            .Select(l => l.LineNumber)
            .OrderBy(n => n)
            .ToList();

        if (unresolved.Count > 0)
            throw new TransformValidationException(unresolved);
    }

    // ── Address / contact blocks ───────────────────────────────────────────────

    private static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>
    /// Builds a <c>&lt;ShipTo&gt;</c> / <c>&lt;BillTo&gt;</c> block with null-gated leaf elements, or
    /// null when the NAME is blank (→ no node → byte-identical for orders without that address). A
    /// null returned here is dropped by the parent <c>Header</c> XElement. Per-leaf null-drop keeps
    /// each emitted block to exactly the populated fields.
    /// </summary>
    private static XElement? BuildAddress(
        string elementName, string? name, string? deliverTo, string? street, string? city,
        string? postal, string? country, string? email, string? phone)
    {
        if (IsBlank(name))
            return null;

        return new XElement(elementName,
            new XElement("Name", name),
            IsBlank(deliverTo)  ? null : new XElement("DeliverTo",  deliverTo),
            IsBlank(street)     ? null : new XElement("Street",     street),
            IsBlank(city)       ? null : new XElement("City",       city),
            IsBlank(postal)     ? null : new XElement("PostalCode", postal),
            IsBlank(country)    ? null : new XElement("Country",    country),
            IsBlank(email)      ? null : new XElement("Email",      email),
            IsBlank(phone)      ? null : new XElement("Phone",      phone));
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
            IsBlank(o.ContactName)  ? null : new XElement("Name",  o.ContactName),
            IsBlank(o.ContactEmail) ? null : new XElement("Email", o.ContactEmail),
            IsBlank(o.ContactPhone) ? null : new XElement("Phone", o.ContactPhone));
    }
}
