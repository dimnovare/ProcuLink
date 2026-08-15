using FluentAssertions;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Mapping;

namespace ProcuLink.Transform.Tests.Mapping;

/// <summary>
/// <see cref="AiAugmentedFieldMappingSuggester"/> is the implementation actually wired in
/// <c>Program.cs</c>, so it — not the bare heuristic — defines what
/// <c>POST /api/suppliers/{id}/mapping/suggest-fields</c> puts on the wire.
///
/// <para>
/// The contract these tests pin is a single one: <b>every suggestion this returns, whatever
/// its provenance, is at or above <see cref="HeuristicFieldMappingSuggester.MinAcceptScore"/></b>.
/// That is what makes the backend floor authoritative. Before this floor existed on the AI
/// path, the decorator merged any AI confidence in [0, 1] verbatim, so a 0.10 answer was
/// scored, serialized, sent, and then dropped unrendered by the mapper editor — a candidate
/// the operator was never told about and could not have asked to see.
/// </para>
/// </summary>
public class AiAugmentedFieldMappingSuggesterTests
{
    private const double Floor = HeuristicFieldMappingSuggester.MinAcceptScore;

    /// <summary>Columns with no signal token for any canonical field: the heuristic returns nothing,
    /// so every canonical field is unresolved and the AI is asked for all of them.</summary>
    private static readonly string[] OpaqueColumns = { "Alpha", "Beta", "Gamma" };

    private static Task<IReadOnlyList<FieldMappingSuggestion>> SuggestAsync(
        IReadOnlyList<string> columns,
        params AiFieldMappingSuggestion[] aiAnswers)
        => new AiAugmentedFieldMappingSuggester(new StubAiMappingService(aiAnswers))
            .SuggestFieldMappingsAsync(Guid.NewGuid(), Guid.NewGuid(), columns);

    // ── The floor applies to the AI path, not just the heuristic one ───────────

    [Fact]
    public async Task Suggest_DropsAiAnswerBelowTheFloor_SoNothingUnrenderableIsSent()
    {
        var result = await SuggestAsync(
            OpaqueColumns,
            new AiFieldMappingSuggestion("PoNumber", "Alpha", 0.30f, "weak hunch"),
            new AiFieldMappingSuggestion("Quantity", "Beta", 0.90f, "confident"));

        result.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                CanonicalField = "Quantity",
                SuggestedColumn = "Beta",
                Source = "ai",
            });
    }

    [Fact]
    public async Task Suggest_KeepsAiAnswerExactlyAtTheFloor()
    {
        var result = await SuggestAsync(
            OpaqueColumns,
            new AiFieldMappingSuggestion("PoNumber", "Alpha", (float)Floor, "borderline"));

        result.Should().ContainSingle().Which.CanonicalField.Should().Be("PoNumber");
    }

    [Fact]
    public async Task Suggest_DropsAiAnswerJustBelowTheFloor()
    {
        var result = await SuggestAsync(
            OpaqueColumns,
            new AiFieldMappingSuggestion("PoNumber", "Alpha", (float)Floor - 0.01f, "borderline"));

        result.Should().BeEmpty();
    }

    // ── The whole-output contract, both provenances at once ───────────────────

    [Fact]
    public async Task Suggest_NeverReturnsAnythingBelowTheFloor_AcrossBothProvenances()
    {
        // Real headers: "PO Number" and "Qty" resolve heuristically; the AI is asked for the rest
        // and answers with a spread of confidences straddling the floor.
        var result = await SuggestAsync(
            new[] { "PO Number", "Qty", "Alpha", "Beta", "Gamma" },
            new AiFieldMappingSuggestion("BuyerName", "Alpha", 0.05f, "guess"),
            new AiFieldMappingSuggestion("Currency", "Beta", 0.49f, "guess"),
            new AiFieldMappingSuggestion("Description", "Gamma", 0.75f, "plausible"));

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(s => s.Confidence >= Floor);
        result.Select(s => s.CanonicalField).Should().NotContain(new[] { "BuyerName", "Currency" });
        result.Select(s => s.CanonicalField).Should().Contain("Description");
    }

    [Fact]
    public async Task Suggest_FallsBackToHeuristicOnly_WhenAiDeclines()
    {
        var result = await SuggestAsync(new[] { "PO Number", "Qty", "Unit Price" });

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(s => s.Source == "heuristic");
        result.Should().OnlyContain(s => s.Confidence >= Floor);
    }

    private sealed class StubAiMappingService : IAiMappingService
    {
        private readonly IReadOnlyList<AiFieldMappingSuggestion> _answers;

        public StubAiMappingService(IReadOnlyList<AiFieldMappingSuggestion> answers) => _answers = answers;

        public Task<IReadOnlyList<AiFieldMappingSuggestion>> SuggestFieldMappingsAsync(
            Guid organisationId,
            Guid supplierId,
            IReadOnlyList<string> columns,
            IReadOnlyList<string> unresolvedCanonicalFields,
            CancellationToken ct = default)
            => Task.FromResult(_answers);

        public Task<AiMappingSuggestion?> SuggestSupplierItemCodeAsync(
            Guid organisationId,
            Guid supplierId,
            string supplierName,
            AiMappingLineContext line,
            IReadOnlyList<AiMappingCandidate> candidates,
            CancellationToken ct = default)
            => Task.FromResult<AiMappingSuggestion?>(null);

        public Task<IReadOnlyDictionary<int, AiMappingSuggestion>> SuggestSupplierItemCodesAsync(
            Guid organisationId,
            Guid supplierId,
            string supplierName,
            IReadOnlyList<AiMappingLineContext> lines,
            IReadOnlyList<AiMappingCandidate> candidates,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<int, AiMappingSuggestion>>(
                new Dictionary<int, AiMappingSuggestion>());
    }
}
