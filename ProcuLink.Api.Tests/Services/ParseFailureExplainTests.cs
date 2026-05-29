using ProcuLink.Api.Services;

namespace ProcuLink.Api.Tests.Services;

public class ParseFailureExplainTests
{
    // ── ForEmptyLines ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".pdf",  "scanned or image-only")]
    [InlineData(".PDF",  "scanned or image-only")]   // case-insensitive
    [InlineData(".csv",  "No line-table columns")]
    [InlineData(".CSV",  "No line-table columns")]
    [InlineData(".xlsx", "No line-table columns")]
    [InlineData(".xls",  "No line-table columns")]
    [InlineData(".xml",  "zero line items")]
    [InlineData(".edi",  "zero line items")]
    public void ForEmptyLines_ReturnsFormatSpecificMessage(string ext, string expectedFragment)
    {
        var msg = ParseFailureExplain.ForEmptyLines(ext);
        Assert.Contains(expectedFragment, msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── ForUnsupportedFormat ──────────────────────────────────────────────────

    [Theory]
    [InlineData(".rar")]
    [InlineData(".docx")]
    [InlineData(".zip")]
    public void ForUnsupportedFormat_IncludesExtensionAndSupportedList(string ext)
    {
        var msg = ParseFailureExplain.ForUnsupportedFormat(ext);
        Assert.Contains(ext, msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Supported:", msg);
    }

    // ── ForException ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(".edi",  "EDI file")]
    [InlineData(".txt",  "EDI file")]
    [InlineData(".x12",  "EDI file")]
    [InlineData(".xml",  "XML file")]
    [InlineData(".cxml", "XML file")]
    [InlineData(".csv",  "Could not parse file")]
    [InlineData(".xlsx", "Could not parse file")]
    [InlineData(".pdf",  "Could not parse file")]
    public void ForException_ReturnsContextualCopy(string ext, string expectedFragment)
    {
        var ex = new Exception("test detail message");
        var msg = ParseFailureExplain.ForException(ext, ex);
        Assert.Contains(expectedFragment, msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test detail message", msg);
    }
}
