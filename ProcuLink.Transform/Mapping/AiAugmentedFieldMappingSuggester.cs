using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Mapping;

/// <summary>
/// Decorates the deterministic <see cref="HeuristicFieldMappingSuggester"/> with an
/// optional OpenAI refinement pass.
///
/// <para>
/// It always runs the heuristic matcher first. When <see cref="IAiMappingService"/>
/// is configured, it asks the AI to fill in the canonical fields the heuristic left
/// unresolved (or matched with low confidence), and merges any confident AI answers
/// in with provenance <c>"ai"</c>. When AI is not configured the AI call is a no-op
/// (the OpenAI implementation returns an empty list) and the result is heuristic-only —
/// so this degrades gracefully and never requires an AI key.
/// </para>
///
/// <para>
/// Lives in ProcuLink.Transform alongside the heuristic matcher; it depends only on
/// the provider-neutral Core <see cref="IAiMappingService"/> contract, not on any
/// concrete provider, so there is no Infrastructure dependency or project cycle.
/// </para>
/// </summary>
public sealed class AiAugmentedFieldMappingSuggester : IFieldMappingSuggester
{
    /// <summary>
    /// Heuristic matches at or above this confidence are considered "good enough" and
    /// are NOT sent to the AI for refinement. Below it, the field is treated as a
    /// candidate for AI augmentation.
    /// </summary>
    private const double HeuristicTrustThreshold = 0.80;

    /// <summary>The full set of canonical PO fields the AI may be asked to map.</summary>
    private static readonly IReadOnlyList<string> AllCanonicalFields = new[]
    {
        "PoNumber", "OrderDate", "BuyerName", "Currency",
        "LineNumber", "BuyerItemCode", "Description", "Quantity", "Unit", "UnitPrice",
    };

    private readonly IAiMappingService _ai;

    public AiAugmentedFieldMappingSuggester(IAiMappingService ai)
    {
        _ai = ai;
    }

    public async Task<IReadOnlyList<FieldMappingSuggestion>> SuggestFieldMappingsAsync(
        Guid organisationId,
        Guid supplierId,
        IReadOnlyList<string> columns,
        CancellationToken ct = default)
    {
        // 1) Deterministic baseline — always runs, never throws on empty input.
        var heuristic = HeuristicFieldMappingSuggester.Suggest(columns);

        if (columns is null || columns.Count == 0)
            return heuristic;

        // 2) Which canonical fields are still weak / missing? Those are AI candidates.
        var confidentByField = heuristic
            .Where(s => s.Confidence >= HeuristicTrustThreshold)
            .ToDictionary(s => s.CanonicalField, StringComparer.Ordinal);

        var unresolved = AllCanonicalFields
            .Where(f => !confidentByField.ContainsKey(f))
            .ToList();

        if (unresolved.Count == 0)
            return heuristic; // heuristic already nailed everything confidently

        IReadOnlyList<AiFieldMappingSuggestion> aiSuggestions;
        try
        {
            aiSuggestions = await _ai.SuggestFieldMappingsAsync(
                organisationId, supplierId, columns, unresolved, ct);
        }
        catch
        {
            // The AI service already swallows and logs its own errors, but be
            // defensive: never let an AI failure break the deterministic result.
            return heuristic;
        }

        if (aiSuggestions.Count == 0)
            return heuristic; // no AI key, or AI declined — heuristic-only

        return Merge(heuristic, aiSuggestions, confidentByField, columns);
    }

    /// <summary>
    /// Merges AI answers into the heuristic baseline. AI may only fill fields the
    /// heuristic was NOT confident about, and may not steal a source column the
    /// heuristic already assigned confidently. AI rows are tagged source="ai".
    /// </summary>
    private static IReadOnlyList<FieldMappingSuggestion> Merge(
        IReadOnlyList<FieldMappingSuggestion> heuristic,
        IReadOnlyList<AiFieldMappingSuggestion> ai,
        IReadOnlyDictionary<string, FieldMappingSuggestion> confidentByField,
        IReadOnlyList<string> columns)
    {
        var allowedColumns = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        var allowedFields = new HashSet<string>(AllCanonicalFields, StringComparer.Ordinal);

        // Start from the confident heuristic matches; index by field for override.
        var byField = new Dictionary<string, FieldMappingSuggestion>(confidentByField, StringComparer.Ordinal);

        // Keep the weak heuristic matches too — AI overrides them only when it has a pick.
        foreach (var h in heuristic)
            byField.TryAdd(h.CanonicalField, h);

        // Columns locked by confident heuristic matches cannot be reused by AI.
        var usedColumns = new HashSet<string>(
            confidentByField.Values.Select(s => s.SuggestedColumn),
            StringComparer.OrdinalIgnoreCase);

        foreach (var s in ai
                     .Where(s => allowedFields.Contains(s.CanonicalField)
                                 && allowedColumns.Contains(s.SuggestedColumn))
                     .OrderByDescending(s => s.Confidence))
        {
            // AI cannot override a confident heuristic field, nor reuse a used column.
            if (confidentByField.ContainsKey(s.CanonicalField))
                continue;
            if (usedColumns.Contains(s.SuggestedColumn))
                continue;

            usedColumns.Add(s.SuggestedColumn);
            byField[s.CanonicalField] = new FieldMappingSuggestion(
                s.CanonicalField,
                s.SuggestedColumn,
                Math.Round(Math.Clamp((double)s.Confidence, 0, 1), 3),
                string.IsNullOrWhiteSpace(s.Reason) ? "AI-suggested mapping" : s.Reason,
                "ai");
        }

        var orderIndex = AllCanonicalFields
            .Select((f, i) => (f, i))
            .ToDictionary(x => x.f, x => x.i);

        // If two fields ended up on the same column (weak heuristic vs ai), keep the
        // higher-confidence one so a column is never suggested for two fields.
        return byField.Values
            .GroupBy(s => s.SuggestedColumn, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderByDescending(s => s.Confidence).Take(1))
            .OrderBy(s => orderIndex.TryGetValue(s.CanonicalField, out var i) ? i : int.MaxValue)
            .ToList();
    }
}
