using UglyToad.PdfPig.Writer;

namespace ProcuLink.Transform.Parsing;

/// <summary>Cuts a page range out of a PDF, so one document of a file can be stored and read on its own pages.</summary>
public static class PdfPages
{
    /// <summary>A new PDF holding pages <paramref name="firstPage"/> to <paramref name="lastPage"/> (1-based, both included).</summary>
    public static byte[] Take(byte[] pdf, int firstPage, int lastPage) =>
        PdfMerger.Merge([pdf], [Enumerable.Range(firstPage, lastPage - firstPage + 1).ToList()]);
}
