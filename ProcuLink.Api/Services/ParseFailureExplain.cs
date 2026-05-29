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
            ".pdf"                    => "This PDF looks scanned or image-only — we couldn't extract any text. OCR isn't enabled; export a text-based PDF or upload a CSV/XLSX instead.",
            ".csv" or ".xlsx" or ".xls" => "No line-table columns detected. We couldn't find recognisable item columns (item code, quantity, unit price). Check the header row or map columns using a PO template.",
            _                         => "The document was read but contained zero line items.",
        };

    public static string ForUnsupportedFormat(string extension) =>
        $"Unsupported file format '{extension.ToLowerInvariant()}'. Supported: CSV, XLSX, PDF, XML (cXML/UBL/Peppol), EDI (EDIFACT).";

    public static string ForException(string extension, Exception ex)
    {
        var ext = extension.ToLowerInvariant();
        if (ext is ".edi" or ".txt" or ".x12")
            return $"We couldn't read this EDI file: {ex.Message}";
        if (ext is ".xml" or ".cxml")
            return $"We couldn't read this XML file: {ex.Message}";
        return $"Could not parse file: {ex.Message}";
    }
}
