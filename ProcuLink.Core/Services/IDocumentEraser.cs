using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// Hard-erases particular documents of one workspace on a written request: the document rows, what
/// hangs off them (deliveries, read calls, history) and their stored files. Billed units survive in
/// <c>erased_document_units</c>, so the bill is unchanged. The receipt goes to <c>platform_audit_events</c>.
/// </summary>
public interface IDocumentEraser
{
    /// Org id ANDed into every match; an empty filter matches nothing; ids of another org are ignored.
    /// <paramref name="history"/>, when given, is saved in the same transaction as the erase: both happen or neither.
    Task<DocumentEraseReceipt> EraseAsync(Guid orgId, DocumentEraseFilter filter, string actor, CancellationToken ct, AuditEvent? history = null);
}

/// <summary>All parts AND together. <see cref="ReceivedBefore"/> compares the document's CreatedAt with <c>&lt;</c>.
/// <see cref="NeverDelivered"/> matches only a document with no delivery row that is not sent, checked again by the delete itself.
/// <see cref="KeepFingerprints"/>: a person's remove, not a legal erase. Each erased real document that was sent or had a delivery
/// leaves a <see cref="RemovedDocumentFingerprint"/>, so a later copy still waits as already received.</summary>
public sealed record DocumentEraseFilter(IReadOnlyList<Guid>? Ids = null, DateTime? ReceivedBefore = null, bool NeverDelivered = false, bool KeepFingerprints = false);

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

/// <summary>A matched document started being read or sent during the erase; nothing was erased.</summary>
public sealed class DocumentsChangedDuringEraseException()
    : InvalidOperationException("Documents changed while they were being erased; nothing was erased. Run it again.");
