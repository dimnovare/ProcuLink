using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Infrastructure.Services.Detection;

/// <summary>
/// "Drop any file and we figure out what it is" detector.
///
/// Strategy (in order):
/// <list type="number">
///   <item>Read up to <see cref="PeekBytes"/> bytes into a buffer (rewind the stream after).</item>
///   <item><b>Magic bytes pass</b> — PDF (<c>%PDF-</c>), XLSX/ZIP (<c>PK\x03\x04</c>), XML
///         (<c>&lt;?xml</c> or <c>&lt;</c>) with namespace/root sniffing for cXML vs UBL,
///         EDIFACT (<c>UNA</c>/<c>UNB+UNOC</c>), X12 (<c>ISA*</c>).</item>
///   <item><b>CSV pass</b> if nothing matched — looks at separators in lines 1+2.</item>
///   <item><b>Metadata extraction</b> — best-effort PO number / supplier / line count
///         from the peek window only (we never re-read the underlying stream past the buffer).</item>
/// </list>
///
/// Safety: <i>never throws</i>. Any exception during sniffing collapses the result to
/// <c>Format = "unknown"</c> with the exception message in reasoning. Stream position is
/// always restored to <c>0</c> before returning.
/// </summary>
public sealed class FormatDetectorService : IFormatDetector
{
    /// <summary>
    /// Size of the leading window we read into a buffer for magic-byte / namespace / CSV sniffing.
    /// 1 KiB is large enough for an XML declaration + root + a couple of namespaces, several EDIFACT
    /// segments, the full X12 ISA envelope, and at least 5 CSV lines for the typical header row.
    /// </summary>
    private const int PeekBytes = 1024;

    public async Task<DetectedFormat> DetectAsync(Stream content, string? fileName, CancellationToken ct)
    {
        var reasoning = new List<string>();

        // Caller may have handed us a non-seekable stream — we need to rewind, so copy into
        // a MemoryStream first. The interface contract documents that the returned stream
        // (i.e. the input stream) is rewound to position 0 before this method returns.
        Stream src = content;
        MemoryStream? ownedCopy = null;
        try
        {
            if (!content.CanSeek)
            {
                ownedCopy = new MemoryStream();
                await content.CopyToAsync(ownedCopy, ct);
                ownedCopy.Position = 0;
                src = ownedCopy;
                reasoning.Add("Input stream was not seekable — copied into a MemoryStream for sniffing.");
            }

            byte[] buffer;
            int read;
            long originalPosition = src.Position;
            try
            {
                buffer = new byte[PeekBytes];
                read = await src.ReadAsync(buffer.AsMemory(0, PeekBytes), ct);
            }
            catch (Exception ex)
            {
                reasoning.Add($"Failed to read peek buffer: {ex.Message}");
                return new DetectedFormat("unknown", 0.0, null, null, null, null, reasoning);
            }
            finally
            {
                if (src.CanSeek)
                    src.Position = originalPosition;
            }

            if (read == 0)
            {
                reasoning.Add("Stream contained zero bytes.");
                return new DetectedFormat("unknown", 0.0, null, null, null, null, reasoning);
            }

            // Decode a UTF-8 text view of the peek window once, used by every text-based check below.
            // Invalid UTF-8 sequences are replaced rather than thrown — this is a sniffer.
            var textHead = Encoding.UTF8.GetString(buffer, 0, read);
            var ext = ExtractExtension(fileName);

            try
            {
                // ── 1. Magic bytes pass ──────────────────────────────────────────

                // PDF — "%PDF-" at offset 0
                if (read >= 5 && buffer[0] == 0x25 && buffer[1] == 0x50 && buffer[2] == 0x44 && buffer[3] == 0x46 && buffer[4] == 0x2D)
                {
                    reasoning.Add("First 5 bytes are %PDF- (PDF magic).");
                    var (poPdf, supplierPdf, linesPdf) = ExtractPdfMetadata(textHead);
                    return new DetectedFormat("pdf", 0.95, "PdfOrderParser", poPdf, supplierPdf, linesPdf, reasoning);
                }

                // XLSX / ZIP — "PK\x03\x04" at offset 0
                if (read >= 4 && buffer[0] == 0x50 && buffer[1] == 0x4B && buffer[2] == 0x03 && buffer[3] == 0x04)
                {
                    reasoning.Add("First 4 bytes are PK\\x03\\x04 (ZIP container).");
                    if (string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase))
                    {
                        reasoning.Add("Filename ends with .xlsx — confident this is an Office Open XML workbook.");
                        return new DetectedFormat("xlsx", 0.95, "XlsxOrderParser", null, null, null, reasoning);
                    }
                    reasoning.Add("Filename did not end with .xlsx — could be any ZIP-based container. Lowering confidence.");
                    return new DetectedFormat("xlsx", 0.7, "XlsxOrderParser", null, null, null, reasoning);
                }

                // XML — starts with "<?xml" or "<"
                var trimmedHead = textHead.TrimStart('﻿', ' ', '\t', '\r', '\n');
                if (trimmedHead.StartsWith("<?xml", StringComparison.Ordinal) || trimmedHead.StartsWith("<", StringComparison.Ordinal))
                {
                    reasoning.Add("Buffer begins with an XML declaration or element (`<`).");

                    // cXML — root element local-name == cXML, possibly with the cXML namespace.
                    // Buffer-scoped contains() check is intentional: 1 KiB always covers the XML decl + root tag + several namespace declarations.
                    if (textHead.Contains("<cXML", StringComparison.OrdinalIgnoreCase)
                        || textHead.Contains("http://www.cxml.org/cXML", StringComparison.Ordinal)
                        || textHead.Contains("cxml.org/cXML", StringComparison.Ordinal))
                    {
                        reasoning.Add("Detected <cXML root element or cxml.org/cXML namespace.");
                        var (poCxml, supplierCxml, linesCxml) = ExtractCxmlMetadata(textHead);
                        return new DetectedFormat("cxml", 0.95, "CxmlOrderParser", poCxml, supplierCxml, linesCxml, reasoning);
                    }

                    // UBL Order — root element <Order> with namespace urn:oasis:names:specification:ubl:schema:xsd:Order-2.
                    const string ublOrderNs = "urn:oasis:names:specification:ubl:schema:xsd:Order-2";
                    if (textHead.Contains(ublOrderNs, StringComparison.Ordinal))
                    {
                        reasoning.Add($"Detected UBL Order-2 namespace ({ublOrderNs}).");
                        var (poUbl, supplierUbl, linesUbl) = ExtractUblMetadata(textHead);
                        return new DetectedFormat("ubl", 0.95, "UblOrderParser", poUbl, supplierUbl, linesUbl, reasoning);
                    }

                    // Generic XML — we know it's XML but not which dialect.
                    reasoning.Add("Buffer is XML but does not match cXML or UBL Order namespaces.");
                    return new DetectedFormat("xml", 0.40, null, null, null, null, reasoning);
                }

                // EDIFACT — UNA:+.?' service advice or UNB+UNOC interchange header.
                // EDIFACT-as-UTF8 may have stray whitespace before UNA in real-world traffic, so check
                // both the raw first 8 bytes for UNA and the first 200 chars for UNB+UNOC.
                var head200 = textHead.Length <= 200 ? textHead : textHead.Substring(0, 200);
                if (textHead.StartsWith("UNA:+.?'", StringComparison.Ordinal)
                    || head200.Contains("UNB+UNOC", StringComparison.Ordinal))
                {
                    reasoning.Add("Detected EDIFACT service advice (UNA) or interchange header (UNB+UNOC).");
                    var poEdi = ExtractEdifactPoNumber(textHead);
                    return new DetectedFormat("edifact", 0.90, "EdifactOrderParser", poEdi, null, null, reasoning);
                }

                // X12 — ISA envelope: literally starts with "ISA" + element separator (commonly '*').
                if (textHead.StartsWith("ISA*", StringComparison.Ordinal)
                    || (textHead.StartsWith("ISA", StringComparison.Ordinal) && read > 3 && IsX12Separator(textHead[3])))
                {
                    reasoning.Add("Detected X12 ISA interchange envelope.");
                    var poX12 = ExtractX12PoNumber(textHead);
                    return new DetectedFormat("x12", 0.85, "X12OrderParser", poX12, null, null, reasoning);
                }

                // ── 2. CSV pass ────────────────────────────────────────────────

                var csvResult = TryDetectCsv(textHead, reasoning);
                if (csvResult is not null) return csvResult;

                // ── Fallback ───────────────────────────────────────────────────
                reasoning.Add("No magic bytes, XML namespace, EDIFACT/X12 envelope, or CSV signal matched.");
                return new DetectedFormat("unknown", 0.0, null, null, null, null, reasoning);
            }
            catch (Exception ex)
            {
                // Defensive: any unhandled sniffing exception collapses to "unknown".
                reasoning.Add($"Detection failed with exception: {ex.GetType().Name}: {ex.Message}");
                return new DetectedFormat("unknown", 0.0, null, null, null, null, reasoning);
            }
        }
        finally
        {
            // Always rewind the original caller-owned stream so they can re-read it.
            if (content.CanSeek)
            {
                try { content.Position = 0; } catch { /* not all streams allow Position = 0 mid-flight; ignore */ }
            }
            ownedCopy?.Dispose();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the lowercase file extension including the leading dot, or empty string.
    /// </summary>
    private static string ExtractExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return string.Empty;
        return fileName.Substring(dot).ToLowerInvariant();
    }

    /// <summary>
    /// Common X12 element separators. Real-world feeds use '*' overwhelmingly but
    /// the standard allows any non-printable byte as separator; we cover the common cases.
    /// </summary>
    private static bool IsX12Separator(char c) => c == '*' || c == '|' || c == '~' || c == '^';

    /// <summary>
    /// CSV pass — looks at line 1 + line 2 separators. CSV detection is fundamentally heuristic;
    /// we require <c>&gt;= 3</c> of the same separator on line 1 AND the same count on line 2.
    /// </summary>
    private static DetectedFormat? TryDetectCsv(string textHead, List<string> reasoning)
    {
        // Split on \n; tolerate \r\n by trimming \r off each line.
        var lines = textHead.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Take(5).ToList();
        if (lines.Count < 2)
        {
            reasoning.Add("CSV pass: fewer than 2 non-empty lines available — skipping.");
            return null;
        }

        // Separator candidates ranked by frequency on line 1.
        var line1 = lines[0];
        var line2 = lines[1];
        var candidates = new[] { ',', ';', '\t' };

        char? winner = null;
        int winningCount = 0;
        foreach (var sep in candidates)
        {
            var count1 = line1.Count(c => c == sep);
            var count2 = line2.Count(c => c == sep);
            if (count1 >= 3 && count1 == count2 && count1 > winningCount)
            {
                winner = sep;
                winningCount = count1;
            }
        }

        if (winner is null)
        {
            reasoning.Add("CSV pass: no separator (',', ';', tab) had >=3 occurrences consistently across line 1 and line 2.");
            return null;
        }

        var sepName = winner switch { ',' => "comma", ';' => "semicolon", '\t' => "tab", _ => "unknown" };
        reasoning.Add($"CSV pass: '{sepName}' separator with {winningCount} consistent occurrences across lines 1 and 2.");

        // Metadata extraction — PO number from row 2 col aligned with a header like "po_number" / "po" / "order_id".
        var (po, lineCount) = ExtractCsvMetadata(lines, winner.Value, reasoning);

        // Column headers (header-row tokens) — surfaced so ParseOrderJob can record an org-scoped
        // schema fingerprint and detect-format can boost confidence + return seenCount on repeat layouts.
        var headers = line1.Split(winner.Value)
                           .Select(h => h.Trim().Trim('"'))
                           .Where(h => h.Length > 0)
                           .ToList();

        return new DetectedFormat("csv", 0.65, "CsvOrderParser", po, null, lineCount, reasoning,
                                  ColumnHeaders: headers.Count > 0 ? headers : null);
    }

    /// <summary>
    /// Looks at the CSV header row for a PO-number column (case-insensitive: <c>po_number</c>,
    /// <c>po</c>, <c>order_id</c>) and pulls the cell from row 2 at the matching column index.
    /// Line count is non-empty lines minus header — clamped to what fit in the peek window.
    /// </summary>
    private static (string? po, int? lineCount) ExtractCsvMetadata(List<string> lines, char separator, List<string> reasoning)
    {
        if (lines.Count < 2) return (null, null);

        var headers = lines[0].Split(separator).Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
        var row2    = lines[1].Split(separator).Select(c => c.Trim().Trim('"')).ToList();

        string? po = null;
        var poColIdx = headers.FindIndex(h => h == "po_number" || h == "po" || h == "order_id" || h == "ponumber" || h == "orderid");
        if (poColIdx >= 0 && poColIdx < row2.Count)
        {
            po = row2[poColIdx];
            reasoning.Add($"CSV header column {poColIdx} ('{headers[poColIdx]}') matched PO-number pattern — extracted '{po}' from row 2.");
        }

        // Line count: total non-empty lines in the peek - 1 (header). Note this is bounded
        // by the 1024-byte peek window and the .Take(5) above, so it is an estimate only.
        var lineCount = Math.Max(0, lines.Count - 1);
        return (po, lineCount);
    }

    private static (string? po, string? supplier, int? lineCount) ExtractCxmlMetadata(string textHead)
    {
        string? po = null;
        var poMatch = Regex.Match(textHead, "orderID=\"([^\"]+)\"");
        if (poMatch.Success) po = poMatch.Groups[1].Value;

        var itemOutCount = Regex.Matches(textHead, "<ItemOut\\b").Count;
        int? lines = itemOutCount > 0 ? itemOutCount : null;

        return (po, null, lines);
    }

    private static (string? po, string? supplier, int? lineCount) ExtractUblMetadata(string textHead)
    {
        // First <cbc:ID> after <Order ...> is the order id.
        string? po = null;
        var idMatch = Regex.Match(textHead, "<cbc:ID>([^<]+)</cbc:ID>");
        if (idMatch.Success) po = idMatch.Groups[1].Value.Trim();

        // Supplier best-effort: SellerSupplierParty -> ... -> first cbc:Name. We don't have
        // the full XML buffered, so this only fires if the supplier block sits early in the doc.
        string? supplier = null;
        var supplierSection = Regex.Match(textHead, "<cac:SellerSupplierParty>[\\s\\S]*?<cbc:Name>([^<]+)</cbc:Name>", RegexOptions.Singleline);
        if (supplierSection.Success) supplier = supplierSection.Groups[1].Value.Trim();

        var orderLineCount = Regex.Matches(textHead, "<cac:OrderLine\\b").Count;
        int? lines = orderLineCount > 0 ? orderLineCount : null;

        return (po, supplier, lines);
    }

    /// <summary>EDIFACT BGM segment carries the document/message reference; document code 220 is "order".</summary>
    private static string? ExtractEdifactPoNumber(string textHead)
    {
        var m = Regex.Match(textHead, "BGM\\+220\\+([^+']+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>X12 BEG03 carries the PO number on 850 transactions: BEG*00*NE*PONUM*...</summary>
    private static string? ExtractX12PoNumber(string textHead)
    {
        var m = Regex.Match(textHead, "BEG\\*00\\*NE\\*([^*]+)\\*");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// PDF metadata extraction from the first 1 KiB is intentionally not attempted —
    /// PDF page content is post-header and almost never appears in the first KiB.
    /// We return nulls and let the actual parser take over.
    /// </summary>
    private static (string? po, string? supplier, int? lineCount) ExtractPdfMetadata(string _textHead)
        => (null, null, null);
}
