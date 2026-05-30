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
    public static readonly IReadOnlyDictionary<string, (int Orders, int Suppliers)> Limits =
        new Dictionary<string, (int, int)>
        {
            [Pilot]       = (PilotOrderLimit,  PilotSupplierLimit),
            [Growth]      = (150,              5),
            [Operations]  = (500,              10),
            [Integration] = (1_000,            20),
            [Distributor] = (2_500,            30),
            [Enterprise]  = (int.MaxValue,     int.MaxValue),
        };

    public static int GetOrderLimit(string plan) =>
        Limits.TryGetValue(plan, out var limits) ? limits.Orders : PilotOrderLimit;

    public static int GetSupplierLimit(string plan) =>
        Limits.TryGetValue(plan, out var limits) ? limits.Suppliers : PilotSupplierLimit;

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
            [BillingFeature.WebhookDelivery]    = Integration,
            [BillingFeature.EmailIngestion]     = Integration,
            [BillingFeature.CustomTemplates]    = Integration,
            [BillingFeature.ErpConnectors]      = Enterprise,
            [BillingFeature.CustomSupplierRules]= Enterprise,
            [BillingFeature.SlaOnboarding]      = Enterprise,
            [BillingFeature.SftpIngestion]      = Integration,
            [BillingFeature.S3Ingestion]        = Integration,
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
