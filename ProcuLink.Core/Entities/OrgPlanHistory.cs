namespace ProcuLink.Core.Entities;

/// <summary>
/// Append-only history of an organisation's billing-relevant plan state: the plan
/// and the admin order-limit override, with the instant each combination became
/// effective. Written automatically by <c>ProcuLinkDbContext</c> whenever
/// <see cref="Organisation.Plan"/> or <see cref="Organisation.OrderLimitOverride"/>
/// changes (and once at org creation), plus a one-time boot backfill for orgs that
/// predate the table.
///
/// <para><b>Why this exists (yearly-billing over-billing corridor).</b> A yearly
/// subscription's renewal invoice decomposes into ~12 PAST calendar-month windows,
/// each metered for per-order overage. Metering those windows with TODAY's plan
/// would retroactively re-price history: a mid-year DOWNGRADE would re-meter the
/// old months at the new lower cap (wrongful overage charges); an upgrade would
/// under-bill. Overage metering therefore resolves the plan + override AS OF each
/// window's start — the latest history row with
/// <see cref="EffectiveFrom"/> ≤ windowStart — falling back to the org's current
/// values only when the org has no history at all (pre-feature orgs meter exactly
/// as before this table existed).</para>
/// </summary>
public class OrgPlanHistory
{
    public Guid Id { get; set; }

    /// <summary>Organisation this history row belongs to.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The org's plan from <see cref="EffectiveFrom"/> onwards (raw value; normalised at read time).</summary>
    public string Plan { get; set; } = string.Empty;

    /// <summary>The admin order-limit override in force from <see cref="EffectiveFrom"/> onwards. Null ⇒ plan default.</summary>
    public int? OrderLimitOverride { get; set; }

    /// <summary>Instant this plan/override combination became effective (UTC).</summary>
    public DateTimeOffset EffectiveFrom { get; set; }
}
