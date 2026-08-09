# Delivery Unconfirmed Park — Frontend Implementation Plan (v2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the new `delivery_unconfirmed` order status a truthful home in the UI: a label, a place in the "needs attention" surfaces, an honest explanation, and the two operator actions ("Send again" / "Mark as delivered").

**Architecture:** The status is a new member of the `OrderStatus` union; everything else keys off that. The two actions live in a dedicated `OrderWorkshop` panel added to the existing gate chain (the 3-column layout is LOCKED — add a branch, do not restructure). No backend work — that is PR #27 in the `ProcuLink` repo.

**Tech Stack:** Next.js 15 App Router, TypeScript, Tailwind, shadcn/ui, TanStack Query v5, **bun** (never npm/yarn).

**Worktree:** `%USERPROFILE%\source\repos\project-proculink\.claude\worktrees\park-ui` (branch `claude/delivery-unconfirmed-ui`, off `origin/main` @ `62fa95c`).

---

## THE PRECEDENT — read this before any task

**`delivery_held` is the same shape of change and it already shipped** (merge `62fa95c`, `feat/ops-health-delivery-held`). It is a status that needs a human, is NOT a failure, and had to be threaded through every one of the surfaces below. **Mirror it; do not reinvent.** v1 of this plan was written before it landed and was wrong in four places as a result.

Where the two differ, and it matters:

| | `delivery_held` | `delivery_unconfirmed` |
|---|---|---|
| Cause | deliberate (billing lapsed) | a crash lost the outcome |
| Resolves | itself, on reactivation | only when a human acts |
| Fix location | one global fix (Settings→Billing) | per-order, in the workshop |
| Backend `TotalProblemOrders` | **excluded** (founder call) | **included** — it is a fault |
| Redeliverable | **no** (deliberately excluded) | **yes** |

So: copy `delivery_held`'s *plumbing*, but do not copy its *classification*. It is a pause; ours is a fault.

## Global Constraints

- **bun only.** `bun install`, `bun run dev`, `bun run build`, `bun test`.
- App Router + `next/navigation`. No `react-router-dom`, no `VITE_*`, no `@clerk/clerk-react`.
- TanStack Query for server state, Client Components only. No `useEffect` fetching.
- **The 3-column Order Workshop layout is LOCKED.**
- **Plain-language copy.** No internal jargon — never "idempotency", "re-adopt", "dispatching row", "park".
- **Never claim what we did not observe.** The backend copy says "We **may** have sent this order" because a crash-recovery signal proves the send was *attempted*, not that it happened. Do not "tighten" that to "We sent".
- Copy pinned by the spec, to use verbatim in the confirm dialogs:
  - Send again → `If the supplier already received this order, sending again may give them a duplicate.`
  - Mark as delivered → `If the supplier never received this order, marking it delivered means it will not be sent.`
- **Backend contract** (PR #27, not yet merged — code it against, but it cannot be live-verified until that merges):
  - `POST /api/orders/{id}/mark-delivered` → `202 { status: "delivered" }`; valid only from `delivery_unconfirmed`, else 400.
  - `POST /api/orders/{id}/redeliver` now valid from `delivery_unconfirmed`.
  - ops-health response gains `deliveryUnconfirmed: number`, and it IS counted in `totalProblemOrders`.
  - `GET /api/orders/{id}` returns the park sentence in `errorMessage`.
- Verify in the browser; render at **390px** too. A static read of the JSX is not verification.

---

### Task 1: The status exists and reads honestly

**Files:**
- Modify: `src/types/procurement.ts` (`OrderStatus` union, lines 59-74 — `delivery_held` is at :69 with a comment above it)
- Modify: `src/components/bridge/UnifiedStatusBadge.tsx` (`STATUS_META`, lines 71-126 — mirror `delivery_held` at :108)
- Modify: `src/components/bridge/StatusJourney.tsx` (`CrossingStatus` :149, `STATUS_PILL` :151-166, `STATUS_STAGE` :168-179)
- Modify: `src/components/bridge/InboxView.tsx` (`STATUS_PRESENTATION` :78-90, `mapStatus` :236-247)

**Interfaces:** Produces `"delivery_unconfirmed"` as an `OrderStatus` member. Every later task depends on it.

- [ ] **Step 1: Add the union member**

`src/types/procurement.ts` — add to the union, with a one-line comment above it, matching how `delivery_held` (:69) is documented:

```ts
  // Sent, but a crash lost the outcome on a channel that can't tell us whether it arrived.
  // Waits for a human: send again, or mark delivered.
  | "delivery_unconfirmed"
```

- [ ] **Step 2: Add the badge label**

`src/components/bridge/UnifiedStatusBadge.tsx` `STATUS_META` — mirror the `delivery_held` entry's exact shape (`{ label, tone, pulse? }`; `pulse` omitted on purpose when nothing is in flight):

```ts
  // "Unknown", never "failed" — we don't know that it failed — and never "sent", which we
  // also don't know. Warning tone: needs a human, but is not a red failure.
  delivery_unconfirmed: { label: "Delivery unknown", tone: "warning" },
```

- [ ] **Step 3: Give it its own journey bucket — do NOT reuse an existing one**

`src/components/bridge/StatusJourney.tsx` defines `CrossingStatus`. The `delivery_held` work added a **brand-new `"held"` member** rather than reuse `review`/`delivering`/`failed`, because none was honest for a paused-not-failed status. The same reasoning applies here, more sharply: reusing `"failed"` would render a red Failed pill for a status we explicitly cannot call failed.

Add a new `CrossingStatus` member for the unconfirmed case with its own pill + stage, mirroring how `"held"` was added at :151-166 / :168-179.

- [ ] **Step 4: Map it in the Inbox**

`InboxView.tsx` — add the `STATUS_PRESENTATION` entry and the `mapStatus` branch pointing at your new bucket. `mapStatus` falls through to `return "new"` for unmapped statuses, which would show a parked order at stage 0 of the pipeline rail.

**Note:** `delivery_dead_letter` and `rejected_by_supplier` also fall through to `"new"` today. That is a pre-existing bug. **Do not fix it here** — report it.

- [ ] **Step 5: Verify**

`bun run build` — no type errors. Then render the Inbox with a parked order: badge reads "Delivery unknown", row is not shown as "New", no red Failed pill.

- [ ] **Step 6: Commit**

```bash
git add src/types/procurement.ts src/components/bridge/UnifiedStatusBadge.tsx src/components/bridge/StatusJourney.tsx src/components/bridge/InboxView.tsx
git commit -m "feat(orders): add delivery_unconfirmed with an honest label and its own journey bucket

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Fix the two surfaces that actively mislead

**Files:**
- Modify: `src/components/bridge/ExceptionDetail.tsx` (`deliveryStatusCopy` :42-58; `delivery_held` branch at :52-56; the lying fallback is at **:57**)
- Modify: `src/components/bridge/review/hooks/useOrderReview.ts` (`finalDeliveryMessage` :43-71; `delivery_held` branch at :62-69; the lying fallback is at **:70**)

**Why its own task:** these do not merely miss a label, they state falsehoods. The `ExceptionDetail` fallback says **"This order has not been sent yet."** — exactly backwards; it *was* sent, and that's the whole problem. `useOrderReview`'s says **"Delivery is still processing."** — implying it resolves itself; it won't, it's waiting on the operator.

- [ ] **Step 1: Share one sentence between the surfaces**

`useOrderReview.ts` already exports `BILLING_HELD_MESSAGE` / `BILLING_HELD_MESSAGE_PLURAL` (:22-41) as a "single sentence shared by two surfaces" pattern. Mirror it — export one `DELIVERY_UNCONFIRMED_MESSAGE` so `ExceptionDetail`, `useOrderReview`, and Task 5's panel cannot drift:

```ts
export const DELIVERY_UNCONFIRMED_MESSAGE =
  "We may have sent this order, but lost the connection before the supplier confirmed it — so we can't tell whether it arrived. Check with the supplier, then either send it again or mark it delivered.";
```

Note "**may** have sent" — mirrors the backend sentence and is the honest claim.

- [ ] **Step 2: Branch both fallbacks**

Insert a branch immediately BEFORE each fallback (`ExceptionDetail.tsx:57`, `useOrderReview.ts:70`), mirroring the `delivery_held` branch shape already sitting right above each one. Prefer the backend's `errorMessage` where the function has it (the backend returns the pinned sentence), falling back to `DELIVERY_UNCONFIRMED_MESSAGE`.

Tone: mirror `delivery_held`'s tone value, not the failure tone.

- [ ] **Step 3: Verify**

Render a `delivery_unconfirmed` order in the exceptions queue and the review screen. Neither says "has not been sent yet" or "still processing".

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/ExceptionDetail.tsx src/components/bridge/review/hooks/useOrderReview.ts
git commit -m "fix(orders): parked orders no longer claim they were never sent

The unknown-status fallbacks said 'This order has not been sent yet' and
'Delivery is still processing' — both false for an order that WAS sent and is
waiting on a human, not on the system.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: It counts as needing attention (and IS redeliverable)

**Files:**
- Modify: `src/components/bridge/inboxSend.ts` (`REDELIVERABLE_STATUSES` :22-25; the exclusion comment for `delivery_held` is :16-21)
- Modify: `src/components/bridge/inboxSend.test.ts` ("exactly two members" assertion :35-40; the "rejects every other raw backend status" test is :15-33)
- Modify: `src/components/bridge/BridgeDashboard.tsx` (`EXCEPTION_STATUSES` :82-90, `delivery_held` at :89; `FAILED_STATUSES` :70-75)
- Modify: `src/components/bridge/BridgeTopbar.tsx` (bell classifier :275-302)
- Modify: `src/components/bridge/LaneDrawer.tsx` (`liveStatusDot` :53-71)

**Careful — this is where our status DIVERGES from the precedent.** `delivery_held` was deliberately **excluded** from `REDELIVERABLE_STATUSES` (a redeliver must stay a 400 for it). `delivery_unconfirmed` is the opposite: the backend added it to `RedeliverableFrom` precisely so a human can choose to re-send. Do not copy the exclusion.

- [ ] **Step 1: Update the failing test first**

`inboxSend.test.ts:35-40` asserts exactly two members. The backend's `RedeliverableFrom` is now three. Update:

```ts
    // Mirrors backend OrderStatusMachine.RedeliverableFrom. A parked order IS redeliverable —
    // the park exists so a HUMAN can choose to re-send, accepting a duplicate risk the automatic
    // retry must never take for them. (delivery_held stays excluded: see inboxSend.ts.)
    expect([...REDELIVERABLE_STATUSES].sort()).toEqual(
      ["delivery_failed", "delivery_unconfirmed", "ready_to_deliver"],
    );
```

Also check the "rejects every other raw backend status" test (:15-33) — it lists statuses that must NOT be redeliverable. If it lists `delivery_unconfirmed`, remove it from there.

- [ ] **Step 2: Run it, watch it fail**

`bun test src/components/bridge/inboxSend.test.ts` → FAIL (set still has two).

- [ ] **Step 3: Add it to the set**

`inboxSend.ts` — add `"delivery_unconfirmed"` to `REDELIVERABLE_STATUSES`.

- [ ] **Step 4: Run it, watch it pass**

- [ ] **Step 5: Add it to the attention surfaces**

Mirror `delivery_held` in each:
- `BridgeDashboard.tsx` → `EXCEPTION_STATUSES` (needs a human). **NOT** `FAILED_STATUSES` — we do not know it failed, and the red chip would be a claim we can't support.
- `BridgeTopbar.tsx` → give it a `kind` in the bell classifier + the unread sum.
- `LaneDrawer.tsx` → an explicit `liveStatusDot` branch (amber, like the `pending_review`/`delivery_held` case) — it is not progressing, so not the blue in-progress dot.

Leave `FAILED_BUCKET` in `InboxView.tsx` alone, for the same reason as `FAILED_STATUSES`; report it.

- [ ] **Step 6: Verify**

`bun test && bun run build`. Render the dashboard with a parked order: it appears in "Needs attention", the bell notifies, and the Inbox row's Send-selected checkbox is enabled.

- [ ] **Step 7: Commit**

```bash
git add src/components/bridge/inboxSend.ts src/components/bridge/inboxSend.test.ts src/components/bridge/BridgeDashboard.tsx src/components/bridge/BridgeTopbar.tsx src/components/bridge/LaneDrawer.tsx
git commit -m "feat(orders): a parked order needs attention and can be sent again

Unlike delivery_held, delivery_unconfirmed IS redeliverable — the park exists so
a human can choose to re-send. It is not added to the failed sets: we do not know
that it failed.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: "All clear" must not hide a parked order

**Files:**
- Modify: **`src/app/(app)/operations/health/opsHealthState.ts`** (the exported `isAllClear`)
- Modify: `src/lib/api/operations.ts` (`OpsHealth` interface :22-53; `deliveryHeld?: number` at :40-46; mock at :75)
- Modify: `src/app/(app)/operations/health/page.tsx` (`TILES` :39-48; `allClear = isAllClear(h)` at :159)
- Test: `src/app/(app)/operations/health/deliveryHeld.test.tsx` is the sibling precedent — read it, then add yours alongside.

**v1 OF THIS PLAN WAS WRONG HERE.** It said to patch the `allClear` gate inline in `page.tsx`. That gate no longer lives there — it was extracted to the exported, tested `isAllClear()` in `opsHealthState.ts` (note: NOT `src/lib/opsHealthState.ts`, which does not exist). Patching `page.tsx` would create a second, divergent copy of the gate — exactly the duplication the extraction removed.

- [ ] **Step 1: Add the field to the interface**

`src/lib/api/operations.ts` — mirror `deliveryHeld?: number` exactly (optional, `?? 0` at use sites), and set it in the mock rows.

- [ ] **Step 2: Write the failing test**

Mirror `deliveryHeld.test.tsx`. Assert `isAllClear()` returns **false** when `deliveryUnconfirmed > 0` and everything else is zero.

- [ ] **Step 3: Run it, watch it fail**

- [ ] **Step 4: Extend the gate**

`opsHealthState.ts` — append one line, mirroring the `deliveryHeld` line exactly:

```ts
    (h.deliveryUnconfirmed ?? 0) === 0
```

**Context worth knowing:** the backend counts `delivery_unconfirmed` inside `totalProblemOrders` (unlike `delivery_held`, which the founder excluded), and `isAllClear` already checks `totalProblemOrders` as a backstop — so green already breaks today. This explicit check is still right: the file's own doc comment says the individual checks are primary and the backstop exists for categories the frontend has never heard of. Once we know about it, we check it directly.

- [ ] **Step 5: Add the tile**

`page.tsx` `TILES` — add a plain tile mirroring the dead-letter tile, linking to `/inbox?status=delivery_unconfirmed`.

**Deliberately NOT a `DeliveryPausedCard`-style bespoke card.** That card exists for `delivery_held` because its fix is one global action (Settings→Billing). A parked order's fix is per-order, in the workshop — so a plain tile that routes to the filtered inbox is the right shape. Note this choice in your PR.

- [ ] **Step 6: Verify**

`bun test && bun run build`. Render `/operations/health` with a parked order: the tile shows the count and the "All clear" banner does not appear.

- [ ] **Step 7: Commit**

```bash
git add "src/app/(app)/operations/health/opsHealthState.ts" "src/app/(app)/operations/health/page.tsx" src/lib/api/operations.ts "src/app/(app)/operations/health/"
git commit -m "fix(health): 'All clear' cannot hide a parked order

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: The two operator actions

**Files:**
- Modify: `src/lib/api-client.ts` (`realRedeliverOrder` :973-984 is the template; registration at :1347)
- Modify: `src/components/bridge/workshop/OrderWorkshop.tsx` (gate chain :439-454; `delivery_held` branch at :448-454; `ConfirmDialog` import :36, usage :789)
- Modify: `src/components/bridge/review/hooks/useSendFlow.ts` (poll predicate :176-187; `delivery_held` severity special-case :193-197)
- Create: the parked-order panel (mirror `src/components/bridge/workshop/BillingHeldPanel.tsx` for styling/`data-testid` conventions only — it is a single-link panel, not a two-button confirm flow)

**Interfaces:** Consumes `POST /api/orders/{id}/mark-delivered` (backend PR #27) and the existing `apiClient.redeliverOrder`. Produces `apiClient.markDelivered`.

- [ ] **Step 1: Add the API client method**

Mirror `realRedeliverOrder` (:973-984) exactly — auth header handling, error handling, return typing all come from it. Do not hand-roll a `fetch`. Add the mock counterpart and register `markDelivered` alongside `redeliverOrder` (:1347).

- [ ] **Step 2: Fix the send-flow poll predicate**

`useSendFlow.ts:176-187` — add `delivery_unconfirmed` to the terminal set. Without it, a "Send again" that re-parks (a second crash) burns the full 45s timeout and shows a false "Send failed".

**Do NOT copy `delivery_held`'s `"info"` severity** (:193-197). A billing hold is self-resolving, so "info" is right for it. A re-park is not — it still needs the operator. Use the neutral/error framing the `else` branch gives, and say why in your PR.

- [ ] **Step 3: Add the panel to the gate chain**

Add a branch for `delivery_unconfirmed` in the chain at :439-454, mirroring how the `delivery_held` branch (:448-454) sits there — its comment notes it belongs "because it has the same job": stop a status the normal mapper would misrepresent.

The panel renders the explanation (prefer the backend `errorMessage`; fall back to `DELIVERY_UNCONFIRMED_MESSAGE` from Task 2) and two actions:
- **Send again** → `apiClient.redeliverOrder(id)`
- **Mark as delivered** → `apiClient.markDelivered(id)`

Both confirm first, via the workshop's own `ConfirmDialog` (:36/:789) for visual consistency with the Send button beside it — **not** `useConfirm()`, and never a native `window.confirm` (banned). Use the spec-pinned copy verbatim:

```ts
// Send again
{ title: "Send this order again?",
  description: "If the supplier already received this order, sending again may give them a duplicate.",
  confirmLabel: "Send again" }

// Mark as delivered
{ title: "Mark this order as delivered?",
  description: "If the supplier never received this order, marking it delivered means it will not be sent.",
  confirmLabel: "Mark delivered", danger: true }
```

After each action invalidate both `["order", orderId]` and `["orders"]` (the `FailedPanels.tsx` pattern).

- [ ] **Step 4: Verify in the browser**

Seed an order in `delivery_unconfirmed`, open the workshop, confirm the panel renders with both actions, click each, confirm the status moves and the list refreshes. Render at 390px. Screenshot both.

**Note:** the backend PR is not merged, so a live end-to-end click needs it locally. If you cannot exercise the real endpoint, say so plainly rather than claiming verification you did not do.

- [ ] **Step 5: Commit**

```bash
git add src/lib/api-client.ts src/components/bridge/workshop/ src/components/bridge/review/hooks/useSendFlow.ts
git commit -m "feat(orders): Send again / Mark as delivered for a parked order

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: Documentation

**Files:**
- Modify: `src/app/(marketing)/help/dashboard-and-statuses/page.mdx` (happy-path table :20; failure-states table :27-33)
- Modify: `src/app/(marketing)/help/exceptions-and-stuck-orders/page.mdx`
- Modify: `src/lib/api/connectors.ts` (`MOCK_MANIFESTS`, 247 lines — one entry per protocol) and `src/lib/api/types.ts` (`ConnectorManifest` :565)

- [ ] **Step 1: Add the glossary row — to the FAILURE-STATES table**

`delivery_held` went in the **happy-path** table (:20) because a billing pause is deliberate and self-releasing. **Ours does not follow that precedent.** A parked order is a fault: the backend counts it in `totalProblemOrders`, and it resolves only when a human acts. Put it in the failure-states table (:27-33), between "Delivery failed" and "Dead-lettered".

Word it as its own thing — not "it failed", but "we don't know whether it arrived". Cross-reference the adjacent "Delivered ≠ accepted" section.

(v1 of this plan said "It is not a failure state" while also placing it in the failure table. That contradiction is resolved here: it IS a fault, so the table is right and the wording was wrong.)

- [ ] **Step 2: Explain the choice**

`exceptions-and-stuck-orders/page.mdx` — a new subsection: what happened (we sent it, then lost the connection before the supplier confirmed), why we don't just retry (on email and ERP connections a re-send can arrive twice, and we'd rather ask than guess), and the two actions with their consequences.

**Note:** this doc has zero mention of `delivery_held`/billing — that is a pre-existing hole, not a template. Report it; don't fill it here.

- [ ] **Step 3: Per-channel caveat**

`connectors.ts` — add the at-least-once caveat per `ConnectorManifest` (in `capabilities`, or a new optional field on the type). Conservative and accurate:
- `sftp`, `ftps` — a repeat send overwrites the same file; no duplicate.
- `http` — we send an idempotency key; endpoints that honour it will ignore a repeat.
- `email`, `smtp`, `erp_erply`, `erp_directo` — we can't tell whether a repeat would arrive twice, so if a send's outcome is ever unknown we ask you instead of resending.

- [ ] **Step 4: Verify + commit**

`bun run build` — MDX compiles, both help pages render.

```bash
git add "src/app/(marketing)/help/" src/lib/api/connectors.ts src/lib/api/types.ts
git commit -m "docs: explain unconfirmed deliveries and per-channel resend behaviour

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 7: Full verification

- [ ] `bun test` — report real counts.
- [ ] `bun run build` — no type errors, no new warnings.
- [ ] Render the flow: Inbox badge → dashboard "Needs attention" → health tile → workshop panel → both actions. Screenshot. Also at 390px.
- [ ] Push, open a PR referencing `ProcuLink` PR #27, and note in the body that the two should land together.

---

## Findings to report, NOT fix here

1. **`mapStatus` drops `delivery_dead_letter` and `rejected_by_supplier` to `"new"`** (`InboxView.tsx:236-247`), showing terminal orders at stage 0 of the pipeline rail. Pre-existing.
2. **`exceptions-and-stuck-orders/page.mdx` never mentions `delivery_held`** — the billing-pause work shipped without a doc entry.
3. **Two confirm patterns coexist** — the shared `useConfirm()` and the workshop's bespoke `ConfirmDialog`. Consolidation is its own task.
4. **`InboxView.tsx` has a second label source** (`STATUS_PRESENTATION`) its own comment flags as needing consolidation with `STATUS_META`.
