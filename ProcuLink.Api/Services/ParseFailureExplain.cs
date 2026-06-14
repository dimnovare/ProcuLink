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

        // A .xlsx is a ZIP; the .NET BCL ZipArchive only supports Stored/Deflate, so a workbook
        // whose parts were written with another method (e.g. Deflate64, emitted by some export
        // tools) throws the raw "The archive entry was compressed using an unsupported compression
        // method." Surface an actionable message instead of the BCL string, mirroring the
        // scanned-PDF honest-rejection pattern. (A SharpCompress repack fallback that actually
        // opens these files is tracked as a follow-up.)
        if (ex.Message.Contains("unsupported compression method", StringComparison.OrdinalIgnoreCase))
            return "This spreadsheet uses a zip compression format we can't open yet (some export tools produce it). Re-save it in Excel (File → Save As → .xlsx), or upload a CSV instead.";

        if (ext is ".edi" or ".txt" or ".x12")
            return $"We couldn't read this EDI file: {ex.Message}";
        if (ext is ".xml" or ".cxml")
            return $"We couldn't read this XML file: {ex.Message}";
        return $"Could not parse file: {ex.Message}";
    }
}
