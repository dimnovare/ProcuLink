using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;

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

    public async Task<IReadOnlyList<OrderValidationResult>?> ValidateOrderAsync(
        Guid orgId, Guid orderId, CancellationToken ct, OutputFormat? outputFormat = null)
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

        // Mandatory, supplier-INDEPENDENT invariants ALWAYS run first (qty/price/currency/identifier),
        // so an order with no acceptance profile — or with a nonsense value — can never surface as a
        // clean "Passed". The supplier profile's rules (if any) are layered on top.
        var results = new List<OrderValidationResult>();
        results.AddRange(InvariantValidator.Evaluate(orgId, orderId, order, now));
        results.AddRange(EvaluateProfile(orgId, orderId, profile, order, now));
        results.AddRange(EvaluateOutputFields(orgId, orderId, order, outputFormat, now));

        _db.OrderValidationResults.AddRange(results);
        await _db.SaveChangesAsync(ct);
        return results;
    }

    // ── WP-17 — the blocking subset, evaluated without persisting ─────────────

    /// <inheritdoc />
    /// <remarks>
    /// Shares BOTH halves of <see cref="ValidateOrderAsync"/>'s supplier-rule evaluation — the same
    /// <see cref="ResolveEffectiveProfileAsync"/> (so a pinned revision's bound profile governs here
    /// too) and the same pure <see cref="EvaluateProfile"/>. Nothing is re-implemented, so the gate
    /// and the validation panel can never disagree about which rules fail;
    /// <c>AcceptanceGateMatchesValidationTests</c> asserts that difference structurally.
    ///
    /// <para><c>AsNoTracking</c> is correct and deliberate here: this method is read-only and runs
    /// INSIDE the transform, where the caller already holds a tracked copy of the same order whose
    /// status it is mid-way through mutating. A tracking query would hand back that instance and
    /// invite a write from a method that must never perform one.</para>
    /// </remarks>
    public async Task<IReadOnlyList<AcceptanceBlocker>?> GetBlockingFailuresAsync(
        Guid orgId, Guid orderId, CancellationToken ct)
    {
        var order = await _db.PurchaseOrders
            .AsNoTracking()
            .Include(o => o.Lines)
            .Include(o => o.Parties)        // shipTo rules (not_label / vat_format) read the party
            .Include(o => o.SourceCapture)  // date_sanity reads the raw printed date string
            .Where(o => o.Id == orderId && o.OrgId == orgId)
            .FirstOrDefaultAsync(ct);
        if (order is null) return null;

        var profile = await ResolveEffectiveProfileAsync(orgId, order, ct);
        if (profile is null) return Array.Empty<AcceptanceBlocker>();

        // BlockOnFail lives on the RULE, not on the result row, so correlate by RuleId rather than
        // widening OrderValidationResult (which is a persisted, frontend-facing shape).
        var ruleById = profile.Rules.ToDictionary(r => r.Id);

        var blockers = new List<AcceptanceBlocker>();
        foreach (var r in EvaluateProfile(orgId, orderId, profile, order, DateTime.UtcNow))
        {
            if (r.Status != "fail") continue;
            if (r.RuleId is not Guid ruleId) continue;                    // invariant / output row
            if (!ruleById.TryGetValue(ruleId, out var rule)) continue;
            if (!IsBlocking(rule)) continue;

            // Everything that makes this failure THIS failure travels with it: which rule, which
            // line, which version of the profile, what was demanded, and what the order said. The
            // gate composes those into AcceptanceBlocker.Key, which is what an override excuses —
            // so a re-parse, a rule edit, or a republished profile is a failure nobody signed off.
            blockers.Add(new AcceptanceBlocker(
                r.Code, r.LineNumber, r.Message,
                RuleId:         ruleId,
                ProfileVersion: profile.VersionNo,
                ExpectedValue:  rule.ExpectedValue,
                ActualValue:    r.ActualValue));
        }

        return blockers;
    }

    /// <summary>
    /// The ONE predicate for "this rule refuses the order" — <c>severity = error</c> OR an explicit
    /// <c>BlockOnFail</c>. Exactly what the supplier-profile UI tells the operator will block
    /// delivery. A <c>warning</c> rule without <c>BlockOnFail</c> never blocks.
    /// </summary>
    internal static bool IsBlocking(SupplierAcceptanceRule rule) =>
        rule.BlockOnFail || string.Equals(rule.Severity, "error", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Surface the OUTPUT-render problems that NOTHING ELSE already shows, in the SAME plain-language
    /// list, BEFORE a transform is attempted — so the user sees them where they're looking, not only as
    /// a later transform exception in <c>/operations/exceptions</c>. Deliberately routes ONLY the
    /// format-mandatory buyer item code (EDIFACT/X12) and sanitized values: <c>NeedsReview</c> +
    /// <c>MissingSupplierItemCode</c> are already in the inbox fix queue, and zero/negative price is
    /// already an <see cref="InvariantValidator"/> row — re-listing either would duplicate.
    /// <paramref name="outputFormat"/> defaults to JSON (no extra item-code requirement); pass the
    /// supplier's real format to catch the EDI/X12 buyer-code requirement. Pure (no transform, no I/O).
    /// </summary>
    private static IReadOnlyList<OrderValidationResult> EvaluateOutputFields(
        Guid orgId, Guid orderId, PurchaseOrderEntity order, OutputFormat? outputFormat, DateTime now)
    {
        var rows = new List<OrderValidationResult>();
        var problems = OutputFieldValidator.CollectEntityProblems(order, outputFormat ?? OutputFormat.Json);

        foreach (var p in problems)
        {
            string? code = p.Kind switch
            {
                LineProblemKind.MissingItemCode => AcceptanceMessages.OutputBuyerCodeCode,
                LineProblemKind.Sanitized       => AcceptanceMessages.OutputSanitizedCode,
                // NOT routed (already shown elsewhere): NeedsReview / MissingSupplierItemCode (fix
                // queue), MissingOrZeroPrice (InvariantValidator's unit-price check).
                _ => null,
            };
            if (code is null) continue;

            rows.Add(new OrderValidationResult
            {
                Id = Guid.NewGuid(), OrgId = orgId, OrderId = orderId,
                ProfileId = null, RuleId = null, LineNumber = p.LineNumber,
                Severity = p.Kind == LineProblemKind.Sanitized ? "warning" : "error",
                Status = "fail",
                Code = code,
                Message = p.Message, // already plain-language from OutputFieldValidator
                DetectedAt = now,
            });
        }
        return rows;
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

        return await GetActiveAsync(orgId, order.SupplierId ?? Guid.Empty, ct);
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
        // Transient (NotMapped) — the value this rule judged, carried to the gate so an operator
        // override is pinned to the failure it actually saw rather than to the rule's name.
        ActualValue = actualValue,
        // Plain-language, fixable message (was the developer template
        // "unitPrice ('100') failed rule max 50000"). See AcceptanceMessages.
        Message = pass
            ? AcceptanceMessages.ForPass(rule.FieldPath, rule.Operator)
            : AcceptanceMessages.ForFail(rule.FieldPath, rule.Operator, actualValue, rule.ExpectedValue, lineNumber),
        DetectedAt = now,
    };

    private static (bool pass, string? value) EvaluateOrderField(PurchaseOrderEntity o, SupplierAcceptanceRule rule)
    {
        string? v = rule.FieldPath switch
        {
            "currency"   => o.Currency,
            "buyerName"  => o.BuyerName,
            // Phase 2 (D slice): ship-to city/VAT resolve from the first shipTo party.
            "shipToCity" => o.Parties.FirstOrDefault(p => string.Equals(p.Role, "shipTo", StringComparison.OrdinalIgnoreCase))?.City,
            "shipToVat"  => o.Parties.FirstOrDefault(p => string.Equals(p.Role, "shipTo", StringComparison.OrdinalIgnoreCase))?.Vat,
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
            var country = o.Parties.FirstOrDefault(p => string.Equals(p.Role, "shipTo", StringComparison.OrdinalIgnoreCase))?.Country;
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
            "manufacturerName"       => l.ManufacturerName,
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
                return TryParseNumeric(actual, out var a1)
                    && TryParseNumeric(rule.ExpectedValue, out var m1)
                    && a1 >= m1;
            case "max":
                return TryParseNumeric(actual, out var a2)
                    && TryParseNumeric(rule.ExpectedValue, out var m2)
                    && a2 <= m2;
            case "not_equals":
                return !string.Equals(actual, rule.ExpectedValue, StringComparison.OrdinalIgnoreCase);
            case "contains":
                return actual is not null
                    && actual.Contains(rule.ExpectedValue ?? "", StringComparison.OrdinalIgnoreCase);
            case "greater_than":
                return TryParseNumeric(actual, out var a3)
                    && TryParseNumeric(rule.ExpectedValue, out var m3)
                    && a3 > m3;
            case "less_than":
                return TryParseNumeric(actual, out var a4)
                    && TryParseNumeric(rule.ExpectedValue, out var m4)
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
                // Fail when the value IS a label word, or is a label word followed by a NON-LETTER
                // boundary — catches a parser that swept a label cell into a data field (REDACTED-PARTY
                // "UIDNr." / "UIDNr " landing in ShipToCity) WITHOUT false-positiving a legitimate
                // value that merely starts with the same letters (e.g. city "Cityville" vs label
                // "City"). A bare StartsWith would wrongly trip the latter.
                if (string.IsNullOrWhiteSpace(actual)) return true;
                var trimmed = actual.Trim();
                var labels = (rule.ExpectedValue ?? "City,VAT,UID,UIDNr,Label,Tel,Fax")
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return !labels.Any(label => IsLabelMatch(trimmed, label));
            }
            case "vat_format":
                // VAT shape is country-aware → resolved in EvaluateOrderField (it has the party country).
                // Reaching here means no country context was available; fall back to the bare shape check.
                return EvaluateVatFormat(actual, country: null);

            default:
                // FAIL CLOSED. An unknown/typo'd operator must never silently pass — that would let a
                // misconfigured rule report green. Unknown operators are also rejected at rule-create
                // time (CreateVersionAsync), so reaching here means a legacy/dangling operator; flag it.
                return false;
        }
    }

    /// <summary>
    /// The acceptance-rule operators the evaluator understands. Used to reject unknown operators at
    /// rule-create time so a misconfigured rule can never be persisted and then silently pass/fail.
    /// (<c>line_amount_reconcile</c> and order-scope <c>vat_format</c> are handled before
    /// <see cref="Evaluate"/> but are valid operators, so they belong in this set.)
    /// </summary>
    public static readonly IReadOnlySet<string> KnownOperators = new HashSet<string>(StringComparer.Ordinal)
    {
        "required", "equals", "in", "min", "max", "not_equals", "contains",
        "greater_than", "less_than", "max_length", "date_sanity", "not_label",
        "vat_format", "line_amount_reconcile",
    };

    /// <summary>
    /// Locale-safe numeric parse for the comparison operators (min / max / greater_than / less_than).
    /// Both operands flow through here:
    ///   • the order's value (<c>actual</c>) — for line numeric fields this is already an
    ///     InvariantCulture string from a TYPED decimal column (e.g. "73.22"), so it reads back exactly;
    ///   • the rule's threshold (<c>rule.ExpectedValue</c>) — a RAW token the author typed, which may be
    ///     comma-decimal ("73,22", "50,00").
    /// The previous <c>double.TryParse(NumberStyles.Any, InvariantCulture)</c> silently read a
    /// comma-decimal token as a thousands-grouped integer ("73,22" → 7322), so a <c>max:5000</c> rule
    /// PASSED a non-conforming value. We reuse the same locale-safe "last separator is the decimal;
    /// refuse genuine ambiguity" logic as the CSV parser. A token that cannot be read unambiguously
    /// returns false → the comparison fails CLOSED (surfaces for review) rather than silently passing.
    /// Mirrors <c>CsvOrderParser.ParseDecimalFlexible</c>; <c>european</c> is left false because the
    /// rule editor / typed columns are not delimiter-tagged, but the "last separator wins" rule still
    /// reads both US ("1,234.56") and EU ("1.234,56" / "73,22") correctly. A US-thousands token whose
    /// only separator is a comma with exactly 3 trailing digits (e.g. "5,000") is treated as the
    /// integer 5000 — the intended threshold, not 5.0.
    /// </summary>
    internal static bool TryParseNumeric(string? raw, out double value)
    {
        value = 0d;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Reject any non-numeric noise (letters, exponents, '%', …) rather than silently stripping it
        // into a plausible-but-wrong number. Digits, the two separators, a sign, whitespace and
        // currency symbols are the only permitted characters.
        foreach (var c in raw)
        {
            if (char.IsDigit(c) || c is '.' or ',' or '-' or '+') continue;
            if (char.IsWhiteSpace(c)) continue;
            if (char.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol) continue;
            return false;
        }

        var s = new string(raw.Where(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
        if (s.Length == 0 || s == "-") return false;

        int lastDot = s.LastIndexOf('.'), lastComma = s.LastIndexOf(',');
        char? decimalSep;
        if (lastDot >= 0 && lastComma >= 0)
        {
            decimalSep = lastComma > lastDot ? ',' : '.';                 // both present → last wins
        }
        else if (lastComma >= 0)
        {
            bool single = s.IndexOf(',') == lastComma;
            int trailing = s.Length - lastComma - 1;
            // A single comma with exactly 3 trailing digits ("5,000") is a US thousands group → integer.
            decimalSep = (single && trailing == 3) ? null : ',';
        }
        else if (lastDot >= 0)
        {
            // Only a dot present. A single dot with exactly 3 trailing digits ("5.000") is ambiguous
            // (decimal 5.0 vs EU thousands 5000); the rule editor / typed columns use a decimal point
            // by convention, so we keep '.' as the decimal (→ 5.0). This matches how the order's typed
            // decimal columns serialise ("73.22"), so the actual operand reads back exactly as before.
            decimalSep = '.';
        }
        else
        {
            decimalSep = null;                                            // pure integer
        }

        string normalized = decimalSep is char ds
            ? s.Replace(ds == '.' ? "," : ".", "").Replace(ds, '.')      // strip groups, decimal → '.'
            : s.Replace(",", "").Replace(".", "");                       // integer / thousands-only

        return double.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// True when <paramref name="actual"/> is a swept label cell for <paramref name="label"/>: it
    /// EQUALS the label (case-insensitive), or it STARTS WITH the label immediately followed by a
    /// non-letter character (".", " ", ":", a digit, …) — e.g. "UIDNr." / "UIDNr 12345" for label
    /// "UIDNr". A legitimate value that merely begins with the same letters but continues with a
    /// letter ("Cityville" for label "City") is NOT a match, so the advisory rule does not
    /// false-positive a real value. A bare prefix check would wrongly trip "Cityville".
    /// </summary>
    private static bool IsLabelMatch(string actual, string label)
    {
        if (label.Length == 0) return false;
        if (string.Equals(actual, label, StringComparison.OrdinalIgnoreCase)) return true;
        if (actual.Length <= label.Length) return false;
        if (!actual.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return false;
        // Boundary char immediately after the label must NOT be a letter, otherwise this is a longer
        // word that merely shares a prefix (e.g. "Cityville") rather than a "label + punctuation" cell.
        return !char.IsLetter(actual[label.Length]);
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
