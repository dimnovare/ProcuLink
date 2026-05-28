using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// Transforms an approved invoice to a binary output format (CSV, XML, JSON).
/// </summary>
public interface IInvoiceTransformService
{
    string Format { get; }
    Task<byte[]> TransformAsync(InvoiceEntity invoice, IReadOnlyList<InvoiceLineEntity> lines, CancellationToken ct);
}
