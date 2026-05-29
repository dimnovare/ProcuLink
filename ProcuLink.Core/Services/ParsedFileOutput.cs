using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services;

/// <summary>
/// The result of <see cref="IOrderService.ParseStoredFileAsync"/>: the parsed entity together
/// with file-format metadata extracted while the buffer was in memory.  The caller passes these
/// directly to <see cref="Detection.ISchemaFingerprintService.RecordParseSuccessAsync"/> so the
/// fingerprint service never needs to download the source file a second time.
/// </summary>
public sealed record ParsedFileOutput(
    PurchaseOrderEntity Entity,
    IReadOnlyList<string>? ColumnHeaders,
    string DetectedFormat);
