namespace ProcuLink.Core.Services;

/// <summary>
/// Hard-erases particular documents of one workspace on a written request: the document rows, what
/// hangs off them (deliveries, read calls, history) and their stored files. Billed units survive in
/// <c>erased_document_units</c>, so the bill is unchanged. The receipt goes to <c>platform_audit_events</c>.
/// </summary>
public interface IDocumentEraser
{
    /// Org id ANDed into every match; an empty filter matches nothing; ids of another org are ignored.
    Task<DocumentEraseReceipt> EraseAsync(Guid orgId, DocumentEraseFilter filter, string actor, CancellationToken ct);
}

/// <summary>Both parts AND together. <see cref="ReceivedBefore"/> compares the document's CreatedAt with <c>&lt;</c>.</summary>
public sealed record DocumentEraseFilter(IReadOnlyList<Guid>? Ids = null, DateTime? ReceivedBefore = null);

/// <summary>
/// What an erase did. <see cref="SkippedInFlight"/> are matches still being read or delivered; <see cref="More"/>
/// says further matches remain past the per-call cap. <see cref="FailedBlobKeys"/> names every stored file (or
/// folder prefix that could not be listed) not confirmed deleted.
/// </summary>
public sealed record DocumentEraseReceipt(
    Guid OrganisationId,
    DocumentEraseFilter Filter,
    int DocumentsErased,
    IReadOnlyList<Guid> SkippedInFlight,
    bool More,
    IReadOnlyDictionary<string, int> RowsDeleted,
    int BlobsDeleted,
    IReadOnlyList<string> FailedBlobKeys,
    string Actor,
    DateTime At);
