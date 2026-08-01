# DB-6 — The Failure and Recovery System

_Design spec produced 2026-07-30 from 02-DESIGN-BRIEFS.md. Feeds the packets named in the brief._

## Code actually read

- C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink/ProcuLink.Core/Constants/OrderStatusMachine.cs:35-109 — the full transition map. Key facts the UI must obey: transform_failed → {transforming, ready, pending_review, rejected_by_supplier} (so a re-transform IS legal and it holds no output); rejected_by_supplier and failed both → Set() i.e. TERMINAL (no resend is possible from either); delivery_dead_letter → {delivering, delivered, delivery_failed, ready, rejected_by_supplier}.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink/ProcuLink.Core/Constants/OrderStatusMachine.cs:146-147 — RedeliverableFrom = Set(DeliveryFailed, ReadyToDeliver, DeliveryUnconfirmed). delivery_dead_letter and delivery_held are NOT in it, so POST /redeliver 400s for both. Dead-letter's rescue is the separate ops requeue path.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink/ProcuLink.Core/Constants/OrderStatusMachine.cs:194-205 — ClaimableForAutomaticDispatchFrom and ClaimableForRetryFrom both = Set(ReadyToDeliver, DeliveryFailed), deliberately excluding delivery_unconfirmed: 'an automatic path never claims a park'. This is the backend half of the no-one-click-resend rule.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink/ProcuLink.Core/Constants/OrderStatusConstants.cs:83-90 — FailureBucket = {failed, transform_failed, delivery_failed, delivery_dead_letter, rejected_by_supplier}. delivery_unconfirmed and delivery_held are deliberately absent.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink/ProcuLink.Core/Constants/OrderStatusConstants.cs:50-76 — unrouted is REACHABLE in production today via four ingress channels; its doc warns a frontend author already shipped a status map that rendered it as brand-new 'New' at stage 0.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink/ProcuLink.Api/Services/Orders/OrderQueryService.cs:89-105 — ?status=failed expands SERVER-side to the whole FailureBucket; every other status is an exact match. So exact per-status deep links work, but 'failed' cannot mean the single parse-failed status without a new param.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink/ProcuLink.Api/Contracts/OpsHealthDto.cs:48-65 and ProcuLink.Api/Controllers/OpsController.cs:80 — the API returns PendingRouting; the frontend OpsHealth interface omits it, so /operations/health has no 'Needs supplier' tile.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/InboxView.tsx:710-731 — every filter is useState with no URL read; no useSearchParams anywhere in the file. This is why all nine /inbox?status= deep links are inert.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/InboxView.tsx:1649-1653 — failure rows get background '#FBE3E308', an 8-digit hex whose alpha is 0x08 (3%): imperceptible on white.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/InboxView.tsx:389-436 — FAILED_BUCKET mirror + FILTER_CHIPS. Five chips; the 'Failed' chip's summaryKeys sum the whole bucket while its api value is the single 'failed'.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/InboxView.tsx:897-903 — enableRowSelection gates on isRedeliverable(rawStatus), which admits delivery_unconfirmed — so parked orders ARE bulk-selectable and one confirm can re-send N of them.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/FailedPanels.tsx:374-391 — the transform_failed branch renders a primary 'Back to review' Link to /inbox/${order.id}: the page it is already on. Confirms the self-linking CTA.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/FailedPanels.tsx:224-227, 392-445 — isDeliveryConfigMissing regex and the config-missing variant (primary 'Set up delivery', Retry visible-but-disabled). Correct as shipped; reused in the spec.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/FailedPanels.tsx:302-310 — header band renders 13px/700 text in accentColor (#B36D14 for transform) on bgColor (#FAF1DD) = 3.65:1, below the 4.5:1 AA floor.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/workshop/OrderWorkshop.tsx:510-552 — the gate chain: failed, transform_failed, delivery_failed, delivery_held, delivery_unconfirmed. No arm for delivery_dead_letter or rejected_by_supplier, so both fall through to the mapper.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/workshop/OrderWorkshop.tsx:613-620 — the dead-letter header chip literally instructs: 'Open the order and click "Send again" to retry.'
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/workshop/OrderWorkshop.tsx:436-439, 674-702 — canSend depends only on crossed/sendState/blockingIssues/exceptionCount, never on order.status. That is why a dead-lettered or rejected order shows a live green Send.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/review/hooks/useSendFlow.ts:183 — the mapper's send calls apiClient.redeliverOrder(orderId), the endpoint that 400s for dead-letter and held.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/review/hooks/useRetryDelivery.ts:78-165 — the inFlight ref guard and its reasoning: a second retry enqueue can dead-letter the order in seconds because the claim accepts delivery_failed with no staleness gate. Every action in the spec inherits this.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/workshop/DeliveryUnconfirmedPanel.tsx:59-71, 190-236 — CONFIRM_COPY (kept verbatim in the spec) and the two one-click buttons the friction design wraps.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/workshop/BillingHeldPanel.tsx:39-123 — the held panel: amber, no Send, single 'Go to billing' link. Correct in substance; its header text is the same 3.65:1 pair.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/UnifiedStatusBadge.tsx:71-138 — STATUS_META. delivery_dead_letter is labelled 'Dead-lettered' (a banned word, live on inbox rows); warning tone correctly uses --amber-text on --amber-soft (5.62:1), which is exactly what the inline panels fail to do.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/operations/health/page.tsx:39-52 — TILES: four separate problem tiles all link to the same /inbox?status=failed; parsingStuck and slaBreached link to a bare /inbox.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/operations/health/page.tsx:174-193 — the Worker band and its copy ('New uploads may wait until processing restarts'), the only place workerHealthy is read in the whole frontend.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/operations/health/opsHealthState.ts:16-40 — isAllClear checks eleven counts but not workerHealthy, so a dead Worker with a clean queue renders the red band directly above a green 'All clear'.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/parseStall.ts:5-24 and MagicMappingPreview.tsx:405-420, 716-740 — the 90s stall escalation hedges ('processing MAY be paused') on a timer while the definite workerHealthy answer sits behind the same query key.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/review/hooks/useOrderReview.ts:29-89 — BILLING_HELD_MESSAGE / _PLURAL / DELIVERY_UNCONFIRMED_MESSAGE and finalDeliveryMessage: the existing shared copy the spec reuses rather than rewrites.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/ui/confirm.tsx:50-131 — useConfirm is a Radix AlertDialog: focus-trapped, Escape resolves false. The confirm primitive; no new dialog needed.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/workshop/OrderDetailsDrawer.tsx:45-48, 69-78 — tabs are 'Audit trail' / 'Standards check' / '{Supplier} response'; Esc closes and Tab is trapped. The rejection-reason surface the rejected_by_supplier panel links to.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/OrderPassport.tsx:438-445 — supplierResponse.rejectionReason exists and is rendered only inside the passport view, three clicks from the failure.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/lib/api-client.ts:1215-1241 — redeliverOrder and markDelivered both POST with no body, so the operator's §5 assertion cannot be persisted today.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/types/procurement.ts:87-120, 259 — the OrderStatus union and Order.errorMessage. Order carries no attempt count and no next-retry time; those live on the passport and on DeadLetterOrder.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/globals.css:13-87, 513-543 — the token set (--amber #B36D14, --amber-text #8A5310, --amber-soft #FAF1DD, --danger #B43838, --danger-soft #FAE6E6, --ink-faint #667085) and the .pill-* classes, which already use --amber-text correctly.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/StatusJourney.tsx:23-39 — FAILURE_JOURNEY_STAGE pins which pipeline node each failure sits on (failed:0, transform_failed:3, the three delivery failures:4).
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/workshop/WorkshopGateChrome.tsx:26-110 — poTitleFrom, InboxBackChip (44px hit area around a 30px chip) and WorkshopGateShell: reused verbatim by the gate mode.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/operations/health/DeliveryPausedCard.tsx:19-62 — links /inbox?status=delivery_held (inert) and is the one surface that already uses --amber-text correctly for its numerals and labels.
- C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/BridgeDashboard.tsx:94-131 — FAILED_STATUSES / EXCEPTION_STATUSES / EXCEPTION_ROW_STATUSES and the documented rule that a count must never link to a page that will be empty.

## Founder decisions this spec cannot make

- `?status=failed` is overloaded: server-side it expands to the whole failure bucket, so there is no way to deep-link the single parse-failed status. Do we add a backend escape (`?status=failed&exact=true`) or drop the 'Couldn't read the file' sub-chip and health tile link? A client-side filter over the returned page would under-count across pagination, so it is not an option. Backend change is ~5 lines in OrderQueryService.
- The §5 friction records the operator's factual assertion ('supplier confirmed they already have it' / 'nothing arrived'), but `POST /mark-delivered` and `POST /redeliver` take no body. Do we add an optional `{ reason }` to both so the order history says WHY it was resolved? Without it the audit trail shows a status flip with no human reasoning behind the most consequential decision in the product.
- A hand-marked `delivered` order is today indistinguishable from one the supplier actually confirmed. The spec requires the delivered header to read 'Marked delivered by you on {date}. Nothing was sent from ProcuLink.' — which needs either an audit action name we can match (does `MarkedDelivered` exist in the audit stream?) or a `deliveredBy: 'supplier' | 'operator'` field on Order. Which do you want?
- Removing `delivery_unconfirmed` from bulk-select (§5.7) means an operator with 40 parked orders after a Worker crash must resolve them one at a time. Is that acceptable, or do you want a bounded 'resolve all parked for one supplier' flow that still requires one assertion per supplier rather than per order?
- `slaBreached` has no server filter, so its tile links to `?sort=oldest` and admits it: 'Sorted oldest first — we can't filter by age yet.' Accept the honest degradation, or is an `?overdue=true` filter worth the backend work?
- Should the outage strip on the inbox be dismissible per session (as specced) or permanent while paused? Dismissible respects an operator who has already acknowledged it; permanent guarantees they cannot forget mid-shift and re-send a mid-flight order.
- `transform_failed` and `delivery_dead_letter` promote to the `us` tier after ONE failed self-serve attempt. Is one the right threshold, or should dead-letter (where the operator has already been told the supplier refused every automatic try) start at `us` like `failed` does?
- Retries are enabled while an org is at its plan order limit, on the reasoning that the shipped meter counts at creation so a retry consumes nothing new. If `Billing:CountDeliveredOnly` is ever turned ON, that reasoning inverts and a retry becomes billable. Confirm the flag stays OFF, or the plan-gated column in §9 needs a different rule.

---

# DB-6 — The Failure and Recovery System

Feeds WP-24 (order-screen recovery) and WP-36 (inbox + health deep links).

---

## 0. What the code actually says (the constraints this spec is built on)

Everything below is verified, not assumed. Numbers are computed.

### 0.1 Confirmed defects

| # | Defect | Evidence |
|---|---|---|
| D1 | **`transform_failed`'s only CTA links to itself.** `FailedPanel` with `stage="transform"` renders a primary button "Back to review" → `/inbox/${order.id}` — the page you are already on. A full-page gate replaces the workshop, so the button navigates nowhere and the operator has no route out except the back-chip. | `FailedPanels.tsx:374-391`, gate at `OrderWorkshop.tsx:517-523` |
| D2 | **`delivery_dead_letter` tells the operator to click a button that 400s.** The order falls through every gate to the normal mapper, where a red header chip reads *"Delivery didn't reach the supplier. Open the order and click 'Send again' to retry."* The mapper's only send control calls `apiClient.redeliverOrder`, and the backend's `RedeliverableFrom = {delivery_failed, ready_to_deliver, delivery_unconfirmed}` — dead-letter is **not** in it. The rescue path is `POST /api/ops/orders/{id}/requeue-delivery`, reachable only from `/operations/health`. | `OrderWorkshop.tsx:613-620`, `useSendFlow.ts:183`, `OrderStatusMachine.cs:146-147` |
| D3 | **Every `/inbox?status=…` deep link is inert.** `InboxView` has no `useSearchParams`; `statusFilter` initialises to `undefined` and `activeChip` to `0`. Nine health-page tiles, the review-backlog card and `DeliveryPausedCard` all link to filtered inbox URLs that land on an unfiltered inbox. | `InboxView.tsx:722-726` (state init, no param read), `health/page.tsx:39-52`, `DeliveryPausedCard.tsx:26` |
| D4 | **`rejected_by_supplier` has no panel at all.** No gate matches it; it renders the ordinary mapper with a green Send button. The rejection reason exists (`passport.supplierResponse.rejectionReason`) but is three clicks away behind Details → Supplier response. | `OrderWorkshop.tsx:510-552` (no `rejected_by_supplier` arm), `OrderPassport.tsx:445` |
| D5 | **AA text-contrast failure in all three amber panels.** `#B36D14` (`--amber`) on `#FAF1DD` (`--amber-soft`) = **3.65:1**. Required 4.5:1 for 13px/700 (not "large text": large = ≥18.66px bold). Present in `BillingHeldPanel` header, `DeliveryUnconfirmedPanel` header, `FailedPanel` transform header, `FailedPanel` "slow" note, and `InboxView`'s `AssignSupplierCell` chip. `--amber-text #8A5310` on the same background = **5.62:1** and is already what the `.pill-*` CSS layer uses. The inline-styled panels are the only offenders. | `BillingHeldPanel.tsx:82`, `DeliveryUnconfirmedPanel.tsx:162`, `FailedPanels.tsx:307`, `InboxView.tsx:662`, vs `globals.css:517-531` |
| D6 | **The health page has no "Needs supplier" tile.** The backend returns `PendingRouting` (`OpsHealthDto.cs:65`, `OpsController.cs:80`); the frontend `OpsHealth` interface omits the field. The one problem state whose fix is 100% self-serve is invisible on the operator's problem dashboard. | `src/lib/api/operations.ts:23-62` |
| D7 | **`isAllClear` ignores `workerHealthy`.** A dead Worker with zero problem orders renders the red "Order processing is paused" band immediately above a green "✓ All clear" banner. | `opsHealthState.ts:16-40` |
| D8 | **Failure row wash is invisible.** Problem rows get `background: "#FBE3E308"` — an 8-digit hex with alpha `0x08` = 3%. Imperceptible on white. Failure rows are visually identical to healthy rows apart from the pill. | `InboxView.tsx:1649-1653` |
| D9 | **The Worker-outage escalation is orphaned.** `workerHealthy` is read on exactly one screen. `parseStall.ts` hedges ("processing **may** be paused") on a 90-second timer while the definite answer is one shared query key away. Nothing on the inbox, the dashboard, the sidebar or the order screen mentions an outage. | `parseStall.ts:5-11`, `MagicMappingPreview.tsx:733`, `grep workerHealthy` → `health/page.tsx` only |

### 0.2 What is already right and must not be re-litigated

- `UnifiedStatusBadge.STATUS_META` is the single label/tone source of truth and its vocabulary is correct ("Delivery paused", "Delivery unknown", "Normalized" vs "Ready to send"). Reuse it; never re-invent labels.
- `useRetryDelivery` owns the hardest problem on this screen correctly: the 202 leaves the row in `delivery_failed` by design, so it polls the claim window and **refuses a second click** (`inFlight` ref) because a duplicate enqueue burns the retry budget and can dead-letter the order in seconds. Every action in this spec inherits that guard.
- `DeliveryUnconfirmedPanel`'s two-action model and its risk-stating confirm copy are correct in substance. §5 adds friction ahead of them; it does not replace them.
- `AssignSupplierBanner` is correctly a **banner over the live workshop**, not a gate, with the stated reason: "A full-screen panel would hide the very evidence the operator needs to answer the question the panel is asking." That sentence is the design law for §2.1.
- `WorkshopGateShell` / `InboxBackChip` / `poTitleFrom` already give every gate an identity row and a way back. Reuse verbatim.
- `useConfirm` is a Radix `AlertDialog` — focus-trapped, Escape-closing, resolves `false` on dismiss. It is the confirm primitive; do not hand-roll another.

### 0.3 Vocabulary enforcement

`delivery_dead_letter` must never surface as "dead letter" / "dead-lettered". `UnifiedStatusBadge` currently labels it **"Dead-lettered"** (`UnifiedStatusBadge.tsx:127`) — that is a banned word and it is on the inbox row today. Change to **"Out of retries"**, matching the health tile that already says it correctly. `unrouted` already surfaces as "Needs supplier" — keep. Nine permitted nouns only: Order, Supplier, Item code, Order layout, Output, Delivery, Rule, Issue, Workspace.

---

## 1. The system: one contract, three surfaces

Every problem state answers the same five questions, in the same order, in the same words, wherever it appears. That contract is a typed record, not prose — which is what makes the inbox line, the panel and the health tile agree by construction.

```ts
// src/components/bridge/problem/problemCopy.ts

export type ProblemStatus =
  | "failed"                 // couldn't read the file
  | "unrouted"               // no supplier known
  | "transform_failed"       // couldn't build the supplier's file
  | "delivery_failed"        // couldn't reach the supplier, retrying
  | "delivery_dead_letter"   // stopped retrying
  | "rejected_by_supplier"   // supplier refused it
  | "delivery_unconfirmed"   // sent, no confirmation
  | "delivery_held";         // paused by plan

export type Tier = "self" | "wait" | "us";

export interface ProblemCopy {
  /** Amber = needs a person, nothing broke. Red = something broke. */
  tone: "warning" | "danger";
  /** gate = replace the page (nothing underneath is worth reading).
      banner = sit above the live workshop. See §2.1 — only "failed" is a gate. */
  presentation: "gate" | "banner";
  /** 1 · WHAT HAPPENED — the panel headline and the inbox row's action line source. */
  headline: string;
  /** 2 · WHOSE IT IS — one clause. Never blames the operator. */
  attribution: (c: Ctx) => string;
  /** 3 · WHAT HAPPENS AUTOMATICALLY — null means "nothing does", and the panel
      renders that explicitly rather than leaving silence. */
  automatic: string | null;
  /** 4 · WHAT YOU CAN DO NOW */
  actions: (c: Ctx) => ProblemAction[];
  /** 5 · THE COST OF DOING NOTHING */
  consequence: (c: Ctx) => string;
  tier: Tier;
  /** ≤22 chars. The inbox row's second line. */
  rowAction: string;
}

interface Ctx {
  supplier: string;        // order.supplierName, or "this supplier" when null
  po: string;              // poTitleFrom(order.poNumber)
  supplierId: string | null;
  orderId: string;
  serverMessage: string | null;  // order.errorMessage, verbatim, never paraphrased
  readOnly: boolean;       // plan is read-only / Pilot ended
  atOrderLimit: boolean;
}
```

Three surfaces consume it:

| Surface | Consumes | Renders |
|---|---|---|
| Order screen `/inbox/[orderId]` | all five answers | `<OrderProblemPanel>` (§2) |
| Inbox `/inbox` | `tone` + `rowAction` | row edge bar + action line (§6) |
| `/operations/health` | `headline` + `tier` | tile label + escalation grouping (§7) |

**The invariant that kills the whole D1/D2 bug class:**

> While an order is in any of the eight problem states, the workshop header's send control is replaced by a disabled control whose accessible name is the state's `rowAction`, and **the panel owns the only action on the screen**. No screen may offer a control the backend's guard set will reject.

Implement it as one derived boolean in `OrderWorkshop`, not eight conditionals:

```ts
const problem = problemFor(order.status);       // ProblemCopy | null
const canSend = !problem && !crossed && sendState === "idle"
             && blockingIssues === 0 && exceptionCount === 0;
```

That one line is the fix for D2 and D4 simultaneously: `delivery_dead_letter` and `rejected_by_supplier` become known states, and their live-but-doomed Send button disappears.

---

## 2. `<OrderProblemPanel>` — the one component

```
src/components/bridge/problem/
  OrderProblemPanel.tsx     the component (gate + banner modes)
  problemCopy.ts            PROBLEM_COPY: Record<ProblemStatus, ProblemCopy>
  problemActions.ts         action → apiClient call + guard + confirm/friction spec
  useProblemAction.ts       shared pending/error/double-submit state (ports useRetryDelivery's inFlight ref)
```

Retire: `FailedPanels.tsx` (both exports), `BillingHeldPanel.tsx`, `DeliveryUnconfirmedPanel.tsx`, the header chip at `OrderWorkshop.tsx:613-620`. Keep `AssignSupplierBanner.tsx` as an **action slot** rendered inside the panel's action row (it holds the picker, the suggestion ranking and the 409 handling — do not reimplement).

### 2.1 Gate or banner

One rule, stated once:

> **Gate only when the page underneath is empty or would lie.** Banner in every other case.

| State | Mode | Why |
|---|---|---|
| `failed` | **gate** | Parsing produced nothing. There are no lines, no header, no output. A banner over blank columns is worse than a panel. |
| all seven others | **banner** | The extracted order, its item codes and (where it exists) the generated output are real and are the evidence the operator needs. Hiding them is D1's root cause. |

This changes shipped behaviour for `transform_failed`, `delivery_failed`, `delivery_held` and `delivery_unconfirmed`, which gate today. It is safe because of the §1 invariant: the workshop renders read-only-for-sending underneath, so there is no button to misfire.

### 2.2 Anatomy — 1440px

Banner mode, full content width of the work area, directly below the workshop header row, above the three columns. Inherits the locked `<XCard>` cross-section language: a 3px left edge strip, `--danger` or `--amber`.

```
┌─ 3px edge ────────────────────────────────────────────────────────────────────┐
│ ▌ ⚠  We couldn't build the file BoltWorks BV needs        [ Out of retries ]  │  ← 44px header band
│ ▌    tone-soft bg · icon 16px · headline 15/700 · UnifiedStatusBadge size=md   │
├───────────────────────────────────────────────────────────────────────────────┤
│                                                                               │
│  Your order is fine — the layout we build for BoltWorks BV isn't.             │  ← attribution · 13.5/500 --ink
│                                                                               │
│  ┌─ server detail ─────────────────────────────────────────────────────────┐  │
│  │ Template 'boltworks-csv-v2' — column 'unit_price' is not in this order. │  │  ← order.errorMessage VERBATIM
│  └─────────────────────────────────────────────────────────────────────────┘  │     12/400 mono --ink-muted on --surface-2
│                                                                               │
│  ○ We won't try again on our own.                                             │  ← automatic · 12.5 --ink-muted
│  ○ Until this is fixed, BoltWorks BV has not received this order — and every   │  ← consequence · 12.5 --ink-muted
│    future order for them will stop in the same place.                          │
│                                                                               │
│  [ Open the order layout → ]  [ Try building it again ]     Get help with this │  ← actions · 40px · gap 8
│                                                                order  (link)  │
└───────────────────────────────────────────────────────────────────────────────┘
```

Specifics:
- Panel max-width: none in banner mode (fills the work area, `max-width: 900px` on the text block so lines stay ≤80ch).
- Header band: `padding 12px 20px`, `min-height 44px`, background = `--amber-soft` | `--danger-soft`, `border-bottom: 1px solid var(--border)`.
- Icon: `AlertTriangle` (lucide) for `danger`; `PauseCircle` for `delivery_held`; `HelpCircle` for `delivery_unconfirmed`; `AlertCircle` for `unrouted`. All lucide, 16px, `strokeWidth 2` — same construction language as `UnifiedStatusBadge`. **No emoji.** The shipped `⚠` glyph at `OrderWorkshop.tsx:618` and `InboxView.tsx:1061,1181,1371,1692` violates the no-emoji-as-icon rule and must go.
- Server detail block renders **only** when `order.errorMessage` is non-empty. Verbatim, `font-mono 12px`, `--ink-muted`, `--surface-2` background, `border-radius 6px`, `padding 8px 12px`, `overflow-wrap: anywhere`, `max-height 4.5em` with a `Show all` disclosure past that. Never paraphrased — it is the only thing an engineer can act on.
- The two `○` lines are `automatic` then `consequence`, in that order, marked with a 4px `--border-strong` dot (decorative, `aria-hidden`). If `automatic` is `null`, the panel still prints the state's explicit "nothing happens on its own" sentence — silence reads as "it's being handled".
- Action row: primary (navy `--navy` bg, white text — **17.46:1**), secondary (transparent, `1px solid var(--border)`, `--ink`), then the tier affordance right-aligned as a text link. `min-height 40px` on desktop.
- Gate mode: identical card, but centred in `WorkshopGateShell`, `max-width 560px`, `justify-content: flex-start`, `padding-top 48px`. **Not** `min-height: 60vh` + `center` (the shipped panels do this and tall content gets pushed below the fold).

### 2.3 Anatomy — 390px

```
┌───────────────────────────────────────┐
│ ▌ ⚠ We couldn't build the file        │  headline wraps, 15/700
│ ▌   BoltWorks BV needs                │
│ ▌   [ Out of retries ]                │  badge drops to its own line
├───────────────────────────────────────┤
│ Your order is fine — the layout we    │
│ build for BoltWorks BV isn't.         │
│                                       │
│ ▸ What happens next                   │  ← disclosure, closed by default
│                                       │
│ ┌───────────────────────────────────┐ │
│ │  Open the order layout →          │ │  48px, full width
│ └───────────────────────────────────┘ │
│ ┌───────────────────────────────────┐ │
│ │  Try building it again            │ │  48px, full width
│ └───────────────────────────────────┘ │
│                                       │
│  Get help with this order             │  44px tap row, underlined
└───────────────────────────────────────┘
```

- Page padding 16px; panel is full-bleed inside it, `border-radius 10px`.
- Closed state is at most **headline + badge + attribution + actions** — never more than ~6 lines before the first button. The `automatic` / `consequence` / server-detail block collapse into one `<details>`-semantics disclosure labelled **"What happens next"**. Rationale: the coordinator on a phone is triaging, not diagnosing; the decision is the button, the diagnosis is one tap away.
- Actions stack, full width, `min-height 48px`, `gap 10px`, primary first.
- The tier link is a 44px-tall row, not a small inline link.
- Banner mode on mobile renders **above** `MobileTriage`, sticky-free (a sticky problem banner plus the existing sticky action bar eats the viewport).

### 2.4 Props

```tsx
<OrderProblemPanel
  order={order}                    // drives every Ctx field
  mode="banner" | "gate"           // defaulted from PROBLEM_COPY[status].presentation
  onResolved={() => void}          // optional; default = invalidate ["order",id] + ["orders"]
/>
```

No `title`/`message`/`tone` props. Passing copy in from the call site is how five panels drifted into three vocabularies. The status is the only input.

---

## 3. The eight states — exact copy

`{supplier}` = `order.supplierName` or `"this supplier"` when null. `{po}` = `poTitleFrom(order.poNumber)`. Server detail = `order.errorMessage`, verbatim, rendered separately (§2.2) and never interpolated into a sentence.

---

### 3.1 `failed` — we could not read this file

- **tone** `danger` · **presentation** `gate` · **tier** `us` (after one self-serve attempt)
- **badge** `Couldn't read the file` *(new `STATUS_META.failed` label; "Failed" alone says nothing)*
- **headline** `We couldn't read this file`
- **attribution** `This is about the file, not your setup — nothing here is misconfigured.`
- **automatic** `null` → renders: `Nothing more will happen to this order on its own.`
- **consequence** `{supplier} has not received this order, and won't until a file we can read replaces it.`
- **actions**
  1. primary link → `/upload?supplierId={supplierId}` — **`Upload this order again`**
  2. secondary link → `/formats` — **`See which file types we can read`**
  3. tier link — **`Get help with this order`**
- **helper under the actions** `Your original file is stored. If you send it to us we can tell you what we saw.`
- **extra** keep the existing source-format chip and the detect-confidence chip (`FailedPanels.tsx:127-155`) in the header band, right-aligned — they are the only clue the operator gets about *why*.

*Escalation note:* the tier is `us` from the start for this state alone, because a file the parser rejected is not something a purchasing coordinator can debug. The re-upload action is offered first because a wrong-file mistake is the most common cause; the help link sits beside it, not after a failed retry.

---

### 3.2 `unrouted` — we do not know which supplier this is for

- **tone** `warning` · **presentation** `banner` · **tier** `self`
- **badge** `Needs supplier` *(unchanged)*
- **headline** `We don't know which supplier this order is for`
- **attribution** `Nothing is wrong — we just need you to say who this is for before we can match item codes.`
- **automatic** `null` → renders: `This order waits here until you choose a supplier. We won't guess.`
- **consequence** `Nothing has been sent, and nothing will be, until this order has a supplier.`
- **actions**
  1. inline slot `assignSupplier` → the existing `<AssignSupplierBanner>` picker + ranked suggestions, rendered **inside** the panel's action row. Primary button label: **`Assign supplier`**
  2. secondary link → `/library/suppliers` — **`Add a new supplier`**
- **helper** `Once you choose, we read the file again and match the item codes automatically.`
- **on 409 (someone else assigned it first)** keep the existing `stale` outcome copy path; panel re-renders from the refetched status and unmounts.

---

### 3.3 `transform_failed` — we could not build the file this supplier needs

- **tone** `danger` · **presentation** `banner` · **tier** `self` → `us` after one failed retry
- **badge** `Output failed` *(replaces "Transform failed" — "transform" is engine vocabulary)*
- **headline** `We couldn't build the file {supplier} needs`
- **attribution** `Your order is fine — the layout we build for {supplier} isn't.`
- **automatic** `null` → renders: `We won't try to build it again on our own.`
- **consequence** `{supplier} has not received this order — and every future order for them will stop in the same place until this is fixed.`
- **actions**
  1. primary link → `/library/templates?supplierId={supplierId}` — **`Open the order layout`**
  2. secondary post `transformOrder` — **`Try building it again`** / pending **`Building…`**
  3. tier link — **`Get help with this order`**
- **helper** `If you haven't changed the layout, building it again won't help — send us the message above instead.`

*This is D1's fix.* The backend explicitly documents `transform_failed` as recoverable — `transforming` accepts `transform_failed` as an entry status (`OrderStatusMachine.cs:108`) and it holds **no output**, so nothing stale can ship. So "Try building it again" is a legitimate, guard-satisfying action. The primary is the layout, because the failure is terminal for the same inputs (`OrderStatusMachine.cs:45-46`: "neither is fixable by retrying the same inputs").

The retry uses `useProblemAction` with the `inFlight` ref and a claim-window poll ported from `useRetryDelivery`: POST, then poll `["order", id]` for up to 30s, then fall back to the queued copy `Queued — waiting for it to start. This page updates on its own; you don't need to click again.` Second click is blocked, not re-fired.

---

### 3.4 `delivery_failed` — we could not reach the supplier, we are retrying

- **tone** `danger` · **presentation** `banner` · **tier** `wait` (or `self` when config is missing)
- **badge** `Delivery failed` *(unchanged)*
- **headline** `We couldn't reach {supplier}`
- **attribution** `{supplier}'s system didn't accept the connection. There's nothing wrong with the order itself.`
- **automatic** `We're trying again automatically. Each try waits a little longer than the last.`
  - **Hard rule:** never render "attempt N of M" or a countdown. `Order` carries only `errorMessage` — no attempt count, no next-run time (`types/procurement.ts:259`). Attempt counts exist on the passport response and on `DeadLetterOrder`. If a future panel fetches them, render `Try {n} of {m}` only when **both** numbers came from the server. An invented number here is the difference between an operator waiting and an operator escalating.
- **consequence** `If the automatic tries run out, this order stops and waits for you.`
- **actions**
  1. primary post `retryDelivery` — **`Try sending now`** / pending **`Queued…`**
  2. secondary link → `/operations/log?orderId={orderId}` — **`See every attempt`**
- **helper** `You don't have to do anything — this is only if you want it to go sooner.`

**Variant — delivery not set up** (`isDeliveryConfigMissing(order.errorMessage)`, keep the existing regex at `FailedPanels.tsx:224-227`): tier flips to `self`.
- **headline** `{supplier} has no delivery set up yet`
- **attribution** `We built the file. We just don't know where to send it.`
- **automatic** `null` → `Trying again won't help until there's somewhere to send it.`
- **consequence** `This order — and every other order for {supplier} — stays here until delivery is set up. It takes about a minute.`
- **actions**
  1. primary link → `/library/suppliers/{supplierId}?tab=delivery` — **`Set up delivery`**
  2. `Try sending now` present but **disabled**, helper `This will work once delivery is set up.`
  (Keeps the shipped behaviour at `FailedPanels.tsx:392-445`, which is correct — visible so the path is discoverable, disabled so it can't fire a guaranteed failure.)

---

### 3.5 `delivery_dead_letter` — we stopped retrying, you need to look

- **tone** `danger` · **presentation** `banner` · **tier** `self` → `us` after one requeue fails
- **badge** `Out of retries` — **change `STATUS_META.delivery_dead_letter` from "Dead-lettered"** (banned word, currently on every inbox row)
- **headline** `We stopped trying to reach {supplier}`
- **attribution** `We tried several times over a longer and longer wait. Every try was refused, so we stopped rather than keep hammering their system.`
- **automatic** `null` → renders: `We won't try again by ourselves. This order needs you now.`
- **consequence** `{supplier} does not have this order. It will sit here until someone sends it — nothing else is queued.`
- **actions**
  1. primary post `requeueDelivery` (`POST /api/ops/orders/{id}/requeue-delivery`) — **`Start sending again`** / pending **`Queued…`**
  2. secondary link → `/library/suppliers/{supplierId}?tab=delivery` — **`Check {supplier}'s delivery settings`**
  3. tier link — **`Get help with this order`**
- **helper** `If nothing has changed at {supplier}'s end, this will probably fail the same way — check their settings first.`

*This is D2's fix.* Two changes: the correct endpoint (`requeueDelivery`, not `redeliverOrder`), and the settings check promoted ahead of a blind retry — the exact reasoning the health page already applies to supplier rejections (`health/page.tsx:86-95`). The mapper's Send button is gone by the §1 invariant, so the old "click Send again" instruction has nothing left to mislead about.

---

### 3.6 `rejected_by_supplier` — the supplier refused it, and why

- **tone** `danger` · **presentation** `banner` · **tier** `self`
- **badge** `Supplier refused it` *(replaces the bare "Rejected", which reads as "we rejected it")*
- **headline** `{supplier} refused this order`
- **attribution** `They received it and turned it down. Their reason is below.`
- **automatic** `null` → renders: `Nothing is queued. Sending the same file again would be refused the same way.`
- **consequence** `{supplier} is not fulfilling this order. If you need it, fix what they flagged and send a corrected order.`
- **actions**
  1. primary link → `/inbox/{orderId}?details=response` — **`See {supplier}'s reply`** (opens the existing Details drawer on its "Supplier response" tab — `OrderDetailsDrawer.tsx:48`)
  2. secondary link → `/inbox/{orderId}?fix=1` — **`Fix the order and send again`** — clears the panel's send lock for this session and re-arms the workshop Send. Per `OrderStatusMachine.cs:99`, `rejected_by_supplier` has **no outgoing transitions**: it is terminal. So this action must **not** promise a re-send of *this* order. Corrected label and behaviour: **`Start a corrected order`** → `/upload?supplierId={supplierId}&from={orderId}`, with helper `We'll keep this one as the record of what they refused.`
- **server detail** render `passport.supplierResponse.rejectionReason` in the detail block when present, falling back to `order.errorMessage`. Requires the panel to fetch `["passport", orderId]` — one extra request on one rare status, gated on `status === "rejected_by_supplier"` (the same pattern as the audit fetch at `OrderWorkshop.tsx:145`).

*This is D4's fix and the correction of a wrong assumption:* a "re-send this order" action for a terminal status would have been the third guaranteed-400 button on this screen.

---

### 3.7 `delivery_unconfirmed` — we sent it but got no confirmation

- **tone** `warning` · **presentation** `banner` · **tier** `self`
- **badge** `Delivery unknown` *(unchanged — correct)*
- **headline** `We may have sent this — we can't tell`
- **attribution** `Neither side is at fault. The connection dropped between sending and {supplier} confirming, and this channel can't tell us after the fact whether it arrived.`
- **automatic** `We will not send this again by ourselves. That is deliberate — an automatic re-send could give {supplier} the same order twice.`
- **consequence** `Left alone, this stays unresolved: {supplier} may be working on it, or may never have seen it.`
- **actions** — see §5. **There is no button here that sends anything in one click.**
- **helper** `The safe first step is a phone call.`

Prefer `order.errorMessage` for the body claim when present (the backend pins a park sentence), falling back to the shared `DELIVERY_UNCONFIRMED_MESSAGE`. Keep that fallback wiring — three surfaces share it and it must not drift.

---

### 3.8 `delivery_held` — paused because of your plan

- **tone** `warning` · **presentation** `banner` · **tier** `self`
- **badge** `Delivery paused` *(unchanged)*
- **headline** `Sending is paused on your plan`
- **attribution** `This is about your plan, not the order. Nothing failed and nothing is lost.`
- **automatic** `Sending starts again by itself as soon as your billing is up to date. You don't need to come back here.`
- **consequence** `Until then {supplier} hasn't received this order. Everything we built for it is waiting exactly as it is.`
- **actions**
  1. primary link → `/settings?tab=billing` — **`Go to billing`**
  2. no secondary. `redeliver` answers 400 from `delivery_held` (`RedeliverableFrom` excludes it) and the release is automatic — offering anything else would be theatre.
- **helper** `You don't need to upload {po} again or redo any item codes.`

Reuse `BILLING_HELD_MESSAGE` for the attribution+automatic pair so the workshop, the health card and this panel stay identical (`useOrderReview.ts:29-41` already maintains the singular/plural pair for exactly this reason).

---

## 4. The escalation ladder

Three tiers. Each has exactly one footer affordance, so an operator learns the shape once.

| Tier | Means | Footer affordance | States |
|---|---|---|---|
| **`self`** — *you can fix this* | The fix is a setting, a supplier, a layout or an invoice — all inside the product. | Nothing extra. The primary action *is* the fix. | `unrouted`, `delivery_held`, `rejected_by_supplier`, `delivery_unconfirmed`, `transform_failed` (first attempt), `delivery_dead_letter` (first attempt), `delivery_failed` config-missing variant |
| **`wait`** — *we're on it* | Something automatic is already running. Acting is optional. | A single line, no control: `You don't have to do anything — this is only if you want it to go sooner.` | `delivery_failed` (transient) |
| **`us`** — *this needs us* | No setting the operator owns can change the outcome. | Text link **`Get help with this order`** → `/support?order={po}&problem={status}` | `failed`, plus any `self` state after **one** failed self-serve attempt |

**Promotion rule (the part that matters).** A `self` state promotes itself to `us` after the operator's own fix attempt fails once. Implementation: `useProblemAction` counts consecutive failures of the state's primary post action in `sessionStorage` under `problemAttempts:{orderId}:{status}`. On `>= 1`, the panel appends the `us` affordance **and** rewrites the helper to:

> `That didn't work. This one probably needs us — send it over and we'll look at the detail above.`

Never demote in-session, never hide the explanation, never replace the actions. The escalation *adds* a route; it does not take one away.

**What `/support?order=…&problem=…` must do:** prefill the existing `<ContactForm>` subject with `Order {po} — {headline}` and the body with the state's headline plus `order.errorMessage`. The operator should never have to retype what the screen already told them. `support@proculink.eu` remains the fallback for a form failure. No `mailto:` as the primary affordance — a coordinator on a managed desktop often has no mail client bound.

---

## 5. `delivery_unconfirmed` — the deliberate-friction interaction

**Hard constraint:** an order in `delivery_unconfirmed` must never offer a one-click re-send. Today it does, twice: `DeliveryUnconfirmedPanel`'s `Send again` button (one click → confirm → POST) and, if the panel is ever bypassed, the inbox bulk bar (`delivery_unconfirmed` is in `isRedeliverable`, so it is selectable — `InboxView.tsx:902`).

The friction is **an explicit statement of fact**, not a speed bump. The operator must say what the supplier told them before either resolution unlocks — because both resolutions are irreversible in opposite directions, and only the supplier knows which is right.

### 5.1 The interaction — 1440px

The panel's action area is a three-step strip, not a button row.

```
Step 1 ─ what we know
┌─────────────────────────────────────────────────────────────────────────────┐
│  Ask BoltWorks BV whether they have this order:                             │
│    PO REDACTED-PHONE  ·  ordered 14 Jul 2026  ·  9 lines  ·  € 12,408.00        │  ← mono, selectable
│    Sent from ProcuLink at 09:14, 27 Jul 2026                                │  ← from the last attempt time
│                                                              [ Copy details ]│
└─────────────────────────────────────────────────────────────────────────────┘

Step 2 ─ what they said                                       (radiogroup, required)
   ( ) They don't have it — nothing arrived
   ( ) They have it — it's already in their system
   ( ) I haven't been able to reach them yet

Step 3 ─ the one action that answer allows
   ┌──────────────────────────────┐
   │  Send it to them again       │   ← appears only after answer 1
   └──────────────────────────────┘
     BoltWorks BV will receive this order for the first time.
```

Behaviour:

1. **Step 3 renders nothing until Step 2 is answered.** Not disabled — absent. A disabled button is an invitation; an absent one is a question.
2. Each answer reveals exactly **one** action, and the reveal carries a consequence line in `--ink-muted` 12.5px:

| Answer | Action revealed | Consequence line |
|---|---|---|
| `They don't have it — nothing arrived` | **`Send it to them again`** (navy primary) → `apiClient.redeliverOrder` | `{supplier} will receive this order for the first time.` |
| `They have it — it's already in their system` | **`Mark it as delivered`** (secondary, `--border`) → `apiClient.markDelivered` | `We'll record it as delivered and stop asking. Nothing will be sent.` |
| `I haven't been able to reach them yet` | *no action* — instead: `Leave it as it is` (ghost, closes the disclosure) and the `us` tier link | `This order stays here. We won't send anything until you know.` |

3. **Second gate — the existing confirm dialog.** Clicking the revealed action opens `useConfirm` with the risk stated in the direction the operator is moving (keep the shipped `CONFIRM_COPY` verbatim; it is correct):
   - *Send again*: title `Send this order again?` · description `If the supplier already received this order, sending again may give them a duplicate.` · confirm `Send again`
   - *Mark delivered*: title `Mark this order as delivered?` · description `If the supplier never received this order, marking it delivered means it will not be sent.` · confirm `Mark delivered` · `danger: true`
4. **Two gates, no third.** Type-to-confirm was considered and rejected: at 390px on a phone, typing a PO number to escape a stuck order trains people to copy-paste past the gate. The load-bearing friction is the factual assertion in Step 2 — it is the one thing that cannot be satisfied without contacting the supplier.
5. **The assertion is recorded.** Send the operator's answer as the reason so the history says *why*, not just *what*: `POST /api/orders/{id}/mark-delivered { reason: "Operator confirmed with supplier that the order was already received." }` and the redeliver equivalent. **The endpoints take no body today** (`api-client.ts:1232-1241`) — see Open Questions. Until the backend accepts it, the FE must not silently drop the assertion: render it in the panel's post-action state as `You recorded: "{answer}"` and keep the panel's `sessionStorage` note keyed to the order so a reload doesn't lose it.
6. **The step strip resets** if the answer changes — switching from answer 1 to answer 2 removes the send action before revealing mark-delivered. Never both visible.
7. **The inbox must not be a way around this.** Remove `delivery_unconfirmed` from `isRedeliverable` so parked rows are **not** bulk-selectable, and change the disabled-checkbox tooltip to `Delivery unknown — open the order to resolve it safely`. Today the bulk path routes N parked orders through one boolean confirm (`InboxView.tsx:829-880`); §5 exists precisely so that decision is per-order and evidence-based. The bulk-bar amber warning line (`InboxView.tsx:1160-1166`) then becomes unreachable and is deleted with it.

### 5.2 The interaction — 390px

Same three steps, stacked, inside the `What happens next` disclosure's sibling — **not** inside it. The resolution strip is always visible; only the diagnosis collapses.

- Step 1 becomes a two-line block: `PO REDACTED-PHONE · € 12,408.00` / `Sent 27 Jul, 09:14`, plus a 48px **`Copy details`** button (a phone call is the action; getting the numbers onto the clipboard is the enabler).
- Step 2 radios: 48px rows, 20px controls, full-width tap targets, `--surface` on `--surface-2` when selected.
- Step 3 action: 48px, full width.
- The revealed consequence line sits directly above its button so it cannot be scrolled past.

### 5.3 Success handoff

`markDelivered` moves the order to `delivered`, the panel unmounts, and the workshop's delivered state takes over — where today a hand-marked order is **indistinguishable** from one the supplier actually confirmed. That is a new honesty gap created by this feature, so it is in scope. The delivered header must read:

> `Marked delivered by you on 27 Jul 2026. Nothing was sent from ProcuLink.`

Source: the order's history event for the mark action. See Open Questions for the event name.

---

## 6. The inbox

### 6.1 The URL is the filter — the fix for D3

`InboxView` reads its filter state from the URL and writes back to it. Not "also reads" — *derives from*. Two sources of truth is how these links died.

```
/inbox?status=<OrderStatus | "failed">   selects the matching chip + sets the server filter
/inbox?sort=oldest                        sorting = [{ id: "ageMin", desc: true }]
/inbox?q=<text>                           search input + committed search term
```

Contract:

1. `const params = useSearchParams();` in `InboxView`. **`src/app/(app)/inbox/page.tsx` must wrap `<InboxView/>` in `<Suspense>`** with the existing skeleton as the fallback — `useSearchParams` in a client component needs a boundary or the route build fails.
2. `statusFilter` and `activeChip` become **derived**, not `useState`:
   ```ts
   const urlStatus = params.get("status") as OrderStatus | "failed" | null;
   const activeChip = CHIPS.findIndex(c => c.api === urlStatus);   // -1 → 0 (All)
   ```
3. `handleChip(i)` calls `router.replace(withParams({ status: chip.api, page: null }), { scroll: false })`. Back/forward then work, a filtered view is shareable, and the health tiles land where they claim.
4. `page` also moves to the URL (`?page=2`) so a filtered page is linkable, and resets to 1 on any filter change.
5. **Unknown `status` value** → do **not** fall through to "All orders" silently (that reads as "no problems"). Show `All orders` active plus a one-line notice above the chips:
   > `We don't recognise that filter, so this is every order.` + **`Clear`**
   `--amber-soft` background, `--amber-text` (**5.62:1**), `role="status"`.
6. Mock mode keeps its client-side column filter, driven off the same derived `activeChip` — one code path decides which chip is active in both modes.

### 6.2 Chips

Primary row, always present:

`All orders` · `Needs review` · `Ready to send` · `Delivered` · `Problems`

Conditional chips, appended **only when their count > 0**, and once shown they stay mounted for the session (a chip must never vanish under the cursor; it shows `0` and still filters):

`Needs supplier` (`unrouted`) · `Delivery unknown` (`delivery_unconfirmed`) · `Paused` (`delivery_held`)

Rationale: a healthy account sees 5 chips at 390px; a troubled one gets the three states whose fixes are entirely different from a failure's. No chip ever shows a permanent 0.

**`Problems` replaces `Failed`.** Server filter stays `?status=failed` (the backend expands it to the whole `FailureBucket` — `OrderQueryService.cs:96-100`), so the count and the rows still agree exactly. The label changes because "Failed" over a set that includes a supplier's business rejection is wrong.

**Sub-chips**, rendered on a second row **only while `Problems` is active** — this is what the health tiles need and what makes the five collapsed statuses reachable:

`All problems` (`failed`) · `Couldn't read` (`failed`†) · `Output failed` (`transform_failed`) · `Delivery failed` (`delivery_failed`) · `Out of retries` (`delivery_dead_letter`) · `Supplier refused` (`rejected_by_supplier`)

† collision: `?status=failed` means both "the parse-failed status" and "the whole bucket" server-side. Resolve with an explicit second param — `?status=failed&only=parse` — handled client-side over the returned page, **or** ask the backend for an exact-match escape (`?status=failed&exact=true`). Prefer the backend change; the client-side filter would under-count across pages. Flagged in Open Questions.

Sub-chip row: horizontal scroll (`no-scrollbar`, `flex-nowrap`), 28px chips at 1440px, **44px** at 390px. Each carries its `byStatus` count.

### 6.3 Row treatment

Replace the invisible 3%-alpha wash (D8) with two changes that work identically on desktop and mobile and add **no columns**:

**(a) A 3px left edge bar** on the row's first cell — the locked `<XCard>` cross-section language applied to a row. `--danger` for the failure bucket, `--amber` for `unrouted` / `delivery_held` / `delivery_unconfirmed`. `aria-hidden`; the status pill carries the meaning. Non-text contrast: `--danger` on `--surface` = **5.89:1**, `--amber` on `--surface` = **4.11:1** — both clear the 3:1 non-text floor.

**(b) The Order cell's second line becomes the action line.** Today it always reads `{n} lines · {m} to review`. For a problem row it renders `PROBLEM_COPY[status].rowAction` in `--danger` / `--amber-text`, weight 600 — the one line that says what to do:

| Status | Pill (existing) | Row action line |
|---|---|---|
| `failed` | Couldn't read the file | `Upload it again` |
| `unrouted` | Needs supplier | `Assign a supplier` |
| `transform_failed` | Output failed | `Check the layout` |
| `delivery_failed` | Delivery failed | `Retrying automatically` |
| `delivery_failed` (config missing) | Delivery failed | `Set up delivery` |
| `delivery_dead_letter` | Out of retries | `Needs you to send it` |
| `rejected_by_supplier` | Supplier refused it | `See their reply` |
| `delivery_unconfirmed` | Delivery unknown | `Check with supplier` |
| `delivery_held` | Delivery paused | `Waiting on billing` |

Healthy rows keep the count line exactly as shipped.

`--amber-text` on `--surface` = **6.31:1**; `--danger` on `--surface` = **5.89:1**. Both AA at 11px.

Keep the leading `tv2DotColor` dot and the `UnifiedStatusBadge` — three signals (dot, pill, action) all derived from one status, so they cannot disagree.

**Delete the emoji.** `⚠` at `InboxView.tsx:1061,1181,1692`, `⊘` at `1371,1692`, `🔍` at `1253`, `↻`/`↑`/`⇅`/`›`/`▦` throughout — replace with lucide (`AlertTriangle`, `SearchX`, `Search`, `RefreshCw`, `Upload`, `ChevronsUpDown`, `ChevronRight`, `Columns3`), 13–14px, `strokeWidth 2`.

### 6.4 Row selection

- `isRedeliverable` loses `delivery_unconfirmed` (§5.7). Selectable set becomes `{ready_to_deliver, delivery_failed}` — exactly `ClaimableForRetryFrom`.
- Select-all tooltip: `Selects orders that are ready to send or had a delivery failure.`
- Non-selectable tooltip: `Only orders that are ready to send or had a failed delivery can be sent from here. Open the others to see what they need.`

---

## 7. `/operations/health` — where each link goes

Every tile is a promise. Today nine of them break it (D3), and four point at a filter three times wider than their label.

| Tile label (copy) | Field | Links to | Tier |
|---|---|---|---|
| `Stuck reading the file` | `parsingStuck` | `/inbox?status=parsing` | wait |
| `Stuck sending` *(was "Stuck delivering")* | `deliveringStuck` | `/inbox?status=delivering` | wait |
| **`Needs supplier`** *(new — D6)* | `pendingRouting` | `/inbox?status=unrouted` | self |
| `Couldn't read the file` *(was "Transform failed" grouping)* | `failed` | `/inbox?status=failed&only=parse` | us |
| `Output failed` *(was "Transform failed")* | `transformFailed` | `/inbox?status=transform_failed` | self |
| `Delivery failed` | `deliveryFailed` | `/inbox?status=delivery_failed` | wait |
| `Out of retries` | `deliveryDeadLetter` | `/inbox?status=delivery_dead_letter` | self |
| `Supplier refused it` *(was "Rejected by supplier")* | `rejectedBySupplier` | `/inbox?status=rejected_by_supplier` | self |
| `Delivery unknown` | `deliveryUnconfirmed` | `/inbox?status=delivery_unconfirmed` | self |
| `Overdue` | `slaBreached` | `/inbox?sort=oldest` | — |
| `Open issues` *(was "Open exceptions")* | `openExceptions` | `/operations/exceptions` | — |

Notes:

- **`slaBreached` has no server filter.** Rather than link to an unfiltered inbox and call it a filter, it links to `?sort=oldest`, which `InboxView` implements as `[{ id: "ageMin", desc: true }]` — a real, honest target. (Default sort today is `desc: false` = newest first.) Add a helper under the tile: `Sorted oldest first — we can't filter by age yet.`
- **Add `pendingRouting` to the `OpsHealth` interface** as `pendingRouting?: number` (optional, `?? 0`, same forward-compat pattern as `deliveryUnconfirmed`).
- **Tone**: `tone()` currently reds `transformFailed / deliveryFailed / rejectedBySupplier / deliveryDeadLetter / failed`. Add nothing; `pendingRouting` and `deliveryUnconfirmed` stay amber — they are backlogs, not faults. But the tile dot must use `--amber` (3:1 non-text, **3.83:1** on `--bg` ✓) and the *label* `--ink-muted` (**5.31:1**) — never `--amber` as label text (**3.83:1**, fails).
- **Group the tiles under the escalation ladder.** Three labelled groups replace the flat `auto-fill` grid:
  - **`You can fix these`** — pendingRouting, transformFailed, deliveryDeadLetter, rejectedBySupplier, deliveryUnconfirmed
  - **`We're working on these`** — parsingStuck, deliveringStuck, deliveryFailed
  - **`Everything else`** — failed, slaBreached, openExceptions
  Group headers: 10.5px/700 uppercase, `0.07em` tracking, `--ink-faint` (**4.97:1** on surface). A group renders only when at least one tile in it is non-zero. This is what turns nine numbers into a work order.
- **`Include delivery-failed` checkbox** leaks a raw status name. Relabel: **`Also show orders we're still retrying`**.
- **`Try sending again`** button on dead-letter rows: keep, but align with §3.5 — the row's secondary becomes a link to the supplier's delivery tab. Label the primary **`Start sending again`** so the panel and the table match word for word.
- **Fix D7**: `isAllClear` gains `&& h.workerHealthy`. When `workerHealthy === false` and everything else is clear, replace the green banner with an amber one:
  > `No orders are in a problem state — but order processing is paused, so new work is waiting.`

---

## 8. The Worker outage

One Hangfire Worker. When it is down nothing parses, transforms or delivers, and the only screen that knows is `/operations/health`. The copy there is genuinely good — *"New uploads may wait until processing restarts."* — and every surface below inherits its tone: **paused, not broken; waiting, not lost.**

### 8.1 One shared source

```ts
// src/hooks/useProcessingStatus.ts
export function useProcessingStatus() {
  const q = useQuery({ queryKey: ["ops-health"], queryFn: getOpsHealth,
                       refetchInterval: 45_000, staleTime: 30_000, retry: 1,
                       enabled: useQueriesEnabled() });
  return {
    paused: q.data ? q.data.workerHealthy === false : false,  // unknown ≠ paused
    lastSeen: q.data?.secondsSinceWorkerHeartbeat ?? null,
    waiting: 0, // filled by the caller from ["orders","summary"].byStatus
  };
}
```

Same query key as the health page → one cache entry, one 45s poll, no extra network. **`paused` is false while unknown** — an unreachable API must never manufacture an outage banner.

### 8.2 Order screen

An amber strip below the workshop header, above the columns (and above `MobileTriage` at 390px). Copy is status-aware, because "what should I do" differs completely:

| Order status | Copy |
|---|---|
| `pending_parse`, `parsing` | `Order processing is paused, so we haven't read this file yet. It stays in the queue and starts on its own when processing restarts.` |
| `pending_review`, `ready`, `unrouted` | `Order processing is paused. You can keep reviewing this order — it will be sent once processing restarts.` |
| `ready_to_deliver` | `Order processing is paused. You can queue this order now; it goes out as soon as processing restarts.` |
| `transforming`, `delivering` | `This order is part-way through and order processing is paused. It picks up where it left off — **don't send it again.**` |
| any problem state | *not shown* — the problem panel owns the screen. Instead the panel's `automatic` line is suffixed: ` Order processing is paused right now, so this will start once it restarts.` |

Every variant ends with `Check system health →` → `/operations/health`.

The last row is the one that earns its place: it is the only thing standing between a paused mid-flight order and a duplicate PO.

**Also fix the parse-stall hedge (D9).** `MagicMappingPreview`'s 90-second note guesses. With `paused` in hand it can know:
- `paused === true` → `Order processing is paused, so this hasn't started yet. Check system health →` (definite)
- `paused === false` and 90s elapsed → keep the shipped hedged copy verbatim (the Worker may be catching up)

### 8.3 Inbox

A dismissible strip above the filter chips (`sessionStorage`, per-session — it must come back tomorrow):

> **`Order processing is paused.`** `New uploads and sends will wait until it restarts. Nothing is lost.` · `Last active 4m ago` · `Check system health →`

- `--amber-soft` bg, `--amber-text` text (**5.62:1**), `1px solid #F0D39A`, `role="status"`, `aria-live="polite"`. 44px dismiss button with `aria-label="Hide this notice for now"`.
- **`Upload order` stays enabled.** The file is stored either way; blocking upload loses work. Add a helper beneath the button (desktop) / under the strip (mobile): `Your file will be stored now and read as soon as processing restarts.`
- **`Send selected` stays enabled.** The job is enqueued and runs on recovery — disabling it would be a lie about durability. What changes is the result copy: `Queued. These will go out when processing restarts.` (`formatBulkSendResult` gains a `paused` branch.)
- Rows in `parsing` / `transforming` / `delivering` swap their spinning `Loader2` for a static `PauseCircle` while paused. A spinner during an outage is the single most dishonest pixel on the screen. Implement as a `pulse: false` override in `UnifiedStatusBadge` when `paused` — pass it as a prop; do not read a hook inside that server-safe component.

### 8.4 Dashboard `/bridge`

1. **A strip above the KPI row** (same styling as §8.3, not dismissible — this is the operator's morning glance):
   > **`Order processing is paused.`** `{n} orders are waiting. Nothing is lost — they start as soon as it restarts.` · `Check system health →`
   where `n = byStatus.pending_parse + parsing + transforming + delivering`. Omit the count clause entirely when `n === 0` rather than print `0 orders are waiting`.
2. **Freeze the wire topology.** The travelling-dot animation asserts flow. While paused: stop the dots, drop wire opacity to 0.55, and set the SVG's `aria-label` to `Order flow is paused`. (Reuse the existing `prefers-reduced-motion` disable path — the mechanism is already there.)
3. **Suppress every health claim.** Any "healthy" / "all clear" / green summary on the dashboard is hidden while paused, replaced by the strip. Do not compute a green number from stale data.

### 8.5 Sidebar

The footer slot (specified in CLAUDE.md §4, not implemented) shows **only** the paused state:

> `● Processing paused` — amber dot, `--navy-text` label, links `/operations/health`, 44px row.

Nothing when healthy. A permanent green "healthy" badge is decoration, and decoration is what makes a real warning invisible.

---

## 9. State matrix

`<OrderProblemPanel>` and its host surfaces, per state.

| | **Loading** | **Empty** | **Error** | **Read-only / Pilot ended** | **Plan-gated (order limit)** | **Success** |
|---|---|---|---|---|---|---|
| **Panel** | Never renders its own spinner. The order query's gate (`BridgePageLoader`, "Preparing your order…") covers it. Action buttons carry their own pending label. | N/A — a panel with no problem does not mount. | Inline strip above the actions: `--danger-soft` bg, `--danger` text (**4.92:1**), server message verbatim, actions re-enabled, `role="alert"`. On the 1st failure the tier promotes to `us` (§4). | Every POST action disabled. Panel keeps the full explanation. A notice replaces the helper: `Your Pilot has ended. You can still view previous orders, but new processing is paused. Upgrade to Growth to continue.` Primary becomes `Upgrade to Growth →` → `/settings?tab=billing`. Link-only actions (open layout, see reply, billing) stay live. | Send-shaped actions disabled with `You've reached your plan's order limit. Upgrade to continue processing new orders this month.` **Retries stay enabled** — the shipped meter counts at creation, so a retry of an existing order consumes nothing new. | The panel does not paint success. It unmounts when the status moves (the `useRetryDelivery` pattern). For queued-but-unmoved: `Queued — waiting for it to start. This page updates on its own; you don't need to click again.` |
| **Inbox** | Existing skeletons (table rows + mobile cards) — keep. | Two branches already correct: filtered → `No matching orders` + `Clear filters`; genuine → `Your inbox is clear` + upload + practice order. Add a third: **problem-filter zero** → `Nothing here needs you right now.` + `See all orders`. | Existing `Couldn't load the queue` card — keep; swap `⚠` for `AlertTriangle`. | `Upload order` disabled with the Pilot copy; every row and filter stays browsable. | `Upload order` disabled with the order-limit copy; `Send selected` disabled with the same. | Bulk bar persists until dismissed (already correct — do not regress it). |
| **Health page** | `Loading pipeline health…` — keep. | `All clear` banner, gated on `workerHealthy` (D7 fix). | `Could not load operations health.` + a real **`Retry`** button (there is none today — the copy says "retry shortly" and offers nothing). | Tiles and the dead-letter table read normally; `Start sending again` disabled with the Pilot copy. | Same. | Requeue notice: `Trying to send {po} again. It will move back to "Sending".` — keep. |
| **Worker paused** | `paused` is false while the health query is loading or errored. Never guess an outage. | — | If `ops-health` errors, show nothing about the Worker. Silence beats a false alarm in both directions. | Orthogonal — both can be true; render the plan notice inside the panel and the outage strip above it. | Orthogonal. | — |

---

## 10. Token map and computed contrast

Every value is a token that exists in `src/app/globals.css`. Ratios computed from the hex values, not asserted.

| Role | Token | Hex | On | Ratio | Verdict |
|---|---|---|---|---|---|
| Failure text, headline, detail | `--danger` | `#B43838` | `--surface` `#FFFFFF` | **5.89** | AA ✓ |
| Failure text on its own band | `--danger` | `#B43838` | `--danger-soft` `#FAE6E6` | **4.92** | AA ✓ |
| Needs-a-person text | **`--amber-text`** | `#8A5310` | `--amber-soft` `#FAF1DD` | **5.62** | AA ✓ |
| Needs-a-person text | **`--amber-text`** | `#8A5310` | `--surface` | **6.31** | AA ✓ |
| ~~Amber text (shipped)~~ | ~~`--amber`~~ | `#B36D14` | `--amber-soft` | **3.65** | ✗ **D5 — replace everywhere** |
| Amber as dot / edge bar only | `--amber` | `#B36D14` | `--surface` | **4.11** | non-text ✓ (3:1) |
| Red edge bar / dot | `--danger` | `#B43838` | `--surface` | **5.89** | non-text ✓ |
| Body / attribution | `--ink` | `#0B1A2F` | `--surface` | **17.46** | AA ✓ |
| Automatic / consequence / helper | `--ink-muted` | `#5E6779` | `--surface` | **5.69** | AA ✓ |
| Same, on an amber band | `--ink-muted` | `#5E6779` | `--amber-soft` | **5.06** | AA ✓ |
| Same, on a red band | `--ink-muted` | `#5E6779` | `--danger-soft` | **4.75** | AA ✓ |
| Group headers, sub-labels | `--ink-faint` | `#667085` | `--surface` | **4.97** | AA ✓ |
| ~~Faint on a red band~~ | ~~`--ink-faint`~~ | `#667085` | `--danger-soft` | **4.15** | ✗ use `--ink-muted` |
| Server detail (mono) | `--ink-muted` | `#5E6779` | `--surface-2` `#F1F3F7` | **5.12** | AA ✓ |
| Primary button | `#FFFFFF` | | `--navy` `#0B1A2F` | **17.46** | AA ✓ |
| Queued / info strip | `--brand-blue-deep` | `#0F4FA8` | `--brand-blue-soft` `#EAF0F8` | **6.77** | AA ✓ |
| Focus ring | `--brand-blue` | `#1E66C9` | `--surface` | **5.53** | non-text ✓ (needs 3:1) |
| Focus ring | `--brand-blue` | `#1E66C9` | `--bg` `#F6F7FA` | **5.16** | non-text ✓ |
| Outage strip border | `#F0D39A` | | `--amber-soft` | 1.4 | decorative only — never load-bearing |
| Panel / card border | `--border` | `#E5E8EE` | `--surface` | 1.23 | decorative only |

Rules that fall out of this table:

- **`--amber` is a dot, a 3px bar, or an icon stroke. It is never text.** `--amber-text` is the text token. The `.pill-*` CSS layer already gets this right (`globals.css:517-531`); only the inline-styled panels do not.
- Never `--ink-faint` on `--danger-soft`.
- Focus indication is `--brand-blue`, never `--border-strong` (**1.55:1** — fails the 3:1 non-text floor).
- Violet `--ai #6F4FCE` (**5.70:1** on surface) appears nowhere in this system. No failure state is AI-generated content.

---

## 11. Accessibility contract

Requirements, not polish. Each is checkable.

1. **Contrast** — every pair in §10, computed. No `--amber` text anywhere.
2. **Tap targets ≥44px** — actions 48px at 390px, 40px at 1440px with 44px hit area via negative margin (the `InboxBackChip` pattern at `WorkshopGateChrome.tsx:44` — reuse it). Step-2 radios 48px rows. Sub-chips 44px at 390px, 28px at 1440px. Tile links already hold `minHeight: 44` — keep.
3. **Inputs ≥16px** — the only input in this system is the `§5` radio group (no text entry) and the inbox search box, which is **12.5px today** (`InboxView.tsx:1263`) and will zoom iOS Safari on focus. Raise to 16px at `<sm`, keep 12.5px at `sm+` via a media query, not an inline style.
4. **Visible focus on everything** — 2px `--brand-blue` outline, 2px offset, on every button, link, radio, chip, checkbox and disclosure. Never `outline: none`. Inline-styled controls in these files have no focus style today; add a shared `.plk-focus` class rather than 40 inline handlers.
5. **Announcements** — the error strip is `role="alert"`; the queued/slow strips are `role="status" aria-live="polite"` (matching `FailedPanels.tsx:324,343`); the outage strips are `role="status"`. A revealed §5 action is announced by moving focus to it, not by an extra live region.
6. **Dialogs trap focus and close on Escape** — `useConfirm` (Radix `AlertDialog`) and `OrderDetailsDrawer` (its own `Tab` trap + Esc at lines 69-78) both already comply. Any new dialog uses `useConfirm`; no new dialog primitives.
7. **`prefers-reduced-motion: reduce`** — no spinner rotation (swap to a static `PauseCircle`/`Loader` glyph), no wire-traveller animation, no pill pulse, no highlight flash on the §6 action line. The topology already respects it; extend the same guard to the badge pulse.
8. **Semantics** — the panel is `<section aria-labelledby>` pointing at its headline; the headline is a `<h2>` in banner mode (the workshop owns the `h1`) and the gate keeps `WorkshopGateHeader`'s `h1` (one `h1` per page — already correct). Step 2 is a real `<fieldset><legend>What did {supplier} say?</legend>` with a native radio group, so arrow keys work and the group is announced as one control.
9. **Icons are never the only signal** — every tone pairs an icon with a word (`UnifiedStatusBadge` already does; the edge bar is `aria-hidden` because the pill carries the meaning).
10. **Disabled ≠ unexplained** — every disabled control has a visible adjacent reason, not only a `title`. `title` is invisible to touch and to screen readers in several combinations; the inbox's current reliance on it (`InboxView.tsx:478-509`) needs a visible companion line.

---

## 12. What I deliberately left out, and why

1. **A "Resend" action for `rejected_by_supplier`.** `OrderStatusMachine.cs:99` gives it **no outgoing transitions** — it is terminal. A resend button would be the fourth guaranteed-400 control on this screen. It is replaced by `Start a corrected order`, which is the flow the state machine actually permits.
2. **Type-to-confirm on the `delivery_unconfirmed` re-send.** Rejected on purpose (§5.4). At 390px, typing a PO to escape a stuck order trains people to copy-paste past the gate. The load-bearing friction is the factual assertion, which cannot be satisfied without contacting the supplier.
3. **Attempt counts and retry countdowns for `delivery_failed`.** `Order` carries only `errorMessage`. Inventing "attempt 2 of 5, next try in 4 minutes" is precisely the class of confident-wrong copy that makes an operator either walk away or escalate on the wrong schedule. The panel says what is true — retries are running, each waits longer — and the honest numbers stay behind `See every attempt`.
4. **A one-screen "bulk fix" for failures.** Every one of the eight has a different cause and a different owner. A grid of checkboxes over `Problems` would let an operator blind-retry a dead-lettered order and a missing-config order in one click, which is how retry budgets get burned. Bulk stays restricted to `{ready_to_deliver, delivery_failed}` — exactly `ClaimableForRetryFrom`.
5. **A new dialog primitive.** `useConfirm` and `OrderDetailsDrawer` already trap focus and close on Escape. Two focus traps in one codebase is one too many.
6. **Retiring the `Failed` server filter in favour of a bespoke "problems" query.** `?status=failed` already expands server-side to the exact bucket the pill represents. Widening it client-side to include `delivery_held` / `delivery_unconfirmed` would silently break across pagination. Those two get their own chips instead.
7. **A dashboard "system status" widget.** The dashboard gets one strip when something is wrong and nothing when it is right. A permanent status widget is decoration that trains people to stop reading the place a real warning appears.
8. **Auto-retrying `transform_failed` on the operator's behalf.** The backend calls it terminal for the same inputs. An automatic retry would burn work and teach the operator that the state resolves itself, which it does not.
9. **A per-state colour scheme.** Two tones only — amber (needs a person, nothing broke) and red (something broke). The eight states are distinguished by words, not by eight hues. Per-screen colour themes are a listed anti-pattern and eight tones would make the two that matter unlearnable.
10. **Reworking the three-column workshop layout.** Locked. The change here is that a problem now sits *above* the three columns instead of replacing them — which is the layout being used as intended, not altered.
