using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcuLink.Infrastructure.Migrations
{
    /// <summary>
    /// Clears <c>organisations.stripe_price_id</c> on rows that hold no subscription, restoring the
    /// invariant the billing-interval read path depends on.
    ///
    /// <para>No schema change — the column is already nullable, and this migration's model snapshot
    /// is byte-identical to the previous one. It exists only because the code fix is forward-only.
    /// <c>BillingController.HandleSubscriptionDeletedAsync</c> nulled <c>stripe_subscription_id</c>
    /// but left <c>stripe_price_id</c> behind, and <c>BillingStatus.BillingInterval</c> is derived
    /// from that price id ALONE (<c>StripeBillingService.GetBillingIntervalFromPriceId</c>), which
    /// reports "monthly" for any id matching no configured <c>Stripe:*YearlyPriceId</c>. A cancelled
    /// workspace was therefore shown "Billed monthly" on the billing screen, directly beneath "Your
    /// subscription isn't active."</para>
    ///
    /// <para>Fixing the handler stops NEW cancellations leaving the field set. It does nothing for
    /// organisations already cancelled, and nothing ever would: a cancelled org has no subscription
    /// id, so it drops out of <c>StripeSubscriptionReconciliationService</c>'s sweep — which selects
    /// on exactly that id — and is never revisited. Without this backfill those rows keep the wrong
    /// answer permanently.</para>
    ///
    /// <para><b>Why the WHERE clause is exactly this.</b> Every production writer sets the two fields
    /// together — <c>HandleCheckoutSessionCompletedAsync</c>, <c>HandleSubscriptionUpdatedAsync</c>
    /// and <c>StripeSubscriptionReconciliationService.ApplyResolvedAsync</c> — and both clear sites
    /// (<c>DowngradeAsync</c>, and now the deletion handler) clear both. "<c>stripe_subscription_id
    /// IS NULL</c> while a <c>stripe_price_id</c> is present" is therefore not a state any correct
    /// path can produce: it is precisely the corruption being removed, and matching on it cannot
    /// touch a live subscription. The price id is deliberately NOT compared against the configured
    /// plan prices — a row whose price matches no configured id is the very case that renders as a
    /// fabricated "monthly", so excluding it would skip the rows that most need clearing.</para>
    ///
    /// <para>Expressed as SQL because a migration is the only place a data backfill can run; the
    /// repo's "EF Core only" rule governs query code, and EF exposes no set-based update here.</para>
    /// </summary>
    public partial class ACancelledOrgKeptAPriceIdAndSaidBilledMonthly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE organisations
                   SET stripe_price_id = NULL
                 WHERE stripe_price_id IS NOT NULL
                   AND stripe_subscription_id IS NULL;
                """);
        }

        /// <summary>
        /// Deliberately empty, and not recoverable by any other means.
        ///
        /// <para>Reversing this would mean writing a price id back onto organisations that hold no
        /// subscription — re-creating the exact inconsistency the change exists to remove. There is
        /// no stored original to restore: the cleared value was a stale copy of a price belonging to
        /// a subscription that no longer exists, and current subscription state is Stripe's, not
        /// ours. Rolling the code back without the data leaves these rows simply unpopulated, which
        /// the read path already handles — null is the documented answer for an org with no Stripe
        /// price on file, and the frontend hides the interval line entirely on null.</para>
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
