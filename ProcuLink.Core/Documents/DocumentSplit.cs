namespace ProcuLink.Core.Documents;

/// <summary>Where the reader saw a document start in a file: its first page, its own number, and whether it says it is a copy.</summary>
public sealed record DocumentSpan(int FirstPage, string? Number, bool IsCopy);

/// <summary>A document's pages in its file, both ends included.</summary>
public sealed record PageRange(int FirstPage, int LastPage);

/// <summary>Turns the starts the reader saw into one page range per document in the file.</summary>
public static class DocumentSplit
{
    /// <summary>Pages before the first start join the first document; a same-number start continues it, and so does a
    /// numberless start on a part boundary (each part is read alone). A start marked as a copy of an earlier number is
    /// left out up to the next start. No usable start means the whole file is one document.</summary>
    public static IReadOnlyList<PageRange> Ranges(IReadOnlyList<DocumentSpan> spans, int pageCount)
    {
        var ranges = new List<PageRange>();
        var kept = new HashSet<string>();
        int? start = 1;
        string? current = null;
        var valid = spans.Where(s => s.FirstPage >= 1 && s.FirstPage <= pageCount)
            .OrderBy(s => s.FirstPage).DistinctBy(s => s.FirstPage).ToList();
        for (var i = 0; i < valid.Count; i++)
        {
            var span = valid[i];
            var number = string.IsNullOrWhiteSpace(span.Number) ? null : DocumentChecks.NormalizeNumber(span.Number);
            var copy = i > 0 && span.IsCopy && number is not null && kept.Contains(number);
            var continues = i > 0 && !copy && (number is not null
                ? number == current
                : span.FirstPage > 1 && (span.FirstPage - 1) % ReadLimits.PagesPerUnit == 0);
            if (continues) continue;
            if (i > 0 && start is int first) ranges.Add(new PageRange(first, span.FirstPage - 1));
            if (i > 0) start = copy ? null : span.FirstPage;
            current = number;
            if (!copy && number is not null) kept.Add(number);
        }
        if (start is int last) ranges.Add(new PageRange(last, pageCount));
        return ranges;
    }

    /// <summary>The file name of one document cut out of a file: "{stem} (page a).pdf" or "{stem} (pages a-b).pdf".
    /// The stem is cut to 150 characters first, so the whole name stays inside a stored name's 200 and keeps its ".pdf".</summary>
    public static string PartName(string fileName, PageRange r)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length > 150) stem = stem[..150];
        return $"{stem} ({(r.FirstPage == r.LastPage ? $"page {r.FirstPage}" : $"pages {r.FirstPage}-{r.LastPage}")}).pdf";
    }
}
