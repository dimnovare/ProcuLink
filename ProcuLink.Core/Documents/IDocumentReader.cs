namespace ProcuLink.Core.Documents;

/// <summary><c>Pages</c> holds PNG page images in page order: the scan's pages, or a text PDF's pages for layout. <c>Scanned</c> says the images are the document.
/// <c>Part</c> of <c>Parts</c> says which part of a longer file this input is; 1 of 1 is the whole file.</summary>
public sealed record DocumentReadInput(string FileName, string ContentType, string Text, FlowFieldList? Expected,
    IReadOnlyList<byte[]>? Pages = null, IReadOnlyList<FieldHint>? Hints = null, int Part = 1, int Parts = 1, bool Scanned = false);

/// <summary>What one answered AI call used, as the provider reported it.</summary>
public sealed record ReadUsage(string Model, int TokensIn, int TokensOut);

/// <summary>FailureReason is one of "usage_cap", "ai_unavailable", "ai_error", "empty_document", "no_egress",
/// "document_too_large". Usage is set when the provider answered with a usage figure, whatever the outcome.
/// Documents lists where separate documents start in the input, when the reader saw them.</summary>
public sealed record DocumentReadOutcome(bool Success, ReadResult? Result, string? FailureReason, ReadUsage? Usage = null,
    IReadOnlyList<DocumentSpan>? Documents = null);

/// <summary>The engine's one AI call: reads every field and table out of a document's text.</summary>
public interface IDocumentReader
{
    bool IsAvailable { get; }

    Task<DocumentReadOutcome> ReadAsync(DocumentReadInput input, Guid organisationId, CancellationToken ct);
}
