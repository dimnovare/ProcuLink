using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>Registers PDF as an order output format: the same writer as invoices, behind the usual guard.</summary>
public sealed class PdfTransformService : ITransformService
{
    public bool CanTransform(OutputFormat format) => format == OutputFormat.Pdf;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order, OutputFormat format, CancellationToken ct, CxmlCredentialConfig? cxmlCredentials = null)
    {
        OutputFieldValidator.ValidateEntity(order, format);
        return Task.FromResult(TransformResult.For(OutputFormat.Pdf, new MemoryStream(DocumentPdfOutput.Write(order))));
    }
}
