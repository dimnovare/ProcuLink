namespace ProcuLink.Core.Documents;

/// <summary>How a file's pages map to billing units, and the most pages one file counts for.</summary>
public static class ReadLimits
{
    public const int PagesPerUnit = 10;
    public const int MaxUnits = 10;
    public const int MaxPages = PagesPerUnit * MaxUnits;

    /// <summary>One unit per ten pages, rounded up: at least 1, at most <see cref="MaxUnits"/>.</summary>
    public static int UnitsFor(int pages) =>
        Math.Clamp((pages + PagesPerUnit - 1) / PagesPerUnit, 1, MaxUnits);
}
