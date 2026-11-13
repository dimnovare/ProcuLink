using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Documents;

/// <summary>
/// Fills the typed invoice and the Peppol parties from a document's read, by meaning. Meanings are
/// document-relative: an invoice's sender is the seller. Rules the writer cannot know (a due date or terms, a buyer or
/// order reference, a seller VAT id) are left to the rule checker, which names them. Nothing is attached to a DbContext.
/// </summary>
public static class StandardInvoiceAdapter
{
    /// <summary>The invoice, its lines and parties, or what the read lacks for <paramref name="needs"/> (never both).</summary>
    public static (InvoiceEntity? Invoice, IReadOnlyList<InvoiceLineEntity> Lines, PeppolPartyOptions? Parties, IReadOnlyList<string> Missing) ToInvoice(
        ReadResult read, FlowFieldList fields, string? tableKey, IReadOnlyList<string> needs, Guid orgId)
    {
        var (built, missing) = StandardOrderAdapter.Build(read, fields, tableKey, needs, a =>
        {
            var net = a.HeaderNumber(FieldMeanings.TotalNet);
            var tax = a.HeaderNumber(FieldMeanings.TotalTax);
            // Lines without their own rate take the header's.
            var headerRate = net == 0 ? 0 : Math.Round(tax / net * 100, 2);
            var invoice = new InvoiceEntity
            {
                OrganisationId = orgId,
                InvoiceNumber = a.Text(FieldMeanings.DocumentNumber) ?? "",
                IssueDate = a.Date(FieldMeanings.IssueDate),
                DueDate = a.OptionalDate(FieldMeanings.DueDate),
                Currency = a.Text(FieldMeanings.Currency)?.ToUpperInvariant() ?? "",
                BuyerRef = a.Text(FieldMeanings.BuyerReference),
                SubTotal = net,
                TaxTotal = tax,
                GrandTotal = a.HeaderNumber(FieldMeanings.TotalGross),
            };
            var lines = a.Rows.Select((r, i) => new InvoiceLineEntity
            {
                LineNumber = a.LineNumber(i),
                Description = a.Cell(r, FieldMeanings.LineDescription) ?? "",
                Quantity = a.Number(r, FieldMeanings.LineQuantity),
                UnitCode = a.Cell(r, FieldMeanings.LineUnit) ?? "EA",
                UnitPrice = a.Number(r, FieldMeanings.LineUnitPrice),
                LineTotal = a.Number(r, FieldMeanings.LineAmount),
                TaxRate = FieldValueParser.Number(a.Cell(r, FieldMeanings.LineTaxRate)) ?? headerRate,
                SupplierItemCode = a.Cell(r, FieldMeanings.LineItemCode),
            }).ToList();
            var (sellerScheme, sellerId) = Endpoint(a.Text(FieldMeanings.SenderEndpointId));
            var (buyerScheme, buyerId) = Endpoint(a.Text(FieldMeanings.ReceiverEndpointId));
            var parties = new PeppolPartyOptions
            {
                SellerName = a.Text(FieldMeanings.SenderName), SellerEndpointScheme = sellerScheme, SellerEndpointId = sellerId,
                SellerVatId = a.Text(FieldMeanings.SenderVatId), SellerCountry = a.Text(FieldMeanings.SenderCountry)?.ToUpperInvariant(),
                BuyerName = a.Text(FieldMeanings.ReceiverName), BuyerEndpointScheme = buyerScheme, BuyerEndpointId = buyerId,
                BuyerVatId = a.Text(FieldMeanings.ReceiverVatId), BuyerCountry = a.Text(FieldMeanings.ReceiverCountry)?.ToUpperInvariant(),
                OrderReference = a.Text(FieldMeanings.OrderReference),
                DeliveryDate = a.OptionalDate(FieldMeanings.DeliveryDate),
            };
            return new Built(invoice, lines, parties);
        });
        return built is null ? (null, [], null, missing) : (built.Invoice, built.Lines, built.Parties, []);
    }

    private sealed record Built(InvoiceEntity Invoice, IReadOnlyList<InvoiceLineEntity> Lines, PeppolPartyOptions Parties);

    // Only a value that already has the scheme:id shape reaches here (a need), so the split cannot fail; an optional absent one is (null, null).
    private static (string? Scheme, string? Id) Endpoint(string? value) =>
        value?.IndexOf(':') is { } at and > 0 ? (value[..at], value[(at + 1)..]) : (null, null);
}
