using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;

namespace ProcuLink.Api.Services;

public sealed class SupplierAcceptanceService : ISupplierAcceptanceService
{
    private readonly ProcuLinkDbContext _db;
    private readonly IEffectiveConnectionConfigResolver? _effectiveConfig;
    private readonly ILogger<SupplierAcceptanceService>? _logger;

    public SupplierAcceptanceService(
        ProcuLinkDbContext db,
        IEffectiveConnectionConfigResolver? effectiveConfig = null,
        ILogger<SupplierAcceptanceService>? logger = null)
    {
        _db = db;
        // Launch batch 7 — revision authority. Null (older positional test ctors / unregistered
        // hosts) behaves exactly like flag-OFF: the live active profile drives validation.
        _effectiveConfig = effectiveConfig;
        _logger = logger;
    }

    public async Task<SupplierAcceptanceProfile?> GetActiveAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        await _db.SupplierAcceptanceProfiles
            .Include(p => p.Rules)
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.Status == "active")
            .FirstOrDefaultAsync(ct);

    public async Task<SupplierAcceptanceProfile?> GetLatestAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        await _db.SupplierAcceptanceProfiles
            .Include(p => p.Rules)
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId && p.Status != "archived")
            .OrderByDescending(p => p.Status == "active")
            .ThenByDescending(p => p.VersionNo)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<SupplierAcceptanceProfile>> ListVersionsAsync(Guid orgId, Guid supplierId, CancellationToken ct) =>
        await _db.SupplierAcceptanceProfiles
            .Include(p => p.Rules)
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId)
            .OrderByDescending(p => p.VersionNo)
            .ToListAsync(ct);

    public async Task<SupplierAcceptanceProfile> CreateVersionAsync(
        Guid orgId, Guid supplierId, string? protocol, string? outputFormat,
        IReadOnlyList<AcceptanceRuleInput> rules, string? createdBy, CancellationToken ct)
    {
        var maxVersion = await _db.SupplierAcceptanceProfiles
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId)
            .Select(p => (int?)p.VersionNo)
            .MaxAsync(ct);
        var nextVersion = (maxVersion ?? 0) + 1;

        // Group V4: bind new rules to reusable RuleDefinitions so they are no longer free-floating.
        // Definitions referenced by the rules must exist FIRST (a rule's RuleDefinitionId is a
        // nullable FK to a definition) — resolve/create + save them before the profile that points
        // at them. This NEVER affects evaluation: the executor reads the rule scalar columns below,
        // which come verbatim from the input. RuleDefinitionId/RuleCode are pure provenance metadata.
        var definitionIdByCode = await ResolveDefinitionsAsync(orgId, rules, createdBy, ct);

        var profile = new SupplierAcceptanceProfile
        {
            Id = Guid.NewGuid(), OrgId = orgId, SupplierId = supplierId,
            VersionNo = nextVersion, Status = "draft",
            Protocol = protocol, OutputFormat = outputFormat,
            CreatedBy = createdBy, CreatedAt = DateTime.UtcNow,
            Rules = rules.Select(r =>
            {
                var code = RuleCatalog.CodeFor(r.FieldPath, r.Operator);
                return new SupplierAcceptanceRule
                {
                    Id = Guid.NewGuid(), Scope = r.Scope, FieldPath = r.FieldPath,
                    Operator = r.Operator, ExpectedValue = r.ExpectedValue,
                    Severity = r.Severity, BlockOnFail = r.BlockOnFail,
                    RuleDefinitionId = definitionIdByCode.TryGetValue(code, out var did) ? did : (Guid?)null,
                    RuleCode = definitionIdByCode.ContainsKey(code) ? code : null,
                };
            }).ToList(),
        };
        _db.SupplierAcceptanceProfiles.Add(profile);
        await _db.SaveChangesAsync(ct);
        return profile;
    }

    /// <summary>
    /// Group V4: ensures an org-scoped <see cref="RuleDefinition"/> exists for every distinct
    /// (fieldPath, operator) the given rules use, creating + saving any missing ones (derived from a
    /// seeded catalog template when one matches, else from the rule's own shape). Returns a
    /// code → definitionId map so callers can bind each rule. Definitions are saved here, BEFORE the
    /// rules that reference them, so there is never an ambiguous circular insert.
    /// </summary>
    private async Task<Dictionary<string, Guid>> ResolveDefinitionsAsync(
        Guid orgId, IReadOnlyList<AcceptanceRuleInput> rules, string? createdBy, CancellationToken ct)
    {
        var wantedCodes = rules
            .Select(r => RuleCatalog.CodeFor(r.FieldPath, r.Operator))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existing = await _db.RuleDefinitions
            .Where(d => d.OrgId == orgId && wantedCodes.Contains(d.Code))
            .ToListAsync(ct);
        var idByCode = existing.ToDictionary(d => d.Code, d => d.Id, StringComparer.Ordinal);

        var seedByCode = RuleCatalog.Entries.ToDictionary(e => e.Code, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var toAdd = new List<RuleDefinition>();
        foreach (var r in rules)
        {
            var code = RuleCatalog.CodeFor(r.FieldPath, r.Operator);
            if (idByCode.ContainsKey(code) || toAdd.Any(d => d.Code == code)) continue;

            RuleDefinition def;
            if (seedByCode.TryGetValue(code, out var seed))
            {
                def = new RuleDefinition
                {
                    Id = Guid.NewGuid(), OrgId = orgId, Code = seed.Code, Title = seed.Title,
                    Description = seed.Description, Scope = seed.Scope, FieldPath = seed.FieldPath,
                    Operator = seed.Operator, DefaultSeverity = seed.DefaultSeverity,
                    DefaultExpectedValue = seed.DefaultExpectedValue, ParamHint = seed.ParamHint,
                    UblRef = seed.UblRef, EdifactRef = seed.EdifactRef, X12Ref = seed.X12Ref,
                    CxmlRef = seed.CxmlRef, IsSystem = true, CreatedBy = "system:seed", CreatedAt = now,
                };
            }
            else
            {
                def = new RuleDefinition
                {
                    Id = Guid.NewGuid(), OrgId = orgId, Code = code,
                    Title = $"{r.FieldPath} {r.Operator}",
                    Description = "Created from a supplier acceptance rule (Group V4).",
                    Scope = r.Scope, FieldPath = r.FieldPath, Operator = r.Operator,
                    DefaultSeverity = r.Severity, DefaultExpectedValue = r.ExpectedValue,
                    IsSystem = false, CreatedBy = createdBy ?? "system", CreatedAt = now,
                };
            }
            toAdd.Add(def);
        }

        if (toAdd.Count > 0)
        {
            _db.RuleDefinitions.AddRange(toAdd);
            await _db.SaveChangesAsync(ct);
            foreach (var d in toAdd) idByCode[d.Code] = d.Id;
        }
        return idByCode;
    }

    public async Task<bool> ActivateVersionAsync(Guid orgId, Guid supplierId, int versionNo, CancellationToken ct)
    {
        var versions = await _db.SupplierAcceptanceProfiles
            .Where(p => p.OrgId == orgId && p.SupplierId == supplierId)
            .ToListAsync(ct);
        var target = versions.FirstOrDefault(p => p.VersionNo == versionNo);
        if (target is null) return false;

        var now = DateTime.UtcNow;
        foreach (var v in versions)
        {
            if (v.Status == "active" && v.Id != target.Id)
            {
                v.Status = "archived";
                v.EffectiveTo = now;
            }
        }
        target.Status = "active";
        target.EffectiveFrom = now;
        target.EffectiveTo = null;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<OrderValidationResult>?> ValidateOrderAsync(Guid orgId, Guid orderId, CancellationToken ct)
    {
        var order = await _db.PurchaseOrders
            .Include(o => o.Lines)
            .Include(o => o.Parties)        // Phase 2 (D slice): not_label / vat_format read ship-to party
            .Include(o => o.SourceCapture)  // Phase 2 (D slice): date_sanity reads the raw printed date string
            .Where(o => o.Id == orderId && o.OrgId == orgId)
            .FirstOrDefaultAsync(ct);
        if (order is null) return null;

        var profile = await ResolveEffectiveProfileAsync(orgId, order, ct);
        var now = DateTime.UtcNow;

        // Re-validation overwrites prior results for this order.
        var prior = _db.OrderValidationResults.Where(r => r.OrgId == orgId && r.OrderId == orderId);
        _db.OrderValidationResults.RemoveRange(prior);

        var results = EvaluateProfile(orgId, orderId, profile, order, now);

        _db.OrderValidationResults.AddRange(results);
        await _db.SaveChangesAsync(ct);
        return results;
    }

    // ── Launch batch 7 — revision authority ────────────────────────────────────

    /// <summary>
    /// The acceptance profile that GOVERNS validation for this order:
    /// <list type="bullet">
    ///   <item>Flag ON + pinned + revision resolves: the revision's BOUND profile
    ///   (<c>AcceptanceProfileId</c> + <c>AcceptanceVersionNo</c>, loaded by id org-scoped — each
    ///   profile version is its own immutable row, so the id IS the version pin). A revision that
    ///   binds NO profile (<c>AcceptanceProfileId</c> = null) honestly means "this published
    ///   contract has no validation" → null (no rules), NOT the live active profile.</item>
    ///   <item>Flag off / unpinned / orphan pin: the LIVE active profile — byte-identical to the
    ///   pre-batch-7 behaviour.</item>
    ///   <item>Defensive: a bound profile row that no longer exists falls back to the live active
    ///   profile (logged) — validation must never brick on a dangling binding.</item>
    /// </list>
    /// </summary>
    private async Task<SupplierAcceptanceProfile?> ResolveEffectiveProfileAsync(
        Guid orgId, PurchaseOrderEntity order, CancellationToken ct)
    {
        if (_effectiveConfig is not null)
        {
            var effective = await _effectiveConfig.ResolveAsync(orgId, order.ConnectionRevisionId, ct);
            if (effective.IsRevision)
            {
                if (effective.AcceptanceProfileId is null)
                {
                    _logger?.LogInformation(
                        "Order {OrderId}: pinned {Source} binds no acceptance profile — validating with no rules.",
                        order.Id, effective.Source);
                    return null;
                }

                var bound = await _db.SupplierAcceptanceProfiles
                    .Include(p => p.Rules)
                    .Where(p => p.OrgId == orgId && p.Id == effective.AcceptanceProfileId.Value)
                    .FirstOrDefaultAsync(ct);

                if (bound is not null)
                {
                    if (effective.AcceptanceVersionNo is int boundVersion && bound.VersionNo != boundVersion)
                        _logger?.LogWarning(
                            "Order {OrderId}: pinned {Source} acceptance binding version {Expected} does not match profile {ProfileId} version {Actual} — the id-bound row governs.",
                            order.Id, effective.Source, boundVersion, bound.Id, bound.VersionNo);

                    _logger?.LogInformation(
                        "Order {OrderId}: validating against pinned {Source} acceptance profile v{Version}.",
                        order.Id, effective.Source, bound.VersionNo);
                    return bound;
                }

                _logger?.LogWarning(
                    "Order {OrderId}: pinned {Source} acceptance profile {ProfileId} not found — falling back to the live active profile.",
                    order.Id, effective.Source, effective.AcceptanceProfileId);
            }
        }

        return await GetActiveAsync(orgId, order.SupplierId, ct);
    }

    /// <summary>
    /// Pure, NON-MUTATING evaluation of an acceptance <paramref name="profile"/> against a loaded
    /// <paramref name="order"/>. Produces the same <see cref="OrderValidationResult"/> rows
    /// <see cref="ValidateOrderAsync"/> persists, but writes nothing to the database. Reused by the
    /// V2 replay path so a DRAFT connection revision's bound validation can be evaluated against a
    /// historical order WITHOUT touching its stored validation state. A null profile yields an empty
    /// list (no active validation). The returned rows are detached (not added to any DbSet).
    /// </summary>
    public static IReadOnlyList<OrderValidationResult> EvaluateProfile(
        Guid orgId, Guid orderId, SupplierAcceptanceProfile? profile, PurchaseOrderEntity order, DateTime now)
    {
        var results = new List<OrderValidationResult>();
        if (profile is null) return results;

        foreach (var rule in profile.Rules)
        {
            if (rule.Scope == "order")
            {
                var (pass, val) = EvaluateOrderField(order, rule);
                results.Add(MakeResult(orgId, orderId, profile.Id, rule, null, pass, val, now));
            }
            else
            {
                foreach (var line in order.Lines)
                {
                    var (pass, val) = EvaluateLineField(line, rule);
                    results.Add(MakeResult(orgId, orderId, profile.Id, rule, line.LineNumber, pass, val, now));
                }
            }
        }
        return results;
    }

    private static OrderValidationResult MakeResult(
        Guid orgId, Guid orderId, Guid profileId, SupplierAcceptanceRule rule,
        int? lineNumber, bool pass, string? actualValue, DateTime now) => new()
    {
        Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId,
        ProfileId = profileId, RuleId = rule.Id, LineNumber = lineNumber,
        Severity = rule.Severity, Status = pass ? "pass" : "fail",
        Code = $"{rule.FieldPath}.{rule.Operator}",
        Message = pass
            ? $"{rule.FieldPath} satisfies {rule.Operator}"
            : $"{rule.FieldPath} ('{actualValue}') failed rule {rule.Operator} {rule.ExpectedValue}",
        DetectedAt = now,
    };

    private static (bool pass, string? value) EvaluateOrderField(PurchaseOrderEntity o, SupplierAcceptanceRule rule)
    {
        string? v = rule.FieldPath switch
        {
            "currency"   => o.Currency,
            "buyerName"  => o.BuyerName,
            // Phase 2 (D slice): ship-to city/VAT resolve from the first shipTo party.
            "shipToCity" => o.Parties.FirstOrDefault(p => p.Role == "shipTo")?.City,
            "shipToVat"  => o.Parties.FirstOrDefault(p => p.Role == "shipTo")?.Vat,
            "incoterms"  => o.Incoterms,
            // Phase 2 (D slice): the ORIGINAL printed date string lives in the lossless SourceCapture
            // raw-token bag — there is no typed raw-date column (DeliveryDate is a DateOnly that has
            // already lost MM/DD vs DD/MM ambiguity). date_sanity inspects the raw printed string.
            "sourceDate" => FirstDateLikeRawToken(o.SourceCapture),
            _            => null,
        };

        // vat_format needs the party's COUNTRY to cross-check the VAT prefix; pass it via the rule's
        // ExpectedValue slot when the author didn't set one (kept inside the pure evaluator).
        if (rule.Operator == "vat_format" && rule.FieldPath == "shipToVat")
        {
            var country = o.Parties.FirstOrDefault(p => p.Role == "shipTo")?.Country;
            return (EvaluateVatFormat(v, country), v);
        }

        return (Evaluate(rule, v), v);
    }

    private static (bool pass, string? value) EvaluateLineField(PurchaseOrderLineEntity l, SupplierAcceptanceRule rule)
    {
        // line_amount_reconcile needs qty AND price, not just one value. Handle it here (inside the
        // pure evaluator) so Evaluate() — which only sees a single value — stays unchanged.
        if (rule.Operator == "line_amount_reconcile")
        {
            var computed = l.Quantity * l.UnitPrice;
            var stated   = l.LineAmount ?? computed; // no stated amount → vacuously reconciled
            var tol      = decimal.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var t)
                ? Math.Abs(t)
                : 0.01m;
            var pass     = Math.Abs(stated - computed) <= tol;
            return (pass, stated.ToString(CultureInfo.InvariantCulture));
        }

        string? v = rule.FieldPath switch
        {
            "supplierItemCode"       => l.SupplierItemCode,
            "buyerItemCode"          => l.BuyerItemCode,
            "description"            => l.Description,
            "quantity"               => l.Quantity.ToString(CultureInfo.InvariantCulture),
            "unitPrice"              => l.UnitPrice.ToString(CultureInfo.InvariantCulture),
            "manufacturerPartNumber" => l.ManufacturerPartNumber,
            "lineAmount"             => (l.LineAmount ?? (l.Quantity * l.UnitPrice)).ToString(CultureInfo.InvariantCulture),
            _                        => null,
        };
        return (Evaluate(rule, v), v);
    }

    /// <summary>
    /// Resolve the raw printed date string from the lossless <see cref="SourceCapture"/> token bag:
    /// the first token whose label mentions "date" with a date-shaped value, else the first token
    /// with a date-shaped value. Returns null when nothing date-like is present (date_sanity then
    /// passes — absence is governed by 'required', not date_sanity). No new column is invented; the
    /// original printed string is the only place the MM/DD vs DD/MM ambiguity survives.
    /// </summary>
    private static string? FirstDateLikeRawToken(SourceCapture? capture)
    {
        if (capture?.TokensJson is null) return null;
        var root = capture.TokensJson.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

        string? firstDateShaped = null;
        foreach (var el in root.EnumerateArray())
        {
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var value = el.TryGetProperty("value", out var ve) && ve.ValueKind == System.Text.Json.JsonValueKind.String
                ? ve.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(value) || !LooksLikeDate(value)) continue;

            var label = el.TryGetProperty("label", out var le) && le.ValueKind == System.Text.Json.JsonValueKind.String
                ? le.GetString()
                : null;
            if (label is not null && label.Contains("date", StringComparison.OrdinalIgnoreCase))
                return value; // a date-labelled token is the strongest signal
            firstDateShaped ??= value;
        }
        return firstDateShaped;
    }

    /// <summary>A value "looks like a date" if it has 2+ numeric components separated by / - or .</summary>
    private static bool LooksLikeDate(string value)
    {
        var parts = value.Split('/', '-', '.');
        var numeric = parts.Count(p => int.TryParse(p.Trim(), out _));
        return numeric >= 2;
    }

    private static bool Evaluate(SupplierAcceptanceRule rule, string? actual)
    {
        switch (rule.Operator)
        {
            case "required":
                return !string.IsNullOrWhiteSpace(actual);
            case "equals":
                return string.Equals(actual, rule.ExpectedValue, StringComparison.OrdinalIgnoreCase);
            case "in":
                var allowed = (rule.ExpectedValue ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return actual is not null && allowed.Contains(actual, StringComparer.OrdinalIgnoreCase);
            case "min":
                return double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a1)
                    && double.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var m1)
                    && a1 >= m1;
            case "max":
                return double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a2)
                    && double.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var m2)
                    && a2 <= m2;
            case "not_equals":
                return !string.Equals(actual, rule.ExpectedValue, StringComparison.OrdinalIgnoreCase);
            case "contains":
                return actual is not null
                    && actual.Contains(rule.ExpectedValue ?? "", StringComparison.OrdinalIgnoreCase);
            case "greater_than":
                return double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a3)
                    && double.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var m3)
                    && a3 > m3;
            case "less_than":
                return double.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a4)
                    && double.TryParse(rule.ExpectedValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var m4)
                    && a4 < m4;
            case "max_length":
                return actual is not null
                    && int.TryParse(rule.ExpectedValue, NumberStyles.None, CultureInfo.InvariantCulture, out var maxLen)
                    && actual.Length <= maxLen;

            // ── Phase 2 (D slice) lossless-mapping validation operators ───────────────
            case "date_sanity":
            {
                // A printed date string is "sane" only if it is UNAMBIGUOUS. With the first two
                // numeric components both ≤ 12 (e.g. "06/12") MM/DD and DD/MM are both valid → fail
                // and review-flag the flip risk. A component > 12 (a day or month) disambiguates →
                // pass. Absence is handled by 'required', not here → pass on empty / non-date input.
                if (string.IsNullOrWhiteSpace(actual)) return true;
                var parts   = actual.Split('/', '-', '.');
                var numeric = parts.Where(p => int.TryParse(p.Trim(), out _)).Select(p => int.Parse(p.Trim())).ToArray();
                if (numeric.Length < 2) return true;        // not a numeric date → don't second-guess
                return numeric.Take(2).Any(n => n > 12);    // > 12 disambiguates → pass; else ambiguous → fail
            }
            case "not_label":
            {
                // Fail when the value IS (or starts with) a label word — catches a parser that swept
                // a label cell into a data field (REDACTED-PARTY "UIDNr" landing in ShipToCity).
                if (string.IsNullOrWhiteSpace(actual)) return true;
                var labels = (rule.ExpectedValue ?? "City,VAT,UID,UIDNr,Label,Tel,Fax")
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return !labels.Any(label =>
                    actual.StartsWith(label, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(actual, label, StringComparison.OrdinalIgnoreCase));
            }
            case "vat_format":
                // VAT shape is country-aware → resolved in EvaluateOrderField (it has the party country).
                // Reaching here means no country context was available; fall back to the bare shape check.
                return EvaluateVatFormat(actual, country: null);

            default:
                return true; // unknown operator → non-blocking pass
        }
    }

    // ── Phase 2 (D slice) VAT-format helpers ─────────────────────────────────────

    /// <summary>
    /// Per-country VAT shape check (length + country-prefix + charset) — ADVISORY, not a VIES
    /// checksum (formats evolve; we flag, never hard-block). Empty → pass (use 'required' to mandate).
    /// When <paramref name="country"/> is known and the VAT carries an explicit 2-letter prefix, the
    /// prefix must match the country. The per-country length set covers the corpus countries
    /// (AT/DE/FR/PL/DK/NO/FI/…); unknown countries fall back to a generic 8–12 alnum body check.
    /// </summary>
    internal static bool EvaluateVatFormat(string? vat, string? country)
    {
        if (string.IsNullOrWhiteSpace(vat)) return true; // absence handled by 'required'
        return IsPlausibleVat(vat, country);
    }

    private static readonly Dictionary<string, (string prefix, int min, int max)> VatRules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // (expected 2-letter prefix, min body length, max body length) — body = digits/alnum after the prefix.
            ["AT"] = ("AT", 9, 9),   // ATU + 8 digits → body "U########" = 9
            ["DE"] = ("DE", 9, 9),   // DE + 9 digits
            ["FR"] = ("FR", 11, 11), // FR + 2 alnum + 9 digits = 11
            ["PL"] = ("PL", 10, 10), // PL + 10 digits
            ["DK"] = ("DK", 8, 8),   // DK + 8 digits
            ["FI"] = ("FI", 8, 8),   // FI + 8 digits
            ["NO"] = ("NO", 9, 9),   // NO + 9 digits (organisation number; MVA suffix stripped)
            ["SE"] = ("SE", 12, 12), // SE + 12 digits
            ["EE"] = ("EE", 9, 9),   // EE + 9 digits
            ["LV"] = ("LV", 11, 11), // LV + 11 digits
            ["LT"] = ("LT", 9, 12),  // LT + 9 or 12 digits
            ["NL"] = ("NL", 12, 12), // NL + 9 alnum + B + 2 digits = 12
            ["BE"] = ("BE", 10, 10), // BE + 10 digits
            ["IT"] = ("IT", 11, 11), // IT + 11 digits
            ["ES"] = ("ES", 9, 9),   // ES + 9 alnum
        };

    /// <summary>
    /// Plausible-VAT shape: optional 2-letter country prefix then an alphanumeric body. When a
    /// <paramref name="country"/> rule is known we enforce its prefix + body length; otherwise a
    /// generic 2-letter prefix + 8–12 alnum (or a bare 8–12 alnum) body is accepted. Charset only —
    /// not a checksum.
    /// </summary>
    private static bool IsPlausibleVat(string vat, string? country)
    {
        var v = new string(vat.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '.').ToArray()).ToUpperInvariant();
        if (v.Length is < 8 or > 16) return false;

        var hasPrefix = v.Length >= 2 && char.IsLetter(v[0]) && char.IsLetter(v[1]);
        var prefix    = hasPrefix ? v[..2] : null;
        var body      = hasPrefix ? v[2..] : v;
        if (body.Length == 0 || !body.All(char.IsLetterOrDigit)) return false;

        if (!string.IsNullOrWhiteSpace(country) && VatRules.TryGetValue(country.Trim(), out var rule))
        {
            // If the VAT carries an explicit prefix, it must match the declared country.
            if (prefix is not null && !string.Equals(prefix, rule.prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            return body.Length >= rule.min && body.Length <= rule.max;
        }

        // No country rule → generic permissive shape.
        return body.Length is >= 6 and <= 14;
    }
}
