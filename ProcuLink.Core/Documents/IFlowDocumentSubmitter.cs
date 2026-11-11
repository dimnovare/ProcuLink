using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Core.Documents;

/// <param name="Error">One of <c>document_engine_not_enabled</c>, <c>flow_not_found</c>, <c>pilot_expired</c> (trial),
/// <c>processing_paused</c> (paid, paused), <c>unsupported_format</c>, <c>file_too_large</c>, <c>unreadable_file</c>.</param>
public sealed record FlowSubmitResult(FlowDocument? Document, string? Error, LimitCheckResult? Limit);

/// <summary>The one way a file enters a flow, whatever channel it came from.</summary>
public interface IFlowDocumentSubmitter
{
    Task<FlowSubmitResult> SubmitAsync(
        Guid orgId, Guid flowId, Stream content, string fileName, string contentType,
        string source, string? sourceRef, CancellationToken ct);

    /// <summary>The file extensions (no dot) this org's flows accept.</summary>
    IReadOnlyList<string> AcceptedFormats(Guid orgId);
}
