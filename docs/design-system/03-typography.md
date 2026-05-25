# 03 — Typography

## Font stack

| Role | Family | Weights used | Fallback |
|---|---|---|---|
| **UI** | Inter | 400 / 500 / 600 / 700 | `system-ui, -apple-system, sans-serif` |
| **Display** | Bricolage Grotesque | 500 / 600 / 700 / 800 | Inter, system-ui |
| **Mono** | JetBrains Mono | 400 / 500 / 600 / 700 | `ui-monospace, "SF Mono", Menlo, monospace` |

Source from Google Fonts:
```html
<link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=Bricolage+Grotesque:wght@500;600;700;800&family=JetBrains+Mono:wght@400;500;600;700&display=swap" rel="stylesheet"/>
```

## When to use what

### Inter (UI)
- All body text
- Labels, buttons, form fields
- Table cells (except SKUs / POs / IDs / file paths)
- Navigation, sidebar, topbar
- Toasts, drawers

### Bricolage Grotesque (Display)
- **KPI monumental numbers** on the Bridge dashboard
- Page titles on the Bridge dashboard, Inbox, review header, marketing
- Marketing H1 (the "Buyers on one side." hero)
- Grand total in the canonical spine
- Section titles like "Order topology" / "Cross a new order"

Display is *visually heavy*. Use it sparingly — once or twice per screen. If a heading doesn't carry weight as a primary visual anchor, use Inter.

### JetBrains Mono (Mono)
- **All technical identifiers:** PO numbers, SKUs, file paths, field paths (`Order/@orderID`), IDs
- File-type chips (`PDF`, `cXML`, `EDI`)
- Output preview (cXML / JSON / CSV in the right column of the spine)
- Confidence percentages
- Time durations (`1m 42s`)
- Volume metrics (`412/wk`)
- Anything an operator would copy-paste

## Scale (in product)

```
xs       10px        Pills, footnotes, micro-labels
sm       11.5px      Table headers, captions, small chips
body-s   12.5px      Dense tables, secondary body
body     13px        Default body
body-l   14px        Marketing body, comfortable reading
h4       16px        Card titles
h3       18px        Section titles
h2       24px        Page titles
h1       32px        Major in-app headings
disp-s   36px        Monumental KPI
disp     48px        Hero KPI, dashboard signature numbers
disp-l   78px        Marketing hero H1
```

## Tracking & weight

| Size range | Tracking | Default weight |
|---|---|---|
| ≥ 32px (h1, display) | `-0.025em` | 600 |
| 24–31px (h2) | `-0.02em` | 600 |
| 18–23px (h3) | `-0.015em` | 600 |
| 14–17px (h4, body-l) | `-0.01em` to `0` | 500 |
| ≤ 13px (body, sm, xs) | `0` | 400 |

Bold text in body copy: weight 600, never 700.

## Numerals

Globally apply `font-variant-numeric: tabular-nums` so numbers in tables stack correctly. Already in `tokens.css`.

For KPI display numbers, also apply `font-feature-settings: "cv11", "ss01"` if Inter is in use — improves the `4` and `1` shapes.

## Line height

- Display sizes (≥32px): 1.0–1.05
- H1 / H2: 1.15–1.2
- H3 / H4: 1.3–1.35
- Body: 1.45–1.55
- Table rows: 1.25 (because the row height handles spacing)

## Pairings (canonical examples)

### Marketing hero
```
H1 (Bricolage Grotesque 78px 600, tracking -0.035em):
  "Buyers on one side.
   Suppliers on the other.
   We are the bridge."

Sub (Inter 17px 400, ink-muted, max-width 600px):
  "ProcuLink turns messy purchase orders into supplier-ready outputs.
   Upload Excel, PDF, cXML or EDI orders, review only exceptions, deliver clean."
```

### Bridge dashboard page header
```
Title (Bricolage Grotesque 30px 600, tracking -0.025em):
  "Order flow"

Subtitle (Inter 13px 400, ink-muted):
  "Today · Mon 12 Jan 2026 · 18 lanes · 6 suppliers"
```

### KPI tile
```
Label (Inter 11px 600, uppercase, tracking 0.06em, ink-muted):
  "ORDERS THIS WEEK"

Value (Bricolage Grotesque 36px 600, tracking -0.035em):
  "1,284"

Sub (Inter 12px 500, brand-green-deep):
  "+18% vs prev"
```

### Table row
```
PO (Mono 11.5px 600, ink):           PO-2026-008412
Buyer (Inter 12.5px 500, blue-deep): Heinrich Industries
Supplier (Inter 12.5px 500, green-deep): Acme Components Ltd.
Status (Inter 11px 500, in pill):    Needs review
```

### Canonical spine node
```
Label (Inter 10px 600, uppercase, tracking 0.05em, ink-faint):
  "PO NUMBER"
Value (Mono 12.5px 500, ink):
  PO-2026-008412
Confidence (Mono 9.5px 700, in chip):
  99%
Source ref (Mono 9.5px 600, brand-blue):
  ← header
Output ref (Mono 9.5px 600, brand-green-deep):
  → Order/@orderID
```

## Don'ts

- ✕ Don't use Bricolage Grotesque for body copy or buttons. It's a display face only.
- ✕ Don't mix Inter and Geist (or Inter and any other UI sans). One UI face.
- ✕ Don't use mono for buyer/supplier names — those are proper nouns, set in Inter.
- ✕ Don't italicize Inter body text. Use weight changes for emphasis.
- ✕ Don't go below 10px anywhere except a `.tiny` legal footnote on marketing.
- ✕ Don't underline anything except inline hyperlinks in body copy.
