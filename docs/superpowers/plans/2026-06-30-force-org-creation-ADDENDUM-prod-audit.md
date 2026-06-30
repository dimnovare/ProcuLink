# ADDENDUM — Prod audit + adopt-on-create revision (2026-06-30)

> Supersedes the "Task 7: delete/merge sub-keyed orgs" + hard-cutover assumptions in the main plan. The hard `org_`-only guard was correct in spirit but **unsafe for the actual prod state** (below). Replaced by **adopt-on-create + softened-resolve**.

## Prod audit (read-only, live Neon via `railway run`)

**All 10 prod organisations are legacy sub-keyed** (`clerk_org_id = user_…`, none have a Clerk `org_…`). Two hold real data:

| Created | Plan | Orders | Suppliers | Note |
|---|---|---:|---:|---|
| 2026-05-31 | pilot | 27 | 21 | biggest tenant |
| 2026-05-27 | **growth/active** | 6 | 1 | only paid/active plan — possible real customer |
| ×8 | pilot | 0–1 | 0–1 | near-empty test orgs |

Totals: 36 orders, 27 suppliers — all under sub-keyed orgs. (`370ca357` from STATUS.md no longer exists; these are 10 different orgs.)

**Consequence:** a hard `org_`-only guard would fail-close **100% of prod**; the FE gate alone would force all 10 users to create orgs and **strand their data**. Pre-deploy rollback snapshot of `organisations` key state captured to `prod-org-snapshot-2026-06-30.csv` (local scratchpad) — restore source for any re-key.

## Revised design — adopt-on-create + softened-resolve (`TenantResolutionMiddleware`, commit `d123e1e`)

- **`org_id` present & `org_`-prefixed:**
  - org exists by that key → **resolve**.
  - not found → **adopt-or-provision**: if an org exists `WHERE ClerkOrgId == sub` (the caller's OWN user id) → **re-key that row** `user_… → org_…` (same Id, all data/Stripe preserved), emit `org_adopted`; else **fresh-provision** (throttled) + `org_created`.
- **No `org_` (sub-only login):** resolve the user's own legacy org `WHERE ClerkOrgId == sub` if present (**softened — no lockout** during transition); else fail closed (never mint a new sub tenant).
- Unique-violation (Postgres 23505) race on adopt/provision → re-query by `org_` key + resolve.
- Invariants: adopt only ever targets the caller's own `sub` row (no cross-tenant attach); re-key via a **tracked** update (no AsNoTracking no-op); throttle stays on fresh-provision only. Full suite green (17 middleware tests incl. a no-cross-tenant-adopt case; 0 failures solution-wide).

## Deploy order — **BE FIRST, then FE** (corrected)

> The generic runbook suggested FE-first; that is **wrong** here. FE-first with the old BE provisions a **fresh empty `org_` tenant** for any user who creates an org in the gap — and once that empty `org_` row exists, the adopt branch (which only runs when the `org_` is *not found*) **never fires for them → permanent data stranding**. BE-first avoids this.

1. **Merge BE #11 first.** Softened-resolve keeps all 10 existing sub orgs working (no lockout); new sub-provisioning stops; adopt stays dormant (no `org_` tokens yet). Only brand-new signups are briefly affected (near-zero, pre-launch).
2. Confirm Railway **API + Worker** both deploy green.
3. **Merge FE #8** (gate). Now existing users → gate → create org → BE adopts their tenant (data preserved); new users → fresh org. Keep the window short.

## Per-org expectations after both deploys

Each user, on next login → forced to gate → create/select a Clerk org → adopt **re-keys their existing row in place** (no new tenant, all data preserved):
- 27-order pilot org: full history retained.
- growth/active org: re-keyed in place; **Stripe subscription rides the same row** → billing continuity preserved. **Verify this user last and most carefully.**
- 8 near-empty test orgs: re-keyed on first login, or dormant+harmless if never revisited (softened-resolve = never lock out).

## Pre-deploy checklist
- [ ] Clerk Dashboard `afterSignUpUrl`/`afterSignInUrl` reconciled (they override the JSX props) → gate or cleared; decide `/welcome`.
- [ ] Clerk Organizations enabled (so `<CreateOrganization>`/`<OrganizationList hidePersonal>` render).
- [ ] BE `dotnet test ProcuLink.slnx` green on `d123e1e`; FE build clean + 6 `orgGate` tests green (3 mapper failures are pre-existing/unrelated).
- [ ] Rollback snapshot captured (done: `prod-org-snapshot-2026-06-30.csv`).

## Post-deploy verification
1. After BE: API/Worker green; an existing sub user (browser) still loads (softened-resolve).
2. After FE: throwaway signup → forced to gate → create org → fresh empty `org_` (no loop). Then an existing **test** org user → gate → create org → **same data visible** + `organisations` row count unchanged (adopt worked). Growth org user **last**, watched, Stripe intact.

## Rollback
- FE: Vercel → promote previous deployment (instant; removes gate).
- BE: Railway → redeploy previous on **API and Worker**.
- Adopt re-key is a one-way DB mutation; revert specific `clerk_org_id` values from `prod-org-snapshot-2026-06-30.csv` if needed. Prefer roll-forward (re-deploy, let adopt re-apply) over key reverts.
