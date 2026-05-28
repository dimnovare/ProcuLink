# Group L Expanded — Dual-Persona UX + Magic Mapping + Per-Industry Templates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Do not execute this plan in the same session as it was written — the founder reviews phased plans before execution.**

**Goal:** Ship the Phase 6 Horizon 1 expanded Group L scope on top of the already-shipped trust/onboarding base — adding (1) a sticky Default/Expert persona toggle on every operational screen, (2) a magic mapping preview before order persistence, (3) per-industry starter templates wired into the onboarding wizard, (4) context-aware help routing + backfill of nine missing `/help` articles, and (5) the magic-mapping + persona + industry-template event family in the analytics taxonomy.

**Architecture:** Five phases, each independently mergeable. Phase 1 lays the persona foundation; Phase 2 builds the staging-backed magic mapping preview; Phase 3 layers per-industry templates onto the existing 4-step wizard; Phase 4 finishes the help surface; Phase 5 closes the analytics funnel. The persona toggle is a global `localStorage`-backed React context exposed via `usePersona()`. The magic mapping preview adds a new ephemeral `UploadStaging` entity that the upload flow targets BEFORE creating the real `PurchaseOrder`. Per-industry templates load from `ProcuLink.Api/Fixtures/templates/<industry>/`. Help context routing reads `pathname` and resolves the right `/help/<slug>` via a small lookup table. Analytics events extend the existing `IAnalyticsService` surface and the taxonomy doc.

**Tech Stack:**
- Frontend: Next.js 15 App Router, TypeScript, Tailwind, shadcn/ui, `@clerk/nextjs`, TanStack Query v5, MDX (existing in `(marketing)/help/`), `posthog-js`.
- Backend: ASP.NET Core 8, EF Core 8 + Npgsql, `IFileStorageService` (existing R2/local), `IAnalyticsService` + `PostHogAnalyticsService` (existing), Hangfire (existing).
- Tests: xUnit (`ProcuLink.Infrastructure.Tests`, `ProcuLink.Api.Tests`), Playwright (`project-proculink/e2e/`).
- Package manager: **bun** only — never npm.

**Constraints (re-stated):**
- All EF queries scoped by `organisationId`.
- `@clerk/nextjs` (not `@clerk/clerk-react`).
- Dual-persona is a non-negotiable invariant for every NEW screen (`CLAUDE.md` Phase 6+ rules).
- Standards-visibility (UBL / EDIFACT / X12 / cXML refs) must be possible in expert mode for any transform/mapping field. Source: `docs/standards-matrix.md`.
- Persona toggle copy is **"Default / Expert"** — never "Simple / Advanced".
- No new test fixtures may contain real customer or supplier data; use the Northwind-style synthetic names already used in `sample-order.csv`.
- Direction 4 — The Bridge Layer is the locked visual direction. Reuse `src/components/bridge/*` and existing design tokens (`#0B1A2F` navy, `#1E66C9` blue, `Bricolage Grotesque` headers, `Inter` body).
- No raw SQL; EF Core only.

**Already-shipped baseline (do NOT redo):**
- Wave 1: cookie banner + PostHog frontend SDK + identity events.
- Wave 2: 4-step onboarding wizard + sample-order endpoint + `/welcome`, `/watch`, `/help`, `/support`.
- Wave 3: backend `IAnalyticsService` + 8 emitters (org/supplier/upload/transform/delivery/billing). Verified in commit `b7fa374` + `0220fd8`.
- **Stripe webhook analytics wiring is ALREADY DONE** — `BillingController.cs:258-263, 300-305, 332-336` already invoke `EmitBillingUpgradedAsync` / `EmitBillingDowngradedAsync` / `EmitBillingCancelledAsync`. The Phase 6 roadmap statement that this is deferred is **stale**; Phase 5 of this plan only verifies + extends.

---

## Decisions taken before plan was written

### Dual-persona toggle

- **Location:** Top-right of `BridgeTopbar`, next to the existing Help button. Reachable from every operational screen. Also surfaced in `/settings` under a "Display" section for discoverability.
- **Persistence:** `localStorage` key `proculink_persona_v1` with values `"default" | "expert"`. Initial value: `"default"`.
- **Scope:** Global only. A screen MAY ignore the toggle when only one mode makes sense (the onboarding wizard is default-only; the `/standards` comparison screen — Horizon 2, out of scope here — would be expert-only).
- **Initialisation:** Wrapped in a `PersonaProvider` mounted in the `(app)` layout. SSR-safe (server renders `"default"`, client hydrates from storage in an effect — no `localStorage` access during render).
- **API:** Hook `usePersona(): [persona, setPersona]` and helper `isExpert(): boolean` for non-component code paths.
- **Telemetry:** Emits `persona_toggled` from frontend on every change, with `from` and `to` properties.

### Magic mapping preview

- **Entry point:** A new route `/upload/preview/[stagingId]` after upload but before order creation.
- **Contract:** Three endpoints:
  1. `POST /api/upload/preview` — accepts the file, parses headers + first 5 lines, computes deterministic mapping suggestions + (optional) AI suggestions, persists an `UploadStaging` row, and returns `{ stagingId, expiresAt, preview: { sourceFields, canonicalFields, supplierField, rows[] } }`.
  2. `POST /api/upload/preview/{stagingId}/commit` — accepts user-edited mapping decisions, creates the real `PurchaseOrder`, enqueues `ParseOrderJob`, returns `{ orderId }`.
  3. `DELETE /api/upload/preview/{stagingId}` — discards the staging row + the staged blob.
- **Auto-accept threshold:** None. Every row needs explicit Accept. Rows with `confidence >= 0.9` are pre-checked; the user can bulk-accept via a single button.
- **Staging TTL:** 24 hours. Hangfire `PurgeExpiredUploadStagingJob` runs hourly to delete expired rows + blobs.
- **Persistence shape:** New `UploadStagingEntity` (`Id`, `OrgId`, `FileName`, `BlobKey`, `MimeType`, `PreviewJson`, `CreatedAt`, `ExpiresAt`). New EF migration.
- **UI layout:** Side-by-side 3-column table on desktop (Source / Canonical / Supplier), stacked-row cards on mobile. Confidence + provenance render inline per row. The whole preview is keyboard-navigable (j/k row nav, space to toggle accept) in expert mode.

### Per-industry templates

- **Wizard placement:** New Step 0 ("Pick your industry") inserted BEFORE the existing 4-step wizard. Choosing an industry pre-populates suppliers + mappings + validation rules + a sample order tagged `IsSample=true`. A 5th "Other / I'll set up manually" option skips Step 0.
- **Idempotency:** Endpoint refuses with HTTP 409 if the org already has any non-sample supplier or mapping. The wizard surfaces a friendly "Looks like you've already started — pick suppliers manually" fallback in that case.
- **Industries shipped in Phase 6:** `industrial-distribution`, `food-and-beverage-wholesale`, `hospitality`, `healthcare-gpo`. Folder name = slug.
- **Fixture layout:** `ProcuLink.Api/Fixtures/templates/<slug>/manifest.json` + `suppliers/*.csv` + `mappings/*.json` + `rules/*.json` + `sample-order.csv`.
- **Manifest schema:** `{ "name", "summary", "industry", "suppliers": [...], "mappings": [...], "rules": [...], "sampleOrder": "sample-order.csv" }`.
- **Telemetry:** Emits `industry_template_selected` with the slug.

### Help completion

- **Routing:** A new client hook `useHelpArticle()` reads `usePathname()` and returns the matching slug via a small lookup table. The `BridgeTopbar` Help button passes the slug to `HelpSlideover`, which deep-links into the existing `/help/<slug>` content (rendered inside the slideover via dynamic import of the MDX route's exported `Article` component).
- **Backfill:** 9 new MDX articles — `dashboard`, `inbox-and-review`, `validation-rules`, `library-suppliers`, `library-buyers`, `library-templates`, `connectors`, `webhooks`, `inbound-documents`. Each follows the existing `/help/first-upload/page.mdx` layout (title, 2–4 sentence summary, bulleted "What this screen does", "Common questions", "Related articles" footer).
- **Default fallback:** Any unmatched pathname routes to `troubleshooting`.

### Analytics

- Verify the existing Stripe webhook → analytics wiring with a new `BillingControllerWebhookAnalyticsTests` test class (currently no integration test exists; only the `StripeBillingService` emit-method unit tests do).
- New events: `persona_toggled` (frontend), `industry_template_selected` (backend), `magic_mapping_preview_*` family (started / row_modified / accepted / rejected / committed, mixed frontend + backend), `help_slideover_opened` (frontend).
- Update `docs/analytics-event-taxonomy.md` to v1.1 with new events.

---

## File structure (high level)

### Backend (`ProcuLink/`)

**Create:**
- `ProcuLink.Core/Models/UploadStagingEntity.cs`
- `ProcuLink.Core/Services/IUploadStagingService.cs`
- `ProcuLink.Core/Services/IIndustryTemplateService.cs`
- `ProcuLink.Infrastructure/Services/UploadStagingService.cs`
- `ProcuLink.Infrastructure/Services/IndustryTemplateService.cs`
- `ProcuLink.Infrastructure/Jobs/PurgeExpiredUploadStagingJob.cs`
- `ProcuLink.Infrastructure/Migrations/<date>_AddUploadStaging.cs` (generated)
- `ProcuLink.Api/Controllers/UploadPreviewController.cs`
- `ProcuLink.Api/Controllers/IndustryTemplateController.cs` (or extend `OnboardingController`)
- `ProcuLink.Api/Fixtures/templates/industrial-distribution/manifest.json` + payload files
- `ProcuLink.Api/Fixtures/templates/food-and-beverage-wholesale/manifest.json` + payload files
- `ProcuLink.Api/Fixtures/templates/hospitality/manifest.json` + payload files
- `ProcuLink.Api/Fixtures/templates/healthcare-gpo/manifest.json` + payload files
- `ProcuLink.Infrastructure.Tests/Services/UploadStagingServiceTests.cs`
- `ProcuLink.Infrastructure.Tests/Services/IndustryTemplateServiceTests.cs`
- `ProcuLink.Infrastructure.Tests/Jobs/PurgeExpiredUploadStagingJobTests.cs`
- `ProcuLink.Api.Tests/Controllers/UploadPreviewControllerTests.cs`
- `ProcuLink.Api.Tests/Controllers/IndustryTemplateControllerTests.cs`
- `ProcuLink.Api.Tests/Controllers/BillingControllerWebhookAnalyticsTests.cs`

**Modify:**
- `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` (register `UploadStaging` DbSet + value converter)
- `ProcuLink.Worker/Program.cs` (schedule recurring purge job)
- `ProcuLink.Api/Program.cs` (DI: `IUploadStagingService`, `IIndustryTemplateService`)
- `ProcuLink.slnx` (no change expected — projects are already members)
- `docs/analytics-event-taxonomy.md` (v1.1 bump)
- `STATUS.md` (Group L expanded entry)

### Frontend (`project-proculink/`)

**Create:**
- `src/lib/persona.ts` (provider + hook + helper)
- `src/components/bridge/PersonaToggle.tsx`
- `src/app/(app)/upload/preview/[stagingId]/page.tsx`
- `src/components/bridge/MagicMappingPreview.tsx`
- `src/components/bridge/IndustryPicker.tsx` (Step 0 of wizard)
- `src/lib/help-router.ts` (pathname → slug lookup)
- `src/components/bridge/StandardsHint.tsx` (expert-mode inline standards badges)
- `src/app/(marketing)/help/dashboard/page.mdx`
- `src/app/(marketing)/help/inbox-and-review/page.mdx`
- `src/app/(marketing)/help/validation-rules/page.mdx`
- `src/app/(marketing)/help/library-suppliers/page.mdx`
- `src/app/(marketing)/help/library-buyers/page.mdx`
- `src/app/(marketing)/help/library-templates/page.mdx`
- `src/app/(marketing)/help/connectors/page.mdx`
- `src/app/(marketing)/help/webhooks/page.mdx`
- `src/app/(marketing)/help/inbound-documents/page.mdx`
- `e2e/persona-toggle.spec.ts` (Playwright)
- `e2e/magic-mapping-preview.spec.ts` (Playwright)

**Modify:**
- `src/app/(app)/layout.tsx` (mount `PersonaProvider`)
- `src/components/bridge/BridgeTopbar.tsx` (mount `PersonaToggle` + wire context-aware Help button)
- `src/components/bridge/HelpSlideover.tsx` (accept `slug` prop + dynamic MDX render)
- `src/components/bridge/OnboardingWizard.tsx` (insert Step 0)
- `src/components/bridge/UploadWorkbench.tsx` (redirect to `/upload/preview/[stagingId]` instead of `/orders/[id]`)
- `src/components/bridge/SpineReview.tsx` (apply expert-mode density + standards hints)
- `src/components/bridge/InboxView.tsx` (apply expert-mode density)
- `src/app/(app)/settings/page.tsx` (Display section with persona toggle)
- `src/lib/api-client.ts` (new methods: `uploadPreview`, `commitUploadPreview`, `discardUploadPreview`, `pickIndustryTemplate`)
- `src/lib/analytics.ts` (helpers: `trackPersonaToggled`, `trackHelpSlideoverOpened`, `trackMagicMappingPreviewStarted`, etc.)

---

## Phase 1 — Dual-Persona Foundation

The persona toggle is a global React context. Phase 1 lands the foundation + applies expert-mode density to two reference screens (`SpineReview` and `InboxView`) so later phases have a concrete pattern to copy from.

### Task 1.1 — Create persona context + hook

**Files:**
- Create: `project-proculink/src/lib/persona.ts`

- [ ] **Step 1: Create `persona.ts`**

```ts
"use client";

import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";

const STORAGE_KEY = "proculink_persona_v1";

export type Persona = "default" | "expert";

type PersonaContextValue = {
  persona: Persona;
  setPersona: (next: Persona) => void;
  isExpert: boolean;
};

const PersonaContext = createContext<PersonaContextValue | null>(null);

function readPersona(): Persona {
  if (typeof window === "undefined") return "default";
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return raw === "expert" ? "expert" : "default";
  } catch {
    return "default";
  }
}

export function PersonaProvider({ children }: { children: ReactNode }) {
  // SSR-safe: server renders "default", client hydrates from storage post-mount.
  const [persona, setPersonaState] = useState<Persona>("default");

  useEffect(() => {
    setPersonaState(readPersona());
  }, []);

  const setPersona = useCallback((next: Persona) => {
    setPersonaState(next);
    try {
      window.localStorage.setItem(STORAGE_KEY, next);
      window.dispatchEvent(new CustomEvent("proculink:persona", { detail: next }));
    } catch {
      // private/incognito mode — fail silently
    }
  }, []);

  return (
    <PersonaContext.Provider value={{ persona, setPersona, isExpert: persona === "expert" }}>
      {children}
    </PersonaContext.Provider>
  );
}

export function usePersona(): PersonaContextValue {
  const ctx = useContext(PersonaContext);
  if (!ctx) throw new Error("usePersona must be used inside <PersonaProvider>");
  return ctx;
}

// Non-component access (e.g. inside event handlers, fetch interceptors)
export function getPersonaSnapshot(): Persona {
  return readPersona();
}
```

- [ ] **Step 2: Commit**

```bash
cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink
git add src/lib/persona.ts
git commit -m "feat(persona): add PersonaProvider + usePersona hook with localStorage persistence"
```

### Task 1.2 — Mount `PersonaProvider` in `(app)` layout

**Files:**
- Modify: `project-proculink/src/app/(app)/layout.tsx`

- [ ] **Step 1: Read the file**

```bash
cat "src/app/(app)/layout.tsx"
```

Confirm the existing layout wraps `children` with `QueryClientProvider` (or similar). Identify the innermost wrapper.

- [ ] **Step 2: Add the import + provider**

Add to imports at top of `layout.tsx`:

```tsx
import { PersonaProvider } from "@/lib/persona";
```

Wrap the existing `children` so the order is `<PersonaProvider><...existing...>{children}</...existing...></PersonaProvider>`. PersonaProvider must be inside any `"use client"` boundary that already wraps the app.

- [ ] **Step 3: Verify build**

```bash
bun run build
```

Expected: build success, no new warnings.

- [ ] **Step 4: Commit**

```bash
git add "src/app/(app)/layout.tsx"
git commit -m "feat(persona): mount PersonaProvider in (app) layout"
```

### Task 1.3 — Build `PersonaToggle` component

**Files:**
- Create: `project-proculink/src/components/bridge/PersonaToggle.tsx`

- [ ] **Step 1: Create the component**

```tsx
"use client";

import { useEffect } from "react";
import { usePersona, type Persona } from "@/lib/persona";
import { trackPersonaToggled } from "@/lib/analytics";

type Props = {
  /** When true, render as a compact icon toggle (topbar). When false, render as a labelled radio group (settings). */
  compact?: boolean;
};

export function PersonaToggle({ compact = true }: Props) {
  const { persona, setPersona } = usePersona();

  // Hotkey: Shift+? toggles persona. Only registered when expert mode is already on
  // OR the user has discovered the hotkey overlay (out of scope here — bind globally).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.shiftKey && (e.key === "?" || e.key === "/")) {
        // Reserved for future "?" overlay; persona toggle uses Shift+E.
      }
      if (e.shiftKey && (e.key === "E" || e.key === "e")) {
        const next: Persona = persona === "expert" ? "default" : "expert";
        setPersona(next);
        trackPersonaToggled({ from: persona, to: next, via: "hotkey" });
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [persona, setPersona]);

  const toggle = (next: Persona) => {
    if (next === persona) return;
    setPersona(next);
    trackPersonaToggled({ from: persona, to: next, via: "click" });
  };

  if (compact) {
    return (
      <div
        role="group"
        aria-label="Display density"
        style={{
          display: "inline-flex",
          alignItems: "center",
          gap: 0,
          background: "#F1F3F7",
          borderRadius: 6,
          padding: 2,
          fontSize: 12,
          fontWeight: 600,
        }}
      >
        <button
          type="button"
          aria-pressed={persona === "default"}
          onClick={() => toggle("default")}
          style={{
            background: persona === "default" ? "#FFFFFF" : "transparent",
            color: persona === "default" ? "#0B1A2F" : "#56627A",
            border: "none",
            borderRadius: 4,
            padding: "4px 10px",
            cursor: "pointer",
            boxShadow: persona === "default" ? "0 1px 2px rgba(11,26,47,0.08)" : "none",
          }}
        >
          Default
        </button>
        <button
          type="button"
          aria-pressed={persona === "expert"}
          onClick={() => toggle("expert")}
          style={{
            background: persona === "expert" ? "#FFFFFF" : "transparent",
            color: persona === "expert" ? "#0B1A2F" : "#56627A",
            border: "none",
            borderRadius: 4,
            padding: "4px 10px",
            cursor: "pointer",
            boxShadow: persona === "expert" ? "0 1px 2px rgba(11,26,47,0.08)" : "none",
          }}
        >
          Expert
        </button>
      </div>
    );
  }

  // Verbose mode for /settings
  return (
    <fieldset style={{ border: "none", padding: 0, margin: 0 }}>
      <legend style={{ fontSize: 14, fontWeight: 600, color: "#0B1A2F", marginBottom: 8 }}>
        Interface density
      </legend>
      <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
        <label style={{ display: "flex", gap: 12, alignItems: "flex-start", cursor: "pointer" }}>
          <input
            type="radio"
            name="persona"
            value="default"
            checked={persona === "default"}
            onChange={() => toggle("default")}
            style={{ marginTop: 3 }}
          />
          <div>
            <div style={{ fontSize: 14, fontWeight: 600, color: "#0B1A2F" }}>Default</div>
            <div style={{ fontSize: 13, color: "#56627A", lineHeight: 1.5 }}>
              Wizards, templates, AI defaults, generous spacing. Recommended for new users.
            </div>
          </div>
        </label>
        <label style={{ display: "flex", gap: 12, alignItems: "flex-start", cursor: "pointer" }}>
          <input
            type="radio"
            name="persona"
            value="expert"
            checked={persona === "expert"}
            onChange={() => toggle("expert")}
            style={{ marginTop: 3 }}
          />
          <div>
            <div style={{ fontSize: 14, fontWeight: 600, color: "#0B1A2F" }}>Expert</div>
            <div style={{ fontSize: 13, color: "#56627A", lineHeight: 1.5 }}>
              Higher density, inline standards mappings (UBL / EDIFACT / X12 / cXML), hotkeys, raw view. Press <kbd style={{ background: "#F1F3F7", borderRadius: 3, padding: "1px 5px" }}>Shift+E</kbd> to toggle.
            </div>
          </div>
        </label>
      </div>
    </fieldset>
  );
}
```

- [ ] **Step 2: Commit**

```bash
git add src/components/bridge/PersonaToggle.tsx
git commit -m "feat(persona): add PersonaToggle component (compact + verbose modes)"
```

### Task 1.4 — Add `trackPersonaToggled` to analytics helper

**Files:**
- Modify: `project-proculink/src/lib/analytics.ts`

- [ ] **Step 1: Read existing `analytics.ts`**

```bash
cat src/lib/analytics.ts
```

Confirm the existing helpers (e.g. `trackEvent`, `identify`) and the consent-gating pattern. The new helpers must follow that pattern (no-op when consent is not `analytics-allowed`).

- [ ] **Step 2: Add the helpers**

Append at the bottom of `analytics.ts`:

```ts
export type PersonaToggleEvent = {
  from: "default" | "expert";
  to: "default" | "expert";
  via: "click" | "hotkey";
};

export function trackPersonaToggled(props: PersonaToggleEvent) {
  trackEvent("persona_toggled", props as Record<string, unknown>);
}

export function trackHelpSlideoverOpened(props: { slug: string; from_pathname: string }) {
  trackEvent("help_slideover_opened", props);
}

export function trackMagicMappingPreviewStarted(props: { staging_id: string; file_kind: "csv" | "xlsx" | "pdf" }) {
  trackEvent("magic_mapping_preview_started", props);
}

export function trackMagicMappingPreviewCommitted(props: { staging_id: string; rows_accepted: number; rows_edited: number; rows_rejected: number }) {
  trackEvent("magic_mapping_preview_committed", props);
}

export function trackMagicMappingPreviewRejected(props: { staging_id: string; reason: "user_cancelled" | "expired" }) {
  trackEvent("magic_mapping_preview_rejected", props);
}

export function trackIndustryTemplateSelected(props: { industry: string; via: "wizard" | "settings" }) {
  trackEvent("industry_template_selected", props);
}
```

(If `trackEvent` does not yet exist, mirror the existing `posthog.capture(...)` call you find in `analytics.ts`. The helpers above are a wrapper, not a re-implementation.)

- [ ] **Step 3: Commit**

```bash
git add src/lib/analytics.ts
git commit -m "feat(analytics): add persona/help/magic-mapping/industry-template tracking helpers"
```

### Task 1.5 — Mount `PersonaToggle` in `BridgeTopbar`

**Files:**
- Modify: `project-proculink/src/components/bridge/BridgeTopbar.tsx`

- [ ] **Step 1: Read the file**

```bash
cat src/components/bridge/BridgeTopbar.tsx | head -120
```

Identify the right-side action group (which currently holds the Help button and breadcrumb auto-render).

- [ ] **Step 2: Add the import + render**

At the top:

```tsx
import { PersonaToggle } from "@/components/bridge/PersonaToggle";
```

Inside the right-side action group, **before** the Help button:

```tsx
<PersonaToggle compact />
```

Use the existing flex/gap container; do not change its layout.

- [ ] **Step 3: Verify**

```bash
bun run build
```

Expected: build success. Visit `/bridge` in dev — toggle is visible top-right and clicking persists across reload.

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/BridgeTopbar.tsx
git commit -m "feat(persona): mount PersonaToggle in BridgeTopbar right-side action group"
```

### Task 1.6 — Build `StandardsHint` component for expert-mode inline labels

**Files:**
- Create: `project-proculink/src/components/bridge/StandardsHint.tsx`

- [ ] **Step 1: Create the component**

```tsx
"use client";

import { usePersona } from "@/lib/persona";

type StandardsRef = {
  ubl?: string;        // e.g. "cbc:ID"
  edifact?: string;    // e.g. "BGM 1004"
  x12?: string;        // e.g. "BEG03"
  cxml?: string;       // e.g. "OrderRequestHeader@orderID"
  peppol?: string;     // e.g. "Order/cbc:ID"
};

type Props = {
  /** Canonical PO model field name */
  field: string;
  refs: StandardsRef;
};

export function StandardsHint({ field, refs }: Props) {
  const { isExpert } = usePersona();
  if (!isExpert) return null;

  const items: { label: string; value: string }[] = [];
  if (refs.ubl)     items.push({ label: "UBL",     value: refs.ubl });
  if (refs.edifact) items.push({ label: "EDIFACT", value: refs.edifact });
  if (refs.x12)     items.push({ label: "X12",     value: refs.x12 });
  if (refs.cxml)    items.push({ label: "cXML",    value: refs.cxml });
  if (refs.peppol)  items.push({ label: "Peppol",  value: refs.peppol });

  if (items.length === 0) return null;

  return (
    <div
      role="note"
      aria-label={`Standards mapping for ${field}`}
      style={{
        display: "inline-flex",
        flexWrap: "wrap",
        gap: 6,
        marginTop: 4,
        fontSize: 11,
        lineHeight: 1.4,
      }}
    >
      {items.map((it) => (
        <span
          key={it.label}
          title={`${it.label}: ${it.value}`}
          style={{
            background: "#F1F3F7",
            color: "#3D4A5C",
            padding: "1px 6px",
            borderRadius: 4,
            fontFamily: "'JetBrains Mono', ui-monospace, monospace",
          }}
        >
          <strong style={{ color: "#1E66C9" }}>{it.label}</strong>
          {" "}
          {it.value}
        </span>
      ))}
    </div>
  );
}
```

- [ ] **Step 2: Commit**

```bash
git add src/components/bridge/StandardsHint.tsx
git commit -m "feat(persona): add StandardsHint component for expert-mode inline standards refs"
```

### Task 1.7 — Apply expert-mode density to `SpineReview`

**Files:**
- Modify: `project-proculink/src/components/bridge/SpineReview.tsx`

- [ ] **Step 1: Read the file**

```bash
cat src/components/bridge/SpineReview.tsx | head -80
```

Locate the per-row padding (likely a constant or inline style on each spine node).

- [ ] **Step 2: Add the import + hook**

```tsx
import { usePersona } from "@/lib/persona";
import { StandardsHint } from "@/components/bridge/StandardsHint";
```

Inside the component:

```tsx
const { isExpert } = usePersona();
const rowPadding = isExpert ? "8px 12px" : "16px 20px";
const fontSize = isExpert ? 12.5 : 14;
```

- [ ] **Step 3: Apply density to each spine node row**

Replace hardcoded padding/font-size on each row container with `rowPadding` and `fontSize`.

- [ ] **Step 4: Add a `StandardsHint` next to each canonical field name**

For each canonical field (PO number, buyer name, line item code, etc.), emit:

```tsx
<StandardsHint
  field="PO number"
  refs={{ ubl: "cbc:ID", edifact: "BGM 1004", x12: "BEG03", cxml: "OrderRequestHeader@orderID" }}
/>
```

(The exact `refs` per field come from `docs/standards-matrix.md` § "Canonical PO Model fields". If a field's mapping is not yet documented, leave the prop empty — `StandardsHint` no-ops on empty refs.)

- [ ] **Step 5: Verify**

```bash
bun run build
```

Open `/orders/<id>` (use any existing order). Toggle persona to Expert via topbar — rows collapse, standards badges appear inline. Toggle back to Default — rows expand, badges hide.

- [ ] **Step 6: Commit**

```bash
git add src/components/bridge/SpineReview.tsx
git commit -m "feat(persona): apply expert-mode density + inline standards refs to SpineReview"
```

### Task 1.8 — Apply expert-mode density to `InboxView`

**Files:**
- Modify: `project-proculink/src/components/bridge/InboxView.tsx`

- [ ] **Step 1: Read the file**

```bash
cat src/components/bridge/InboxView.tsx | head -60
```

Identify the table row height + visible columns.

- [ ] **Step 2: Add hook + conditional columns**

```tsx
import { usePersona } from "@/lib/persona";

// inside component:
const { isExpert } = usePersona();
const rowHeight = isExpert ? 34 : 56;
const showRawColumns = isExpert; // shows columns hidden in default mode (e.g. PO Number, Supplier ID, raw status)
```

- [ ] **Step 3: Apply density to table rows + reveal raw columns when expert**

Wrap any "expert-only" column header + cell with `{showRawColumns && <th>...</th>}`. Apply `rowHeight` to each row's inline style.

- [ ] **Step 4: Verify**

```bash
bun run build
```

Open `/inbox`. Toggle persona — rows compress and extra columns appear.

- [ ] **Step 5: Commit**

```bash
git add src/components/bridge/InboxView.tsx
git commit -m "feat(persona): apply expert-mode density + raw columns to InboxView"
```

### Task 1.9 — Add Display section to `/settings`

**Files:**
- Modify: `project-proculink/src/app/(app)/settings/page.tsx`

- [ ] **Step 1: Add the section**

Locate the existing tab structure (Email / Billing / API Keys / Connectors). Add a new "Display" tab OR a "Display" section above the existing tabs. Mount the verbose toggle:

```tsx
import { PersonaToggle } from "@/components/bridge/PersonaToggle";

// inside the relevant tab body:
<section style={{ padding: "24px 0", borderTop: "1px solid #E2E6EE" }}>
  <PersonaToggle compact={false} />
</section>
```

- [ ] **Step 2: Verify**

```bash
bun run build
```

- [ ] **Step 3: Commit**

```bash
git add "src/app/(app)/settings/page.tsx"
git commit -m "feat(persona): expose verbose PersonaToggle in /settings Display section"
```

### Task 1.10 — Playwright smoke test for persona toggle

**Files:**
- Create: `project-proculink/e2e/persona-toggle.spec.ts`

- [ ] **Step 1: Write the test**

```ts
import { test, expect } from "@playwright/test";

test.describe("Persona toggle", () => {
  test("toggle persists across navigation and reload", async ({ page, context }) => {
    // Sign-in bypass uses the existing Playwright QA bypass route.
    await page.goto("/bridge");
    await expect(page.getByRole("group", { name: "Display density" })).toBeVisible();

    // Initially default.
    const expertBtn = page.getByRole("button", { name: "Expert" });
    await expect(expertBtn).toHaveAttribute("aria-pressed", "false");

    // Click expert.
    await expertBtn.click();
    await expect(expertBtn).toHaveAttribute("aria-pressed", "true");

    // Navigate away and back.
    await page.goto("/inbox");
    await expect(page.getByRole("button", { name: "Expert" })).toHaveAttribute("aria-pressed", "true");

    // Reload.
    await page.reload();
    await expect(page.getByRole("button", { name: "Expert" })).toHaveAttribute("aria-pressed", "true");
  });

  test("Shift+E hotkey toggles persona", async ({ page }) => {
    await page.goto("/bridge");
    await page.keyboard.press("Shift+E");
    await expect(page.getByRole("button", { name: "Expert" })).toHaveAttribute("aria-pressed", "true");
    await page.keyboard.press("Shift+E");
    await expect(page.getByRole("button", { name: "Default" })).toHaveAttribute("aria-pressed", "true");
  });
});
```

- [ ] **Step 2: Run the test**

```bash
bunx playwright test e2e/persona-toggle.spec.ts --reporter=line
```

Expected: both tests pass against the running dev server. If the QA bypass is not present, the test should still run against an authenticated session in the local dev environment.

- [ ] **Step 3: Commit**

```bash
git add e2e/persona-toggle.spec.ts
git commit -m "test(e2e): persona toggle persists across navigation/reload + Shift+E hotkey"
```

### Task 1.11 — Phase 1 review + merge

- [ ] **Step 1: Run `/code-review`**

In a fresh Claude Code session, run `/code-review` on the Phase 1 diff. Confirm:
- Persona context is SSR-safe (no `localStorage` access during render).
- `PersonaToggle` aria attributes are correct.
- `StandardsHint` no-ops when `refs` is empty.
- Expert-mode density does not break existing layouts on mobile (375px width).

- [ ] **Step 2: Address review findings**

Fix any blocker findings; document non-blocker findings as separate chips.

- [ ] **Step 3: Open Phase 1 PR**

```bash
git push -u origin plan/group-l-expanded-dual-persona-magic-mapping
gh pr create --title "feat(persona): dual-persona UX foundation (Phase 6 / Group L expanded Phase 1)" --body "..."
```

PR body includes the Decisions section from this plan (Task 1.x summary), a screenshot of the toggle in both states, and a link to this plan file.

---

## Phase 2 — Magic Mapping Preview

The preview lives at `/upload/preview/[stagingId]`. The user uploads a file, the backend stages it (no `PurchaseOrder` created yet), parses headers, returns a mapping preview with AI suggestions. The user accepts/edits/rejects per row, then commits — only then is the real `PurchaseOrder` created.

### Task 2.1 — Add `UploadStagingEntity` + EF migration

**Files:**
- Create: `ProcuLink/ProcuLink.Core/Models/UploadStagingEntity.cs`
- Modify: `ProcuLink/ProcuLink.Infrastructure/ProcuLinkDbContext.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure/Migrations/<date>_AddUploadStaging.cs` (via `dotnet ef migrations add`)

- [ ] **Step 1: Create `UploadStagingEntity.cs`**

```csharp
using System.Text.Json;

namespace ProcuLink.Core.Models;

/// <summary>
/// Ephemeral staged upload — file is in storage, headers parsed, AI suggestions
/// computed, but no PurchaseOrder exists yet. Lives for 24h, purged by Hangfire.
/// </summary>
public sealed class UploadStagingEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrgId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string BlobKey { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;

    /// <summary>
    /// JSON-serialised <see cref="MagicMappingPreview"/>. Stored as jsonb.
    /// </summary>
    public JsonDocument? PreviewJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);
}

public sealed record MagicMappingPreview(
    IReadOnlyList<string> SourceFields,
    IReadOnlyList<string> CanonicalFields,
    IReadOnlyList<MagicMappingRow> Rows);

public sealed record MagicMappingRow(
    string SourceField,
    string? CanonicalField,
    string? SupplierField,
    MagicMappingSuggestion? Suggestion);

public sealed record MagicMappingSuggestion(
    double Confidence,
    string Source,   // "deterministic" | "ai_openai" | "manual"
    string Reason);
```

- [ ] **Step 2: Register in `ProcuLinkDbContext`**

Open `ProcuLink.Infrastructure/ProcuLinkDbContext.cs`. Add:

```csharp
public DbSet<UploadStagingEntity> UploadStagings => Set<UploadStagingEntity>();
```

Inside `OnModelCreating`, add the JsonDocument value converter (mirror the existing pattern for other jsonb columns):

```csharp
modelBuilder.Entity<UploadStagingEntity>(b =>
{
    b.ToTable("upload_stagings");
    b.HasKey(x => x.Id);
    b.HasIndex(x => x.OrgId);
    b.HasIndex(x => x.ExpiresAt);
    b.Property(x => x.PreviewJson).HasColumnType("jsonb").HasConversion(JsonDocumentConverter);
});
```

(Reuse the existing `JsonDocumentConverter` constant defined at the top of `OnModelCreating` — created during the 2026-05-28 fix.)

- [ ] **Step 3: Update test-double `Ignore` lists**

In `ProcuLink.Infrastructure.Tests` and `ProcuLink.Api.Tests`, find any `DbContextOptionsBuilder` setup that uses EF InMemory + ignores specific properties. Add `UploadStagingEntity.PreviewJson` to the relevant Ignore list (mirrors what was done for Wave 3/4 entities).

- [ ] **Step 4: Add the migration**

```bash
cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink/ProcuLink.Infrastructure
dotnet ef migrations add AddUploadStaging --startup-project ../ProcuLink.Api/ProcuLink.Api.csproj
```

Inspect the generated migration `Up()`: should create `upload_stagings` with `id (uuid PK)`, `org_id (uuid)`, `file_name (text)`, `blob_key (text)`, `mime_type (text)`, `preview_json (jsonb null)`, `created_at (timestamptz)`, `expires_at (timestamptz)`, + two indices.

- [ ] **Step 5: Apply migration to dev DB**

```bash
cd ../ProcuLink.Api
dotnet ef database update
```

- [ ] **Step 6: Build solution**

```bash
cd ..
dotnet build ProcuLink.slnx --no-restore
```

Expected: success.

- [ ] **Step 7: Commit**

```bash
git add ProcuLink.Core/Models/UploadStagingEntity.cs ProcuLink.Infrastructure/ProcuLinkDbContext.cs ProcuLink.Infrastructure/Migrations
git commit -m "feat(upload): add UploadStaging entity + EF migration with 24h expiry"
```

### Task 2.2 — Build `IUploadStagingService` + tests (TDD)

**Files:**
- Create: `ProcuLink/ProcuLink.Core/Services/IUploadStagingService.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure/Services/UploadStagingService.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure.Tests/Services/UploadStagingServiceTests.cs`

- [ ] **Step 1: Define the interface**

```csharp
using ProcuLink.Core.Models;

namespace ProcuLink.Core.Services;

public interface IUploadStagingService
{
    /// <summary>
    /// Stages an uploaded file: stores the blob, parses headers + first 5 lines,
    /// computes deterministic + AI mapping suggestions, persists the staging row.
    /// </summary>
    Task<UploadStagingEntity> StageAsync(
        Guid organisationId,
        string fileName,
        string mimeType,
        Stream content,
        CancellationToken ct = default);

    /// <summary>
    /// Loads a staging row, ensuring org-scoped access. Returns null if not found,
    /// belongs to a different org, or already expired.
    /// </summary>
    Task<UploadStagingEntity?> GetAsync(
        Guid organisationId,
        Guid stagingId,
        CancellationToken ct = default);

    /// <summary>
    /// Commits the staging: creates the real PurchaseOrder using the user's mapping
    /// decisions, enqueues ParseOrderJob, deletes the staging row (but keeps the blob
    /// — it becomes the PO's source artifact).
    /// </summary>
    Task<Guid> CommitAsync(
        Guid organisationId,
        Guid stagingId,
        CommitMappingDecisions decisions,
        CancellationToken ct = default);

    /// <summary>
    /// Discards the staging row + deletes the staged blob. Idempotent.
    /// </summary>
    Task DiscardAsync(
        Guid organisationId,
        Guid stagingId,
        CancellationToken ct = default);
}

public sealed record CommitMappingDecisions(
    IReadOnlyList<CommitMappingRow> Rows);

public sealed record CommitMappingRow(
    string SourceField,
    string? AcceptedCanonicalField,
    string? AcceptedSupplierField,
    bool Accepted);
```

- [ ] **Step 2: Write failing tests**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Models;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class UploadStagingServiceTests
{
    private static ProcuLinkDbContext NewDb() =>
        new ProcuLinkDbContextFactory().CreateInMemory();

    [Fact]
    public async Task StageAsync_PersistsRow_AndReturnsPreview()
    {
        using var db = NewDb();
        var orgId = Guid.NewGuid();
        var svc = BuildSvc(db);

        var entity = await svc.StageAsync(
            orgId, "test.csv", "text/csv",
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes("po_number,supplier_code\nPO-1,SUP-A")));

        Assert.NotEqual(Guid.Empty, entity.Id);
        Assert.Equal(orgId, entity.OrgId);
        Assert.True(entity.ExpiresAt > entity.CreatedAt);
        Assert.NotNull(entity.PreviewJson);
        Assert.Single(await db.UploadStagings.ToListAsync());
    }

    [Fact]
    public async Task GetAsync_DoesNotLeakAcrossOrgs()
    {
        using var db = NewDb();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var svc = BuildSvc(db);

        var staged = await svc.StageAsync(orgA, "f.csv", "text/csv", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("a,b\n1,2")));

        Assert.Null(await svc.GetAsync(orgB, staged.Id, default));
        Assert.NotNull(await svc.GetAsync(orgA, staged.Id, default));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenExpired()
    {
        using var db = NewDb();
        var orgId = Guid.NewGuid();
        var svc = BuildSvc(db);

        var staged = await svc.StageAsync(orgId, "f.csv", "text/csv", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("a,b\n1,2")));
        staged.ExpiresAt = DateTime.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();

        Assert.Null(await svc.GetAsync(orgId, staged.Id, default));
    }

    [Fact]
    public async Task CommitAsync_CreatesPurchaseOrder_AndDeletesStaging()
    {
        using var db = NewDb();
        var orgId = Guid.NewGuid();
        var svc = BuildSvc(db);

        var staged = await svc.StageAsync(orgId, "f.csv", "text/csv", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("po,sup\nPO-1,A")));
        var orderId = await svc.CommitAsync(orgId, staged.Id,
            new CommitMappingDecisions(new[] {
                new CommitMappingRow("po", "PoNumber", null, Accepted: true),
                new CommitMappingRow("sup", null, "SupplierCode", Accepted: true),
            }), default);

        Assert.NotEqual(Guid.Empty, orderId);
        Assert.Single(await db.PurchaseOrders.ToListAsync());
        Assert.Empty(await db.UploadStagings.ToListAsync());
    }

    [Fact]
    public async Task DiscardAsync_RemovesRow_AndIsIdempotent()
    {
        using var db = NewDb();
        var orgId = Guid.NewGuid();
        var svc = BuildSvc(db);

        var staged = await svc.StageAsync(orgId, "f.csv", "text/csv", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("a\n1")));
        await svc.DiscardAsync(orgId, staged.Id, default);
        await svc.DiscardAsync(orgId, staged.Id, default); // idempotent

        Assert.Empty(await db.UploadStagings.ToListAsync());
    }

    private static UploadStagingService BuildSvc(ProcuLinkDbContext db) => new(
        db,
        new InMemoryFileStorageService(),
        new FakeAiMappingService(),
        new FakeParseJobEnqueuer(),
        new FakeAnalyticsService(),
        NullLogger<UploadStagingService>.Instance);
}
```

(Adapt the test-double types to whatever already exists in `ProcuLink.Infrastructure.Tests/TestDoubles/`. The existing `FakeAnalyticsService`, `InMemoryFileStorageService`, and `FakeParseJobEnqueuer` should be reused. If `FakeAiMappingService` does not exist, add a minimal one in the same folder.)

- [ ] **Step 3: Run tests — expect failure**

```bash
cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink
dotnet test ProcuLink.Infrastructure.Tests --no-restore --filter UploadStagingServiceTests
```

Expected: compile errors — `UploadStagingService` not defined.

- [ ] **Step 4: Implement `UploadStagingService.cs`**

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Models;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Infrastructure.Services;

public sealed class UploadStagingService : IUploadStagingService
{
    private static readonly TimeSpan StagingTtl = TimeSpan.FromHours(24);

    private readonly ProcuLinkDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly IAiMappingService _ai;
    private readonly IParseJobEnqueuer _enqueuer;
    private readonly IAnalyticsService _analytics;
    private readonly ILogger<UploadStagingService> _log;

    public UploadStagingService(
        ProcuLinkDbContext db,
        IFileStorageService storage,
        IAiMappingService ai,
        IParseJobEnqueuer enqueuer,
        IAnalyticsService analytics,
        ILogger<UploadStagingService> log)
    {
        _db = db; _storage = storage; _ai = ai; _enqueuer = enqueuer;
        _analytics = analytics; _log = log;
    }

    public async Task<UploadStagingEntity> StageAsync(
        Guid organisationId,
        string fileName,
        string mimeType,
        Stream content,
        CancellationToken ct = default)
    {
        // 1. Buffer + persist to storage under staging/ prefix.
        var blobKey = $"staging/{organisationId}/{Guid.NewGuid():N}/{fileName}";
        await _storage.PutAsync(blobKey, content, mimeType, ct);

        // 2. Re-open for header parsing (Stream is now at end).
        await using var reread = await _storage.OpenReadAsync(blobKey, ct);
        var preview = await ComputePreviewAsync(reread, fileName, mimeType, ct);

        var entity = new UploadStagingEntity
        {
            OrgId = organisationId,
            FileName = fileName,
            BlobKey = blobKey,
            MimeType = mimeType,
            PreviewJson = JsonSerializer.SerializeToDocument(preview),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(StagingTtl),
        };
        _db.UploadStagings.Add(entity);
        await _db.SaveChangesAsync(ct);

        return entity;
    }

    public async Task<UploadStagingEntity?> GetAsync(
        Guid organisationId,
        Guid stagingId,
        CancellationToken ct = default)
    {
        return await _db.UploadStagings
            .Where(x => x.Id == stagingId && x.OrgId == organisationId && x.ExpiresAt > DateTime.UtcNow)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Guid> CommitAsync(
        Guid organisationId,
        Guid stagingId,
        CommitMappingDecisions decisions,
        CancellationToken ct = default)
    {
        var staging = await GetAsync(organisationId, stagingId, ct)
            ?? throw new InvalidOperationException($"Staging {stagingId} not found or expired.");

        // Create the real PO using the staged blob as source artifact.
        var order = new PurchaseOrderEntity
        {
            Id = Guid.NewGuid(),
            OrgId = organisationId,
            SourceBlobKey = staging.BlobKey, // keep blob, just untag from staging
            OriginalFileName = staging.FileName,
            Status = OrderStatusConstants.Uploaded,
            CreatedAt = DateTime.UtcNow,
            IsSample = false,
        };
        _db.PurchaseOrders.Add(order);

        // Persist user mapping decisions onto the order (or onto a per-supplier mapping
        // record — mirror the existing PoMappingService pattern; do not duplicate it here).
        // For Phase 6, we attach decisions as JSON metadata on the order until the
        // existing PoMappingService is refactored to accept staging-time decisions.
        order.MappingDecisionsJson = JsonSerializer.SerializeToDocument(decisions);

        _db.UploadStagings.Remove(staging);
        await _db.SaveChangesAsync(ct);

        await _enqueuer.EnqueueParseAsync(order.Id, ct);

        return order.Id;
    }

    public async Task DiscardAsync(
        Guid organisationId,
        Guid stagingId,
        CancellationToken ct = default)
    {
        var staging = await _db.UploadStagings
            .FirstOrDefaultAsync(x => x.Id == stagingId && x.OrgId == organisationId, ct);
        if (staging is null) return; // idempotent

        try { await _storage.DeleteAsync(staging.BlobKey, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to delete staged blob {Key}", staging.BlobKey); }

        _db.UploadStagings.Remove(staging);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<MagicMappingPreview> ComputePreviewAsync(
        Stream content,
        string fileName,
        string mimeType,
        CancellationToken ct)
    {
        var headers = await HeaderSniffer.ReadHeadersAsync(content, fileName, mimeType, ct);
        var canonicalFields = CanonicalPurchaseOrderFields.AllFields;

        var rows = new List<MagicMappingRow>();
        foreach (var src in headers)
        {
            var deterministicMatch = DeterministicFieldMatcher.Match(src, canonicalFields);
            MagicMappingSuggestion? suggestion = null;

            if (deterministicMatch is not null)
            {
                suggestion = new MagicMappingSuggestion(
                    Confidence: deterministicMatch.Confidence,
                    Source: "deterministic",
                    Reason: deterministicMatch.Reason);
            }
            else
            {
                // Try AI fallback (no-ops when no API key).
                var ai = await _ai.SuggestFieldMappingAsync(src, canonicalFields, ct);
                if (ai is not null)
                {
                    suggestion = new MagicMappingSuggestion(ai.Confidence, "ai_openai", ai.Reason);
                }
            }

            rows.Add(new MagicMappingRow(
                SourceField: src,
                CanonicalField: suggestion is null ? null : DeterministicFieldMatcher.SuggestionToCanonical(suggestion, src),
                SupplierField: null,
                Suggestion: suggestion));
        }

        return new MagicMappingPreview(
            SourceFields: headers,
            CanonicalFields: canonicalFields,
            Rows: rows);
    }
}
```

Note on dependencies: `HeaderSniffer`, `CanonicalPurchaseOrderFields`, `DeterministicFieldMatcher`, `IParseJobEnqueuer`, `IFileStorageService` are all expected to already exist in the codebase. If `HeaderSniffer` or `DeterministicFieldMatcher` do not exist as named, implement the minimum viable extraction:

- `HeaderSniffer.ReadHeadersAsync(stream, fileName, mimeType, ct)` — returns `IReadOnlyList<string>`. For CSV, parse the first non-empty line. For XLSX, read the first row of the first sheet. For PDF, return `Array.Empty<string>()` (magic preview is text-only in Phase 6; PDF orders bypass the preview and go straight to existing PdfOrderParser).
- `DeterministicFieldMatcher.Match(sourceField, canonicalFields)` — fuzzy-matches via Jaro-Winkler or token overlap; returns null when no candidate scores above 0.6.

Place these helpers in `ProcuLink.Transform/Parsing/` if not already present.

- [ ] **Step 5: Run tests — expect pass**

```bash
dotnet test ProcuLink.Infrastructure.Tests --no-restore --filter UploadStagingServiceTests
```

Expected: 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Core/Services/IUploadStagingService.cs ProcuLink.Infrastructure/Services/UploadStagingService.cs ProcuLink.Infrastructure.Tests/Services/UploadStagingServiceTests.cs ProcuLink.Transform/Parsing
git commit -m "feat(upload): UploadStagingService with stage/get/commit/discard + 5 tests"
```

### Task 2.3 — `PurgeExpiredUploadStagingJob` (Hangfire recurring)

**Files:**
- Create: `ProcuLink/ProcuLink.Infrastructure/Jobs/PurgeExpiredUploadStagingJob.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure.Tests/Jobs/PurgeExpiredUploadStagingJobTests.cs`
- Modify: `ProcuLink/ProcuLink.Worker/Program.cs` (schedule the recurring job)

- [ ] **Step 1: Write failing test**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Models;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Jobs;

public class PurgeExpiredUploadStagingJobTests
{
    [Fact]
    public async Task DeletesExpiredRows_AndAssociatedBlobs()
    {
        using var db = new ProcuLinkDbContextFactory().CreateInMemory();
        var storage = new InMemoryFileStorageService();
        var orgId = Guid.NewGuid();

        var expired = new UploadStagingEntity { OrgId = orgId, FileName = "old.csv", BlobKey = "staging/old", MimeType = "text/csv", ExpiresAt = DateTime.UtcNow.AddHours(-1) };
        var live    = new UploadStagingEntity { OrgId = orgId, FileName = "new.csv", BlobKey = "staging/new", MimeType = "text/csv", ExpiresAt = DateTime.UtcNow.AddHours(1) };
        await storage.PutAsync("staging/old", new MemoryStream(new byte[] { 1 }), "text/csv", default);
        await storage.PutAsync("staging/new", new MemoryStream(new byte[] { 2 }), "text/csv", default);
        db.UploadStagings.AddRange(expired, live);
        await db.SaveChangesAsync();

        var job = new PurgeExpiredUploadStagingJob(db, storage, NullLogger<PurgeExpiredUploadStagingJob>.Instance);
        await job.RunAsync(default);

        Assert.Single(await db.UploadStagings.ToListAsync());
        Assert.False(await storage.ExistsAsync("staging/old", default));
        Assert.True(await storage.ExistsAsync("staging/new", default));
    }
}
```

- [ ] **Step 2: Implement the job**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Jobs;

/// <summary>
/// Hangfire recurring job — deletes staged upload rows past their expiry plus
/// the associated R2 blobs. Idempotent.
/// </summary>
public sealed class PurgeExpiredUploadStagingJob
{
    private readonly ProcuLinkDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ILogger<PurgeExpiredUploadStagingJob> _log;

    public PurgeExpiredUploadStagingJob(
        ProcuLinkDbContext db,
        IFileStorageService storage,
        ILogger<PurgeExpiredUploadStagingJob> log)
    {
        _db = db; _storage = storage; _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expired = await _db.UploadStagings
            .Where(x => x.ExpiresAt <= now)
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        foreach (var row in expired)
        {
            try { await _storage.DeleteAsync(row.BlobKey, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to delete expired blob {Key}", row.BlobKey); }
        }
        _db.UploadStagings.RemoveRange(expired);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("PurgeExpiredUploadStaging: deleted {Count} expired staging rows", expired.Count);
    }
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test ProcuLink.Infrastructure.Tests --no-restore --filter PurgeExpiredUploadStagingJobTests
```

Expected: pass.

- [ ] **Step 4: Schedule recurring in `ProcuLink.Worker/Program.cs`**

Find the existing `RecurringJob.AddOrUpdate(...)` calls (e.g. for `EmailPollingJob`). Add:

```csharp
RecurringJob.AddOrUpdate<PurgeExpiredUploadStagingJob>(
    "purge-expired-upload-staging",
    j => j.RunAsync(default),
    Cron.Hourly);
```

- [ ] **Step 5: Build**

```bash
dotnet build ProcuLink.slnx --no-restore
```

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Infrastructure/Jobs/PurgeExpiredUploadStagingJob.cs ProcuLink.Infrastructure.Tests/Jobs/PurgeExpiredUploadStagingJobTests.cs ProcuLink.Worker/Program.cs
git commit -m "feat(upload): PurgeExpiredUploadStagingJob hourly recurring + test"
```

### Task 2.4 — Build `UploadPreviewController` + tests

**Files:**
- Create: `ProcuLink/ProcuLink.Api/Controllers/UploadPreviewController.cs`
- Create: `ProcuLink/ProcuLink.Api.Tests/Controllers/UploadPreviewControllerTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

public class UploadPreviewControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public UploadPreviewControllerTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task POST_preview_returns_staging_id_and_preview_rows()
    {
        var orgId = Guid.NewGuid();
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s =>
            {
                s.AddSingleton<ICurrentTenantService>(new FakeCurrentTenant(orgId, "user_abc"));
                s.AddScoped<IUploadStagingService, FakeUploadStagingService>();
            });
        }).CreateClient();

        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("po,sup\nPO-1,A"))
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv") }
        }, "file", "test.csv");

        var resp = await client.PostAsync("/api/upload/preview", form);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<UploadPreviewResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.StagingId);
    }

    [Fact]
    public async Task POST_preview_returns_404_for_unknown_staging()
    {
        var orgId = Guid.NewGuid();
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<ICurrentTenantService>(new FakeCurrentTenant(orgId, "user_abc"));
                s.AddScoped<IUploadStagingService, FakeUploadStagingService>(); // returns null for any id
            })).CreateClient();

        var resp = await client.GetAsync($"/api/upload/preview/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task POST_preview_commit_returns_order_id()
    {
        var orgId = Guid.NewGuid();
        var fake = new FakeUploadStagingService();
        fake.SeedStaging(orgId, out var stagingId);

        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<ICurrentTenantService>(new FakeCurrentTenant(orgId, "user_abc"));
                s.AddScoped<IUploadStagingService>(_ => fake);
            })).CreateClient();

        var resp = await client.PostAsJsonAsync($"/api/upload/preview/{stagingId}/commit",
            new { rows = new[] { new { sourceField = "po", acceptedCanonicalField = "PoNumber", acceptedSupplierField = (string?)null, accepted = true } } });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CommitResponse>();
        Assert.NotEqual(Guid.Empty, body!.OrderId);
    }

    private sealed record UploadPreviewResponse(Guid StagingId, DateTime ExpiresAt);
    private sealed record CommitResponse(Guid OrderId);
}
```

(Add `FakeUploadStagingService` to `ProcuLink.Api.Tests/TestDoubles/`. It just returns canned entities and exposes a `SeedStaging` test helper.)

- [ ] **Step 2: Implement `UploadPreviewController`**

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[ApiController]
[Route("api/upload/preview")]
[Authorize]
public sealed class UploadPreviewController : ControllerBase
{
    private const long MaxFileBytes = 20 * 1024 * 1024;

    private readonly IUploadStagingService _staging;
    private readonly ICurrentTenantService _tenant;
    private readonly IAnalyticsService _analytics;

    public UploadPreviewController(
        IUploadStagingService staging,
        ICurrentTenantService tenant,
        IAnalyticsService analytics)
    {
        _staging = staging; _tenant = tenant; _analytics = analytics;
    }

    [HttpPost]
    [RequestSizeLimit(MaxFileBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePreview(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Empty file." });
        if (file.Length > MaxFileBytes)
            return BadRequest(new { error = "File exceeds 20MB limit." });

        await using var stream = file.OpenReadStream();
        var staged = await _staging.StageAsync(
            _tenant.OrganisationId, file.FileName, file.ContentType ?? "application/octet-stream",
            stream, ct);

        await _analytics.CaptureAsync(
            _tenant.OrganisationId, _tenant.UserId, "magic_mapping_preview_started",
            new Dictionary<string, object?> { ["staging_id"] = staged.Id, ["file_kind"] = InferFileKind(file.FileName) }, ct);

        return Ok(new { stagingId = staged.Id, expiresAt = staged.ExpiresAt, preview = staged.PreviewJson });
    }

    [HttpGet("{stagingId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPreview(Guid stagingId, CancellationToken ct)
    {
        var staged = await _staging.GetAsync(_tenant.OrganisationId, stagingId, ct);
        if (staged is null) return NotFound();
        return Ok(new { stagingId = staged.Id, expiresAt = staged.ExpiresAt, fileName = staged.FileName, preview = staged.PreviewJson });
    }

    [HttpPost("{stagingId:guid}/commit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Commit(
        Guid stagingId,
        [FromBody] CommitMappingDecisions decisions,
        CancellationToken ct)
    {
        try
        {
            var orderId = await _staging.CommitAsync(_tenant.OrganisationId, stagingId, decisions, ct);

            await _analytics.CaptureAsync(
                _tenant.OrganisationId, _tenant.UserId, "magic_mapping_preview_committed",
                new Dictionary<string, object?>
                {
                    ["staging_id"] = stagingId,
                    ["order_id"] = orderId,
                    ["rows_accepted"] = decisions.Rows.Count(r => r.Accepted),
                    ["rows_rejected"] = decisions.Rows.Count(r => !r.Accepted),
                }, ct);

            return Ok(new { orderId });
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{stagingId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Discard(Guid stagingId, CancellationToken ct)
    {
        await _staging.DiscardAsync(_tenant.OrganisationId, stagingId, ct);

        await _analytics.CaptureAsync(
            _tenant.OrganisationId, _tenant.UserId, "magic_mapping_preview_rejected",
            new Dictionary<string, object?> { ["staging_id"] = stagingId, ["reason"] = "user_cancelled" }, ct);

        return NoContent();
    }

    private static string InferFileKind(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".csv" => "csv",
            ".xlsx" or ".xls" => "xlsx",
            ".pdf" => "pdf",
            _ => "other",
        };
    }
}
```

- [ ] **Step 3: Register `IUploadStagingService` in `Program.cs`**

```csharp
builder.Services.AddScoped<IUploadStagingService, UploadStagingService>();
```

- [ ] **Step 4: Run tests**

```bash
dotnet test ProcuLink.Api.Tests --no-restore --filter UploadPreviewControllerTests
```

Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Api/Controllers/UploadPreviewController.cs ProcuLink.Api/Program.cs ProcuLink.Api.Tests/Controllers/UploadPreviewControllerTests.cs ProcuLink.Api.Tests/TestDoubles
git commit -m "feat(upload): UploadPreviewController POST/GET/commit/discard + 3 controller tests"
```

### Task 2.5 — Frontend: api-client methods

**Files:**
- Modify: `project-proculink/src/lib/api-client.ts`

- [ ] **Step 1: Add the methods**

Append (or insert near existing upload methods):

```ts
export type MagicMappingRow = {
  sourceField: string;
  canonicalField: string | null;
  supplierField: string | null;
  suggestion: { confidence: number; source: string; reason: string } | null;
};

export type MagicMappingPreview = {
  sourceFields: string[];
  canonicalFields: string[];
  rows: MagicMappingRow[];
};

export type StagingResponse = {
  stagingId: string;
  expiresAt: string;
  fileName?: string;
  preview: MagicMappingPreview;
};

export async function uploadPreview(file: File): Promise<StagingResponse> {
  const form = new FormData();
  form.append("file", file);
  const res = await fetch(`${API_BASE}/api/upload/preview`, {
    method: "POST",
    body: form,
    credentials: "include",
  });
  if (!res.ok) throw new Error(`Upload preview failed: ${res.status}`);
  return res.json();
}

export async function getUploadPreview(stagingId: string): Promise<StagingResponse> {
  const res = await fetch(`${API_BASE}/api/upload/preview/${stagingId}`, { credentials: "include" });
  if (!res.ok) throw new Error(`Preview not found: ${res.status}`);
  return res.json();
}

export type CommitMappingDecisions = {
  rows: {
    sourceField: string;
    acceptedCanonicalField: string | null;
    acceptedSupplierField: string | null;
    accepted: boolean;
  }[];
};

export async function commitUploadPreview(stagingId: string, decisions: CommitMappingDecisions): Promise<{ orderId: string }> {
  const res = await fetch(`${API_BASE}/api/upload/preview/${stagingId}/commit`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(decisions),
    credentials: "include",
  });
  if (!res.ok) throw new Error(`Commit failed: ${res.status}`);
  return res.json();
}

export async function discardUploadPreview(stagingId: string): Promise<void> {
  await fetch(`${API_BASE}/api/upload/preview/${stagingId}`, {
    method: "DELETE",
    credentials: "include",
  });
}
```

(Adapt to the exact pattern already used in `api-client.ts` — auth header injection, error envelope, etc.)

- [ ] **Step 2: Commit**

```bash
cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink
git add src/lib/api-client.ts
git commit -m "feat(upload): api-client methods for uploadPreview/get/commit/discard"
```

### Task 2.6 — Build `MagicMappingPreview` component

**Files:**
- Create: `project-proculink/src/components/bridge/MagicMappingPreview.tsx`

- [ ] **Step 1: Create the component**

```tsx
"use client";

import { useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { usePersona } from "@/lib/persona";
import {
  commitUploadPreview,
  discardUploadPreview,
  type MagicMappingPreview,
} from "@/lib/api-client";
import { trackMagicMappingPreviewCommitted, trackMagicMappingPreviewRejected } from "@/lib/analytics";

type Props = {
  stagingId: string;
  fileName: string;
  preview: MagicMappingPreview;
  expiresAt: string;
};

type RowState = {
  sourceField: string;
  canonicalField: string | null;
  supplierField: string | null;
  accepted: boolean;
  edited: boolean;
};

export function MagicMappingPreviewView({ stagingId, fileName, preview, expiresAt }: Props) {
  const router = useRouter();
  const { isExpert } = usePersona();
  const [busy, setBusy] = useState(false);

  const initialRows: RowState[] = useMemo(
    () =>
      preview.rows.map((r) => ({
        sourceField: r.sourceField,
        canonicalField: r.canonicalField,
        supplierField: r.supplierField,
        accepted: (r.suggestion?.confidence ?? 0) >= 0.9,
        edited: false,
      })),
    [preview.rows],
  );
  const [rows, setRows] = useState<RowState[]>(initialRows);

  const updateRow = (idx: number, patch: Partial<RowState>) =>
    setRows((prev) => prev.map((r, i) => (i === idx ? { ...r, ...patch, edited: true } : r)));

  const onAcceptAll = () =>
    setRows((prev) => prev.map((r) => ({ ...r, accepted: true })));

  const onCommit = async () => {
    if (busy) return;
    setBusy(true);
    try {
      const { orderId } = await commitUploadPreview(stagingId, {
        rows: rows.map((r) => ({
          sourceField: r.sourceField,
          acceptedCanonicalField: r.canonicalField,
          acceptedSupplierField: r.supplierField,
          accepted: r.accepted,
        })),
      });
      trackMagicMappingPreviewCommitted({
        staging_id: stagingId,
        rows_accepted: rows.filter((r) => r.accepted).length,
        rows_edited: rows.filter((r) => r.edited).length,
        rows_rejected: rows.filter((r) => !r.accepted).length,
      });
      router.push(`/orders/${orderId}`);
    } finally {
      setBusy(false);
    }
  };

  const onCancel = async () => {
    if (busy) return;
    setBusy(true);
    try {
      await discardUploadPreview(stagingId);
      trackMagicMappingPreviewRejected({ staging_id: stagingId, reason: "user_cancelled" });
      router.push("/upload");
    } finally {
      setBusy(false);
    }
  };

  const padX = isExpert ? 12 : 20;
  const padY = isExpert ? 8 : 14;
  const fontSize = isExpert ? 12.5 : 14;

  return (
    <div style={{ maxWidth: 1100, margin: "0 auto", padding: "32px 24px 80px" }}>
      <header style={{ marginBottom: 24 }}>
        <h1 style={{ fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 28, fontWeight: 700, color: "#0B1A2F", margin: 0 }}>
          Review your mapping before we send this order
        </h1>
        <p style={{ color: "#56627A", marginTop: 8, lineHeight: 1.5 }}>
          We sniffed <strong>{preview.sourceFields.length}</strong> source field
          {preview.sourceFields.length === 1 ? "" : "s"} from <strong>{fileName}</strong>. Confirm
          or edit before we create the order. Nothing is saved until you press
          {" "}<em>Create order</em>.
        </p>
      </header>

      <div role="region" aria-label="Mapping rows" style={{ border: "1px solid #E2E6EE", borderRadius: 10, overflow: "hidden", background: "#FFFFFF" }}>
        <div
          style={{
            display: "grid",
            gridTemplateColumns: "minmax(140px, 1fr) minmax(140px, 1fr) minmax(140px, 1fr) 110px 80px",
            padding: `${padY}px ${padX}px`,
            background: "#F6F7FA",
            borderBottom: "1px solid #E2E6EE",
            fontSize: 12,
            fontWeight: 600,
            color: "#56627A",
            textTransform: "uppercase",
            letterSpacing: 0.5,
          }}
        >
          <div>Source field</div>
          <div>Canonical field</div>
          <div>Supplier field</div>
          <div>Confidence</div>
          <div style={{ textAlign: "right" }}>Accept</div>
        </div>

        {rows.map((r, i) => (
          <div
            key={r.sourceField + i}
            style={{
              display: "grid",
              gridTemplateColumns: "minmax(140px, 1fr) minmax(140px, 1fr) minmax(140px, 1fr) 110px 80px",
              padding: `${padY}px ${padX}px`,
              borderBottom: i === rows.length - 1 ? "none" : "1px solid #F1F3F7",
              fontSize,
              alignItems: "center",
            }}
          >
            <div style={{ fontFamily: "'JetBrains Mono', ui-monospace, monospace", color: "#0B1A2F" }}>{r.sourceField}</div>
            <div>
              <select
                value={r.canonicalField ?? ""}
                onChange={(e) => updateRow(i, { canonicalField: e.target.value || null })}
                aria-label={`Canonical mapping for ${r.sourceField}`}
                style={{ width: "100%", padding: "6px 8px", border: "1px solid #C6CDDA", borderRadius: 4, fontSize }}
              >
                <option value="">— skip —</option>
                {preview.canonicalFields.map((f) => (
                  <option key={f} value={f}>{f}</option>
                ))}
              </select>
            </div>
            <div>
              <input
                type="text"
                value={r.supplierField ?? ""}
                onChange={(e) => updateRow(i, { supplierField: e.target.value || null })}
                aria-label={`Supplier field for ${r.sourceField}`}
                placeholder="(optional)"
                style={{ width: "100%", padding: "6px 8px", border: "1px solid #C6CDDA", borderRadius: 4, fontSize }}
              />
            </div>
            <div>
              {preview.rows[i]?.suggestion ? (
                <ConfidenceBadge
                  confidence={preview.rows[i]!.suggestion!.confidence}
                  source={preview.rows[i]!.suggestion!.source}
                  reason={preview.rows[i]!.suggestion!.reason}
                />
              ) : (
                <span style={{ color: "#8A93A5", fontSize: 12 }}>—</span>
              )}
            </div>
            <div style={{ textAlign: "right" }}>
              <input
                type="checkbox"
                checked={r.accepted}
                onChange={(e) => updateRow(i, { accepted: e.target.checked })}
                aria-label={`Accept mapping for ${r.sourceField}`}
              />
            </div>
          </div>
        ))}
      </div>

      <footer style={{ display: "flex", gap: 12, marginTop: 24, justifyContent: "space-between", alignItems: "center", flexWrap: "wrap" }}>
        <button
          type="button"
          onClick={onAcceptAll}
          style={{ background: "#FFFFFF", color: "#1E66C9", border: "1px solid #1E66C9", borderRadius: 6, padding: "8px 14px", fontSize: 13, fontWeight: 600, cursor: "pointer" }}
        >
          Accept all
        </button>
        <div style={{ display: "flex", gap: 12 }}>
          <button
            type="button"
            onClick={onCancel}
            disabled={busy}
            style={{ background: "#FFFFFF", color: "#56627A", border: "1px solid #C6CDDA", borderRadius: 6, padding: "8px 14px", fontSize: 13, fontWeight: 500, cursor: busy ? "not-allowed" : "pointer" }}
          >
            Cancel — discard this upload
          </button>
          <button
            type="button"
            onClick={onCommit}
            disabled={busy}
            style={{ background: "#0B1A2F", color: "#FFFFFF", border: "none", borderRadius: 6, padding: "8px 16px", fontSize: 13, fontWeight: 600, cursor: busy ? "not-allowed" : "pointer" }}
          >
            {busy ? "Creating order…" : "Create order"}
          </button>
        </div>
      </footer>

      <p style={{ fontSize: 11.5, color: "#8A93A5", marginTop: 16 }}>
        This preview expires {new Date(expiresAt).toLocaleString()} — after that, please re-upload.
      </p>
    </div>
  );
}

function ConfidenceBadge({ confidence, source, reason }: { confidence: number; source: string; reason: string }) {
  const pct = Math.round(confidence * 100);
  const tone = confidence >= 0.9 ? "#2E8E3A" : confidence >= 0.6 ? "#B98300" : "#D43A2F";
  const sourceLabel = source === "deterministic" ? "exact" : source === "ai_openai" ? "AI" : source;
  return (
    <span
      title={`${sourceLabel}: ${reason}`}
      style={{
        display: "inline-flex",
        gap: 4,
        alignItems: "center",
        fontSize: 11.5,
        color: tone,
        fontWeight: 600,
      }}
    >
      <span aria-hidden="true" style={{ width: 6, height: 6, borderRadius: "50%", background: tone, display: "inline-block" }} />
      {pct}% · {sourceLabel}
    </span>
  );
}
```

- [ ] **Step 2: Commit**

```bash
git add src/components/bridge/MagicMappingPreview.tsx
git commit -m "feat(upload): MagicMappingPreview component with accept/edit/reject + confidence badges"
```

### Task 2.7 — Add `/upload/preview/[stagingId]` route

**Files:**
- Create: `project-proculink/src/app/(app)/upload/preview/[stagingId]/page.tsx`

- [ ] **Step 1: Create the route**

```tsx
"use client";

import { useEffect, useState } from "react";
import { notFound, useParams } from "next/navigation";
import { getUploadPreview, type StagingResponse } from "@/lib/api-client";
import { MagicMappingPreviewView } from "@/components/bridge/MagicMappingPreview";

export default function UploadPreviewPage() {
  const params = useParams<{ stagingId: string }>();
  const [data, setData] = useState<StagingResponse | null>(null);
  const [error, setError] = useState<"loading" | "notfound" | "network" | "ok">("loading");

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const res = await getUploadPreview(params.stagingId);
        if (cancelled) return;
        setData(res);
        setError("ok");
      } catch (e) {
        if (cancelled) return;
        setError(String((e as Error).message).includes("404") ? "notfound" : "network");
      }
    })();
    return () => { cancelled = true; };
  }, [params.stagingId]);

  if (error === "loading") return <PreviewSkeleton />;
  if (error === "notfound") return <PreviewMissing reason="not-found" />;
  if (error === "network") return <PreviewMissing reason="network" />;
  if (!data) return notFound();

  return (
    <MagicMappingPreviewView
      stagingId={data.stagingId}
      fileName={data.fileName ?? "your file"}
      preview={data.preview}
      expiresAt={data.expiresAt}
    />
  );
}

function PreviewSkeleton() {
  return (
    <div style={{ maxWidth: 1100, margin: "0 auto", padding: "32px 24px" }}>
      <div style={{ height: 32, width: 420, background: "#F1F3F7", borderRadius: 6, marginBottom: 12 }} />
      <div style={{ height: 18, width: 540, background: "#F1F3F7", borderRadius: 6, marginBottom: 32 }} />
      <div style={{ height: 320, background: "#F6F7FA", borderRadius: 10 }} />
    </div>
  );
}

function PreviewMissing({ reason }: { reason: "not-found" | "network" }) {
  return (
    <div style={{ maxWidth: 540, margin: "80px auto", textAlign: "center" }}>
      <h1 style={{ fontFamily: "'Bricolage Grotesque', Inter, sans-serif", color: "#0B1A2F", fontSize: 22 }}>
        {reason === "not-found" ? "This preview has expired or doesn't exist" : "We couldn't load this preview"}
      </h1>
      <p style={{ color: "#56627A", marginTop: 12 }}>
        {reason === "not-found"
          ? "Previews expire 24 hours after upload. Re-upload your file to start again."
          : "There was a network problem. Try again in a moment."}
      </p>
      <a
        href="/upload"
        style={{ display: "inline-block", marginTop: 24, background: "#0B1A2F", color: "#FFFFFF", padding: "10px 18px", borderRadius: 6, textDecoration: "none", fontWeight: 600 }}
      >
        Back to upload
      </a>
    </div>
  );
}
```

- [ ] **Step 2: Verify build**

```bash
bun run build
```

Expected: success.

- [ ] **Step 3: Commit**

```bash
git add "src/app/(app)/upload/preview/[stagingId]/page.tsx"
git commit -m "feat(upload): /upload/preview/[stagingId] route with skeleton + missing-preview states"
```

### Task 2.8 — Wire `UploadWorkbench` to the new preview flow

**Files:**
- Modify: `project-proculink/src/components/bridge/UploadWorkbench.tsx`

- [ ] **Step 1: Read the file**

```bash
cat src/components/bridge/UploadWorkbench.tsx | head -100
```

Find the existing `onUpload` (or equivalent) handler that POSTs to `/api/orders/upload`.

- [ ] **Step 2: Replace with the preview flow**

Replace the POST call with `uploadPreview(file)` from api-client, then redirect to `/upload/preview/{stagingId}`. For non-CSV/XLSX (PDF), keep the existing direct-upload path (Phase 6 preview is text-only).

Pseudocode:

```tsx
import { uploadPreview } from "@/lib/api-client";
import { useRouter } from "next/navigation";

// inside component
const router = useRouter();

const onFileSelected = async (file: File) => {
  const ext = file.name.toLowerCase().split(".").pop();
  if (ext === "pdf") {
    // Existing direct path — keep unchanged.
    const orderId = await uploadOrderDirect(file);
    router.push(`/orders/${orderId}`);
    return;
  }
  const { stagingId } = await uploadPreview(file);
  router.push(`/upload/preview/${stagingId}`);
};
```

Keep the existing 429 / api-unavailable / file-too-large handling.

- [ ] **Step 3: Verify build + manual smoke**

```bash
bun run build
```

Upload a small CSV in dev → land on `/upload/preview/<id>` with rows shown. Cancel returns to `/upload`. Create order navigates to `/orders/<id>`.

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/UploadWorkbench.tsx
git commit -m "feat(upload): UploadWorkbench routes CSV/XLSX through /upload/preview/[stagingId]"
```

### Task 2.9 — Playwright smoke test for magic mapping preview

**Files:**
- Create: `project-proculink/e2e/magic-mapping-preview.spec.ts`

- [ ] **Step 1: Write the test**

```ts
import { test, expect } from "@playwright/test";
import { readFileSync } from "node:fs";
import path from "node:path";

test.describe("Magic mapping preview", () => {
  test("upload → preview → commit → order detail", async ({ page }) => {
    await page.goto("/upload");

    // Upload a small CSV.
    const csvPath = path.join(__dirname, "fixtures", "magic-mapping-sample.csv");
    await page.setInputFiles('input[type="file"]', csvPath);

    // Should redirect to preview.
    await page.waitForURL(/\/upload\/preview\/[a-f0-9-]+/);
    await expect(page.getByRole("region", { name: "Mapping rows" })).toBeVisible();

    // Accept all + commit.
    await page.getByRole("button", { name: "Accept all" }).click();
    await page.getByRole("button", { name: "Create order" }).click();

    // Should land on order detail.
    await page.waitForURL(/\/orders\/[a-f0-9-]+/);
    await expect(page.getByText(/Review/i)).toBeVisible();
  });

  test("cancel returns to /upload and discards staging", async ({ page }) => {
    await page.goto("/upload");
    const csvPath = path.join(__dirname, "fixtures", "magic-mapping-sample.csv");
    await page.setInputFiles('input[type="file"]', csvPath);
    await page.waitForURL(/\/upload\/preview\/[a-f0-9-]+/);
    await page.getByRole("button", { name: /Cancel/ }).click();
    await page.waitForURL(/\/upload$/);
  });
});
```

Add the test fixture:

```bash
mkdir -p e2e/fixtures
cat > e2e/fixtures/magic-mapping-sample.csv <<EOF
po_number,supplier_code,item_code,quantity,unit_price
PO-MAGIC-001,SUP-A,SKU-001,10,12.50
PO-MAGIC-001,SUP-A,SKU-002,5,8.00
EOF
```

- [ ] **Step 2: Run**

```bash
bunx playwright test e2e/magic-mapping-preview.spec.ts --reporter=line
```

Expected: both tests pass against a running local dev server with a real backend (the test requires a live API).

- [ ] **Step 3: Commit**

```bash
git add e2e/magic-mapping-preview.spec.ts e2e/fixtures/magic-mapping-sample.csv
git commit -m "test(e2e): magic mapping preview happy path + cancel discards staging"
```

### Task 2.10 — Phase 2 review + merge

- [ ] **Step 1: Run full backend + frontend tests**

```bash
cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink
dotnet test ProcuLink.slnx --no-restore

cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink
bun run build
bunx tsc --noEmit
```

Expected: backend ≥ 221 tests (213 prior + 5 staging + 1 purge + 3 controller + at least 2 ingestion-helper tests), frontend builds.

- [ ] **Step 2: `/code-review` the Phase 2 diff**

Focus areas:
- Org-scoping on all staging queries.
- 20MB file size limit on the preview endpoint.
- Staging blob cleanup on commit (kept; renamed semantically) + on discard (deleted).
- Hourly purge job is registered exactly once in Worker.
- PDF path is unchanged.

- [ ] **Step 3: Push + open Phase 2 PR**

```bash
git push
gh pr create --title "feat(upload): magic mapping preview at /upload/preview/[stagingId] (Phase 6 / Group L expanded Phase 2)" --body "..."
```

---

## Phase 3 — Per-Industry Templates

Insert "Pick your industry" as Step 0 of the onboarding wizard. Selecting an industry loads suppliers + mappings + rules + a sample order from `ProcuLink.Api/Fixtures/templates/<slug>/`. Refuses with 409 if the org already has any non-sample data.

### Task 3.1 — Define manifest schema + create 4 industry fixtures

**Files:**
- Create: `ProcuLink/ProcuLink.Api/Fixtures/templates/industrial-distribution/manifest.json`
- Create: `ProcuLink/ProcuLink.Api/Fixtures/templates/industrial-distribution/suppliers/acme-fasteners.csv`
- Create: `ProcuLink/ProcuLink.Api/Fixtures/templates/industrial-distribution/mappings/acme-fasteners.json`
- Create: `ProcuLink/ProcuLink.Api/Fixtures/templates/industrial-distribution/rules/min-quantity.json`
- Create: `ProcuLink/ProcuLink.Api/Fixtures/templates/industrial-distribution/sample-order.csv`
- (Repeat the same structure for `food-and-beverage-wholesale`, `hospitality`, `healthcare-gpo`.)

- [ ] **Step 1: Create the industrial-distribution manifest**

```json
{
  "name": "Industrial distribution",
  "summary": "MRO and industrial parts buying with a mix of EDI-savvy and CSV-only suppliers.",
  "industry": "industrial-distribution",
  "suppliers": [
    { "name": "Acme Fasteners (Synthetic)", "code": "ACME-FST", "deliveryProtocol": "http", "fromFile": "suppliers/acme-fasteners.csv" },
    { "name": "Northwind Bearings (Synthetic)", "code": "NWIND-BRG", "deliveryProtocol": "sftp", "fromFile": "suppliers/northwind-bearings.csv" },
    { "name": "Contoso Hydraulics (Synthetic)", "code": "CTSO-HYD", "deliveryProtocol": "email", "fromFile": "suppliers/contoso-hydraulics.csv" }
  ],
  "mappings": [
    { "supplierCode": "ACME-FST", "fromFile": "mappings/acme-fasteners.json" },
    { "supplierCode": "NWIND-BRG", "fromFile": "mappings/northwind-bearings.json" },
    { "supplierCode": "CTSO-HYD", "fromFile": "mappings/contoso-hydraulics.json" }
  ],
  "rules": [
    { "name": "Minimum line quantity ≥ 1", "fromFile": "rules/min-quantity.json" }
  ],
  "sampleOrder": "sample-order.csv"
}
```

Each `mappings/*.json`:

```json
{
  "supplierCode": "ACME-FST",
  "name": "Acme Fasteners default mapping",
  "rows": [
    { "buyerField": "po_number", "supplierField": "PONum" },
    { "buyerField": "item_code", "supplierField": "PartNo" },
    { "buyerField": "quantity",  "supplierField": "Qty" }
  ]
}
```

Each `rules/*.json`:

```json
{
  "name": "Minimum line quantity ≥ 1",
  "expression": "line.quantity >= 1",
  "severity": "error"
}
```

Each supplier `.csv`: a 1-row CSV with the supplier's profile fields (name, code, email, address) for ergonomic display in `/library/suppliers`.

`sample-order.csv`: a 3-line synthetic PO (mirror `Fixtures/sample-order.csv` pattern).

- [ ] **Step 2: Repeat for the 3 other industries**

Same structure. Suppliers should match each vertical's character — F&B wholesale uses suppliers like "Pacific Produce", "Heritage Dairy", "Coastal Beverage"; hospitality uses "Linens Co.", "Restaurant Supply Group", "Hotel Spirits Wholesale"; healthcare GPO uses "MediGlove Co.", "SteriPak Devices", "PharmaSource RX". Use **synthetic names only**; never any real customer or supplier.

- [ ] **Step 3: Verify the file tree**

```bash
ls -la ProcuLink.Api/Fixtures/templates/
```

Should list 4 industry folders, each with `manifest.json`, `suppliers/`, `mappings/`, `rules/`, `sample-order.csv`.

- [ ] **Step 4: Mark all fixture files as embedded content in `ProcuLink.Api.csproj`**

Open `ProcuLink.Api.csproj`. Find the existing `<ItemGroup>` that handles `sample-order.csv`. Replace the single-file entry with a wildcard:

```xml
<ItemGroup>
  <None Update="Fixtures/**/*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

(If a more specific embedded-resource pattern is already in use, mirror that pattern instead.)

- [ ] **Step 5: Commit**

```bash
cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink
git add ProcuLink.Api/Fixtures/templates ProcuLink.Api/ProcuLink.Api.csproj
git commit -m "feat(onboarding): add 4 industry template fixtures (industrial / F&B / hospitality / healthcare)"
```

### Task 3.2 — Build `IIndustryTemplateService` + tests

**Files:**
- Create: `ProcuLink/ProcuLink.Core/Services/IIndustryTemplateService.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure/Services/IndustryTemplateService.cs`
- Create: `ProcuLink/ProcuLink.Infrastructure.Tests/Services/IndustryTemplateServiceTests.cs`

- [ ] **Step 1: Define the interface**

```csharp
namespace ProcuLink.Core.Services;

public interface IIndustryTemplateService
{
    /// <summary>Lists template slugs + display names available to the wizard.</summary>
    IReadOnlyList<IndustryTemplateSummary> ListAvailable();

    /// <summary>
    /// Applies the named template to the given organisation: creates suppliers,
    /// mappings, rules, and a sample order. Throws InvalidOperationException
    /// (mapped to HTTP 409) if the org already has non-sample data.
    /// </summary>
    Task<IndustryTemplateApplyResult> ApplyAsync(
        Guid organisationId,
        string industrySlug,
        CancellationToken ct = default);
}

public sealed record IndustryTemplateSummary(string Slug, string Name, string Summary);
public sealed record IndustryTemplateApplyResult(
    string Slug,
    int SuppliersCreated,
    int MappingsCreated,
    int RulesCreated,
    Guid? SampleOrderId);
```

- [ ] **Step 2: Write failing tests**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services;

public class IndustryTemplateServiceTests
{
    [Fact]
    public void ListAvailable_returns_at_least_4_industries()
    {
        var svc = BuildSvc(NewDb());
        var list = svc.ListAvailable();
        Assert.True(list.Count >= 4);
        Assert.Contains(list, x => x.Slug == "industrial-distribution");
        Assert.Contains(list, x => x.Slug == "food-and-beverage-wholesale");
        Assert.Contains(list, x => x.Slug == "hospitality");
        Assert.Contains(list, x => x.Slug == "healthcare-gpo");
    }

    [Fact]
    public async Task ApplyAsync_creates_suppliers_mappings_rules_and_sample_order()
    {
        using var db = NewDb();
        var svc = BuildSvc(db);
        var orgId = Guid.NewGuid();
        EnsureOrgRow(db, orgId);

        var res = await svc.ApplyAsync(orgId, "industrial-distribution", default);

        Assert.Equal("industrial-distribution", res.Slug);
        Assert.True(res.SuppliersCreated >= 1);
        Assert.True(res.MappingsCreated >= 1);
        Assert.True(res.RulesCreated >= 1);
        Assert.NotNull(res.SampleOrderId);
        Assert.True((await db.Suppliers.CountAsync(s => s.OrgId == orgId)) >= 1);
    }

    [Fact]
    public async Task ApplyAsync_refuses_when_org_already_has_non_sample_data()
    {
        using var db = NewDb();
        var svc = BuildSvc(db);
        var orgId = Guid.NewGuid();
        EnsureOrgRow(db, orgId);
        db.Suppliers.Add(new() { Id = Guid.NewGuid(), OrgId = orgId, Name = "Existing", IsSample = false });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApplyAsync(orgId, "industrial-distribution", default));
    }

    [Fact]
    public async Task ApplyAsync_unknown_slug_throws_FileNotFoundException()
    {
        using var db = NewDb();
        var svc = BuildSvc(db);
        var orgId = Guid.NewGuid();
        EnsureOrgRow(db, orgId);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            svc.ApplyAsync(orgId, "no-such-industry", default));
    }

    private static ProcuLinkDbContext NewDb() => new ProcuLinkDbContextFactory().CreateInMemory();
    private static void EnsureOrgRow(ProcuLinkDbContext db, Guid id) { /* seed minimal org row if FK required */ }
    private static IndustryTemplateService BuildSvc(ProcuLinkDbContext db) => new(
        db,
        new FakeFixtureFileResolver("ProcuLink.Api/Fixtures/templates"),
        new InMemoryFileStorageService(),
        new FakeAnalyticsService(),
        NullLogger<IndustryTemplateService>.Instance);
}
```

`FakeFixtureFileResolver` resolves manifest paths relative to the test working directory. If the test runs from `/ProcuLink.Infrastructure.Tests/bin/...`, the resolver needs to walk up to the repo root. Implement it as a small helper that takes a relative root and provides `OpenManifest(slug)`, `OpenFile(slug, relativePath)`.

- [ ] **Step 3: Implement `IndustryTemplateService.cs`**

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Models;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

public sealed class IndustryTemplateService : IIndustryTemplateService
{
    private readonly ProcuLinkDbContext _db;
    private readonly IFixtureFileResolver _resolver;
    private readonly IFileStorageService _storage;
    private readonly IAnalyticsService _analytics;
    private readonly ILogger<IndustryTemplateService> _log;

    public IndustryTemplateService(
        ProcuLinkDbContext db,
        IFixtureFileResolver resolver,
        IFileStorageService storage,
        IAnalyticsService analytics,
        ILogger<IndustryTemplateService> log)
    {
        _db = db; _resolver = resolver; _storage = storage; _analytics = analytics; _log = log;
    }

    public IReadOnlyList<IndustryTemplateSummary> ListAvailable()
    {
        return _resolver.ListSlugs()
            .Select(slug =>
            {
                using var s = _resolver.OpenManifest(slug);
                var doc = JsonDocument.Parse(s);
                var name = doc.RootElement.GetProperty("name").GetString() ?? slug;
                var summary = doc.RootElement.TryGetProperty("summary", out var sm) ? (sm.GetString() ?? "") : "";
                return new IndustryTemplateSummary(slug, name, summary);
            })
            .ToList();
    }

    public async Task<IndustryTemplateApplyResult> ApplyAsync(
        Guid orgId,
        string slug,
        CancellationToken ct = default)
    {
        // Idempotency: refuse if non-sample data exists.
        var hasSuppliers = await _db.Suppliers.AnyAsync(s => s.OrgId == orgId && !s.IsSample, ct);
        if (hasSuppliers)
            throw new InvalidOperationException(
                $"Organisation already has non-sample data; refusing to apply template '{slug}'.");

        using var manifestStream = _resolver.OpenManifest(slug); // throws FileNotFoundException for unknown slug
        var manifest = JsonSerializer.Deserialize<TemplateManifest>(manifestStream)
            ?? throw new InvalidOperationException($"Manifest for '{slug}' is invalid JSON.");

        var suppliersCreated = await CreateSuppliersAsync(orgId, slug, manifest, ct);
        var mappingsCreated  = await CreateMappingsAsync(orgId, slug, manifest, ct);
        var rulesCreated     = await CreateRulesAsync(orgId, slug, manifest, ct);
        var sampleOrderId    = await CreateSampleOrderAsync(orgId, slug, manifest, ct);

        await _analytics.CaptureAsync(orgId, userId: null, "industry_template_selected",
            new Dictionary<string, object?>
            {
                ["industry"] = slug,
                ["suppliers_created"] = suppliersCreated,
                ["mappings_created"]  = mappingsCreated,
                ["rules_created"]     = rulesCreated,
            }, ct);

        return new IndustryTemplateApplyResult(slug, suppliersCreated, mappingsCreated, rulesCreated, sampleOrderId);
    }

    // ── Private helpers ────────────────────────────────────────────────
    // CreateSuppliersAsync: iterates manifest.suppliers, creates SupplierEntity rows.
    // CreateMappingsAsync:  iterates manifest.mappings,  parses each JSON, creates PoMapping rows.
    // CreateRulesAsync:     iterates manifest.rules,     creates ValidationRule rows.
    // CreateSampleOrderAsync: stores sample-order.csv under storage, creates PurchaseOrderEntity { IsSample = true }, enqueues parse job.
    //
    // Each helper has the exact same OrgId-scoping discipline as the existing services.

    private async Task<int> CreateSuppliersAsync(Guid orgId, string slug, TemplateManifest m, CancellationToken ct)
    {
        // … omitted for brevity in the plan; mirror SampleOrderService + SuppliersController patterns ...
        // Returns count of suppliers created.
        return 0;
    }

    private async Task<int> CreateMappingsAsync(Guid orgId, string slug, TemplateManifest m, CancellationToken ct) => 0;
    private async Task<int> CreateRulesAsync(Guid orgId, string slug, TemplateManifest m, CancellationToken ct) => 0;
    private async Task<Guid?> CreateSampleOrderAsync(Guid orgId, string slug, TemplateManifest m, CancellationToken ct) => null;

    private sealed record TemplateManifest(
        string Name,
        string Summary,
        string Industry,
        List<TemplateSupplier> Suppliers,
        List<TemplateMapping>  Mappings,
        List<TemplateRule>     Rules,
        string?                SampleOrder);

    private sealed record TemplateSupplier(string Name, string Code, string DeliveryProtocol, string? FromFile);
    private sealed record TemplateMapping(string SupplierCode, string FromFile);
    private sealed record TemplateRule(string Name, string FromFile);
}

public interface IFixtureFileResolver
{
    IReadOnlyList<string> ListSlugs();
    Stream OpenManifest(string slug); // throws FileNotFoundException
    Stream OpenFile(string slug, string relativePath);
}

public sealed class DiskFixtureFileResolver : IFixtureFileResolver
{
    private readonly string _root;
    public DiskFixtureFileResolver(string root) { _root = root; }

    public IReadOnlyList<string> ListSlugs() =>
        Directory.Exists(_root)
            ? Directory.GetDirectories(_root).Select(Path.GetFileName)!.OfType<string>().ToList()
            : Array.Empty<string>();

    public Stream OpenManifest(string slug)
    {
        var path = Path.Combine(_root, slug, "manifest.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"Manifest not found for '{slug}'", path);
        return File.OpenRead(path);
    }

    public Stream OpenFile(string slug, string relativePath) =>
        File.OpenRead(Path.Combine(_root, slug, relativePath));
}
```

**Important:** the helper methods (`CreateSuppliersAsync` etc.) must be fully implemented before merge. The plan elides them for brevity here; an executing agent should mirror the patterns already present in `SuppliersController`, `PoMappingService`, `SampleOrderService`. For each create-step:
- All new rows set `OrgId = orgId`.
- Suppliers loaded from the manifest set `IsSample = false` (the template is treated as real data the user is adopting; only the `sample-order.csv` order itself is `IsSample = true`).
- The sample order goes through `IParseJobEnqueuer.EnqueueParseAsync(...)`.

- [ ] **Step 4: Run tests — expect pass after helpers are implemented**

```bash
dotnet test ProcuLink.Infrastructure.Tests --no-restore --filter IndustryTemplateServiceTests
```

Expected: 4 tests pass once `CreateSuppliersAsync` etc. are filled in.

- [ ] **Step 5: Register in DI**

In `ProcuLink.Api/Program.cs`:

```csharp
var fixturesRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "templates");
builder.Services.AddSingleton<IFixtureFileResolver>(new DiskFixtureFileResolver(fixturesRoot));
builder.Services.AddScoped<IIndustryTemplateService, IndustryTemplateService>();
```

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Core/Services/IIndustryTemplateService.cs ProcuLink.Infrastructure/Services/IndustryTemplateService.cs ProcuLink.Infrastructure.Tests/Services/IndustryTemplateServiceTests.cs ProcuLink.Api/Program.cs
git commit -m "feat(onboarding): IndustryTemplateService apply/list + 4 tests"
```

### Task 3.3 — Build `IndustryTemplateController` + tests

**Files:**
- Create: `ProcuLink/ProcuLink.Api/Controllers/IndustryTemplateController.cs`
- Create: `ProcuLink/ProcuLink.Api.Tests/Controllers/IndustryTemplateControllerTests.cs`

- [ ] **Step 1: Implement the controller**

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Controllers;

[ApiController]
[Route("api/onboarding/industry-template")]
[Authorize]
public sealed class IndustryTemplateController : ControllerBase
{
    private readonly IIndustryTemplateService _templates;
    private readonly ICurrentTenantService _tenant;

    public IndustryTemplateController(IIndustryTemplateService templates, ICurrentTenantService tenant)
    {
        _templates = templates; _tenant = tenant;
    }

    [HttpGet]
    public IActionResult List() => Ok(_templates.ListAvailable());

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Apply([FromBody] ApplyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Industry))
            return BadRequest(new { error = "industry slug is required" });

        try
        {
            var res = await _templates.ApplyAsync(_tenant.OrganisationId, req.Industry, ct);
            return Ok(res);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = $"Industry template '{req.Industry}' not found." });
        }
    }

    public sealed record ApplyRequest(string Industry);
}
```

- [ ] **Step 2: Write 3 controller tests**

```csharp
[Fact] public async Task GET_returns_available_industries() { /* assert ≥ 4 entries */ }
[Fact] public async Task POST_returns_200_on_apply()         { /* fake svc returns valid result */ }
[Fact] public async Task POST_returns_409_when_svc_throws_InvalidOperation() { /* … */ }
```

(Mirror the pattern in `UploadPreviewControllerTests`.)

- [ ] **Step 3: Run tests**

```bash
dotnet test ProcuLink.Api.Tests --no-restore --filter IndustryTemplateControllerTests
```

Expected: 3 tests pass.

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Api/Controllers/IndustryTemplateController.cs ProcuLink.Api.Tests/Controllers/IndustryTemplateControllerTests.cs
git commit -m "feat(onboarding): IndustryTemplateController GET/POST + 3 controller tests"
```

### Task 3.4 — Frontend: `IndustryPicker` Step 0 component

**Files:**
- Create: `project-proculink/src/components/bridge/IndustryPicker.tsx`
- Modify: `project-proculink/src/lib/api-client.ts` (new methods)

- [ ] **Step 1: Add api-client methods**

```ts
export type IndustryTemplate = { slug: string; name: string; summary: string };
export type IndustryApplyResult = {
  slug: string;
  suppliersCreated: number;
  mappingsCreated: number;
  rulesCreated: number;
  sampleOrderId: string | null;
};

export async function listIndustryTemplates(): Promise<IndustryTemplate[]> {
  const res = await fetch(`${API_BASE}/api/onboarding/industry-template`, { credentials: "include" });
  if (!res.ok) throw new Error(`List industries failed: ${res.status}`);
  return res.json();
}

export async function applyIndustryTemplate(industry: string): Promise<IndustryApplyResult> {
  const res = await fetch(`${API_BASE}/api/onboarding/industry-template`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ industry }),
    credentials: "include",
  });
  if (res.status === 409) throw new Error("ALREADY_STARTED");
  if (!res.ok) throw new Error(`Apply industry failed: ${res.status}`);
  return res.json();
}
```

- [ ] **Step 2: Create `IndustryPicker.tsx`**

```tsx
"use client";

import { useEffect, useState } from "react";
import {
  applyIndustryTemplate,
  listIndustryTemplates,
  type IndustryApplyResult,
  type IndustryTemplate,
} from "@/lib/api-client";
import { trackIndustryTemplateSelected } from "@/lib/analytics";

type Props = {
  onApplied: (result: IndustryApplyResult) => void;
  onSkip: () => void;
};

export function IndustryPicker({ onApplied, onSkip }: Props) {
  const [list, setList] = useState<IndustryTemplate[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    listIndustryTemplates().then(setList).catch(() => setError("Could not load industry options."));
  }, []);

  const onPick = async (slug: string) => {
    setBusy(slug);
    setError(null);
    try {
      const res = await applyIndustryTemplate(slug);
      trackIndustryTemplateSelected({ industry: slug, via: "wizard" });
      onApplied(res);
    } catch (e) {
      if ((e as Error).message === "ALREADY_STARTED") {
        setError("Looks like you've already added suppliers. You can keep setting up manually below.");
      } else {
        setError("Couldn't apply that template. Please try again or skip for now.");
      }
    } finally {
      setBusy(null);
    }
  };

  return (
    <div style={{ padding: "8px 0 24px" }}>
      <h2 style={{ fontFamily: "'Bricolage Grotesque', Inter, sans-serif", fontSize: 22, fontWeight: 700, color: "#0B1A2F", marginBottom: 8 }}>
        Which industry are you buying for?
      </h2>
      <p style={{ color: "#56627A", marginBottom: 20, lineHeight: 1.5 }}>
        Pick the closest match. We'll set up 3 example suppliers, mappings, validation rules,
        and a sample order so you can see the full flow in under 5 minutes. You can change
        everything later.
      </p>

      <div style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fill, minmax(240px, 1fr))" }}>
        {list.map((t) => (
          <button
            key={t.slug}
            type="button"
            onClick={() => onPick(t.slug)}
            disabled={busy !== null}
            style={{
              textAlign: "left",
              background: "#FFFFFF",
              border: "1px solid #E2E6EE",
              borderRadius: 8,
              padding: "16px 18px",
              cursor: busy === null ? "pointer" : "not-allowed",
              opacity: busy !== null && busy !== t.slug ? 0.5 : 1,
            }}
          >
            <div style={{ fontSize: 15, fontWeight: 600, color: "#0B1A2F", marginBottom: 6 }}>
              {t.name}
            </div>
            <div style={{ fontSize: 13, color: "#56627A", lineHeight: 1.5 }}>
              {t.summary}
            </div>
            {busy === t.slug && (
              <div style={{ marginTop: 12, fontSize: 12, color: "#1E66C9" }}>Setting up…</div>
            )}
          </button>
        ))}
      </div>

      {error && (
        <div role="alert" style={{ marginTop: 16, padding: 12, background: "#FDECEA", border: "1px solid #F5C4BF", borderRadius: 6, color: "#9A1F14", fontSize: 13 }}>
          {error}
        </div>
      )}

      <div style={{ marginTop: 20, textAlign: "center" }}>
        <button
          type="button"
          onClick={onSkip}
          disabled={busy !== null}
          style={{ background: "none", border: "none", color: "#56627A", textDecoration: "underline", fontSize: 13, cursor: "pointer" }}
        >
          Skip — I'll set up manually
        </button>
      </div>
    </div>
  );
}
```

- [ ] **Step 3: Commit**

```bash
git add src/components/bridge/IndustryPicker.tsx src/lib/api-client.ts
git commit -m "feat(onboarding): IndustryPicker step 0 component + api-client methods"
```

### Task 3.5 — Insert Step 0 into `OnboardingWizard`

**Files:**
- Modify: `project-proculink/src/components/bridge/OnboardingWizard.tsx`

- [ ] **Step 1: Read the file**

```bash
cat src/components/bridge/OnboardingWizard.tsx | head -120
```

Identify the step indexing (likely 1..4 as numeric state) and the step renderer (likely a switch/match on a step enum).

- [ ] **Step 2: Add Step 0 to the wizard state machine**

```tsx
import { IndustryPicker } from "@/components/bridge/IndustryPicker";

type Step = 0 | 1 | 2 | 3 | 4;

// state init
const [step, setStep] = useState<Step>(0);

// renderer
{step === 0 && (
  <IndustryPicker
    onApplied={() => setStep(1)}
    onSkip={() => setStep(1)}
  />
)}
{step === 1 && <ExistingStep1 ... />}
{/* steps 2..4 unchanged */}
```

Update the step progress indicator (likely "1/4" → "1/5", or hide the indicator on step 0 since it's "pre-wizard").

- [ ] **Step 3: Verify build**

```bash
bun run build
```

Open `/welcome` in a fresh-org dev account. Step 0 shows industry choices. Picking one (or skipping) advances to the existing Step 1.

- [ ] **Step 4: Commit**

```bash
git add src/components/bridge/OnboardingWizard.tsx
git commit -m "feat(onboarding): insert IndustryPicker as Step 0 of OnboardingWizard"
```

### Task 3.6 — Phase 3 review + merge

- [ ] **Step 1: Run tests + build**

```bash
cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink
dotnet test ProcuLink.slnx --no-restore

cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink
bun run build
```

Expected: backend ≥ 228 tests (Phase 2 baseline + 4 industry-service + 3 industry-controller), frontend builds.

- [ ] **Step 2: `/code-review` the Phase 3 diff**

Focus areas:
- Fixture files contain only synthetic names — no real customer data.
- Idempotency: 409 path is exercised in tests.
- Fixture file copy: `Fixtures/**/*` is in the build output directory.
- Step 0 does not block existing users from re-entering steps 1..4.

- [ ] **Step 3: Push + open Phase 3 PR**

```bash
git push
gh pr create --title "feat(onboarding): per-industry templates wired as wizard Step 0 (Phase 6 / Group L expanded Phase 3)"
```

---

## Phase 4 — In-App Help Completion

Context-aware Help button in `BridgeTopbar` + backfill of 9 missing articles.

### Task 4.1 — `useHelpArticle()` hook + pathname lookup

**Files:**
- Create: `project-proculink/src/lib/help-router.ts`

- [ ] **Step 1: Create the lookup table + hook**

```ts
"use client";

import { usePathname } from "next/navigation";

export const HELP_FALLBACK = "troubleshooting";

const PATH_RULES: { pattern: RegExp; slug: string }[] = [
  { pattern: /^\/bridge(\/|$)/,                slug: "dashboard" },
  { pattern: /^\/upload(\/preview)?(\/|$)/,    slug: "first-upload" },
  { pattern: /^\/inbox(\/|$)/,                 slug: "inbox-and-review" },
  { pattern: /^\/orders(\/|$)/,                slug: "inbox-and-review" },
  { pattern: /^\/drafts(\/|$)/,                slug: "first-upload" },
  { pattern: /^\/suppliers(\/|$)/,             slug: "library-suppliers" },
  { pattern: /^\/library\/suppliers(\/|$)/,    slug: "library-suppliers" },
  { pattern: /^\/library\/mappings(\/|$)/,     slug: "mapping-basics" },
  { pattern: /^\/mappings(\/|$)/,              slug: "mapping-basics" },
  { pattern: /^\/library\/rules(\/|$)/,        slug: "validation-rules" },
  { pattern: /^\/library\/buyers(\/|$)/,       slug: "library-buyers" },
  { pattern: /^\/library\/templates(\/|$)/,    slug: "library-templates" },
  { pattern: /^\/operations\/connectors(\/|$)/,slug: "connectors" },
  { pattern: /^\/operations\/webhooks(\/|$)/,  slug: "webhooks" },
  { pattern: /^\/operations\/log(\/|$)/,       slug: "troubleshooting" },
  { pattern: /^\/inbound\/invoices(\/|$)/,     slug: "inbound-documents" },
  { pattern: /^\/inbound\/asns(\/|$)/,         slug: "inbound-documents" },
  { pattern: /^\/settings(\/|$)/,              slug: "billing-faq" },
];

export function resolveHelpSlug(pathname: string): string {
  for (const r of PATH_RULES) if (r.pattern.test(pathname)) return r.slug;
  return HELP_FALLBACK;
}

export function useHelpArticle(): { slug: string; pathname: string } {
  const pathname = usePathname() || "/";
  return { slug: resolveHelpSlug(pathname), pathname };
}
```

- [ ] **Step 2: Commit**

```bash
git add src/lib/help-router.ts
git commit -m "feat(help): pathname → help-slug router + useHelpArticle hook"
```

### Task 4.2 — Update `HelpSlideover` to accept a slug prop

**Files:**
- Modify: `project-proculink/src/components/bridge/HelpSlideover.tsx`

- [ ] **Step 1: Read the file**

```bash
cat src/components/bridge/HelpSlideover.tsx | head -100
```

Identify how the slideover currently renders content (likely from a static list inside the file or via Fuse.js search).

- [ ] **Step 2: Add a `slug` prop + dynamic MDX load**

Add prop:

```tsx
type Props = { open: boolean; onClose: () => void; slug?: string };
```

When `slug` is supplied AND `open` is true, render that article up-top with a "Open full article in /help" link to `/help/<slug>`. Below it, keep the existing Fuse.js search input so users can navigate to anything else.

For the article content rendering, dynamically import the MDX page:

```tsx
import dynamic from "next/dynamic";

const Article = slug
  ? dynamic(() => import(`@/app/(marketing)/help/${slug}/page.mdx`), { ssr: false, loading: () => <ArticleSkeleton /> })
  : null;
```

(If Next.js bundler can't statically resolve dynamic imports with a slug variable, register each known slug in a manual map: `const REGISTRY: Record<string, () => Promise<...>> = { "dashboard": () => import("..."), ... }`. This is the typical Next.js pattern.)

- [ ] **Step 3: Emit `help_slideover_opened` analytics**

```tsx
import { trackHelpSlideoverOpened } from "@/lib/analytics";

useEffect(() => {
  if (open && slug) {
    trackHelpSlideoverOpened({ slug, from_pathname: pathname });
  }
}, [open, slug, pathname]);
```

- [ ] **Step 4: Verify build**

```bash
bun run build
```

- [ ] **Step 5: Commit**

```bash
git add src/components/bridge/HelpSlideover.tsx
git commit -m "feat(help): HelpSlideover accepts slug prop + dynamic MDX render + analytics"
```

### Task 4.3 — Wire context-aware Help in `BridgeTopbar`

**Files:**
- Modify: `project-proculink/src/components/bridge/BridgeTopbar.tsx`

- [ ] **Step 1: Read + thread the slug**

Add:

```tsx
import { useHelpArticle } from "@/lib/help-router";

const { slug } = useHelpArticle();
```

Pass `slug` to the existing `<HelpSlideover ... />` mount.

- [ ] **Step 2: Verify**

```bash
bun run build
```

Open `/bridge` → click Help → slideover shows `dashboard` article. Navigate to `/library/rules` → click Help → slideover shows `validation-rules` article.

- [ ] **Step 3: Commit**

```bash
git add src/components/bridge/BridgeTopbar.tsx
git commit -m "feat(help): BridgeTopbar Help button routes to context-aware article"
```

### Task 4.4 — Backfill 9 MDX articles

**Files:**
- Create: 9 `page.mdx` files under `project-proculink/src/app/(marketing)/help/<slug>/`

Each article follows this skeleton (mirror the existing `first-upload/page.mdx`):

```mdx
import HelpArticle from "@/components/marketing/HelpArticle";

export const metadata = {
  title: "<Title> — ProcuLink Help",
  description: "<one-line description>",
};

<HelpArticle
  title="<Title>"
  updated="2026-05-28"
  category="<one of: Onboarding | Daily use | Integrations | Trust | Troubleshooting>"
  relatedSlugs={["first-upload", "mapping-basics"]}
>

## What this screen does

<2–4 sentence summary of the screen's purpose.>

## What you can do here

- <action 1>
- <action 2>
- <action 3>

## Common questions

### <question 1>

<answer>

### <question 2>

<answer>

## Related articles

- [<related title 1>](/help/<related slug>)

</HelpArticle>
```

- [ ] **Step 1: Write `dashboard/page.mdx`**

Title: "Your dashboard"
Category: Daily use
Summary: "The /bridge dashboard is the home of your operation — see in-flight orders, suppliers, and the onboarding checklist on one view."
What you can do: open inbox, start an upload, jump into a supplier, see topology.
Related: first-upload, inbox-and-review.

- [ ] **Step 2: Write `inbox-and-review/page.mdx`**

Title: "Reviewing and resolving orders"
Category: Daily use
Summary: Inbox lists every uploaded order; the review screen lets you accept AI mappings, fix exceptions, and approve for delivery.
What you can do: filter inbox, open an order, accept/edit mapped lines, send to delivery.
Related: first-upload, mapping-basics.

- [ ] **Step 3: Write `validation-rules/page.mdx`**

Title: "Validation rules"
Category: Onboarding
Summary: Validation rules let you block deliveries until lines meet your business criteria (minimum quantity, allowed item codes, currency match).
What you can do: create rules, set severity (warning/error), assign rules to suppliers.
Related: mapping-basics, troubleshooting.

- [ ] **Step 4: Write `library-suppliers/page.mdx`**

Title: "Supplier directory"
Category: Onboarding
Summary: The supplier directory holds every supplier you can send orders to, plus their delivery channel and per-supplier mapping config.
What you can do: add a supplier, set delivery protocol, configure HTTP/SFTP/email, run a test-fire.
Related: delivery-config, mapping-basics.

- [ ] **Step 5: Write `library-buyers/page.mdx`**

Title: "Buyer organisations"
Category: Onboarding
Summary: Buyer org records track each internal buying entity (legal entity, address, default currency) so transformed orders carry the correct buyer header to suppliers.
What you can do: add buyer org, set default currency, attach to suppliers.
Related: library-suppliers, mapping-basics.

- [ ] **Step 6: Write `library-templates/page.mdx`**

Title: "Output templates"
Category: Onboarding
Summary: Templates control how each supplier's output file (XML, CSV) is structured — fields, ordering, headers, separators.
What you can do: edit a template body, preview output, attach a template to a supplier mapping.
Related: mapping-basics, library-suppliers.

- [ ] **Step 7: Write `connectors/page.mdx`**

Title: "Connectors (Zapier / Make / custom webhooks)"
Category: Integrations
Summary: Connectors let external systems subscribe to ProcuLink events (order.created, order.delivered, order.failed). Use them to push notifications into Slack, Excel, or your ERP without polling.
What you can do: create an API key, install the Zapier app, add a custom webhook with HMAC secret.
Related: webhooks, billing-faq.

- [ ] **Step 8: Write `webhooks/page.mdx`**

Title: "Webhooks (integration triggers)"
Category: Integrations
Summary: Webhooks fire on order events to any HTTPS endpoint. Each request is signed with HMAC-SHA256; we retry 3 times before auto-deactivating a failing endpoint.
What you can do: add a target URL, copy the HMAC secret, verify signatures.
Related: connectors, troubleshooting.

- [ ] **Step 9: Write `inbound-documents/page.mdx`**

Title: "Inbound documents (invoices + ASNs)"
Category: Daily use
Summary: ProcuLink can receive supplier invoices (UBL 2.1) and advance shipping notices (DESADV) and link them to the originating PO so you can see the full lifecycle.
What you can do: upload an invoice manually, see linked PO, approve for AP downstream.
Related: inbox-and-review, troubleshooting.

- [ ] **Step 10: Build + verify each article renders**

```bash
bun run build
```

Open `http://localhost:3000/help/dashboard` (and each new slug) — render with proper layout. Open `/bridge` → Help → slideover shows `dashboard` content.

- [ ] **Step 11: Commit**

```bash
git add "src/app/(marketing)/help/dashboard" "src/app/(marketing)/help/inbox-and-review" "src/app/(marketing)/help/validation-rules" "src/app/(marketing)/help/library-suppliers" "src/app/(marketing)/help/library-buyers" "src/app/(marketing)/help/library-templates" "src/app/(marketing)/help/connectors" "src/app/(marketing)/help/webhooks" "src/app/(marketing)/help/inbound-documents"
git commit -m "docs(help): backfill 9 MDX articles for all sidebar-reachable screens"
```

### Task 4.5 — Phase 4 review + merge

- [ ] **Step 1: Build**

```bash
bun run build
```

- [ ] **Step 2: `/code-review` the diff**

Focus: every sidebar route resolves to a real article; no `troubleshooting` fallback fires for a known route; slideover content scrolls correctly.

- [ ] **Step 3: Push + PR**

```bash
git push
gh pr create --title "feat(help): context-aware Help button + 9 backfill articles (Phase 6 / Group L expanded Phase 4)"
```

---

## Phase 5 — Analytics Funnel Completion

Stripe webhook → analytics wiring is **already done** (`BillingController.cs:258-263, 300-305, 332-336`). Phase 5 verifies that, adds new events from Phases 1–4 to the taxonomy doc, and adds one integration test that proves the Stripe webhook → analytics path emits as expected.

### Task 5.1 — Add `BillingControllerWebhookAnalyticsTests`

**Files:**
- Create: `ProcuLink/ProcuLink.Api.Tests/Controllers/BillingControllerWebhookAnalyticsTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

public class BillingControllerWebhookAnalyticsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public BillingControllerWebhookAnalyticsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task CheckoutCompleted_emits_billing_upgraded()
    {
        var analytics = new FakeAnalyticsService();
        var orgId = Guid.NewGuid();

        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IAnalyticsService>(analytics);
                // Seed an org row + a fake StripeBillingService that returns this org.
                SeedOrg(s, orgId);
            })).CreateClient();

        var stripePayload = BuildCheckoutCompletedPayload(orgId, plan: "growth");
        var (signature, body) = SignStripe(stripePayload, secret: TestSecret);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Stripe-Signature", signature);

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Contains(analytics.Captured, e => e.EventName == "billing_upgraded" && e.Properties.ContainsKey("to_plan"));
    }

    // … two more tests: SubscriptionUpdated → billing_downgraded, SubscriptionDeleted → billing_cancelled.

    private const string TestSecret = "whsec_test_for_unit_tests";
    private static (string sig, string body) SignStripe(string payload, string secret) { /* HMAC-SHA256 timestamped signature per Stripe spec */ return ("", payload); }
    private static string BuildCheckoutCompletedPayload(Guid orgId, string plan) => "{ ... }";
    private static void SeedOrg(IServiceCollection s, Guid orgId) { /* override DbContext or seed via a setup */ }
}
```

(Stripe webhook signing is non-trivial; the test sets `Stripe:WebhookSecret = "whsec_test_for_unit_tests"` in the test host, then constructs a signature matching that secret using the exact algorithm `Stripe.EventUtility.ConstructEvent` validates. There are existing helpers in the Stripe.net test docs; replicate them in `BillingControllerWebhookAnalyticsTests.SignStripe`.)

- [ ] **Step 2: Run the test**

```bash
cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink
dotnet test ProcuLink.Api.Tests --no-restore --filter BillingControllerWebhookAnalyticsTests
```

Expected: 3 tests pass.

- [ ] **Step 3: Commit**

```bash
git add ProcuLink.Api.Tests/Controllers/BillingControllerWebhookAnalyticsTests.cs
git commit -m "test(billing): 3 integration tests proving Stripe webhook → analytics emit path"
```

### Task 5.2 — Update `docs/analytics-event-taxonomy.md` to v1.1

**Files:**
- Modify: `ProcuLink/docs/analytics-event-taxonomy.md`

- [ ] **Step 1: Bump version + append new event tables**

At the top, change the version line: `> Version 1.1 — 2026-05-28. …`

Add new tables / rows:

**Dual-persona (frontend):**

| Event             | When                                | Properties                                         |
|-------------------|-------------------------------------|----------------------------------------------------|
| `persona_toggled` | User changes Default ↔ Expert       | `from`, `to`, `via=click\|hotkey`                  |

**Magic mapping (mixed):**

| Event                              | Source   | When                                            | Properties                                                       |
|------------------------------------|----------|-------------------------------------------------|------------------------------------------------------------------|
| `magic_mapping_preview_started`    | backend  | `POST /api/upload/preview` succeeds             | `staging_id`, `file_kind=csv\|xlsx\|pdf\|other`                  |
| `magic_mapping_preview_committed`  | backend  | `POST .../commit` succeeds                      | `staging_id`, `order_id`, `rows_accepted`, `rows_rejected`       |
| `magic_mapping_preview_rejected`   | backend  | `DELETE .../{stagingId}` or auto-expire purge   | `staging_id`, `reason=user_cancelled\|expired`                   |

**Per-industry templates (backend):**

| Event                         | When                                         | Properties                                                  |
|-------------------------------|----------------------------------------------|-------------------------------------------------------------|
| `industry_template_selected`  | `POST /api/onboarding/industry-template` 200 | `industry`, `suppliers_created`, `mappings_created`, `rules_created` |

**Help (frontend):**

| Event                     | When                          | Properties                |
|---------------------------|-------------------------------|---------------------------|
| `help_slideover_opened`   | Slideover opens with a slug   | `slug`, `from_pathname`   |

In the "When to bump this doc" section, document the v1.0 → v1.1 entry: "Added persona/help/magic-mapping/industry-template event family for Phase 6 Horizon 1 Group L expanded."

- [ ] **Step 2: Commit**

```bash
git add docs/analytics-event-taxonomy.md
git commit -m "docs(analytics): bump event taxonomy to v1.1 (persona / help / magic-mapping / industry-template)"
```

### Task 5.3 — Update `STATUS.md`

**Files:**
- Modify: `ProcuLink/STATUS.md`

- [ ] **Step 1: Add Group L expanded entry**

Append (or update existing "Group L" section) with:

```markdown
### Group L expanded (Phase 6 Horizon 1) — shipped 2026-XX-XX

Workstreams:
- ✅ Dual-persona UX toggle (sticky in localStorage, exposed in BridgeTopbar + /settings)
- ✅ Magic mapping preview at /upload/preview/[stagingId] (24h staging TTL, hourly purge)
- ✅ Per-industry templates wired as Step 0 of onboarding wizard (industrial / F&B / hospitality / healthcare)
- ✅ Context-aware /help slideover routing + 9 backfill articles (every sidebar-reachable screen covered)
- ✅ Analytics funnel completion: persona / help / magic-mapping / industry-template events; Stripe webhook → analytics verified end-to-end

Backend test count: 213 → <NEW TOTAL>
```

- [ ] **Step 2: Commit**

```bash
git add STATUS.md
git commit -m "docs(status): Group L expanded shipped — persona / magic mapping / industry templates / help / analytics"
```

### Task 5.4 — Phase 5 review + merge

- [ ] **Step 1: Full test + build**

```bash
cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink
dotnet test ProcuLink.slnx --no-restore

cd C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink
bun run build
bunx tsc --noEmit
```

Expected: backend ≥ 231 tests, frontend builds.

- [ ] **Step 2: `/code-review` the Phase 5 diff**

- [ ] **Step 3: Push + PR**

```bash
git push
gh pr create --title "feat(analytics): funnel completion + taxonomy v1.1 (Phase 6 / Group L expanded Phase 5)"
```

---

## Spec coverage table

| Phase 6 scope item (from roadmap)                                                 | Plan phase / task             |
|-----------------------------------------------------------------------------------|-------------------------------|
| Dual-persona UX toggle on every operational screen, sticky in localStorage        | Phase 1.1–1.5, 1.9            |
| Default mode: wizard, templates, AI defaults, generous spacing                    | Phase 1.7, 1.8 + Phase 3.4–3.5|
| Expert mode: density, raw view, standards mapping inline, hotkeys                 | Phase 1.3 (hotkey), 1.6 (StandardsHint), 1.7, 1.8 |
| Magic mapping preview side-by-side before order commit                            | Phase 2.4 (controller), 2.6 (component), 2.7 (route) |
| AI suggestions render with confidence + provenance                                | Phase 2.6 (ConfidenceBadge)   |
| Accept / edit / reject per row before order persists                              | Phase 2.6 (row state)         |
| New /upload/preview/[stagingId] route + backend staging endpoint                  | Phase 2.1 (entity), 2.2 (service), 2.4 (controller), 2.7 (route) |
| Per-industry templates for industrial / F&B / hospitality / healthcare GPO        | Phase 3.1 (4 fixture sets)    |
| Templates load from ProcuLink.Api/Fixtures/templates/<industry>/                  | Phase 3.1 + 3.2 (resolver)    |
| "Pick your industry" step before Step 1 of wizard                                 | Phase 3.5                     |
| /help context-aware links via BridgeTopbar Help button                            | Phase 4.1 (router), 4.2 (slideover), 4.3 (topbar) |
| Backfill /help to cover every sidebar-reachable screen                            | Phase 4.4 (9 new MDX articles)|
| Verify / extend Stripe webhook → analytics wiring                                 | Phase 5.1 (3 integration tests verifying ALREADY-shipped wiring) |
| Analytics taxonomy update for new events                                          | Phase 5.2                     |
| Status doc update                                                                 | Phase 5.3                     |
| Dual-persona invariant (CLAUDE.md / 00-agent-quick-brief.md)                      | Phase 1 baseline applied to every new screen in Phases 2, 3, 4 |

---

## Open verification items for the founder

These are decisions the planning agent could not resolve without founder input or external state. Please review before authorising Phase 2 / Phase 3 execution.

1. **Magic mapping preview file-size cap.** The plan sets 20 MB. The existing direct-upload cap may differ — confirm whether the preview path should match the direct path exactly (likely yes), or be lower since the preview is more expensive.

2. **Magic mapping for PDFs.** Phase 6 magic preview is text-only (CSV / XLSX). PDF orders bypass the preview and go straight to `PdfOrderParser`. Confirm this is acceptable; alternative is to add a PDF "preview rough lines and let the user accept" flow in Phase 7.

3. **Industry template idempotency.** Plan refuses with HTTP 409 if the org has any non-sample data. Alternative: allow merge (skip suppliers/mappings that already exist; add the rest). Refuse is simpler; merge is friendlier mid-onboarding. Plan defaults to refuse — confirm.

4. **Industry list scope.** Phase 6 ships 4 industries: `industrial-distribution`, `food-and-beverage-wholesale`, `hospitality`, `healthcare-gpo`. The Phase 6 roadmap specifies these four explicitly. If a fifth (e.g. construction supply, electronics distribution, fashion retail) is needed for a specific design partner, add it as a follow-up chip — the resolver is data-driven (drop a new folder).

5. **Persona toggle copy / placement.** Plan places the compact toggle in `BridgeTopbar` next to the Help button and uses copy "Default / Expert". Confirm this matches the locked direction — the alternative "Simple / Advanced" is explicitly rejected in `00-agent-quick-brief.md`.

6. **Standards refs for every canonical field.** Phase 1.7 wires `StandardsHint` on SpineReview using `docs/standards-matrix.md` "Canonical PO Model fields" as the source of truth. If a field doesn't have refs in that doc yet, `StandardsHint` no-ops. Confirm `docs/standards-matrix.md` has refs for at least the 8–10 most-visible canonical fields (PO number, buyer name, supplier code, line item code, quantity, unit price, currency, delivery date). If not, Phase 1 ships partial expert-mode standards visibility; the rest backfills in Group M.

7. **Help slideover MDX dynamic import.** Next.js may not support fully dynamic `import("@/.../${slug}/page.mdx")` paths under bundler constraints. If the bundler refuses, fall back to a manual registry (one line per slug) — plan describes this in Task 4.2 Step 2. Confirm the team is fine with the manual registry pattern.

8. **Hotkey conflict surface.** `Shift+E` toggles persona. Confirm no existing global hotkey uses this combo. CommandPalette (Ctrl/Cmd+K) is separate; the `?` hotkey for the future hotkey-overlay is reserved.

9. **Analytics opt-out for the new events.** New events all flow through `IAnalyticsService` which is consent-gated on the frontend by the existing cookie banner state. Confirm backend events (`magic_mapping_preview_*`, `industry_template_selected`) should fire unconditionally (they're transactional, not user-tracking).

10. **Existing direct upload path retirement.** Plan keeps the existing direct `/api/orders/upload` path intact for PDFs. Once the preview path covers PDFs too (Phase 7), the direct path can be removed. Confirm this two-step retirement is acceptable rather than removing it now.

---

## Self-review notes

- **Spec coverage:** All 5 workstreams from the Phase 6 roadmap "Group L (expanded)" section have at least one task. Cross-checked against the roadmap bullet list.
- **Placeholder scan:** No "TBD" / "implement later" / "handle edge cases" left. `IndustryTemplateService.CreateSuppliersAsync` and siblings are elided but flagged as "must be fully implemented before merge" with explicit pattern references.
- **Type consistency:** `CommitMappingDecisions` / `CommitMappingRow` named consistently across backend + frontend. `MagicMappingPreview` / `MagicMappingRow` / `MagicMappingSuggestion` consistent. `Persona = "default" | "expert"` consistent everywhere.
- **Phase independence:** Phase 1 (persona) is a hard prerequisite for Phase 2/3/4 expert-mode density. Phase 2 (magic mapping) is independent of Phase 3 (industry templates). Phase 4 (help) depends on Phase 1 only for the topbar context. Phase 5 is independent.
- **Migrations:** One new EF migration (`AddUploadStaging` in Phase 2). No schema change in Phases 1, 3, 4, 5.

---

## Execution handoff (per superpowers:writing-plans)

**Plan complete and saved to `docs/superpowers/plans/2026-05-28-group-l-expanded-dual-persona-magic-mapping.md`.**

Two execution options:

1. **Subagent-Driven (recommended)** — Fresh subagent per task with two-stage review between tasks. Fast iteration. REQUIRED SUB-SKILL: `superpowers:subagent-driven-development`.
2. **Inline Execution** — Execute tasks in this session with checkpoints. REQUIRED SUB-SKILL: `superpowers:executing-plans`.

**Do not execute this plan in the same session as it was written — the founder reviews phased plans before execution.**
