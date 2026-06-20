# Phase 0 / Task 1 — Restore inline per-line resolution in the Order Workshop

> Approved by founder 2026-06-19 ("Start Phase 0 now"; build in a fresh focused session, TDD, on the prod-default review screen).
> Parent plan: [2026-06-19-po-tool-redesign-analysis.md](2026-06-19-po-tool-redesign-analysis.md). This is Phase 0 item #1.

## The bug (root-caused live + in code, 2026-06-19)

The v3 Order Workshop (`ORDER_WORKSHOP_V2`, now prod default) has **no working way to enter a supplier code** for an unresolved line. Live prod: 78 / 83 loaded POs stuck in `pending_review`. The founder sees "3 fields to fill before sending" with "Needs a supplier code" chips that do nothing.

Two compounding causes:
1. **The blocker chip jump is dead.** `SendReadinessStrip` chip → `onFocusField(ref)` where `ref` = the line **GUID** (`fixQueueToIssues` line ~80: `ref: c.lineId ?? c.key`). The mapper (`MapperWorkbench.resolveRowRef`) keys rows by **output path** (`OutgoingPane` `portRef(field.outputPath, el)`) / incoming `field.id` — never the line GUID. `resolveRowRef(GUID)` → null → `scrollIntoView` no-ops. Confirmed live: the `?field=<guid>` matches zero DOM elements, nothing flashes. (This is why Task #123 + the fuzzy-matcher patch both "passed" yet the button stays dead — fuzzy can't bridge a GUID to an output path.)
2. **There is no inline code-entry anywhere.** `IssuesPanel` renders only "Where →" (the dead jump) + a one-click "Accept suggestion" for AI cards. The manual-code machinery EXISTS in `useResolveActions` (`startLineEdit`, `lineEditId`, `lineDraft`, `setLineDraft`, `commitLineCode`, `cancelLineEdit`, `confirmFlaggedLine`, `acceptingLineId`) and `OrderWorkshop` already creates `resolve = useResolveActions(...)` (line ~106) — but only `resolve.acceptSuggestion` is wired (via `onFix`). The manual path is built and disconnected.

## The fix (machinery exists — wire it in, don't invent)

Files:
- `src/components/bridge/review/buildFixQueue.ts` — `FixCardKind`, card carries `kind` + `lineId` + `lineNumber` (already there).
- `src/components/bridge/workshop/OrderWorkshop.tsx` — `fixQueueToIssues` (~line 71); `resolve` (~line 106); IssuesPanel render site (find it — rendered as the desktop center list AND in `MobileTriage`).
- `src/components/bridge/workshop/IssuesPanel.tsx` — the card list (the "Fix these to send" panel).
- `src/components/bridge/review/hooks/useResolveActions.ts` — the resolution API (no change; just consume).

Steps:
1. **`WorkshopIssue`** (IssuesPanel.tsx): add `kind?: FixCardKind` and `lineId?: string`.
2. **`fixQueueToIssues`** (OrderWorkshop.tsx): carry `kind: c.kind` and `lineId: c.lineId`.
3. **`IssuesPanel`**: accept an optional `resolve` prop (the subset of `ResolveActionsApi`: `lineEditId, lineDraft, setLineDraft, startLineEdit, commitLineCode, cancelLineEdit, confirmFlaggedLine, acceptingLineId`) + the order lines (to read current code / AI suggestion per `lineId`). Render per `kind`:
   - `manual-code` → an inline text input ("supplier code") + **Save** (`commitLineCode(lineId)`); Enter commits, Esc cancels; disabled+spinner while `acceptingLineId === lineId`. This REPLACES "Where →" as the primary action for manual-code cards.
   - `ai-suggestion` → keep the green "Accept suggestion" (onFix) AND add "Enter manually" → `startLineEdit`.
   - `review-flag` → "Confirm" (`confirmFlaggedLine`) + "Change code" → `startLineEdit`.
   - header `rule-failure` → keep "Where →" (header edit lives in the mapper/details for now; out of Phase 0 scope).
4. **`OrderWorkshop`**: pass `resolve` + `order.lines` into the IssuesPanel render(s) (desktop + `MobileTriage`).
5. **`SendReadinessStrip` chip**: change `onJump` target so it scrolls to the issue CARD (anchor each card `data-issue-ref={code}` or the line id) rather than the dead mapper jump — the card is where the fix now lives. Keep `onFocusField` for the mapper as a secondary.

## Tests (the acceptance gate that was missing)

The prior "fix" shipped because tests checked `buildFixQueue` purity but never that the control DOES anything. New tests (vitest + testing-library, alongside `workshop/__tests__/invariants.test.tsx`):
- A `manual-code` issue renders a **focusable text input**; typing a code + Enter calls `commitLineCode` with that code.
- A `review-flag` issue renders a **Confirm** button calling `confirmFlaggedLine`.
- The SendReadinessStrip chip for a line blocker scrolls to an element that actually exists (`data-issue-ref` present) — assert the target node is found, not null.

## Verify before shipping

- `bun run build` clean; vitest green.
- Live: open a real `pending_review` order on prod (there are ~78), type a supplier code in the issue card, Save → line resolves → order can reach `ready`. Confirm on desktop AND mobile width.
- Confirm no order shows both the old "Fix these to send" duplicate and the new strip (separate Phase 0 cleanup if it recurs).

## Out of scope for Task 1 (later Phase 0 / redesign)
Header-field inline fixes beyond what exists; killing the duplicate review surface; the output-model convergence (Phase 1). Keep this change to **line-code resolution in the issue cards**.
