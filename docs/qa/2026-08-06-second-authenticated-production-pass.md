# Second authenticated production pass — 2026-08-06

Companion to `2026-08-01-wp-39-authenticated-production-pass.md`. That pass proved the manual
happy path. This one exists because **about fifteen packets landed after it** — WP-36's nine
failure screens, the SFTP host-key work, the SSO/Peppol claim removals, both P1 audit-trail
fixes, the UBL supplier-name fix and the supplier-response sanitiser — and the composed product
had not been driven by a real signed-in user since.

**Method.** Real Chrome, real production Clerk session, `https://proculink.eu`, real organisation
data (26 orders, 25 suppliers, Growth plan with admin limit overrides). API ground truth read
directly from `https://api.proculink.eu` using the live session token. Mobile checked by mounting
a 390 px same-origin iframe, because viewport resizing is inert in this harness.

**Nothing was sent to a third party.** Every supplier on this org is `ProcuLink Sample Supplier`
or a `Demo — …` fixture. No order was transformed or delivered during the pass; it was read-only
apart from one injected iframe, removed afterwards.

---

## What is verified working in production

These were changed since WP-39 and are confirmed live:

- **WP-36's failure panel is real and good.** The order WP-39 left failed (`delivery_dead_letter`) renders
  "We stopped trying to reach this supplier", a plain explanation of the retry exhaustion, two
  concrete actions ("Start sending again", "Check the delivery settings"), and the honest caveat
  "If nothing has changed at the supplier's end, this will probably fail the same way". It also
  correctly separates *field* problems from *delivery* problems: "No field problems — every
  required field is filled and checked. This order stopped for another reason."
- **Mobile holds up.** `/bridge` at 390 px: `scrollWidth === clientWidth === 387`, **zero**
  elements wider than the viewport, no horizontal scroll. Group I's responsive work survives.
- **Status accounting is internally consistent.** `/api/orders/summary` returns
  `{"byStatus":{"delivery_dead_letter":2,"pending_review":21,"delivered":3},"total":26}` — the
  parts sum to the total, and the pipeline strip renders the same five numbers.
- **The dashboard refuses to fabricate.** `DashboardContextLine` passes `blockers: null` while its
  source query is loading and simply omits the segment rather than showing a zero.
- **Auth boundary intact.** Session token accepted by the API; unauthenticated protected routes
  still redirect.

---

## Findings

### F1 (HIGH) — the operator is shown raw HTML as the reason their order failed, still

The production order left failed by WP-39 (PO `WP39-QA-001`; its id is not recorded here — this
repository is public) renders this **on screen**, inside the failure panel, as the explanation of
why the order failed:

```
HTTP 404: supplier endpoint returned an error. Response summary: <!-- Tip: Set the Accept
header to application/json to get errors in JSON format. --> <!DOCTYPE html> <html> <head>
```

This is the exact defect FE #94 was written to prevent, and **FE #94 does not stop it.**

The hole, verified in `main` and not a stale deploy:

| Where | What |
|---|---|
| `OrderProblemPanel.tsx:136` | `supplierReasonText(rejectionReason) ?? ctx.serverMessage ?? detailFallback` |
| `OrderProblemPanel.tsx:115` | `serverMessage: order.errorMessage?.trim() ? order.errorMessage : null` |
| `problemCopy.ts:74` | doc comment: `order.errorMessage` is *"rendered verbatim in its own block, never interpolated."* |

The sanitiser guards `rejectionReason`. Production's markup arrived through `order.errorMessage`,
which is rendered **verbatim by design**. One door guarded, the other left open — the same shape
as the billing-gate defect whose PR title was "the guard for exactly that was blind twice over".

**Why the tests could not catch it, which is the more useful half:** both
`__tests__/supplierReasonRendered.test.tsx:56` and `__tests__/unconfirmedFriction.test.tsx:65`
set `errorMessage: null` on their fixture. The render-level test added specifically to pin the
*wiring* pins only the `rejectionReason` wire. A fixture that nulls the other input makes a
one-sided guard look complete.

> **Generalised trap:** when a value can reach the screen by more than one field, a render test
> that nulls the other fields proves the path it exercises and hides the path it does not. Pin
> every input that can carry the value, or the fixture becomes the blind spot.

### F2 (HIGH) — the correct sentence already exists and the UI ignores it

Same order, `GET /api/orders/{id}/passport`, on **every** delivery attempt:

> `passport.deliveryAttempts[*].errorMessage` = "The supplier's endpoint was not found (HTTP 404).
> The delivery address in this supplier's delivery settings has most likely moved or contains a
> typo — confirm t…"

That is human-written operator copy, produced by WP-19 ("split 4xx, end the dead end"). The panel
prefers the raw blob over it. So sanitising is not even the main fix — **preferring the sentence
the backend already wrote is.** `passport.timeline[7].detail` carries the same raw blob a second
time, which is worth checking as a third exposure.

### F3 (MEDIUM) — three delivery numbers on one screen that cannot be reconciled

`/bridge`, within one viewport:

| Card | Number | Window shown |
|---|---|---|
| Throughput | 0 delivered | "last 30 days" |
| Delivery | 60% success rate, 2 failed | **none** |
| Supplier health | 33% | "last 30 days" |

The three deliveries all happened **2026-07-02**, 35 days before the pass, so "0 delivered · last
30 days" is correct and the unlabelled 60% is **all-time** (3 delivered / 5 terminal attempts).
An unlabelled all-time rate placed between two cards labelled "last 30 days" reads as a 30-day
number and contradicts both. This is a labelling defect, not a computation defect.

### F4 (MEDIUM) — Growth is sold an "Audit log" the backend gates at Operations

A real **Growth** org (`/api/billing/status` → `"plan":"growth"`) loading `/operations/log` is told:

> "The full delivery log is not included in your plan. This is included from the Operations plan
> up. Upgrade to turn it on."

While `src/lib/plans.ts` sells Growth the bullet `"Audit log"`, and CLAUDE.md §11.5 repeats it.

Backend ground truth: `PlanConstants.cs:276` — `[BillingFeature.AdvancedAudit] = Operations`,
enforced at `AuditController.cs:49`. There is **exactly one** audit-related `BillingFeature` in
the enum and it starts at Operations.

Same class as FE #88 (IMAP sold at the wrong tier) and FE #91 (SSO sold with nothing behind it).

**Why both truth guards missed it:** `gatedCapabilityClaims.test.ts` matches a capability sold at
the wrong *named tier* — "Audit log" contains no tier word, so it is invisible. And
`BillingFeatureGateCoverageTests` pins the ladder per **enum member**; a marketing bullet naming
no enum member at all is outside what it can see.

> **The uncovered class:** *a plan-card bullet that names a capability which no `BillingFeature`
> grants at that tier.* Neither guard can see it, because one needs a tier word and the other
> needs an enum member.

### F5 (LOW) — three different "active supplier" counts across three screens

`/bridge` header: "**1** active suppliers". `/library/suppliers`: "**26** active suppliers".
`/api/billing/status`: `"suppliersUsed": 25`. The dashboard's 1 is plausibly "suppliers with
recent orders" — a different meaning wearing the same word. The 25 vs 26 is a straight
disagreement between the list and the number the supplier-limit gate counts with, and the gate's
count is the one that decides whether a customer may add another supplier.

Also `"1 active suppliers"` — plural agreement.

### F6 (LOW, feature gap, not a regression) — nothing warns that two orders are the same PO

Several orders in the queue are real customer documents ingested more than once — one PO appears
three times (pdf, xlsx, pdf), and three further pairs exist. **These are correct.** They are deliberate
two-format test ingests uploaded seconds apart, and WP-22 dedupes one inbound *document*, so two
different documents rightly produce two orders.

The gap is that the product never says so. An operator working two of them sends the supplier the
same purchase order twice — the single most expensive mistake available in this product. Worth a
same-PO warning on the queue and the review screen. Filed, not fixed.

### Not findings — checked and dismissed

Recording these so nobody pays for them twice:

- **The founder's org shows `orderLimit: 100000` and `supplierLimit: 30` on a Growth plan.** Not a
  bug: `OrderLimitOverride` / `SupplierLimitOverride` are a designed admin feature
  (`AdminDtos.cs:68-69`, `AdminController.cs:378-403`, with an `EffectiveOrderLimit`). This looked
  like a P0 billing failure and is a comped internal account.
- **The dashboard greets the operator by first name.** CLAUDE.md §12 lists dashboard greetings as an
  anti-pattern to refuse to build, but `DashboardContextLine.tsx:3` records it as a
  **founder-approved mock (2026-07)**, shipped with tests. The rule is stale, not the feature.
  CLAUDE.md §12 is being corrected; the greeting stays.
- **Repeated four-letter "supplier codes" in the suppliers list.** Those are initials avatars
  concatenated into `innerText`, not supplier codes.
- **`Needs you 0` on mobile.** A load-time zero-state that resolves to 23; re-read after settling
  shows the correct number.

---

## What this pass did not cover

Stated so the next reader does not mistake absence for a pass:

- **No upload → parse → transform → deliver loop was run.** WP-39 proved that path 2026-08-01;
  this pass was read-only by choice, so any regression in the *write* path since then is still
  unverified live.
- **`/library/mappings`, `/library/rules`, `/drafts`, `/operations/connectors`,
  `/operations/webhooks`** were not opened.
- **Tablet (768 px) not checked** — only 390 px and 1440 px.
- **The output designer** was not exercised; WP-16 is rewriting it concurrently.
