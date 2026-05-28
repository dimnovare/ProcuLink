# ProcuLink Design System - Agent Quick Brief

**Read this file first for any frontend/UI task.** It is the compact routing guide
for Claude Code/Codex so agents do not need to load the full design-system folder
unless the task requires it.

## Source Of Truth

The locked design direction is **Direction 4 - The Bridge Layer**, supported by
**Direction 3 - System Identity** for the logo/glyph language.

- Direction 4 defines the product spatial metaphor: buyers on one side,
  suppliers on the other, ProcuLink as the bridge.
- Direction 3 defines the visual mark system: one asymmetric link curve with
  two endpoint dots, extended into rails, spine nodes, glyphs, loading states,
  and pipeline motion.
- The attached reference images are examples of this locked direction:
  motion ideas, System Identity, Bridge Layer, Wire Topology dashboard, and
  Canonical Spine review.

Do not invent a new visual direction. Do not use Lovable. Do not paste or adapt
Vite/Lovable output into this project.

## Design Workflow

Use this order:

1. Project design system in `docs/design-system/` is canonical.
2. Use `/frontend-design` for design judgement, polish, layout critique, and
   production-quality implementation.
3. Use Claude Design/reference images as visual evidence for the locked direction.

`/frontend-design` must not override the locked system. It should help execute
The Bridge Layer more clearly.

## What To Read

Start with this file, then read only what the task needs:

- New page or major UI flow: `10-claude-code-brief.md`
- Tokens/colors/type: `02-tokens.md`, `03-typography.md`, `04-color.md`
- Signature components: `05-components.md`
- Motion: `06-motion.md`
- Copy/vocabulary: `07-content.md`
- Logo/glyphs/icons: `08-iconography.md`
- Trust/provenance/AI rules: `09-trust-rules.md`
- Visual reference only: `showcase.html`
- Reusable component examples: `components/*.tsx`
- Token imports: `tokens/tailwind.config.ts`, `tokens/tokens.ts`

Do not bulk-read the whole directory. Files on disk do not cost tokens unless
loaded into the session.

## Non-Negotiable UI Signatures

1. Edge rails: blue buyer rail on the left, green supplier rail on the right.
2. Wire Topology dashboard: buyers left, suppliers right, wires between them.
3. Canonical Spine review: source document, canonical spine, supplier output.
4. Document Anatomy: labeled source zones with confidence/provenance.
5. Cross-section card edge: primary cards use a 3px blue/green/bridge edge.

Supporting signatures:

- Navy app chrome, light work area.
- Link-spine gradient line in topbars and section dividers.
- Status as a five-stage journey: Parse, Normalize, Validate, Transform, Deliver.
- Monumental KPI numbers in display type.
- System Identity mark/glyph family everywhere a custom mark is needed.

## Dual-Persona UX (Phase 6+)

Every new screen must work for two personas at once and be QA'd in both
modes. This is a non-negotiable product invariant from 2026-05-28 onward,
codified in `CLAUDE.md` under "Coding conventions → Product-level rules".

### Default mode (first-time / novice user)

- Wizard-style flows with one decision per step.
- Sensible defaults from per-industry templates (industrial distribution,
  food and beverage wholesale, hospitality procurement, healthcare GPO).
- AI-pre-filled fields rendered with visible confidence + provenance +
  Accept / Edit / Reject controls (never auto-applied).
- Explanatory copy that names the user's outcome ("Send this order to your
  supplier") instead of internal mechanics ("Run the transform job").
- Conservative density: generous spacing, large click targets, fewer
  columns visible.
- The five-stage journey (Parse → Normalize → Validate → Transform →
  Deliver) is the primary mental model surfaced everywhere.

### Expert mode (power user / 30-year procurement veteran)

- Toggle is visible on every operational screen and sticky across sessions
  via `localStorage`.
- Higher density: compact rows, more columns visible at once, condensed
  type scale.
- **Standards mappings inline** — every field in a transform / mapping
  context surfaces its UBL / EDIFACT / X12 / cXML / Peppol BIS / ISO 20022
  reference next to the value. Source of truth for those references:
  `docs/standards-matrix.md` § "Canonical PO Model fields".
- Inline edit-of-anything affordances; modals reserved for irreversible or
  multi-field operations.
- Hotkeys for common actions; `?` opens a hotkey overlay on every screen.
- Raw view (JSON / XML / EDI envelope) accessible from any artifact.
- No "are you sure" confirmations on safe / reversible operations.

### Implementation rule

No new screen ships without both modes considered. The PR description must
call out which mode each interaction belongs to. Default mode is the
unauthenticated / new-user experience; expert mode is opt-in but sticky.
Per-screen overrides (a flow that only makes sense in one mode) are
allowed but must be justified in the PR.

The toggle copy is "Default / Expert" — not "Simple / Advanced" or any
phrasing that implies the default user is less capable.

## Current Product Wedge

First ICP: buyer/procurement teams sending purchase orders out to many suppliers.

The UI should help procurement users see:

- which orders are ready to send,
- which supplier-specific mapping or validation issue blocks delivery,
- which supplier delivery channel will be used,
- whether delivery actually succeeded,
- what changed and who approved it.

Do not imply an order is sent just because an artifact was generated. Use explicit
states such as `ready_to_deliver`, `delivering`, `delivered`, and
`delivery_failed`.

## Practical Rules

- Next.js 15 App Router only.
- Tailwind + shadcn/ui, restyled through the Bridge Layer tokens.
- Use existing `src/components/bridge/*` patterns before creating new primitives.
- Keep operational screens dense, calm, and keyboard-friendly.
- Use AI violet only for AI-generated suggestions, never as decoration.
- No generic SaaS hero cards, purple gradients, glassmorphism, or decorative blobs.
- No hidden AI automation: every AI suggestion needs visible confidence and
  Accept/Edit/Reject style controls.
- Motion must communicate state, not flair. Respect `prefers-reduced-motion`.

## Current UI Polish Target

Before adding new visual patterns, QA the existing Bridge Layer screens across
desktop and mobile. Known issue to fix first: Wire Topology traveller/pulse dots
must always be visually attached to a rendered wire path. Do not allow a standalone
dot to appear between buyer and supplier cards without the corresponding line.
The pulse and visible wire must share the exact same SVG path, and the pulse should
fade or be disabled if the path cannot be rendered cleanly at the current viewport.
