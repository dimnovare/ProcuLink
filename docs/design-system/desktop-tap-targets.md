# Desktop tap targets — WP-31b

The deferred half of WP-31: ~50 desktop controls under 44px, several under WCAG SC 2.5.8's
hard 24px floor. A global padding increase would ship a density regression, so this is a
rule, not a sweep.

## 1 · The rule — size by consequence and frequency, not by control type

Three tiers, one token set, applied at fine-pointer breakpoints:

| Tier | Min box | What qualifies | Examples |
|---|---|---|---|
| **Committing** | **40×40** (44 coarse) | Changes data, sends, deletes, pays, or leaves the page | Save, Send to supplier, Delete, Add webhook, plan CTA, every marketing/pricing link |
| **Operating** | **32×32** visible, **40×40** hit | Repeated inside a dense working surface; reversible | Row actions, filter chips, tab triggers, toggles, sort headers |
| **Incidental** | **24×24** hard floor | Rare, non-destructive, duplicated elsewhere | Row disclosure carets, inline copy, drawer close |

Density is defended only where it is **earned** — inside tables and repeating rows.
Settings and marketing pages are not dense surfaces; they get tier 1. Nothing ships
below 24px.

## 2 · What keeps small controls hittable

1. **Hit area ≥ visible box** — pad with a pseudo-element, never the layout:
   ```css
   .pl-hit{position:relative}
   .pl-hit::after{content:"";position:absolute;inset:-8px}
   ```
   32px visible → 48px hit, zero layout cost. This is the primary mechanism.
2. **Spacing is part of the target** — min **8px** between adjacent interactive boxes,
   **12px** when either is destructive. (SC 2.5.8 can be met by spacing alone; we don't
   rely on that.)
3. **Hover/focus plate at hit size** — a `surface2` plate so the affordance reads bigger
   than the glyph. Focus ring is the global 2px blue + 4px halo.
4. Never two tier-3 targets adjacent without a divider or 12px gap.

## 3 · Smallest correct control at 13px type

**28px tall** — 13px text at 18px line-height + 5px vertical padding, 10px horizontal
padding, radius 6. Clears the 24px floor with margin and still reads as a real button.
Reserved for row-level secondary actions. **32px stays the default**; 28 is the floor,
not a licence.

## 4 · Named fixes

- **`/settings`** — not a dense surface. All 17 controls → tier 1 (40px); inputs to a
  **36px** min height (the 13px-tall input is a defect, not a density choice). Cost
  ≈ +90px page height per section. Acceptable: settings is scanned, not operated.
- **`/operations/webhooks`** — dense list. Row actions → tier 2 (32 visible / 48 hit).
  The 32px button with 12.5px text keeps its box and gains the hit pad + hover plate.
  Page-level **Add webhook** → tier 1 (40px).
- **`/pricing`** — the 20.3px link violates the hard floor. Marketing is tier 1: 44px,
  no exceptions.

## 5 · Tokens (one system, no per-screen theming)

```
--pl-target-commit: 40px;   /* 44px @ (pointer: coarse) */
--pl-target-operate: 32px;  /* + .pl-hit → 48px */
--pl-target-min: 24px;      /* hard floor, never crossed */
--pl-target-gap: 8px;       /* 12px when destructive-adjacent */
--pl-control-h-sm: 28px;    /* smallest legal control @13px type */
--pl-input-h: 36px;
```

## 6 · Outstanding

Before/after captures of `/settings` and `/operations/webhooks` at **1280×900**. The
density cost has to be judged visually, not asserted — that review gates the sweep.
