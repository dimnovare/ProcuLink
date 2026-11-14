using System.Xml;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

/// <summary>
/// A standard file an output can be: its id (the FE catalog id), the writer that makes it, the stored format, and the meanings it needs.
/// A <see cref="Vesid"/> marks a Peppol invoice-model standard (made by the invoice writer, not <see cref="Writer"/>, which is then
/// unused) and names the official rule set it is checked against; <see cref="Flagged"/> standards sit behind the Peppol flag.
/// </summary>
public sealed record StandardOutput(string Id, OutputFormat Writer, string Format, IReadOnlyList<string> Needs,
    string? Vesid = null, bool Flagged = false, bool CreditNote = false, InvoiceSyntax Syntax = InvoiceSyntax.Ubl, string RuleSet = "Peppol", string? Customization = null);

/// <summary>The XML syntax an invoice-model standard is written in.</summary>
public enum InvoiceSyntax { Ubl, Cii }

/// <summary>The standard order files a flow output can be, filled by meaning and written by the existing writers.</summary>
public static class StandardOutputs
{
    private static readonly IReadOnlyList<string> OrderNeeds =
    [
        FieldMeanings.DocumentNumber, FieldMeanings.IssueDate, FieldMeanings.Currency,
        FieldMeanings.LineQuantity, FieldMeanings.LineUnitPrice, FieldMeanings.LineItemCode,
    ];

    // cXML also needs a unit: its writer writes a blank one, which networks refuse. UBL also needs both parties: its writer otherwise names them with placeholders.
    private static readonly IReadOnlyList<string> PeppolNeeds =
    [
        FieldMeanings.DocumentNumber, FieldMeanings.IssueDate, FieldMeanings.Currency,
        FieldMeanings.SenderName, FieldMeanings.ReceiverName, FieldMeanings.SenderEndpointId, FieldMeanings.ReceiverEndpointId,
        FieldMeanings.SenderCountry, FieldMeanings.ReceiverCountry, FieldMeanings.TotalNet, FieldMeanings.TotalTax, FieldMeanings.TotalGross,
        FieldMeanings.LineDescription, FieldMeanings.LineQuantity, FieldMeanings.LineUnitPrice, FieldMeanings.LineAmount,
    ];

    // EN 16931 makes the electronic addresses (BT-34 / BT-49) optional: the CII writer emits them when the read has them.
    private static readonly IReadOnlyList<string> CiiNeeds =
        [.. PeppolNeeds.Where(m => m is not (FieldMeanings.SenderEndpointId or FieldMeanings.ReceiverEndpointId))];

    // XRechnung 3.0 (BR-DE-1..9, 15): the Peppol needs plus a buyer reference, both cities and postcodes, a seller contact and an IBAN. Street is optional.
    private static readonly IReadOnlyList<string> XRechnungNeeds =
    [
        .. PeppolNeeds, FieldMeanings.BuyerReference, FieldMeanings.SenderCity, FieldMeanings.SenderPostcode, FieldMeanings.ReceiverCity, FieldMeanings.ReceiverPostcode,
        FieldMeanings.SenderContactName, FieldMeanings.SenderContactPhone, FieldMeanings.SenderContactEmail, FieldMeanings.Iban,
    ];

    private const string XRechnungCustomization = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";

    public static readonly IReadOnlyList<StandardOutput> All =
    [
        new("cxml-1-2", OutputFormat.CXml, FlowOutputFormats.Xml, [.. OrderNeeds, FieldMeanings.LineUnit]),
        new("ubl-2-1-order", OutputFormat.Ubl, FlowOutputFormats.Xml, [.. OrderNeeds, FieldMeanings.SenderName, FieldMeanings.ReceiverName]),
        new("x12-850", OutputFormat.X12, FlowOutputFormats.Text, OrderNeeds),
        new("peppol-bis-billing-3", OutputFormat.Ubl, FlowOutputFormats.Xml, PeppolNeeds, "eu.peppol.bis3:invoice:2026.5", true),
        new("peppol-bis-billing-3-credit-note", OutputFormat.Ubl, FlowOutputFormats.Xml, PeppolNeeds, "eu.peppol.bis3:creditnote:2026.5", true, true),
        new("edifact-orders", OutputFormat.EdifactOrders, FlowOutputFormats.Text, OrderNeeds),
        new("en16931-cii", OutputFormat.Ubl, FlowOutputFormats.Xml, CiiNeeds, "eu.cen.en16931:cii:1.3.17", true, Syntax: InvoiceSyntax.Cii, RuleSet: "EN 16931"),
        new("en16931-cii-credit-note", OutputFormat.Ubl, FlowOutputFormats.Xml, CiiNeeds, "eu.cen.en16931:cii:1.3.17", true, true, Syntax: InvoiceSyntax.Cii, RuleSet: "EN 16931"),
        new("xrechnung-ubl", OutputFormat.Ubl, FlowOutputFormats.Xml, XRechnungNeeds, "de.xrechnung:ubl-invoice:3.0.2", true, RuleSet: "XRechnung", Customization: XRechnungCustomization),
        new("xrechnung-ubl-credit-note", OutputFormat.Ubl, FlowOutputFormats.Xml, XRechnungNeeds, "de.xrechnung:ubl-creditnote:3.0.2", true, true, RuleSet: "XRechnung", Customization: XRechnungCustomization),
        new("xrechnung-cii", OutputFormat.Ubl, FlowOutputFormats.Xml, XRechnungNeeds, "de.xrechnung:cii:3.0.2", true, Syntax: InvoiceSyntax.Cii, RuleSet: "XRechnung", Customization: XRechnungCustomization),
        new("xrechnung-cii-credit-note", OutputFormat.Ubl, FlowOutputFormats.Xml, XRechnungNeeds, "de.xrechnung:cii:3.0.2", true, true, Syntax: InvoiceSyntax.Cii, RuleSet: "XRechnung", Customization: XRechnungCustomization),
    ];

    public static StandardOutput? Find(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// The standard file, or <c>output_invalid</c>: with the needs sentence when the read lacks a need, without a detail
    /// for an unknown id or anything a writer still refuses, an amount too large for its arithmetic included (its messages are not approved copy). Never throws for either.
    /// </summary>
    public static async Task<DocumentRender> RenderAsync(string id, ReadResult read, FlowFieldList fields, string? tableKey, Guid orgId, CancellationToken ct)
    {
        if (Find(id) is not { } standard) return new(null, "output_invalid", null);
        try
        {
            var invoice = standard.Vesid is null ? default : StandardInvoiceAdapter.ToInvoice(read, fields, tableKey, standard.Needs, orgId, standard.CreditNote);
            var (order, orderMissing) = standard.Vesid is null ? StandardOrderAdapter.ToOrder(read, fields, tableKey, standard.Needs, orgId) : (null, invoice.Missing);
            if (order is null && invoice.Invoice is null)
            {
                var items = orderMissing.Count == 1 ? orderMissing[0] : $"{string.Join(", ", orderMissing.SkipLast(1))} and {orderMissing[^1]}";
                return new(null, "output_invalid", $"This standard needs {items}.");
            }
            if (invoice.Invoice is { } inv)
            {
                if (standard.CreditNote && inv.GrandTotal < 0) WritePositive(inv, invoice.Lines);
                var doc = standard.Syntax == InvoiceSyntax.Cii
                    ? CiiInvoiceWriter.Build(inv, invoice.Lines, invoice.Parties!, standard.CreditNote, standard.Customization)
                    : new PeppolBisInvoiceTransformService().BuildDocument(inv, invoice.Lines, invoice.Parties!, standard.CreditNote, standard.Customization);
                var bytes = System.Text.Encoding.UTF8.GetBytes(doc.Declaration + Environment.NewLine + doc);
                return new(TransformResult.For(OutputFormat.Xml, new MemoryStream(bytes, writable: false)), null, null);
            }
            // Not in OutputTransformRegistry: registering it would surface EDIFACT in the legacy order screens.
            if (standard.Writer == OutputFormat.EdifactOrders)
                return new(TransformResult.For(OutputFormat.EdifactOrders,
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes(EdifactOrdersWriter.Write(order!, new EdifactEnvelope())), writable: false)), null, null);
            var writer = OutputTransformRegistry.All.First(w => w.CanTransform(standard.Writer));
            return new(await writer.TransformAsync(order!, standard.Writer, ct), null, null);
        }
        catch (Exception ex) when (ex is TransformValidationException or ArgumentException or InvalidOperationException or XmlException or OverflowException)
        {
            return new(null, "output_invalid", null);
        }
    }

    /// <summary>
    /// A credit note printed negative, written positive as EN 16931 credit notes carry it: each total and line amount changes sign
    /// (so a re-charge line inside the credit stays negative), a quantity is negative only when its line amount is (a free item is credited,
    /// so it stays positive), and a unit price is never negative. The tax rate is left alone. The adapter's invoice is fresh and never attached to a DbContext, so it is corrected in place.
    /// </summary>
    internal static void WritePositive(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines)
    {
        (invoice.SubTotal, invoice.TaxTotal, invoice.GrandTotal) = (-invoice.SubTotal, -invoice.TaxTotal, -invoice.GrandTotal);
        foreach (var l in lines)
        {
            l.LineTotal = -l.LineTotal;
            l.Quantity = l.LineTotal < 0 ? -Math.Abs(l.Quantity) : Math.Abs(l.Quantity);
            l.UnitPrice = Math.Abs(l.UnitPrice);
        }
    }
}
