# 02 — Tokens

All design tokens are sourced from `tokens/tokens.json` and emitted as:
- `tokens/tokens.css` (CSS custom properties)
- `tokens/tailwind.config.ts` (Tailwind theme extension)
- `tokens/tokens.ts` (typed exports for TS apps)

Edit the JSON; regenerate the rest.

## Color tokens

### Brand
| Token | Value | Use |
|---|---|---|
| `--brand-blue` | `#1E66C9` | Buyer side · structural accents · primary marketing CTA |
| `--brand-blue-deep` | `#0F4FA8` | Hover / pressed state of brand-blue |
| `--brand-blue-soft` | `#E3EDFB` | Buyer-tinted surfaces, blue chip backgrounds |
| `--brand-green` | `#2E8E3A` | Supplier side · success · completion |
| `--brand-green-deep` | `#1E6D29` | Hover / pressed state of brand-green |
| `--brand-green-soft` | `#E2F1E2` | Supplier-tinted surfaces, green chip backgrounds |

### Navy chrome
| Token | Value | Use |
|---|---|---|
| `--navy` | `#0B1A2F` | Sidebar + topbar background |
| `--navy-surface` | `#10243E` | Raised surfaces inside chrome (workspace switcher, search) |
| `--navy-border` | `#1C2F49` | Borders inside chrome |
| `--navy-text` | `#C5D2E4` | Default text on navy |
| `--navy-muted` | `#7C8DA6` | Secondary text on navy |

### Light surfaces
| Token | Value | Use |
|---|---|---|
| `--bg` | `#F6F7FA` | App background |
| `--bg-warm` | `#F8F6F1` | Marketing surfaces, auth page |
| `--surface` | `#FFFFFF` | Card surfaces |
| `--surface-2` | `#EFF2F7` | Subtle nested surfaces |
| `--border` | `#E2E6EE` | Default border |
| `--border-strong` | `#C6CDDA` | Active / focused border |

### Text
| Token | Value | Use |
|---|---|---|
| `--ink` | `#0B1A2F` | Default body text |
| `--ink-muted` | `#56627A` | Secondary text |
| `--ink-faint` | `#8A93A5` | Tertiary text, placeholders |

### Semantic
| Token | Value | Use |
|---|---|---|
| `--amber` | `#C97A14` | Warning |
| `--amber-soft` | `#FAEFD6` | Warning background |
| `--danger` | `#C53A3A` | Error, destructive action |
| `--danger-soft` | `#FBE3E3` | Error background |
| `--success` | same as `--brand-green` | Success states |
| `--ai` | `#6F4FCE` | **Only** for AI-generated content |
| `--ai-soft` | `#EEE7FB` | AI surface background |

### Gradient tokens
| Token | Value | Use |
|---|---|---|
| `--gradient-link-spine` | `linear-gradient(90deg, #1E66C9 0%, #1E66C9 35%, #2E8E3A 65%, #2E8E3A 100%)` | The signature spine line |
| `--gradient-bridge-deck` | `linear-gradient(90deg, #1E66C9, #2E8E3A)` | Bridge graphic on review header |
| `--gradient-rail-buyer` | `linear-gradient(180deg, rgba(30,102,201,0.2), #1E66C9 50%, rgba(30,102,201,0.2))` | Left edge rail |
| `--gradient-rail-supplier` | `linear-gradient(180deg, rgba(46,142,58,0.2), #2E8E3A 50%, rgba(46,142,58,0.2))` | Right edge rail |

## Typography

| Token | Value |
|---|---|
| `--font-sans` | `"Inter", system-ui, -apple-system, sans-serif` |
| `--font-display` | `"Bricolage Grotesque", "Inter", system-ui, sans-serif` |
| `--font-mono` | `"JetBrains Mono", ui-monospace, monospace` |

### Type scale
| Token | Size / line-height | Use |
|---|---|---|
| `--text-xs`  | 10px / 1.3 | Micro-labels, footnotes |
| `--text-sm`  | 11.5px / 1.4 | Table headers, captions |
| `--text-body-s` | 12.5px / 1.45 | Table rows, dense UI |
| `--text-body` | 13px / 1.5 | Default body |
| `--text-body-l` | 14px / 1.55 | Marketing body, descriptions |
| `--text-h4` | 16px / 1.35 | Card titles |
| `--text-h3` | 18px / 1.3 | Section titles |
| `--text-h2` | 24px / 1.2 | Page titles |
| `--text-h1` | 32px / 1.15 | Major in-app headings |
| `--text-display-s` | 36px / 1.05 | KPI monumental numbers |
| `--text-display` | 48px / 1.0 | Hero KPI |
| `--text-display-l` | 78px / 0.98 | Marketing hero |

All tracking is `-0.025em` for display sizes ≥24px; default elsewhere.

All numerics use `font-variant-numeric: tabular-nums` globally.

## Spacing

4px base unit. Use this scale only: `4 · 8 · 12 · 16 · 20 · 24 · 32 · 40 · 48 · 64 · 80 · 96`.

Tokens map to Tailwind's default 0.25rem scale, so `gap-3` = 12px, `p-4` = 16px, etc.

## Radii

| Token | Value | Use |
|---|---|---|
| `--radius-sm` | 4px | Pills, chips |
| `--radius` | 6px | Controls, small cards |
| `--radius-md` | 8px | Default card |
| `--radius-lg` | 10px | Featured card |
| `--radius-xl` | 12px | Hero card, modal |
| `--radius-full` | 9999px | Round buttons, badges |

**No pill-shaped containers** except chips and round badges. No `border-radius` above 12px on rectangular elements.

## Shadows

| Token | Value | Use |
|---|---|---|
| `--shadow-card` | `0 1px 2px rgba(11,26,47,0.04)` | Default card depth (used sparingly — most cards use border only) |
| `--shadow-pop` | `0 8px 24px rgba(11,26,47,0.10)` | Popovers, drawers, the supplier card hover-lift on hero |
| `--shadow-hero` | `0 50px 120px rgba(11,26,47,0.10), 0 8px 24px rgba(11,26,47,0.06)` | Marketing hero product peek |

Avoid heavy drop shadows. Borders carry depth, not shadows.

## Borders

- Default: `1px solid var(--border)` (#E2E6EE)
- Strong / focused: `1px solid var(--border-strong)` (#C6CDDA)
- Cross-section card edge: 3px strip on one edge (not all four) — see XCard component.

## Motion tokens

| Token | Value |
|---|---|
| `--ease-out` | `cubic-bezier(0.16, 1, 0.3, 1)` |
| `--ease-in-out` | `cubic-bezier(0.65, 0, 0.35, 1)` |
| `--duration-fast` | `150ms` |
| `--duration` | `250ms` |
| `--duration-slow` | `400ms` |
| `--duration-spine` | `1200ms` (link-spine activation) |
| `--duration-wire-loop` | `6000ms` (wire-topology traveller loop) |

All animation respects `prefers-reduced-motion: reduce` — disable wire travellers and the spine activation; keep state transitions instant.

## Z-index

| Token | Value | Use |
|---|---|---|
| `--z-base` | 0 | Content |
| `--z-rails` | 1 | Edge rails |
| `--z-sticky` | 10 | Sticky action bars |
| `--z-drawer` | 20 | Right drawer |
| `--z-topbar` | 30 | Topbar |
| `--z-popover` | 40 | Popovers, dropdowns |
| `--z-modal` | 50 | Modals |
| `--z-toast` | 60 | Toasts |
