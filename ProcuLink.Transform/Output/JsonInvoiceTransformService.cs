using System.Text.Json;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

public sealed class JsonInvoiceTransformService : IInvoiceTransformService
{
    public string Format => "json";

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public Task<byte[]> TransformAsync(
        InvoiceEntity invoice,
        IReadOnlyList<InvoiceLineEntity> lines,
        CancellationToken ct)
    {
        var payload = new
        {
            invoiceNumber = invoice.InvoiceNumber,
            issueDate     = invoice.IssueDate.ToString("yyyy-MM-dd"),
            dueDate       = invoice.DueDate?.ToString("yyyy-MM-dd"),
            currency      = invoice.Currency,
            buyerRef      = invoice.BuyerRef,
            supplierRef   = invoice.SupplierRef,
            paymentTerms  = invoice.PaymentTerms,
            subTotal      = invoice.SubTotal,
            taxTotal      = invoice.TaxTotal,
            grandTotal    = invoice.GrandTotal,
            lines         = lines.OrderBy(l => l.LineNumber).Select(l => new
            {
                lineNumber      = l.LineNumber,
                buyerItemCode   = l.BuyerItemCode,
                supplierItemCode = l.SupplierItemCode,
                description     = l.Description,
                quantity        = l.Quantity,
                unitCode        = l.UnitCode,
                unitPrice       = l.UnitPrice,
                taxRate         = l.TaxRate,
                lineTotal       = l.LineTotal,
            }).ToList(),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _opts);
        return Task.FromResult(bytes);
    }
}
