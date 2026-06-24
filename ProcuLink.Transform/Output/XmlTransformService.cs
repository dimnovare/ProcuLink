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
///   &lt;/Header&gt;
///   &lt;Lines&gt;
///     &lt;Line&gt;
///       &lt;LineNumber/&gt; &lt;SupplierItemCode/&gt; &lt;Description/&gt;
///       &lt;Quantity/&gt; &lt;Unit/&gt; &lt;UnitPrice/&gt; &lt;LineTotal/&gt;
///     &lt;/Line&gt;
///   &lt;/Lines&gt;
/// &lt;/PurchaseOrder&gt;
/// </code>
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
                    new XElement("SupplierName", order.Supplier?.Name ?? string.Empty)
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
}
