using System.Xml;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

/// <summary>
/// A standard file an output can be: its id (the FE catalog id), the writer that makes it, the stored format, and the meanings it needs.
/// A <see cref="Vesid"/> marks a Peppol invoice-model standard (made by the invoice writer, not <see cref="Writer"/>, which is then
/// unused) and names the official rule set it is checked against; <see cref="Flagged"/> standards sit behind the Peppol flag.
/// A <see cref="Format"/> of pdf marks a Factur-X / ZUGFeRD PDF. <see cref="Embeds"/> is a PDF standard's XML twin: the PDF carries
/// exactly that standard's file, and its rule checks check that file.
/// </summary>
public sealed record StandardOutput(string Id, OutputFormat Writer, string Format, IReadOnlyList<string> Needs,
    string? Vesid = null, bool Flagged = false, bool CreditNote = false, InvoiceSyntax Syntax = InvoiceSyntax.Ubl, string RuleSet = "Peppol", string? Customization = null, string? Embeds = null)
{
    /// <summary>The envelope has an interchange control number a receiver checks for duplicates, so every real send needs a fresh one.</summary>
    public bool NeedsControlNumber => Writer is OutputFormat.X12 or OutputFormat.EdifactOrders;
}

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

    // cXML also needs a unit (networks refuse a guessed one) and the buyer's name (its DTD requires BillTo/Address/Name). UBL also needs both parties: its writer otherwise names them with placeholders.
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
        new("cxml-1-2", OutputFormat.CXml, FlowOutputFormats.Xml, [.. OrderNeeds, FieldMeanings.LineUnit, FieldMeanings.SenderName]),
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
        new("factur-x-en16931", OutputFormat.Ubl, FlowOutputFormats.Pdf, CiiNeeds, "eu.cen.en16931:cii:1.3.17", true, Syntax: InvoiceSyntax.Cii, RuleSet: "EN 16931", Embeds: "en16931-cii"),
        new("factur-x-en16931-credit-note", OutputFormat.Ubl, FlowOutputFormats.Pdf, CiiNeeds, "eu.cen.en16931:cii:1.3.17", true, true, Syntax: InvoiceSyntax.Cii, RuleSet: "EN 16931", Embeds: "en16931-cii-credit-note"),
        new("zugferd-xrechnung", OutputFormat.Ubl, FlowOutputFormats.Pdf, XRechnungNeeds, "de.xrechnung:cii:3.0.2", true, Syntax: InvoiceSyntax.Cii, RuleSet: "XRechnung", Customization: XRechnungCustomization, Embeds: "xrechnung-cii"),
        new("zugferd-xrechnung-credit-note", OutputFormat.Ubl, FlowOutputFormats.Pdf, XRechnungNeeds, "de.xrechnung:cii:3.0.2", true, true, Syntax: InvoiceSyntax.Cii, RuleSet: "XRechnung", Customization: XRechnungCustomization, Embeds: "xrechnung-cii-credit-note"),
    ];

    public static StandardOutput? Find(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>The bytes a standard's text is written in: EDIFACT declares UNOC, which is ISO 8859-1; every other file is UTF-8.</summary>
    public static System.Text.Encoding TextEncoding(string? id) => id == "edifact-orders" ? System.Text.Encoding.Latin1 : System.Text.Encoding.UTF8;

    /// <summary>A standard output's file, with the sender and receiver IDs stored on it (sending and Preview both call this).</summary>
    public static Task<DocumentRender> RenderAsync(FlowOutput output, ReadResult read, FlowFieldList fields, Guid orgId, CancellationToken ct, long? controlNumber = null)
        => RenderAsync(output.Standard!, read, fields, output.TableKey, orgId, ct, DocumentJson.Deserialize<OutputNodeTemplate>(output.TemplateJson).Envelope, controlNumber);

    /// <summary>
    /// The standard file, or <c>output_invalid</c>: with the needs sentence when the read lacks a need, with the
    /// <see cref="ValueTooLongException"/> sentence when an identifier is too long for the standard, without a detail
    /// for an unknown id or anything a writer still refuses, an amount too large for its arithmetic included (its messages are not approved copy). Never throws for either.
    /// The order standards write the sender and receiver IDs in <paramref name="envelope"/>, the placeholders without it.
    /// </summary>
    public static async Task<DocumentRender> RenderAsync(string id, ReadResult read, FlowFieldList fields, string? tableKey, Guid orgId, CancellationToken ct, EnvelopeConfig? envelope = null, long? controlNumber = null)
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
                if (standard.Format == FlowOutputFormats.Pdf)
                    return new(TransformResult.For(OutputFormat.Pdf, new MemoryStream(FacturXPdfWriter.Write(inv, invoice.Lines, invoice.Parties!, standard.CreditNote,
                        standard.RuleSet == "XRechnung" ? FacturXProfile.XRechnung : FacturXProfile.En16931, bytes), writable: false)), null, null);
                return new(TransformResult.For(OutputFormat.Xml, new MemoryStream(bytes, writable: false)), null, null);
            }
            // Not in OutputTransformRegistry: registering it would surface EDIFACT in the legacy order screens.
            if (standard.Writer == OutputFormat.EdifactOrders)
                return new(TransformResult.For(OutputFormat.EdifactOrders,
                    new MemoryStream(TextEncoding(id).GetBytes(EdifactOrdersWriter.Write(order!, envelope?.Edifact, controlNumber)), writable: false)), null, null);
            if (standard.Writer == OutputFormat.X12)
                return new(await new X12TransformService().TransformAsync(order!, OutputFormat.X12, ct, envelope, controlNumber), null, null);
            var writer = OutputTransformRegistry.All.First(w => w.CanTransform(standard.Writer));
            // A flow's cXML declares its DTD (the legacy order path declares one only when configured).
            var cxml = standard.Writer == OutputFormat.CXml ? CxmlTransformService.StandardConfig(envelope?.Cxml) : null;
            return new(await writer.TransformAsync(order!, standard.Writer, ct, cxml), null, null);
        }
        catch (ValueTooLongException ex)
        {
            return new(null, "output_invalid", ex.Message);
        }
        catch (Exception ex) when (ex is TransformValidationException or ArgumentException or InvalidOperationException or XmlException or OverflowException
            or QuestPDF.Drawing.Exceptions.DocumentLayoutException or QuestPDF.Drawing.Exceptions.DocumentComposeException)
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
