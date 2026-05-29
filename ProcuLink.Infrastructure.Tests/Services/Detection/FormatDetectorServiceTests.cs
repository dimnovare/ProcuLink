using System.Text;
using FluentAssertions;
using ProcuLink.Core.Services.Detection;
using ProcuLink.Infrastructure.Services.Detection;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

public class FormatDetectorServiceTests
{
    private readonly FormatDetectorService _sut = new();

    [Fact]
    public async Task DetectAsync_ReturnsPdf_ForPdfMagicBytes()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%binary garbage here\n1 0 obj <<>> endobj");
        using var ms = new MemoryStream(bytes);

        var result = await _sut.DetectAsync(ms, "purchase-order.pdf", CancellationToken.None);

        result.Format.Should().Be("pdf");
        result.Confidence.Should().BeGreaterOrEqualTo(0.9);
        result.SuggestedParser.Should().Be("PdfOrderParser");
        result.Reasoning.Should().Contain(r => r.Contains("%PDF-"));
    }

    [Fact]
    public async Task DetectAsync_ReturnsXlsx_ForZipMagicBytesWithXlsxExtension()
    {
        // PK\x03\x04 — standard ZIP local file header signature, used by every XLSX file.
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00, 0x08, 0x00 };
        using var ms = new MemoryStream(bytes);

        var result = await _sut.DetectAsync(ms, "orders.xlsx", CancellationToken.None);

        result.Format.Should().Be("xlsx");
        result.Confidence.Should().BeGreaterOrEqualTo(0.9);
        result.SuggestedParser.Should().Be("XlsxOrderParser");
    }

    [Fact]
    public async Task DetectAsync_ReturnsXlsxWithLowerConfidence_WhenZipButNoXlsxExtension()
    {
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00 };
        using var ms = new MemoryStream(bytes);

        var result = await _sut.DetectAsync(ms, "mystery-archive.zip", CancellationToken.None);

        result.Format.Should().Be("xlsx");
        result.Confidence.Should().BeLessThan(0.9);
        result.Reasoning.Should().Contain(r => r.Contains("not end with .xlsx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_PopulatesColumnHeaders_ForCsv()
    {
        // Regression: the fingerprint moat needs DetectAsync to emit the CSV header row so
        // ParseOrderJob can record a schema fingerprint and detect-format can return seenCount.
        var csv = "po_number,supplier_code,sku,qty,price\nDEMO-1,SUP-9,ABC-1,2,9.99\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await _sut.DetectAsync(ms, "order.csv", CancellationToken.None);

        result.Format.Should().Be("csv");
        result.ColumnHeaders.Should().NotBeNull();
        result.ColumnHeaders.Should().Equal("po_number", "supplier_code", "sku", "qty", "price");
    }

    [Fact]
    public async Task DetectAsync_ReturnsCxml_ForCxmlNamespace()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <cXML xmlns="http://www.cxml.org/cXML" payloadID="abc">
              <Header><From><Credential domain="NetworkID"><Identity>Acme</Identity></Credential></From></Header>
              <Request deploymentMode="production">
                <OrderRequest>
                  <OrderRequestHeader orderID="PO-12345" orderDate="2024-01-15" type="new"/>
                </OrderRequest>
              </Request>
            </cXML>
            """;
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var result = await _sut.DetectAsync(ms, "order.xml", CancellationToken.None);

        result.Format.Should().Be("cxml");
        result.Confidence.Should().BeGreaterOrEqualTo(0.9);
        result.SuggestedParser.Should().Be("CxmlOrderParser");
        result.DetectedPoNumber.Should().Be("PO-12345");
    }

    [Fact]
    public async Task DetectAsync_ReturnsUbl_ForUblOrderNamespace()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
                   xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
                   xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
              <cbc:ID>PO-77</cbc:ID>
              <cac:OrderLine><cbc:ID>1</cbc:ID></cac:OrderLine>
              <cac:OrderLine><cbc:ID>2</cbc:ID></cac:OrderLine>
            </Order>
            """;
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var result = await _sut.DetectAsync(ms, "ubl-order.xml", CancellationToken.None);

        result.Format.Should().Be("ubl");
        result.Confidence.Should().BeGreaterOrEqualTo(0.9);
        result.SuggestedParser.Should().Be("UblOrderParser");
        result.DetectedPoNumber.Should().Be("PO-77");
        result.EstimatedLineCount.Should().Be(2);
    }

    [Fact]
    public async Task DetectAsync_ReturnsEdifact_ForUnaSegment()
    {
        // Minimal-but-realistic ORDERS shape: UNA service advice, UNB envelope, UNH/BGM header.
        var ediText = "UNA:+.?'UNB+UNOC:3+ACMEBUYER+ACMESUPPLIER+240115:1200+12345'UNH+1+ORDERS:D:96A:UN'BGM+220+PO-EDI-9001+9'";
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(ediText));

        var result = await _sut.DetectAsync(ms, "order.edi", CancellationToken.None);

        result.Format.Should().Be("edifact");
        result.Confidence.Should().BeGreaterOrEqualTo(0.85);
        result.SuggestedParser.Should().Be("EdifactOrderParser");
        result.DetectedPoNumber.Should().Be("PO-EDI-9001");
    }

    [Fact]
    public async Task DetectAsync_ReturnsX12_ForIsaSegment()
    {
        var x12 = "ISA*00*          *00*          *ZZ*BUYER         *ZZ*SUPPLIER      *240115*1200*U*00401*000000001*0*P*>~"
                + "GS*PO*BUYER*SUPPLIER*20240115*1200*1*X*004010~"
                + "ST*850*0001~"
                + "BEG*00*NE*PO-X12-555*200*20240115~";
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(x12));

        var result = await _sut.DetectAsync(ms, "order.x12", CancellationToken.None);

        result.Format.Should().Be("x12");
        result.Confidence.Should().BeGreaterOrEqualTo(0.8);
        result.SuggestedParser.Should().Be("X12OrderParser");
        result.DetectedPoNumber.Should().Be("PO-X12-555");
    }

    [Fact]
    public async Task DetectAsync_ReturnsCsv_ForCsvByteStream()
    {
        var csv =
            "po_number,supplier_code,sku,qty,price\n" +
            "PO-001,ACME,WIDGET-1,10,9.99\n" +
            "PO-001,ACME,WIDGET-2,5,19.99\n" +
            "PO-001,ACME,WIDGET-3,2,49.99\n" +
            "PO-001,ACME,WIDGET-4,1,99.99\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await _sut.DetectAsync(ms, "orders.csv", CancellationToken.None);

        result.Format.Should().Be("csv");
        result.Confidence.Should().BeGreaterOrEqualTo(0.6);
        result.SuggestedParser.Should().Be("CsvOrderParser");
    }

    [Fact]
    public async Task DetectAsync_ReturnsUnknown_ForBinaryRandomBytes()
    {
        // High-entropy bytes that don't match any magic and don't decode to a CSV-shaped peek.
        var bytes = new byte[64];
        new Random(42).NextBytes(bytes);
        // Zero out the first 5 so we definitely don't accidentally start with %PDF- or PK..
        bytes[0] = 0xAA; bytes[1] = 0xBB; bytes[2] = 0xCC; bytes[3] = 0xDD; bytes[4] = 0xEE;
        using var ms = new MemoryStream(bytes);

        var result = await _sut.DetectAsync(ms, "junk.bin", CancellationToken.None);

        result.Format.Should().Be("unknown");
        result.Confidence.Should().Be(0.0);
        result.SuggestedParser.Should().BeNull();
    }

    [Fact]
    public async Task DetectAsync_ExtractsPoNumberFromCsvHeader()
    {
        var csv =
            "order_id,buyer_name,sku,qty,unit_price\n" +
            "ORDER-9911,Northwind Trading,SKU-A,3,5.00\n" +
            "ORDER-9911,Northwind Trading,SKU-B,2,7.00\n" +
            "ORDER-9911,Northwind Trading,SKU-C,1,11.00\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var result = await _sut.DetectAsync(ms, "orders.csv", CancellationToken.None);

        result.Format.Should().Be("csv");
        result.DetectedPoNumber.Should().Be("ORDER-9911");
        result.EstimatedLineCount.Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task DetectAsync_RewindsStreamForCallerReuse()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\nrest of file");
        using var ms = new MemoryStream(bytes);

        _ = await _sut.DetectAsync(ms, "file.pdf", CancellationToken.None);

        ms.Position.Should().Be(0, "the detector must rewind the stream so callers can re-read it");
    }

    [Fact]
    public async Task DetectAsync_HandlesNonSeekableStream_WithoutThrowing()
    {
        var bytes = Encoding.UTF8.GetBytes("po_number,sku,qty,price\nPO-1,A,1,1.0\nPO-1,B,2,2.0\nPO-1,C,3,3.0\n");
        using var inner = new MemoryStream(bytes);
        await using var nonSeekable = new NonSeekableStream(inner);

        var result = await _sut.DetectAsync(nonSeekable, "orders.csv", CancellationToken.None);

        result.Format.Should().Be("csv");
        result.DetectedPoNumber.Should().Be("PO-1");
    }

    /// <summary>Wraps a <see cref="MemoryStream"/> to simulate a non-seekable upload stream (e.g. a raw network read).</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStream(Stream inner) { _inner = inner; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
