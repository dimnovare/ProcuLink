using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Infrastructure.Tests.Services;

public class OpenAiMappingServiceTests
{
    [Fact]
    public async Task SuggestSupplierItemCodeAsync_NoApiKey_ReturnsNull()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:MappingModel"] = "gpt-5-mini"
        });

        var result = await service.SuggestSupplierItemCodeAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            new AiMappingLineContext(1, "HEI-PLT-09", "Mounting plate 90mm", 4, "PCS"),
            Array.Empty<AiMappingCandidate>());

        result.Should().BeNull();
    }

    [Fact]
    public async Task SuggestSupplierItemCodeAsync_NonOpenAiProvider_ReturnsNull()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "none",
            ["Ai:OpenAI:ApiKey"] = "test-key",
            ["Ai:OpenAI:MappingModel"] = "gpt-5-mini"
        });

        var result = await service.SuggestSupplierItemCodeAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            new AiMappingLineContext(1, "HEI-PLT-09", "Mounting plate 90mm", 4, "PCS"),
            new[]
            {
                new AiMappingCandidate("HEI-PLT-08", "ACM-PLT-080", "existing mapping")
            });

        result.Should().BeNull();
    }

    // ── Batch variant: same no-op guarantees as the single-line method ──────────

    [Fact]
    public async Task SuggestSupplierItemCodesAsync_NoApiKey_ReturnsEmpty()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:MappingModel"] = "gpt-5-mini"
        });

        var result = await service.SuggestSupplierItemCodesAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            new[]
            {
                new AiMappingLineContext(1, "HEI-PLT-09", "Mounting plate 90mm", 4, "PCS"),
                new AiMappingLineContext(2, "HEI-PLT-10", "Mounting plate 100mm", 2, "PCS"),
            },
            Array.Empty<AiMappingCandidate>());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SuggestSupplierItemCodesAsync_NonOpenAiProvider_ReturnsEmpty()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "none",
            ["Ai:OpenAI:ApiKey"] = "test-key",
            ["Ai:OpenAI:MappingModel"] = "gpt-5-mini"
        });

        var result = await service.SuggestSupplierItemCodesAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            new[]
            {
                new AiMappingLineContext(1, "HEI-PLT-09", "Mounting plate 90mm", 4, "PCS"),
            },
            new[]
            {
                new AiMappingCandidate("HEI-PLT-08", "ACM-PLT-080", "existing mapping")
            });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SuggestSupplierItemCodesAsync_EmptyLineList_ReturnsEmpty()
    {
        // Even with a configured key the call must short-circuit (and never hit the
        // network) when there are no lines to suggest for.
        var service = CreateService(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "openai",
            ["Ai:OpenAI:ApiKey"] = "test-key",
            ["Ai:OpenAI:MappingModel"] = "gpt-5-mini"
        });

        var result = await service.SuggestSupplierItemCodesAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            Array.Empty<AiMappingLineContext>(),
            Array.Empty<AiMappingCandidate>());

        result.Should().BeEmpty();
    }

    // ── No-egress gate (Organisation.SelfHostedOcr) ─────────────────────────────
    // This is the single IAiMappingService chokepoint: a no-egress org must never have
    // its line data OR its source column headers (the "magic auto-map" path) sent to
    // OpenAI, even with a key configured. The short-circuit runs BEFORE the cap check,
    // so a strict tracker (no setups) proves OpenAI is never reached.

    [Fact]
    public async Task SuggestFieldMappingsAsync_NoEgressOrg_ReturnsEmptyWithoutCallingOpenAi()
    {
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);

        var service = CreateServiceWithNoEgress(
            new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object,
            noEgress: true);

        var result = await service.SuggestFieldMappingsAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new[] { "Item No", "Qty", "Unit Price" },
            new[] { "lines[].SupplierItemCode", "lines[].Quantity" });

        result.Should().BeEmpty("no-egress orgs never send source column headers to OpenAI");
        // Strict mock would have thrown if the cap check (IsAtOrOverLimitAsync) ran.
    }

    [Fact]
    public async Task SuggestSupplierItemCodesAsync_NoEgressOrg_ReturnsEmptyWithoutCallingOpenAi()
    {
        var tracker = new Mock<IAiUsageTracker>(MockBehavior.Strict);

        var service = CreateServiceWithNoEgress(
            new Dictionary<string, string?>
            {
                ["Ai:Provider"]      = "openai",
                ["Ai:OpenAI:ApiKey"] = "sk-test-key",
            },
            tracker: tracker.Object,
            noEgress: true);

        var result = await service.SuggestSupplierItemCodesAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Acme Components",
            new[]
            {
                new AiMappingLineContext(1, "HEI-PLT-09", "Mounting plate 90mm", 4, "PCS"),
            },
            Array.Empty<AiMappingCandidate>());

        result.Should().BeEmpty("no-egress orgs never send line data to OpenAI");
    }

    // ── Batch chunking ──────────────────────────────────────────────────────────
    // A large PO must be split into fixed-size, in-order batches so a single request
    // never truncates against the token ceiling and the per-org cap can be re-checked
    // between chunks. A small order (≤ BatchSize) must stay a single chunk so behaviour
    // is unchanged. ChunkLines is the pure partitioning seam exercised here.

    [Theory]
    [InlineData(0, 0)]    // no lines → no chunks
    [InlineData(1, 1)]    // single line → one chunk
    [InlineData(50, 1)]   // exactly BatchSize → still ONE chunk (small-order path unchanged)
    [InlineData(51, 2)]   // one over → two chunks
    [InlineData(100, 2)]  // exact multiple
    [InlineData(500, 10)] // large PO
    [InlineData(1000, 20)]
    public void ChunkLines_PartitionsIntoFixedBatchesOf50(int lineCount, int expectedChunks)
    {
        var lines = Enumerable.Range(1, lineCount)
            .Select(n => new AiMappingLineContext(n, $"BUY-{n}", $"Item {n}", 1, "PCS"))
            .ToList();

        var chunks = OpenAiMappingService.ChunkLines(lines, 50).ToList();

        chunks.Should().HaveCount(expectedChunks);

        // Chunks must cover every line exactly once, in order, with none larger than 50.
        chunks.SelectMany(c => c.Chunk.Select(l => l.LineNumber))
            .Should().Equal(lines.Select(l => l.LineNumber));
        // OnlyContain throws on an empty collection, so guard the zero-line case.
        if (chunks.Count > 0)
            chunks.Should().OnlyContain(c => c.Chunk.Count <= 50 && c.Chunk.Count >= 1);

        // Offsets are the running start index of each chunk.
        chunks.Select(c => c.Offset)
            .Should().Equal(Enumerable.Range(0, expectedChunks).Select(i => i * 50));
    }

    [Fact]
    public void ChunkLines_LastChunkHoldsTheRemainder()
    {
        var lines = Enumerable.Range(1, 120)
            .Select(n => new AiMappingLineContext(n, $"BUY-{n}", $"Item {n}", 1, "PCS"))
            .ToList();

        var chunks = OpenAiMappingService.ChunkLines(lines, 50).ToList();

        chunks.Should().HaveCount(3);
        chunks[0].Chunk.Should().HaveCount(50);
        chunks[1].Chunk.Should().HaveCount(50);
        chunks[2].Chunk.Should().HaveCount(20);
        chunks[2].Offset.Should().Be(100);
    }

    // ── Catalog allow-list guard (Supplier Catalog P2) ──────────────────────────
    // When the supplier has a product catalog, the candidate set carries the REAL codes
    // (IsCatalogProduct = true) and the model MUST select one of them. The post-response
    // guard rejects any code not in the catalog so a hallucinated code can never surface.
    // With NO catalog the guard is inert — today's free suggestion is returned (offer ⇔ works).
    // ApplyCatalogGuardForTest exercises the exact same decision point the live single-line
    // and batch paths use, without a network call.

    private static readonly AiMappingCandidate[] CatalogCandidates =
    {
        new("", "ES-RES-220R", "catalog product ES-RES-220R — Resistor 220Ω 0.25W",
            IsCatalogProduct: true, Name: "Resistor 220Ω 0.25W", Unit: "PCS", Price: 0.04m, Barcode: "4711000220R"),
        new("", "ES-CAP-100N", "catalog product ES-CAP-100N — Capacitor 100nF",
            IsCatalogProduct: true, Name: "Capacitor 100nF", Unit: "PCS"),
    };

    [Fact]
    public void CatalogGuard_AcceptsRealCatalogCode_WithConcreteProvenance()
    {
        var result = OpenAiMappingService.ApplyCatalogGuardForTest(
            CatalogCandidates,
            suggestedCode: "ES-RES-220R",
            confidence: 0.82f,
            reason: "closest match",
            provenance: "buyer description evidence");

        result.Should().NotBeNull();
        result!.SupplierItemCode.Should().Be("ES-RES-220R");
        result.Confidence.Should().BeApproximately(0.82f, 0.0001f);
        // Provenance names the matched catalog row, not the vague model text.
        result.Provenance.Should().Contain("ES-RES-220R");
        result.Provenance.Should().Contain("Resistor 220Ω 0.25W");
        result.Provenance.Should().NotContain("buyer description evidence");
    }

    [Fact]
    public void CatalogGuard_RejectsCodeNotInCatalog()
    {
        // The model hallucinated a plausible-looking code that does not exist in the catalog.
        var result = OpenAiMappingService.ApplyCatalogGuardForTest(
            CatalogCandidates,
            suggestedCode: "ES-RES-999X",
            confidence: 0.95f,
            reason: "looks right",
            provenance: "model");

        result.Should().BeNull("a code absent from the supplier catalog must never surface");
    }

    [Fact]
    public void CatalogGuard_MatchIsCaseInsensitiveAndTrimmed()
    {
        var result = OpenAiMappingService.ApplyCatalogGuardForTest(
            CatalogCandidates,
            suggestedCode: "  es-res-220r  ",
            confidence: 0.7f,
            reason: "",
            provenance: null);

        result.Should().NotBeNull();
        result!.SupplierItemCode.Should().Be("es-res-220r"); // trimmed; matched case-insensitively
        result.Provenance.Should().Contain("ES-RES-220R");   // names the canonical catalog row
    }

    [Fact]
    public void CatalogGuard_NoCatalog_KeepsFreeSuggestion_Unchanged()
    {
        // Only past-mapping candidates (IsCatalogProduct = false) → no allow-list, free suggestion.
        var mappingsOnly = new[]
        {
            new AiMappingCandidate("HEI-PLT-08", "ACM-PLT-080", "existing mapping"),
        };

        var result = OpenAiMappingService.ApplyCatalogGuardForTest(
            mappingsOnly,
            suggestedCode: "ANYTHING-THE-MODEL-WANTS",
            confidence: 0.6f,
            reason: "weak",
            provenance: "buyer code/description signals");

        result.Should().NotBeNull("with no catalog the guard is inert (offer ⇔ works)");
        result!.SupplierItemCode.Should().Be("ANYTHING-THE-MODEL-WANTS");
        result.Provenance.Should().Be("buyer code/description signals"); // model provenance preserved
    }

    [Fact]
    public void CatalogGuard_EmptyCode_ReturnsNull_RegardlessOfCatalog()
    {
        OpenAiMappingService.ApplyCatalogGuardForTest(CatalogCandidates, "", 0.9f, "r", "p")
            .Should().BeNull();
        OpenAiMappingService.ApplyCatalogGuardForTest(CatalogCandidates, "   ", 0.9f, "r", "p")
            .Should().BeNull();
    }

    // ── Manufacturer part numbers inside the allow-list ─────────────────────────
    // A punchout line often carries ONLY the manufacturer's part number, so that is the
    // identifier the model can actually recognise. Answering with it used to be indistinguishable
    // from a hallucination and was thrown away. The allow-list now also indexes each catalog
    // candidate's normalised MPN, so such an answer is TRANSLATED into the supplier code that
    // owns it — while a manufacturer's number can still never leave here posing as a supplier
    // code, and an MPN that names more than one product resolves to nothing at all.

    private static readonly AiMappingCandidate[] ManufacturerCandidates =
    {
        new("", "REDACTED-ITEM", "catalog product REDACTED-ITEM",
            IsCatalogProduct: true, Name: "Zebra REDACTED-ORDER-DATA handheld scanner", Unit: "PCS", Price: 219.00m,
            ManufacturerPartNumber: "REDACTED-ORDER-DATA", ManufacturerName: "Zebra"),
        new("", "REDACTED-ITEM", "catalog product REDACTED-ITEM",
            IsCatalogProduct: true, Name: "Zebra ZD421 label printer", Unit: "PCS",
            ManufacturerPartNumber: "REDACTED-ITEM", ManufacturerName: "Zebra"),
    };

    [Fact]
    public void CatalogGuard_ManufacturerPartNumberAnswer_ResolvesToTheOwningSupplierCode()
    {
        var result = OpenAiMappingService.ApplyCatalogGuardForTest(
            ManufacturerCandidates,
            suggestedCode: "REDACTED-ORDER-DATA",
            confidence: 0.78f,
            reason: "manufacturer part number matches",
            provenance: "line carries the manufacturer number only");

        result.Should().NotBeNull("a manufacturer part number naming exactly one catalog product is a real match");
        result!.SupplierItemCode.Should().Be("REDACTED-ITEM");
        result.SupplierItemCode.Should().NotBe("REDACTED-ORDER-DATA",
            "the manufacturer's number must never surface as if it were the supplier's own code");
        // Provenance names the resolved catalog row, not the model's vague text.
        result.Provenance.Should().Contain("REDACTED-ITEM");
        result.Provenance.Should().Contain("Zebra REDACTED-ORDER-DATA handheld scanner");
        result.Provenance.Should().NotContain("line carries the manufacturer number only");
        result.Confidence.Should().BeApproximately(0.78f, 0.0001f);
    }

    [Fact]
    public void CatalogGuard_ManufacturerPartNumberAnswer_ResolvesAcrossSeparatorAndCaseDifferences()
    {
        // The distributor's feed spells the part with no separators and in lower case; the model
        // answered with the punchout line's spelling. Literal comparison finds nothing — the
        // normalised key is what makes the two meet.
        var candidates = new[]
        {
            new AiMappingCandidate("", "REDACTED-ITEM", "catalog product REDACTED-ITEM",
                IsCatalogProduct: true, Name: "Zebra REDACTED-ORDER-DATA handheld scanner",
                ManufacturerPartNumber: "qbt2500bkbtk1", ManufacturerName: "Zebra"),
        };

        var result = OpenAiMappingService.ApplyCatalogGuardForTest(
            candidates,
            suggestedCode: "REDACTED-ORDER-DATA",
            confidence: 0.71f,
            reason: "same manufacturer part, different punctuation",
            provenance: "model");

        result.Should().NotBeNull();
        result!.SupplierItemCode.Should().Be("REDACTED-ITEM");
        result.Provenance.Should().Contain("REDACTED-ITEM");
    }

    [Fact]
    public void CatalogGuard_ManufacturerPartNumberHeldByTwoProducts_IsRejectedAsAmbiguous()
    {
        // The same manufacturer part sold under two supplier codes (different pack sizes, an EU
        // and a UK variant …). The part number names no single product, so it must resolve to
        // nothing rather than silently picking whichever row was seen first. The spellings differ
        // so this also proves ambiguity is detected on the NORMALISED key, not the raw string.
        var ambiguous = new[]
        {
            new AiMappingCandidate("", "REDACTED-ITEM", "catalog product REDACTED-ITEM",
                IsCatalogProduct: true, Name: "Zebra REDACTED-ORDER-DATA scanner (EU)",
                ManufacturerPartNumber: "REDACTED-ORDER-DATA"),
            new AiMappingCandidate("", "REDACTED-ITEM", "catalog product REDACTED-ITEM",
                IsCatalogProduct: true, Name: "Zebra REDACTED-ORDER-DATA scanner (UK)",
                ManufacturerPartNumber: "qbt2500 bk btk1"),
        };

        OpenAiMappingService.ApplyCatalogGuardForTest(
                ambiguous, "REDACTED-ORDER-DATA", 0.93f, "manufacturer part match", "model")
            .Should().BeNull("a part number held by two supplier codes names no single product");

        // …and the allow-list is still live for the same candidate set: answering with either
        // real supplier code succeeds. Without this, the rejection above could equally be caused
        // by a broken allow-list rather than by the ambiguity rule.
        OpenAiMappingService.ApplyCatalogGuardForTest(
                ambiguous, "REDACTED-ITEM", 0.93f, "operator picked the UK variant", "model")
            .Should().NotBeNull();
    }

    [Fact]
    public void CatalogGuard_StillRejectsACodeThatIsNeitherACatalogCodeNorAnyCandidatesMpn()
    {
        // The MPN index widened what counts as a match; it must not have widened it to anything
        // that merely looks like a part number. This runs against candidates that DO carry MPNs,
        // so the manufacturer lookup is populated and genuinely consulted before the rejection.
        OpenAiMappingService.ApplyCatalogGuardForTest(
                ManufacturerCandidates, "REDACTED-ORDER-DATA-XX-ZZ9", 0.95f, "looks right", "model")
            .Should().BeNull("a plausible-looking part number no catalog row claims is still a hallucination");

        OpenAiMappingService.ApplyCatalogGuardForTest(
                ManufacturerCandidates, "REDACTED-ITEM", 0.95f, "looks right", "model")
            .Should().BeNull("a plausible-looking supplier code no catalog row claims is still a hallucination");
    }

    [Fact]
    public void CatalogGuard_RealCatalogCode_StillPassesThroughUnchanged_WhenCandidatesCarryMpns()
    {
        var result = OpenAiMappingService.ApplyCatalogGuardForTest(
            ManufacturerCandidates,
            suggestedCode: "REDACTED-ITEM",
            confidence: 0.88f,
            reason: "description match",
            provenance: "buyer description evidence");

        result.Should().NotBeNull();
        result!.SupplierItemCode.Should().Be("REDACTED-ITEM");
        result.Provenance.Should().Contain("REDACTED-ITEM");
        result.Provenance.Should().Contain("Zebra ZD421 label printer");
    }

    [Fact]
    public void CatalogGuard_IgnoresManufacturerPartNumbersOnNonCatalogCandidates()
    {
        // Past-mapping candidates are not catalog rows: they are what the org did before, not
        // what the supplier sells today. Their manufacturer numbers must not become allow-list
        // entries, or a discontinued part could be suggested as if it were in stock.
        var mixed = new[]
        {
            new AiMappingCandidate("", "REDACTED-ITEM", "catalog product REDACTED-ITEM",
                IsCatalogProduct: true, Name: "Zebra REDACTED-ORDER-DATA handheld scanner"),
            new AiMappingCandidate("HEI-SCAN-01", "REDACTED-ITEM", "existing mapping",
                IsCatalogProduct: false, Name: "Zebra LS2208 (discontinued)",
                ManufacturerPartNumber: "REDACTED-ITEM"),
        };

        OpenAiMappingService.ApplyCatalogGuardForTest(
                mixed, "REDACTED-ITEM", 0.9f, "manufacturer part match", "model")
            .Should().BeNull("only catalog rows may contribute manufacturer part numbers to the allow-list");

        // The catalog row itself is still reachable, so the guard has not simply gone inert.
        OpenAiMappingService.ApplyCatalogGuardForTest(
                mixed, "REDACTED-ITEM", 0.9f, "description match", "model")
            .Should().NotBeNull();
    }

    private static OpenAiMappingService CreateService(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new OpenAiMappingService(
            configuration,
            NullLogger<OpenAiMappingService>.Instance);
    }

    private static OpenAiMappingService CreateServiceWithNoEgress(
        Dictionary<string, string?> values,
        IAiUsageTracker?            tracker,
        bool                        noEgress)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        // Internal test ctor: a configured key constructs a real ChatClient (so we get
        // past the _client-null guard), and noEgressCheck forces the short-circuit
        // without a DbContext. If the gate failed, the strict tracker / live client
        // would be reached and the test would fail.
        return new OpenAiMappingService(
            configuration,
            NullLogger<OpenAiMappingService>.Instance,
            tracker,
            overrideClient: null,
            noEgressCheck: noEgress ? (_, _) => Task.FromResult(true) : null);
    }
}
