namespace ProcuLink.Core.Documents;

/// <summary>The one list of file formats a flow accepts, whatever channel the file arrives by.</summary>
public static class DocumentFormats
{
    public static readonly string[] Base = ["pdf", "csv", "xlsx"];
    public static readonly string[] Images = ["png", "jpg", "jpeg"];

    /// <summary>Base formats, plus photos and scans when the org has them on.</summary>
    public static string[] For(bool scansOn) => scansOn ? [.. Base, .. Images] : Base;

    public static bool IsImage(string fileName) =>
        Images.Contains(Path.GetExtension(fileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase);
}
