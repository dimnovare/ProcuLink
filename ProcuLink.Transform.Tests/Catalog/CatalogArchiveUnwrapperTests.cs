using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using ProcuLink.Transform.Catalog;

namespace ProcuLink.Transform.Tests.Catalog;

/// <summary>
/// Tests for the transparent ZIP unwrap (plan 2026-07-02 Phase 1). Real feeds ship the catalog
/// inside a ZIP (Ingram PRICE.ZIP, Also pricelist-1.txt.zip). xlsx (also a zip) must NOT be
/// unwrapped, and the decompress copy must be bomb-bounded (never trusting declared sizes).
/// </summary>
public class CatalogArchiveUnwrapperTests
{
    private const long Cap = 256L * 1024 * 1024;

    private static MemoryStream Zip(params (string name, string content)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = z.CreateEntry(name);
                using var s = e.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void SingleCsvEntry_Unwrapped_InnerNameReturned()
    {
        using var zip = Zip(("PRICE.TXT", "code,name\nA-1,Widget\n"));
        var result = CatalogArchiveUnwrapper.TryUnwrap(zip, Cap);
        result.Should().NotBeNull();
        result!.EntryName.Should().Be("PRICE.TXT");
        new StreamReader(result.Data).ReadToEnd().Should().Contain("A-1,Widget");
    }

    [Fact]
    public void XlsxBytes_NotUnwrapped()
    {
        // A real xlsx is a zip containing [Content_Types].xml — must be left intact.
        var xlsx = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("S1");
            ws.Cell(1, 1).Value = "code";
            wb.SaveAs(xlsx);
        }
        xlsx.Position = 0;

        var result = CatalogArchiveUnwrapper.TryUnwrap(xlsx, Cap);
        result.Should().BeNull("xlsx (OPC package) must not be unwrapped");
    }

    [Fact]
    public void MultipleEntries_LargestRecognizedExtensionChosen()
    {
        using var zip = Zip(
            ("readme.md", "hello"),
            ("small.csv", "code\nA\n"),
            ("catalog.csv", new string('x', 5000) + "\ncode\nBIG\n"));
        var result = CatalogArchiveUnwrapper.TryUnwrap(zip, Cap);
        result.Should().NotBeNull();
        result!.EntryName.Should().Be("catalog.csv");
    }

    [Fact]
    public void EntryOverCap_ThrowsDuringCopy()
    {
        // Small cap; the entry decompresses to more than the cap → bounded copy aborts.
        using var zip = Zip(("big.csv", new string('y', 4096)));
        var act = () => CatalogArchiveUnwrapper.TryUnwrap(zip, maxUncompressedBytes: 1024);
        act.Should().Throw<CatalogTooLargeException>();
    }

    [Fact]
    public void NonZip_Passthrough_ReturnsNull()
    {
        using var plain = new MemoryStream(Encoding.UTF8.GetBytes("code,name\nA-1,Widget\n"));
        CatalogArchiveUnwrapper.TryUnwrap(plain, Cap).Should().BeNull();
    }

    [Fact]
    public void EmptyStream_ReturnsNull()
    {
        using var empty = new MemoryStream();
        CatalogArchiveUnwrapper.TryUnwrap(empty, Cap).Should().BeNull();
    }
}
