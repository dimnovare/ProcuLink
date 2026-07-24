# Admin account-status endpoint — design

**Date:** 2026-07-24
**Task:** `POST /api/admin/organisations/{id}/account-status`
**Gap it closes:** on 2026-07-24 the founder org (`account_status = read_only` after a Stripe
cancel) could only be lifted with a raw production `UPDATE`. There is no product surface for
the one transition that a frozen-Pilot org needs to come back to life.

> Founder was not in-session to approve the design interactively. The design below is written
> under explicitly stated assumptions (see **Open assumptions**); the permitted transition set is
> deliberately the narrowest one that closes the proven gap.

---

## 1. Ground truth — who writes `organisations.account_status`

| Writer | Site | Writes |
|---|---|---|
| Pilot start | `StripeBillingService.MarkPilotStartedAsync` (`:750`) | `trialing` |
| Trial-window arbiter | `StripeBillingService.MarkPilotExpiredIfNeededAsync` (`:761`) | `trial_expired` ⇄ `trialing`, **Pilot plan only**, **early-returns on `read_only`** (`:766`) |
| Checkout webhook | `BillingController` checkout-completed | `trialing` \| `active` |
| Subscription-updated webhook | `BillingController` → `StripeBillingMapping.MapStatusToAccountStatus` (`:48-56`) | Stripe-derived |
| Subscription-deleted webhook | `BillingController.HandleSubscriptionDeletedAsync` (`:412`) | `read_only` + plan → Pilot, `StripeSubscriptionId = null` |
| Reconciliation (adopt) | `StripeSubscriptionReconciliationService.ApplyResolvedAsync` | Stripe-derived (same mapping) |
| Reconciliation (downgrade) | `StripeSubscriptionReconciliationService.DowngradeAsync` (`:293`) | `read_only` + plan → Pilot, `StripeSubscriptionId = null` |

**The two facts the design turns on:**

1. **The Stripe reconciler cannot see a cancelled org.**
   `ReconcileOrgAsync` early-returns at `StripeSubscriptionReconciliationService.cs:80`:
   `if (org is null || string.IsNullOrWhiteSpace(org.StripeSubscriptionId)) return;`
   Both cancel paths null the subscription id. So for a cancelled org there is **nothing** to
   fight — an admin write survives. For an org that still HAS a subscription id, the reconciler
   re-derives status from Stripe on its next run, so an admin write there would be silently
   reverted (a lie). This is the gate.

2. **The trial-window arbiter is the thing that would still overwrite us.**
   `MarkPilotExpiredIfNeededAsync` runs on every `GetStatusAsync` call. It early-returns while
   the org is `read_only` (that is exactly why the frozen org is stuck), but the moment we set
   `trialing` it becomes live again and will re-derive `trialing` vs `trial_expired` from the
   effective trial end and the effective Pilot order cap.

## 2. Permitted transition set

**Exactly one transition is permitted:**

| From | To | Preconditions |
|---|---|---|
| `read_only` | `trialing` | `Plan == pilot` **and** `StripeSubscriptionId is null` |

Everything else returns `400` with a specific reason. Rationale per denial:

- **→ `active`** — a lie unless Stripe says active. If the org has a live subscription the
  reconciler overwrites it on the next run; if it has none, the org has no paid entitlement and
  `active` would grant paid features with no revenue. Never permitted.
- **→ `past_due` / `cancelled` / `read_only` / `trial_expired`** — all system-derived
  (Stripe status mapping, or the trial arbiter). No proven operational need to set them by hand,
  and each is a punitive/derived state that the owning writer would re-derive anyway. YAGNI.
- **from `trial_expired`** — already handled automatically: `MarkPilotExpiredIfNeededAsync` has a
  bidirectional branch that reactivates a `trial_expired` Pilot as soon as an admin extends the
  trial or raises the cap via `POST /api/admin/organisations/{id}/limits`. The 400 for this
  source names that endpoint, so the operator is taught the right tool rather than given a
  second one that would be immediately re-derived.
- **from `active` / `past_due` / `trialing`** — the org's status is Stripe-owned. Denied.
- **org with a live `StripeSubscriptionId`** — reconciler owns it. Denied even from `read_only`
  (this is the `paused`-subscription case: unpause in Stripe, don't paper over it here).
- **org whose plan is not Pilot** — `trialing` on a paid plan is not a state any writer produces.
  Denied.

## 3. Never lie: hand the verdict back to the canonical arbiter

The endpoint does **not** re-implement the trial-expiry predicate (that would be a fourth copy
of a rule that has already caused status-drift bugs in this codebase). Instead:

1. Validate the transition.
2. Write `account_status = trialing`, `SaveChanges`.
3. **Call `_billing.MarkPilotExpiredIfNeededAsync(orgId, ct)`** — the canonical arbiter — which
   immediately flips the org back to `trial_expired` if the trial window has passed or the Pilot
   order cap is spent.
4. Reload the org and return the **effective** status, plus `revertedByTrialWindow` and a
   plain-language `note` telling the operator to extend the trial via `.../limits`.

So the response can never claim a status the system does not actually hold, and the endpoint
cannot drift from the expiry rule because it does not own a copy of it.

## 4. API shape

```
POST /api/admin/organisations/{id:guid}/account-status
[AdminOnly] (controller-level), cross-tenant by route id — same as SetOrganisationLimits
```

Request:

```jsonc
{ "accountStatus": "trialing" }
```

Response `200`:

```jsonc
{
  "id": "...", "name": "...", "plan": "pilot",
  "previousAccountStatus": "read_only",
  "requestedAccountStatus": "trialing",
  "accountStatus": "trialing",          // EFFECTIVE, after the trial arbiter ran
  "revertedByTrialWindow": false,
  "effectiveTrialEndsAt": "2026-08-...",
  "note": null
}
```

Errors: `400` (unknown status, denied transition, denied source, live subscription, non-Pilot
plan, missing body), `403` (non-admin, via `[AdminOnly]`), `404` (unknown org).

## 5. Audit

Reuses the existing `AdminController.WriteAdminAuditAsync` helper → `audit_events` row:

- `Action` = `admin.org.account_status_changed`
- `EntityType` = `Organisation`, `EntityId` = `OrgId` = the target org
- `Payload` = `{ actor: { sub, email }, detail: { from, requested, effective, revertedByTrialWindow, plan } }`
- `CreatedAt` = UTC now

Who / when / from / to — all four present. The helper already swallows audit-write failures with
an error log so an accountability gap never fails the action itself.

## 6. Testing

TDD RED-first.

**Real Postgres** (`ProcuLink.Api.Tests/Integration/`, Docker-gated, `[DockerRequiredFact]`):
the status write must round-trip through the real migration and be visible from a **fresh**
`DbContext`, and the `audit_events` jsonb payload must persist. InMemory would mask both.

**InMemory unit tests** (`AdminControllerTests`) for the transition matrix: denied target,
denied source, live-subscription gate, non-Pilot gate, unknown status, missing org, missing body,
and the arbiter-reverts-us case.

## 7. Open assumptions

1. Only `read_only → trialing` is needed today. If ops later needs a manual freeze
   (`→ read_only`) it is a separate, founder-approved addition.
2. No frontend surface in this change — backend endpoint only (the founder's gap was "no product
   surface at all"; the admin UI can follow).
3. The endpoint does not touch `plan`, trial dates, or limits. Extending a trial stays
   `.../limits`; this endpoint only unfreezes.
