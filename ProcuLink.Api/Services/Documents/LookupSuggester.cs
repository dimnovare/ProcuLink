using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Catalog;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Mapping;

namespace ProcuLink.Api.Services.Documents;

public sealed record LookupSuggestion(Guid LookupId, string Value, string To, float? Confidence, string? Description);

public sealed record LookupSuggestionResult(IReadOnlyList<LookupSuggestion> Suggestions, bool CapReached);

/// <summary>
/// Proposes a match for each value a lookup missed, choosing only from that lookup's own list of values. Reuses the
/// order product's AI mapping service (usage accounting, no-egress skip and allow-list live there). Never applies anything.
/// </summary>
public sealed class LookupSuggester(ProcuLinkDbContext db, IAiMappingService ai, IAiUsageTracker usage)
{
    public const int MaxMisses = 25, PerMiss = 8, PerCall = 5, MaxContext = 500;

    private sealed record Ranked(int Order, LookupMiss Miss, string Context, List<LookupCandidate> Candidates);

    public async Task<LookupSuggestionResult> SuggestAsync(Guid orgId, IReadOnlyList<LookupMiss> misses, CancellationToken ct)
    {
        if (misses.Count == 0) return new([], false);
        if (await usage.IsAtOrOverLimitAsync(orgId, ct)) return new([], true);

        var ids = misses.Select(m => m.LookupId).Distinct().ToList();
        var lookups = await db.Set<Lookup>().AsNoTracking()
            .Where(l => l.OrgId == orgId && ids.Contains(l.Id) && l.CandidatesJson != "[]").ToListAsync(ct);

        var found = new List<(int Order, LookupSuggestion Suggestion)>();
        foreach (var lookup in lookups)
        {
            var candidates = DocumentJson.Deserialize<List<LookupCandidate>>(lookup.CandidatesJson);
            var ranked = new List<Ranked>();
            for (var order = 0; order < Math.Min(misses.Count, MaxMisses); order++)
            {
                var miss = misses[order];
                if (miss.LookupId != lookup.Id) continue;
                var context = Context(miss);
                var tokens = LexicalScore.Tokenize($"{miss.Value} {context}");
                var top = candidates
                    .Select(c => (c, score: LexicalScore.Score(miss.Value, tokens, c.Value, c.Description, null)))
                    .Where(x => x.score > 0).OrderByDescending(x => x.score).Take(PerMiss).Select(x => x.c).ToList();
                if (top.Count > 0) ranked.Add(new(order, miss, context, top));
            }

            foreach (var chunk in ranked.Chunk(PerCall))
            {
                var pool = chunk.SelectMany(r => r.Candidates).DistinctBy(c => c.Value, StringComparer.OrdinalIgnoreCase).ToList();
                var lines = chunk.Select((r, i) => new AiMappingLineContext(i, r.Miss.Value, r.Context, 0m, null)).ToList();
                var options = pool.Select(c => new AiMappingCandidate("", c.Value, "lookup", IsCatalogProduct: true, Name: c.Description)).ToList();
                var answers = await ai.SuggestSupplierItemCodesAsync(orgId, Guid.Empty, lookup.Name, lines, options, ct);
                foreach (var (i, s) in answers)
                {
                    if (i < 0 || i >= chunk.Length) continue;
                    var description = pool.FirstOrDefault(c => string.Equals(c.Value, s.SupplierItemCode, StringComparison.OrdinalIgnoreCase))?.Description;
                    found.Add((chunk[i].Order, new(lookup.Id, chunk[i].Miss.Value, s.SupplierItemCode, s.Confidence, description)));
                }
            }
        }
        return new(found.OrderBy(f => f.Order).Select(f => f.Suggestion).ToList(), false);
    }

    private static string Context(LookupMiss miss)
    {
        var text = string.Join(" · ", (miss.Row ?? new Dictionary<string, string>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && kv.Value != miss.Value).Select(kv => $"{kv.Key}: {kv.Value}"));
        return text.Length > MaxContext ? text[..MaxContext] : text;
    }
}
