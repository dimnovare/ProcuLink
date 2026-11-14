namespace ProcuLink.Core.Constants;

public static class PlanConstants
{
    // ── Plan string identifiers ────────────────────────────────────────────
    public const string Pilot       = "pilot";
    public const string Growth      = "growth";
    public const string Operations  = "operations";
    public const string Integration = "integration";
    public const string Distributor = "distributor";
    public const string Enterprise  = "enterprise";

    // ── Document ladder (plan 11): sold to workspaces that have the document engine ──
    public const string Starter  = "starter";
    public const string Pro      = "pro";
    public const string Business = "business";
    public const string Scale    = "scale";

    /// <summary>The document ladder, cheapest first. Deliberately NOT part of <see cref="All"/>.</summary>
    public static readonly string[] DocumentLadder = [Starter, Pro, Business, Scale];

    public static bool IsDocumentPlan(string? plan) =>
        plan is not null && DocumentLadder.Contains(plan);

    public static bool IsKnownPlan(string? plan) =>
        plan is not null && (All.Contains(plan) || IsDocumentPlan(plan));

    public static readonly string[] All =
    [
        Pilot,
        Growth,
        Operations,
        Integration,
        Distributor,
        Enterprise,
    ];

    // ── Pilot trial config ─────────────────────────────────────────────────
    public static readonly TimeSpan PilotDuration     = TimeSpan.FromDays(30);
    public const int                PilotOrderLimit   = 20;
    public const int                PilotSupplierLimit = 1;

    // ── Per-plan limits (orders/month for paid; cumulative for Pilot) ──────
    // NOTE: the order limit is a SOFT cap for active paid plans — going over it
    // NEVER blocks an order (see #1 never-block rule); it only flags over-limit
    // usage and accrues the per-order overage fee below. It is a HARD cap only
    // for Pilot (a trial-ended read-only state, not a volume block).
    public static readonly IReadOnlyDictionary<string, (int Orders, int Suppliers)> Limits =
        new Dictionary<string, (int, int)>
        {
            [Pilot]       = (PilotOrderLimit,  PilotSupplierLimit),
            [Growth]      = (150,              5),
            [Operations]  = (500,              10),
            // Integration raised 1_000 → 1_500 so €/order is monotonic across paid
            // tiers: Operations €0.80, Integration €0.67, Distributor €0.60.
            [Integration] = (1_500,            20),
            [Distributor] = (2_500,            30),
            [Enterprise]  = (int.MaxValue,     int.MaxValue),
            // Document ladder: documents/month; no supplier cap.
            [Starter]     = (150,              int.MaxValue),
            [Pro]         = (400,              int.MaxValue),
            [Business]    = (1_500,            int.MaxValue),
            [Scale]       = (5_000,            int.MaxValue),
        };

    // ── Flows per workspace (absent = unlimited). Legacy plans have no flow cap. ──
    public static readonly IReadOnlyDictionary<string, int> FlowLimits =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [Pilot]    = 2,
            [Starter]  = 5,
            [Pro]      = 20,
            [Business] = 100,
        };

    /// <summary>Flow cap for a plan; null for a known plan without one. An unknown plan gets Pilot's cap.</summary>
    public static int? GetFlowLimit(string? plan) =>
        plan is not null && FlowLimits.TryGetValue(plan, out var n) ? n
        : IsKnownPlan(plan) ? null
        : FlowLimits[Pilot];

    // ── Billed overage rate per order/document (EUR). Legacy rows are literals so the
    // frontend mirror can parse them; a test pins them to OveragePerOrderEur. ──
    public static readonly IReadOnlyDictionary<string, decimal> OverageRatesEur =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            [Growth]      = 0.50m,
            [Operations]  = 0.50m,
            [Integration] = 0.50m,
            [Distributor] = 0.50m,
            [Starter]     = 0.24m,
            [Pro]         = 0.18m,
            [Business]    = 0.10m,
            [Scale]       = 0.08m,
        };

    public static decimal GetOverageRateEur(string? plan) =>
        plan is not null && OverageRatesEur.TryGetValue(plan, out var rate) ? rate : 0m;

    // ── Monthly AI token allowance ───────────────────────────────────────
    // One allowance per org across every AI feature: 40,000 tokens per billing unit
    // read this month (or being read), floor 1M, capped by the emergency brake.
    // A trial's units are capped at its document limit (AiUsageTracker), so it stays at the floor.
    public const long AiTokensPerBilledUnit = 40_000;
    public const long AiMonthlyTokenFloor = 1_000_000;
    public const long AiPastDueGraceTokens = 200_000;
    public const long AiEmergencyBrakeTokens = 250_000_000;

    /// <summary>
    /// Monthly AI token allowance: follows the billing units read this month, never below
    /// the floor. past_due keeps a small grace budget; every other read-only status gets 0.
    /// </summary>
    public static long GetAiMonthlyTokenAllowance(long billedUnits, string? accountStatus) =>
        AccountStatusConstants.IsReadOnly(accountStatus)
            ? string.Equals(accountStatus, AccountStatusConstants.PastDue, StringComparison.OrdinalIgnoreCase)
                ? AiPastDueGraceTokens
                : 0L
            : Math.Max(AiMonthlyTokenFloor, AiTokensPerBilledUnit * Math.Max(0L, billedUnits));

    /// <summary>
    /// Per-order overage fee (EUR) charged on every order an active paid
    /// self-serve plan processes ABOVE its monthly order limit. Billed via a
    /// Stripe invoice item at the period boundary — going over the cap is always
    /// allowed, never blocked. Pilot has no overage (it is a hard trial cap) and
    /// Enterprise is custom-contracted (effectively unlimited).
    ///
    /// <para>NOTE: the raw per-order fee alone produced a "pricing overage
    /// inversion" — at some volumes a lower tier + overage cost MORE than the
    /// next flat tier, disincentivising upgrades (e.g. Growth@500 ≈ €324 &lt;
    /// Operations €399; Operations@1,500 ≈ €899 &lt; Integration €999). The
    /// best-price logic in <see cref="BestPriceOverageOrders"/> caps the metered
    /// overage so a customer is NEVER charged more than the cheapest tier whose
    /// included volume covers their usage — see that method for the formula.</para>
    /// </summary>
    public const decimal OveragePerOrderEur = 0.50m;

    public static int GetOrderLimit(string plan) =>
        Limits.TryGetValue(plan, out var limits) ? limits.Orders : PilotOrderLimit;

    public static int GetSupplierLimit(string plan) =>
        Limits.TryGetValue(plan, out var limits) ? limits.Suppliers : PilotSupplierLimit;

    /// <summary>
    /// Effective order limit for an org: the admin per-org override when set
    /// (and non-negative), otherwise the plan default. Used by the billing limit
    /// checks so the founder can grant a prospect extra headroom without changing
    /// their plan. A null or negative override falls back to the plan default.
    /// </summary>
    public static int GetEffectiveOrderLimit(string plan, int? orderLimitOverride) =>
        orderLimitOverride is { } o && o >= 0 ? o : GetOrderLimit(plan);

    /// <summary>
    /// When a trial ends: the latest of start + <see cref="PilotDuration"/>, the stored end and the
    /// admin override (nulls ignored), so a trial begun under an older, shorter rule gets the full window.
    /// </summary>
    public static DateTime GetEffectiveTrialEnd(DateTime trialStartedAt, DateTime? trialEndsAt, DateTime? trialEndsAtOverride)
    {
        var end = trialStartedAt.Add(PilotDuration);
        if (trialEndsAt is { } stored && stored > end) end = stored;
        if (trialEndsAtOverride is { } hand && hand > end) end = hand;
        return end;
    }

    /// <summary>
    /// Effective supplier limit for an org: the admin per-org override when set
    /// (and non-negative), otherwise the plan default.
    /// </summary>
    public static int GetEffectiveSupplierLimit(string plan, int? supplierLimitOverride) =>
        supplierLimitOverride is { } s && s >= 0 ? s : GetSupplierLimit(plan);

    // ── Monthly list price in EUR per plan (the published /pricing ladder) ──
    // Used by the admin overview to compute a DB-side MRR estimate from active
    // paid orgs. Pilot is €0 (trial, no Stripe). Enterprise is contact-sales
    // with no fixed list price, so it contributes €0 to the DB estimate — its
    // real revenue only shows up via the Stripe reconciliation path.
    public static readonly IReadOnlyDictionary<string, decimal> MonthlyPriceEur =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            [Pilot]       = 0m,
            [Growth]      = 149m,
            [Operations]  = 399m,
            [Integration] = 999m,
            [Distributor] = 1_499m,
            [Enterprise]  = 0m,   // custom — not a fixed list price
            [Starter]     = 35m,
            [Pro]         = 69m,
            [Business]    = 149m,
            [Scale]       = 399m,
        };

    /// <summary>
    /// Published monthly list price (EUR) for a plan, or 0 when the plan has no
    /// fixed list price (Pilot, Enterprise) or is unrecognised.
    /// </summary>
    public static decimal GetMonthlyPriceEur(string plan) =>
        MonthlyPriceEur.TryGetValue(plan ?? string.Empty, out var price) ? price : 0m;

    /// <summary>
    /// True when <paramref name="toPlan"/> costs strictly less per month than <paramref name="fromPlan"/>,
    /// across both ladders. Enterprise ranks highest; an equal price is not a downgrade.
    /// </summary>
    public static bool IsDowngrade(string? fromPlan, string toPlan)
    {
        static decimal Rank(string p) => p == Enterprise ? decimal.MaxValue : GetMonthlyPriceEur(p);
        var from = fromPlan?.ToLowerInvariant();
        var to = toPlan.ToLowerInvariant();
        return IsKnownPlan(from) && IsKnownPlan(to) && Rank(to) < Rank(from!);
    }

    public static bool IsPaidPlan(string plan) =>
        plan is Growth or Operations or Integration or Distributor or Enterprise
            or Starter or Pro or Business or Scale;

    // ── Best-price overage (fixes the upgrade-disincentivising inversion) ──
    /// <summary>
    /// Ordered self-serve PAID tiers (lowest included-volume → highest), each
    /// paired with its included monthly order volume and flat list price. This is
    /// the single source of truth the best-price overage calc walks; every value
    /// is derived from <see cref="Limits"/> + <see cref="MonthlyPriceEur"/> so there
    /// are NO duplicated magic numbers. Pilot (trial) and Enterprise (custom /
    /// contact-sales, no fixed list price) are intentionally excluded — neither
    /// meters overage. Declared AFTER both source dictionaries so the static field
    /// initializer reads fully-populated values.
    /// </summary>
    public static readonly IReadOnlyList<(string Plan, int Included, decimal FlatEur)> SelfServeLadder =
    [
        (Growth,      GetOrderLimit(Growth),      GetMonthlyPriceEur(Growth)),
        (Operations,  GetOrderLimit(Operations),  GetMonthlyPriceEur(Operations)),
        (Integration, GetOrderLimit(Integration), GetMonthlyPriceEur(Integration)),
        (Distributor, GetOrderLimit(Distributor), GetMonthlyPriceEur(Distributor)),
    ];

    /// <summary>The document ladder's rows, built the same way as <see cref="SelfServeLadder"/>.</summary>
    public static readonly IReadOnlyList<(string Plan, int Included, decimal FlatEur)> DocumentSelfServeLadder =
        DocumentLadder.Select(p => (p, GetOrderLimit(p), GetMonthlyPriceEur(p))).ToArray();

    /// <summary>
    /// BEST-PRICE overage: returns how many overage units (orders or documents, each
    /// billed at the plan's own <see cref="GetOverageRateEur"/>) an org on
    /// <paramref name="plan"/> with <paramref name="effectiveLimit"/> included units should
    /// be billed for after processing <paramref name="ordersUsed"/> — WITHOUT ever charging
    /// more than the plan's own ladder would for that volume. This removes the
    /// upgrade-disincentivising "overage inversion".
    ///
    /// <para><b>Formula.</b> The ladder is the plan's own: <see cref="DocumentSelfServeLadder"/>
    /// for a document plan, <see cref="SelfServeLadder"/> otherwise; the cap never compares
    /// against the other ladder. Let r = this plan's rate, F_T = its flat price,
    /// over = max(0, used − limit), rawEur = over × r, and r_top = the top tier's rate:
    /// <code>
    ///   topTier = the ladder's top tier (largest included volume)
    ///   if used ≤ topTier.Included:
    ///       capEur            = min flat price over tiers whose Included ≥ used
    ///       effectiveTotalEur = min(F_T + rawEur, capEur)      // never beat the next flat tier
    ///   else if limit ≥ topTier.Included:                       // admin headroom above the top tier:
    ///       effectiveTotalEur = F_T + (used − limit) × r        // meter only beyond the granted limit
    ///   else:                                                   // above the top tier: priced as the top tier
    ///       effectiveTotalEur = topTier.FlatEur + (used − topTier.Included) × r_top
    ///   effectiveOverageEur   = max(0, effectiveTotalEur − F_T)
    ///   return floor(effectiveOverageEur / r)                   // whole units for Stripe, never past the cap
    /// </code>
    /// Legacy: every rate is €0.50 (r = r_top) and every flat price a whole number of euros,
    /// so the result is exact and the floor changes nothing. Document plans: rates differ
    /// (Starter €0.24 … Scale €0.08), so the capped total is usually not a whole number of
    /// units; flooring bills at most the cap, never past it.</para>
    ///
    /// <para>Returns 0 for non-self-serve plans (Pilot/Enterprise) and whenever the
    /// org is within its included volume. Admin per-org limit overrides flow through
    /// <paramref name="effectiveLimit"/>, so a granted-headroom org is metered against
    /// its real limit (and a raised limit only ever shrinks the overage). The cap is
    /// applied against the PLAN's published flat price (<see cref="MonthlyPriceEur"/>),
    /// independent of any admin limit override.</para>
    /// </summary>
    public static int BestPriceOverageOrders(string plan, int effectiveLimit, int ordersUsed)
    {
        // Only active self-serve paid plans meter overage.
        if (!IsPaidPlan(plan) || plan == Enterprise) return 0;

        var over = Math.Max(0, ordersUsed - effectiveLimit);
        if (over == 0) return 0;

        var ladder  = IsDocumentPlan(plan) ? DocumentSelfServeLadder : SelfServeLadder;
        var rate    = GetOverageRateEur(plan);
        var flatEur = GetMonthlyPriceEur(plan);
        var rawEur  = over * rate;

        // The top self-serve tier (largest included volume) — above its inclusion
        // there is no higher tier to cap against, so pure metered overage applies.
        var topTier = ladder[^1];

        decimal effectiveTotalEur;
        if (ordersUsed <= topTier.Included)
        {
            // Cap the monthly total at the cheapest tier whose inclusion covers usage
            // (always exists here because used ≤ topTier.Included). Never charge more
            // than that flat price — this is the upgrade-incentive guarantee.
            var capEur = ladder
                .Where(t => t.Included >= ordersUsed)
                .Min(t => t.FlatEur);
            effectiveTotalEur = Math.Min(flatEur + rawEur, capEur);
        }
        else if (effectiveLimit >= topTier.Included)
        {
            // Admin-granted headroom at/above the top tier's inclusion: the org keeps its
            // plan's flat price and meters ONLY beyond its real (overridden) limit — a
            // raised limit must only ever shrink the overage, never re-meter the granted
            // orders against the smaller published inclusion.
            effectiveTotalEur = flatEur + (ordersUsed - effectiveLimit) * rate;
        }
        else
        {
            // Above the top self-serve tier: charge as if on the top tier, excess at the top
            // tier's rate (no higher self-serve tier exists to cap).
            effectiveTotalEur = topTier.FlatEur + (ordersUsed - topTier.Included) * GetOverageRateEur(topTier.Plan);
        }

        var effectiveOverageEur = Math.Max(0m, effectiveTotalEur - flatEur);
        return (int)Math.Floor(effectiveOverageEur / rate);
    }

    // ── Feature gate: minimum plan required per feature ───────────────────
    // EVERY row here must have a real enforcement site in production code — see
    // BillingGateEnforcementIsRealTests, which reads compiled IL and fails the build otherwise.
    // A row nothing checks is not a gate, it is a false claim about the price list.
    // Plan 13b (2026-11-13) deleted eight rows with the legacy order surfaces they gated.
    private static readonly IReadOnlyDictionary<BillingFeature, string> MinimumPlan =
        new Dictionary<BillingFeature, string>
        {
            [BillingFeature.AdvancedAudit]      = Operations,
            // Enterprise SSO (SAML/OIDC) via Clerk Enterprise Connections.
            [BillingFeature.Sso]                = Enterprise,
        };

    private static readonly List<string> PlanOrder =
        new() { Pilot, Growth, Operations, Integration, Distributor, Enterprise };

    /// <summary>
    /// The lowest plan that includes <paramref name="feature"/>, or null when the feature has
    /// no gate-table entry (which <see cref="PlanHasFeature"/> treats as "nobody has it").
    /// This is the ONLY sanctioned way to name a plan in a user-facing gate message: WP-11
    /// found four 403 codes hardcoding "integration" for gates whose real minimum was Growth,
    /// telling customers to buy a €999 tier to unlock what their €149 tier already included.
    /// Derive, never hardcode.
    /// </summary>
    public static string? GetMinimumPlan(BillingFeature feature) =>
        MinimumPlan.TryGetValue(feature, out var plan) ? plan : null;

    public static bool PlanHasFeature(string plan, BillingFeature feature)
    {
        // Document plans carry everything but Sso (nothing sells it; Enterprise-only).
        if (IsDocumentPlan(plan)) return feature != BillingFeature.Sso;
        if (!MinimumPlan.TryGetValue(feature, out var minPlan)) return false;
        var planIdx = PlanOrder.IndexOf(plan);
        var minIdx  = PlanOrder.IndexOf(minPlan);
        return planIdx >= 0 && planIdx >= minIdx;
    }

    public static BillingFeature[] FeaturesForPlan(string plan) =>
        Enum.GetValues<BillingFeature>()
            .Where(f => PlanHasFeature(plan, f))
            .ToArray();
}
