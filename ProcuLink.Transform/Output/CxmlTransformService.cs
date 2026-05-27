using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

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
/// </summary>
public sealed class CxmlTransformService : ITransformService
{
    private static readonly XNamespace Xml = "http://www.w3.org/XML/1998/namespace";

    public bool CanTransform(OutputFormat format) => format == OutputFormat.CXml;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order,
        OutputFormat format,
        CancellationToken ct)
    {
        ValidateOrder(order);

        var payloadId = $"{Guid.NewGuid():N}@proculink";
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var currency  = string.IsNullOrWhiteSpace(order.Currency) ? "EUR" : order.Currency;

        var totalAmount = order.Lines.Sum(l => l.Quantity * l.UnitPrice)
                              .ToString("F2", CultureInfo.InvariantCulture);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("cXML",
                new XAttribute("payloadID", payloadId),
                new XAttribute("timestamp",  timestamp),
                new XAttribute(Xml + "lang", "en-US"),

                // ── Header ────────────────────────────────────────────────
                new XElement("Header",
                    new XElement("From",
                        new XElement("Credential",
                            new XAttribute("domain", "OrgId"),
                            new XElement("Identity", order.OrgId.ToString()))),
                    new XElement("To",
                        new XElement("Credential",
                            new XAttribute("domain", "SupplierId"),
                            new XElement("Identity", order.SupplierId.ToString()))),
                    new XElement("Sender",
                        new XElement("Credential",
                            new XAttribute("domain", "NetworkUserId"),
                            new XElement("Identity", "proculink")),
                        new XElement("UserAgent", "ProcuLink/1.0"))
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
            )
        );

        var bytes  = Encoding.UTF8.GetBytes(doc.Declaration + Environment.NewLine + doc.ToString());
        var stream = new MemoryStream(bytes);

        return Task.FromResult(new TransformResult(
            Content:       stream,
            ContentType:   "application/xml",
            FileExtension: ".cxml"
        ));
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
