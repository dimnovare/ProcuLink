# ProcuLink Design System

**Version:** 1.0 · **Locked direction:** The Bridge Layer
**Date:** May 2026

> Buyers on one side. Suppliers on the other. We are the bridge.

This package is the source of truth for the ProcuLink visual language. It is designed to be handed verbatim to Claude Code (or any frontend agent) as the implementation brief.

For token-efficient agent sessions, start with `00-agent-quick-brief.md` and
then load only the specific design files needed for the current screen.

---

## What's in this package

```
design-system/
├── README.md                       ← you are here
├── 00-agent-quick-brief.md         ← compact first-read guide for Claude/Codex
├── 01-foundations.md               ← brand voice, principles, trust rules
├── 02-tokens.md                    ← all tokens documented
├── 03-typography.md                ← scale, pairings, usage
├── 04-color.md                     ← semantic system, contrast, do/don't
├── 05-components.md                ← signature components with React/JSX
├── 06-motion.md                    ← six patterns + budget
├── 07-content.md                   ← copy guidelines, vocabulary, microcopy
├── 08-iconography.md               ← System Identity glyph family
├── 09-trust-rules.md               ← provenance, no-auto, failure-as-feature
├── 10-claude-code-brief.md         ← single-file handoff for Claude Code
├── tokens/
│   ├── tokens.css                  ← CSS custom properties
│   ├── tokens.json                 ← Style Dictionary / Figma Tokens format
│   ├── tailwind.config.ts          ← Tailwind v3 theme extension
│   └── tokens.ts                   ← typed exports for TS apps
├── assets/
│   ├── logo/                       ← all marks, SVG + PNG
│   ├── glyphs/                     ← stage icons & UI glyphs
│   └── fonts.md                    ← font sourcing instructions
├── components/
│   ├── EdgeRails.tsx
│   ├── WireTopology.tsx
│   ├── CanonicalSpine.tsx
│   ├── DocumentAnatomy.tsx
│   ├── XCard.tsx
│   ├── StatusJourney.tsx
│   ├── LinkSpine.tsx
│   ├── MonumentNumber.tsx
│   └── primitives.tsx              ← Button, ConfidenceChip, SrcChip, etc.
└── showcase.html                   ← visual reference — open in any browser
```

---

## How to use this package

### For Claude Code (preferred path)
1. Drop the whole `design-system/` folder into the project repository.
2. Have Claude Code read `00-agent-quick-brief.md` first.
3. For major UI work, read `10-claude-code-brief.md` as the fuller implementation brief.
4. Point at `tokens/tailwind.config.ts` to wire up the theme.
5. Reference `components/` files as the source for the signature components.

**Do not use Lovable for ProcuLink.** All UI/UX and design decisions should run
through the local design system, `/frontend-design`, and Claude Design/reference
images. The locked direction is Direction 4 — The Bridge Layer, supported by
Direction 3 — System Identity.

### For a designer working in Figma
- Import `tokens/tokens.json` into Figma using the Tokens Studio plugin.
- Use `showcase.html` as the visual reference.
- The mark family lives in `assets/logo/`.

### For a developer reading the system manually
- Start with `01-foundations.md` to understand intent.
- Skim `showcase.html` for a visual map.
- Then `05-components.md` for the building blocks.

---

## The 60-second summary

ProcuLink is the order-transformation bridge between buyers and suppliers. The UI structurally shows itself as a bridge:

**Five signatures** — non-negotiable:
1. **Edge rails** — blue (buyer) + green (supplier) vertical rails frame the work area.
2. **Wire Topology** — the dashboard is a network diagram, not a grid of KPI cards.
3. **Canonical Spine** — order detail is source → vertical schema spine → output (3-column ETL).
4. **Document Anatomy** — source files always shown with labeled zone overlays + confidence.
5. **Cross-section card edge** — primary cards have a 3px brand-gradient edge strip.

**Supporting signatures** — navy chrome / light work area, 2px link-spine on every topbar, status-as-journey (5 stages), monumental display KPIs, System Identity logo family.

**Trust rules** — provenance everywhere, no silent automation, failure as a first-class view.

**Stack** — Next.js 15 App Router · Tailwind · shadcn/ui (restyled) · TanStack · Clerk · ASP.NET API. Light theme v1, tokens structured for future dark theme.

---

## Quick links

| If you want to… | Read |
|---|---|
| Understand the brand | `01-foundations.md` |
| Pick a color / size / radius | `02-tokens.md` |
| Wire Tailwind | `tokens/tailwind.config.ts` |
| Drop in CSS variables | `tokens/tokens.css` |
| Build the Bridge dashboard | `05-components.md` § Wire Topology |
| Build the order detail screen | `05-components.md` § Canonical Spine |
| Hand off to Claude Code | `10-claude-code-brief.md` |
| Keep agent context small | `00-agent-quick-brief.md` |

---

## Versioning & changes

- **1.0** — Locked The Bridge Layer direction. Initial release.
- Future versions: dark theme variant, motion-on-canvas refinements, illustration kit for marketing.

For the live prototype that demonstrates this system, see `ProcuLink Prototype.html` at the project root.
