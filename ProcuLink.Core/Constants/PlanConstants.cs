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

    /// <summary>
    /// Per-order overage fee (EUR) charged on every order an active paid
    /// self-serve plan processes ABOVE its monthly order limit. Billed via a
    /// Stripe invoice item at the period boundary — going over the cap is always
    /// allowed, never blocked. Pilot has no overage (it is a hard trial cap) and
    /// Enterprise is custom-contracted (effectively unlimited).
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

    // ── Feature gate: minimum plan required per feature ───────────────────
    private static readonly IReadOnlyDictionary<BillingFeature, string> MinimumPlan =
        new Dictionary<BillingFeature, string>
        {
            [BillingFeature.Xml]                = Growth,
            [BillingFeature.Pdf]                = Growth,
            [BillingFeature.MappingLibrary]     = Growth,
            [BillingFeature.ValidationRules]    = Growth,
            [BillingFeature.BulkMapping]        = Operations,
            [BillingFeature.Cxml]               = Operations,
            [BillingFeature.DeliveryHistory]    = Operations,
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
            [BillingFeature.CustomTemplates]    = Integration,
            [BillingFeature.ErpConnectors]      = Enterprise,
            [BillingFeature.CustomSupplierRules]= Enterprise,
            [BillingFeature.SlaOnboarding]      = Enterprise,
        };

    private static readonly List<string> PlanOrder =
        new() { Pilot, Growth, Operations, Integration, Distributor, Enterprise };

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
