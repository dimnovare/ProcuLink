using ProcuLink.Core.Services.Mapping;

namespace ProcuLink.Transform.Mapping;

/// <summary>
/// Deterministic, AI-free implementation of <see cref="IFieldMappingSuggester"/>.
///
/// <para>
/// For every canonical PO field it scores each candidate source column by
/// normalized token similarity (lowercase, separators/underscores stripped,
/// plus a curated synonym/substring table per field) and picks the highest-scoring
/// source column. Each source column is assigned to at most one canonical field —
/// the best (canonicalField, column) pair wins, so the same column is not claimed twice.
/// </para>
///
/// <para>
/// This type has no dependencies and is safe to call with no AI key configured.
/// It is also exposed as a static <see cref="Score"/> helper so an AI decorator
/// can reuse the same heuristic scoring.
/// </para>
/// </summary>
public sealed class HeuristicFieldMappingSuggester : IFieldMappingSuggester
{
    /// <summary>
    /// Below this score a match is considered too weak to surface at all — the single
    /// accept floor for everything <c>POST /api/suppliers/{id}/mapping/suggest-fields</c>
    /// returns, whatever the provenance. <see cref="AiAugmentedFieldMappingSuggester"/>
    /// applies the same number to AI answers, and the mapper editor renders every
    /// suggestion it receives without a floor of its own.
    ///
    /// <para>
    /// It is 0.50 because that is where the editor's own <c>ADOPT_THRESHOLD</c> stood while
    /// this constant said 0.45: two floors, neither aware of the other, and the lower one
    /// therefore dead. Whoever changes this number owns both ends — a backend floor below
    /// the number the editor renders from means suggestions that are scored, serialized,
    /// sent, and then silently dropped, which is indistinguishable to the operator from
    /// the backend having had no candidate at all.
    /// </para>
    ///
    /// <para>
    /// Raising 0.45 → 0.50 discarded nothing that this scorer can produce: the weakest
    /// non-zero score is <c>0.5 + 0.4/|Tokens| - 0.05</c> for a single token hit on a
    /// long header, which stays at or above 0.50 while no field carries more than eight
    /// signal tokens. That bound is not a happy accident to rely on silently — it is
    /// pinned by <c>HeuristicFieldMappingSuggesterTests</c>.
    /// </para>
    /// </summary>
    public const double MinAcceptScore = 0.50;

    /// <summary>
    /// The canonical fields and their signal tokens, exposed read-only so the token-count
    /// bound behind <see cref="MinAcceptScore"/> can be asserted rather than assumed.
    /// </summary>
    public static IReadOnlyList<CanonicalField> CanonicalFields => Fields;

    /// <summary>
    /// Canonical PO fields in display order (header first, then line fields), each
    /// with the tokens/aliases that signal a match. Tokens are already normalized
    /// (lowercase, no separators). Order here is the order suggestions are returned in.
    ///
    /// <para>
    /// A field's <c>tokens</c> list must stay at or under eight entries: the weakest
    /// partial match it can produce is <c>0.45 + 0.4/|tokens|</c>, which drops below
    /// <see cref="MinAcceptScore"/> at nine. Pinned by
    /// <c>HeuristicFieldMappingSuggesterTests.NoCanonicalFieldHasMoreSignalTokensThanTheFloorAllows</c>.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<CanonicalField> Fields = new[]
    {
        // ── Header fields (ParsedOrder) ──────────────────────────────────────
        new CanonicalField("PoNumber",
            exact: new[] { "ponumber", "purchaseordernumber", "ordernumber", "orderno", "pono", "ponum", "orderid", "purchaseorderno", "poref" },
            tokens: new[] { "po", "purchase", "order" },
            mustHaveAny: new[] { "po", "order", "purchase" }),

        new CanonicalField("OrderDate",
            exact: new[] { "orderdate", "podate", "purchaseorderdate", "documentdate", "issuedate", "createddate", "dateordered" },
            tokens: new[] { "date", "ordered" },
            mustHaveAny: new[] { "date" }),

        new CanonicalField("BuyerName",
            exact: new[] { "buyername", "buyer", "customername", "customer", "billto", "billtoname", "company", "companyname", "purchaser" },
            tokens: new[] { "buyer", "customer", "company", "purchaser" },
            mustHaveAny: new[] { "buyer", "customer", "company", "purchaser", "billto" }),

        new CanonicalField("Currency",
            exact: new[] { "currency", "currencycode", "curr", "ccy", "iso4217" },
            tokens: new[] { "currency", "curr", "ccy" },
            mustHaveAny: new[] { "currency", "curr", "ccy" }),

        // ── Line fields (ParsedOrderLine) ────────────────────────────────────
        new CanonicalField("LineNumber",
            exact: new[] { "linenumber", "lineno", "line", "linenum", "rownumber", "rowno", "row", "seq", "sequence", "position", "pos", "itemnumber", "itemno", "lineid" },
            tokens: new[] { "line", "row", "seq", "position", "pos" },
            mustHaveAny: new[] { "line", "row", "seq", "position", "pos" }),

        new CanonicalField("BuyerItemCode",
            exact: new[] { "buyeritemcode", "itemcode", "sku", "article", "articlenumber", "articleno", "itemid", "item", "productcode", "product", "partnumber", "partno", "code", "mpn", "ean", "gtin", "barcode", "material", "materialnumber" },
            tokens: new[] { "item", "sku", "article", "product", "part", "code", "material" },
            mustHaveAny: new[] { "item", "sku", "article", "product", "part", "code", "material", "ean", "gtin", "mpn", "barcode" }),

        new CanonicalField("Description",
            exact: new[] { "description", "desc", "itemdescription", "productdescription", "productname", "itemname", "name", "details", "text", "longdescription" },
            tokens: new[] { "description", "desc", "name", "details", "text" },
            mustHaveAny: new[] { "description", "desc", "name", "details", "text" }),

        new CanonicalField("Quantity",
            exact: new[] { "quantity", "qty", "quantityordered", "orderqty", "orderedquantity", "qtyordered", "amount", "count", "units", "noofunits" },
            tokens: new[] { "quantity", "qty", "ordered", "count", "units" },
            mustHaveAny: new[] { "quantity", "qty", "count", "units" }),

        new CanonicalField("Unit",
            exact: new[] { "unit", "uom", "unitofmeasure", "measure", "unitofmeasurement", "packunit", "uomcode" },
            tokens: new[] { "unit", "uom", "measure" },
            mustHaveAny: new[] { "uom", "measure", "unitofmeasure" },
            // bare "unit" is ambiguous vs unit price; require it NOT be a price column
            negativeTokens: new[] { "price", "cost", "amount", "value", "net", "gross" }),

        new CanonicalField("UnitPrice",
            exact: new[] { "unitprice", "price", "priceeach", "unitcost", "cost", "netprice", "net", "rate", "listprice", "priceperunit", "ppu", "unitamount" },
            tokens: new[] { "price", "cost", "net", "rate", "amount", "value" },
            mustHaveAny: new[] { "price", "cost", "net", "rate" }),
    };

    public Task<IReadOnlyList<FieldMappingSuggestion>> SuggestFieldMappingsAsync(
        Guid organisationId,
        Guid supplierId,
        IReadOnlyList<string> columns,
        CancellationToken ct = default)
        => Task.FromResult(Suggest(columns));

    /// <summary>
    /// Synchronous core. Greedily assigns each source column to at most one canonical
    /// field, taking the globally highest-scoring pairs first, and returns suggestions
    /// in canonical field display order.
    /// </summary>
    public static IReadOnlyList<FieldMappingSuggestion> Suggest(IReadOnlyList<string>? columns)
    {
        if (columns is null || columns.Count == 0)
            return Array.Empty<FieldMappingSuggestion>();

        // Distinct, non-blank source columns paired with their normalized form.
        var candidates = columns
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => (Original: c, Normalized: Normalize(c)))
            .Where(c => c.Normalized.Length > 0)
            .ToList();

        if (candidates.Count == 0)
            return Array.Empty<FieldMappingSuggestion>();

        // Score every (field, column) pair, keep those above the accept threshold.
        var scored = new List<ScoredPair>();
        foreach (var field in Fields)
        {
            foreach (var cand in candidates)
            {
                var (score, reason) = Score(field, cand.Normalized);
                if (score >= MinAcceptScore)
                    scored.Add(new ScoredPair(field.Name, cand.Original, score, reason));
            }
        }

        // Greedy global assignment: best pairs first; each field and each column used once.
        var orderIndex = Fields
            .Select((f, i) => (f.Name, i))
            .ToDictionary(x => x.Name, x => x.i);

        var takenFields = new HashSet<string>(StringComparer.Ordinal);
        var takenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chosen = new List<FieldMappingSuggestion>();

        foreach (var pair in scored
                     .OrderByDescending(p => p.Score)
                     .ThenBy(p => orderIndex[p.Field]))
        {
            if (takenFields.Contains(pair.Field) || takenColumns.Contains(pair.Column))
                continue;

            takenFields.Add(pair.Field);
            takenColumns.Add(pair.Column);
            chosen.Add(new FieldMappingSuggestion(
                pair.Field,
                pair.Column,
                Math.Round(pair.Score, 3),
                pair.Reason,
                "heuristic"));
        }

        // Return in canonical field display order, not score order.
        return chosen
            .OrderBy(s => orderIndex[s.CanonicalField])
            .ToList();
    }

    /// <summary>
    /// Scores a single (canonical field, normalized source column) pair in [0, 1]
    /// and produces a short reason. Public so an AI decorator can reuse it.
    /// </summary>
    public static (double Score, string Reason) Score(CanonicalField field, string normalizedColumn)
    {
        if (string.IsNullOrEmpty(normalizedColumn))
            return (0, "empty column");

        // Hard negative: e.g. "unit price" must not win Unit.
        if (field.NegativeTokens.Any(neg => normalizedColumn.Contains(neg, StringComparison.Ordinal)))
            return (0, "excluded by negative token");

        // 1) Exact normalized alias hit → near-certain.
        if (field.Exact.Contains(normalizedColumn))
            return (0.97, $"\"{normalizedColumn}\" is a known alias for {field.Name}");

        // 2) Source column equals an alias plus surrounding noise (contains an alias as a whole chunk).
        //    e.g. "customerponumber" contains "ponumber".
        foreach (var alias in field.Exact)
        {
            if (alias.Length >= 4 && normalizedColumn.Contains(alias, StringComparison.Ordinal))
                return (0.85, $"contains the alias \"{alias}\" for {field.Name}");
        }

        // 3) Token-overlap (Jaccard-ish) scoring against the field's signal tokens,
        //    gated by mustHaveAny so unrelated columns score 0.
        var hasRequired = field.MustHaveAny.Count == 0
                          || field.MustHaveAny.Any(t => normalizedColumn.Contains(t, StringComparison.Ordinal));
        if (!hasRequired)
            return (0, "no signal token for this field");

        var hits = field.Tokens.Count(t => normalizedColumn.Contains(t, StringComparison.Ordinal));
        if (hits == 0)
            return (0, "no signal token for this field");

        // Base score scales with how many distinct signal tokens matched, with a
        // length penalty so very long noisy headers score slightly lower.
        var coverage = (double)hits / field.Tokens.Count;
        var lengthPenalty = normalizedColumn.Length > 24 ? 0.05 : 0.0;
        var score = Math.Clamp(0.5 + 0.4 * coverage - lengthPenalty, 0, 0.9);

        return (score, $"matched {hits} signal token(s) for {field.Name}");
    }

    /// <summary>
    /// Lowercases and strips everything except ASCII letters and digits — so
    /// "PO_Number", "po number", "PO-Number", and "po.number" all collapse to "ponumber".
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        Span<char> buffer = raw.Length <= 128 ? stackalloc char[raw.Length] : new char[raw.Length];
        var len = 0;
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[len++] = char.ToLowerInvariant(ch);
        }

        return len == 0 ? string.Empty : new string(buffer[..len]);
    }

    private readonly record struct ScoredPair(string Field, string Column, double Score, string Reason);

    /// <summary>
    /// A canonical PO field and the normalized tokens/aliases that signal a match.
    /// </summary>
    public sealed class CanonicalField
    {
        public CanonicalField(
            string name,
            IReadOnlyList<string> exact,
            IReadOnlyList<string> tokens,
            IReadOnlyList<string> mustHaveAny,
            IReadOnlyList<string>? negativeTokens = null)
        {
            Name = name;
            Exact = exact;
            Tokens = tokens;
            MustHaveAny = mustHaveAny;
            NegativeTokens = negativeTokens ?? Array.Empty<string>();
        }

        /// <summary>Canonical PO field name (e.g. <c>PoNumber</c>).</summary>
        public string Name { get; }

        /// <summary>Normalized aliases that, when matched exactly, signal near-certainty.</summary>
        public IReadOnlyList<string> Exact { get; }

        /// <summary>Normalized signal tokens used for partial overlap scoring.</summary>
        public IReadOnlyList<string> Tokens { get; }

        /// <summary>At least one of these normalized tokens must be present for a non-exact match.</summary>
        public IReadOnlyList<string> MustHaveAny { get; }

        /// <summary>If any of these normalized tokens is present, the field is excluded (score 0).</summary>
        public IReadOnlyList<string> NegativeTokens { get; }
    }
}
