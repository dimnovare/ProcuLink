# Unified Order Workshop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the two-mode order-review screen with ONE collapsible 3-zone Order Workshop — lossless received fields (left), AI-first issues+mapping (center), exact supplier output+send (right) — composing the existing engine, not rewriting it.

**Architecture:** P0 makes the backend source-capture lossless (every field, every ingest path). P1 builds a new `OrderWorkshop` React component that composes the existing `MapperWorkbench`, a new `IssuesPanel`, and an `OutputZone`, behind a feature flag, with a collapse/focus layout hook and an AI-first mapping list. P2 ships reduced-mobile, flips the flag, and deletes the old two-mode + orphaned triptych code. Characterization tests freeze the invariants to end the 3-rebuild churn.

**Tech Stack:** Backend ASP.NET Core 8 + EF Core + Hangfire + xUnit (Postgres via Testcontainers). Frontend Next.js 15 App Router + TanStack Query + Clerk + Tailwind/shadcn (Bridge Layer tokens) + vitest + Playwright.

**Spec:** `docs/superpowers/specs/2026-06-16-unified-order-workshop-design.md` (commit 921a831). **Mockups:** `order_workshop_unified_3zone`, `order_workshop_ai_mapping_focus`. **Design system:** read `docs/design-system/00-agent-quick-brief.md` + `10-claude-code-brief.md` before any JSX.

**Conventions:** Backend tests are Postgres-backed (InMemory masks FK/ExecuteUpdate). Frontend: `bunx tsc --noEmit` + `bun run test` must be green per task. Each repo's commits are separate. Worktree isolation for any parallel work (shared dir races on EF snapshot / `.next`).

---

## File structure

**Backend (`C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink`)**
- `ProcuLink.Transform/Tokenizing/SourceTokenizer.cs` — add a `.json` arm (today JSON → empty).
- `ProcuLink.Api/Services/Orders/OrderIngestionService.cs` — tokenize+persist on every ingest path.
- `ProcuLink.Api/Controllers/IngressController.cs` — tokenize+persist the pushed payload.
- `ProcuLink.Api/Controllers/OrdersController.cs` — `GetSourceTokens` prefers persisted `SourceCapture`.
- `ProcuLink.Transform.Tests/Tokenizing/SourceTokenizerJsonTests.cs` (new) — JSON arm.
- `ProcuLink.Api.Tests/.../SourceCaptureLosslessTests.cs` (new) — golden no-field-dropped + endpoint precedence + API-push.

**Frontend (`C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink`)**
- `src/components/bridge/workshop/useWorkshopLayout.ts` (new) — collapse/focus state + derived grid.
- `src/components/bridge/workshop/OrderWorkshop.tsx` (new) — the 3-zone shell.
- `src/components/bridge/workshop/ReceivedZone.tsx` (new) — lossless left pane (wraps existing IncomingPane logic, lossless-first).
- `src/components/bridge/workshop/IssuesPanel.tsx` (new) — the one issue list (from the unified validator).
- `src/components/bridge/workshop/MappingPanel.tsx` (new) — AI-first list + collapsed-mapped + attention rows; mounts existing `MapperWorkbench` for drag.
- `src/components/bridge/workshop/OutputZone.tsx` (new) — wraps existing `OutputPreview` + `OutputStructureDesigner` + Send.
- `src/components/bridge/workshop/mappingListModel.ts` (new) — pure: split suggestions into auto vs attention by calibrated threshold.
- `src/lib/flags.ts` (modify or new) — `ORDER_WORKSHOP_V2` flag.
- `src/components/bridge/SpineReview.tsx` — P1: branch to `OrderWorkshop` when the flag is on. P2: delete the two-mode branches.
- Tests: `*.test.ts(x)` colocated; `tests/e2e/order-workshop.spec.ts` (new).

---

## PHASE P0 — Backend lossless capture (ships invisibly; the existing pane just gains fields)

### Task 1: JSON arm for SourceTokenizer

**Files:**
- Modify: `ProcuLink.Transform/Tokenizing/SourceTokenizer.cs`
- Test: `ProcuLink.Transform.Tests/Tokenizing/SourceTokenizerJsonTests.cs` (create)

- [ ] **Step 1: Ground** — read `SourceTokenizer.cs` fully. Confirm the dispatch switch on extension/format, the token shape (id + label + value), and that `.json` currently falls to the empty default. Note the existing CSV/XML token id conventions to mirror.

- [ ] **Step 2: Write the failing test**

```csharp
using ProcuLink.Transform.Tokenizing;
using Xunit;

public class SourceTokenizerJsonTests
{
    [Fact]
    public async Task Json_object_and_array_every_leaf_becomes_a_token()
    {
        var json = """{ "poNumber":"PO-1", "lines":[ {"sku":"A","qty":2}, {"sku":"B","qty":3} ] }""";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        var result = await new SourceTokenizer().TokenizeAsync(stream, ".json", default);

        // every leaf present, addressed by JSON pointer, with value
        Assert.Contains(result.Tokens, t => t.Id == "/poNumber" && t.Value == "PO-1");
        Assert.Contains(result.Tokens, t => t.Id == "/lines/0/sku" && t.Value == "A");
        Assert.Contains(result.Tokens, t => t.Id == "/lines/1/qty" && t.Value == "3");
        Assert.DoesNotContain(result.Tokens, t => t.Id == "/lines"); // containers are not leaf tokens
    }
}
```

(If the real `TokenizeAsync` signature / token type differs from grounding, adapt the test to the real names — keep the assertions: every leaf, JSON-pointer id, value present.)

- [ ] **Step 3: Run it — expect FAIL** (`.json` returns empty). Run: `dotnet test ProcuLink.Transform.Tests --filter SourceTokenizerJsonTests`

- [ ] **Step 4: Implement the `.json` arm** — add a `case ".json"` (and any json media types) that parses with `System.Text.Json.JsonDocument` and walks it recursively: for each object property recurse with id `parent + "/" + name`; for each array element recurse with id `parent + "/" + index`; for each leaf (string/number/bool/null) emit a token `{ Id = pointer, Label = last-segment humanized, Value = raw text }`. Never throw (catch → empty, mirroring the other arms).

- [ ] **Step 5: Run it — expect PASS.** Also run the full Transform suite: `dotnet test ProcuLink.Transform.Tests` (no regressions).

- [ ] **Step 6: Commit** — `git commit -m "feat(tokenize): JSON arm for SourceTokenizer (every leaf → pointer token)"`

### Task 2: Tokenize + persist on the file-ingest paths

**Files:**
- Modify: `ProcuLink.Api/Services/Orders/OrderIngestionService.cs`
- Test: `ProcuLink.Api.Tests/Services/SourceCaptureLosslessTests.cs` (create)

- [ ] **Step 1: Ground** — read `OrderIngestionService.cs`: find `CreateFromFileAsync` (sync LLM path) and `ParseStoredFileAsync` (async path), the existing `UpsertSourceCaptureAsync` helper + `SourceCapture.TokensJson` column, and where raw bytes are held. Identify which paths already persist tokens vs which skip.

- [ ] **Step 2: Write the failing test** (Postgres-backed, in the existing Postgres collection)

```csharp
[Fact]
public async Task File_ingest_persists_a_lossless_token_set()
{
    // arrange: a CSV with 6 columns, ingested through the real path
    // act: run the ingest
    // assert: SourceCapture for the order exists and TokensJson contains a token per source cell
    var capture = await Db.SourceCaptures.FirstAsync(c => c.OrderId == orderId);
    var tokens = ParseTokens(capture.TokensJson);
    Assert.Equal(6 * rowCount, tokens.Count);     // every cell
    Assert.Contains(tokens, t => t.Label == "buyer_code" && t.Value == "ACM-BOLT-001");
}
```

- [ ] **Step 3: Run — expect FAIL** on the path that skips persistence.

- [ ] **Step 4: Implement** — at the end of each ingest path, if no `SourceCapture` was written, call `SourceTokenizer.TokenizeAsync(rawBytes, ext)` on the bytes already in hand and `UpsertSourceCaptureAsync(orderId, tokens)`. Make it idempotent (skip if a non-empty capture exists). No schema change.

- [ ] **Step 5: Run — expect PASS** + full `ProcuLink.Api.Tests` for that class.

- [ ] **Step 6: Commit** — `git commit -m "feat(ingest): persist lossless SourceCapture on every file ingest path"`

### Task 3: Tokenize + persist the API/pushed payload

**Files:**
- Modify: `ProcuLink.Api/Controllers/IngressController.cs` (and/or the service it calls)
- Test: same `SourceCaptureLosslessTests.cs`

- [ ] **Step 1: Write the failing test** — POST an order via the ingress path (JSON body, no stored file) → assert a `SourceCapture` with tokens exists (every line field present), so the order has draggable fields with no file.

- [ ] **Step 2: Run — expect FAIL** (pushed path persists nothing).

- [ ] **Step 3: Implement** — in `ReceiveOrder`, after the stub is created, tokenize the payload bytes with the payload format (JSON → the Task-1 arm) and persist via the same `UpsertSourceCapture`. Bound by the existing ingress size cap.

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: Commit** — `git commit -m "feat(ingress): persist lossless SourceCapture for pushed payloads"`

### Task 4: Endpoint prefers persisted capture

**Files:**
- Modify: `ProcuLink.Api/Controllers/OrdersController.cs` (`GetSourceTokens`, ~:1126)
- Test: `SourceCaptureLosslessTests.cs`

- [ ] **Step 1: Write the failing test** — an order with a persisted `SourceCapture` but a **purged/absent** source blob → `GET /api/orders/{id}/source-tokens` still returns the full token set (today it re-tokenizes from R2 and returns empty).

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement** — `GetSourceTokens`: if `SourceCapture.TokensJson` is present + non-empty, return it; ELSE fall back to the live R2 re-tokenize (existing behavior); keep the graceful empty for genuinely source-less orders.

- [ ] **Step 4: Run — expect PASS.**

- [ ] **Step 5: Commit** — `git commit -m "fix(orders): source-tokens endpoint prefers persisted capture (purge/no-file safe)"`

### Task 5: Golden "no field dropped" tests per format

**Files:** Test: `ProcuLink.Transform.Tests/Tokenizing/SourceTokenizerGoldenTests.cs` (create) — use sanitized fixtures.

- [ ] **Step 1** — for each of CSV / XLSX / XML / cXML / EDIFACT / X12 / JSON, a small real-shaped fixture → assert the token count equals the source field count and key fields appear by id. (Theory with one case per format.)
- [ ] **Step 2** — run; fix any format whose arm drops fields.
- [ ] **Step 3: Commit** — `git commit -m "test(tokenize): golden no-field-dropped per format"`

### Task 6: P0 verify + deploy

- [ ] Run full backend: `dotnet test ProcuLink.slnx` — 0 fail.
- [ ] Commit + push; confirm Railway API + Worker redeploy.
- [ ] Live-verify on prod: upload one CSV + one JSON-push order → `GET /source-tokens` returns every field. Update the spec build-log.

---

## PHASE P1 — The OrderWorkshop FE shell (flag-gated, composes the engine)

> Read `docs/design-system/10-claude-code-brief.md` + the two mockups first. Reuse `src/components/bridge/*`. AI violet only on suggestions. Green-primary Send. 3px buyer-blue/supplier-green zone edges.

### Task 7: `useWorkshopLayout` — collapse/focus state

**Files:** Create `src/components/bridge/workshop/useWorkshopLayout.ts` + `.test.ts`.

- [ ] **Step 1: Write the failing test**

```ts
import { computeGrid } from "./useWorkshopLayout";
test("mapping focus collapses both sides", () => {
  expect(computeGrid({ focus: "mapping", leftCollapsed: false, rightCollapsed: false }))
    .toEqual({ left: "rail", center: "1fr", right: "rail" });
});
test("output focus gives output the width", () => {
  expect(computeGrid({ focus: "output", leftCollapsed: false, rightCollapsed: false }))
    .toEqual({ left: "rail", center: "rail", right: "1fr" });
});
test("all = three zones, honoring manual collapses", () => {
  expect(computeGrid({ focus: "all", leftCollapsed: true, rightCollapsed: false }))
    .toEqual({ left: "rail", center: "1fr", right: "auto" });
});
```

- [ ] **Step 2: Run — FAIL.**
- [ ] **Step 3: Implement** — pure `computeGrid(state)` returning `{left,center,right}` each `"rail" | "auto" | "1fr"`; `focus` overrides per the spec; otherwise per-zone `collapsed`. Plus the `useWorkshopLayout()` hook wrapping `useState` + session persistence (sessionStorage, keyed `plk-workshop-layout` — layout state, NOT a persona flag) + `setFocus`/`toggleLeft`/`toggleRight`.
- [ ] **Step 4: Run — PASS.** **Step 5: Commit.**

### Task 8: `mappingListModel` — split auto vs attention

**Files:** Create `src/components/bridge/workshop/mappingListModel.ts` + `.test.ts`.

- [ ] **Step 1: Write the failing test**

```ts
import { splitMappings } from "./mappingListModel";
const sug = [
  { outputField: "orderNumber", source: "PO", confidence: 0.99, accepted: true },
  { outputField: "items[].sku", source: "WIDGET-B", confidence: 0.6, accepted: false },
  { outputField: "currency", source: null, confidence: 0, accepted: false },
];
test("auto = accepted/high-confidence; attention = unmapped or low-confidence", () => {
  const { auto, attention } = splitMappings(sug, { trustedThreshold: 0.85 });
  expect(auto.map(a => a.outputField)).toEqual(["orderNumber"]);
  expect(attention.map(a => a.outputField)).toEqual(["items[].sku", "currency"]);
});
```

- [ ] **Step 2: Run — FAIL. Step 3: Implement** — `splitMappings(suggestions, {trustedThreshold})`: `auto` = `accepted || confidence >= trustedThreshold && source != null`; `attention` = the rest. Pull `trustedThreshold` from the existing calibration response (default 0.85). **Step 4: PASS. Step 5: Commit.**

### Task 9: `IssuesPanel` (center-top)

**Files:** Create `src/components/bridge/workshop/IssuesPanel.tsx` + `.test.tsx`.

- [ ] **Step 1: Ground** — read the existing unified-validator client + `FixQueueTriage` to reuse the structured issue shape `{code,severity,ref,title,why,fixAction?}` and the existing accept/fix handlers (do NOT re-implement the validator).
- [ ] **Step 2: Write the test (characterization)** — given N issues → renders N plain-language rows (title + why), each with a "where" affordance that calls `onFocusField(ref)`; given 0 issues → renders the green "ready to send" bar; a failing-invariant order never renders green.
- [ ] **Step 3: Implement** — `IssuesPanel({ issues, onFocusField, onFix })`: maps issues to rows (title from RuleCatalog, why, click→onFocusField); deterministic `fixAction` → one-click button; 0 → green ready bar. Bridge Layer styles per the mockup. No new data fetching — issues come in as a prop from `OrderWorkshop`.
- [ ] **Step 4: Run vitest — PASS. Step 5: Commit.**

### Task 10: `MappingPanel` (AI-first list + drag escape)

**Files:** Create `src/components/bridge/workshop/MappingPanel.tsx` + `.test.tsx`.

- [ ] **Step 1: Ground** — read `MapperWorkbench.tsx` props (how it takes the order + emits wire changes) and the AI-suggestion + accept endpoints in `api-client.ts`. MappingPanel COMPOSES `MapperWorkbench` for the drag surface; it does not reimplement wires.
- [ ] **Step 2: Write the test** — given suggestions, `splitMappings` → a collapsed "N mapped by AI · review" toggle (shows the auto rows on expand) + the attention rows; each attention row has Accept (calls `onAccept`), a change-source `<select>` (calls `onChangeSource`), and renders a drag handle; an `+ Add output field` calls `onAddField`. Provenance + confidence shown; AI violet class present.
- [ ] **Step 3: Implement** — `MappingPanel({ order, suggestions, calibration, onAccept, onChangeSource, onAddField })`: uses `splitMappings`; renders the collapsed-auto block + attention rows per the `order_workshop_ai_mapping_focus` mockup; mounts `MapperWorkbench` below (or behind the drag affordance) for manual wiring; Accept-all reuses the already-fixed calibrated/raw boundary.
- [ ] **Step 4: Run — PASS. Step 5: Commit.**

### Task 11: `OutputZone` (right) + `ReceivedZone` (left)

**Files:** Create `OutputZone.tsx`, `ReceivedZone.tsx` (+ light tests).

- [ ] **Step 1: Ground** — `OutputPreview.tsx` (preview==delivery), `OutputStructureDesigner.tsx` (reshape/add/paste-sample), the send flow hook; `incomingPaneModel.ts` (make it lossless-source-first: show ALL tokens from the P0 capture, grouped, with source pointers — stop the canonical-dedup demotion).
- [ ] **Step 2: Implement `OutputZone`** — wraps `OutputPreview` + a format switch + `+ Edit output structure` (opens `OutputStructureDesigner`) + the green Send button (gated by `canSend`); collapse rail per layout.
- [ ] **Step 3: Implement `ReceivedZone`** — reads the lossless tokens (P0 endpoint), groups Header/Parties/Lines/Other, each row = label + value + source pointer + drag handle; collapse rail; honest empty only when truly source-less.
- [ ] **Step 4: Tests** (model-level: lossless grouping shows every token; no dedup-drop) **+ Commit.**

> **ARCHITECTURE CORRECTION (grounded 2026-06-16, after the P1-leaf agent).** `MapperWorkbench`
> is ALREADY a full 3-pane mapper (`IncomingPane` + gutter/wires + `OutgoingPane` + `MapperPreviewPane`
> + `OutputStructureDesigner`), from its solid 2026-06-14 rebuild. So the separate `ReceivedZone` /
> `OutputZone` / `MappingPanel` wrappers DUPLICATE its panes. **Corrected approach:** `OrderWorkshop` =
> `IssuesPanel` (top) + the existing `MapperWorkbench` **enhanced** with (a) collapse/focus props for its
> incoming/preview panes (driven by `useWorkshopLayout`), (b) an "attention-first" default filter on
> `OutgoingPane` (collapse the AI-auto-mapped behind an "N mapped" chip; show only unmapped/low-conf,
> using `splitMappings`), and (c) `onFocusField` hooked to the existing `?field=` deep-link/`selectedId`.
> KEEPERS from P1-leaf: `useWorkshopLayout`, `mappingListModel`, `IssuesPanel`. The `ReceivedZone`/
> `OutputZone`/`MappingPanel` wrappers are folded into MapperWorkbench enhancements, not mounted alongside
> it (avoid the double-preview the leaf agent flagged). Touch MapperWorkbench additively (new optional
> props default to today's behavior → byte-identical when the flag is off).

### Task 12: `OrderWorkshop` shell + flag wiring

**Files:** Create `OrderWorkshop.tsx`; modify `src/lib/flags.ts` + `SpineReview.tsx`.

- [ ] **Step 1: Implement the flag** — `ORDER_WORKSHOP_V2` (env `NEXT_PUBLIC_ORDER_WORKSHOP_V2` + optional `?workshop=1` override for QA).
- [ ] **Step 2: Implement `OrderWorkshop`** — one TanStack query for the order (spine + lossless rawFields + mapping suggestions + issues + preview ptr); composes `ReceivedZone | (IssuesPanel + MappingPanel) | OutputZone` in a grid driven by `useWorkshopLayout` + a `Focus: All/Mapping/Output` control + per-zone chevrons; passes `onFocusField` from IssuesPanel → MappingPanel scroll/highlight; gates Send on `issues.length===0 && invariantsPass`.
- [ ] **Step 3: Wire the route** — in `SpineReview.tsx`, when the flag is on, render `<OrderWorkshop orderId=.../>` instead of the two-mode branches (leave the old branches intact for now).
- [ ] **Step 4: Verify** — `bunx tsc --noEmit` 0; `bun run test` green; `bun run build` clean. **Commit.**

### Task 13: Characterization tests for the 5 invariants

**Files:** `src/components/bridge/workshop/__tests__/invariants.test.tsx` + extend the e2e.

- [ ] Write tests asserting: (1) adding/wiring a field never shrinks the visible target list; (2) the issues list === the send-gating validator (mock a -3 qty → not green, Send disabled); (3) every source field appears in ReceivedZone for a sample of each format; (4) collapsing a zone preserves an unsaved mapping edit; (5) preview==delivery is covered by the existing parity suite (reference it). Run — green. **Commit.**

### Task 14: P1 verify + deploy (flag OFF in prod, ON for QA)

- [ ] Full FE verify; push; Vercel Ready.
- [ ] Live QA on prod with `?workshop=1` on a real order: AI-mapped → accept the few → issues clear → green → (dry-run) the output matches the old screen's delivery bytes (preview==delivery). Side-by-side vs the old screen. Update the spec build-log.

---

## PHASE P2 — Reduced mobile, cutover, delete the old

### Task 15: Reduced-mobile OrderWorkshop

- [ ] Below `lg`: render issues (inline quick-fixes) + `OutputPreview` + Send/retry; the drag mapper shows an honest "Open on desktop to map fields" card. Test at 390px (model-level + a Playwright viewport check). **Commit.**

### Task 16: Flip the flag + delete the old two-mode + orphans

- [ ] Turn `ORDER_WORKSHOP_V2` ON in prod env.
- [ ] Delete the `SpineReview` two-mode `subView` branches + `?view=` swap; delete `FixQueueTriage` (separate screen), `SpineConnectors`, `WireDragLayer`, `SourceTokenPanel`, `TabletSpineLayout`, `MobileSpineAccordion` (confirm each is unreferenced via grep first; fold any still-needed pure-fn into the workshop). `bunx tsc --noEmit` 0; full test + build. **Commit.**

### Task 17: Prod cutover verify

- [ ] Re-run the 3-supplier × 3-format live delivery proof on the new screen (real PO corpus). Confirm preview==delivery, green-only-when-valid, every field draggable, collapse/focus work, mobile reduced. Update the spec build-log + MASTER-BUILD-STATUS.

---

## Self-review

- **Spec coverage:** north-star (DoD task 17) · center issues-over-mapper (T9/T12) · lossless left (P0 + T11) · AI-first mapping + drag-escape (T8/T10) · collapsible/focus (T7/T12) · flexible add-field/output (T10/T11) · reduced mobile (T15) · delete old + orphans (T16) · characterization invariants (T13) · phased shippable (P0/P1/P2) — all mapped. ✓
- **Placeholders:** none — backend steps carry real tests + impl direction; FE steps carry contracts + test specs + composition (JSX generated by the executing agent from the design system + mockups, which is the correct division for a UI shell, not a placeholder). ✓
- **Type consistency:** `computeGrid`/`splitMappings`/`OrderWorkshop` props referenced consistently across tasks. ✓
