using System.IO.Compression;
using SharpCompress.Readers;
using ScZipArchive = SharpCompress.Archives.Zip.ZipArchive;

namespace ProcuLink.Transform.Catalog;

/// <summary>
/// Transparent ZIP unwrap for catalog pulls (plan 2026-07-02 D1). Several real distributor feeds
/// ship the catalog inside a ZIP (Ingram <c>PRICE.ZIP</c>→PRICE.TXT, Also
/// <c>pricelist-1.txt.zip</c>→a .txt). The pull pipeline calls <see cref="TryUnwrap"/> AFTER the
/// SHA-256 hash (so <c>LastFileHash</c> unchanged-skip stays over the transported bytes) and
/// BEFORE parsing; the returned inner file name drives extension routing.
///
/// Detection: leading bytes <c>PK\x03\x04</c> AND the archive does NOT contain
/// <c>[Content_Types].xml</c> (that marks an OPC/xlsx — left intact for the XLSX parser).
///
/// Bomb safety: sizes are NEVER trusted from the central directory — the copy loop reads at most
/// <c>maxUncompressedBytes + 1</c> per entry and aborts on overflow (mirrors
/// <c>BoundedRead.CopyAsync</c>; Transform can't reference Infrastructure so it has its own copy).
/// The BCL <see cref="ZipArchive"/> is tried first; SharpCompress is the fallback for exotic
/// compression methods (same pattern as <see cref="ProcuLink.Transform.Parsing.XlsxCompressionFallback"/>).
/// </summary>
public static class CatalogArchiveUnwrapper
{
    /// <summary>The unwrapped inner entry: its bytes and its name (for extension routing).</summary>
    public sealed record UnwrappedEntry(MemoryStream Data, string EntryName);

    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 }; // "PK\x03\x04"

    // Extensions we recognize as parseable catalog payloads when choosing among multiple entries.
    private static readonly string[] RecognizedExtensions =
        { ".csv", ".txt", ".json", ".xml", ".xls", ".xlsx", ".cif", ".tsv" };

    /// <summary>
    /// Returns the chosen inner entry when <paramref name="data"/> is a (non-xlsx) ZIP, else null
    /// (caller keeps the original stream + name). Entry choice: the single file entry when there is
    /// exactly one, otherwise the largest entry with a recognized extension (falling back to the
    /// largest entry overall). Throws <see cref="CatalogTooLargeException"/> if the chosen entry's
    /// decompressed size exceeds <paramref name="maxUncompressedBytes"/>.
    /// </summary>
    public static UnwrappedEntry? TryUnwrap(MemoryStream data, long maxUncompressedBytes)
    {
        if (data.Length < 4) return null;
        var magic = new byte[4];
        data.Position = 0;
        var n = data.Read(magic, 0, 4);
        data.Position = 0;
        if (n < 4 || !(magic[0] == ZipMagic[0] && magic[1] == ZipMagic[1]
                        && magic[2] == ZipMagic[2] && magic[3] == ZipMagic[3]))
            return null; // not a zip

        // BCL first; SharpCompress fallback for exotic compression methods.
        try
        {
            return TryUnwrapBcl(data, maxUncompressedBytes);
        }
        catch (CatalogTooLargeException)
        {
            throw; // size guard is authoritative — never mask it with a fallback
        }
        catch (InvalidDataException)
        {
            data.Position = 0;
            return TryUnwrapSharpCompress(data, maxUncompressedBytes);
        }
    }

    private static UnwrappedEntry? TryUnwrapBcl(MemoryStream data, long maxUncompressedBytes)
    {
        using var archive = new ZipArchive(data, ZipArchiveMode.Read, leaveOpen: true);

        // An OPC package (xlsx/docx/…) contains [Content_Types].xml — leave it for the XLSX parser.
        if (archive.Entries.Any(e => string.Equals(e.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase)))
            return null;

        var fileEntries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        if (fileEntries.Count == 0) return null;

        var chosen = ChooseEntry(fileEntries.Select(e => (e.FullName, e.Name, (long)e.Length)).ToList());
        var entry = fileEntries.First(e => e.FullName == chosen.FullName);

        using var entryStream = entry.Open();
        var outStream = CopyBounded(entryStream, maxUncompressedBytes);
        return new UnwrappedEntry(outStream, entry.Name);
    }

    private static UnwrappedEntry? TryUnwrapSharpCompress(MemoryStream data, long maxUncompressedBytes)
    {
        data.Position = 0;
        using var archive = ScZipArchive.OpenArchive(data, new ReaderOptions { LeaveStreamOpen = true });

        var entries = archive.Entries
            .Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key))
            .ToList();
        if (entries.Count == 0) return null;
        if (entries.Any(e => string.Equals(e.Key, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase)))
            return null;

        var chosen = ChooseEntry(entries.Select(e => (e.Key!, NameOf(e.Key!), e.Size)).ToList());
        var entry = entries.First(e => e.Key == chosen.FullName);

        using var entryStream = entry.OpenEntryStream();
        var outStream = CopyBounded(entryStream, maxUncompressedBytes);
        return new UnwrappedEntry(outStream, NameOf(entry.Key!));

        static string NameOf(string key) => Path.GetFileName(key.Replace('\\', '/'));
    }

    /// <summary>
    /// Entry-choice policy (D1): exactly one file → take it; otherwise the largest entry with a
    /// recognized extension, falling back to the largest entry overall. Declared sizes only steer
    /// the CHOICE — the actual copy is still bomb-bounded, never trusting the declared size.
    /// </summary>
    private static (string FullName, string Name, long Size) ChooseEntry(
        List<(string FullName, string Name, long Size)> entries)
    {
        if (entries.Count == 1) return entries[0];

        var recognized = entries
            .Where(e => RecognizedExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()))
            .ToList();
        var pool = recognized.Count > 0 ? recognized : entries;
        return pool.OrderByDescending(e => e.Size).ThenBy(e => e.Name, StringComparer.Ordinal).First();
    }

    /// <summary>
    /// Bounded decompress copy: reads at most <paramref name="maxBytes"/>+1 (the extra byte only
    /// detects overflow), aborting with <see cref="CatalogTooLargeException"/> — a lying central
    /// directory or an endless stream can therefore never materialize more than the cap. Mirrors
    /// <c>BoundedRead.CopyAsync</c> (Transform cannot reference Infrastructure).
    /// </summary>
    private static MemoryStream CopyBounded(Stream source, long maxBytes)
    {
        var dest = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var want = (int)Math.Min(buffer.Length, maxBytes + 1 - total);
            if (want <= 0) throw new CatalogTooLargeException(UnzipTooLargeMessage(maxBytes));
            var read = source.Read(buffer, 0, want);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new CatalogTooLargeException(UnzipTooLargeMessage(maxBytes));
            dest.Write(buffer, 0, read);
        }
        dest.Position = 0;
        return dest;
    }

    private static string UnzipTooLargeMessage(long maxBytes) =>
        $"The catalog archive expands to more than {maxBytes / (1024 * 1024)} MB — refusing to unpack.";
}
