using FluentAssertions;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Tests.Services;

/// <summary>
/// WP-20 regression guard for <see cref="DeliveryMediaTypes"/>.
///
/// <para>
/// The defect this table replaced was a <c>switch</c> with a silent
/// <c>_ =&gt; "application/octet-stream"</c> arm: every format nobody remembered to list fell
/// through it and reached suppliers mis-typed and mis-named. The guard below makes that
/// unreachable — <see cref="EveryOutputFormat"/> is generated from the enum, so ADDING a value to
/// <see cref="OutputFormat"/> without adding a row produces a new, FAILING test case rather than a
/// new silent fallback.
/// </para>
/// </summary>
public class DeliveryMediaTypeTableTests
{
    public static TheoryData<OutputFormat> EveryOutputFormat()
    {
        var data = new TheoryData<OutputFormat>();
        foreach (var format in Enum.GetValues<OutputFormat>())
            data.Add(format);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryOutputFormat))]
    public void EveryFormat_DeclaresItsContentTypeAndExtension(OutputFormat format)
    {
        // For() throws for a format with no row — that throw IS the guard. Adding an OutputFormat
        // value without a DeliveryMediaTypes row fails here.
        var media = DeliveryMediaTypes.For(format);

        media.ContentType.Should().NotBeNullOrWhiteSpace();
        media.ContentType.Should().NotBe(DeliveryMediaTypes.Unknown.ContentType,
            "a format ProcuLink can produce is never 'we don't know what these bytes are'");
        media.ContentType.Should().Contain("/", "a media type is type/subtype");
        media.FileExtension.Should().StartWith(".");
        media.FileExtension.Should().NotBe(DeliveryMediaTypes.Unknown.FileExtension);
    }

    [Fact]
    public void TheTable_HasARowForEveryOutputFormat_AndNothingElse()
    {
        DeliveryMediaTypes.All.Keys.Should().BeEquivalentTo(
            Enum.GetValues<OutputFormat>(),
            "the table is the ONE source of truth: a format with no row, or a row for a format that "
            + "no longer exists, means some call site is deriving the envelope somewhere else");
    }

    /// <summary>
    /// The exact rows, pinned. The guard above proves every format HAS an envelope; this proves it
    /// is the RIGHT one — a receiver rejecting on content type or extension does not care that a row
    /// exists, only what it says.
    /// </summary>
    [Theory]
    [InlineData(OutputFormat.Xml,           "application/xml",     ".xml")]
    [InlineData(OutputFormat.Csv,           "text/csv",            ".csv")]
    [InlineData(OutputFormat.CXml,          "application/xml",     ".xml")]
    [InlineData(OutputFormat.Json,          "application/json",    ".json")]
    [InlineData(OutputFormat.Ubl,           "application/xml",     ".xml")]
    [InlineData(OutputFormat.X12,           "application/EDI-X12", ".x12")]
    [InlineData(OutputFormat.UblOrder,      "application/xml",     ".xml")]
    [InlineData(OutputFormat.X12_850,       "application/EDI-X12", ".x12")]
    [InlineData(OutputFormat.EdifactOrders, "application/EDIFACT", ".edi")]
    public void TheRows_AreExactly(OutputFormat format, string contentType, string extension)
    {
        DeliveryMediaTypes.For(format).Should().Be(new DeliveryMediaType(contentType, extension));
    }

    // ── The persisted token index is a projection of the same rows ────────────

    [Theory]
    [MemberData(nameof(EveryOutputFormat))]
    public void ThePersistedFormatToken_ResolvesToTheSameRowAsTheEnum(OutputFormat format)
    {
        // OutboundArtifact.Format is written as OutputFormat.ToString().ToLowerInvariant()
        // (OrderTransformService). The token index must resolve that string to the same row —
        // if it ever did not, delivery would type the document differently from the transform.
        var token = format.ToString().ToLowerInvariant();

        DeliveryMediaTypes.TryForToken(token, out var viaToken).Should().BeTrue(
            "'{0}' is the string persisted for {1}", token, format);
        viaToken.Should().Be(DeliveryMediaTypes.For(format));
    }

    [Theory]
    [InlineData("peppol",  "application/xml", ".xml")]
    [InlineData("edifact", "application/EDIFACT", ".edi")]
    [InlineData("CXML",    "application/xml", ".xml")]
    public void KnownAliasesAndCasingResolve(string token, string contentType, string extension)
    {
        DeliveryMediaTypes.ForTokenOrUnknown(token)
            .Should().Be(new DeliveryMediaType(contentType, extension));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some-format-nobody-has-heard-of")]
    public void AnUnrecognisedToken_IsHonestlyUnknown(string? token)
    {
        // The ONE place octet-stream is still right: a legacy or hand-written format string whose
        // bytes we genuinely cannot describe. Never reached by a format ProcuLink produces.
        DeliveryMediaTypes.ForTokenOrUnknown(token).Should().Be(DeliveryMediaTypes.Unknown);
        DeliveryMediaTypes.Unknown.Should().Be(new DeliveryMediaType("application/octet-stream", ".dat"));
    }

    // ── Reverse lookup (operator-supplied template content types) ─────────────

    [Theory]
    [InlineData("application/EDI-X12",            ".x12")]
    [InlineData("application/edi-x12",            ".x12")]
    [InlineData("application/xml; charset=utf-8", ".xml")]
    [InlineData("text/csv",                       ".csv")]
    public void ContentTypeReverseLookup_MatchesTheTable(string contentType, string expected)
    {
        DeliveryMediaTypes.TryExtensionForContentType(contentType, out var extension).Should().BeTrue();
        extension.Should().Be(expected);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("")]
    [InlineData(null)]
    public void ContentTypeReverseLookup_DeclinesWhatTheTableDoesNotOwn(string? contentType)
    {
        DeliveryMediaTypes.TryExtensionForContentType(contentType, out _).Should().BeFalse();
    }
}
