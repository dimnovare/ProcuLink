namespace ProcuLink.Api.Helpers;

/// <summary>
/// Strips path traversal sequences and illegal characters from uploaded filenames.
/// Call before any use of IFormFile.FileName in application code.
/// </summary>
public static class FileNameSanitiser
{
    private const int MaxLength = 200;

    /// <summary>
    /// Returns a safe filename suitable for logging, storage keys, and extension checks.
    /// Never returns null or empty — falls back to "upload".
    /// </summary>
    public static string Sanitise(string? rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
            return "upload";

        // Strip directory separators — prevents traversal such as ../../evil.sh
        var name = Path.GetFileName(rawFileName);

        // Remove null bytes and control characters (e.g. null-byte extension tricks)
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());

        // Replace any remaining illegal filename characters with underscores
        var invalidChars = Path.GetInvalidFileNameChars();
        name = string.Join("_", name.Split(invalidChars));

        // Strip leading dots — blocks hidden-file tricks like ".htaccess"
        name = name.TrimStart('.');

        // Collapse multiple consecutive underscores/dots left by substitutions
        while (name.Contains("__")) name = name.Replace("__", "_");

        // Enforce a reasonable length cap
        if (name.Length > MaxLength)
            name = name[..MaxLength];

        return string.IsNullOrWhiteSpace(name) ? "upload" : name;
    }

    /// <summary>
    /// Returns the lower-cased file extension (including the dot) from a raw filename,
    /// after sanitising. Returns empty string if no extension is present.
    /// </summary>
    public static string GetExtension(string? rawFileName)
        => Path.GetExtension(Sanitise(rawFileName)).ToLowerInvariant();
}
