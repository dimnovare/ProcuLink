namespace ProcuLink.Api.Services;

/// <summary>
/// Produces operator-friendly error messages for parse failures.
/// Pure static helper — no dependencies.
/// </summary>
public static class ParseFailureExplain
{
    public static string ForEmptyLines(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".pdf"                    => "This PDF looks scanned or image-only — we couldn't extract any text. Export a text-based PDF, or upload a CSV/XLSX instead.",
            ".csv" or ".xlsx" or ".xls" => "No line-table columns detected. We couldn't find recognisable item columns (item code, quantity, unit price). Check the header row or map columns using a PO template.",
            _                         => "The document was read but contained zero line items.",
        };

    public static string ForAiCapReached() =>
        "AI document extraction is paused for this workspace — the monthly AI usage limit was reached. Raise the limit or contact support, then re-upload.";

    public static string ForUnsupportedFormat(string extension) =>
        $"Unsupported file format '{extension.ToLowerInvariant()}'. Supported: CSV, XLSX, PDF, XML (cXML/UBL/Peppol), EDI (EDIFACT).";

    public static string ForException(string extension, Exception ex)
    {
        var ext = extension.ToLowerInvariant();

        // A .xlsx is a ZIP. When a part uses a compression method the .NET BCL ZipArchive can't
        // read, it throws either "The archive entry was compressed using an unsupported compression
        // method." (generic — e.g. PPMd, what live prod order ba89b09c hit) or "...using {Method}
        // and is not supported." (named — BZip2/LZMA); both share this stem. The parsers now repack
        // such workbooks via SharpCompress (XlsxCompressionFallback) and parse them transparently,
        // so this branch fires only as a last resort — the file is still unreadable after the repack
        // attempt (truly corrupt, or a method SharpCompress can't read either). Surface an actionable
        // message, not the BCL string, mirroring the scanned-PDF honest-rejection pattern.
        if (ex.Message.Contains("archive entry was compressed using", StringComparison.OrdinalIgnoreCase))
            return "This spreadsheet uses a zip compression format we can't open (some export tools produce it). Re-save it in Excel (File → Save As → .xlsx), or upload a CSV instead.";

        if (ext is ".edi" or ".txt" or ".x12")
            return $"We couldn't read this EDI file: {ex.Message}";
        if (ext is ".xml" or ".cxml")
            return $"We couldn't read this XML file: {ex.Message}";
        return $"Could not parse file: {ex.Message}";
    }
}
