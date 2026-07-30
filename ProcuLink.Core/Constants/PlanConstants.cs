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
    public static readonly TimeSpan PilotDuration     = TimeSpan.FromDays(14);
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
        };

    // ── Per-plan monthly AI token limits ───────────────────────────────────
    // ONE shared monthly OpenAI token budget per org across ALL AI features
    // (PDF text/vision extraction, line-SKU mapping suggestions, email-body
    // NLP, schema inference). Sized so PDF-heavy orgs do not silently latch
    // the cap mid-month (~4k tokens/PDF → Pilot ≈ 50 PDFs, Growth ≈ 250,
    // Operations ≈ 625, Integration/Distributor ≈ 1,250, Enterprise ≈ 2,500).
    // PRECEDENCE: the global config key Ai:OpenAI:MonthlyTokenLimitPerOrg
    // (when set and > 0) overrides EVERY plan value — it is the production
    // emergency lever (see AiUsageTracker).
    public static readonly IReadOnlyDictionary<string, long> AiMonthlyTokenLimits =
        new Dictionary<string, long>
        {
            [Pilot]       = 200_000,
            [Growth]      = 1_000_000,
            [Operations]  = 2_500_000,
            [Integration] = 5_000_000,
            [Distributor] = 5_000_000,
            [Enterprise]  = 10_000_000,
        };

    /// <summary>
    /// Monthly AI token limit for a plan. Unknown or null plans fall back to
    /// the Pilot value (fail-safe: an unrecognised plan never gets a bigger
    /// AI budget than the trial tier).
    /// </summary>
    public static long GetAiMonthlyTokenLimit(string? plan) =>
        plan is not null && AiMonthlyTokenLimits.TryGetValue(plan, out var limit)
            ? limit
            : AiMonthlyTokenLimits[Pilot];

    /// <summary>
    /// AI monthly token budget for a READ-ONLY / delinquent org, overriding the
    /// paid-plan ceiling so a correct downgrade actually STOPS the OpenAI spend
    /// (audit 2026-07-10 P2). Founder decision 2026-07-10:
    /// <list type="bullet">
    ///   <item><c>past_due</c> keeps the Pilot GRACE budget (a transient Stripe
    ///   dunning state the customer usually recovers from) — but never MORE than
    ///   its own plan budget, so a past_due Pilot stays at Pilot.</item>
    ///   <item><c>read_only</c>, <c>cancelled</c>, <c>trial_expired</c> get ZERO —
    ///   these are non-recoverable / terminated states, and the org already cannot
    ///   process orders or use gated features, so any AI spend is pure cost leak.</item>
    /// </list>
    /// Intended for statuses where <see cref="AccountStatusConstants.IsReadOnly"/> is
    /// true; a good-standing status defensively returns the full plan budget (no clamp).
    /// </summary>
    public static long GetDelinquentAiMonthlyTokenLimit(string? plan, string accountStatus)
    {
        if (!AccountStatusConstants.IsReadOnly(accountStatus))
            return GetAiMonthlyTokenLimit(plan); // not delinquent — no clamp

        // past_due: Pilot grace, capped at the plan's own budget (Math.Min so a
        // sub-Pilot tier, should one ever exist, is never raised to Pilot).
        if (string.Equals(accountStatus, AccountStatusConstants.PastDue, StringComparison.OrdinalIgnoreCase))
            return Math.Min(GetAiMonthlyTokenLimit(plan), AiMonthlyTokenLimits[Pilot]);

        // read_only / cancelled / trial_expired: no AI budget at all.
        return 0L;
    }

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
        };

    /// <summary>
    /// Published monthly list price (EUR) for a plan, or 0 when the plan has no
    /// fixed list price (Pilot, Enterprise) or is unrecognised.
    /// </summary>
    public static decimal GetMonthlyPriceEur(string plan) =>
        MonthlyPriceEur.TryGetValue(plan ?? string.Empty, out var price) ? price : 0m;

    public static bool IsPaidPlan(string plan) =>
        plan is Growth or Operations or Integration or Distributor or Enterprise;

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

    /// <summary>
    /// BEST-PRICE overage: returns how many "overage orders" (at
    /// <see cref="OveragePerOrderEur"/> each) an org on <paramref name="plan"/> with
    /// <paramref name="effectiveLimit"/> included orders should be billed for after
    /// processing <paramref name="ordersUsed"/> orders — WITHOUT ever charging more
    /// than the cheapest higher self-serve tier whose included volume covers the
    /// usage. This removes the upgrade-disincentivising "overage inversion".
    ///
    /// <para><b>Formula.</b> Let r = <see cref="OveragePerOrderEur"/>, F_T = this
    /// plan's flat price, over = max(0, used − limit), and rawEur = over × r:
    /// <code>
    ///   topTier = the highest self-serve tier (largest included volume)
    ///   if used ≤ topTier.Included:
    ///       capEur            = min flat price over tiers whose Included ≥ used
    ///       effectiveTotalEur = min(F_T + rawEur, capEur)      // never beat the next flat tier
    ///   else if limit ≥ topTier.Included:                       // admin headroom above the top tier:
    ///       effectiveTotalEur = F_T + (used − limit) × r        // meter only beyond the granted limit
    ///   else:                                                   // above the TOP self-serve tier:
    ///       effectiveTotalEur = topTier.FlatEur + (used − topTier.Included) × r
    ///   effectiveOverageEur   = max(0, effectiveTotalEur − F_T)
    ///   return round(effectiveOverageEur / r)                   // back to an integer order count for Stripe
    /// </code>
    /// Because every tier flat price is a whole number of euros and r = €0.50, the
    /// capped overage is always an exact integer number of orders.</para>
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

        var rate    = OveragePerOrderEur;
        var flatEur = GetMonthlyPriceEur(plan);
        var rawEur  = over * rate;

        // The top self-serve tier (largest included volume) — above its inclusion
        // there is no higher tier to cap against, so pure metered overage applies.
        var topTier = SelfServeLadder[^1];

        decimal effectiveTotalEur;
        if (ordersUsed <= topTier.Included)
        {
            // Cap the monthly total at the cheapest tier whose inclusion covers usage
            // (always exists here because used ≤ topTier.Included). Never charge more
            // than that flat price — this is the upgrade-incentive guarantee.
            var capEur = SelfServeLadder
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
            // Above the top self-serve tier: charge as if on the top tier plus pure
            // metered overage for the excess (no higher self-serve tier exists to cap).
            effectiveTotalEur = topTier.FlatEur + (ordersUsed - topTier.Included) * rate;
        }

        var effectiveOverageEur = Math.Max(0m, effectiveTotalEur - flatEur);
        return (int)Math.Round(effectiveOverageEur / rate, MidpointRounding.AwayFromZero);
    }

    // ── Feature gate: minimum plan required per feature ───────────────────
    // EVERY row here must have a real enforcement site in production code — see
    // BillingFeatureGateCoverageTests.EnforcedBy, which fails the build otherwise.
    // A row nothing checks is not a gate, it is a false claim about the price list.
    private static readonly IReadOnlyDictionary<BillingFeature, string> MinimumPlan =
        new Dictionary<BillingFeature, string>
        {
            [BillingFeature.BulkMapping]        = Operations,
            [BillingFeature.Cxml]               = Operations,
            [BillingFeature.AdvancedAudit]      = Operations,
            // ── Delivery / ingestion CHANNELS are decoupled from VOLUME ──────
            // These were gated to Integration, which forced a volume upgrade just
            // to unlock a channel. They are now available on ALL paid self-serve
            // plans (Growth+), so picking a channel never forces a volume tier.
            // Pilot stays restricted (PlanHasFeature returns false below Growth).
            [BillingFeature.WebhookDelivery]    = Growth,
            [BillingFeature.EmailIngestion]     = Growth,
            [BillingFeature.SftpIngestion]      = Growth,
            [BillingFeature.S3Ingestion]        = Growth,
            [BillingFeature.ErpConnectors]      = Enterprise,
            [BillingFeature.CustomSupplierRules]= Enterprise,
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
