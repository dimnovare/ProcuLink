using ProcuLink.Core.Services.Detection;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// The format→media-type map that <c>GET /api/orders/{id}/source</c> answers with.
/// </summary>
public sealed class SourceDocumentMediaTypeTests
{
    /// <summary>Every format token <see cref="DetectedFormat.Format"/> documents it can produce.</summary>
    private static readonly string[] DetectorFormats =
        ["csv", "xlsx", "pdf", "cxml", "ubl", "edifact", "x12", "xml", "unknown"];

    public static TheoryData<string> DetectorVocabulary
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var format in DetectorFormats)
                data.Add(format);
            return data;
        }
    }

    [Theory]
    [InlineData("pdf", "application/pdf", true)]
    [InlineData("csv", "text/csv", true)]
    [InlineData("xml", "application/xml", true)]
    [InlineData("cxml", "application/xml", true)]
    [InlineData("ubl", "application/xml", true)]
    [InlineData("edifact", "text/plain", true)]
    [InlineData("x12", "text/plain", true)]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", false)]
    public void EachDetectedFormat_MapsToItsHonestMediaType(string format, string expected, bool inline)
    {
        var media = SourceDocumentMediaType.For(format);

        Assert.Equal(expected, media.ContentType);
        Assert.Equal(inline, media.RenderInline);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("email")]      // a real SourceCapture.Format value with no media type of its own
    [InlineData("docx")]       // a format the detector does not emit
    public void AnythingUnrecognised_IsOpaqueOctets_AndNeverInline(string? format)
    {
        var media = SourceDocumentMediaType.For(format);

        Assert.Equal(SourceDocumentMediaType.Unknown, media);
        Assert.Equal("application/octet-stream", media.ContentType);
        Assert.False(media.RenderInline);
    }

    [Theory]
    [InlineData("PDF")]
    [InlineData("  Csv  ")]
    public void TheLookupIsCaseAndWhitespaceInsensitive(string format) =>
        Assert.NotEqual(SourceDocumentMediaType.Unknown, SourceDocumentMediaType.For(format));

    /// <summary>
    /// The security property the map exists to hold: nothing it can return will execute in a
    /// browser. A source document is attacker-influenced content — an uploader chooses the bytes —
    /// so a map that could answer <c>text/html</c> would turn this endpoint into stored XSS on the
    /// API origin. Asserted over the detector's whole documented vocabulary plus junk, not over
    /// the branches this file happens to have written.
    /// </summary>
    [Theory]
    [MemberData(nameof(DetectorVocabulary))]
    [InlineData("html")]
    [InlineData("svg")]
    [InlineData("<script>")]
    public void NoInputCanProduceAScriptableMediaType(string format)
    {
        string[] scriptable =
        [
            "text/html",
            "application/xhtml+xml",
            "image/svg+xml",
            "text/javascript",
            "application/javascript",
            "application/x-httpd-php",
        ];

        var media = SourceDocumentMediaType.For(format);

        Assert.DoesNotContain(media.ContentType, scriptable, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Anti-vacuity for the test above: the vocabulary really does reach live, distinct mappings,
    /// so "none of them is scriptable" is not passing because they all collapse to octet-stream.
    /// </summary>
    [Fact]
    public void TheDetectorVocabulary_ReachesSeveralDistinctMediaTypes()
    {
        var mapped = DetectorFormats
            .Select(SourceDocumentMediaType.For)
            .Where(m => m != SourceDocumentMediaType.Unknown)
            .Select(m => m.ContentType)
            .Distinct()
            .ToList();

        Assert.Equal(5, mapped.Count); // pdf, csv, xml, text/plain, xlsx
    }
}
