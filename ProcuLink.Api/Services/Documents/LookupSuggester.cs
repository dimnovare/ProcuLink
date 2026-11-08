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
    public const int MaxMisses = 25, PerMiss = 8, PerCall = 5, MaxContext = 500, WholeListMax = 40;

    private sealed record Ranked(int Order, LookupMiss Miss, string Context, List<LookupCandidate> Candidates);

    public async Task<LookupSuggestionResult> SuggestAsync(Guid orgId, IReadOnlyList<LookupMiss> misses, IReadOnlyDictionary<string, string>? header, CancellationToken ct)
    {
        if (misses.Count == 0) return new([], false);
        if (await usage.IsAtOrOverLimitAsync(orgId, ct)) return new([], true);

        var ids = misses.Select(m => m.LookupId).Distinct().ToList();
        var lookups = await db.Set<Lookup>().AsNoTracking()
            .Where(l => l.OrgId == orgId && ids.Contains(l.Id) && l.CandidatesJson != "[]").ToListAsync(ct);

        // One global bound per call, counted over usable misses only (their lookup has a list); original index kept for ordering.
        var loaded = lookups.Select(l => l.Id).ToHashSet();
        var usable = misses.Select((m, i) => (m, i)).Where(x => loaded.Contains(x.m.LookupId)).Take(MaxMisses).ToList();

        var found = new List<(int Order, LookupSuggestion Suggestion)>();
        foreach (var lookup in lookups)
        {
            var candidates = DocumentJson.Deserialize<List<LookupCandidate>>(lookup.CandidatesJson);
            var ranked = new List<Ranked>();
            foreach (var (miss, order) in usable)
            {
                if (miss.LookupId != lookup.Id) continue;
                var cells = Cells(miss, header);
                var context = string.Join(" · ", cells.Select(kv => $"{kv.Key}: {kv.Value}"));
                if (context.Length > MaxContext) context = context[..MaxContext];
                if (candidates.Count <= WholeListMax) { ranked.Add(new(order, miss, context, candidates)); continue; }
                var tokens = LexicalScore.Tokenize($"{miss.Value} {string.Join(' ', cells.Select(kv => kv.Value))}");
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
                    var match = pool.FirstOrDefault(c => string.Equals(c.Value, s.SupplierItemCode, StringComparison.OrdinalIgnoreCase));
                    found.Add((chunk[i].Order, new(lookup.Id, chunk[i].Miss.Value, match?.Value ?? s.SupplierItemCode, s.Confidence, match?.Description)));
                }
            }
        }
        return new(found.OrderBy(f => f.Order).Select(f => f.Suggestion).ToList(), false);
    }

    /// <summary>The row's own cells: header pairs (equal key and value) are dropped, unless nothing else is left. Blanks and the miss itself are skipped.</summary>
    private static List<KeyValuePair<string, string>> Cells(LookupMiss miss, IReadOnlyDictionary<string, string>? header)
    {
        var all = (miss.Row ?? new Dictionary<string, string>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && kv.Value.Trim() != miss.Value.Trim()).ToList();
        var own = header is null ? all : all.Where(kv => !(header.TryGetValue(kv.Key, out var h) && h == kv.Value)).ToList();
        return own.Count > 0 ? own : all;
    }
}
