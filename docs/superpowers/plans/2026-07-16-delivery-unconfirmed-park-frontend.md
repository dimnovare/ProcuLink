# Delivery Unconfirmed Park — Frontend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the new `delivery_unconfirmed` order status a truthful home in the UI: a label, a place in the "needs attention" surfaces, an explanation, and the two operator actions ("Send again" / "Mark as delivered").

**Architecture:** The status is a new member of the `OrderStatus` union; everything else keys off that. The two actions live in a dedicated `OrderWorkshop` panel (the workshop's 3-column layout is LOCKED — add a panel to the existing failure-gate chain, do not restructure). No backend work — that is the sibling plan.

**Tech Stack:** Next.js 15 App Router, TypeScript, Tailwind, shadcn/ui, TanStack Query v5, **bun** (never npm/yarn).

**Repo:** `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink`
**Spec:** `ProcuLink/docs/superpowers/specs/2026-07-16-delivery-unconfirmed-park-design.md`
**Sibling plan (backend):** `ProcuLink/docs/superpowers/plans/2026-07-16-delivery-unconfirmed-park.md`

## Global Constraints

- **Work in an isolated git worktree**, not the shared checkout — parallel agents collide on `.next`. Audit/fix against `origin/main`, not a possibly-stale local `main`.
- **bun only.** `bun install`, `bun run dev`, `bun run build`, `bun test`.
- App Router + `next/navigation`. No `react-router-dom`, no `VITE_*`, no `@clerk/clerk-react`.
- TanStack Query for server state, in Client Components only. No `useEffect` fetching.
- **The 3-column Order Workshop layout is LOCKED.** Add to the existing failure-gate chain; do not restructure or replace the layout.
- **Plain-language copy.** No internal jargon — never "idempotency", "re-adopt", "dispatching row", "park".
- **Copy is pinned by the spec** (verbatim):
  - Send again → `If {supplier} already received this order, sending again may give them a duplicate.`
  - Mark as delivered → `If {supplier} never received this order, marking it delivered means it will not be sent.`
- **Backend dependency:** Task 5 of the backend plan defines `POST /api/orders/{id}/mark-delivered` → `202 { status: "delivered" }`. Tasks 1–4 below need no backend. Task 5 below does.
- Verify in the browser before claiming done — render at **390px** as well; a static read of the JSX is not verification.

---

### Task 1: The status exists in the type system and reads honestly

**Files:**
- Modify: `src/types/procurement.ts:59-70` (the `OrderStatus` union)
- Modify: `src/components/bridge/UnifiedStatusBadge.tsx:71-119` (`STATUS_META`)
- Modify: `src/components/bridge/InboxView.tsx:78-89` (`STATUS_PRESENTATION`) and `:232-239` (`mapStatus`)

**Interfaces:**
- Produces: `"delivery_unconfirmed"` as a member of `OrderStatus`. Every later task depends on it.

- [ ] **Step 1: Add the union member**

`src/types/procurement.ts` — add to the `OrderStatus` union:

```ts
  | "delivery_unconfirmed"
```

- [ ] **Step 2: Add the badge label**

`src/components/bridge/UnifiedStatusBadge.tsx` — add to `STATUS_META`:

```ts
  // The send happened; what we don't know is whether it arrived. "Delivery unknown" is the
  // honest label — never "failed" (we don't know that) and never "sent" (we don't know that either).
  delivery_unconfirmed: { label: "Delivery unknown", tone: "warning" },
```

**Note for the implementer:** match the exact `StatusMeta` shape used by the neighbouring entries (it may include `pulse` or other keys). Pick the `tone` value the file already uses for attention-needed states — read `delivery_failed`'s entry and choose the warning-ish tone that exists, do not invent a new tone.

- [ ] **Step 3: Map it in the Inbox's collapsed view**

`src/components/bridge/InboxView.tsx` — `mapStatus` currently falls through to `return "new"` for unmapped statuses, which would show a parked order at stage 0 of the pipeline rail ("New"). Add an explicit mapping so it lands in the same collapsed bucket the Inbox uses for attention-needed delivery states, and add the matching `STATUS_PRESENTATION` entry.

**Note for the implementer:** read `mapStatus` and `STATUS_PRESENTATION` (and the `CrossingStatus` type) before choosing the target bucket. `delivery_dead_letter` and `rejected_by_supplier` ALSO fall through to `"new"` today — that is a pre-existing bug. **Do not fix it here.** Note it in your report; it is a separate change with its own review.

- [ ] **Step 4: Verify**

Run: `bun run build`
Expected: compiles with no type errors.

Then render the Inbox with a parked order and confirm the badge reads "Delivery unknown" and the row is not shown as "New".

- [ ] **Step 5: Commit**

```bash
git add src/types/procurement.ts src/components/bridge/UnifiedStatusBadge.tsx src/components/bridge/InboxView.tsx
git commit -m "feat(orders): add delivery_unconfirmed status with an honest label

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Fix the two surfaces that actively mislead

**Files:**
- Modify: `src/components/bridge/ExceptionDetail.tsx:42-53` (`deliveryStatusCopy`)
- Modify: `src/components/bridge/review/hooks/useOrderReview.ts:22-42` (`finalDeliveryMessage`)

**Interfaces:**
- Consumes: the `OrderStatus` member (Task 1).

**Why this is its own task:** these two are not missing labels — they say things that are false. `deliveryStatusCopy` falls back to **"This order has not been sent yet."** for an unknown status. For a parked order that is exactly backwards: it WAS sent; that's the entire problem. An operator reading it would conclude nothing happened and re-send by hand. `finalDeliveryMessage` falls back to "Delivery is still processing…", implying it will resolve itself — it won't; it's waiting on them.

- [ ] **Step 1: Fix the exceptions-queue copy**

`src/components/bridge/ExceptionDetail.tsx` — add an explicit branch to `deliveryStatusCopy` before the fallback:

```ts
    case "delivery_unconfirmed":
      return "We sent this order but lost the connection before the supplier confirmed it, and this delivery channel can't tell us whether it arrived. Check with the supplier, then either send it again or mark it delivered.";
```

- [ ] **Step 2: Fix the review-hook message**

`src/components/bridge/review/hooks/useOrderReview.ts` — add an explicit branch to `finalDeliveryMessage`:

```ts
  if (status === "delivery_unconfirmed")
    return errorMessage
      ?? "We sent this order but never got confirmation it arrived. Check with the supplier, then send it again or mark it delivered.";
```

**Note for the implementer:** prefer the backend's `errorMessage` when present — Task 5B of the backend plan makes `GET /api/orders/{id}` return the pinned park sentence, and a single source of copy beats two that can drift. The literal above is the fallback for when it's absent. Match the function's existing signature/return style.

- [ ] **Step 3: Verify**

Render an order in `delivery_unconfirmed` in both the exceptions queue and the review screen. Confirm neither says "has not been sent yet" or "still processing".

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/ExceptionDetail.tsx src/components/bridge/review/hooks/useOrderReview.ts
git commit -m "fix(orders): parked orders no longer claim they were never sent

The unknown-status fallbacks said 'This order has not been sent yet' and
'Delivery is still processing' — both false for a parked order, which WAS sent
and is waiting on a human, not on the system.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: A parked order counts as needing attention

**Files:**
- Modify: `src/components/bridge/inboxSend.ts:16-24` (`REDELIVERABLE_STATUSES`)
- Modify: `src/components/bridge/inboxSend.test.ts:9-39` (asserts exactly two members — a guaranteed CI break)
- Modify: `src/components/bridge/BridgeDashboard.tsx:60-88` (`EXCEPTION_STATUSES`)
- Modify: `src/components/bridge/BridgeTopbar.tsx:271-291` (notification-bell classifier)
- Modify: `src/components/bridge/LaneDrawer.tsx:53-68` (`liveStatusDot`)

**Interfaces:**
- Consumes: the `OrderStatus` member (Task 1).
- Produces: `isRedeliverable("delivery_unconfirmed") === true`, which gates the Inbox bulk-select checkbox.

- [ ] **Step 1: Update the failing test first**

`src/components/bridge/inboxSend.test.ts` asserts `REDELIVERABLE_STATUSES` has *exactly two* members. It mirrors the backend's `OrderStatusMachine.RedeliverableFrom`, which backend Task 4 grows to three. Update the assertion to expect all three:

```ts
    // Mirrors backend OrderStatusMachine.RedeliverableFrom. A parked order is redeliverable:
    // the park exists so a HUMAN can choose to re-send, accepting the duplicate risk the
    // automatic retry must not take for them.
    expect([...REDELIVERABLE_STATUSES].sort()).toEqual(
      ["delivery_failed", "delivery_unconfirmed", "ready_to_deliver"],
    );
```

- [ ] **Step 2: Run it to watch it fail**

Run: `bun test src/components/bridge/inboxSend.test.ts`
Expected: FAIL — the set still has two members.

- [ ] **Step 3: Add the status to the redeliverable set**

`src/components/bridge/inboxSend.ts`:

```ts
// Mirrors backend OrderStatusMachine.RedeliverableFrom — keep the two in step.
const REDELIVERABLE_STATUSES = new Set<string>([
  "ready_to_deliver",
  "delivery_failed",
  "delivery_unconfirmed",
]);
```

- [ ] **Step 4: Run it to watch it pass**

Run: `bun test src/components/bridge/inboxSend.test.ts`
Expected: PASS.

- [ ] **Step 5: Add it to the attention surfaces**

- `BridgeDashboard.tsx` — add `"delivery_unconfirmed"` to `EXCEPTION_STATUSES` (it needs a human; that is the definition of the exception bucket). Do **not** add it to `FAILED_STATUSES` — we do not know that it failed, and the red "Failed" chip would be a claim we can't support.
- `BridgeTopbar.tsx` — give it a `kind` in the bell classifier so it isn't silently filtered out of notifications and the unread count.
- `LaneDrawer.tsx` — give `liveStatusDot` an explicit branch so it doesn't show the blue "in progress" dot; a parked order is not progressing.

**Note for the implementer:** read each file's existing `delivery_failed` handling and mirror its shape. For `FAILED_BUCKET` in `InboxView.tsx:279-285`, leave it alone for the same reason as `FAILED_STATUSES`, and say so in your report.

- [ ] **Step 6: Verify**

Run: `bun test && bun run build`
Expected: green. Then render the dashboard with a parked order and confirm it appears in "Needs attention" and in the notification bell.

- [ ] **Step 7: Commit**

```bash
git add src/components/bridge/inboxSend.ts src/components/bridge/inboxSend.test.ts src/components/bridge/BridgeDashboard.tsx src/components/bridge/BridgeTopbar.tsx src/components/bridge/LaneDrawer.tsx
git commit -m "feat(orders): a parked order counts as needing attention, not as failed

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: The Health page must not claim "All clear" while orders are parked

**Files:**
- Modify: `src/app/(app)/operations/health/page.tsx:37-46` (`TILES`) and `:157-167` (the `allClear` gate)
- Modify: `src/lib/api/operations.ts:22-45` (`OpsHealth` interface + mock rows)

**Interfaces:**
- Consumes: a backend `OpsHealth` field for the parked count.

**Why this is its own task:** `allClear` renders a green "All clear" banner from a fixed set of zero-checks. A parked order is invisible to all of them, so the Health page would tell an operator everything is fine while a PO sits unsent, waiting on them. That is a truthfulness bug, not a cosmetic one — and it is the surface most likely to be trusted at a glance.

**Blocked on:** backend plan **Task 8B**, which adds `OpsHealthSummary.DeliveryUnconfirmed` (an org-scoped count, included in `TotalProblemOrders`). This task cannot start until that has merged.

- [ ] **Step 1: Confirm the backend field is live before writing UI**

Hit the ops-health endpoint (or read `ProcuLink.Core/Services/IOpsHealthService.cs`) and confirm `DeliveryUnconfirmed` is present in the response.

**If it is missing: STOP and report.** Do not fake it, and do not derive it client-side from a list endpoint — a wrong "All clear" is worse than a missing tile.

- [ ] **Step 2: Add it to the interface and the tile**

`src/lib/api/operations.ts` — add `deliveryUnconfirmed: number;` to `OpsHealth` and to the mock rows.

`src/app/(app)/operations/health/page.tsx` — add a tile to `TILES` mirroring the dead-letter tile's shape, and add the zero-check to the `allClear` gate:

```tsx
  // "All clear" must mean all clear. A parked order is a PO sitting unsent, waiting on a
  // human — the one thing this banner must never hide.
  const allClear = h.deadLetter === 0 && /* …existing checks… */ && h.deliveryUnconfirmed === 0;
```

**Note for the implementer:** copy the exact existing check names from the file; the line above is illustrative, not literal.

- [ ] **Step 3: Verify**

Render `/operations/health` with a parked order present. Confirm the tile shows the count and the "All clear" banner does **not** appear.

- [ ] **Step 4: Commit**

```bash
git add src/app/\(app\)/operations/health/page.tsx src/lib/api/operations.ts
git commit -m "fix(health): 'All clear' no longer hides parked orders

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: The two operator actions

**Files:**
- Modify: `src/lib/api-client.ts` (~line 973-984 for the real fn, ~line 1329-1347 for registration)
- Modify: `src/components/bridge/workshop/OrderWorkshop.tsx:438-446` (failure-gate chain)
- Modify: `src/components/bridge/review/hooks/useSendFlow.ts:176-183` (poll predicate)
- Create: the parked-order panel component (mirror `src/components/bridge/FailedPanels.tsx`'s structure)

**Interfaces:**
- Consumes: backend `POST /api/orders/{id}/mark-delivered` → `202 { status: "delivered" }` (backend Task 5); `apiClient.redeliverOrder(id)` (exists, `api-client.ts:1347`).
- Produces: `apiClient.markDelivered(id)`.

**Why the workshop needs a panel:** `OrderWorkshop`'s failure-gate chain catches `failed` / `transform_failed` / `delivery_failed` and renders dedicated panels. `delivery_unconfirmed` matches none, so it falls through to the normal mapper/review screen with a plain Send button — the two actions this whole feature exists for would have **no home in the UI at all**.

- [ ] **Step 1: Add the API client method**

`src/lib/api-client.ts` — mirror `realRedeliverOrder` (~line 973-984) exactly:

```ts
async function realMarkDelivered(orderId: string): Promise<{ status: string }> {
  return request(`/api/orders/${orderId}/mark-delivered`, { method: "POST" });
}
```

Add the matching mock, and register `markDelivered` on the exported `apiClient` alongside `redeliverOrder`.

**Note for the implementer:** copy the real function's exact shape — auth header handling, error handling, and return typing all come from it. Do not hand-roll a `fetch`.

- [ ] **Step 2: Fix the send-flow poll predicate**

`src/components/bridge/review/hooks/useSendFlow.ts` — the terminal set the poll waits for is `delivered | delivery_failed | rejected_by_supplier | delivery_dead_letter`. Add `delivery_unconfirmed`:

```ts
  // A "Send again" from a parked order can itself crash and re-park. Without this the poll
  // burns its full 45s timeout and shows a false "Send failed" for an order that is simply
  // parked again.
```

- [ ] **Step 3: Add the parked panel to the gate chain**

In `OrderWorkshop.tsx`'s failure-gate chain, add a branch for `delivery_unconfirmed` rendering a panel with:

- the explanation (prefer the backend `errorMessage`; Task 2's fallback otherwise);
- **Send again** → `apiClient.redeliverOrder(id)`;
- **Mark as delivered** → `apiClient.markDelivered(id)`.

Both actions confirm first, with the spec-pinned copy stating the risk in the direction the operator is moving:

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

**Decide and state your choice in the PR:** two confirm patterns coexist in this codebase — the shared `useConfirm()` primitive (`src/components/ui/confirm.tsx`) and the workshop's bespoke `../review/ConfirmDialog` (`OrderWorkshop.tsx:35,780-793`). Use the workshop's own `ConfirmDialog` for visual consistency with the Send button beside it. Do **not** use a native `window.confirm` — that is banned.

After each action, invalidate both `["order", orderId]` and `["orders"]` (the pattern `FailedPanels.tsx:253-266` uses).

- [ ] **Step 4: Verify in the browser**

Drive the real flow: park an order (or seed one in `delivery_unconfirmed`), open the workshop, confirm the panel renders with both actions, click each, and confirm the status transitions and the list refreshes. Render at 390px too. Screenshot both actions.

- [ ] **Step 5: Commit**

```bash
git add src/lib/api-client.ts src/components/bridge/workshop/OrderWorkshop.tsx src/components/bridge/review/hooks/useSendFlow.ts src/components/bridge/
git commit -m "feat(orders): Send again / Mark as delivered for a parked order

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: Documentation (offer ⇔ works)

**Files:**
- Modify: `src/app/(marketing)/help/dashboard-and-statuses/page.mdx:24-32` (the failure-states table)
- Modify: `src/app/(marketing)/help/exceptions-and-stuck-orders/page.mdx:19,29-33` (retries/dead-letter section)
- Modify: `src/lib/api/connectors.ts:48-148` (`MOCK_MANIFESTS` — per-protocol entries)

- [ ] **Step 1: Add the status to the glossary**

`help/dashboard-and-statuses/page.mdx` — add a row between "Delivery failed" and "Dead-lettered". It is **not** a failure state; word it as its own thing. The adjacent "Delivered ≠ accepted" section (lines 36-38) is the natural cross-reference.

- [ ] **Step 2: Explain the choice**

`help/exceptions-and-stuck-orders/page.mdx` — a subsection covering: what happened (we sent it, then lost the connection before the supplier confirmed), why we don't just retry (on email and ERP connections a re-send can arrive twice, and we'd rather ask than guess), and the two actions with their consequences.

- [ ] **Step 3: Add the per-channel caveat**

`src/lib/api/connectors.ts` — add the at-least-once caveat to each `ConnectorManifest`'s `capabilities` (or a new optional field on `ConnectorManifest` in `src/lib/api/types.ts:565-580`). Be conservative and accurate per the spec's tiers:

- `sftp`, `ftps` — a repeat send overwrites the same file; no duplicate.
- `http` — we send an idempotency key; suppliers that honour it will ignore a repeat.
- `email`, `smtp`, `erp_erply`, `erp_directo` — we can't tell whether a repeat would arrive twice, so if a send's outcome is ever unknown we ask you instead of resending.

- [ ] **Step 4: Verify**

Run: `bun run build`
Expected: MDX compiles; both help pages render.

- [ ] **Step 5: Commit**

```bash
git add src/app/\(marketing\)/help/ src/lib/api/connectors.ts src/lib/api/types.ts
git commit -m "docs: explain unconfirmed deliveries and per-channel resend behaviour

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 7: Full verification

- [ ] **Step 1:** `bun test` — report real counts.
- [ ] **Step 2:** `bun run build` — no type errors, no new warnings.
- [ ] **Step 3:** Render the parked flow end-to-end against a real backend with the sibling plan merged: Inbox badge → dashboard "Needs attention" → workshop panel → both actions. Screenshot. Also at 390px.
- [ ] **Step 4:** Push, then `gh run list` — local green ≠ CI green.

---

## Findings from recon that are NOT this plan's job

Report these; do not fix them here.

1. **`delivery_held` does not exist in the frontend at all.** The backend can put an order in `delivery_held` (the A5 billing hold, shipped `df2292f`), but no `STATUS_META` entry, dashboard bucket, or copy exists for it — a billing-held order shows a humanized fallback label and is missing from the attention surfaces. Same class of gap this plan fixes, already live. Worth its own task.
2. **`mapStatus` drops `delivery_dead_letter` and `rejected_by_supplier` to `"new"`** (`InboxView.tsx:232-239`), showing terminal orders at stage 0 of the pipeline rail. Pre-existing.
3. **Two confirm patterns coexist** — the shared `useConfirm()` and the workshop's bespoke `ConfirmDialog`. Consolidation is a design-drift task, not this one.
4. **`InboxView.tsx` has a second label source** (`STATUS_PRESENTATION`) that its own comment (lines 65-69) flags as needing consolidation with `STATUS_META`.
