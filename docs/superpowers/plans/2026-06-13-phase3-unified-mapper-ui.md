# Phase 3 — Unified three-pane mapper + field discovery + inbox redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This is a **FRONTEND** plan — the repo is `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink` (Next.js 15 App Router, TypeScript, Tailwind, shadcn/ui, **bun**). Adapt TDD to the frontend: **vitest + @testing-library** is the test gate (the repo already has `WireDragLayer.test.ts`, `SourceWireDragLayer.test.ts`, `OutputMappingEditor.test.ts`, `DeliveryConfigEditor.governance.test.tsx`). Run tests with `bun run test`, type-check + build with `bun run build`, lint with `bun run lint`.

**Goal:** Merge the three existing wire pieces (`SourceWireDragLayer` raw→canonical, `WireDragLayer` canonical→output, `PoMappingEditor` source-column→canonical bezier/confidence) into **ONE prop-driven three-pane mapper** — *Incoming (raw source) │ Canonical spine │ Outgoing target* — reused in BOTH the inbox per-order review (`SpineReview`) and the Supplier Connection revision editor (`connections/[connectionId]`). Add the **anti-overwhelm field-discovery** pattern (grouped/collapsible source list with Header/Parties/Lines/Raw groups, debounced search over labels AND values, filter chips, value previews, virtualization, AI ghost wires), **inline add/remove of custom canonical fields**, **catalog + validation badges**, **manipulator pills on wires**, **command-palette power commands**, a **first-visit SectionGuide** for the mapper, and a **redesigned inbox** (list → open → mapper → preview → deliver) built entirely on the locked Bridge design primitives.

**Architecture (what already exists — build on, do NOT rebuild):**

- `src/components/bridge/WireDragLayer.tsx` — the canonical→output drag engine. Native pointer events, `SNAP_PX=36`, `bezier()`, keyboard connect, `useScrollResync`/`useDragAutoScroll`, `measure()` via `sigRef`/`nodesRef` stability. **Hard-codes** `OUTPUT_LINE_IDS` (7) + `NODE_TO_CANONICAL`. **The unify job makes the node/target lists PROPS.** Pure exported `resolveWireSource()` is unit-tested.
- `src/components/bridge/SourceWireDragLayer.tsx` — the raw-token→canonical mirror. Pure exported `resolveSourceWires()`; `onConnect(tokenId, canonicalField)` → `OrderMappingOverride.sourceMap[field].sourceToken`. Stale-token-safe.
- `src/components/bridge/SpineConnectors.tsx` — decorative bezier wire rendering (confidence coloring). Told `drawOutput={false}` while the drag layer owns the right side.
- `src/components/bridge/SourceTokenPanel.tsx` — **already** does search + header/line grouping + collapse + auto-reveal-on-search + value preview + wired state. **Field discovery EXTENDS this**, not greenfield.
- `src/components/bridge/OutputMappingEditor.tsx` — field-mode (source + 8-manipulator chain) AND template-mode (Scriban + ~400ms live preview). `buildOverrideDraft()` MUST carry `sourceMap` through unchanged (PUT replaces the whole document — documented data-loss trap).
- `src/components/bridge/PoMappingEditor.tsx` — supplier source-column→canonical with SVG bezier + confidence + accept/edit/reject; hard-codes `ALL_CANONICAL` (10) target.
- `src/components/bridge/SpineReview.tsx` — the triptych host (`2330-2479`): grid `1fr 1.05fr 1.15fr`, sticky source/output, scrolling center spine, `SpineConnectors` + `WireDragLayer` + `SourceTokenPanel` wired via `nodeEls`/`outLineEls`/`dotEls` refs.
- `src/components/connections/ConnectionDetail.tsx` + `connections/[connectionId]/page.tsx` + `ReplayPanel.tsx` — the **Supplier Connection revision editor host** (V1/V2 shipped). `ConnectionRevisionBundle` carries `inputMappingJson` / `outputMappingJson` — the second mount point for the unified mapper.
- `src/lib/api/types.ts` — the SHARED type module (`OrderMappingOverride`, `OutputFieldRule`, `SourceFieldRule`, `SourceToken`, `CustomField`, `ManipulatorEntry`, `MANIPULATOR_TYPES`, `CANONICAL_HEADER_FIELDS`/`CANONICAL_LINE_FIELDS`). **Every wire component imports from here → it is the parallel-safety hazard; touch it FIRST, once (Task 1).**
- `src/lib/api-client.ts` + `src/lib/api/core.ts` + `src/lib/api/mapping.ts` — all API access. `getMappingOverride` / `upsertMappingOverride` / `previewMappingOverride` (api-client `1393-1497`); `getMappingSourceColumns` / `suggestMappingFields` (mapping.ts); `getSupplierCatalog` (api-client `816-862`). `authHeader()` from Clerk; `fetchWithTimeout`; `isApiMockMode`.
- Design primitives (LOCKED): `PageShell`/`PageHeader`/`Card`/`MobileListRow` (`components/bridge/layout/`), `UnifiedStatusBadge`, `Button`/`ConfidenceChip`/`AiSuggestion` (`DSPrimitives.tsx`), CSS-var tokens (`--brand-green #2E8E3A`, `--brand-blue #1E66C9`, `--ai`/violet `#6F4FCE`, `--amber`, `--danger`, `--surface`, `--ink`, `--border`, `--radius-md`, `--shadow-card`, `--tap-min 44px`, `--font-display`).
- Command palette / guides: `CommandPalette.tsx` (`buildIndex` hardcoded actions `a1..a12`, Cmd+K in `BridgeTopbar.tsx:335-344`), `section-guides.ts` (`SECTION_GUIDES` array + `matchGuide` dynamic-segment matcher), `HelpSlideover.tsx`.

**Tech Stack:** Next.js 15 App Router, React 18, TypeScript 5.8, Tailwind 3.4 + shadcn/ui (Radix), `@tanstack/react-query` v5, **bun**. Test: vitest 3 + @testing-library/react + jsdom. Spec: `docs/superpowers/specs/2026-06-13-flexible-mapping-design.md` (Layer D + Phase 3). No new dependency is required — `react-resizable-panels` (resizable panes) and `@radix-ui/react-accordion` (groups) and `cmdk` are already installed.

**Dependency on Phase 2 backend (read this before sequencing):** Phase 3 is a UI layer over Phase 2 engine endpoints (`CanonicalFieldDef` CRUD, AI source-suggestion, `catalog.*` price suggestion, validation badges). Those endpoints may not exist yet. **Every Phase-2-dependent task is built against a typed client function with a mock fallback (the existing `isApiMockMode` / `USE_MOCK` pattern), so the UI ships and tests pass NOW; wiring to the real endpoint is a one-line swap when Phase 2 lands.** Each task's header states its Phase-2 dependency explicitly. Tasks 1–6, 11–14 have **no** Phase-2 dependency.

---

## File structure

**Create:**
- `src/components/bridge/mapper/ThreePaneMapper.tsx` — the unified mapper shell (3 resizable panes + SVG wire layer + top action bar + preview pane). Reused by inbox + connection editor.
- `src/components/bridge/mapper/MapperWireLayer.tsx` — generalized, **prop-driven** drag/keyboard wire engine extracted from `WireDragLayer` (node list + target list become props; pure `resolveWireSource` reused).
- `src/components/bridge/mapper/wireMath.ts` — extracted pure helpers (`bezier`, `nearestZone`, `resolveWireSource`, `resolveSourceWires`) so both old + new layers share ONE implementation and the unit tests move here.
- `src/components/bridge/mapper/wireMath.test.ts` — moved/expanded pure-logic tests.
- `src/components/bridge/mapper/SourceUniverse.tsx` — the field-discovery left pane (grouped Header/Parties/Lines/Raw, search, filter chips, virtualization, value previews) — extends `SourceTokenPanel`.
- `src/components/bridge/mapper/SourceUniverse.test.ts` — discovery filtering/grouping/search logic tests (pure helpers below the component).
- `src/components/bridge/mapper/sourceUniverseModel.ts` — pure grouping/filter/relevance helpers (`groupSourceFields`, `filterSourceFields`, `FieldFilter`).
- `src/components/bridge/mapper/CanonicalLane.tsx` — center spine pane with inline "+ Add field" / remove-via-overflow (Tier-2 custom canonical).
- `src/components/bridge/mapper/TargetLane.tsx` — right pane: arbitrary declared target schema (prop-driven), inline catalog/validation badges, manipulator pills.
- `src/components/bridge/mapper/GhostWire.tsx` — AI-suggested dashed wire with confidence ring + ✓/✗ accept/reject.
- `src/components/bridge/mapper/FieldBadges.tsx` — teal catalog chip / green validated / amber review (reason tooltip) / catalog-price inline action.
- `src/components/bridge/mapper/MapperPreviewPane.tsx` — collapsible live-preview pane (reuses `previewMappingOverride`, 400ms debounce, format toggle, just-touched highlight, copy/download).
- `src/components/bridge/mapper/useMapperModel.ts` — the single state hook (loads override/source-tokens/canonical-defs/suggestions/catalog/validation; produces `onConnect`/`onAddField`/`onAcceptSuggestion` etc.; persists via `upsertMappingOverride` carrying `sourceMap`).
- `src/lib/api/canonical-fields.ts` — typed client for Tier-2 `CanonicalFieldDef` CRUD (**Phase 2 endpoints**, mock-fallback).
- `src/lib/api/mapper-ai.ts` — typed client for AI source-suggestions + catalog price-suggestion + validation (**Phase 2 endpoints**, mock-fallback).
- `src/components/bridge/mapper/types.ts` — Phase-3-only view types (`CanonicalNode`, `TargetField`, `SourceField`, `MappingSuggestion`, `FieldValidation`, `CatalogHint`) — kept OUT of the shared `lib/api/types.ts` to avoid churn there.

**Modify:**
- `src/lib/api/types.ts` — add `CanonicalFieldDef`, `CanonicalFieldScope`, `MappingSuggestion`, `FieldValidationState`, `CatalogPriceHint` (ONE edit, Task 1 — every other task imports from here).
- `src/components/bridge/SpineReview.tsx` — replace the `2330-2479` triptych body with `<ThreePaneMapper variant="order" .../>` (keep the existing query/state plumbing).
- `src/components/connections/ConnectionDetail.tsx` — add a "Mapping" tab/section that mounts `<ThreePaneMapper variant="connection" .../>` bound to the draft revision bundle.
- `src/components/bridge/WireDragLayer.tsx` — re-export `bezier`/`resolveWireSource` from `mapper/wireMath.ts` (keep back-compat; old triptych path still compiles during migration).
- `src/components/bridge/SourceWireDragLayer.tsx` — re-export `resolveSourceWires` from `mapper/wireMath.ts`.
- `src/components/bridge/CommandPalette.tsx` — add mapper power commands (`a13..a17`) to `buildIndex`.
- `src/lib/section-guides.ts` — add `/connections/[connectionId]` mapper guide entry (the `/inbox/[orderId]` entry already exists).
- `src/lib/api-client.ts` — add `getOrderSourceTokens` if not present for the order path (confirm against existing `getSourceTokens`).
- `src/components/bridge/InboxView.tsx` — minor: ensure row click + status chips route into the redesigned review (mostly unchanged; verify list → open path).

---

## Parallel-safety map (read before dispatching)

| Group | Tasks | Parallel-safe? | Why |
|---|---|---|---|
| **A — Foundations** | 1 (shared types), 2 (wireMath extract + tests) | **Sequential, FIRST.** | Task 1 edits the shared `lib/api/types.ts`; Task 2 creates `wireMath.ts`. Everything else imports both. Do these before fan-out. |
| **B — Lanes** | 3 (SourceUniverse), 4 (CanonicalLane), 5 (TargetLane) | **Parallel** after A. | Separate new files; only share Task-1 types + Task-2 math. |
| **C — Wire engine + shell** | 6 (MapperWireLayer), 7 (ThreePaneMapper + useMapperModel) | **Sequential** (7 needs 6); after A+B. | Shell composes the lanes + engine. |
| **D — Enrichment** | 8 (GhostWire/AI), 9 (FieldBadges/catalog+validation), 10 (PreviewPane) | **Parallel** after C. | Separate files; each consumes a Phase-2 client (mock-fallback). |
| **E — Hosts** | 11 (SpineReview swap), 12 (ConnectionDetail mount) | **Parallel** after C (D optional). | Different host files. |
| **F — Power + polish** | 13 (command palette), 14 (section guide), 15 (states + deep-link), 16 (inbox list polish) | **Parallel** after E. | Independent files. |

**The ONE shared mutable file is `src/lib/api/types.ts` (Task 1).** After Task 1 lands, no other task edits it. `wireMath.ts` (Task 2) is created once then only imported. This is what makes B/D/F safe to parallelize.

---

### Task 1: Shared view-model types (the single shared-file edit) — *no Phase-2 dependency*

**Files:**
- Modify: `src/lib/api/types.ts` (append after the existing `OrderMappingOverride` block, ~line 109)

- [ ] **Step 1: Append the Phase-3 contract types**

These mirror the Phase-2 backend contracts (`CanonicalFieldDef`, AI suggestion, catalog price, validation). They are additive — nothing existing changes.

```ts
// ── Phase 3 — unified mapper view-model + Phase-2 engine contracts ────────────
// Additive. The mapper renders three lanes (source universe / canonical spine /
// target schema) and wires between them via the EXISTING OrderMappingOverride
// (sourceMap = source→canonical; output = canonical→target). These types describe
// the spine's extensibility (Tier-2 custom fields), AI suggestions, catalog
// enrichment, and validation — surfaced as the badges/ghost-wires in the UI.

/** "header" | "line" — which scope a custom canonical field lives in. */
export type CanonicalFieldScope = "header" | "line";

/**
 * A Tier-2 user-defined canonical field (Phase-2 `CanonicalFieldDef`). Added inline
 * in the mapper's "+ Add field"; removal soft-deletes. Scoped to org/connection.
 */
export interface CanonicalFieldDef {
  /** Stable key used as the canonical field NAME in OrderMappingOverride.sourceMap/output. */
  key: string;
  label: string;
  scope: CanonicalFieldScope;
  type: "string" | "number" | "date" | "bool";
  /** Optional standards reference shown on demand (e.g. "UBL cbc:ID"). */
  standardsRef?: string | null;
  order?: number;
  /** True for the built-in spine fields (not removable). */
  system?: boolean;
}

/**
 * An AI-proposed source→target (or source→canonical) mapping, rendered as a ghost
 * wire the user accepts/rejects. Reuses the catalog allow-list discipline (never
 * an invented value). `sourceId` is a SourceToken id or a canonical field key.
 */
export interface MappingSuggestion {
  /** Target/output field path OR canonical field key being suggested a source for. */
  targetKey: string;
  /** Suggested source: a SourceToken id (raw/structured) or a canonical field key. */
  sourceId: string;
  /** 0..1. Rendered as a confidence ring; coloring reuses confidenceTier(). */
  confidence: number;
  /** Short human reason ("label 'Ihre Materialnr' ~ manufacturerPartNumber"). */
  reason: string;
  /** "canonical" | "raw" | "custom" — provenance of the source. */
  sourceKind: "canonical" | "raw" | "custom";
}

/** Per-field validation outcome surfaced as a green/amber badge. */
export interface FieldValidationState {
  /** Field key/path this applies to (canonical key or output path). */
  key: string;
  state: "valid" | "review";
  /** Tooltip reason when state="review" (e.g. "City looks like a label: 'UIDNr'"). */
  reason?: string | null;
  /** True = blocks delivery; false = advisory only. */
  blocking?: boolean;
}

/** A catalog price/code suggestion for a resolved line (Phase-2 `catalog.*`). */
export interface CatalogPriceHint {
  /** Line key this applies to. */
  lineKey: string;
  catalogCode: string;
  catalogPrice: number | null;
  poPrice: number | null;
  /** (catalog - po)/po as a percentage; null when either price is missing. */
  variancePercent: number | null;
  currency?: string | null;
}
```

- [ ] **Step 2: Build to confirm the additive change compiles**

Run: `bun run build`
Expected: PASS (additive; no existing import breaks).

- [ ] **Step 3: Commit**

```bash
git add src/lib/api/types.ts
git commit -m "feat(mapper): Phase-3 shared view-model + Phase-2 engine contract types"
```

---

### Task 2: Extract `wireMath.ts` (one shared wire implementation + tests) — *no Phase-2 dependency*

**Files:**
- Create: `src/components/bridge/mapper/wireMath.ts`
- Create: `src/components/bridge/mapper/wireMath.test.ts`
- Modify: `src/components/bridge/WireDragLayer.tsx` (re-export from wireMath), `src/components/bridge/SourceWireDragLayer.tsx` (re-export `resolveSourceWires`)

- [ ] **Step 1: Write the failing test (move + widen the existing pure-logic tests)**

The current `WireDragLayer.test.ts` + `SourceWireDragLayer.test.ts` test `resolveWireSource` / `resolveSourceWires` against the hard-coded `NODE_TO_CANONICAL`. The unified engine makes the node↔canonical map a PARAMETER. Write the new test against the parameterized signatures:

```ts
import { describe, it, expect } from "vitest";
import { bezier, nearestZone, resolveWireSource, resolveSourceWires } from "./wireMath";

const NODE_TO_CANONICAL = { po: "PoNumber", currency: "Currency" } as const;

describe("resolveWireSource (parameterized node↔canonical map)", () => {
  it("identity when no override", () => {
    expect(resolveWireSource("po", null, NODE_TO_CANONICAL)).toEqual({ sourceNode: "po", isOverride: false });
  });
  it("re-points to the override's canonical node", () => {
    expect(resolveWireSource("po", "Currency", NODE_TO_CANONICAL)).toEqual({ sourceNode: "currency", isOverride: true });
  });
  it("falls back to lineId for an unknown canonical (custom field with no node)", () => {
    expect(resolveWireSource("po", "MadeUp", NODE_TO_CANONICAL)).toEqual({ sourceNode: "po", isOverride: false });
  });
});

describe("resolveSourceWires (stale-token-safe)", () => {
  it("draws only for tokens that still exist", () => {
    const sourceMap = { PoNumber: { sourceToken: "cell:r1c1", manipulators: [] }, Currency: { sourceToken: "gone", manipulators: [] } };
    const wires = resolveSourceWires(["PoNumber", "Currency"], sourceMap, new Set(["cell:r1c1"]));
    expect(wires).toEqual([{ nodeId: "PoNumber", canonicalField: "PoNumber", tokenId: "cell:r1c1" }]);
  });
});

describe("nearestZone (snap)", () => {
  it("returns the zone within SNAP_PX, else null", () => {
    const zones = [{ id: "a", x: 0, y: 0 }, { id: "b", x: 100, y: 0 }];
    expect(nearestZone(zones, 5, 5, 36)).toBe("a");
    expect(nearestZone(zones, 60, 60, 36)).toBeNull();
  });
});

describe("bezier", () => {
  it("emits a cubic path with clamped horizontal offset", () => {
    expect(bezier(0, 0, 200, 50)).toMatch(/^M 0 0 C 80 0 120 50 200 50$/);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `bun run test -- wireMath`
Expected: FAIL — `wireMath.ts` does not exist.

- [ ] **Step 3: Create `wireMath.ts` (lift the exact bodies from the two existing layers)**

Copy `bezier` (WireDragLayer.tsx:99-105) and `resolveWireSource` (54-60) verbatim, **generalizing** the node↔canonical map from the module constant to a parameter. Lift `resolveSourceWires` from `SourceWireDragLayer.tsx:69-89`. Add `nearestZone` (the body from WireDragLayer.tsx:215-219, made pure).

```ts
// One source of truth for the mapper's wire geometry + routing. Lifted verbatim from
// WireDragLayer.tsx (bezier:99-105, resolveWireSource:54-60, nearestZone:215-219) and
// SourceWireDragLayer.tsx (resolveSourceWires:69-89), generalized so the node↔canonical
// map is a PARAMETER instead of a module constant — that is the whole unify trick.

export interface Pt { id: string; x: number; y: number; }

export function bezier(x1: number, y1: number, x2: number, y2: number): string {
  const dx = x2 - x1;
  const off = Math.sign(dx || 1) * Math.max(24, Math.min(Math.abs(dx) * 0.5, 80));
  return `M ${x1} ${y1} C ${x1 + off} ${y1} ${x2 - off} ${y2} ${x2} ${y2}`;
}

export function resolveWireSource(
  lineId: string,
  overrideField: string | undefined | null,
  nodeToCanonical: Record<string, string>,
): { sourceNode: string; isOverride: boolean } {
  const canonicalToNode = Object.fromEntries(Object.entries(nodeToCanonical).map(([n, c]) => [c, n]));
  const sourceNode = overrideField ? (canonicalToNode[overrideField] ?? lineId) : lineId;
  return { sourceNode, isOverride: overrideField != null && sourceNode !== lineId };
}

export function resolveSourceWires(
  nodeIds: string[],
  sourceMap: Record<string, { sourceToken?: string | null } | undefined> | null | undefined,
  knownTokenIds: Set<string>,
): { nodeId: string; canonicalField: string; tokenId: string }[] {
  if (!sourceMap) return [];
  const out: { nodeId: string; canonicalField: string; tokenId: string }[] = [];
  for (const nodeId of nodeIds) {
    const rule = sourceMap[nodeId];
    const tokenId = rule?.sourceToken ?? null;
    if (tokenId && knownTokenIds.has(tokenId)) out.push({ nodeId, canonicalField: nodeId, tokenId });
  }
  return out;
}

export function nearestZone(zones: Pt[], x: number, y: number, snapPx: number): string | null {
  let best: string | null = null;
  let bestD = snapPx;
  for (const z of zones) {
    const d = Math.hypot(z.x - x, z.y - y);
    if (d < bestD) { bestD = d; best = z.id; }
  }
  return best;
}
```

- [ ] **Step 4: Re-export from the old layers (back-compat during migration)**

In `WireDragLayer.tsx`, replace the local `bezier` + `resolveWireSource` definitions with:
```ts
import { bezier, resolveWireSource as resolveWireSourceBase } from "./mapper/wireMath";
export function resolveWireSource(lineId: string, overrideField: string | undefined | null) {
  return resolveWireSourceBase(lineId, overrideField, NODE_TO_CANONICAL);
}
```
(Keep `NODE_TO_CANONICAL`/`OUTPUT_LINE_IDS` in `WireDragLayer.tsx` for now — Task 11 deletes the old triptych path. The wrapper preserves the existing test + call sites.) Do the equivalent re-export of `resolveSourceWires` in `SourceWireDragLayer.tsx`.

- [ ] **Step 5: Run to verify pass (new + existing wire tests green)**

Run: `bun run test -- wireMath WireDragLayer SourceWireDragLayer`
Expected: PASS — the existing layer tests still pass through the wrappers; the new parameterized tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/components/bridge/mapper/wireMath.ts src/components/bridge/mapper/wireMath.test.ts src/components/bridge/WireDragLayer.tsx src/components/bridge/SourceWireDragLayer.tsx
git commit -m "refactor(mapper): extract one wireMath (bezier/resolveWireSource/resolveSourceWires/nearestZone) + parameterize node map"
```

---

### Task 3: `SourceUniverse` — the anti-overwhelm field-discovery left pane — *no Phase-2 dependency (consumes Phase-1 SourceCapture tokens)*

**Files:**
- Create: `src/components/bridge/mapper/sourceUniverseModel.ts`, `src/components/bridge/mapper/SourceUniverse.tsx`, `src/components/bridge/mapper/SourceUniverse.test.ts`
- Create: `src/components/bridge/mapper/types.ts` (the `SourceField` view type lives here)

This EXTENDS `SourceTokenPanel.tsx` (which already does search + header/line split + collapse + value preview). The new pane adds: **Parties + Raw groups** (from the Phase-1 `SourceCapture` raw bag), **filter chips** (All/Unmapped/Mapped/AI/Has value), **raw bag collapsed by default**, **relevance ordering** (AI-suggested pin to top), **virtualization** when a group exceeds ~50 rows.

- [ ] **Step 1: Define the view type (`mapper/types.ts`)**

```ts
import type { SourceToken, MappingSuggestion, FieldValidationState, CatalogPriceHint, CanonicalFieldScope } from "@/lib/api/types";

/** A source field as shown in the SourceUniverse pane. Wraps a SourceToken with
 *  discovery state (group, mapped/suggested flags). Raw-bag tokens carry group="raw". */
export interface SourceField {
  id: string;
  label: string;
  value: string;
  group: "header" | "parties" | "line" | "raw";
  /** True when this token is already wired to a canonical/target field. */
  mapped: boolean;
  /** Present when AI proposed this token for some target. */
  suggestedFor?: string | null;
  suggestionConfidence?: number | null;
}

export type FieldFilter = "all" | "unmapped" | "mapped" | "ai" | "hasValue";

export interface CanonicalNode {
  /** id === canonical field key (PoNumber, …, or a Tier-2 custom key). */
  id: string;
  label: string;
  scope: CanonicalFieldScope;
  system: boolean;
  standardsRef?: string | null;
}

export interface TargetField {
  /** Output path in the delivered doc (e.g. "ItemCode"). */
  outputPath: string;
  label: string;
  scope: CanonicalFieldScope;
}

export type { MappingSuggestion, FieldValidationState, CatalogPriceHint };
```

- [ ] **Step 2: Write the failing test for the pure discovery helpers**

```ts
import { describe, it, expect } from "vitest";
import { groupSourceFields, filterSourceFields } from "./sourceUniverseModel";
import type { SourceField } from "./types";

const fields: SourceField[] = [
  { id: "h1", label: "po_number", value: "4730", group: "header", mapped: true },
  { id: "p1", label: "ship_to_city", value: "Linz", group: "parties", mapped: false, suggestedFor: "ShipToCity", suggestionConfidence: 0.8 },
  { id: "r1", label: "EDI id", value: "REDACTED-TAXID", group: "raw", mapped: false },
  { id: "r2", label: "cost centre", value: "", group: "raw", mapped: false },
];

describe("groupSourceFields", () => {
  it("buckets by group, raw last", () => {
    const g = groupSourceFields(fields);
    expect(g.map((x) => x.group)).toEqual(["header", "parties", "raw"]);
    expect(g.find((x) => x.group === "raw")!.fields).toHaveLength(2);
  });
});

describe("filterSourceFields", () => {
  it("search matches label OR value (case-insensitive)", () => {
    expect(filterSourceFields(fields, "995", "all").map((f) => f.id)).toEqual(["r1"]);
    expect(filterSourceFields(fields, "LINZ", "all").map((f) => f.id)).toEqual(["p1"]);
  });
  it("filter chips: unmapped / mapped / ai / hasValue", () => {
    expect(filterSourceFields(fields, "", "mapped").map((f) => f.id)).toEqual(["h1"]);
    expect(filterSourceFields(fields, "", "unmapped").map((f) => f.id).sort()).toEqual(["p1", "r1", "r2"]);
    expect(filterSourceFields(fields, "", "ai").map((f) => f.id)).toEqual(["p1"]);
    expect(filterSourceFields(fields, "", "hasValue").map((f) => f.id).sort()).toEqual(["h1", "p1", "r1"]);
  });
});
```

- [ ] **Step 3: Run to verify it fails**

Run: `bun run test -- sourceUniverseModel`
Expected: FAIL — module missing.

- [ ] **Step 4: Implement `sourceUniverseModel.ts`**

```ts
import type { SourceField, FieldFilter } from "./types";

const GROUP_ORDER = ["header", "parties", "line", "raw"] as const;

export function groupSourceFields(fields: SourceField[]): { group: SourceField["group"]; fields: SourceField[] }[] {
  return GROUP_ORDER
    .map((group) => ({ group, fields: fields.filter((f) => f.group === group) }))
    .filter((g) => g.fields.length > 0);
}

export function filterSourceFields(fields: SourceField[], query: string, filter: FieldFilter): SourceField[] {
  const q = query.trim().toLowerCase();
  return fields.filter((f) => {
    if (q && !(f.label.toLowerCase().includes(q) || f.value.toLowerCase().includes(q))) return false;
    switch (filter) {
      case "mapped":   return f.mapped;
      case "unmapped": return !f.mapped;
      case "ai":       return f.suggestedFor != null;
      case "hasValue": return f.value.trim().length > 0;
      default:         return true;
    }
  });
}
```

- [ ] **Step 5: Build the `SourceUniverse.tsx` component**

Reuse `SourceTokenPanel`'s chip visuals (the `TokenChip` styling at `SourceTokenPanel.tsx:30-76` — lift it into a shared `SourceFieldChip`). Add: group accordion (Radix `@radix-ui/react-accordion`, already imported via `components/ui/accordion.tsx`) with **Raw (N) collapsed by default**; a filter-chip row (5 chips, the locked button styling); a 150ms-debounced search; AI-suggested fields pinned to the top of their group with a faint ghost indicator. **Virtualize** any group with >50 rows using a simple windowed render (the grounding confirms ~50–100 token ceiling; use a `maxHeight + overflowY` window like the existing panel, and only mount a slice — no new dependency). Honor `touchAction: "pan-y"` on chips (drag-vs-scroll). The component is presentational: it takes `fields`, `filter`/`onFilter`, `query`/`onQuery`, and `chipProps(id)` (the drag handle props from the wire layer).

- [ ] **Step 6: Run + build**

Run: `bun run test -- sourceUniverseModel SourceUniverse && bun run build`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/components/bridge/mapper/sourceUniverseModel.ts src/components/bridge/mapper/SourceUniverse.tsx src/components/bridge/mapper/SourceUniverse.test.ts src/components/bridge/mapper/types.ts
git commit -m "feat(mapper): SourceUniverse field-discovery pane (groups/search/filters/raw-collapsed/virtualize)"
```

---

### Task 4: `CanonicalLane` — center spine with inline add/remove (Tier-2) — *depends on Phase-2 `CanonicalFieldDef` CRUD (mock-fallback)*

**Files:**
- Create: `src/lib/api/canonical-fields.ts`, `src/components/bridge/mapper/CanonicalLane.tsx`
- Test: extend `src/components/bridge/mapper/SourceUniverse.test.ts` or new `canonicalFields.test.ts` for the pure draft-merge helper

**Phase-2 dependency:** `GET/POST/DELETE /api/connections/{id}/canonical-fields` (or order-scoped equivalent). Until Phase 2 lands, `canonical-fields.ts` returns a mock list under `isApiMockMode` and the POST/DELETE are optimistic no-ops — the UI is fully exercisable.

- [ ] **Step 1: Write the typed client with mock-fallback (`canonical-fields.ts`)**

```ts
import type { CanonicalFieldDef } from "@/lib/api/types";
import { API_BASE_URL, authHeader, fetchWithTimeout, isApiMockMode } from "@/lib/api/core";

// Phase-2 backend: CanonicalFieldDef CRUD. Mock-fallback so Phase-3 ships before Phase-2.
const MOCK: CanonicalFieldDef[] = [];

export async function getCanonicalFields(scopeId: string): Promise<CanonicalFieldDef[]> {
  if (isApiMockMode) return MOCK;
  const res = await fetchWithTimeout(`${API_BASE_URL}/api/connections/${scopeId}/canonical-fields`, { headers: await authHeader() });
  if (res.status === 404) return []; // Phase-2 not deployed yet → graceful empty
  if (!res.ok) throw new Error(`canonical-fields: ${res.status}`);
  return res.json() as Promise<CanonicalFieldDef[]>;
}

export async function addCanonicalField(scopeId: string, def: Omit<CanonicalFieldDef, "system">): Promise<CanonicalFieldDef> {
  if (isApiMockMode) { const d = { ...def, system: false }; MOCK.push(d); return d; }
  const res = await fetchWithTimeout(`${API_BASE_URL}/api/connections/${scopeId}/canonical-fields`, {
    method: "POST", headers: { ...(await authHeader()), "Content-Type": "application/json" }, body: JSON.stringify(def),
  });
  if (!res.ok) throw new Error(`add canonical-field: ${res.status}`);
  return res.json() as Promise<CanonicalFieldDef>;
}

export async function removeCanonicalField(scopeId: string, key: string): Promise<void> {
  if (isApiMockMode) { const i = MOCK.findIndex((d) => d.key === key); if (i >= 0) MOCK.splice(i, 1); return; }
  const res = await fetchWithTimeout(`${API_BASE_URL}/api/connections/${scopeId}/canonical-fields/${encodeURIComponent(key)}`, {
    method: "DELETE", headers: await authHeader(),
  });
  if (!res.ok && res.status !== 404) throw new Error(`remove canonical-field: ${res.status}`);
}
```

- [ ] **Step 2: Write a failing test for the spine-merge helper**

A pure `mergeCanonicalNodes(system, custom)` returns `CanonicalNode[]` ordered system-first then custom-by-`order`, de-duped by key. Test it.

- [ ] **Step 3: Build `CanonicalLane.tsx`**

Render the existing spine node visuals (mirror `SpineReview`'s `SpineNodeCard` look — gradient blue→green spine line, node cards). Append a **"+ Add field"** affordance at the bottom: a small inline form (name + type select + optional standards ref) → `addCanonicalField` → optimistic insert as a wireable node. Each **custom** node gets an overflow `⋯` menu with "Remove" → `removeCanonicalField` (soft-delete; system nodes have no remove). Per-node info icon surfaces `standardsRef` on demand (reuse the existing `StandardsFieldPopover` referenced by `PoMappingEditor`). Use a `useMutation` + `queryClient.invalidateQueries(["canonical-fields", scopeId])`.

- [ ] **Step 4: Run + build**

Run: `bun run test -- canonicalFields && bun run build`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/lib/api/canonical-fields.ts src/components/bridge/mapper/CanonicalLane.tsx src/components/bridge/mapper/*.test.ts
git commit -m "feat(mapper): CanonicalLane with inline add/remove Tier-2 custom fields (mock-fallback client)"
```

---

### Task 5: `TargetLane` — prop-driven arbitrary output schema — *no Phase-2 dependency for the lane itself*

**Files:**
- Create: `src/components/bridge/mapper/TargetLane.tsx`

The current `WireDragLayer` hard-codes 7 output ids. The target list becomes a **prop** (`TargetField[]`). For the inbox order path, the target list is derived from the order's `OrderMappingOverride.output` rules (+ the canonical defaults). For the connection path, it's the declared target schema from the revision's `outputMappingJson`. The lane just renders the target field rows + their drop zones (the wire engine in Task 6 owns the SVG).

- [ ] **Step 1: Build `TargetLane.tsx`**

Each row: output path label (editable inline for the connection editor; read-only label for the order path), a drop-zone anchor (`ref` registered into the engine's `outLineEls`-equivalent), a slot for `FieldBadges` (Task 9) and manipulator pills (Task 9), and a "Fixed value" affordance (reuse `OutputMappingEditor`'s `RuleRow` fixed-value pattern). When `variant="connection"`, show a header "+ Add output field" + the **schema-source picker** stub (Standard / Sample / Import / Clone / AI — these populate from the Phase-2 declared-target-schema sources; for Phase 3, wire the menu and leave the non-`Standard` items behind a "coming soon"/disabled state until the Phase-2 endpoints exist, per offer⇔works).

- [ ] **Step 2: Build**

Run: `bun run build`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/components/bridge/mapper/TargetLane.tsx
git commit -m "feat(mapper): TargetLane (prop-driven arbitrary output schema + fixed-value + schema-source picker stub)"
```

---

### Task 6: `MapperWireLayer` — the generalized prop-driven drag/keyboard engine — *no Phase-2 dependency*

**Files:**
- Create: `src/components/bridge/mapper/MapperWireLayer.tsx`

This is `WireDragLayer` generalized: the node list, target/zone list, and node↔canonical map are **props**, not module constants. **Lift the entire measure/scroll/pointer/keyboard machinery verbatim** (`measure()` with `sigRef`/`nodesRef`, `useScrollResync`, `useDragAutoScroll`, `onHandleDown`/`onMove`/`onUp`, `onHandleKey`, `bezier` from `wireMath`, `nearestZone` from `wireMath`, the SR announcer, the halo/focus handling) — only the *sources of the id lists* change. The engine draws BOTH sides (source→canonical AND canonical→target) by accepting two banks of handles/zones, dispatching `onConnect` to the correct path (token→canonical vs canonical→target) based on which bank the drag started in.

- [ ] **Step 1: Build `MapperWireLayer.tsx`**

Signature (the unify shape):
```ts
interface MapperWireLayerProps {
  gridRef: React.RefObject<HTMLElement | null>;
  // Three lanes' element refs (mirrors SpineReview's nodeEls/outLineEls/srcSectionEls).
  sourceEls: React.MutableRefObject<Record<string, HTMLElement | null>>;
  canonicalEls: React.MutableRefObject<Record<string, HTMLElement | null>>;
  targetEls: React.MutableRefObject<Record<string, HTMLElement | null>>;
  canonicalNodes: CanonicalNode[];
  targetFields: TargetField[];
  /** outputPath → canonicalField (OrderMappingOverride.output). */
  outputConnections: Partial<Record<string, string>>;
  /** canonicalField → sourceToken id (OrderMappingOverride.sourceMap). */
  sourceConnections: Partial<Record<string, string>>;
  knownSourceTokenIds: Set<string>;
  /** Dispatch: a token dropped on a canonical node. */
  onSourceConnect: (tokenId: string, canonicalField: string) => void;
  /** Dispatch: a canonical node dropped on a target field. */
  onTargetConnect: (canonicalField: string, outputPath: string) => void;
  onSourceDisconnect: (canonicalField: string) => void;
  onTargetDisconnect: (outputPath: string) => void;
  /** AI ghost wires to overlay (Task 8). */
  suggestions?: MappingSuggestion[];
  onAcceptSuggestion?: (s: MappingSuggestion) => void;
  onRejectSuggestion?: (s: MappingSuggestion) => void;
  hoveredId?: string | null;
  hidden?: boolean;
  signature: string;
}
```
Build the `nodeToCanonical` map from `canonicalNodes` (`{ [n.id]: n.id }` — identity now that nodes ARE canonical keys; this is why `resolveWireSource`'s param generalization matters). Keep `SNAP_PX=36`, the `requestAnimationFrame` double-rAF schedule, and the "never blank to empty on a transient null-ref pass" guard from the original. Source→canonical wires use violet `#6F4FCE` (matches `SourceTokenPanel` chip accent); canonical→target wires keep blue-override `#1E66C9` / green-default `#2E8E3A`.

- [ ] **Step 2: Add an engine smoke test (jsdom-friendly: assert the pure dispatch, not layout)**

The measurement loop needs real layout (jsdom has no `getBoundingClientRect` geometry), so do NOT unit-test pixel positions. Instead assert the dispatch wiring via a small extracted pure `pickConnectTarget(bank, drag, zones, snap)` helper (reuses `nearestZone`) — test that a token-bank drag routes to `onSourceConnect` and a canonical-bank drag routes to `onTargetConnect`. Layout correctness is covered by the live QA step in Task 11/12.

- [ ] **Step 3: Run + build**

Run: `bun run test -- MapperWireLayer wireMath && bun run build`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/mapper/MapperWireLayer.tsx src/components/bridge/mapper/*.test.ts
git commit -m "feat(mapper): MapperWireLayer — prop-driven both-sides drag/keyboard engine (lifted from WireDragLayer)"
```

---

### Task 7: `ThreePaneMapper` shell + `useMapperModel` state hook — *no Phase-2 dependency for the shell (consumers inject Phase-2 data)*

**Files:**
- Create: `src/components/bridge/mapper/ThreePaneMapper.tsx`, `src/components/bridge/mapper/useMapperModel.ts`

- [ ] **Step 1: Build `useMapperModel.ts`**

The single state hook. Inputs: `{ variant: "order" | "connection"; scopeId: string /* orderId or connectionId */; supplierId?: string }`. It:
- loads `getMappingOverride(orderId)` (order) — `enabled: isApiMockMode || clerkReady`;
- loads source tokens (order: `getSourceTokens`/`getOrderSourceTokens`; the Phase-1 `SourceCapture` set now includes raw-bag fields — wrap each into a `SourceField` with `group`);
- loads `getCanonicalFields(scopeId)` (Task 4) and merges with the system spine;
- loads suggestions + catalog + validation (Tasks 8/9 clients, mock-fallback);
- derives `outputConnections` (from `override.output`), `sourceConnections` (from `override.sourceMap`), `knownSourceTokenIds`;
- exposes mutators that **persist via `upsertMappingOverride` ALWAYS carrying the existing `sourceMap` through `buildOverrideDraft`** (reuse `OutputMappingEditor.buildOverrideDraft` — do NOT hand-roll the draft or you reintroduce the documented sourceMap data-loss bug);
- bumps a `signature` string on every connection change so the wire engine re-measures.

```ts
// CRITICAL invariant (from OutputMappingEditor history): the PUT replaces the WHOLE
// OrderMappingOverride document. buildOverrideDraft() carries customFields + sourceMap +
// template through unchanged. Never construct the override by hand here.
```

- [ ] **Step 2: Build `ThreePaneMapper.tsx`**

Compose the shell on the locked primitives:
- `react-resizable-panels` (already installed) for three resizable panes: `SourceUniverse` │ `CanonicalLane` │ `TargetLane`, with `MapperWireLayer` absolutely positioned over the grid (mirror `SpineReview`'s `gridRef` + sticky columns + `position: relative` wrapper).
- A top action bar (Validate · Enrich · Manipulate · Preview · Deliver) — `Button` primitives; "Deliver" is `variant="primary"` (green) and **disabled until validation is green** (no blocking `review` badges).
- A collapsible `MapperPreviewPane` (Task 10) docked right/bottom.
- Mobile: render a read-only summary + the primary approve/deliver action (desktop-first power tool per spec; reuse `MobileListRow`).
- Deep-link: read `?field=` from `useSearchParams()` and scroll/focus that node; write it on selection (Task 15 finalizes states).

- [ ] **Step 3: Build**

Run: `bun run build`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/mapper/ThreePaneMapper.tsx src/components/bridge/mapper/useMapperModel.ts
git commit -m "feat(mapper): ThreePaneMapper shell + useMapperModel (carries sourceMap via buildOverrideDraft)"
```

---

### Task 8: AI ghost wires (accept/reject + confidence) — *depends on Phase-2 AI source-suggestion endpoint (mock-fallback)*

**Files:**
- Create: `src/lib/api/mapper-ai.ts`, `src/components/bridge/mapper/GhostWire.tsx`

**Phase-2 dependency:** `POST /api/orders/{id}/mapping-suggestions` (or connection-scoped) returning `MappingSuggestion[]`. Reuse the existing `suggestMappingFields` shape (mapping.ts) as the precedent. Mock-fallback returns `[]` (no ghost wires) so the mapper works without Phase 2; manual wiring is unaffected.

- [ ] **Step 1: Build `mapper-ai.ts` (mock-fallback)**

```ts
import type { MappingSuggestion } from "@/lib/api/types";
import { API_BASE_URL, authHeader, fetchWithTimeout, isApiMockMode } from "@/lib/api/core";

export async function getMappingSuggestions(orderId: string): Promise<MappingSuggestion[]> {
  if (isApiMockMode) return [];
  const res = await fetchWithTimeout(`${API_BASE_URL}/api/orders/${orderId}/mapping-suggestions`, { headers: await authHeader() });
  if (res.status === 404) return []; // Phase-2 not deployed → no ghost wires, manual still works
  if (!res.ok) throw new Error(`mapping-suggestions: ${res.status}`);
  return res.json() as Promise<MappingSuggestion[]>;
}
```

- [ ] **Step 2: Build `GhostWire.tsx`**

A dashed, faint bezier (reuse `wireMath.bezier`) with a confidence ring (reuse `confidenceTier` from `ds-tokens.ts` + `ConfidenceChip` colors) and a small ✓/✗ control at the target end. `onAccept` → calls the model's `onSourceConnect`/`onTargetConnect` (promotes the ghost to a real wire); `onReject` → removes the suggestion locally (and records the decision so the V9 calibration loop sees it — POST the existing `ai-suggestion-decisions` path if present, else no-op). Render ghost wires inside `MapperWireLayer`'s SVG (pass `suggestions` through, Task 6 already accepts them).

- [ ] **Step 3: Build**

Run: `bun run build`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/lib/api/mapper-ai.ts src/components/bridge/mapper/GhostWire.tsx
git commit -m "feat(mapper): AI ghost wires (accept/reject + confidence ring; mock-fallback)"
```

---

### Task 9: Catalog + validation badges + manipulator pills — *depends on Phase-2 catalog price + validation endpoints (mock-fallback); catalog read uses existing getSupplierCatalog*

**Files:**
- Create: `src/components/bridge/mapper/FieldBadges.tsx`
- Modify: `src/lib/api/mapper-ai.ts` (add `getFieldValidation` + `getCatalogHints`)

**Phase-2 dependency:** validation rules (`GET /api/orders/{id}/validation` → `FieldValidationState[]`) and catalog price variance (`GET /api/orders/{id}/catalog-hints` → `CatalogPriceHint[]`). The catalog list itself uses the **already-shipped** `getSupplierCatalog`. Mock-fallback returns `[]` (no badges) — fields render clean.

- [ ] **Step 1: Build `FieldBadges.tsx`**

Three inline badges using locked tokens:
- **teal catalog chip** (enriched-from-catalog) — `var(--brand-blue-soft)`-ish teal; tooltip shows catalog code/name;
- **green ✓ validated** — `UnifiedStatusBadge` success tone or a compact dot;
- **amber ⚠ review** — amber tone, tooltip shows `FieldValidationState.reason`; if `blocking`, it gates the Deliver button (Task 7).
- **catalog-price inline action** — when a `CatalogPriceHint.variancePercent` is set, render "Use catalog €X (PO €Y, +Z%)" as a one-click `Button variant="secondary" size="sm"` that writes a `Multiply`/`fixedValue` manipulator or sets the resolved price (a suggestion, never silent — per spec Decision 6).

- [ ] **Step 2: Manipulator pills on wires**

Lift `OutputMappingEditor`'s `ManipChip` (the `#EEE7FB`/`#DACEF3` pill with inline 60px param inputs, `MANIPULATOR_TYPES` from `types.ts`). Render a compact pill cluster ON the target row (anchored to the wire's `fx` point) for the field's `fieldManipulators`; a "+ transform" dropdown appends a manipulator. Reuse the exact param-edit semantics from `OutputMappingEditor.tsx:101-117` (module-level declaration to avoid the documented focus-loss bug — declare `ManipChip` at module scope).

- [ ] **Step 3: Build**

Run: `bun run build`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/mapper/FieldBadges.tsx src/lib/api/mapper-ai.ts
git commit -m "feat(mapper): catalog/validation badges + catalog-price action + manipulator pills (mock-fallback)"
```

---

### Task 10: Live preview pane — *no Phase-2 dependency (reuses shipped previewMappingOverride)*

**Files:**
- Create: `src/components/bridge/mapper/MapperPreviewPane.tsx`

- [ ] **Step 1: Build `MapperPreviewPane.tsx`**

Reuse `OutputMappingEditor`'s template-mode + ~400ms-debounced preview pattern (`previewMappingOverride(orderId, override, format)` at api-client `1465-1497`). Add: a format toggle (`PREVIEW_FORMATS` from `types.ts` — CSV/JSON/XML/cXML/UBL/X12 + the Scriban template content types), a **just-touched-field highlight** (pass the last-edited key from the model; highlight the matching token in the rendered text), copy + download buttons (reuse the existing artifact-download path), and the inline error surface for template-mode failures (the endpoint returns 200 `{ok:false,error}` — render amber, don't crash). For `variant="connection"`, preview runs against a sample/recent order via the replay/preview path (note: if no sample order exists, show the empty state, not an error).

- [ ] **Step 2: Build**

Run: `bun run build`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/components/bridge/mapper/MapperPreviewPane.tsx
git commit -m "feat(mapper): collapsible live-preview pane (format toggle + just-touched highlight + copy/download)"
```

---

### Task 11: Mount the unified mapper in the inbox (`SpineReview`) — *no Phase-2 dependency*

**Files:**
- Modify: `src/components/bridge/SpineReview.tsx` (`2330-2479` triptych body)

- [ ] **Step 1: Replace the triptych body with `ThreePaneMapper`**

`SpineReview` already loads `order`, `supplierMappings`, `catalogPage`, `mappingOverride` (lines `1410-1481`). Replace the hand-wired grid (`SpineConnectors` + `WireDragLayer` + `SourceTokenPanel`) at `2330-2479` with:
```tsx
<ThreePaneMapper
  variant="order"
  orderId={orderId}
  supplierId={order.supplierId}
  initialOverride={mappingOverride}
/>
```
Keep the surrounding `SpineReview` chrome (header, tabs, send/deliver gating, `useOrderReview` poll). The line-resolver (`ManualCodeRow`) stays as the per-line code-entry path — it lives inside the canonical lane's line node (the heart-piece). Keep the old `WireDragLayer`/`SpineConnectors`/`SourceTokenPanel` files for now (Task 2 wrappers keep them green); a later cleanup chip deletes them once both hosts are on the unified mapper.

- [ ] **Step 2: Type-check + build**

Run: `bun run build && bun run lint`
Expected: PASS.

- [ ] **Step 3: Live QA (manual — the layout/geometry gate vitest can't cover)**

Per the repo's local golden-path recipe (`PROCULINK_QA_BYPASS_AUTH`, local Postgres 5435, Worker running): upload a CSV → `/inbox/{orderId}` → confirm the three-pane mapper renders, source fields are discoverable (Header expanded, Raw collapsed), drag a source chip → canonical node, drag a canonical node → output field, the wire STAYS after persist, preview updates, Deliver gates on validation. Record the result in the PR description (offer⇔works).

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/SpineReview.tsx
git commit -m "feat(inbox): mount unified ThreePaneMapper in order review (replaces hand-wired triptych)"
```

---

### Task 12: Mount the unified mapper in the Supplier Connection editor — *no Phase-2 dependency (connection bundle types already exist)*

**Files:**
- Modify: `src/components/connections/ConnectionDetail.tsx`

- [ ] **Step 1: Add a "Mapping" section that mounts `ThreePaneMapper variant="connection"`**

`ConnectionDetail` already shows the revision history + `ReplayPanel`. Add a "Mapping" tab/section that, for the **draft** revision (`status === "draft"`), mounts:
```tsx
<ThreePaneMapper
  variant="connection"
  connectionId={connection.id}
  supplierId={connection.supplierId}
  // bundle carries inputMappingJson (source→canonical) + outputMappingJson (canonical→target)
/>
```
`useMapperModel` reads/writes the draft `ConnectionRevisionBundle` (`inputMappingJson`/`outputMappingJson`) instead of `OrderMappingOverride` when `variant="connection"` (one branch in the hook's load/persist). Author-once here; the inbox per-order path is the exception lane. Published revisions are immutable → the mapper is read-only when `status !== "draft"` (show a "Clone to a new draft to edit" CTA, reusing the existing `CreateConnectionRevisionRequest` flow).

- [ ] **Step 2: Build + lint**

Run: `bun run build && bun run lint`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/components/connections/ConnectionDetail.tsx
git commit -m "feat(connections): author the mapping once via ThreePaneMapper on the draft revision"
```

---

### Task 13: Command-palette power commands — *no Phase-2 dependency*

**Files:**
- Modify: `src/components/bridge/CommandPalette.tsx` (`buildIndex`, after `a12`)

- [ ] **Step 1: Add mapper commands**

Append to the hardcoded actions array (`CommandPalette.tsx:85-108`) — `a13..a17`, group `"Actions"`: "Jump to field" (opens the mapper field search / focuses a node via `?field=`), "Add a transform" (focuses the manipulator dropdown on the selected target), "Switch output format" (cycles `PREVIEW_FORMATS`), "Show standards mapping" (opens the standards popover for the focused field), "Add custom canonical field" (focuses the CanonicalLane add form). Each is a closure routing/dispatching via a small mapper event bus (a `window`-dispatched `CustomEvent("plk:mapper", {detail})` the mounted `ThreePaneMapper` listens for) so the palette stays decoupled from the mapper's React tree.

- [ ] **Step 2: Build + the existing CommandPalette tests**

Run: `bun run build && bun run test -- CommandPalette`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/components/bridge/CommandPalette.tsx
git commit -m "feat(palette): mapper power commands (jump-to-field/add-transform/switch-format/standards/add-field)"
```

---

### Task 14: First-visit SectionGuide for the connection mapper — *no Phase-2 dependency*

**Files:**
- Modify: `src/lib/section-guides.ts`

- [ ] **Step 1: Add the guide entry**

The `/inbox/[orderId]` guide already exists. Add a `SECTION_GUIDES` entry for `/connections/[connectionId]` (the connection mapper) — `title`, `purpose`, `bullets` (drag source→canonical→output, AI suggests mappings, add custom fields, raw bag is collapsed, preview before publish), `firstStep`, `articleSlugs` (point at the Mapping help category). `matchGuide` already handles the dynamic `[connectionId]` segment and the unseen-dot badge is automatic (`BridgeTopbar.tsx:320-332`). Use direction-aware `{Supplier}` tokens.

- [ ] **Step 2: Build + the section-guides test**

Run: `bun run build && bun run test -- section-guides`
Expected: PASS (the existing `section-guides.test.ts` validates the registry shape).

- [ ] **Step 3: Commit**

```bash
git add src/lib/section-guides.ts
git commit -m "feat(guides): first-visit section guide for the connection mapper route"
```

---

### Task 15: States + deep-link finalization — *no Phase-2 dependency*

**Files:**
- Modify: `src/components/bridge/mapper/ThreePaneMapper.tsx`, `src/components/bridge/mapper/SourceUniverse.tsx`, `src/components/bridge/mapper/MapperPreviewPane.tsx`

- [ ] **Step 1: Implement the full state set**

- **Empty** (no source): "Upload a doc or pick a sample" (reuse `SourceTokenPanel`'s honest empty-state wording for API-ingress orders).
- **Loading**: skeleton lanes + shimmer wires (a faint animated dashed bezier placeholder).
- **No-search-results**: the "No source field matches X — try Y" state already in `SourceTokenPanel`; carry it into `SourceUniverse`.
- **Extraction-failed**: surface the deterministic-fallback message + "map manually" — manual wiring still fully works.
- **AI-unavailable** (suggestions client returned `[]` / errored): no ghost wires, a subtle "AI suggestions unavailable" note, manual mapping unaffected.

- [ ] **Step 2: Deep-link**

`?order=`/`?connection=` opens the host; `?field=<key>` selects + scrolls to a node and focuses it (wire to the command-palette "Jump to field"). Write `?field=` on selection via `router.replace` (shallow) so the URL is shareable + restores state. Gate all queries on `isApiMockMode || clerkReady`.

- [ ] **Step 3: Build + lint + full test run**

Run: `bun run build && bun run lint && bun run test`
Expected: PASS (whole suite green).

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/mapper/
git commit -m "feat(mapper): empty/loading/no-results/extraction-failed/AI-unavailable states + ?field deep-link"
```

---

### Task 16: Inbox list polish (list → open → mapper) — *no Phase-2 dependency*

**Files:**
- Modify: `src/components/bridge/InboxView.tsx`

- [ ] **Step 1: Confirm the redesigned flow**

`InboxView` already paginates (`getOrders` page/pageSize), has status filter chips + bulk send, and rows route to `/inbox/[orderId]`. Verify: (a) the row click + keyboard open lands on the redesigned `SpineReview`/mapper; (b) status chips use `UnifiedStatusBadge`; (c) the mobile path uses `MobileListRow` (≥44px). This is the "classic view → list → open → three-pane mapper → preview → deliver" wiring — mostly verification + any leftover inline-style/responsive fixes (watch the documented `inline style defeats md:hidden` trap). No new visual language — locked Bridge primitives only.

- [ ] **Step 2: Build + lint + the inbox tests**

Run: `bun run build && bun run lint && bun run test -- inboxSend InboxView`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/components/bridge/InboxView.tsx
git commit -m "feat(inbox): confirm list→open→mapper flow on locked primitives (status badges, mobile rows)"
```

---

## Verification gate (every task)

- `bun run build` — Next.js production build + TypeScript type-check (the primary gate; jsdom can't measure wire geometry).
- `bun run lint` — `next lint`.
- `bun run test -- <pattern>` — vitest + @testing-library (pure logic: `wireMath`, `sourceUniverseModel`, `canonicalFields`, dispatch helpers; existing `WireDragLayer`/`SourceWireDragLayer`/`OutputMappingEditor`/`CommandPalette`/`section-guides` suites must stay green).
- **Live QA** for the geometry/drag the unit tests CAN'T cover — Tasks 11/12 carry the manual golden-path check (upload → mapper → drag both sides → persist → preview → deliver). Honest offer⇔works: record it in the PR.

Full final gate: `bun run build && bun run lint && bun run test`.

---

## Self-review

### Spec coverage (Layer D + Phase 3)
- **Three-pane unify** (merge SourceWireDragLayer + WireDragLayer + PoMappingEditor bezier/confidence; target list a PROP; source includes Phase-1 SourceCapture raw bag) → Tasks 2 (wireMath), 6 (MapperWireLayer prop-driven), 7 (shell), 3 (raw bag in SourceUniverse).
- **Field discovery / anti-overwhelm** (grouped collapsible Header/Parties/Lines/Raw, debounced search over labels AND values, filter chips All/Unmapped/Mapped/AI/Has value, raw collapsed, value previews, virtualization, relevance/AI pin) → Task 3.
- **AI ghost wires** (accept/reject + confidence, reuse suggest endpoint + confidence coloring) → Task 8.
- **Extensible canonical inline** ("+ Add field" → CanonicalFieldDef; remove via overflow) → Task 4.
- **Catalog + validation badges** (teal catalog / green validated / amber review + reason tooltip / catalog-price inline action) → Task 9.
- **Live preview + power** (template-mode + ~400ms preview; manipulator pills on wires; command-palette commands; section guide) → Tasks 10, 9, 13, 14.
- **Inbox redesign** (classic view → list → open → mapper → preview → deliver, locked primitives) → Tasks 11, 16.
- **States + deep-link** (empty/loading/no-results/extraction-failed/AI-unavailable; `?field` deep-link) → Task 15.
- **Reuse, not greenfield** — every task cites the existing component/prop it extends (WireDragLayer math, SourceTokenPanel chips/grouping, OutputMappingEditor buildOverrideDraft/ManipChip/preview, SpineReview grid, ConnectionDetail host, CommandPalette/section-guides patterns).

### Parallel-safety map
- **Sequential FIRST:** Task 1 (shared `types.ts`) → Task 2 (`wireMath.ts`). The only shared mutable file is `types.ts` (Task 1, once). After that, no task re-edits it.
- **Parallel after A:** Tasks 3/4/5 (separate lane files). 
- **Sequential:** 6 → 7 (shell needs engine).
- **Parallel after C:** 8/9/10 (enrichment, separate files). 
- **Parallel hosts:** 11/12 (different host files). 
- **Parallel polish:** 13/14/15/16.
- Worktree discipline: per the project memory (chips race on shared `.next`/EF snapshot — frontend twin: shared `.next`), run parallel agents in **isolated git worktrees**; gate each on its own `bun run build`, not an aggregate.

### Dependency on Phase-2 endpoints
| Task | Phase-2 endpoint | Build-now-against-mock? |
|---|---|---|
| 1, 2, 3, 5, 6, 7, 10, 11, 12, 13, 14, 15, 16 | **none** | n/a — ship now |
| 4 (CanonicalLane) | `GET/POST/DELETE /api/connections/{id}/canonical-fields` | **Yes** — `canonical-fields.ts` mock-fallback + 404→`[]` |
| 8 (GhostWire/AI) | `POST/GET /api/orders/{id}/mapping-suggestions` | **Yes** — `mapper-ai.ts` returns `[]`; manual wiring unaffected |
| 9 (badges) | `GET /api/orders/{id}/validation` + `…/catalog-hints` | **Yes** — `[]` → no badges; catalog list uses shipped `getSupplierCatalog` |

Every Phase-2-dependent client returns a safe empty under `isApiMockMode` and treats HTTP 404 as "Phase-2 not deployed → graceful empty", so Phase 3 ships and its tests pass before Phase 2 lands; the swap to the real endpoint is a no-op in the component (the client already calls it).

### Known anchors to confirm at execution (cited, not placeheld)
- `WireDragLayer.tsx:99-105` (`bezier`), `:54-60` (`resolveWireSource`), `:215-219` (`nearestZone`), `:138-198` (measure/ResizeObserver) — lifted verbatim into `wireMath.ts` + `MapperWireLayer`.
- `SourceWireDragLayer.tsx:69-89` (`resolveSourceWires`).
- `SourceTokenPanel.tsx:30-76` (`TokenChip`), `:86-129` (search/group/collapse) — extended by `SourceUniverse`.
- `OutputMappingEditor.tsx:67-93` (`buildOverrideDraft` — carry `sourceMap`), `:101-117` (`ManipChip`, module-level to avoid focus loss), `:410-463` (400ms preview).
- `SpineReview.tsx:2330-2479` (triptych body to replace), `:1410-1481` (queries to keep).
- `ConnectionDetail.tsx` + `connections/[connectionId]/page.tsx` (connection host); `ConnectionRevisionBundle.inputMappingJson/outputMappingJson` (types.ts:499-512).
- `CommandPalette.tsx:85-108` (`buildIndex` actions), `BridgeTopbar.tsx:335-344` (Cmd+K).
- `section-guides.ts:44` (`SECTION_GUIDES`), `:389-409` (`matchGuide`).
- api-client `1393-1497` (`getMappingOverride`/`upsertMappingOverride`/`previewMappingOverride`), `816-862` (`getSupplierCatalog`); `mapping.ts` (`getMappingSourceColumns`/`suggestMappingFields`); `core.ts:46-50` (`authHeader`/`fetchWithTimeout`).

### Repo rules honored
App Router only · `@clerk/nextjs` + `isApiMockMode || clerkReady` query gating · `next/navigation` (`useRouter`/`useSearchParams`) · **bun** only · all API via `src/lib/api-client.ts`/`src/lib/api/*` · no react-router/Vite · locked design tokens + shipped primitives (no new visual language) · no `localStorage` user-mode toggle (progressive disclosure via raw-collapsed + Cmd+K, per the One-Great-Experience rule).

### Ambiguity to resolve before/at execution
1. **Phase-2 endpoint shapes are assumed.** `CanonicalFieldDef` CRUD, `mapping-suggestions`, `validation`, `catalog-hints` URLs/payloads mirror the Phase-2 spec but aren't built yet — confirm against the Phase-2 plan when it lands; the mock-fallback clients absorb drift.
- 2. **Connection-path source tokens.** The inbox path has a real order with `SourceCapture` tokens; the connection (author-once) path has no single order. The mapper on a connection should preview/wire against a **sample or recent order** for that supplier (replay/preview path) — confirm the chosen sample-order source for connection-mode discovery.
- 3. **Old triptych deletion.** Tasks 2/11/12 keep `WireDragLayer`/`SourceWireDragLayer`/`SpineConnectors`/`SourceTokenPanel`/`PoMappingEditor` alive behind back-compat wrappers; a follow-up cleanup chip deletes them once both hosts are migrated and live-QA'd — not in this plan's scope.
- 4. **`getSourceTokens` vs an order-scoped variant.** Confirm the exact existing client name for loading an order's source tokens (grounding shows `getSourceTokens()` in api-client; verify the order-id signature) before Task 7.
```
