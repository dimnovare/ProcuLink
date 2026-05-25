# 08 — Iconography

## The shape language

The ProcuLink mark is **one expression of a complete visual system**, not a separate logo glued onto a SaaS app. The construction rule is:

> A single curve linking two endpoint nodes. The buyer endpoint, the path between, and the supplier endpoint are all part of the same shape.

The same construction extends to:

- **The mark** (logo / favicon / lockup)
- **Edge rails** — the buyer/supplier endpoints are the rail markers; the work area is the path between
- **The canonical spine** — vertical link, gradient stroke, two endpoints
- **The link-spine** divider on topbars — the path, without endpoints
- **Stage glyphs** — each is a constrained variant of the same curve
- **Loading state** — the mark draws itself

The brand is the procurement flow itself.

## The mark

### Geometry
- 40×40 viewBox.
- Buyer endpoint (blue, `#1E66C9`) at `cx=8, cy=10`, radius 2.5.
- Supplier endpoint (green, `#2E8E3A`) at `cx=8, cy=30`, radius 2.5.
- A single curve: `M8 10h14a10 10 0 0 1 10 10v0a10 10 0 0 1-10 10H8`. From the buyer endpoint, right 14 units, arc down 20 units on the right, back left to the supplier endpoint. Stroke width 3.6, round linecap.
- Stroke uses a `linearGradient` from `#1E66C9` to `#2E8E3A` (top-left to bottom-right).

This shape is asymmetric on purpose. It's ownable — you can't confuse it with a chain link, a knot, or two intersecting squares.

### Variants

| Variant | Use | File |
|---|---|---|
| **Primary (color)** | Light backgrounds | `assets/logo/mark-primary.svg` |
| **Mono** | Navy/dark surfaces — uses `currentColor` | `assets/logo/mark-mono.svg` |
| **Lockup primary** | Mark + wordmark, color | `assets/logo/lockup-primary.svg` |
| **Lockup mono** | Mark + wordmark, currentColor | `assets/logo/lockup-mono.svg` |
| **Favicon** | 32×32 with navy rounded-square background, white mark | `assets/logo/favicon.svg` |
| **LinkedIn banner** | 1584×396, navy with link-dot grid + tagline | `assets/logo/linkedin-banner.svg` |

### Sizes

| Size | Use |
|---|---|
| 16px | Favicon, table-row leading icon, button icon |
| 20px | Inline in text |
| 24px | Sidebar logo, topbar logo |
| 32px | Marketing nav |
| 48–64px | Marketing hero |
| 96–160px | Auth page, empty state, splash |

The mark is **never** rotated, recolored outside the brand palette, modified, or used as a decorative element.

## Wordmark

Inter 700, tracking `-0.015em`, paired flush-left to the mark with **8px gap** at the 24px mark size (scales proportionally).

### System Identity wordmark accent
For marketing-only surfaces (auth page, marketing footer, brand stand-alone treatments), the wordmark can include a 2px link-spine gradient stripe to the right of "ProcuLink". Don't use it inside the app chrome — the sidebar uses the mono lockup, period.

## Stage glyphs (the same shape language)

Five icons representing the canonical crossing stages. Each is a constrained variant of the link-curve, **not** generic UI iconography:

| Stage | Glyph concept (System Identity construction) |
|---|---|
| **Parse** | A page outline with the link-curve's opening (no closure yet — input is unstructured) |
| **Normalize** | Both endpoints visible, no curve — fields about to be linked |
| **Validate** | The curve halfway drawn (testing the connection) |
| **Transform** | The mark itself — the buyer endpoint linked to the supplier endpoint |
| **Deliver** | The curve plus an arrow exiting the supplier endpoint |

Files: `assets/glyphs/stage-{parse|normalize|validate|transform|deliver}.svg` (to be created in the build).

All 24×24 viewBox, 1.75–3.6px stroke, `currentColor`. Sized down to 16px in compact contexts.

## UI icons

Use **Lucide** for general UI icons (back arrow, settings, search, etc.). Stroke width 1.75. Size 16px in UI, 14px in dense table rows. Never mix Lucide with another icon set.

Custom UI icons (chevrons, status dots) live in `assets/glyphs/` and follow the System Identity construction — never break it for one-off icons.

## System Identity in the product

| Surface | How the shape language shows up |
|---|---|
| Sidebar logo | Mark in mono, paired with wordmark |
| Topbar | 2px `--gradient-link-spine` at the bottom edge — the path without endpoints |
| Edge rails | Vertical version of the shape: blue endpoint marker → 4px rail → green endpoint marker |
| Canonical spine | Vertical 3px `--gradient-link-spine` with circular nodes |
| Empty state | The mark, large, with the curve drawing in on hover (motion M6) |
| Loading state | The mark, with the curve drawing in over 600ms — replaces all spinners |
| LinkedIn banner | Large mark + tagline + buyer→supplier lockup |

## Don'ts

- ✕ No 3D, no shadows, no skeuomorphism, no glow effects.
- ✕ No emoji as iconography.
- ✕ No filled vs outlined mixing — outlined construction throughout.
- ✕ No icon-only buttons without an accessible label.
- ✕ No "AI sparkle" iconography. AI is signaled by violet + the word "AI" + a confidence number. Never with a magic wand or sparkles.
- ✕ Don't introduce a third logo variant. Color, mono, and the lockup are it.
- ✕ Don't redraw the mark as two interlocking shapes (the older direction). System Identity is asymmetric — one curve, two endpoints.

## Favicon, app icons, and avatars

- **Favicon:** 32×32 navy rounded square (`rx=6`) with the white mono mark. See `assets/logo/favicon.svg`. Provide PNG fallbacks at 16/32/180 if your build needs them.
- **iOS / Android app icon:** 1024×1024 navy fill, white mono mark at ~60% scale, centered.
- **Slack / Discord avatar:** 256×256 navy with white mono mark.
- **LinkedIn company avatar:** 400×400 navy with white mono mark.
- **LinkedIn banner:** 1584×396 — see `assets/logo/linkedin-banner.svg`.
