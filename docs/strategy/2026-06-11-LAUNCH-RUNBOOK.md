# LAUNCH RUNBOOK — final founder steps (2026-06-11)

State: ALL batches + flips LIVE. BE `96949c5` (2226 tests), FE `e825595` (88 unit + 48 e2e).
Migrations verified applied on Neon. Machine-verifiable gate items done.

## 1. G8 session (the redesign sign-off) — ~15 min
1. Open any order with exceptions on https://proculink.eu → lands in **Triage** view.
2. Work it for real: `A` accept suggestion, `E` manual entry, `S` skip, watch auto-advance,
   "Accept all ≥90%", Context Stage on selected card, Send-Readiness card, `C` send, "Next order →".
   `?` shows shortcuts. `g-d`/`g-b` switch sub-views. Resize to 13-inch width — no cut-off.
3. Check classic anytime: `?view=classic` or Full-document toggle.
4. SIGN-OFF A: conditional confirm checkbox — to enable: set Vercel env
   `NEXT_PUBLIC_CONFIRM_ALWAYS=false` (checkbox then appears only when something is wrong).
5. SIGN-OFF B: make Triage the universal default? Today: Triage iff exceptions exist, else
   Full document. (Current deterministic default may already be right — your call.)

## 2. Revision-authority flag flip (the reproducibility contract)
Pre-flip (one-time, test org):
- Republish a fresh revision per supplier (Connections → create draft (clone) → Run tests → Publish).
  This snapshots CURRENT config so pinned orders match today's behavior; old test orders pinned to
  stale 06-10 rev-1 snapshots are irrelevant test data.
Flip (Railway → BOTH services `ProcuLink` AND `aware-amazement`):
- Add env `Connections__RevisionAuthority=true` → redeploy both.
Post-flip verify (I can run): upload one order → confirm digest provenance shows `revision:{id}` +
delivery uses revision channel. Preview/conformance/replay already parity-tested.
Rollback: remove the env var. Flag-off is byte-identical (2175-test proven).

## 3. Yearly checkout verify — 2 min
Settings → Billing → pick Annual → checkout any paid tier → Stripe page should show the yearly
price (1488/3972/9948/14928 — your Stripe numbers, already in plans.ts). Cancel at Stripe page.
(Backend charges the real Stripe price regardless; this just eyeballs the session.)

## 4. Optional flips (later, deliberate)
- Delivered-only meter: Railway `Billing__CountDeliveredOnly=true` BOTH services + apply the copy
  diff stored in commit `d473637` message body (FAQ + Terms §5).
- Retention: per-org via `POST /api/admin/organisations/{id}/retention {retentionDays:90}`;
  then when ready `Retention__DryRun=false` on Worker. Default = nothing deletes, audit-only.

## 5. Known watch-items (low/theoretical, post-launch backlog)
Fable review leftovers: numeric-string OutputFormat enum parse; resolver lacks published-status
filter (lifecycle invariant currently guarantees it); yearly anchor-month double allowance
(customer-favorable); cancellation final-partial unmetered; 12-window webhook latency at yearly renewal.
Plus: live-mode Playwright legs (specs ready, need local :5223 stack or authed prod run).
