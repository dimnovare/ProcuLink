# Stripe → Plan Reconciliation Job — Design

**Date:** 2026-07-10
**Author:** Claude (with founder decisions)
**Status:** Approved decisions; awaiting spec review
**Area:** Billing (LIVE, real money — high-care; founder must be present to merge/deploy)

## Problem

`organisations.plan` and `organisations.account_status` are mutated ONLY reactively by
Stripe webhook handlers in `BillingController`:

- `HandleCheckoutCompletedAsync` (~:260) — grant on `checkout.session.completed`.
- `HandleSubscriptionUpdatedAsync` (~:312) — status/plan on `customer.subscription.updated`.
- `HandleSubscriptionDeletedAsync` (~:354) — revert to frozen Pilot on `customer.subscription.deleted`.

There is **no reconciliation** against Stripe as source of truth. Proven consequences:

- **P1** — a missed/failed `customer.subscription.deleted` or `.updated` webhook leaves an org
  on a paid tier forever.
- **P2** — persisted `StripeSubscriptionId`/`StripeCustomerId` are trusted unconditionally; a
  test-mode or already-deleted subscription keeps its paid plan because live webhooks never
  fire for it. Real prod example: org `75abde9a` has `plan='growth'` with Stripe ids that both
  404 on the live `sk_live` key.

## Goal

An **idempotent, recurring Hangfire job** that, for every org with a non-null
`StripeSubscriptionId`, re-derives `plan` + `account_status` from Stripe as source of truth,
correcting drift and downgrading vanished/dead subscriptions. Org-scoped throughout. Fully
unit-tested against a faked Stripe transport — **never against prod or any live Stripe object.**

## Founder decisions (locked 2026-07-10)

| # | Decision | Choice |
|---|----------|--------|
| a | Downgrade target for a vanished/dead subscription | **Frozen Pilot + `read_only`** (mirrors the `subscription.deleted` handler) |
| b | Grace period before applying the read-only downgrade | **3 days confirmed-missing** (new nullable column) |
| c | Dead Stripe ids on downgrade | **Clear `StripeSubscriptionId` + `StripePriceId`, keep `StripeCustomerId`**; set status `canceled`, log old id |
| d | Reconciliation cadence | **Daily 02:00 UTC** |

## Architecture

### Placement
- Stripe.net (51.1.0) is referenced **only** by `ProcuLink.Api`. The Worker references Api.
  So the reconciliation service lives in `ProcuLink.Api\Services` alongside `StripeBillingService`
  and `BillingController`; its tests live in `ProcuLink.Api.Tests` (where the faked `IStripeClient`
  pattern already exists — see `OverageInvoiceAttachTests`).
- The recurring job is a thin Worker wrapper registered in `Worker.cs`, delegating to the service
  (same shape as `StuckOrderDetectionJob` → `IStuckOrderDetectionService`).

### Stripe client construction (CRITICAL)
The **Worker process never sets `StripeConfiguration.ApiKey`** (only `Api/Program.cs:72` does).
Relying on the process-global client would make Stripe calls fail auth inside the Worker.
Therefore the service constructs its own client from config:

```
var client = _stripeClient ?? new StripeClient(_config["Stripe:SecretKey"]);
var subscriptions = new SubscriptionService(client);
```

`_stripeClient` is an optional injected `IStripeClient?` (unregistered in DI; tests supply a fake).
This mirrors `StripeBillingService`'s test seam exactly and makes the service work identically in
Api and Worker processes.

### Shared plan/status mapping (DRY, anti-drift)
`MapPriceIdToPlan` and the Stripe-status → `AccountStatus` switch currently live **private** in
`BillingController`. Reconciliation needs identical mapping; duplicating it in a high-care billing
path invites silent drift. Extract both into a shared static helper:

```
public static class StripeBillingMapping
{
    // priceId → plan constant (config-driven), or null when unrecognised (keep existing plan).
    public static string? MapPriceIdToPlan(IConfiguration config, string? priceId);

    // Stripe subscription.status → AccountStatus constant, given the current status as fallback.
    public static string MapStatusToAccountStatus(string stripeStatus, string currentStatus);
}
```

`BillingController` is refactored to delegate to it (observable webhook behaviour byte-identical;
`BillingControllerTests` re-run to prove it). This is a targeted improvement justified by the work.

### New persisted state (grace period)
Add one nullable column to `Organisation`:

```
/// <summary>
/// UTC instant the reconciliation job FIRST observed this org's Stripe subscription
/// missing (Stripe GetAsync 404 resource_missing). NULL = not currently observed missing.
/// Drives the 3-day grace window before a vanished subscription is downgraded to frozen
/// Pilot + read_only, protecting a real paying customer from a transient 404 / key slip.
/// Cleared when the subscription reappears healthy (self-heal) or once the downgrade is applied.
/// </summary>
public DateTime? StripeReconciliationMissingSince { get; set; }
```

EF migration `AddStripeReconciliationMissingSince` (nullable, no default, additive).

## Reconciliation logic — `ReconcileOrgAsync(Guid orgId, ct)`

Load the tracked org (org-scoped, `Id == orgId`). If `StripeSubscriptionId` is null/blank, return
(nothing to reconcile). Call `subscriptions.GetAsync(org.StripeSubscriptionId, ct)` and classify:

**1. Fetch succeeds — classify by `subscription.status`:**

- **LIVE_PAID** (`active`, `trialing`): re-derive plan via `MapPriceIdToPlan(priceId)`; keep
  existing plan when mapping is null (unrecognised price → likely manual Enterprise; never clobber).
  Set `AccountStatus` via `MapStatusToAccountStatus`. Persist plan/priceId/subscriptionStatus/status
  + `BillingUpdatedAt` only if any changed. Clear `StripeReconciliationMissingSince` (self-heal).
  This branch also silently repairs a **missed `checkout.completed` upgrade** — access is granted,
  so it applies immediately (no grace).
- **DUNNING** (`past_due`, `unpaid`): `AccountStatus → past_due`, KEEP plan (mirrors
  `HandleSubscriptionUpdatedAsync`). Real Stripe signal, applied immediately. Clear missing-since.
- **DEAD** (`canceled`, `incomplete_expired`): downgrade **immediately** (no grace — a resolving
  GetAsync that returns `canceled` is authoritative, not a transient misread). See Downgrade below.
- **UNKNOWN/other** (`incomplete`, `paused`, anything unmapped): no change, log at Information.
  Clear missing-since (the sub resolves, so it is not missing).

**2. Fetch throws `StripeException` that is specifically a 404 resource_missing**
(`HttpStatusCode == NotFound` AND `StripeError?.Code == "resource_missing"`): the subscription is
gone. Apply the **grace state machine**:

- `StripeReconciliationMissingSince == null` → set it to `DateTime.UtcNow`, save, log Warning +
  Sentry breadcrumb. **No downgrade this run.**
- else `UtcNow - MissingSince >= 3 days` → **downgrade** (see below).
- else (inside grace) → no change, log Information.

**3. Fetch throws any other `StripeException`** (401 auth / bad key, 429 rate-limit, 5xx, network):
**never downgrade, never touch state.** Log Error, return. A wrong/missing key (401) must never be
misread as "subscription missing" — this is a safety-critical distinction (dedicated test).

**4. Stripe not configured** (`Stripe:SecretKey` blank): the whole sweep no-ops (mirrors
`GetStripeMrrAsync` returning null). Nothing is ever downgraded without a configured key.

### Downgrade (decisions a + c)
```
org.Plan                    = PlanConstants.Pilot;
org.AccountStatus           = AccountStatusConstants.ReadOnly;
var deadSubId               = org.StripeSubscriptionId;   // capture for the log
org.StripeSubscriptionId    = null;                       // (c) clear
org.StripePriceId           = null;                       // (c) clear
org.StripeSubscriptionStatus= "canceled";                 // forensic marker
org.StripeReconciliationMissingSince = null;              // downgrade done
org.BillingUpdatedAt        = DateTime.UtcNow;            // keep StripeCustomerId
// log Warning with orgId + deadSubId; emit billing_cancelled analytics (reuse EmitBillingCancelledAsync).
```
Clearing `StripeSubscriptionId` makes the org fall OUT of the sweep predicate next run →
self-terminating, no repeated work.

## Recurring job — `BillingReconciliationJob` (Worker)

```
[Queue("background")]
[AutomaticRetry(Attempts = 0)]
public async Task ExecuteAsync(CancellationToken ct)
{
    var ids = await _db.Organisations.AsNoTracking()
        .Where(o => o.StripeSubscriptionId != null && o.StripeSubscriptionId != "")
        .Select(o => o.Id).ToListAsync(ct);
    foreach (var id in ids)
    {
        try { await _reconciliation.ReconcileOrgAsync(id, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Reconcile failed for org {OrgId}", id); } // per-org isolation
    }
}
```

Single sweep (not dispatcher/child): org count is small; matches `GetStripeMrrAsync`'s single-pass
style. Per-org `try/catch` isolates one org's failure from the rest. Registered in `Worker.cs`:

```
_recurringJobs.AddOrUpdate<BillingReconciliationJob>(
    "billing-reconciliation", job => job.ExecuteAsync(CancellationToken.None), "0 2 * * *");
```

Registered in `Worker/Program.cs` DI (`IBillingReconciliationService` → impl). Optionally also in
`Api/Program.cs` for symmetry (not required — only the Worker runs it).

## Idempotency (mandatory per CLAUDE.md)
- Healthy correction writes the same derived values every run → converges, no oscillation.
- Missing: `MissingSince` set only when null (ages correctly across runs); downgrade clears the
  sub id → org drops out of the sweep → runs exactly once.
- Self-heal clears `MissingSince` when the sub reappears.
- Re-running the whole sweep any number of times yields the same terminal DB state.

## Tests (`ProcuLink.Api.Tests`, faked `IStripeClient` — no HTTP, no prod)
Fake transport returns a canned `Subscription` (status + price id) or throws a constructed
`StripeException` (404 resource_missing, or 401 auth). Cases:

1. Healthy `active` sub, DB drifted (e.g. plan stale) → corrected to Stripe truth.
2. Missed upgrade: DB `pilot`, Stripe `active` growth → upgraded, status `active`.
3. `past_due` → `account_status=past_due`, plan kept.
4. `canceled` status (resolves) → immediate downgrade to Pilot+read_only, ids cleared per (c).
5. 404 resource_missing, first run → `MissingSince` set, **no** plan change.
6. 404 resource_missing, `MissingSince` 4 days ago → downgrade applied.
7. 404 resource_missing, `MissingSince` 1 day ago → no change (inside grace).
8. Self-heal: `MissingSince` set, then Stripe returns healthy → `MissingSince` cleared, plan correct.
9. **401 auth error → never downgrades, state untouched** (safety-critical).
10. Org-scope: two orgs, reconcile one → the other's row is untouched.
11. Stripe not configured (blank key) → sweep no-ops, no state change.
12. `StripeSubscriptionId` null → skipped (not in sweep, `ReconcileOrgAsync` returns early).
13. `BillingControllerTests` re-run green after the mapping extraction (byte-identical webhook behaviour).

## Out of scope / non-goals
- No dispatcher/child fan-out (single sweep is adequate; trivial to convert later if org count grows).
- No change to webhook handlers' observable behaviour (only the mapping extraction, proven by tests).
- Does not hard-clean org `75abde9a` (separate task); reconciliation will downgrade it after grace.
- No real Stripe calls in tests; **no execution against prod** — deploy is founder-gated.

## Risk & safety
- Billing is LIVE real money. The only state-changing path that removes access (downgrade) is
  double-guarded: (1) requires a configured key, (2) 404 must be `resource_missing` specifically
  (never a 401/5xx), (3) 3-day grace. Everything else only grants/keeps access or no-ops.
- Merge/deploy requires the founder present (per CLAUDE.md billing rule).

## Post-review additions (2026-07-11)

A 5-lens adversarial review (14 findings, 0 rejected) drove the following, all implemented + tested:

- **Shared-DbContext poisoning (HIGH):** the sweep shares one scoped `DbContext`; a failed
  `SaveChanges` left an org tracked and could batch into the next org's save. Fix: `ChangeTracker.Clear()`
  at the start of each `ReconcileOrgAsync`.
- **Mass-downgrade circuit breaker:** a persistent valid-but-wrong key (wrong mode/account) 404s
  every live sub and would downgrade the whole paying base after grace. `BillingReconciliationJob`
  now aborts + `LogCritical` when more than `Billing:ReconciliationMassDowngradeThreshold` (default 10)
  orgs are simultaneously past grace.
- **Concurrency:** `[DisableConcurrentExecution(600)]` on the job (no optimistic-concurrency token
  on `Organisation`).
- **Analytics resilience:** the `billing_cancelled` emit is wrapped in try/catch so a PostHog hiccup
  cannot fail or roll back the committed downgrade.
- **Re-subscribe adoption (founder decision B2):** before ANY downgrade (dead-status or
  past-grace-missing), `SubscriptionService.List(customer)` is consulted; a newer active/trialing
  subscription is ADOPTED (reconcile to it, replacing the stored dead id) instead of freezing a
  customer who is paying again.
- **`paused` → read_only (founder decision B3):** added to the shared `StripeBillingMapping`, so
  BOTH the webhook (`HandleSubscriptionUpdatedAsync`) and reconciliation treat a paused subscription
  as read_only. This is an intentional webhook behaviour change (no prior webhook test covered
  `paused`).
