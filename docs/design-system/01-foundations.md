# 01 — Foundations

## What ProcuLink is

ProcuLink is an **AI-assisted order-transformation bridge**. It sits between buyers, suppliers, ERPs, and procurement systems. It ingests messy purchase orders (PDF, Excel, CSV, XML, cXML, EDI, JSON, email), normalizes them into a canonical PO model, validates them against supplier-specific rules, lets a human review only the exceptions, and emits a clean supplier- or buyer-ready output.

**Tagline:** *"Connecting Procurement — the missing link between buyers and suppliers."*

**Marketing line:** *"Buyers on one side. Suppliers on the other. We are the bridge."*

## What it is *not*

A chatbot · a generic PDF parser · a Zapier clone · a marketplace · a procurement CRM · only a PunchOut tool.

It **is** a vertical integration workbench for order documents and supplier-specific transformation rules.

## Who uses it

- Order-processing operators at distributors, wholesalers, IT resellers, industrial/medical/building suppliers
- Integration specialists who maintain supplier mappings and validation rules
- Procurement managers who watch the flow across all suppliers

They are **power users**. They know SKUs, EDI segments, cXML payloads, PunchOut quirks. Treat them as such.

## Core loop

```
Parse → Normalize → Validate → Review → Transform → Deliver → Learn
```

Every screen serves this loop. Every screen makes the underlying order visible. We never hide the source file behind a wizard.

## Design principles

### 1. The product is a bridge — and shows itself as one
Every order-handling surface has visible architecture: edge rails frame the work area, the canonical spine sits at the center of the review screen, wires arc between docks on the dashboard. The metaphor is structural, not decorative.

### 2. Operator workbench, not marketing site
Density over whitespace. Information per pixel matters. We are not selling — we are working. Bodies are 13–14px; tables run 36px rows; the inbox shows 30+ orders above the fold.

### 3. Show the data
Every screen makes the underlying order, mapping, or rule visible. No tooltip if a column will do. No modal if an inline editor will do.

### 4. Exceptions, not everything
Default views surface only what needs human attention. Everything that passed silently is collapsed.

### 5. Provenance everywhere
Every AI suggestion shows confidence, source, and a one-click "show me where this came from in the source file." We never auto-apply.

### 6. Keyboard-first
Every list, mapping, and review action has a shortcut. Tooltips show them.

### 7. Trust through transparency
Failures are loud, recoverable, and explained. Auto-process is per-supplier, deliberate, audited.

### 8. Restraint with color
Blue = buyer / incoming / structure. Green = supplier / outgoing / completion. Violet = AI-generated content. Amber = warning. Red = error. Nothing else. Ever.

### 9. The mark is one expression of a system
The brand is not a logo glued to a SaaS app. The link-spine, the edge rails, the stage glyphs, the loading state, and the logo are all the same shape language.

### 10. Motion communicates state, not flair
Six motion patterns, each with a single job. All respect `prefers-reduced-motion`.

## Brand voice

| Trait | Example | Anti-example |
|---|---|---|
| **Operational, not aspirational** | "Cross the bridge → " | "Transform your procurement" |
| **Specific, not generic** | "1m 42s avg crossing" | "Lightning-fast" |
| **Plain English, not jargon-padded** | "Auto-process is OFF for this supplier" | "AI-powered intelligent routing" |
| **Honest about limits** | "AI suggests ACM-PLT-200×200×4 (84%)" | "Smart matching" |
| **Direct, not chatty** | "Acme rejects negative quantities." | "Hmm, looks like Acme might not love that quantity." |

## Vocabulary (use these terms consistently)

| Term | Meaning |
|---|---|
| **Bridge** | The product itself. Also the dashboard route. |
| **Crossing** | A single order transit (parse → deliver). |
| **Dock** | A supplier or buyer endpoint. |
| **Lane** | A buyer↔supplier pairing. |
| **Spine** | The canonical PO model, rendered as the center column on the review screen. |
| **Anatomy** | The source-document zone overlay. |
| **Wire** | A buyer↔supplier connection drawn on the Bridge dashboard. |
| **Output** | The supplier-ready file (cXML, CSV, EDI, etc.) we produce. |
| **Map** | A buyer-SKU ↔ supplier-SKU pairing. |

## Trust rules (designed in, not aspirational)

1. **Provenance everywhere** — every AI suggestion has confidence + source attribution + a one-click jump to the matching source zone.
2. **No silent automation** — Auto-process is per-supplier, opt-in, with a visible audit trail in the Crossings Log. Never a default.
3. **Failure is loud, recoverable, and explained** — the Failed view is the most useful screen in the product. Every failure shows what the supplier said, what we sent, what we'd retry, and a one-click fix-and-resend.

These are not principles; they are non-negotiable product rules.
