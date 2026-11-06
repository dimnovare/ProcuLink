namespace ProcuLink.Core.Documents;

public sealed record DocumentReadInput(string FileName, string ContentType, string Text, FlowFieldList? Expected);

/// <summary>FailureReason is one of "usage_cap", "ai_unavailable", "ai_error", "empty_document".</summary>
public sealed record DocumentReadOutcome(bool Success, ReadResult? Result, string? FailureReason);

/// <summary>The engine's one AI call: reads every field and table out of a document's text.</summary>
public interface IDocumentReader
{
    bool IsAvailable { get; }

    Task<DocumentReadOutcome> ReadAsync(DocumentReadInput input, Guid organisationId, CancellationToken ct);
}
