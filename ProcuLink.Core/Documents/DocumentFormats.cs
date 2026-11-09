namespace ProcuLink.Core.Documents;

/// <summary>The one list of file formats a flow accepts, whatever channel the file arrives by.</summary>
public static class DocumentFormats
{
    public static readonly string[] Base = ["pdf", "csv", "xlsx"];
    public static readonly string[] Images = ["png", "jpg", "jpeg"];

    public static readonly string[] More = ["docx", "txt", "xml", "cxml", "json", "edi", "x12"];

    /// <summary>Base formats, plus photos and scans and the more-formats set when the org has each on.</summary>
    public static string[] For(bool scansOn, bool formatsOn) =>
        [.. Base, .. (scansOn ? Images : []), .. (formatsOn ? More : [])];

    public static bool IsMore(string fileName) =>
        More.Contains(Path.GetExtension(fileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string fileName) =>
        Images.Contains(Path.GetExtension(fileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase);
}
