using ClosedXML.Excel;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Compatibility fallback for .xlsx workbooks whose zip parts were written with a
/// compression method the .NET BCL cannot read.
///
/// A .xlsx is an OPC zip. <see cref="System.IO.Compression.ZipArchive"/> (used by
/// ClosedXML → System.IO.Packaging and by our zip-bomb pre-guard) only supports the
/// <c>Stored</c> (0) and <c>Deflate</c> (8) methods. A workbook whose parts use any
/// other method — <c>Deflate64</c> (9, emitted by some Excel/export tooling on larger
/// sheets), <c>BZip2</c>, <c>LZMA</c>, <c>PPMd</c>, … — makes the BCL throw
/// <see cref="System.IO.InvalidDataException"/> with the message
/// "The archive entry was compressed using an unsupported compression method." the
/// moment a part is read. (Live prod order ba89b09c / PO-20260606210428 hit this.)
///
/// SharpCompress (MIT — no commercial-licence concern) <i>can</i> read those methods.
/// <see cref="RepackToStandardZip"/> reads every entry via SharpCompress and re-writes
/// it into a plain Stored/Deflate zip that the BCL — and therefore ClosedXML — can open.
/// </summary>
internal static class XlsxCompressionFallback
{
    // Declared-uncompressed backstop applied during the repack. These read the central
    // directory's declared sizes (no decompression needed) so a hostile workbook cannot
    // turn the repack itself into a decompression-bomb DoS. Generous enough that any real
    // purchase-order / catalog workbook (a few MB) passes; the per-format zip-bomb guard
    // re-runs on the repacked stream for the authoritative check.
    private const long MaxEntryBytes = 128L * 1024 * 1024;   // 128 MB per part
    private const long MaxTotalBytes = 512L * 1024 * 1024;   // 512 MB total

    // The .NET BCL zip layer reports an unreadable compression method with one of two
    // messages, both starting with this stem:
    //   • generic — "The archive entry was compressed using an unsupported compression method."
    //               (method not in the BCL's known enum, e.g. PPMd — this is what live prod
    //                order ba89b09c produced)
    //   • named   — "The archive entry was compressed using {Method} and is not supported."
    //               (BZip2 / LZMA — methods the BCL names but can't read)
    // Matching the shared stem catches both, across .NET method-name variations.
    // (Note: Deflate64 itself is read natively by .NET 8, so it is NOT one of these failures.)
    private const string BclCompressionFailureStem = "archive entry was compressed using";

    /// <summary>
    /// True when <paramref name="ex"/> (or any inner exception) is the BCL's
    /// unreadable-compression-method failure. ClosedXML / System.IO.Packaging may surface
    /// the raw <see cref="System.IO.InvalidDataException"/> or wrap it, so the whole
    /// inner-exception chain is inspected by message. Used for user-facing classification —
    /// stays STRICT so only genuine compression failures get the actionable copy.
    /// </summary>
    public static bool IsUnsupportedCompression(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains(BclCompressionFailureStem, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Broader predicate for the parsers' recovery path: should we ATTEMPT a SharpCompress
    /// repack before giving up? True for the strict compression failure AND for the masked
    /// form ClosedXML surfaces — <c>System.IO.Packaging.ZipPackage</c> swallows the underlying
    /// BCL compression error and rethrows <c>FileFormatException("File contains corrupted
    /// data.")</c> with no inner. The repack is self-validating (SharpCompress can only re-pack
    /// a zip it can actually read), so a genuinely corrupt / non-zip file simply fails the
    /// repack and the caller surfaces the original error — this never masks real corruption.
    /// </summary>
    public static bool ShouldAttemptRepack(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains(BclCompressionFailureStem, StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.GetType().Name, "FileFormatException", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Opens an .xlsx workbook from <paramref name="fileStream"/>, transparently repacking it
    /// first when its zip parts use a compression method the BCL can't read. ClosedXML routes
    /// through System.IO.Packaging, which masks such failures as
    /// <c>FileFormatException("File contains corrupted data.")</c>, so the recovery triggers on
    /// that broad signal (<see cref="ShouldAttemptRepack"/>) and falls back to the original error
    /// when the repack can't help. The input is buffered to a seekable stream only when needed,
    /// so the repacked bytes can be retried. Shared by <see cref="XlsxOrderParser"/> and
    /// <see cref="XlsxTextExtractor"/> so both open workbooks identically.
    /// </summary>
    public static XLWorkbook OpenWorkbook(Stream fileStream)
    {
        // Need a rewindable stream to retry with the repacked bytes if the first open fails.
        Stream seekable = fileStream;
        if (!fileStream.CanSeek)
        {
            var buffered = new MemoryStream();
            fileStream.CopyTo(buffered);
            buffered.Position = 0;
            seekable = buffered;
        }

        try
        {
            return new XLWorkbook(seekable);
        }
        catch (Exception ex) when (ShouldAttemptRepack(ex))
        {
            // RepackToStandardZip rewinds `seekable` internally before reading.
            if (TryRepackToStandardZip(seekable, out var repacked))
                return new XLWorkbook(repacked);
            throw; // not a repackable zip — surface the original (honest) failure
        }
    }

    /// <summary>
    /// Best-effort <see cref="RepackToStandardZip"/>: returns <c>false</c> (instead of throwing)
    /// when <paramref name="source"/> is not a zip SharpCompress can read, so the caller can
    /// surface the original parse failure unchanged.
    /// </summary>
    public static bool TryRepackToStandardZip(Stream source, out MemoryStream repacked)
    {
        try
        {
            repacked = RepackToStandardZip(source);
            return true;
        }
        catch
        {
            repacked = null!;
            return false;
        }
    }

    /// <summary>
    /// Reads <paramref name="source"/> (a seekable zip whose entries may use any
    /// SharpCompress-readable compression method) and returns a fresh, rewound
    /// <see cref="MemoryStream"/> containing the same entries re-packed with
    /// Stored/Deflate so the BCL <c>ZipArchive</c> / ClosedXML can open it.
    /// The caller owns the returned stream (it is independent of <paramref name="source"/>).
    /// </summary>
    public static MemoryStream RepackToStandardZip(Stream source)
    {
        if (source.CanSeek)
            source.Position = 0;

        var output = new MemoryStream();
        using (var src = ZipArchive.OpenArchive(source, new ReaderOptions { LeaveStreamOpen = true }))
        using (var writer = WriterFactory.OpenWriter(
                   output,
                   ArchiveType.Zip,
                   new WriterOptions(CompressionType.Deflate) { LeaveStreamOpen = true }))
        {
            long total = 0;
            foreach (var entry in src.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key))
                    continue;

                if (entry.Size > MaxEntryBytes)
                    throw new InvalidDataException(
                        $"Workbook part '{entry.Key}' declares {entry.Size:N0} uncompressed bytes, over the {MaxEntryBytes:N0} repack limit.");

                total += entry.Size;
                if (total > MaxTotalBytes)
                    throw new InvalidDataException(
                        $"Workbook declares more than {MaxTotalBytes:N0} uncompressed bytes — refusing to repack.");

                using var entryStream = entry.OpenEntryStream();
                writer.Write(entry.Key, entryStream, entry.LastModifiedTime);
            }
        }

        output.Position = 0;
        return output;
    }
}
