# Legal Entity Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the stale public company identity with Diip Solutions OÜ while preserving ProcuLink as the product brand.

**Architecture:** Add one immutable frontend legal-entity module and consume it from every public legal surface. Regression tests verify the authoritative values and reject the old entity details. Project trust documentation and STATUS.md are reconciled after the code is green.

**Tech Stack:** Next.js 15 App Router, TypeScript, Vitest, Playwright, Markdown

---

### Task 1: Define the legal identity contract

**Files:**
- Create: `project-proculink/src/test/legal-entity.test.ts`
- Create: `project-proculink/src/lib/legal-entity.ts`
- Modify: `project-proculink/tests/e2e/marketing.spec.ts`

- [x] Write a Vitest test asserting the ProcuLink product name, Diip Solutions OÜ legal name, registry code, registered address, operator notice, copyright notice, and Organization JSON-LD values.
- [x] Run `bun run test -- src/test/legal-entity.test.ts` and confirm it fails because `@/lib/legal-entity` does not exist.
- [x] Add the immutable module with the approved identity and derived strings.
- [x] Update the Playwright legal-entity assertion to require Diip Solutions OÜ and reject ProcuLink OÜ, 17477775, and Katusepapi.
- [x] Run the targeted Vitest test and confirm it passes.

### Task 2: Reconcile public legal surfaces

**Files:**
- Modify: `project-proculink/src/app/(marketing)/terms/page.tsx`
- Modify: `project-proculink/src/app/(marketing)/privacy/page.tsx`
- Modify: `project-proculink/src/app/(marketing)/dpa/page.tsx`
- Modify: `project-proculink/src/app/(marketing)/one-pager/page.tsx`
- Modify: `project-proculink/src/app/(marketing)/layout.tsx`
- Modify: `project-proculink/src/app/page.tsx`
- Modify: `project-proculink/src/app/layout.tsx`

- [x] Import the central identity module into each public legal surface.
- [x] Name Diip Solutions OÜ as operator, processor, and IP owner while retaining ProcuLink as the service and brand.
- [x] Replace both stale footer strings with the compact approved copyright notice.
- [x] Add Organization JSON-LD using Diip Solutions OÜ as `name` and ProcuLink as `alternateName`.
- [x] Update the legal-page revision dates to June 2026.
- [x] Run the focused Playwright legal-entity test.

### Task 3: Reconcile project documentation

**Files:**
- Modify: `docs/trust/gdpr.md`
- Modify: `docs/group-l-go-live-playbook.md`
- Modify: `docs/strategy/2026-05-30-four-lens-product-analysis.md`
- Modify: `STATUS.md`

- [x] Replace the stale legal entity, registry code, and address in current trust and go-live guidance.
- [x] Preserve historical product references to ProcuLink while correcting statements that identify a legal company.
- [x] Add a concise STATUS.md handoff recording the legal identity source of truth.
- [x] Search both repositories for the old identity and verify only unrelated untracked/generated artifacts remain.

### Task 4: Verify the complete change

**Files:**
- Verify all files above.

- [x] Run `bun run test`.
- [x] Run the focused Playwright legal/trust test.
- [x] Run `bun run build`.
- [x] Inspect `git diff --check` and both repository status outputs.
- [x] Confirm no secret, personal email, or VAT number was added.
