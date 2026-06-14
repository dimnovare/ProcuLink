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

    // ── ForAiCapReached ───────────────────────────────────────────────────────

    [Fact]
    public void ForAiCapReached_ExplainsCapHonestly_NotScannedPdf()
    {
        var msg = ParseFailureExplain.ForAiCapReached();
        Assert.Equal(
            "AI document extraction is paused for this workspace — the monthly AI usage limit was reached. Raise the limit or contact support, then re-upload.",
            msg);
        Assert.DoesNotContain("scanned", msg, StringComparison.OrdinalIgnoreCase);
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

    [Theory]
    [InlineData(".xlsx")]
    [InlineData(".XLSX")]
    [InlineData(".xls")]
    public void ForException_UnsupportedZipCompression_GivesActionableMessage_NotRawBclString(string ext)
    {
        // The .NET BCL ZipArchive throws this verbatim for Deflate64 etc. (some xlsx writers).
        var ex = new System.IO.InvalidDataException(
            "The archive entry was compressed using an unsupported compression method.");

        var msg = ParseFailureExplain.ForException(ext, ex);

        Assert.Contains("Re-save it in Excel", msg);
        Assert.Contains("CSV", msg);
        // Must NOT leak the raw BCL exception text to the operator.
        Assert.DoesNotContain("unsupported compression method", msg);
        Assert.DoesNotContain("Could not parse file", msg);
    }
}
