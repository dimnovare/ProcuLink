using System.Text;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// WP-15 · S6 — the CSV dialect, and the byte-parity oracle that lets it exist at all.
///
/// <para>Delimiter, quoting, line ending and encoding were hardcoded, so a supplier whose importer
/// wants semicolons and CRLF could not be served without a code change. The line ending was worse
/// than hardcoded: <c>StringBuilder.AppendLine</c> uses <c>Environment.NewLine</c>, so the same tree
/// has always produced LF on the Railway container and CRLF on a Windows dev box.</para>
///
/// <para><b>Why the parity test asserts on RAW BYTES.</b> The existing
/// <c>OutputTemplateEmitterByteParityTests</c> normalises line endings before comparing — reasonable
/// for what it guards, and precisely blind to the field this slice adds. A dialect change that
/// silently re-terminated every existing supplier's rows would pass it. These assert
/// <c>byte[]</c>.</para>
///
/// <para>Founder ruling 2026-07-31: existing layouts keep the bytes they have; only layouts created
/// after this default to CRLF, and the DESIGNER writes that into the tree rather than the emitter
/// assuming it. Which is why every member of <c>CsvDialect</c> is nullable and every null means
/// "as before".</para>
/// </summary>
public class CsvDialectTests
{
    /// <summary>The shared header with <paramref name="note"/> as the buyer name, the value the oddly-shaped cells ride in.</summary>
    private static Dictionary<string, string> HeaderWith(string? note = null)
    {
        var header = EmitterRows.Header();
        header["BuyerName"] = note ?? string.Empty;
        return header;
    }

    private static OutputNodeTemplate Tree(CsvDialect? dialect = null, string headerField = "PoNumber") => new()
    {
        Format = OutputFormat.Csv,
        CsvDialect = dialect,
        Root = OutputNode.Obj("root",
            OutputNode.FieldOf("OrderRef", new OutputFieldRule { OutputPath = "OrderRef", CanonicalField = headerField }),
            OutputNode.Arr("Lines", OutputNode.Obj("Line",
                OutputNode.FieldOf("ItemCode", new OutputFieldRule { OutputPath = "ItemCode", CanonicalField = "SupplierItemCode" }),
                OutputNode.FieldOf("Qty", new OutputFieldRule { OutputPath = "Qty", CanonicalField = "Quantity" })))),
    };

    private static byte[] EmitBytes(OutputNodeTemplate t, Dictionary<string, string>? header = null) =>
        EmitterRows.Emit(t, header ?? HeaderWith(), EmitterRows.TwoLines()).Bytes();

    // ── The oracle ───────────────────────────────────────────────────────────

    /// <summary>
    /// THE test this slice rests on. A null dialect must produce the bytes the emitter produced
    /// before <c>CsvDialect</c> existed — including the platform-dependent terminator, which is why
    /// the expectation is built from <c>Environment.NewLine</c> rather than a literal.
    ///
    /// <para>Pinning CRLF here instead would look tidier and would be the bug: it would assert the
    /// behaviour we deliberately did NOT change, and pass on Windows while every Railway-emitted
    /// file quietly gained a carriage return.</para>
    /// </summary>
    [Fact]
    public void NoDialect_EmitsExactlyThePreDialectBytes()
    {
        var nl = Environment.NewLine;
        var expected = Encoding.UTF8.GetBytes(
            $"OrderRef,ItemCode,Qty{nl}PO-1,S-1,3{nl}PO-1,S-2,2{nl}");

        Assert.Equal(expected, EmitBytes(Tree()));
    }

    /// <summary>An empty dialect object is the same thing as no dialect — no member may default.</summary>
    [Fact]
    public void EmptyDialect_IsIdenticalToNoDialect()
    {
        Assert.Equal(EmitBytes(Tree()), EmitBytes(Tree(new CsvDialect())));
    }

    /// <summary>
    /// No BOM in the output — a receiver parsing the first column by name chokes on EF BB BF.
    ///
    /// <para>Asserted on the ENCODING's preamble, not just on the bytes. A mutation flipping
    /// <c>encoderShouldEmitUTF8Identifier</c> to <c>true</c> SURVIVED the bytes-only version of this
    /// test, and correctly so: <c>GetBytes</c> never writes a preamble whatever the flag says. The
    /// flag only bites the day this path becomes a <c>StreamWriter</c> — which is exactly when a
    /// bytes-only assertion would still be green and every receiver would break.</para>
    /// </summary>
    [Fact]
    public void Utf8Output_HasNoByteOrderMark_AndTheEncodingCannotProduceOne()
    {
        var bytes = EmitBytes(Tree());
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        Assert.Empty(OutputTemplateEmitter.CsvEncoding(null).GetPreamble());
        Assert.Empty(OutputTemplateEmitter.CsvEncoding("").GetPreamble());
    }

    // ── Line ending ──────────────────────────────────────────────────────────

    [Fact]
    public void CrlfDialect_TerminatesEveryRowWithCrLf_OnEveryPlatform()
    {
        var text = Encoding.UTF8.GetString(EmitBytes(Tree(new CsvDialect { LineEnding = "\r\n" })));

        Assert.Equal("OrderRef,ItemCode,Qty\r\nPO-1,S-1,3\r\nPO-1,S-2,2\r\n", text);
        // Every CR is followed by an LF and every LF preceded by a CR — no mixed terminators.
        Assert.Equal(text.Count(c => c == '\r'), text.Count(c => c == '\n'));
    }

    [Fact]
    public void LfDialect_TerminatesEveryRowWithLf_OnEveryPlatform()
    {
        var text = Encoding.UTF8.GetString(EmitBytes(Tree(new CsvDialect { LineEnding = "\n" })));

        Assert.Equal("OrderRef,ItemCode,Qty\nPO-1,S-1,3\nPO-1,S-2,2\n", text);
        Assert.DoesNotContain('\r', text);
    }

    // ── Delimiter ────────────────────────────────────────────────────────────

    [Fact]
    public void SemicolonDialect_SeparatesWithSemicolons()
    {
        var text = Encoding.UTF8.GetString(EmitBytes(Tree(new CsvDialect { Delimiter = ";", LineEnding = "\n" })));
        Assert.Equal("OrderRef;ItemCode;Qty\nPO-1;S-1;3\nPO-1;S-2;2\n", text);
    }

    /// <summary>
    /// Quoting covers the ACTIVE delimiter — a value holding a semicolon under a semicolon dialect is
    /// quoted; missing that splits a supplier's column in half without any error. A comma is quoted
    /// too (since the CSV-injection fix): a ';' file opened in a comma-locale spreadsheet splits on
    /// ',', and an unquoted "Acme,=cmd…" would start a formula cell. Over-quoting is valid CSV.
    /// </summary>
    [Fact]
    public void QuotingCoversTheActiveDelimiter_AndTheComma()
    {
        var withComma = Encoding.UTF8.GetString(EmitBytes(
            Tree(new CsvDialect { Delimiter = ";", LineEnding = "\n" }, headerField: "BuyerName"),
            HeaderWith("Acme, Inc")));
        Assert.Contains("\"Acme, Inc\";S-1;3", withComma);

        var withSemicolon = Encoding.UTF8.GetString(EmitBytes(
            Tree(new CsvDialect { Delimiter = ";", LineEnding = "\n" }, headerField: "BuyerName"),
            HeaderWith("Acme; Inc")));
        Assert.Contains("\"Acme; Inc\";S-1;3", withSemicolon);
    }

    [Fact]
    public void TabDialect_Works_AndIsWrittenAsARealTab()
    {
        var text = Encoding.UTF8.GetString(EmitBytes(Tree(new CsvDialect { Delimiter = "\t", LineEnding = "\n" })));
        Assert.Equal("OrderRef\tItemCode\tQty\nPO-1\tS-1\t3\nPO-1\tS-2\t2\n", text);
    }

    // ── Quote policy ─────────────────────────────────────────────────────────

    [Fact]
    public void AlwaysQuote_QuotesEveryFieldIncludingTheHeaderRow()
    {
        var text = Encoding.UTF8.GetString(EmitBytes(
            Tree(new CsvDialect { QuotePolicy = "always", LineEnding = "\n" })));

        Assert.Equal("\"OrderRef\",\"ItemCode\",\"Qty\"\n\"PO-1\",\"S-1\",\"3\"\n\"PO-1\",\"S-2\",\"2\"\n", text);
    }

    [Fact]
    public void AlwaysQuote_StillDoublesAnEmbeddedQuote()
    {
        var text = Encoding.UTF8.GetString(EmitBytes(
            Tree(new CsvDialect { QuotePolicy = "always", LineEnding = "\n" }, headerField: "BuyerName"),
            HeaderWith("He said \"hi\"")));

        Assert.Contains("\"He said \"\"hi\"\"\"", text);
    }

    [Fact]
    public void MinimalQuote_IsTheDefault_AndMatchesNoDialect()
    {
        Assert.Equal(
            EmitBytes(Tree(new CsvDialect { LineEnding = "\n" })),
            EmitBytes(Tree(new CsvDialect { QuotePolicy = "minimal", LineEnding = "\n" })));
    }

    // ── Header row ───────────────────────────────────────────────────────────

    [Fact]
    public void WriteHeaderRowFalse_OmitsTheHeader_AndNothingElse()
    {
        var text = Encoding.UTF8.GetString(EmitBytes(
            Tree(new CsvDialect { WriteHeaderRow = false, LineEnding = "\n" })));

        Assert.Equal("PO-1,S-1,3\nPO-1,S-2,2\n", text);
    }

    [Fact]
    public void WriteHeaderRowNull_WritesTheHeader()
    {
        var text = Encoding.UTF8.GetString(EmitBytes(Tree(new CsvDialect { LineEnding = "\n" })));
        Assert.StartsWith("OrderRef,ItemCode,Qty", text);
    }

    // ── Encoding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A receiver on a legacy code page reads UTF-8 bytes as mojibake and cannot say so — the file
    /// imports, the name is wrong. windows-1252 puts ö in ONE byte (0xF6); UTF-8 uses two.
    /// </summary>
    [Fact]
    public void Windows1252_RoundTripsAnUmlaut_InOneByte()
    {
        var bytes = EmitBytes(
            Tree(new CsvDialect { Encoding = "windows-1252", LineEnding = "\n" }, headerField: "BuyerName"),
            HeaderWith("Köln"));

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Contains("Köln", Encoding.GetEncoding("windows-1252").GetString(bytes));
        Assert.Contains((byte)0xF6, bytes);
        // And the same content in UTF-8 is a DIFFERENT byte sequence, so the test is about the
        // encoding rather than about the text surviving at all.
        Assert.DoesNotContain(bytes, b => b == 0xC3);
    }

    [Fact]
    public void Utf8_IsTheDefault_AndAnUmlautTakesTwoBytes()
    {
        var bytes = EmitBytes(Tree(new CsvDialect { LineEnding = "\n" }, headerField: "BuyerName"), HeaderWith("Köln"));

        Assert.Contains("Köln", Encoding.UTF8.GetString(bytes));
        Assert.Contains((byte)0xC3, bytes);
    }

    /// <summary>
    /// An unknown encoding FAILS rather than falling back to UTF-8. A silent fallback ships mojibake
    /// to a receiver who explicitly asked for something else and has no way to diagnose it; the name
    /// is only ever present because a person typed it.
    /// </summary>
    [Fact]
    public void UnknownEncoding_FailsLoudly_RatherThanFallingBackToUtf8()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EmitBytes(Tree(new CsvDialect { Encoding = "not-an-encoding" })));

        Assert.Contains("not-an-encoding", ex.Message);
        Assert.Contains("windows-1252", ex.Message);
    }

    // ── Combination ──────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point, in one file: the dialect an operator most often needs — semicolons, CRLF,
    /// every field quoted, no header — composes without any member interfering with another.
    /// </summary>
    [Fact]
    public void EveryOptionAtOnce_Composes()
    {
        var text = Encoding.UTF8.GetString(EmitBytes(Tree(new CsvDialect
        {
            Delimiter = ";", QuotePolicy = "always", LineEnding = "\r\n", WriteHeaderRow = false,
        })));

        Assert.Equal("\"PO-1\";\"S-1\";\"3\"\r\n\"PO-1\";\"S-2\";\"2\"\r\n", text);
    }
}
