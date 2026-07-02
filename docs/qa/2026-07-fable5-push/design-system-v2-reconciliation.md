# Claude Design v2 handoff — reconciliation with the live app (2026-07-02)

Handoff package: `handoff_v2/` (from `~/Downloads/ProcuLink (1).zip`). A full design-system
kit (tokens + shadcn theme + tailwind preset + 10 spec docs + component sources + styleguide).

## Verdict: the live app already IS the v2 design system.

A token-level diff (v2 `tokens.css` vs live `tailwind.config.ts` + `globals.css`) found:

| Layer | Match |
|---|---|
| Colors (28 tokens: blue/green/navy/surfaces/ink/amber/danger/ai) | **100%** identical |
| Spacing (10 steps, 4pt base) | **100%** |
| Type families + sizes (xs..display) | **100%** (one nuance below) |
| Radii (sm..xl, full) | match; **added** the missing `2xl` (14px) |
| Z-index scale | **100%** |
| Motion easings | match |

So this was a delta-close, not a redesign — consistent with the earlier `ProcuLink.zip` handoff.

## Applied (branch `feat/ds-v2-token-sync-2026-07-02`, additive only)
- **5-step shadow elevation ramp** added (`--shadow-md/lg/xl` + Tailwind `shadow-md/lg/xl`) alongside
  the existing `card/pop/hero` (kept). Lets components reach a semantic elevation level directly;
  nothing existing changes.
- **`--radius-2xl: 14px`** CSS var + Tailwind `rounded-2xl` (the class was already used; the var was implicit).

## Deliberate deviations — KEEP the live values (app is source of truth)
Documented so future design passes don't "correct" them back:
- **Primary button = brand-green** (not navy). Intentional per the in-app primary-action rule; navy is
  reserved for rare neutral CTAs. Handoff's shadcn `--primary` blue is a legacy artifact.
- **Motion durations 150/250/400ms** (handoff suggests 130/200/260). Slower/calmer chosen deliberately
  for a procurement app where actions carry financial weight; keep live.
- **h2 = 24px** live (handoff 22px). Minor; keep live scale.
- **Table row 38px desktop / 44px mobile** + **buyer-tinted hover** (handoff 44px / neutral hover).
  Density + side-context are improvements; keep live.
- **Card default radius `rounded-lg` (10px)**; `rounded-2xl` (14px) for featured/hero only.

## Net-new in the handoff, NOT applied (out of design-system-sync scope)
These are new FEATURE components / builds, not tokens — flagged for a separate opt-in decision:
- `EdgeRails.tsx` — blue/green vertical rails framing work areas (CSS `.railed` foundation exists; no React component).
- `DocumentAnatomy.tsx` — source-document overlay with confidence-zone annotations (green ≥90% / amber <90%).
- README "Remaining build items": **Fill-from-catalog** modal (item-code resolution) + **Customize-output-layout** modal + the **Issues-resolution blocker-cards** panel on the wired needs-work page.
- Already present (agent's "missing" list was wrong): `StatusJourney`, `WireTopology`, `CanonicalSpine`, `XCard` all exist.

## Locked rules — no conflicts
3-column Order Workshop (polish-only), semantic color law, and FABLE5_BRIEF §6 truthfulness flags
(EDIFACT/ERP/scanned-PDF/SFTP shown as assisted/coming-soon) all align with what the app already enforces.
