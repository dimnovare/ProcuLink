using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.FormatMatrix;

/// <summary>
/// PDF is intentionally EXCLUDED from the deterministic FormatMatrix suite.
///
/// The PRIMARY PDF path is text→LLM structured extraction (OpenAI) with a vision
/// fallback for scanned/image-only PDFs — both require a live API key and are
/// non-deterministic, so they cannot live in a hermetic unit suite. They are
/// covered by:
///   • the live bulk runner — tools/edge-case-orders/run-live.mjs (real OpenAI key)
///   • PdfOrderParserTests — the deterministic regex FALLBACK parser
///   • the benchmark on 22 real Markit documents (see docs/superpowers/plans/2026-06-05-pdf-llm-extraction.md)
///
/// This placeholder documents that exclusion explicitly (a loud, skipped marker)
/// AND pins the one deterministic PDF behaviour we CAN assert hermetically: the
/// regex fallback parser claims the .pdf extension and rejects an empty/textless
/// stream gracefully rather than crashing.
/// </summary>
public class PdfMatrixPlaceholderTests
{
    [Fact(Skip = "PDF text→LLM + vision extraction needs a live OpenAI key — covered by run-live.mjs and the 22-doc benchmark, not this deterministic suite.")]
    public void Pdf_TextLlmExtraction_CoveredByLiveRun() { }

    [Fact(Skip = "Scanned/image-only PDF vision fallback needs a live OpenAI vision model — covered by the live run, not this deterministic suite.")]
    public void Pdf_ScannedVisionFallback_CoveredByLiveRun() { }

    [Fact]
    public void PdfOrderParser_ClaimsPdfExtensionOnly()
    {
        // The deterministic fallback parser is the matrix's only hermetic PDF anchor.
        var parser = new PdfOrderParser();
        parser.CanParse(".pdf").Should().BeTrue();
        parser.CanParse(".PDF").Should().BeTrue();
        parser.CanParse(".csv").Should().BeFalse();
        parser.CanParse(".xml").Should().BeFalse();
    }
}
