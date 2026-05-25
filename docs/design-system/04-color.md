# 04 — Color

## The four-bucket rule

Every color in the product belongs to exactly one of four buckets. If you can't classify it, it doesn't belong.

| Bucket | Tokens | Meaning |
|---|---|---|
| **Brand** | `brand-blue*`, `brand-green*` | Identity. Buyer (blue) vs Supplier (green). |
| **Chrome** | `navy*` | App frame. Sidebar + topbar only. |
| **Surface** | `bg`, `surface*`, `border*`, `ink*` | Neutral page substrate. |
| **Semantic** | `amber*`, `danger*`, `ai*` | Status. Warn / error / AI. Never decorative. |

There is **no** "primary purple," "accent teal," "decorative orange." If you find yourself wanting one, the answer is to use weight, position, or scale instead.

## Brand pairing

ProcuLink runs on **two brand colors plus navy chrome**. They are always paired with meaning:

```
BLUE    →  buyer / incoming / structure / left side / before-state
GREEN   →  supplier / outgoing / completion / right side / after-state
NAVY    →  the bridge frame itself (sidebar + topbar only)
```

**Never** use blue and green decoratively, only directionally. A "blue card" should mean "this is the buyer's side."

## Semantic system

| State | Token | Background | Foreground |
|---|---|---|---|
| Default / informational | `brand-blue` | `brand-blue-soft` | `brand-blue-deep` |
| Success / completed | `brand-green` | `brand-green-soft` | `brand-green-deep` |
| Warning / needs review | `amber` | `amber-soft` (`#FAEFD6`) | `amber` (`#C97A14`) |
| Error / failed | `danger` | `danger-soft` (`#FBE3E3`) | `danger` (`#C53A3A`) |
| AI-generated | `ai` (`#6F4FCE`) | `ai-soft` (`#EEE7FB`) | `ai` |

### Confidence thresholds
| Range | Color |
|---|---|
| ≥ 90% | `brand-green-deep` on `brand-green-soft` |
| 75–89% | `amber` on `amber-soft` |
| < 75% | `danger` on `danger-soft` |

Used consistently in: `<ConfidenceChip>`, the Document Anatomy zone overlays, field-level state on the Canonical Spine.

## Accessibility

All text meets **WCAG AA** on its intended background. Contrast notes:

| Pair | Ratio | Pass |
|---|---|---|
| `ink` (#0B1A2F) on `bg` (#F6F7FA) | 15.4 : 1 | AAA |
| `ink-muted` (#56627A) on `bg` | 6.4 : 1 | AA |
| `ink-faint` (#8A93A5) on `bg` | 3.6 : 1 | **AA only for text ≥ 18px / 14px bold** — never for body |
| `navy-text` (#C5D2E4) on `navy` (#0B1A2F) | 9.8 : 1 | AAA |
| `navy-muted` (#7C8DA6) on `navy` | 4.7 : 1 | AA |
| `brand-blue` (#1E66C9) on white | 4.8 : 1 | AA |
| `brand-green` (#2E8E3A) on white | 4.3 : 1 | AA |
| `brand-blue-deep` (#0F4FA8) on `brand-blue-soft` (#E3EDFB) | 8.2 : 1 | AAA |
| `brand-green-deep` (#1E6D29) on `brand-green-soft` (#E2F1E2) | 7.6 : 1 | AAA |
| `amber` (#C97A14) on `amber-soft` (#FAEFD6) | 4.5 : 1 | AA |
| `danger` (#C53A3A) on `danger-soft` (#FBE3E3) | 5.1 : 1 | AA |
| `ai` (#6F4FCE) on `ai-soft` (#EEE7FB) | 5.4 : 1 | AA |

### Color-only signaling is forbidden
Color is paired with at least one of: icon, label, weight, position. So a "red row" also says "⚠ Failed" or has a 3px left bar. Critical for color-blind operators.

## Do / Don't

### Do
- Use `brand-blue` for the primary CTA button (or `navy` if the screen is light and you want chrome continuity — pick one and stick with it per route group).
- Use `brand-blue-soft` for buyer-tinted backgrounds (hover state of buyer rows, the highlighted PO row on the Inbox).
- Use `brand-green-soft` for supplier-tinted backgrounds and the "ready" status pill.
- Use `navy` for sidebar + topbar only.
- Use `ai-soft` exclusively on cards/rows containing AI-generated content.

### Don't
- ✕ Don't use `brand-blue` for a "send" action. Sends are completion — use `brand-green` or navy.
- ✕ Don't use `navy` on body cards or buttons in the work area. Navy is chrome.
- ✕ Don't tint a row both blue and green at once. Pick a side.
- ✕ Don't use `ai` violet anywhere except for AI suggestions, AI-mapped lines, and AI confidence chips.
- ✕ Don't use semantic colors as decoration. An amber border means "warning." If nothing is wrong, don't use amber.
- ✕ Don't introduce new colors. If you need to differentiate a third category (e.g. a third file-type chip), use a neutral grey, not a new hue.
- ✕ Don't apply gradients to body surfaces. Gradients are reserved for the link-spine and rail tokens.

## File-type chip palette

These are the only places we use non-brand color, and only at chip-size:

| File type | Background | Foreground |
|---|---|---|
| `PDF` | `#FBEEEE` | `#B53F3F` |
| `XLSX` | `brand-green-soft` | `brand-green-deep` |
| `CSV` | `#EEF3F8` | `#345470` |
| `XML` / `cXML` | `ai-soft` | `#5E3DB0` |
| `EDI` | `amber-soft` | `amber` |
| `EMAIL` | `#E9EDF3` | `#4A5568` |
| `API` | `#E3F0E3` | `brand-green-deep` |
| `JSON` | `#FFF4D6` | `#846100` |

These are intentionally muted — chips should read as labels, not signage.

## Hover & focus states

| State | Treatment |
|---|---|
| Hover on a row | Background shifts to `surface-2` (#EFF2F7) |
| Hover on a buyer-tinted row | Background shifts to `brand-blue-soft` |
| Hover on a button-primary | Background `brand-blue-deep` |
| Focus visible | 2px `brand-blue` outline at `--radius` offset 2px |
| Active / pressed | Slightly darker background, no scale transform |

Never use opacity changes for hover (low contrast). Always shift the background or border.
