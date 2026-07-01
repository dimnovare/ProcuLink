> # ⚑ PRODUCTION CAPTURE
> The screenshots in this document are **full-page captures of the LIVE production app** at `https://proculink.eu` (org **"Dim's Organization"**, Pilot plan), captured **2026-06-30** with **real production data** — not mock/staged data. Real volumes at capture time: **27 orders** (11 need review, 1 failed), **15 suppliers**, **20 connections**, **1 invoice**, **0 ASNs**, **no drafts**. Empty/low-data states you see (ASNs, drafts, invoices) are the *real* current states. Screenshots live in `./screenshots-prod`. The page spec text below is identical to the mock handoff (same components/routes); only the rendered data differs.

# ProcuLink — Page-by-Page Redesign Spec (Claude Design handoff, 2026-06-29)

---

## 1. How to use this document

This is the **single master spec** for the ProcuLink app redesign. It is one continuous document: this front-matter frames the whole product, the design system as-built, the global shell, and the cross-cutting principles; then **one numbered section per app page** follows (`00. Global App Shell`, then `01-…` through `25-…`). Each page section is self-contained — file paths, what the page is, who uses it, current layout, data shown, every interactive element, every overlay, all states, responsive behaviour, the current UX issues, and ranked redesign recommendations.

- **Screenshots** for each page live in `./screenshots` (228 PNGs, rendered fresh 2026-06-29 from the live app in **mock mode** — real components, staged data). Every page was shot at **four viewports** — mobile **390**, tablet **768**, desktop **1440**, HD **1920** — plus a capture of **every modal / drawer / tab / popover / inline-panel state** the page can open. Filenames follow `NN-base-<viewport>-<slug>.png` for the plain page and `NN-<state>-<viewport>-<slug>.png` for each overlay state (e.g. `12-add-mapping-modal-desktop-1440-library-mappings.png`). Each page section embeds its own screenshots inline under a **Screenshots** heading. Read each page section *with* its screenshots, and read `00. Global App Shell` first — it is the chrome every page lives inside.
- **The ask is POLISH, not a redesign.** The founder explicitly rejected "start fresh" concepts. **Keep** the navy `#0B1A2F` + violet/blue `#1E66C9` "Bridge Layer" brand, every route, every component name, and the existing vocabulary. **Change** hierarchy, spacing rhythm, density, consistency, and states. When a recommendation in a page section conflicts with this rule, the rule wins: refine the existing direction, do not replace it.
- Each page section references the **12 cross-cutting principles** in section 5 by name/number (older sections call them "DESIGN BAR #N"). Treat section 5 as the shared rubric and the per-page sections as the concrete instances; fix the twelve at the system level and the per-page work shrinks.
- The bar to hit is **calmer and more consistent, not flashier.** When it's done, a procurement coordinator should feel the product is *precise and trustworthy*: one spacing rhythm, one badge language, real states everywhere, numbers that line up, one clear action per screen, and the order-review/mapper reading as obviously the centre of gravity.

---

## 2. What ProcuLink is + who uses it

ProcuLink is a **B2B outbound procurement bridge**. It turns a messy purchase order — arriving as CSV, Excel, PDF, XML, cXML, UBL/Peppol, EDIFACT, or X12, by browser upload, email, REST API, or SFTP/S3 polling — into **the exact file a specific supplier accepts**, validates it in plain language, previews the real output ("this is exactly what {supplier} receives"), delivers it through that supplier's required channel (HTTP/SFTP/FTPS/email/ERP), and **remembers the setup** so the next order for that supplier just works. The full loop is `Parse → Normalize → Validate → Review exceptions → Transform → Deliver → Learn`, and the per-supplier setup is captured as a **versioned Supplier Connection** (draft → tested → live → previous) so deliveries are reproducible and roll-back-able. ProcuLink never equates "HTTP 200" with "the supplier accepted the order" — validation and acceptance are first-class, honest concepts.

The primary user is the **procurement coordinator** — a non-technical operator who runs the daily PO flow, triages what needs a human, and approves the plan without a committee. They live in the Inbox and the order-review/mapper screen. A secondary user is the **integration expert / operator / admin** who seeds mappings, configures delivery credentials, mints API keys, and works the standards/connectors/REDACTED-ITEM surfaces. The product must be effortless for the coordinator (smart defaults, plain language, AI suggestions with visible confidence + provenance, one clear next action) while staying dense and honest enough for the expert (raw field names, standards mappings, truthful delivery states) — **one great experience with progressive disclosure, never a novice/expert mode toggle**. Power affordances surface through the Command Palette (Cmd+K), per-table column selectors, and "Show advanced / Show standards" disclosures.

---

## 3. The current design system (as-built)

These are the **actual tokens in the codebase** (`src/app/globals.css`, `tailwind.config.ts`). They are the canon to preserve and apply *consistently* — the central polish problem is not the tokens themselves, it's that most screens bypass them with inline `style={{}}` hard-coded hex and off-scale pixel literals. Keep the tokens; eliminate the drift.

### Colour

| Role | Token(s) | Value(s) |
|---|---|---|
| Navy chrome (sidebar/topbar) | `--navy` / `--navy-surface` / `--navy-border` / `--navy-text` / `--navy-muted` | `#0B1A2F` / `#14253D` / `#1F3252` / `#C8D1E0` / `#7C8DA6` |
| Brand blue (buyer / incoming / structure / trust) | `--brand-blue` / `-deep` / `-soft` / `-soft-2` | `#1E66C9` / `#0F4FA8` / `#EAF0F8` / `#DCE8F7` |
| Brand green (supplier / outgoing / success / completion) | `--brand-green` / `-deep` / `-soft` / `-soft-2` | `#2E8E3A` / `#1E6D29` / `#E9F1EA` / `#D8EBDA` |
| Violet / AI (AI-generated content **only**) | `--ai` / `-soft` / `-border` | `#6F4FCE` / `#F0EAFB` / `#D9CCF4` |
| Amber (warning / needs-review) | `--amber` / `-soft` | `#B36D14` / `#FAF1DD` |
| Danger (blocking / failure) | `--danger` / `-soft` | `#B43838` / `#FAE6E6` |
| Surfaces | `--bg` / `--bg-warm` / `--surface` / `--surface-2` | `#F6F7FA` (page canvas) / `#F8F6F1` / `#FFFFFF` / `#F1F3F7` |
| Borders | `--border` / `-strong` / `-faint` | `#E5E8EE` / `#CBD0DA` / `#EEF0F4` |
| Ink (text) | `--ink` / `-muted` / `-faint` | `#0B1A2F` / `#5E6779` / `#98A0AE` |
| Signature gradient | `--gradient-link-spine` / `--gradient-bridge-deck` / rail-buyer / rail-supplier | blue→green `linear-gradient(90deg, #1E66C9 … #2E8E3A)` |

> Colour semantics are load-bearing: **green = success/output/live**, **red = blocking**, **amber = warning/review**, **blue = in-progress/buyer/structure**, **violet = AI only**, **neutral/grey = new/draft**. Status must never be carried by colour alone. A separate shadcn primitives layer maps these into HSL CSS vars (`--primary` = brand-blue, `--destructive` = danger, etc.) — note its `--border` (`#E2E6EE`) is a *slightly different* value from the DS `--border` (`#E5E8EE`), one small source of drift.

### Typography

- **Families:** `--font-sans` **Inter** (UI/body, 400/500/600/700); `--font-display` **Bricolage Grotesque** (page titles `.page-title`, `.monument` KPI numbers, 500–800); `--font-mono` **JetBrains Mono** (codes, PO#, prices, values, standards refs, 400–700).
- **`html` sets `font-variant-numeric: tabular-nums` globally** — but many call-sites override it with proportional figures, so tabular alignment is only *partially* realised in practice (see principle 3).
- **Scale (`tailwind.config.ts` `fontSize`):** `xs 10px · sm 11.5px · body-s 12.5px · body 13px · body-l 14px · h4 16px · h3 18px · h2 24px · h1 32px · display-s 36px · display 48px · display-l 78px` (each with bundled line-height + tracking).
- **Weights:** 400 body · 500 label · 600 heading/display · 700 mono-emphasis/eyebrows.

### Radius / shadow / spacing / motion / icons

| System | Tokens | Values |
|---|---|---|
| Radius | `--radius-sm / – / md / lg / xl` | `4 / 6 / 8 / 10 / 12 px` (shadcn `--radius` = `0.5rem`) |
| Shadow (3 tiers) | `--shadow-card` / `--shadow-pop` / `--shadow-hero` | `0 1px 2px …04` (cards) / `0 8px 24px …10` (popovers/sheets/drawers) / large hero |
| Spacing | intended 4/8 rhythm; named DS spacings | `rail 4 · card-edge 3 · spine 3 · topbar 52 · sidebar 220` |
| Layout vars | shell + page | `--sidebar-w 236 · --topbar-h 56 · --tap-min 44 · --container-narrow 1040 · --container-wide 1480 · --page-gutter 16→24→34` |
| Motion | `--ease-out` (.16,1,.3,1) · durations `fast 150 / 250 / slow 400` (+ spine 1200, wire-loop 6000) | all neutralised under `prefers-reduced-motion` (global reset + per-component guards) |
| Icons | **Lucide** (the mandated set) | shadcn/ui + Tailwind as primitives |

**Where it's consistent vs where it drifts.**
- **Consistent (keep, propagate):** the token *definitions* are complete and well-named; the shell (navy sidebar/topbar) is largely token-driven; the global `:focus-visible` ring (blue, lighter `#6BA5F0` on navy chrome) and the reduced-motion handling are genuinely global; **`BridgeLoader`/`BridgePageLoader`** is the one canonical loader; the shared component classes `.pill / .pill-*`, `.conf / .conf-*`, `.src-* / FileChip`, `.journey / StatusJourney`, `.monument`, `.xcard`, `.toggle`, `.skel`, plus `PageShell` / `PageHeader` / `UnifiedStatusBadge` / `DSPrimitives` exist as a shared vocabulary.
- **Drifts (the core polish work):** almost every *screen* is built from inline `style={{}}` with literal hex (`#0B1A2F`, `#5E6779`, `#E5E8EE`, `#B36D14`, `#F0F2F6`…) and off-scale px (`9.5 / 10.5 / 11.5 / 12.5 / 13.5 / 14.5 / 19 / 22 / 30`, `padding "9px 10px"`, radii mixing `6/7/8/10/12`, control heights `28/30/32/34/36/38/40`) instead of the tokens above. `BillingSection.tsx` and `PullIngressSettings.tsx` carry an entire *second* hardcoded colour system; `InboxView.tsx` and `SupplierDockProfile.tsx` declare their own hex constants. Status is encoded by 3–5 different pill systems per screen. Glyph/emoji characters (`↻ ↑ 🔍 ▦ ⊘ ⚠ › → ↓ ⠿ ✦ ✕`) stand in for Lucide; the command palette uses ASCII symbols. This drift is the #1 "looks unfinished" signal — closing it is most of the win.

---

## 4. Global shell & information architecture

The app shell (`src/app/(app)/layout.tsx`) is a fixed full-viewport flex frame (`flex h-dvh overflow-hidden`, canvas `#F6F7FA`): a **navy left sidebar** + a **navy topbar** over a single scrollable `<main>`.

- **Sidebar** (`BridgeSidebar`, navy `#0B1A2F`, **232px expanded / 64px collapsed rail**; full-screen drawer on mobile with Escape, body-scroll-lock, focus-trap, focus-restore): logo header (`ProcuLinkMark` + wordmark + collapse toggle), a **read-only workspace badge** (org initials + name + plan — deliberately no switcher), the grouped nav (scrollable), and a footer ("Talk to a human" `mailto` + "Back to site"). Forced to the icon rail in the md→lg band; honors `localStorage["pl-side"]` at lg+.
- **Topbar** (`BridgeTopbar`, navy, **56px**): mobile menu button, demo-data badge (mock only), auto-breadcrumb from pathname (`useAutoCrumb`) or mobile page-title, the **Setup-progress chip** ("Setup n/6"), global search, **Notifications bell** (amber unread badge), **Help "?"**, and the account avatar (Clerk `<UserButton>`). A 2px blue→green `link-spine` gradient sits on its bottom edge and re-animates on navigation (frozen under reduced-motion).
- **Command Palette (Cmd/Ctrl+K)** — the canonical home for power affordances (raw envelopes, hotkeys, standards mappings, jump-to order/supplier/buyer). **Help slideover** (right drawer). **Two global toast layers** (`<Toaster/>` + `<Sonner/>` — should be unified to one).
- Pages render inside `PageShell` (`variant="wide"` ≈ 1480px for tables/dashboards, narrow ≈ 1040px for forms; responsive gutter 16→24→34px) with a `PageHeader` (one `h1` in Bricolage Grotesque 28→30px/600 + a muted subtitle + a right-aligned actions slot).

### IA map — the 25 app pages by area

(The number is the spec section. Nav groups are flag-gated; a `LAUNCH_CORE_ONLY` default trims the nav to a core subset.)

| Area | Pages (section · route) |
|---|---|
| **★ Core product** (where coordinators live) | 01 Dashboard "Bridge" · `/bridge` — 02 Order inbox / work queue · `/inbox` — **03 Order review / mapper (the money screen)** · `/inbox/[orderId]` — 04 Upload an order · `/upload` — 25 Confirm item codes (magic-mapping preview) · `/upload/preview/[orderId]` |
| **Supplier setup** (the reusable connection flow) | 05 Suppliers directory · `/library/suppliers` — 06 Supplier setup hub (detail, 7 tabs: Overview/Mappings/Catalog/PO-Mapping/Delivery/Validation-rules/History) · `/library/suppliers/[id]` — 07 Connections (versioned supplier connections) · `/connections` — 08 Connection detail (lifecycle verbs Edit-mapping/Make-live/Test/Restore) · `/connections/[connectionId]` |
| **Library** (reusable config) | 12 Item/code mappings · `/library/mappings` — 13 Validation rules · `/library/rules` — 14 Rule definitions · `/library/rule-definitions` — 15 Output templates · `/library/templates` — 16 Buyers · `/library/buyers` — 17 Standards reference (UBL/EDIFACT/X12/cXML) · `/library/standards` |
| **Operations** (the operator's monitoring) | 18 Exceptions queue · `/operations/exceptions` — 19 System health · `/operations/health` — 20 Delivery log · `/operations/log` — 21 Connectors · `/operations/connectors` — 22 Webhooks · `/operations/webhooks` |
| **Inbound docs** (flag-gated `NEXT_PUBLIC_INBOUND_ENABLED`) | 09 Drafts · `/drafts` — 10 Invoices · `/inbound/invoices` — 11 ASNs · `/inbound/asns` |
| **Settings + Admin** | 23 Settings hub (org / billing / email IMAP / SFTP / S3-R2 / API keys / connectors) · `/settings` — 24 Owner Admin (revenue / customers / manual invoicing) · `/admin` (allowlist-gated) |

(Public marketing + onboarding — landing, pricing, how-it-works, welcome/first-run checklist — carry a lighter touch and stay in the same brand; out of the 25-page app scope but follow the same canon.)

---

## 5. The 12 cross-cutting redesign principles

The founder's original 10-point polish pass, extended to 12 with the two patterns that recur most across the page sections (mapper human-field-name leading; truthful delivery/health states). Each principle = the rule + the concrete instances to fix. Page sections cite these by number (older sections say "DESIGN BAR #N").

1. **One spacing rhythm.** Adopt a strict **4/8px** scale for padding, gaps, and section spacing; replace inline px literals. *Instances:* the shell logo header `"16px 12px 12px 16px"`; inbox `padding:"9px 10px"`; mapper gaps 5/6/7/10/12; supplier-detail `py-3.5`/`8px 12px`/`11px 14px`; settings `11px 0`/`13px 15px`/`16px 18px`. Inconsistent gutters are the #1 "unfinished" signal.

2. **One type scale + weight hierarchy.** Hierarchy by **size + weight** (600 heading / 500 label / 400 body), not colour. Use the defined scale; kill fractional sizes (`9.5/10.5/11.5/12.5/13.5/14.5`). *Instances:* dashboard, inbox, upload, supplier-detail (9 sizes 10–15px) and settings (12 sizes) all carry meaning in low-contrast `#5E6779` / `--ink-faint` / navy `#C8D1E0` greys that risk falling below 4.5:1 — promote to weight/size; audit `#98A0AE`, `#AEB6C4`, `#7A8395`, `#566982` on tints.

3. **Tabular figures for all numbers.** PO#, quantities, prices, totals, counts, ages, dates, timestamps, "Page X of Y", usage bars in `tabular-nums`, so columns don't jitter and money right-aligns. *Instances:* PO#/Value use mono (good), but inbox line/issue counts + "2m ago", supplier-detail KPI values + catalog prices + confidence %, settings API-key dates + failure counts, and log timestamps are proportional and jitter.

4. **One status-badge system.** Every status/health/state pill — order status, delivery state, connection lifecycle, supplier health, exception severity, AI/confidence, "Active/Paused/Revoked/Coming soon/Fixed", failure counts — shares **one** shape/size/padding and the same semantics (green=success/live · amber=review · red=blocking · blue=in-progress · neutral=new/draft) **+ an icon or word, never colour alone**. *Instances:* the inbox shows the *same* state twice (a `StatusJourney` "Pipeline" column **and** a status pill — collapse to one + an optional progress affordance in hover/popover, reclaim ~300px); the dashboard runs **five** vocabularies on one screen; the connections list paints **every** row green incl. unpublished drafts; supplier-detail uses 4 (`.pill`, `.chip`, version pill, severity dots); settings Connectors uses 3 pill shapes; the shell uses 3 (blue Inbox count, amber notifications, navy setup chip). Unify on `UnifiedStatusBadge`.

5. **One table/list density.** Pick one row height (48–56px) + cell padding + low-contrast `gray-200`/`--border` gridlines + sticky header + sortable affordance with `aria-sort`, and apply it to every list (inbox, suppliers, mappings, rules, rule-definitions, templates, buyers, standards, exceptions, delivery log, connectors, webhooks, API keys, catalog, acceptance rules). *Instances:* inbox row states are five hand-rolled hex backgrounds applied via JS `onMouseEnter/Leave` (should be CSS) and use two gridline greys; supplier-detail's Mappings (`px-5 py-3`) vs Catalog (`6–7px 10px`) vs Acceptance (`px-4 py-3`) all differ; the dashboard "In transit" list, the connections link-card stack, and the settings webhook flex-rows should be real tables.

6. **Real states on every data surface.** Each list/panel needs a real **empty** (nothing-yet + the next action), **loading** (skeleton via `BridgeLoader` / `.skel`, never a bare spinner or blank), and **error** (specific reason + Retry). *Instances:* supplier-detail Overview is a dead hero in real mode (four `—` cards, no next step) — make it a setup-progress hub; Delivery/Catalog/Acceptance and settings-Connectors show bare "Loading…" text instead of skeletons; keep the header/toolbar mounted in error states (inbox replaces the whole screen — don't); de-duplicate empty/error copy across desktop+mobile branches; differentiate 401/403/timeout/500 instead of one generic message.

7. **One primary action per screen.** The single most important button is visually dominant (green for in-app commit/Send, ≥44px); demote the rest to outline/ghost; separate destructive actions. *Instances:* the mapper's disabled **Send** is muted *green* (`#5A7660`) and reads as "ready" — make disabled neutral grey and stop duplicating Send between header and toolbar; the dashboard has Export + window selector + 2 hero tabs + exception strip + checklist CTA at equal weight; connections' only header CTA points *away* from the page; supplier-detail has co-equal Save/Test/Delete per tab with no page-level "make this supplier deliverable" goal; settings has a green Save per section competing with Stripe links + copy buttons. Upload correctly has one green CTA — use it as the model.

8. **Consistent cards & elevation.** One radius, one border colour (`--border #E5E8EE`), one shadow tier for cards (`--shadow-card`); one elevated tier (`--shadow-pop`) for popovers/sheets/drawers; a two-tier modal/drawer system. No ad-hoc shadow/radius literals. *Instances:* `0 1px 2px rgba(11,26,47,.04)` and `0 8px 24px rgba(11,26,47,.12)` are re-typed inline in ~8+ places (dashboard, inbox Columns menu); supplier-detail re-implements card chrome in 4 components (radii 6/7/8/10/12, two border colours) each with a private token block + private `Field` — collapse to one shared `Card` + `Field`; extract one shared `SegmentedControl` for the hand-rolled inset-pill controls.

9. **Focus-visible + touch targets + Lucide.** Keep the global focus ring; ensure every interactive control is **≥44px** with a visible **hover + pressed** state; **replace all glyph/emoji icons with Lucide** (`RefreshCw`, `Upload`, `Search`, `Columns3`, `AlertTriangle`, `ChevronRight`, `X`, `ArrowRight`…) + `aria-label` on icon-only buttons. *Instances:* inbox checkboxes are 13×13px and chips/sort headers/pagination 28–32px; settings Pause/Resume/Edit are 27–28px; the mapper's 20–22px `= value`/`ƒx` chips have no focus ring; the shell + inbox + mapper + command palette use Unicode/ASCII glyphs instead of Lucide; nav hover is hand-rolled via JS `onMouseEnter/Leave` (should be CSS `:hover`/`:focus-visible`).

10. **Mobile = stacked, not shrunk.** Lists become cards, tabs stay reachable, the primary action stays visible (not clipped). **The mapper is desktop-first — on mobile show review/triage (`MobileTriage`), not a broken drag canvas.** *Instances:* fix the inbox **768–1023px tablet cliff** (wide tablet shows phone cards instead of the table) and the dashboard topology's in-card horizontal scroll (`min-w-[760px]`); supplier-detail's protocol rails + 2-col Overview collapse early at `lg` leaving an empty right gutter; let tables appear at `md` with horizontal scroll or a deliberate tablet card.

11. **Lead mapping rows with the human field name.** On the mapper, the PO-mapping editor, and the validation read-table, every row leads with the **human field name** ("Order number", "Supplier item code"), with the standards/machine token (`cbc:ID` / `BEG03` / `OrderRequestHeader@orderID`, raw `fieldPath`/`operator`) demoted to secondary mono text or behind an info-popover / "Show standards" disclosure — never the primary label. Row idiom: *source example value · meaning · supplier field · confidence · remember*. Keep power options (conditions/transforms/fixed values) behind one "Advanced" disclosure. *Instances:* the mapper's dense mono second line; supplier-detail's Validation read-table leading with raw `fieldPath`/`operator` despite existing `OPERATOR_LABELS`/`FIELD_OPTIONS`.

12. **Truthful delivery, health & offer⇔works states.** Never show "healthy" when something is failing; **HTTP 200 ≠ supplier acceptance**; every channel/format the UI offers must be a real, tested capability. Delivery states read honestly (delivered / rejected / failed / retrying), and "Failed" distinguishes redeliverable (`delivery_failed`) from non-redeliverable (parse/transform) so the next action is self-evident without a tooltip. *Instances:* keep the test-fire caveat ("their endpoint answered ≠ an order was accepted"); keep the honest `ready` ("Normalized") vs `ready_to_deliver` ("Ready to send") split; the inbox collapses five failure statuses into one red "Failed" pill (split the affordance); never let connectors/webhooks show green "Active" while `failureCount > 0`; settings' "Native Zapier/Make — Coming soon" must read as clearly not-yet (muted + lock/clock icon), not clickable; the standards catalog stays the conservative source of truth.

---

## 6. Prioritised redesign backlog (most leverage first)

| # | Pri | Change | Pages it touches |
|---|---|---|---|
| 1 | **P0** | **Kill inline-hex/off-scale drift → tokens + 4/8 rhythm + one type scale.** The single highest-leverage sweep; fixes most "unfinished" reads at once. Retire the second colour system in `BillingSection`/`PullIngressSettings`. | Every page (esp. inbox, dashboard, upload, supplier-detail, settings, shell) |
| 2 | **P0** | **One status-badge system** (`UnifiedStatusBadge`): one shape, the 5 colour semantics + icon/word, never colour alone. Collapse the inbox's duplicate Pipeline+pill; unify the dashboard's 5 vocabularies; fix the all-green connections rows; replace settings/supplier-detail's ad-hoc pills. | Inbox, dashboard, connections, suppliers, supplier-detail, exceptions, ops-health, delivery-log, settings, shell |
| 3 | **P0** | **Make the order-review / mapper the clear centre of gravity.** Tighten the 3-pane balance; lead output rows with the **human field name** (P11); make "this is exactly what {supplier} receives" prominent; one calm blocking-first issue list; single dominant **Send** (neutral disabled, no duplication, never clipped); fix the ~1024–1280px responsive cliff; mobile = triage not a broken canvas. | Order review/mapper (`/inbox/[orderId]`), PO-mapping editor, upload-preview |
| 4 | **P0** | **Truthful delivery/health states** (P12): 200 ≠ accepted; split redeliverable vs non-redeliverable "Failed"; never show healthy when failing; "Coming soon" reads clearly disabled. | Ops health, delivery log, inbox, connectors, webhooks, supplier-detail (delivery), settings |
| 5 | **P1** | **One table/list density** + CSS-driven row states + sticky header + `aria-sort` + tabular figures; turn the dashboard "In transit" list, the connections link-card stack and the settings webhook rows into real tables. | Inbox (the density standard), all library + ops + inbound lists, connections, dashboard, settings, supplier-detail tables |
| 6 | **P1** | **One primary action per screen**; demote competing buttons; separate destructive; extract a shared `SegmentedControl`; consistent card chrome + the 3 shadow tiers; one shared `Card` + `Field`. | Dashboard, connections, supplier-detail, settings, upload, every header |
| 7 | **P1** | **One overlay/modal system** — Esc + ✕(`aria-label`) + backdrop close + focus-trap + animate-from-trigger; **promote the settings one-time API-key reveal to a modal**; replace the three native `confirm()`s; bring OnboardingWizard to LaneDrawer parity. | Settings, supplier-detail (delete/history/template), connections detail, dashboard (wizard) |
| 8 | **P1** | **Replace all glyphs/emoji with Lucide** + `aria-label`s; enforce ≥44px targets + visible hover/pressed/focus on every control; convert JS hover to CSS. | Every page + shell + command palette |
| 9 | **P1** | **Real empty/loading/error on every surface**, de-duped across desktop+mobile, header/toolbar preserved in error, specific reasons; rebuild supplier-detail Overview into a setup-progress hub. | Connections, supplier-detail, dashboard, all library + ops + inbound lists, settings |
| 10 | **P2** | **Lead with human names; standards behind disclosure** (mapper output rows, validation read-table, PO-mapping). | Mapper, supplier-detail, library rules/rule-definitions, standards, upload-preview |
| 11 | **P2** | **Consolidate triplicated intake** (Email + SFTP + S3 → one "Intake channels" tab); write `?tab=` on manual tab clicks for shareable deep links; fix the 7-tab supplier-detail strip (wrap/overflow, not scroll). | Settings, supplier-detail |
| 12 | **P2** | **Fix responsive cliffs**: inbox 768–1023px tablet cliff; dashboard topology in-card horizontal scroll; supplier-detail protocol-rail early collapse; mapper 1024–1280px degradation. | Inbox, dashboard, supplier-detail, mapper |
| 13 | **P2** | **Unify to ONE toast system** (drop `<Toaster/>` or `<Sonner/>`) matching the badge semantics; consistent date format (e.g. "12 Jun 2026", not raw `toLocaleDateString`). | Shell (affects all), inbox, connections, billing/settings |

---

## 7. Brand canon to KEEP (do not "fix")

These are deliberate and load-bearing. Refine *around* them; do not replace them.

- **Navy `#0B1A2F` + violet/blue `#1E66C9` "Bridge Layer" brand**, the blue→green signature gradient (link-spine topbar edge, edge-rails, the mapper's received→supplier direction, the navy preview code body), navy top-edge/left-edge accents, and the buyer=blue / supplier=green / AI=violet colour semantics. Calm enterprise-operational tone.
- **Every route and every component name** (`/bridge`, `/inbox`, `/connections`, `/library/*`, `/operations/*`, `/settings`, `/admin`; `PageShell`, `PageHeader`, `BridgeSidebar`, `BridgeTopbar`, `WireTopology`, `LaneDrawer`, `StatusJourney`, `OrderWorkshop`, `MapperWorkbench`, `SupplierDockProfile`, `PoMappingEditor`, `DeliveryConfigEditor`, `UnifiedStatusBadge`, `BridgeLoader`…). Don't rename or reorganize URLs.
- **The vocabulary**: plain verbs over revision jargon (Edit mapping / Make live / Test / Restore, not draft/publish/archive); "Order workshop"; "this is exactly what {supplier} receives"; direction-aware Supplier↔Customer labels; "Inbox"/"Bridge"; the parse→…→learn loop framing.
- **Lucide** as the only icon set (converge *onto* it); **shadcn/ui + Tailwind** as the primitives layer.
- **Reduced-motion respected** (global reset + per-component guards) — every animation must stay opt-out-safe; keep the signature motions and `--ease-out` timing.
- **`BridgeLoader` / `BridgePageLoader`** as the single canonical loading animation (the blue→green "wire" mark with two pulsing nodes) — every route uses it; no ad-hoc spinners.
- The existing **token set** in `globals.css` / `tailwind.config.ts` (colours, fonts, radius, shadow, spacing, motion) — the job is to *apply it consistently*, not to redefine it.
- **The honesty stance**: offer ⇔ works; "HTTP 200 ≠ supplier acceptance"; the conservative standards catalog as source of truth.

---

## 00. Global App Shell — `(app) layout / shell`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/layout.tsx`
- **Key components:**
  - `src/components/bridge/BridgeSidebar.tsx` — left navy nav (desktop rail + mobile drawer body)
  - `src/components/bridge/BridgeTopbar.tsx` — top bar (mobile menu button, demo badge, breadcrumb, setup chip, search, notifications, help, account) + hosts `NotificationsBell`, `AccountMenu`, `useAutoCrumb`
  - `src/components/bridge/CommandPalette.tsx` — Cmd/Ctrl+K palette
  - `src/components/bridge/HelpSlideover.tsx` — right help drawer
  - `src/components/bridge/SetupProgressChip.tsx` — "Setup n/6" topbar pill
  - `src/components/bridge/BridgeLoader.tsx` — `BridgeLoader` + `BridgePageLoader` (the single loading mark; used by `(app)/bridge/loading.tsx`)
  - `src/components/bridge/ErrorBoundary.tsx` — client render-error catch + `DefaultErrorPanel`
  - `src/components/bridge/breadcrumb.ts` — pure crumb label/href helpers
  - `src/components/bridge/OnboardingWizard.tsx` — first-run modal (rendered by `BridgeDashboard.tsx`, NOT the layout)
  - `src/components/bridge/DSPrimitives.tsx` — `ProcuLinkMark` logo glyph
  - `src/components/ui/toaster.tsx` (`<Toaster />`) + `src/components/ui/sonner.tsx` (`<Sonner />`) — two global toast layers
  - `src/mocks/MSWProvider.tsx` — mock service worker wrapper (mock mode only)
- **Capture URL (mock):** `/bridge` (the dashboard renders inside this shell; the shell itself is the same on every `(app)/*` route, e.g. `/inbox`, `/library/suppliers`)

### What it is & why it exists
The `(app)` layout is the persistent chrome wrapped around every authenticated screen in the parse→normalize→validate→review→transform→deliver→learn workflow. It is the operator's home frame: the left sidebar is the map of the whole product (Workbench / Library / Operations / Inbound / Admin), the top bar tells them where they are (breadcrumb), what needs action (notifications, setup progress), and gives them the two universal accelerators — global search (Cmd+K) and contextual help. A procurement coordinator never "opens" the shell deliberately; it is simply always there, and its job is to make every page feel like one coherent application rather than a pile of routes.

### Who uses it & the primary job
**Primary persona: procurement coordinator** (also integration expert, operator, admin via the gated Admin link). The single most important job the shell does is **orientation + fast traversal**: from anywhere, jump to the order/supplier/buyer you need (Cmd+K or sidebar), see at a glance whether anything is failing or needs review (Notifications bell badge, Inbox count badge), and never get lost (breadcrumb + active nav state). Secondary: surface "you still have setup to do" (Setup chip) without nagging once complete.

### Layout & structure (current)
Full-viewport flex shell, no scroll on the outer wrapper (`flex h-dvh overflow-hidden`, page background `#F6F7FA`). Two columns:

**Left column — sidebar.**
- Desktop (`md+`): `BridgeSidebar collapsible collapseBelowLg`, fixed navy `#0B1A2F`, width **232px expanded / 64px collapsed**, right border `1px #1F3252`. Width animates `transition-[width] 200ms`. In the tablet band (≤1023px / `md`→`lg`) it is forced to the 64px icon rail regardless of preference; at `lg+` it honors the persisted `localStorage["pl-side"]` preference. A screen-requested auto-collapse channel (`SIDEBAR_AUTO_COLLAPSE_EVENT`, e.g. the Review screen's full-document mode) can also force the rail.
- Sidebar internal stack, top→bottom:
  1. **Logo header** (height 56, border-bottom `#1F3252`): `ProcuLinkMark` 24px + "ProcuLink" wordmark (Bricolage Grotesque 16/700) + a collapse toggle chip (`ChevronsLeft`/`ChevronsRight`, 28×28 visible, 44×44 hit area). In the mobile drawer this slot shows an `X` close button instead.
  2. **Workspace badge** (`#14253D` card, border `#1F3252`): 26px square org-initials chip (`#1E66C9`) + org name (12.5/600 white) + plan label (10.5px `#7C8DA6`, e.g. "Operations plan"). **Read-only** — deliberately NO chevron / switcher (comment notes there is no multi-org switcher wired; not implying a menu that doesn't exist).
  3. **Nav** (scrollable, hidden scrollbar): grouped link list — see Data shown.
  4. **Footer** (border-top `#1F3252`): "Talk to a human" (`mailto:sales@proculink.eu`, MessageCircle) + "Back to site" (link to `/`, ExternalLink).
- Mobile (`<md`): the desktop sidebar is hidden; a full-screen drawer (`BridgeSidebar fullWidth showClose`) mounts only when `sidebarOpen`, as a `role="dialog" aria-modal` navy panel over `inset-0 z-50`.

**Right column — topbar + main.**
- `BridgeTopbar`: height **56px**, navy `#0B1A2F`, `position: relative`. Single flex row, `gap-3 sm:gap-4`, `px-3 sm:px-5`, left→right: mobile menu button (`md:hidden`), Demo-data badge (mock only), breadcrumb (`hidden sm:flex`) OR mobile page-title (`sm:hidden`), Setup chip, then `ml-auto` pushes the right cluster: search field (`sm+`, 320px / max 38vw) or search icon (mobile), Notifications bell, Help "?" (`sm+`), Account avatar. A 2px blue→green gradient `link-spine` sits on the bottom edge (animated, keyed by pathname so it re-runs on navigation; frozen under reduced-motion).
- `main`: `flex-1 overflow-auto`, wraps `children` in `<ErrorBoundary context="App">`. This is the only scrolling region.

**Spacing/type/density observations:** Heavy reliance on **inline `style={{}}` with literal hex + literal px** (e.g. `fontSize: 12.5`, `13.5`, `10.5`, `11.5`) rather than tokens/Tailwind scale — the shell predates and partly bypasses the design-token system in globals.css. Type sizes are fractional and non-systematic (10.5/11.5/12.5/13/13.5/14/16/18). Tap targets are correct (44×44 via `var(--tap-min)`) but built as "visible 28/32px chip inside a transparent 44px hit area" repeatedly by hand.

### Data shown
**Sidebar nav (`NAV` → `buildVisibleNav`)** — grouped, direction-aware, flag-gated:
| Group | Items (label → href, icon) |
|---|---|
| (ungrouped) | Dashboard → `/bridge` (Layers) |
| Workbench | Upload → `/upload` (Upload); Inbox → `/inbox` (Inbox, **review badge**); Drafts → `/drafts` (Files) |
| Library | Suppliers → `/library/suppliers` (Truck, relabels to **Customers** in inbound mode); Buyers → `/library/buyers` (Building2); Mappings → `/library/mappings` (GitBranch); Rules → `/library/rules` (ShieldCheck); Rule definitions → `/library/rule-definitions` (ListChecks); Output templates → `/library/templates` (FileCode); Standards → `/library/standards` (BookOpen) |
| Operations | Exceptions → `/operations/exceptions` (AlertTriangle); System health → `/operations/health` (Activity); Delivery log → `/operations/log` (ScrollText); Connectors → `/operations/connectors` (Plug); Webhooks → `/operations/webhooks` (Webhook) |
| Inbound (flag `NEXT_PUBLIC_INBOUND_ENABLED`) | Invoices → `/inbound/invoices` (FileText); ASNs → `/inbound/asns` (Package) |
| (ungrouped) | Admin → `/admin` (ShieldHalf, allowlist-gated); Help → `/help` (HelpCircle, **opens new tab**); Settings → `/settings` (Settings) |

Launch default (`LAUNCH_CORE_ONLY`, i.e. `NEXT_PUBLIC_LAUNCH_FULL_NAV !== "true"`) filters the nav down to `LAUNCH_CORE_HREFS` only: `/bridge, /upload, /inbox, /library/suppliers, /operations/exceptions, /operations/health, /admin, /settings, /help`, dropping now-empty group headers. The full nav above is only shown when that flag is set.

**Live data feeding the shell (all via TanStack Query):**
- Inbox review badge + Setup-chip + Notifications unread: `apiClient.getOrdersSummary()` (mock: `mockGetOrdersSummary`) — `byStatus["pending_review"]` drives the blue Inbox count; `pending_review + failed + delivery_failed + transform_failed + delivery_dead_letter + rejected_by_supplier` drives the bell's amber unread count.
- Workspace badge plan: `getBillingStatus()` → `billing.plan` ("Operations" etc.); org name + initials from Clerk `useOrganization()`.
- Admin-link visibility: `checkAdminAccess()` (query `["admin-access"]`) — UX hint only; page + API re-gate server-side.
- Notifications popover list: `apiClient.getOrders({ pageSize: 100 })` (mock: `mockGetOrders`) → mapped to `failed` / `review` / `delivered` notification kinds, each row showing `o.poNumber` (mono) · `o.supplierName` + relative `timeAgo(createdAt)`.
- Command palette index: `getOrders({pageSize:100})` / `getOrders({search,pageSize:8})`, `getSuppliers()`, `getBuyers()` + a static Actions list + mapper power-commands.
- Setup chip: `useOnboardingStatus()` → `buildChecklistSteps()` → `{totalDone}/{totalSteps}`.
- Breadcrumb dynamic labels read the **existing query cache** (no new fetch): `["order", id].poNumber`, `["suppliers"]` list `.name`, `["connection", id].name`.

`AutoActivateOrg` (invisible) auto-activates the user's first Clerk org so the JWT carries `org_id` (else every API call fails "Organisation not resolved"), then invalidates all queries.

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| Sidebar nav link (each) | click | `next/link` navigate to its href; mobile drawer also calls `onNavigate`→close |
| Help nav link | click | opens `/help` in a **new tab** (`target=_blank`) |
| Admin nav link | click (only if allowlisted) | `/admin` |
| Sidebar collapse toggle (desktop) | click | toggles 232↔64px; persists `localStorage["pl-side"]`; first clears any auto-collapse |
| Sidebar close `X` (mobile drawer) | click | `onClose` → `closeSidebar()` |
| Workspace badge | none (read-only div) | static org identity; no menu |
| "Talk to a human" | click | `mailto:sales@proculink.eu?subject=ProcuLink%20support` |
| "Back to site" | click | navigate `/` (marketing) |
| Topbar mobile menu button (`md:hidden`) | click | `openSidebar()` → mounts mobile nav drawer |
| Breadcrumb crumb link | click | navigate to that crumb's cumulative path |
| Setup chip ("Setup n/6") | click | navigate `/bridge` (the checklist) |
| Search field / mobile search icon | click | open **Command Palette** (`setPaletteOpen(true)`) |
| ⌘K / Ctrl+K (global) | keydown | toggle Command Palette |
| Notifications bell | click | toggle Notifications popover |
| Notification row | click | close popover + `router.push('/inbox/{id}')` |
| "View all in inbox →" (popover footer) | click | close popover + `/inbox` |
| Help "?" button (`sm+`) | click | mark current route guide "seen" (localStorage), open **Help slideover** |
| Account avatar | click | Clerk `<UserButton>` menu (sign-out, manage account); placeholder "MK" chip if Clerk unconfigured |
| Command palette input | type | filters index (client substring) + debounced server order search (`>=2` chars) |
| Command palette ↑/↓, Enter, Esc | keydown | navigate list / run item / close |
| Command palette result row | click / hover | run (navigate or dispatch action) / set active |
| Help slideover search | type (150ms debounce) | filters help articles (Fuse) |
| Help slideover article / footer links | click | open `/help/*`, `/support` etc. in **new tab** |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Mobile nav drawer** | full-screen drawer (`role=dialog aria-modal`, navy `#0B1A2F`, `inset-0 z-50`, `md:hidden`) | Topbar mobile menu button (`openSidebar`) | full `BridgeSidebar` (logo+X, workspace badge, nav, footer) | in-panel `X`; **Esc**; tapping any nav link (`onNavigate`); resizing to `md+` (conditionally unmounts). Has body-scroll lock + focus trap + focus restore to trigger. NO visible backdrop scrim (the panel is opaque full-screen) |
| **Command Palette** | centered modal + backdrop (`top:20vh`, 600px, `z-9999`; backdrop `rgba(11,26,47,.55)` blur, `z-9998`) | search field, mobile search icon, or **⌘K/Ctrl+K** | search input, grouped results (Orders/Suppliers/Buyers/Actions/Mapper cmds), keyboard-hint footer | backdrop click; **Esc** (two listeners); selecting any item (`run`→`onClose`). Body-scroll locked while open. Mounted only when open |
| **Help slideover** | right drawer (`role=dialog aria-modal`, `width min(380px,100%)`, `z-70`, scrim `rgba(11,26,47,.32)`) | Help "?" button | "This screen" guide / popular fallback, Related reading, optional Watch-walkthrough, search results, footer (help center / support / report bug) | header `×`; **Esc**; scrim click; following any link. On close focus returns to "?" trigger |
| **Notifications popover** | popover/dropdown (white card, `sm:absolute right-0 top-8 w-80`; mobile `fixed inset-x-2 top-14`; `z-50`) | Notifications bell | header ("Notifications" + "n need action"), up to 7 ranked rows (failed→review→delivered), "View all in inbox →" footer; empty: "No new activity." | outside mousedown; **Esc**; selecting a row; toggling the bell. NO scrim |
| **Account / user menu** | dropdown (Clerk `<UserButton>` internal popover) | avatar click | Clerk-managed account actions (manage account, sign out, org) | Clerk-managed (outside click / Esc). Not rendered when `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY` unset (static "MK" chip only) |
| **Onboarding wizard** | centered modal (`role=dialog aria-modal`, `z-50`, max 480px, backdrop `rgba(11,26,47,.45)` blur) | *NOT the layout* — `BridgeDashboard` renders it when `!wizardDismissed && onboardingStatus && !hasSupplier` | Step 0 (order direction radios) → Step 1 (add first supplier form) → done card; step indicator dots; "Dismiss" notice | corner `X` / backdrop click (`handleDismiss`); "Open my setup guide" (`onDismiss`); `?onboard=skip`; dismissal persisted `localStorage["plk-onboarding-wizard-dismissed"]` |
| **Toasts (shadcn `<Toaster/>`)** | toast region | imperative `toast()` from any page | per-page confirmations/errors | auto-dismiss / swipe / close |
| **Toasts (`<Sonner/>`)** | toast region (second system) | `sonner` `toast()` from any page | per-page confirmations/errors | auto-dismiss / close |
| **Tooltips** | tooltip (Radix `TooltipProvider`) | hover/focus on tooltip-wrapped controls | short labels | blur / pointer-leave |

### States
- **Empty:** Shell itself is always present. Notifications popover has a real empty state ("No new activity."). Command palette has `No results for "<q>"`. Sidebar nav: if `getOrdersSummary` returns no `pending_review`, the Inbox badge simply doesn't render (correct). Setup chip renders **nothing** when complete or status unknown (honest — no fabricated 0/6). Workspace badge plan shows "Loading…" until billing resolves.
- **Loading:** Route transitions use `(app)/bridge/loading.tsx` → `BridgePageLoader` (88px animated blue→green wire mark with two pulsing nodes, NOT a bare spinner). Per-route `loading.tsx` files use the same loader. Sidebar/topbar data (badges, plan) loads silently in the background (no skeleton on the chrome itself — badges/counts pop in).
- **Error:** `main` is wrapped in `<ErrorBoundary context="App">` → `DefaultErrorPanel` ("App failed to load", logged to Sentry, collapsible error details, "Try again" / "Reload page"). API-fetch failures for chrome data fail silently (badges just stay absent — `retry:1`, networkMode "always" so they don't latch "paused offline"). Onboarding wizard steps surface inline humanised errors (incl. a CORS/API-unreachable hint).
- **Success/feedback:** Two global toast layers (`<Toaster/>` + `<Sonner/>`). The `link-spine` bottom-edge gradient re-animates on each navigation as a subtle "you moved" cue. Help/wizard fire analytics `capture()` events.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** sidebar expanded at 232px (honors persisted collapse), full breadcrumb, 320px search field, full topbar cluster, Demo badge full pill.
- **Tablet 768 (`md`→`lg`, ≤1023px):** sidebar **forced to the 64px icon rail** (`collapseBelowLg`/`belowLg`) regardless of preference — labels hidden, badges become a corner dot; nav group headers collapse to a divider line. Breadcrumb + search field still shown (`sm+`).
- **Mobile 390 (`<md`):** desktop sidebar hidden entirely → hamburger menu button opens the full-screen navy drawer. Breadcrumb replaced by a single bold page-title (`mobileLabel`). Search field collapses to an icon-only trigger. Help "?" hidden (`sm:flex`); Setup chip hides its "Setup" word (count only); Demo badge shrinks to a dot + "Demo". Notifications popover goes full-width (`inset-x-2 top-14`).
- **Known cliffs:** the `md`→`lg` band forces the icon rail, so labels disappear at exactly 1024px down — a hard cliff with no tween. The Help "?" is unreachable below `sm` (640px), so contextual guidance is desktop/tablet-only on phones. The mobile drawer has **no backdrop scrim** (opaque full-screen panel) so there is no dimmed-context affordance.

### Current UX issues
- **Token bypass / spacing drift (Bar #1, #2).** The entire shell is built with inline `style={{}}` literals (hex `#0B1A2F`, `#14253D`, `#7C8DA6`, `#C8D1E0`; px `12.5/13.5/10.5/11.5`) instead of the CSS variables that exist in globals.css (`--navy`, `--ink-faint`, `--tap-min`). Type sizes are fractional and unsystematic; padding/gaps don't follow a strict 4/8 rhythm (e.g. logo header `"16px 12px 12px 16px"`, badge margin `"12px 14px 6px"`). This makes the chrome hard to keep consistent with the token-driven page bodies.
- **Two toast systems (Bar #4/#6).** Both shadcn `<Toaster/>` and `<Sonner/>` are mounted globally — duplicate, divergent toast styling/behavior depending on which a page calls. Should be one.
- **Inconsistent badge shapes (Bar #4).** The Inbox count (blue pill `#1E66C9`, mono), the Notifications unread (amber `#B36D14` square-ish, 15px), and the Setup chip (navy pill, green dot) are three different shapes/colors/sizes for "count that needs attention." No single status-badge system across the chrome.
- **Hand-rolled hover via JS (Bar #9).** Every nav link, icon button, and breadcrumb sets hover colors through `onMouseEnter/onMouseLeave` inline JS rather than CSS `:hover`/`:focus-visible`. Focus-visible rings are not consistently visible on the navy chrome controls; keyboard users get weak affordance.
- **Workspace badge looks clickable but isn't.** It's a card with initials + name + plan but no menu — fine as honesty, but visually it reads like a switcher; a user will click it expecting org-switching.
- **No scrim on the mobile nav drawer.** Opaque full-screen panel with no dimmed context behind; loses the "overlay over my work" mental model and the tap-outside-to-close affordance other overlays have.
- **Notifications & command-palette mix glyphs.** The palette uses ASCII/symbol icons (`↗ ⊞ ◎ ⚠ ❤ ▤ ≣ ▶`) while the rest of the app uses Lucide — inconsistent icon language, and several read as clip-art.
- **Help unreachable on mobile; two help entry points.** The "?" is `sm+` only, and Help also lives as a sidebar link that opens a new tab AND as the slideover — three overlapping "help" surfaces with different behaviors.
- **Breadcrumb is the only depth cue; no page H1 in chrome.** On detail pages the breadcrumb carries all orientation; on mobile it degrades to a single label.

### Redesign recommendations (for Claude Design)
1. **Tokenize the shell.** Replace every inline hex/px literal in `BridgeSidebar`, `BridgeTopbar`, `CommandPalette`, `HelpSlideover`, `SetupProgressChip` with the existing CSS variables and a strict 4/8 spacing scale. Keep navy `#0B1A2F` + violet/blue `#1E66C9` brand and the blue→green wire identity. This is the single highest-leverage change — it makes the chrome consistent with the token-driven pages for free.
2. **One type scale + weight hierarchy (Bar #2).** Collapse the 10.5/11.5/12.5/13.5 fractional sizes onto a real scale (e.g. 11/12/13/14/16/18) with weight, not color, carrying hierarchy. Ensure nav inactive labels (`#C8D1E0` on navy) and faint text meet 4.5:1.
3. **Unify to ONE toast system (Bar #4/#6).** Drop either `<Toaster/>` or `<Sonner/>`; standardize one toast shape with green/amber/red/neutral + icon semantics, matching the status-badge system.
4. **One status-badge system for the chrome (Bar #4).** Make the Inbox count, Notifications unread, and Setup chip share one pill shape/size/padding and the green/amber/red/neutral palette with an icon-or-word — never color alone. Tabular/mono figures for all counts (already partly done) so they don't jitter.
5. **CSS-native hover + focus-visible (Bar #9).** Replace the `onMouseEnter/onMouseLeave` JS hover handlers with `:hover`/`:focus-visible` so every nav item, icon button, and crumb has a visible, consistent focus ring on navy. Keep ≥44px hit areas (already correct).
6. **Add a scrim + slide-in animation to the mobile nav drawer (Bar overlays).** Give it the same `rgba(11,26,47,.x)` scrim, tap-outside-to-close, and animate-from-trigger that the palette and help slideover already have, so all overlays behave identically.
7. **Resolve the tablet rail cliff (Bar #10).** Either tween the 232↔64 collapse smoothly across the `md`→`lg` band, or keep labels longer; today labels vanish abruptly at 1024px. Stacked-not-shrunk principle: confirm the phone drawer shows full labels (it does) and reaches every group.
8. **Normalize the command-palette + popover icons to Lucide.** Replace the ASCII symbol glyphs with the same Lucide set the sidebar uses; keep the per-group color accents.
9. **Make the workspace badge honestly inert or a real switcher.** Either flatten it so it doesn't read as a button, or wire an actual org switcher — don't leave a card that invites a click with no menu.
10. **Consolidate help entry points.** Pick one primary help affordance (the slideover) and make it reachable on mobile; demote the new-tab `/help` link and de-duplicate the three "help" surfaces.

---

### Screenshots — PRODUCTION (11)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/00-base-desktop-1440-global-shell.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/00-base-hd-1920-global-shell.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/00-base-mobile-390-global-shell.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/00-base-tablet-768-global-shell.png)

**account-menu-open · desktop-1440**

![account-menu-open · desktop-1440](screenshots-prod/00-account-menu-open-desktop-1440-global-shell.png)

**command-palette-open · desktop-1440**

![command-palette-open · desktop-1440](screenshots-prod/00-command-palette-open-desktop-1440-global-shell.png)

**command-palette-query-results · desktop-1440**

![command-palette-query-results · desktop-1440](screenshots-prod/00-command-palette-query-results-desktop-1440-global-shell.png)

**help-slideover-open · desktop-1440**

![help-slideover-open · desktop-1440](screenshots-prod/00-help-slideover-open-desktop-1440-global-shell.png)

**mobile-nav-drawer-open · mobile-390**

![mobile-nav-drawer-open · mobile-390](screenshots-prod/00-mobile-nav-drawer-open-mobile-390-global-shell.png)

**notifications-popover-open · desktop-1440**

![notifications-popover-open · desktop-1440](screenshots-prod/00-notifications-popover-open-desktop-1440-global-shell.png)

**sidebar-collapsed-rail · desktop-1440**

![sidebar-collapsed-rail · desktop-1440](screenshots-prod/00-sidebar-collapsed-rail-desktop-1440-global-shell.png)

---

## 01. Dashboard — `/bridge`

- **File:** `src/app/(app)/bridge/page.tsx` (thin wrapper; renders `<BridgeDashboard />`)
- **Key components:**
  - `src/components/bridge/BridgeDashboard.tsx` (the whole screen — ~1260 lines, all logic + layout inline)
  - `src/components/bridge/WireTopology.tsx` (the SVG "System map" canvas + its mobile lane-list fallback `WireTopologyLaneList`)
  - `src/components/bridge/LaneDrawer.tsx` (right-side drawer opened by clicking a wire)
  - `src/components/bridge/StatusJourney.tsx` (the compact 5-node Parse→Deliver mini-stepper in each "In transit" row; also exports `StatusCell`)
  - `src/components/bridge/FileChip.tsx` (the uppercase format tag: PDF/XLSX/cXML/EDI…)
  - `src/components/bridge/OnboardingChecklist.tsx` (the "Get your first order automated" band / hero card + one-time completion card)
  - `src/components/bridge/OnboardingWizard.tsx` (full-screen modal for orgs with no supplier yet)
  - `src/components/bridge/buildChecklistSteps.ts` (pure model deriving the 6 checklist steps)
  - `src/components/bridge/layout/PageHeader.tsx`, `.../layout/PageShell.tsx` (canonical title row + page wrapper)
  - `src/components/bridge/BridgeLoader.tsx` (`BridgePageLoader` used by `loading.tsx`)
- **Capture URL (mock):** `/bridge` — mock mode is config-driven (`NEXT_PUBLIC_USE_MOCK=true`), not a query param. In mock mode the topology renders 6 buyers / 5 suppliers / 11 wires (incl. alerts + a `down` lane), the funnel/KPIs count the 3 mock orders, and a wire is clickable to open the LaneDrawer. There is no per-page mock id.

### What it is & why it exists
This is the operational home screen — the first thing a procurement coordinator sees after sign-in. It answers "what is happening to my orders right now?" across the whole `Parse → Normalize → Validate → Review → Transform → Deliver → Learn` loop: an order-pipeline funnel (Received → Needs review → Ready → Delivered → Failed), a buyer→supplier "System map" of live connections, four headline KPIs, an "In transit" activity list, and a per-supplier "delivery success rate" health list. It also doubles as the onboarding surface: a brand-new org sees a guided wizard + a 6-step checklist instead of an empty map. Coordinators open it to triage exceptions, confirm deliveries are flowing, and jump to the inbox or a specific order.

### Who uses it & the primary job
**Procurement coordinator** (the buyer-side persona who approves the €399 plan). The single most important task: **spot orders that need a human and get into triage fast** — the amber exception strip, the "Needs review" funnel tile, and the "Needs attention" KPI all deep-link to `/operations/exceptions`. Secondary jobs: confirm throughput (Orders received/delivered), and onboard (checklist → first delivery).

### Layout & structure (current)
Rendered inside `PageShell variant="wide"` (max-width `var(--container-wide)` ≈ 1480px; gutters 16→24→34px, vertical 20→28px) on the grey app canvas `var(--bg)` (#F6F7FA). Top to bottom:

1. **PageHeader** (`PageHeader.tsx`) — floating title "Dashboard" (Bricolage Grotesque, 28→30px, weight 600) directly on the canvas (no white bar). Sub-line: green status dot · "Live order view" · `{n} connections` · `{n} active suppliers`. Right-aligned actions (only shown when there is order data): a **time-window segmented control** (Today / 7d / 30d / All — inset white pill track, active pill = navy #0B1A2F) and an **Export report** button (outline, Download icon).
2. **OnboardingWizard** — full-screen modal overlay (only when no supplier; see overlays table).
3. **Branch A — Onboarding hero** (when `orderCount===0 && no endpoint topology && checklist incomplete`): a centered max-980px column with a one-line intro + the `OnboardingChecklist` card as the primary content. The rest of the dashboard is replaced.
4. **Branch B — Normal dashboard** (a `flex flex-col gap-4 sm:gap-5` stack):
   - **Exception strip** (conditional) — full-width amber link banner (`border #F0D39A`, 3px amber left border, bg #FFF8EA): "`N` orders need your attention" → "Review exceptions →". Only shows when the summary query has settled AND count > 0.
   - **Hero section** with a tab strip (`role="tablist"`): **Pipeline** (default, BarChart3 icon) | **System map** (Network icon), same navy-active inset-pill style as the window selector.
     - **Pipeline tab**: a white card (radius `--radius-card`, border #E5E8EE, 1px shadow, 3px blue→green top accent) containing a 5-tile funnel grid (`grid-cols-2 sm:grid-cols-3 lg:grid-cols-5`, gap 3 = 12px). Each tile: uppercase label + Lucide icon, a `monument` (display-font) `clamp(24–32px)` tabular number, and a proportional bar (5px). "Needs review" and "Failed" tiles are links to exceptions. Below: a plain-language flow caption "Order pipeline · Received → Needs review → Ready → Delivered · All time".
     - **System map tab**: same white card frame with a 3px blue→green accent, a legend header row (Buyer dot, Supplier dot, "At-risk connection" dash, and a right-aligned "⚠ N open exceptions" pill link), then the `WireTopology` SVG canvas (inner card chrome stripped so the wrapper is the single card). Canvas height is adaptive (`min 320 / max 520`, `150 + maxPorts*74`).
   - **OnboardingChecklist** band (renders again here as a full-width band while setup is incomplete; self-nulls when complete/loading/errored).
   - **KPI strip** — `grid-cols-2 xl:grid-cols-4`, four white cards (radius `--radius-card`, 1px border, 1px shadow, 3px colored top accent each). Each: uppercase label, `monument` `clamp(28–36px)` value, a sub-line with icon. The "Needs attention" card is itself a link to exceptions.
   - **Bottom row** — `grid-cols-1 xl:grid-cols-2`:
     - **In transit** card — header (Send icon + title + "moving through the pipeline now"), then a `divide-y` list of active orders. Each row: PO# (mono, blue-deep) · buyer · `FileChip` format · stage word, with a compact `StatusJourney` 5-node stepper underneath. Rows with an id link to `/inbox/{id}`.
     - **Supplier health** ("{noun} health") card — header (Activity icon + title + "Delivery success rate, last 30 days" + "All suppliers →" link), then a list of suppliers each as a 44px-min row: name · 160px health bar (desktop only) · `health%` (mono, bold, color-coded green/amber/red).

**Spacing/type/density observations:** heavy reliance on inline `style={{}}` with hard-coded hex (#0B1A2F, #5E6779, #E5E8EE, #B36D14 …) and px values, rather than tokens/Tailwind scale. Font sizes are fractional and ad-hoc (10.5, 11.5, 12.5, 13). Numbers DO use `tabular-nums` / JetBrains Mono in most places (good). Three distinct "inset pill" segmented controls (window, hero tabs) repeat the same markup.

### Data shown
- **Orders working set** — `apiClient.getOrders({ pageSize: 100 })` (`GET /api/orders`; mock `mockGetOrders`). Fields used per order (`OrderSummary`): `id`, `poNumber`, `buyerName`, `supplierName`, `status`, `sourceFormat`, `lineCount`, `unresolvedCount`, `totalValue`, `currency`, `createdAt`. Drives derived topology, in-transit rows, windowed counts, auto-processed %, and CSV export.
- **Orders summary** — `apiClient.getOrdersSummary()` (`GET /api/orders/summary`; mock `mockGetOrdersSummary`). `{ byStatus: Record<status,number>, total }`. Drives the funnel stages + the "Needs attention" (open-exceptions) KPI — all-time, full-population.
- **Suppliers** — `apiClient.getSuppliers()` (`GET /api/suppliers`; mock `mockGetSuppliersFn`). Used to align derived dock ids with configured supplier ids.
- **Server topology** — `apiClient.getDashboardTopology()` (`GET /api/dashboard/topology`; mock `mockGetDashboardTopology`). `{ buyers[], suppliers[], wires[] }` (buyer: id/name/code/volume; supplier: +health; wire: buyerId/supplierId/weight 1–6/health ok|risk|down/alert?). Preferred over client-derived topology when it has data.
- **Windowed counts** — two extra `getOrders({ pageSize:1, dateFrom })` queries (received total, delivered total) read only `totalCount`; disabled in mock mode.
- **Onboarding status** — `useOnboardingStatus()` (`GET /api/onboarding/status`). Booleans `hasSupplier`, `hasCatalog`/`hasItemMappings`, `hasUpload`, `hasResolvedMapping`, `hasDeliveryConfig`, `hasTestFired`, `hasDelivery` + ids → fed to `buildChecklistSteps()` for the 6-step checklist + wizard gating.
- **Direction labels** — `useOrderDirection()` swaps "Supplier"↔"Customer" copy (display only) for inbound orgs.
- Mock fallbacks: `IN_TRANSIT_MOCK_FALLBACK` (5 staged rows, only in mock mode when no live active orders); `MOCK_CROSSINGS` in LaneDrawer (mock mode only).

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| Time-window pills (Today / 7d / 30d / All) | `setWindowKey()` | Re-filters windowed KPIs (Received, Delivered, Auto-processed) + the CSV export scope; default 30d |
| **Export report** button | `handleExport()` | Client-side CSV download of the current window's orders (capped at loaded 100; truncation noted inside the file) |
| Hero tab **Pipeline** | `setHeroTab("funnel")` | Shows the funnel card |
| Hero tab **System map** | `setHeroTab("map")` | Shows the WireTopology canvas |
| Funnel tile **Needs review** | `Link` | `/operations/exceptions` (only when value > 0) |
| Funnel tile **Failed** | `Link` | `/operations/exceptions` (only when value > 0) |
| Exception strip banner | `Link` | `/operations/exceptions` |
| "⚠ N open exceptions" pill (System map legend) | `Link` | `/operations/exceptions` |
| KPI card **Needs attention** | `Link` | `/operations/exceptions` |
| **A wire** in the SVG topology | `onWireClick` → `setActiveLane()` | Opens the LaneDrawer (right drawer) |
| A lane row (mobile lane-list) | `onWireClick` | Opens the LaneDrawer |
| **In transit** row (with id) | `Link` | `/inbox/{orderId}` |
| **Supplier health** row | `Link` | `/library/suppliers/{id}` |
| "All suppliers →" link | `Link` | `/library/suppliers` |
| Topology empty-state "Add a supplier →" | `Link` | `/library/suppliers` |
| Topology/funnel/in-transit **Retry** buttons | `refetchOrders()` | Re-runs the orders query on error |
| Checklist **primary CTA** (active step) | `Link` | Step href (`/library/suppliers`, `/upload`, `/inbox/{id}`, `/library/suppliers/{id}?tab=catalog|delivery`) |
| Checklist "Use guided setup" | `onResumeSetup` → `resumeWizard()` | Re-opens the OnboardingWizard modal |
| Checklist "Try a practice order →" | `runSample()` (`useSampleOrder`) | Creates a sample order, routes to it; or "Open your practice order →" if one exists |
| Checklist intermediate "Send a test →" | `Link` | `/library/suppliers/{id}?tab=delivery` |
| Completion card "Done" | `onDismiss` | Marks celebrated (session); links: email intake / API key / add supplier |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **OnboardingWizard** | Modal (`role="dialog" aria-modal`, fixed, scrim `rgba(11,26,47,.45)` + 4px backdrop blur, z-50) | Auto-opens on mount when `onboardingStatus.hasSupplier === false` and not session-dismissed; re-opened by checklist "Use guided setup"; auto-dismissed via `?onboard=skip` | Step 0 choose order direction (2 radio cards) → Step 1 add first supplier (labeled text input + green submit) → "done" closing notice ("Open my setup guide"); step indicator dots at top | Top-right ✕ button; backdrop click; "Open my setup guide" / "Done" button; sets `sessionStorage` dismissed flag (no Esc handler) |
| **LaneDrawer** | Right-side drawer (fixed, 400px / `maxWidth:100vw`, `-8px 0 32px` shadow, z-8999) + dim scrim (`rgba(11,26,47,.3)`, z-8998) | Clicking a wire in the topology canvas (or a lane row on mobile) | "Connection detail" header; Buyer→Supplier card (codes, health label); 3-up stats (Volume / Health / Alerts); "Recent deliveries" list (live supplier-scoped orders, mock crossings, or honest empty state); footer "View all deliveries →" (`/inbox`) + "Connection settings" (`/library/suppliers/{id}`) | ✕ button; **Esc key**; backdrop click; clicking any recent-delivery row or footer button (navigates + closes) |
| Funnel tile / KPI / exception strip / supplier row / in-transit row | Inline `Link` (no overlay) | Click | — | Navigates away in place (not an overlay) |
| `title=""` attributes on window/export/tile/KPI controls | Native browser tooltip | Hover | Short helper text (e.g. "Export contains the most recent 100 of N…") | Mouse-out |
| OnboardingChecklist completion card | Inline card (not an overlay) | Reaching 6/6 in a session that saw it incomplete | Celebration + 3 next-step links | "Done" button (session-persisted) |

Note: There is **no toast system** on this page — all feedback is inline (errors, the practice-order error line, sample "Starting…" label). The CSV export gives no on-screen confirmation; it just triggers a download.

### States
- **Empty (brand-new org):** handled well — the **Onboarding hero** replaces the dashboard with the checklist; the **OnboardingWizard** modal fires first if no supplier. Topology empty (has data but no crossings) shows "No deliveries yet — Add a {supplier} →". In-transit empty: "No orders in flight right now." Supplier-health empty: "No {suppliers} yet."
- **Loading:** route-level `loading.tsx` → `BridgePageLoader` (animated blue→green "wire" mark, reduced-motion-safe — not a bare spinner). In-component: topology shows a pulsing grey rectangle skeleton; funnel shows 5 skeleton tiles; in-transit shows 3 skeleton rows; KPI values show "…" and pulse. Good skeleton coverage.
- **Error:** explicit, honest, and recoverable — topology, funnel, and in-transit each render a "Couldn't load…" message (`role="alert"`) with a **Retry** button (`refetchOrders`). KPIs show "—" and "Live data unavailable". Deliberately does NOT show the onboarding empty state on an error (guarded against `ordersError`). Strong adherence to the "never show healthy when failing" rule.
- **Success/feedback:** mostly state-driven re-render (counts update, exception strip appears/disappears). Wizard shows inline "Saving…" and per-step success advances. No toast confirmations; CSV export is silent.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** full layout — KPI strip 4-up (`xl:grid-cols-4`), bottom row 2-up (`xl:grid-cols-2`), funnel 5-up (`lg:grid-cols-5`). WireTopology renders the full SVG canvas (`hidden lg:block`). Content capped at ~1480px and centered, so HD has large side gutters.
- **Tablet 768:** WireTopology switches to the **lane-list** fallback (`lg:hidden`) — stacked buyer→arc→supplier cards, no SVG. KPI strip drops to 2-up; bottom row to 1 column; funnel to 2/3-up. Supplier-health bar (`sm:block`, 160px) still shows at ≥640px. PageHeader actions wrap below the title.
- **Mobile 390:** everything stacks. Funnel 2-up; KPIs 2-up; in-transit rows wrap (PO/buyer/format on top, stage badge drops below); supplier-health hides the 160px bar, keeps name + %. LaneDrawer is `maxWidth:100vw` (full-bleed). OnboardingChecklist collapses its 2-col grid to single column.
- **Known cliffs:** the SVG canvas has `min-w-[760px]` with `overflow-x-auto`, so between `lg` (1024px) and ~1130px the canvas can horizontally scroll inside its card. The checklist grid uses `lg:grid-cols-[minmax(280px,0.85fr)_minmax(430px,1.15fr)]` — at exactly `lg` with a long step description it can feel cramped. KPI strip jumps 2→4 only at `xl` (1280px), so 1024–1279px shows a 2×2 grid that under-uses width.

### Current UX issues
- **Token drift / hard-coded values everywhere.** The screen is built almost entirely from inline `style={{}}` with literal hex (#0B1A2F, #5E6779, #E5E8EE, #B36D14, #FFF8EA…) and fractional px sizes (10.5/11.5/12.5/13). Violates "ONE spacing rhythm" and "ONE type scale" — sizes don't snap to a 4/8 grid and weights/colors are chosen per-call-site. (DESIGN BAR 1, 2)
- **Hierarchy carried by color, not size+weight.** Many secondary lines are #5E6779 on white at small sizes; the `--ink-faint` captions risk falling below 4.5:1. (DESIGN BAR 2)
- **No single status-badge system.** The "In transit" row uses a bare colored stage WORD (no pill), the funnel uses icon+uppercase-label tiles, the supplier-health uses a colored %, the LaneDrawer uses "Healthy/At risk/Down" text, and `StatusJourney`/`StatusCell` define yet another pill set. Five different status vocabularies on one screen. (DESIGN BAR 4)
- **Three near-identical inset-pill segmented controls** (window selector, hero tabs) each hand-rolled with duplicated markup and navy-active styling — should be one shared `SegmentedControl`. (DESIGN BAR 8)
- **More than one primary action competing.** Export, the window selector, the two hero tabs, the exception strip, the checklist green CTA, and multiple linked KPI/funnel tiles all read as roughly equal weight. There is no single visually dominant primary action; the green checklist CTA only appears during onboarding. (DESIGN BAR 7)
- **"In transit" list is not a real table** — it's a `divide-y` of flex rows with no header, no sort, no column alignment; PO/buyer/format/stage don't line up between rows, and the mini-stepper adds vertical noise. (DESIGN BAR 5)
- **LaneDrawer is 100% inline-styled, has an ✕ glyph button with no `aria-label`, and a `✕` text character instead of an icon.** Its "Recent deliveries" mock crossings only render in mock mode; the live path is supplier-scoped (not buyer↔supplier-pair scoped) which is a known honesty caveat. (DESIGN BAR 9, accessibility)
- **OnboardingWizard modal has no Esc-to-close** (only ✕ / backdrop / button), unlike LaneDrawer which does — inconsistent dismissal. (DESIGN BAR: modals need clear close/escape)
- **Icon-only buttons / glyphs** (wizard ✕ uses an SVG with `aria-label="Dismiss wizard"` — good; LaneDrawer ✕ is a bare text glyph with no label — bad). Inconsistent. (accessibility)
- **The "System map" SVG can horizontally scroll** on tablet/small-desktop (`min-w-[760px]`), an awkward in-card scroll. The mobile lane-list is good but the breakpoint switch is abrupt.
- **Mixed temporal bases** are explained in copy ("All time" vs "Last 30 days") but require the user to read fine print to know the four KPIs aren't comparable — the design could make scope a visible chip rather than buried sub-text.
- **No breadcrumb / page is the app root** — fine, but the header's status sub-line packs "Live order view · N connections · N active suppliers" as a single muted run-on rather than discrete labeled stats.

### Redesign recommendations (for Claude Design)
1. **Tokenize the entire screen.** Replace every inline hex + px with the existing CSS variables / Tailwind scale (navy #0B1A2F, violet brand, green #2E8E3A success, amber #B36D14 warn, red #B43838 block; surfaces #FFFFFF/#F6F7FA; border #E5E8EE). Snap all padding/gap/margins to a strict 4/8px rhythm and collapse the fractional font sizes to one type scale (heading 600 / label 500 / body 400). Keep the navy + violet Bridge brand. (BAR 1, 2)
2. **Unify status into ONE badge system.** One pill shape/size/padding with green/amber/red/neutral + icon-or-word, used by the in-transit stage, supplier health, funnel tiles, and LaneDrawer health alike. Consider reusing/upgrading `UnifiedStatusBadge`. Never color-only. (BAR 4)
3. **Make the funnel the single dominant hero and pick ONE primary action.** Elevate the pipeline funnel; demote Export and the window selector to quiet outline/ghost controls. Make "Review exceptions" the clear primary when exceptions exist (it is the coordinator's #1 job) — one green, ≥44px, visually dominant CTA. (BAR 7)
4. **Turn "In transit" into a real, dense table** with a sticky header, one row height, aligned columns (PO# | Buyer | Format | Stage | progress), tabular figures, low-contrast gridlines, hover, and aria-sort — instead of free-flow flex rows. Same density language as the inbox. (BAR 3, 5)
5. **Extract ONE shared `SegmentedControl`** for the window selector and hero tabs (and reuse app-wide). One radius, one active treatment (navy fill), one focus ring, ≥44px targets. (BAR 8, 9)
6. **Rebuild LaneDrawer with the shared drawer primitive:** tokenized, animate-from-trigger, scrim, Esc + ✕ (with `aria-label`) + backdrop close (it already has Esc — keep it as the standard and bring the wizard up to parity). Replace the ✕ text glyph with a Lucide `X` and an aria-label. (BAR: drawers/modals, accessibility)
7. **Give OnboardingWizard Esc-to-close** and a focus trap; align its close affordance and animation with LaneDrawer so all overlays behave identically. (consistency, accessibility)
8. **Surface KPI temporal scope as a visible chip** ("All time" / "Last 30d") on each card instead of fine-print sub-text, so the four headline numbers' bases are legible at a glance and never read as contradictory. (honesty + clarity)
9. **Standardize card chrome:** one radius (`--radius-card`), one border (#E5E8EE), one shadow tier for the KPI/funnel/list cards, and one elevated tier for the drawer/modal — remove the ad-hoc `0 1px 2px rgba(11,26,47,0.04)` repeated literally in ~8 places. (BAR 8)
10. **Fix the tablet topology cliff:** either let the SVG canvas scale down responsively (drop `min-w-[760px]`) or extend the lane-list up into the small-desktop range so there's no in-card horizontal scroll. Keep the mobile lane-list (good pattern). (BAR 10)
11. **Add visible focus-visible rings + hover/pressed states** to every pill, tile-link, and row (many rely on `hover:bg`/`hover:shadow` only). Ensure all interactive controls clear 44px. (BAR 9)

---

### Screenshots — PRODUCTION (8)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/01-base-desktop-1440-dashboard-bridge.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/01-base-hd-1920-dashboard-bridge.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/01-base-mobile-390-dashboard-bridge.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/01-base-tablet-768-dashboard-bridge.png)

**lane-drawer-open · desktop-1440**

![lane-drawer-open · desktop-1440](screenshots-prod/01-lane-drawer-open-desktop-1440-dashboard-bridge.png)

**onboarding-wizard-modal · desktop-1440**

![onboarding-wizard-modal · desktop-1440](screenshots-prod/01-onboarding-wizard-modal-desktop-1440-dashboard-bridge.png)

**system-map-mobile-lane-list · mobile-390**

![system-map-mobile-lane-list · mobile-390](screenshots-prod/01-system-map-mobile-lane-list-mobile-390-dashboard-bridge.png)

**system-map-tab · desktop-1440**

![system-map-tab · desktop-1440](screenshots-prod/01-system-map-tab-desktop-1440-dashboard-bridge.png)

---

## 02. Inbox — Order Work Queue — `/inbox`

- **File:** `src/app/(app)/inbox/page.tsx` (thin wrapper; metadata `title: "Inbox — ProcuLink"`, renders `<InboxView />`)
- **Key components:**
  - `src/components/bridge/InboxView.tsx` (the whole page — 1581 lines, `"use client"`, TanStack Table)
  - `src/components/bridge/layout/PageShell.tsx` (`variant="wide"`, max-width `var(--container-wide)` ≈ 1480px, grey `var(--bg)` canvas)
  - `src/components/bridge/layout/PageHeader.tsx` (title + sub + actions slot)
  - `src/components/bridge/StatusJourney.tsx` (compact 5-node pipeline track + `CrossingStatus` type)
  - `src/components/bridge/FileChip.tsx` (coloured source-format tag)
  - `src/components/bridge/inboxSend.ts` (pure helpers: `isRedeliverable`, `shouldShowBulkBar`, `formatBulkSendResult`)
  - `src/components/bridge/BridgeLoader.tsx` (`BridgePageLoader` for `loading.tsx`)
  - Hooks: `src/hooks/useOrderDirection.ts`, `src/hooks/useSampleOrder.ts`, `src/hooks/useQueriesEnabled.ts`
  - Data: `src/lib/api-client.ts` (`getOrders`, `getOrdersSummary`, `redeliverOrder`), types in `src/types/procurement.ts`
- **Capture URL (mock):** `/inbox` (the list renders 50 generated mock rows; a row click navigates to `/inbox/demo-001` — `demo-001` is the first seeded mock detail id)

### What it is & why it exists
The Inbox is the operator's work-queue: every order that has entered ProcuLink (uploaded, emailed, or API-ingested) appears here as one row, showing where each order sits in the `Parse → Normalize → Validate → Transform → Deliver` pipeline. It is the triage surface for the *review* and *deliver* stages — the procurement coordinator opens it to answer "what needs me right now?" (the header literally reads "N need review · N failed"), to filter to the orders that are blocked or ready, and to click into a single order's review screen. It also offers a bulk "Send selected" path to re-deliver orders that are ready or whose delivery failed.

### Who uses it & the primary job
Primary persona: the **procurement coordinator / operator** running daily PO flow. The single most important task: **scan the queue, spot the orders that need attention (needs-review / failed), and open one** — i.e. each row's job is to communicate status + counterparties + value at a glance and route to `/inbox/[orderId]` on click. The secondary job is **bulk re-delivering** ready/failed orders without opening each one.

### Layout & structure (current)
Top-to-bottom inside a wide `PageShell` (grey canvas, content centred at ~1480px, gutter `16 → 24 → 34px`, vertical `20 → 28px`):

1. **PageHeader row** — `h1` "Inbox" (Bricolage Grotesque, 28→30px, weight 600, `var(--ink)` navy). Subtitle line (13px, `var(--ink-muted)`): `"{reviewCount} need review · {failedCount} failed"`, with `· N selected` appended in blue when rows are selected. Right-aligned actions: a **Sync** button (white outline, 32px, ↻ glyph that spins while `isFetching`, label flips to "Syncing…") and the **↑ Upload order** primary button (solid blue `#1E66C9`, white text, 32px, hover → `#0F4FA8`, routes to `/upload`).
2. **Bulk action bar** (conditional) — full-width navy (`#0B1A2F`) strip, radius 8, `mb-3`. Left: "{N} selected" headline + Clear/Dismiss text button. Right: a `role="status"` result line (green `#7FD18A` ✓ / red `#F2A6A6` ⚠) + a "Send selected" / "Sending…" text button. Shown when `selectedCount > 0` OR a `bulkResult` is still on display.
3. **Toolbar** (on grey canvas, `pb-3`) — left: a horizontal-scroll row of 5 **filter chips** (All orders / Needs review / Ready to send / Delivered / Failed), each 28px tall, with a mono count badge; active chip = navy fill, white text. Right: a **search box** (32px, white, `🔍` emoji + input, placeholder "Search PO, buyer, supplier…", capped 160–240px on `sm+`) and a **Columns** menu button (`lg+` only, `▦` glyph, toggles a dropdown).
4. **Queue table card** — a single floating white card (`#FFFFFF`, 1px `#E5E8EE` border, radius 12), `flex-1`, internally scrollable. Inside:
   - **Mobile (`< lg`):** a stack of route **cards** (`p-3` gap `2.5`), not the table.
   - **Desktop (`lg+`):** a fixed-layout `<table>` (`minWidth: 1180px`, `tableLayout: fixed`, `borderCollapse`, 12.5px) with a **sticky header** (`thead`, `position: sticky; top: 0; z-index: 4`, white bg, uppercase 10.5px/700 `var(--ink-faint)` labels, sort arrows). Rows are 56px tall with 1px `#F0F2F6` separators.
5. **Footer row** (on grey canvas) — left: `"{totalCount} orders"` (11px faint, `· N selected` in blue). Right: pagination — **← Prev**, mono `Page {currentPage} of {totalPages}`, **Next →** (28px buttons, disabled states greyed to `#CBD0DA`).

Density/type/spacing observations: spacing and sizing are highly *ad-hoc* — almost everything is inline `style` with literal px (`height: 32`, `padding: "9px 10px"`, `11px 10px`, font sizes `10.5 / 11 / 12 / 12.5 / 13 / 14px`, radii `6 / 8 / 10 / 12`). Colours are hard-coded hex constants (`BLUE = #1E66C9`, `NAVY = #0B1A2F`, plus `#5E6779`, `#E5E8EE`, `#F0F2F6`, `#CBD0DA`, `#FBE3E3` etc.) only loosely mapped to tokens. The status pill (`StatusDotPill`) *does* use the `.pill / .pill-*` token classes from globals.css.

### Data shown
Entity: **Order** (rendered as `OrderRow`, mapped from `OrderSummary`). Per row:

| Column (id) | Field shown | Source |
|---|---|---|
| `select` | checkbox (only enabled for `ready_to_deliver` / `delivery_failed` raw statuses) | `OrderRow.rawStatus` via `isRedeliverable` |
| `po` ("Order") | `poNumber` (mono semibold navy) + `"{lines} lines · {issues} to review"` sub | `OrderSummary.poNumber / lineCount / unresolvedCount` |
| `lane` ("Buyer → Supplier" / "Customer → You") | `buyer` (blue) → `supplier` (green); falls back to `labels.unknownBuyer` | `OrderSummary.buyerName / supplierName`; header from `useOrderDirection().labels.railHeader` |
| `fmt` ("Source") | `FileChip` (PDF/cXML/XLSX/EDI/EMAIL/API/CSV) | `OrderSummary.sourceFormat` → mapped to upper-case label |
| `value` ("Value") | `valueLabel` e.g. `€ 24,180.50` (mono semibold) | `OrderSummary.totalValue` + `currency` |
| `status` ("Pipeline") | `StatusJourney` compact 5-node track | `mapStatus(status)` → `STATUS_PRESENTATION[...].stage` |
| `statusPill` ("Status") | `StatusDotPill` (New/Extracting/Needs review/Normalized/Delivered/Ready to send/Failed) | `mapStatus(status)` |
| `ageMin` ("Updated") | `"{age} ago"` e.g. "2m ago", "1h ago" | derived from `OrderSummary.createdAt` |
| `chevron` | `›` affordance | static |

Data source:
- **List:** `apiClient.getOrders({ page, pageSize: 25, status, search })` → `GET /api/orders?page=&pageSize=&status=&search=` returning `{ items: OrderSummary[], totalCount, page, pageSize }`. Mock fn: `mockGetOrders` (filters/paginates `mockOrders` client-side).
- **Counts:** `apiClient.getOrdersSummary()` → `GET /api/orders/summary` returning `{ byStatus: Partial<Record<OrderStatus,number>>, total }`. Mock fn: `mockGetOrdersSummary`. Drives header summary + every chip badge in BOTH mock and live.
- **Mock mode:** `MOCK_ORDERS = generateOrders(50)` — 12 hand-seeded rows (ids `demo-001`, `nrd9981`, `sh44120`, `850201`, `wmt341`, `008411`, …) + 38 procedural (`gen-000012`…).
- **Bulk send:** `apiClient.redeliverOrder(id)` → `POST /api/orders/{id}/redeliver` (server-gated to `RedeliverableFrom = {delivery_failed, ready_to_deliver}`).

Important display nuance: the backend `ready` status renders as **"Normalized"** (pre-transform) while `ready_to_deliver` renders as **"Ready to send"** — deliberately split so the row badge can't contradict the "Ready to send" chip. The red "Failed" pill **collapses five** backend statuses (`failed`, `transform_failed`, `delivery_failed`, `delivery_dead_letter`, `rejected_by_supplier` = `FAILED_BUCKET`) into one display state.

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| Sync button (header) | `queryClient.invalidateQueries(["orders"])` | refetches list; ↻ spins, label → "Syncing…", disabled while `isFetching` |
| ↑ Upload order button (header) | `router.push("/upload")` | navigates to upload page |
| Filter chip × 5 (All / Needs review / Ready to send / Delivered / Failed) | `handleChip(idx)` — mock: client column filter; live: sets `?status=` | re-filters list, resets to page 1, clears selection; active chip = navy fill |
| Search input | `handleSearch(value)` — mock: instant client filter; live: 350ms debounce → server `search=` | filters by PO / buyer / supplier; resets page 1, clears selection |
| Columns button (`lg+`) | `setColumnsMenuOpen(o => !o)` | opens the column-visibility dropdown (see overlays) |
| Column header (sortable: Order, Source, Value, Pipeline, Updated) | `header.column.getToggleSortingHandler()` — click or Enter/Space | toggles asc/desc; `aria-sort` announced; arrow ↑/↓/⇅ |
| Row checkbox | `row.getToggleSelectedHandler()` | selects row for bulk send; disabled (35% opacity, not-allowed) unless `isRedeliverable(rawStatus)`; `stopPropagation` so it doesn't open the row |
| Header "select all" checkbox | `table.getToggleAllPageRowsSelectedHandler()` | selects all *sendable* page rows; disabled (40% opacity) + explanatory title when none sendable |
| Table row (body) | `router.push('/inbox/{id}')` (whole `<tr>` onClick) | opens the order review screen |
| Mobile route card | `router.push('/inbox/{id}')` (`<button>`) | opens the order review screen |
| j / ArrowDown, k / ArrowUp | keyboard row highlight (desktop) | moves the active-row highlight, scrolls into view |
| Enter (with active row) | `router.push('/inbox/{activeRow.id}')` | opens the highlighted order |
| Bulk bar "Send selected" | `handleSendSelected()` → parallel `redeliverOrder(id)` | re-delivers selected; reports per-PO failures; keeps only failed rows selected |
| Bulk bar Clear / Dismiss | `setRowSelection({}); setBulkResult(null)` | clears selection / dismisses result bar |
| ← Prev / Next → (footer) | `setPage(...)` | paginates; disabled at ends |
| Empty-state "↑ Upload an order" | `router.push("/upload")` | navigates to upload |
| Empty-state "Try a practice order" | `sample.runSample()` (`useSampleOrder("/inbox")`) | `POST /api/onboarding/sample-order`, seeds a sample, routes to it; shows "Starting practice order…" + error line on failure |
| Error-state "↻ Retry" | `refetch()` | retries the orders query |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Columns visibility menu | dropdown / popover (`role="menu"`, absolute, white card, radius 8, shadow `0 8px 24px rgba(11,26,47,0.12)`, z-20) | the **Columns** toolbar button (`lg+` only) | "SHOW COLUMNS" heading + a `role="menuitemcheckbox"` toggle per hideable column (Source / Value / Pipeline / Updated) with a read-only checkbox + label | outside `mousedown` (doc listener), **Escape** key, or toggling the button again. (No backdrop scrim.) |
| Bulk action bar | inline panel (not an overlay — pushed into flow, navy strip below header) | selecting ≥1 row (`selectedCount > 0`) OR a `bulkResult` present | "{N} selected" headline, Clear/Dismiss, a `role="status" aria-live="polite"` result line, "Send selected"/"Sending…" button | "Clear" (clears selection) or "Dismiss" (clears result); auto-clears selection on a full success |
| Tooltips | native `title=` attributes | hovering disabled select checkboxes / select-all header | "Only orders that are Ready to send or have a failed delivery can be sent" etc. | pointer leave (browser-native) |

There are **no modal dialogs, drawers, or sheets** on this page — every "open" either is an inline panel (bulk bar), a single anchored dropdown (Columns), or a route navigation. The keyboard handler explicitly bails when `[role="dialog"]` / `[aria-modal]` exists, confirming none are expected here. Row clicks **navigate in place** to `/inbox/[orderId]` rather than opening a detail drawer.

### States
- **Empty (genuinely empty, no filter):** in-card centred state — `⊘` glyph, **"Your inbox is clear"** (20px/600 Bricolage on desktop, 14px on mobile), direction-aware copy ("New orders land here automatically as buyers/customers send them, or upload one yourself."), a blue **"↑ Upload an order"** button, and a secondary **"Try a practice order"** button (sample-order CTA). Rendered both in the desktop table body (`colSpan`) and the mobile card stack.
- **Empty (filtered/searched, 0 results):** distinct state — `⊘` glyph, **"No matching orders"**, "No orders match the current filter or search…", and a **"Clear filters"** button (`handleClearFilters`). No practice-order CTA in this branch.
- **Loading:** two layers. (1) Route-level `loading.tsx` → `BridgePageLoader` (the animated blue→green "wire" mark over `#F6F7FA`, reduced-motion frozen). (2) In-component **skeleton** for the first page load (`isInitialLoading = !mock && isLoading && !ordersPage`): desktop = 9 skeleton rows (one pulse bar per visible column, header stays mounted); mobile = 6 card-shaped skeletons. Subsequent page/filter fetches keep prior rows visible (`placeholderData: prev`) — no spinner flash.
- **Error:** full-screen replacement inside `PageShell` — a `#FBE3E3` circle with `⚠`, **"Couldn't load the queue"**, reassuring body ("your data is safe… Try again in a moment."), and a **"↻ Retry"** button (`refetch`). Triggered only in live mode on `isError`.
- **Success/feedback:** Sync button spinner + "Syncing…"; bulk bar `aria-live` result line ("N orders sent" green / per-PO failure list red); selection count echoed in header + footer; active-row blue inset ring on keyboard nav.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** full desktop table inside the wide (~1480px) shell; sticky header; Columns menu visible (`lg+`); all 9 columns shown; toolbar chips + search + Columns on one row.
- **Tablet 768:** still **below the `lg` table breakpoint** — renders the **mobile route-card stack**, NOT the table (table is `hidden … lg:block`). Columns menu hidden. Toolbar collapses: chips on a horizontal-scroll row, search drops to its own full-width row (`sm:flex-row` brings them side-by-side at `sm`, but the table itself only appears at `lg`). This is a notable cliff: a 768–1023px viewport with plenty of width still shows phone-style cards instead of the table.
- **Mobile 390:** route cards — each card stacks PO# + age/lines/value sub, a status pill top-right, a FileChip + red "N to review" tag row, and a buyer→supplier rail that stacks vertically (`↓` connector) when a buyer exists. Primary upload action reachable via the header. Bulk-select checkboxes are **not present** on mobile cards (selection/bulk-send is desktop-only).

### Current UX issues
- **Glyph/emoji iconography instead of a single icon system.** The brief mandates Lucide icons, but this page uses literal characters: `↻` (sync/retry), `↑` (upload), `🔍` (search emoji), `▦` (columns), `⊘` (empty), `⚠` (error), `›` (chevron), `→`/`↓` (rail), `↑↓⇅` (sort). Inconsistent weight, baseline, and a11y; the `🔍` emoji especially clashes with the navy/violet brand.
- **Spacing/type/radius drift everywhere.** Sizing is inline literals off a non-4/8 scale (`12.5px`, `10.5px`, `11px`, `padding: "9px 10px"`, radii `6/8/10/12`, heights `28/32`). Violates the "one spacing rhythm / one type scale" bar. Should be tokenised.
- **Two parallel status systems on every row.** The "Pipeline" column (`StatusJourney`) and the "Status" pill encode the *same* state twice, eating ~308px of width and adding cognitive load. The brief wants ONE status-badge system.
- **Tabular figures only partially applied.** PO# and Value use `font-mono` (good), but counts ("14 lines · 3 to review"), the "Updated" age ("2m ago"), and chip badges are not consistently tabular — columns can jitter.
- **Hierarchy carried by colour, low-contrast greys.** Sub-text uses `#5E6779` and `var(--ink-faint)` on white; the buyer/supplier rail and "Updated" rely on colour for meaning. Several greys risk falling under 4.5:1. The brief: carry hierarchy via size+weight, not colour.
- **Tablet cliff (768–1023px).** A wide tablet shows phone cards, not the table — wasted horizontal space and an inconsistent experience versus 1024px+.
- **Click targets below 44px.** Select checkboxes are 13×13px; sort headers, chips (28px), Columns/pagination buttons (28–32px) are under the 44px minimum. Hover exists but pressed/focus-visible states are inconsistent (focus ring not explicitly styled on the inline-styled controls).
- **Row-level zebra/hover is ad-hoc.** Hover/selected/active/review-tint/failed-tint backgrounds are five hand-rolled hex values applied via JS `onMouseEnter/Leave` rather than CSS; gridlines use two different greys (`#E5E8EE` header vs `#F0F2F6` rows).
- **Bulk bar is a navy slab disconnected from the table.** It animates in by mounting (no transition), sits above the toolbar, and its actions are *text links* (no button affordance / size). The result line and "Send selected" share a cramped right cluster.
- **"Pipeline" + "Failed" semantics can mislead.** The collapsed "Failed" pill mixes redeliverable and non-redeliverable failures; only the checkbox gating hints at the difference — a user can't tell from the row why a "Failed" order isn't selectable without reading a tooltip.
- **Empty/error copy duplicated** across desktop and mobile branches (drift risk), and the error state replaces the whole screen (loses the header/toolbar context the loading state preserves).

### Redesign recommendations (for Claude Design)
1. **Unify the status system into ONE badge (highest impact).** Collapse the "Pipeline" column and the "Status" pill into a single token-driven status badge (one shape/size/padding, green=delivered/output, amber=needs-review, red=failed-blocking, blue=in-progress, neutral=new) with an icon + word. Keep a small optional 5-dot progress affordance *inside* a hover/popover, not as a permanent second column — reclaims ~300px and removes the duplicate encoding. Preserve the honest `ready` ("Normalized") vs `ready_to_deliver` ("Ready to send") split.
2. **Tokenise spacing, type, radius, and colour; drop inline literals.** Move to a strict 4/8 scale, one type scale (heading 600 / label 500 / body 400), one card radius + one border colour (`gray-200`) + one shadow tier, and the existing `--brand-*` / `--ink-*` tokens. This single sweep fixes most "unfinished" reads.
3. **Replace all glyphs/emoji with Lucide.** `RefreshCw` (sync), `ArrowUpFromLine`/`Upload`, `Search`, `Columns3`, `Inbox`/`CheckCircle2` (empty), `AlertTriangle` (error), `ChevronRight`, `ArrowRight` (rail), `ChevronsUpDown`/`ArrowUp`/`ArrowDown` (sort). Add `aria-label`s on icon-only buttons.
4. **Make the table responsive down to ~1024px and define a deliberate tablet layout.** Either let the table appear at `md` with horizontal scroll + a sticky first column, or design a richer two-line tablet card. Eliminate the 768–1023px phone-card cliff.
5. **Apply tabular figures to every number** (PO#, qty/line counts, value, age, chip badge counts, "Page X of Y") so columns stop jittering and money aligns.
6. **One row density + CSS-driven states.** Single 48–56px row height, one cell padding, gray-200 gridlines, a single hover and a single selected style in CSS (not JS), low-contrast review/failed left-edge accent bar instead of near-invisible 8%-alpha tints. Add `aria-sort` (already present) + a visible sortable affordance on hover.
7. **Promote one primary action, demote the rest.** Keep "Upload order" as the dominant green/blue ≥44px primary; make Sync, Columns, chips, pagination consistent outline/ghost secondaries with real button sizing (≥44px touch, visible hover + pressed + focus-visible ring).
8. **Rework the bulk bar as a sticky bottom action bar** that animates from the bottom, with real buttons ("Send selected" primary, "Clear" ghost), a clear count, and the result/feedback inline — separated from destructive ambiguity. Keep the per-PO failure detail.
9. **Clarify failed semantics in the row.** Split "Failed" into a redeliverable vs non-redeliverable visual cue (e.g. an inline "Retry" affordance only on `delivery_failed` rows, a muted "Needs fix" on parse/transform failures) so selectability is self-evident without a tooltip — and never imply "200 = accepted".
10. **Single source for empty/error/loading copy** shared between desktop and mobile; keep the header + toolbar mounted in the error state (as the loading state already does) and show a skeleton — never a bare spinner. Preserve the "Try a practice order" CTA only in the genuine-empty branch.

---

### Screenshots — PRODUCTION (9)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/02-base-desktop-1440-inbox-list.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/02-base-hd-1920-inbox-list.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/02-base-mobile-390-inbox-list.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/02-base-tablet-768-inbox-list.png)

**bulk-send-bar · desktop-1440**

![bulk-send-bar · desktop-1440](screenshots-prod/02-bulk-send-bar-desktop-1440-inbox-list.png)

**columns-menu-open · desktop-1440**

![columns-menu-open · desktop-1440](screenshots-prod/02-columns-menu-open-desktop-1440-inbox-list.png)

**empty-filtered-no-results · desktop-1440**

![empty-filtered-no-results · desktop-1440](screenshots-prod/02-empty-filtered-no-results-desktop-1440-inbox-list.png)

**filter-failed-active · desktop-1440**

![filter-failed-active · desktop-1440](screenshots-prod/02-filter-failed-active-desktop-1440-inbox-list.png)

**mobile-route-cards · mobile-390**

![mobile-route-cards · mobile-390](screenshots-prod/02-mobile-route-cards-mobile-390-inbox-list.png)

---

## 03. Order Review / Mapper (the "Workshop") — `/inbox/[orderId]`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/inbox/[orderId]/page.tsx`
- **Key components:**
  - `src/components/bridge/workshop/OrderWorkshop.tsx` — the orchestrator (header, send gate, flow notice, body switch, all overlays)
  - `src/components/bridge/mapper/MapperWorkbench.tsx` — the 3-pane mapper shell (toolbar, attention-first split, wire layer host)
  - `src/components/bridge/mapper/IncomingPane.tsx` — left "What we received" column (grouped source rows + values)
  - `src/components/bridge/mapper/OutgoingPane.tsx` — center "What we'll send" column (output rows, picker chip, inline AI fix, transform/fixed-value controls, "Add output field")
  - `src/components/bridge/mapper/MapperPreviewPane.tsx` — right "Live preview" column (navy code body, format toggle, copy/download)
  - `src/components/bridge/mapper/SourcePickerChip.tsx` — the inline searchable source dropdown on each output row (picker mode)
  - `src/components/bridge/mapper/TransformPopover.tsx` — the per-field "Edit value" (manipulator-chain) portal popover
  - `src/components/bridge/workshop/IssuesPanel.tsx` — the plain-language "Fix these to send" issue list with inline per-line resolution
  - `src/components/bridge/workshop/SendReadinessStrip.tsx` + `WorkshopStepper.tsx` — slim readiness bar + 5-stage pipeline stepper
  - `src/components/bridge/workshop/MobileTriage.tsx` — the < 1024px review-and-send fallback (no drag canvas)
  - `src/components/bridge/workshop/OrderDetailsDrawer.tsx` — the right "Details" drawer (Audit trail / Standards check / Response sub-tabs)
  - `src/components/bridge/review/ConfirmDialog.tsx` — the send confirmation modal
  - `src/components/bridge/OutputStructureDesigner.tsx` — full-screen "Customize output layout" power editor
  - `src/components/bridge/FailedPanels.tsx` (`ParseFailedPanel` / `FailedPanel`) — parse/transform/delivery failure recovery screens
  - Hooks: `review/hooks/useOrderReview.ts`, `useResolveActions.ts`, `useAcceptanceValidation.ts`, `useSendFlow.ts`; `workshop/useWorkshopLayout.ts`
- **Capture URL (mock):** `/inbox/ord-002` (status `pending_review`, 4 lines, 2 unresolved with AI suggestions — the richest "needs work" state; `ord-001` = clean/ready, `ord-003` = delivered)

### What it is & why it exists
This is the money screen: the place where a messy parsed purchase order becomes the exact file a specific supplier accepts. It sits at `review → transform` in the `parse → normalize → validate → review → transform → deliver → learn` loop. A procurement coordinator opens it to confirm/fix item-code mappings, see (and tune) which incoming field fills each output field, watch a byte-accurate live preview of what the supplier will receive, clear every blocking issue, and press one Send that transforms and delivers. It is deliberately the only screen that gates Send on server-truth validation (`exceptionCount === 0 && blockingIssues === 0`) so HTTP 200 is never mistaken for supplier acceptance.

### Who uses it & the primary job
Primary persona: the **procurement coordinator** (approves a €399 plan alone, lives in Excel today). The single most important task: **resolve every unresolved line / required field, confirm the preview is correct, and Send.** A secondary **integration expert** persona uses the deeper affordances (source picker re-pointing, fixed values, value transforms, the Output Structure Designer, the Standards-check tab) but everything is one experience with progressive disclosure — no mode toggle.

### Layout & structure (current)
Top-to-bottom, the whole screen is a flex column (`100% h`, `#F6F7FA` background, `overflow:hidden`):

1. **Header bar** (white, `1px #E5E8EE` bottom border, `px-4 lg:px-6`, ~`pt/pb 2.5`). Left cluster: a 30×30 boxed back arrow (`← Back to inbox`), then the PO number as an `h1` (Bricolage Grotesque, 22px, weight 800), a `UnifiedStatusBadge`, an optional amber "Looks like an invoice" pill (`InvoiceBadge`, when `documentType === "invoice"`), and an optional red dead-letter pill. Sub-line: `buyer (blue #1E66C9) → supplier (green #2E8E3A) · grand total (mono #566982)`. Right cluster (`ml-auto`, `flex-wrap`, lg+ only for the first two): **Details** outline button, the **Focus** segmented control (All / Mapping / Output), and the dominant **Send** button (green when armed `#2E8E3A`, muted gray-green `#5A7660` when disabled; label morphs to `Fix N to send` / `Preparing the file…` / progress / done).
2. **Flow notice strip** (conditional) — full-width `role="status"` bar tinted by severity (blue info / green success / red error) carrying send progress + outcome text from `useSendFlow`.
3. **Send-readiness strip** (lg+ only, `SendReadinessStrip`) — slim full-width bar: green "Ready to send — every required field is filled and validated" OR amber "N fields to fill before sending" + one mono chip per blocker (click → scroll/flash the issue card). At its right end the `WorkshopStepper` (xl+ only) renders the 5-stage Parse → Normalize → Validate → Transform → Deliver pipeline.
4. **Body** (`flex:1; overflow:auto`):
   - **Desktop (lg ≥1024):** an optional `IssuesPanel` card on top, then the `MapperWorkbench`. The workbench is a **two-level CSS grid**: OUTER `[ received+output canvas | live-preview ]` at `minmax(0,1.85fr) minmax(380px,1.05fr)` (collapses to a 46px rail when a pane is collapsed); INNER canvas `[ received | output ]` at `minmax(300px,0.92fr) minmax(360px,1fr)` with the SVG wire overlay drawn over the inner pair. Above the canvas sits the workbench toolbar ("Map this order", an N-of-M mapped chip, saving/✓Saved/error/AI-unavailable inline status; right side: Show/Hide connections toggle, required-unmapped warning, **Customize output layout**, **Fill from catalog · N**, optional **Save mappings**, optional **Send**). Each of the three panes has an identical 52px column header forming one connected strip (blue dot "What we received" · green dot "What we'll send" · green dot "Live preview · FORMAT").
   - **Mobile/tablet (< lg):** the `MobileTriage` review-and-send surface renders instead (summary cards + the same issue list + one-click fixes + a sticky Send bar). The drag canvas is intentionally not shown.
5. **No persistent footer** — the primary action lives in the header (desktop) or a sticky bottom Send bar (mobile triage).

**Density/type/spacing observations:** spacing is highly hand-tuned with **fractional pixel values everywhere** (font sizes 9.5/10.5/11.5/12.5/13.5, paddings like `9px 11px`, `3px 11px`, gaps 6/7/10/12) — heavily inline-styled, not on a strict 4/8 scale. Numbers correctly use `'JetBrains Mono'` + `fontVariantNumeric: tabular-nums` in most places (values, totals, output paths). Headings use Bricolage Grotesque; labels weight 600–800.

### Data shown
- **Order** (`Order` from `getOrderById(orderId)` → `["order", orderId]`): `poNumber`, `status`, `documentType`, `supplierId`, `supplierName`, `buyerName`, `orderDate`, `currency`, `grandTotal`/`subTotal`/`taxTotal`, `paymentTerms`, `sourceFileKey`, `artifacts[]`, and `lines[]` each with `lineNumber`, `buyerItemCode`, `supplierItemCode`, `description`, `quantity`, `unit`, `unitPrice`, `confidence`, `needsReview`, and optional `aiSuggestion {supplierItemCode, confidence, reason, provenance}`. (Mock store: `mockOrders` in `api-client.ts`.)
- **Incoming pane** rows = the model's `sourceFields` (header / parties / line items / raw extras), each `label` + real `value` (mono) + mapped/AI-suggested flags. Built from the parsed order directly (`incomingFromOrder.ts`), not a separate fetch.
- **Outgoing pane** rows = the model's `targetFields` (output path + human label + per-row resolved status: wired / fixed / auto / unmapped + `valuePreview`). Plus read-only auto-filled Ship-to/Bill-to/Contact/Tax-ID blocks for structured formats.
- **Live preview** = `previewMappingOverride(orderId, override, format, honorFormat)` — the actual delivered bytes (or honest amber warning).
- **Mapping override** seed = `getMappingOverride(orderId)` → `["mapping-override", orderId]`.
- **Issues** = `buildFixQueue(order, validationResult)` → mapped to `WorkshopIssue[]` (title + why + kind + severity).
- **AI calibration** = `getAiCalibration()` → drives the trust threshold for the attention-first split.
- **Audit events** = `getOrderAudit(orderId)` (only when `status === "failed"`, to seed the parse-failure copy).
- **Drawer** sub-panels (`OrderPassport`, `ConformancePanel`, `SupplierResponsePanel`) each fetch their own data on mount.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| Back arrow (header) | click | `router.push("/inbox")` |
| PO `h1` / status badge / invoice pill | display only | — |
| **Details** button (lg+) | click | opens `OrderDetailsDrawer` on the Audit-trail tab (`?tab=passport`) |
| **Focus** segmented control (All / Mapping / Output, lg+) | click | sets `useWorkshopLayout` focus → collapses/expands the incoming + preview panes |
| **Send** button | click (only when `canSend`) | opens `ConfirmDialog`; hovering while disabled shows a navy tooltip "Fill N required field(s) below first" |
| Send-readiness blocker chip | click | `onJumpToIssueCard` → scroll + amber flash the matching `IssuesPanel` card |
| IssuesPanel "Enter code" / inline code input + Save / Esc | click/type/Enter | `useResolveActions.startLineEdit` → `commitLineCode` (server commit → refetch) |
| IssuesPanel "Accept suggestion" | click | `resolve.acceptSuggestion(ref)` (one-click AI accept) |
| IssuesPanel "Confirm" / "Change code" (review-flag) | click | `confirmFlaggedLine` / `startLineEdit` |
| IssuesPanel "Accept all AI suggestions" / "Accept ≥85% only" | click | `bulkAcceptSuggestions(0)` / `(0.85)` (same `POST /accept-ai-suggestions` as upload preview) |
| IssuesPanel "Where →" | click | `onFocusField(ref)` → select + scroll the row in the mapper |
| Incoming search box | type (150ms debounce) | filters incoming rows, auto-reveals collapsed groups |
| Incoming filter chips (All / Unmapped / Mapped / Has AI suggestion / Has value) | click | filters rows; counts shown in mono |
| Incoming group header (Header / Parties / Line items / Raw extras) | click | collapse/expand group |
| Incoming row drag grip (⠿, 22px, blue ring) | drag onto an output row | wires the field (drag-to-connect) — wires mode; in picker mode wires are hidden by default |
| **Show / Hide connections** toggle (toolbar, picker mode) | click | reveals/hides the wire SVG layer |
| Output row source picker chip "← pick a field ▾" | click | opens `SourcePickerChip` portal dropdown |
| Source picker option / "= Fixed value…" / "Clear" / search / arrows / Enter | click/type/keyboard | `onPickSource` (→ wire-connect dispatch) / open fixed-value editor / disconnect |
| Output row "= value" chip | click | opens inline fixed-value input (Set / Clear) |
| Output row "Edit value · N" (ƒx) chip | click | opens `TransformPopover` (manipulator chain) |
| Output row status tag ✕ (wired) / ✎ (fixed) | click | `onDisconnect` / open fixed-value edit |
| Output "Apply" (inline AI fix strip) | click | `onPickSource(path, suggestedId)` — maps + clears blocker |
| Output "N mapped · review" attention chip | click | expand/collapse the auto-mapped rows |
| Output "N fields ready · mapped automatically" summary | click | collapse/expand auto group (picker mode) |
| **Add output field** (+ dashed green) | click | opens the canonical-field combobox + custom-create footer |
| **Customize output layout** (toolbar) | click | opens `OutputStructureDesigner` (full-screen power editor) |
| **Fill from catalog · N** (toolbar) | click | scrolls to first line with a catalog price/code hint |
| **Save mappings** (toolbar, when handler present) | click | promotes the per-order mapping to the supplier |
| Preview format toggle (CSV/JSON/XML/cXML/UBL/X12) | click | re-renders the preview in that format (exploratory vs delivered) |
| Preview **Copy** / **Download** | click | clipboard / blob download with correct extension+mime |
| `AI suggestions to review` banner "Dismiss all" | click | rejects every AI wire suggestion |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Send confirmation** (`ConfirmDialog`) | modal (scrim + blur, `z 9990`, focus-trap, `aria-modal`) | the green **Send** button | title, summary grid (grand total · lines · issues · format), policy confirm checkbox, failing-rules acknowledgement checkbox, stale-validation note, retry note, Cancel + primary CTA | Cancel button · Esc · backdrop click · confirm (`onConfirm` → `useSendFlow.confirmSend`) |
| **Order details drawer** (`OrderDetailsDrawer`) | right drawer (scrim `rgba(11,26,47,.18)`, slide-in, focus-trap, `aria-modal`, 760px / max 64%) | header **Details** button, or `?tab=` deep link | sub-tab strip (Audit trail / Standards check / {Supplier} response) + the matching panel (`OrderPassport` / `ConformancePanel` / `SupplierResponsePanel`) | × close button · Esc · backdrop click (clears `?tab=`) |
| **Output Structure Designer** (`OutputStructureDesigner`) | full-screen inline editor (replaces body when `showDesigner`) | toolbar **Customize output layout** | output-node tree editor, paste-supplier-sample inference, JSON/XML/CSV format, value-format presets, live preview, Save | onClose (its own close control) · onSaved (saves + invalidates queries) |
| **Source picker dropdown** (`SourcePickerChip`) | popover (body portal, `position:fixed`, flips above on overflow, `role="listbox"`, `z 1000`) | output row "pick a field ▾" chip | search box, grouped incoming options (AI suggestion first + % confidence + value), "= Fixed value…", "Clear" footer | outside mousedown · Esc · pick an option · footer action |
| **Transform / Edit-value popover** (`TransformPopover`) | popover (body portal, `position:fixed`, flips above, `role="dialog"`, `z 1000`) | output row "Edit value" (ƒx) chip | manipulator chain rows (plain-English labels + params), "+ Add an adjustment…" select, Done; live-saves on change | outside mousedown · Esc · Done button · trigger toggle |
| **Add output field combobox** | popover (in-flow absolute, transparent click-away scrim, `role="dialog"`, `z 31`) | "Add output field" + button | search/type input, grouped canonical Header/Line picker items (with standards ref tooltip), custom-create footer with header/line scope toggle | scrim click · Esc · pick a field · add custom |
| **Inline fixed-value editor** | inline panel (in-row, transient) | output row "= value" chip · picker "= Fixed value…" · status-tag ✎ | text input + Set + Clear | Set (commit) · Esc · Clear |
| **Inline supplier-code editor** | inline panel (in IssuesPanel row) | "Enter code" / "Change code" / "Enter manually" | mono text input + Save + Esc | Save (commit→refetch) · Esc · Cancel |
| **Send-disabled tooltip** | tooltip (absolute navy bubble, `z 60`) | hovering the disabled Send button | "Fill N required field(s) below first…" | mouse leave |
| **Flow notice** | inline status strip (`aria-live`) | `useSendFlow.setFlow` during transform/deliver | progress / success / error text | replaced/cleared by the next flow state |
| **Standards-check** | reuses the Details drawer | OutgoingPane/`onValidate` → `openDetails("conformance")` | the Conformance panel | (see drawer row) |

This screen is overlay-rich (the user excepted it from exhaustive open/close mapping, but the table above is complete). It also navigates in place for back (`/inbox`) and uses `?field=` / `?tab=` query params for deep-link focus rather than separate routes.

### States
- **Empty:** no true "empty list" — an order always has lines/fields once parsed. Incoming pane has honest empty copy per cause ("arrived already-structured…", extraction-failed fallback). Output pane shows "No output fields yet — add one…" when truly empty.
- **Loading:** `loading.tsx` → `BridgePageLoader`. In-component: `BridgePageLoader` with "Preparing your order…" while the order query is in flight; `WorkbenchSkeleton` (3-column pulse grid) while the mapper model loads; preview shows "Rendering…"; a dedicated **parsing** screen (`BridgeLoader` + "We're reading your order…" + PO mono, auto-polls every 3s).
- **Error:** order load failure / not-found → centered card with icon, "Order not found" / "Failed to load order", and a "← Back to inbox" button. Status-driven failure screens: `ParseFailedPanel` (status `failed`, seeded from audit events), `FailedPanel` for `transform_failed` and `delivery_failed`. Preview render failure → amber inline note (never crash). Save failure → inline red error text in the toolbar.
- **Success/feedback:** "✓ Saved" inline flash (~2s) after a clean auto-save; green flow notice on delivered ("Delivered to supplier / Order confirmed. The audit trail has been updated."); preview "Valid" green pill + one-shot content flash; copy "✓ Copied". Issues panel collapses to a green "Ready to send" bar at zero issues.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** full 3-pane mapper — received + output canvas docked beside the live preview (`1.85fr / 1.05fr`); the xl WorkshopStepper shows the full 5-stage pipeline; Details + Focus controls visible.
- **Desktop 1024–1439 (lg):** mapper still renders (the inner canvas fits ~1000px so a 13"/14" laptop keeps the full field mapper); below ~1440 the preview wraps under the canvas rather than docking beside it. The xl stepper hides below 1280.
- **Tablet 768 / Mobile 390 (< lg):** the drag canvas is dropped entirely; `MobileTriage` renders the review-and-send surface (summary cards, full issue list with one-click fixes, read-only "what we'll send" preview, sticky Send bar). The header's Details + Focus controls are hidden so Send isn't clipped.
- **Known cliffs:** the layout leans on hand-tuned `minmax()` tracks and many fractional inline sizes, so column balance is fragile between ~1024–1280; the wire overlay depends on "nothing is sticky / scroll the canvas as one unit," which is correct but brittle to future layout edits.

### Current UX issues
- **Spacing is off any single rhythm.** Padding/gaps/font sizes are pervasively fractional (9.5/10.5/11.5/12.5/13.5px; `3px 11px`, `9px 11px`, gaps 5/6/7/10/12) and almost entirely inline-styled rather than tokenized — violates "ONE 4/8px scale."
- **Glyph zoo / non-Lucide icons.** Arrows and marks are literal Unicode (`←`, `→`, `▾`, `▸`, `›`, `‹`, `⠿`, `✦`, `�e2`, `⚠`, `✕`, `◉`/`○`) plus ad-hoc inline SVGs. Inconsistent with a Lucide system; some are decorative-only without aria.
- **Status/badge inconsistency.** Many parallel pill styles: `UnifiedStatusBadge`, the invoice pill, dead-letter pill, "unmapped"/"needs a value"/"auto"/"not set" tags, AI "✦ AI %", catalog/validation badges, readiness chips. They share intent but not one shape/size/padding/icon system — violates "ONE status-badge system."
- **Two different "fix" surfaces for the same work.** Required/unmapped lines surface both in the top `IssuesPanel` (with inline code entry) AND inline in the OutgoingPane "SUGGESTED … Apply" strip. Powerful but potentially redundant/confusing about where the canonical action is.
- **Disabled-Send color reads as a second green.** The disabled Send is muted green `#5A7660` (not a neutral gray), which can read as "ready." A clearly neutral disabled state would better honor "ONE primary action, dominant."
- **Brand discipline drift on color-as-meaning.** Several states lean on tint alone (preview green wash, amber strips) with text that's sometimes low-contrast gray-on-tint (`#98A0AE`, `#AEB6C4`, `#7A8395`) — risk of < 4.5:1.
- **The mapper toolbar is busy.** "Map this order", mapped chip, saving status, AI-unavailable, Show/Hide connections, required warning, Customize output layout, Fill from catalog, Save mappings, Send — a lot competes; the single dominant action (Send) is duplicated between header and toolbar in some host configs.
- **Output paths still leak machine names as the secondary line** (`cbc:ID`, `BEG03`, `OrderRequestHeader@orderID`) — correctly demoted under the human label, but the mono second line is dense; for a coordinator it could be hidden behind a "show standards" disclosure.
- **Preview pane prominence is calibrated by magic numbers** (12px / specific heights) and uses its own navy code surface — good signature, but its 52px header + 46px format bar + info/error strips stack tightly with no spacing token.
- **Focus rings are partial.** Many controls are raw `<button>` with inline hover only; focus-visible is applied in places (drawer close) but not uniformly across the dense row chips (22px ƒx/= value chips are below 44px and have no visible focus ring).

### Redesign recommendations (for Claude Design)
1. **Tokenize the whole screen onto the 4/8 scale + one type scale.** Replace the fractional inline px (font sizes, paddings, gaps) with CSS variables; lead hierarchy by size+weight (heading 600 / label 500 / body 400), not color. This is the single biggest polish win given how inline-heavy the file is. (Bar 1, 2)
2. **Unify every pill into ONE badge component** — one shape/size/padding, green/amber/red/neutral semantics, always icon-or-word: status badge, invoice/dead-letter, output row status tags, AI chip, catalog/validation, readiness chips. (Bar 4)
3. **Make Send unmistakably the one primary action.** Keep green ≥44px in the header; make the disabled state neutral gray (not muted green); demote the in-toolbar Send to avoid duplication; keep secondaries (Details, Save mappings, Customize layout, Fill from catalog) as outline/ghost. (Bar 7)
4. **Converge the "fix a line" path.** Decide one canonical resolution surface (the inline OutgoingPane AI-fix strip vs the top IssuesPanel) or make the IssuesPanel a summary-that-jumps and the row the editor — eliminate the sense of two competing places. (Bar 6 honesty, Bar 5 density)
5. **Standardize Lucide icons** for arrows/marks/grips; keep the blue grip / green port semantics but as consistent iconography; ensure every icon-only control has an aria-label. (Bar 9)
6. **Lift contrast on all tinted/secondary text** to ≥4.5:1 (audit `#98A0AE`, `#AEB6C4`, `#7A8395`, `#566982` on tints), so hierarchy survives without relying on faint gray. (Bar 2)
7. **One overlay elevation system.** The screen has popovers (`SourcePickerChip`, `TransformPopover`, Add-field), a drawer, a full-screen designer, and a modal — give them one radius, one border color, and two shadow tiers (popover vs modal/drawer). Animate from trigger; all already have Esc/scrim/close — keep that, make it consistent. (Bar 8)
8. **Make the dense row chips touch-safe + focusable.** The 20–22px "= value" / "Edit value" / port chips need ≥44px hit areas (padding can stay tight visually with a larger hit box) and a visible focus-visible ring + pressed state. (Bar 9)
9. **Standards visibility as disclosure, not default density.** Keep the human field name as the lead; move the `cbc:ID`/`BEG03` machine path behind an info-icon popover or a per-pane "Show standards" toggle so coordinators see plain language and experts can reveal mappings. (CLAUDE.md standards-visibility rule, Bar 2)
10. **Keep the navy + violet brand and the signature blue→green "received → supplier" gradient,** the navy preview code body, and green=output/red=blocking/amber=warning — this is a polish pass, so preserve the visual canon while fixing the rhythm, badges, contrast, and focus. Confirm mobile stays STACKED (MobileTriage) rather than a broken canvas. (Bar 10)

---

### Screenshots — PRODUCTION (15)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/03-base-desktop-1440-order-review-mapper.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/03-base-hd-1920-order-review-mapper.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/03-base-mobile-390-order-review-mapper.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/03-base-tablet-768-order-review-mapper.png)

**add-output-field-combobox · desktop-1440**

![add-output-field-combobox · desktop-1440](screenshots-prod/03-add-output-field-combobox-desktop-1440-order-review-mapper.png)

**delivered-order-state · desktop-1440**

![delivered-order-state · desktop-1440](screenshots-prod/03-delivered-order-state-desktop-1440-order-review-mapper.png)

**issues-panel-with-unresolved-lines · desktop-1440**

![issues-panel-with-unresolved-lines · desktop-1440](screenshots-prod/03-issues-panel-with-unresolved-lines-desktop-1440-order-review-mapper.png)

**mobile-triage-review-and-send · mobile-390**

![mobile-triage-review-and-send · mobile-390](screenshots-prod/03-mobile-triage-review-and-send-mobile-390-order-review-mapper.png)

**order-details-drawer-audit-trail · desktop-1440**

![order-details-drawer-audit-trail · desktop-1440](screenshots-prod/03-order-details-drawer-audit-trail-desktop-1440-order-review-mapper.png)

**order-details-drawer-standards-check · desktop-1440**

![order-details-drawer-standards-check · desktop-1440](screenshots-prod/03-order-details-drawer-standards-check-desktop-1440-order-review-mapper.png)

**output-structure-designer · desktop-1440**

![output-structure-designer · desktop-1440](screenshots-prod/03-output-structure-designer-desktop-1440-order-review-mapper.png)

**ready-to-send-clean-order · desktop-1440**

![ready-to-send-clean-order · desktop-1440](screenshots-prod/03-ready-to-send-clean-order-desktop-1440-order-review-mapper.png)

**send-confirmation-modal · desktop-1440**

![send-confirmation-modal · desktop-1440](screenshots-prod/03-send-confirmation-modal-desktop-1440-order-review-mapper.png)

**source-picker-dropdown-open · desktop-1440**

![source-picker-dropdown-open · desktop-1440](screenshots-prod/03-source-picker-dropdown-open-desktop-1440-order-review-mapper.png)

**transform-edit-value-popover · desktop-1440**

![transform-edit-value-popover · desktop-1440](screenshots-prod/03-transform-edit-value-popover-desktop-1440-order-review-mapper.png)

---

## 04. Upload an order — `/upload`

- **File:** `src/app/(app)/upload/page.tsx` (thin wrapper) → renders `UploadWorkbench`
- **Key components:**
  - `src/components/bridge/UploadWorkbench.tsx` (the entire page; ~1,737 lines — dropzone, supplier picker, plan usage, recent uploads, all overlays, batch logic)
  - `src/components/bridge/FileChip.tsx` (format tag: PDF / XLSX / CSV / cXML / EDI / JSON)
  - `src/components/bridge/layout/PageShell.tsx` + `src/components/bridge/layout/PageHeader.tsx` (canonical page chrome)
  - `src/components/bridge/BridgeLoader.tsx` → `BridgePageLoader` (route `loading.tsx`)
  - Local sub-components defined **inside** `UploadWorkbench.tsx`: `XCard`, `StepBadge`, `StepHeading`, `InfoDisclosure`, `UsageLine`
  - Hooks: `useOrderDirection`, `useQueriesEnabled`, `useOnboardingStatus`, `useSampleOrder`
- **Capture URL (mock):** `/upload` (no route params; mock mode renders suppliers `FastParts Inc / ElectroSupply Co / GlobalComponents / PrecisionMfg`, a Pilot billing block 5/20 orders · 1/1 supplier, and 3 `DEMO_RECENT` rows)

### What it is & why it exists
This is the **intake / ingest** entry point of the parse → normalize → validate → review → transform → deliver → learn loop. The coordinator drops a buyer-side purchase order file (any messy shape) and points it at the supplier it must go to; ProcuLink then parses + normalizes it and hands the user off to the Order Workshop (`/inbox/{id}`) to review and ultimately deliver. It exists because procurement teams receive/produce POs in inconsistent formats (CSV, XLSX, PDF, cXML/UBL/Peppol XML, EDIFACT/X12) and need a single reliable "put the file in here" surface that doesn't lie about what it accepts.

### Who uses it & the primary job
**Persona:** procurement coordinator (the buyer who approves a €399 plan without a committee). **Single most important task:** pick a supplier, drop one (or several) order files, and press the green primary CTA to send them for review — landing in `/inbox/{id}`.

### Layout & structure (current)
Top-to-bottom on the grey page canvas (`PageShell` narrow variant, `max-w` 1040px content column; the intake card is additionally wrapped to `max-w-[1040px]`):

1. **`PageHeader`** — H1 "Upload an order" (28–30px, Bricolage Grotesque, 600) + muted sub "Three steps: choose who it goes to, add the file(s), send for review. We parse and normalize any shape."
2. **Conditional Pilot "Book a 15-min demo" banner** — only when `billing.plan === "pilot"` AND `NEXT_PUBLIC_BOOK_DEMO_URL` is set. Grey card, green left-border accent, navy CTA button (opens external URL in new tab).
3. **First-run sample card** (only when `isEmptyOrg`: live mode, 0 suppliers, 0 recent) — rendered ABOVE the intake card; otherwise rendered BELOW it (see step 6).
4. **The stepped intake card** — the centerpiece. White card, radius 12, 1px `#E5E8EE` border, with a 3px top "Bridge edge" gradient (`#1E66C9` blue → `#2E8E3A` green). Inside is a **2-column grid** `grid-cols-1 lg:grid-cols-[minmax(0,340px)_minmax(0,1fr)]`:
   - **LEFT aside (`#FAFBFD`, 340px) — Step ① Choose a supplier:** numbered green `StepBadge` "1" + heading "Choose a supplier" / hint "Where these orders are sent". A native `<select>` (48px, 1.5px border that turns green `#1E6D29` once a supplier is chosen, custom chevron SVG). Below it a white "Routes to → {Supplier}" confirmation chip + a clock-icon note "Buyer fills in automatically once the document is parsed." Then a 1px divider and the **Plan usage** block: "{plan} plan" label + "Ready"/"Processing paused" pill, two `UsageLine` bars (Orders used/limit, Suppliers used/limit, mono tabular figures), and a Pilot end-date line.
   - **RIGHT section — Step ② Add your order file(s):** numbered blue `StepBadge` "2" + heading + hint "Drop one or more — each file becomes its own order". The **dropzone** is a focusable `role="button"` dashed card (1.5px dashed `#CBD0DA`, radius 10, `#FBFCFE` bg) with a 40px blue upload icon, a bold 18px headline ("Drop your order files here" / filename / "{n} files selected"), a 12.5px helper line ("One or more files · CSV, XLSX, PDF, XML (cXML/UBL/Peppol), EDI (EDIFACT/X12) · up to 10 MB each"), a blue **"Browse files"** button (44px), an italic muted caption ("or drop files anywhere in this area"), and (once a single file is picked) the **format-detection pill** + optional PO/line-count line + optional green "We've seen this layout N times before" fingerprint chip. Below the dropzone, a **multi-file batch list** appears when ≥2 files are selected (one row per file with pending → uploading → done/failed status).
   - **FOOTER (full width, white) — Step ③ Send for review:** numbered green `StepBadge` "3" + the **single primary CTA** (full-width green gradient button, 48px min height). Above it sits the `uploadError` banner (429 / supplier-required / upload-failed); below it the honest gating hint ("Choose a supplier in step 1…") and the single-file **pipeline progress** (3 segments: Reading file → Checking format → Preparing review).
5. **Sample card** (established orgs) — rendered here, below the intake card.
6. **Recent uploads card** — only when `recentRows.length > 0`. Header "Recent uploads" + "View all ↗" link to `/inbox`. Desktop: a `<table min-w-[760px]>` (File / Format / Route / Size / Age / Status). Mobile (`lg:hidden`): stacked card buttons.
7. **Tip card** — "✦ AI extraction" (violet label) explaining text-PDF extraction + number reconciliation.

**Spacing/type/density observations:** almost everything is **inline `style={{}}`** with hardcoded hex + non-4/8px values (gap 2.5, `text-[11.5px]`, `text-[12.5px]`, `py-2.5`, radius 6/7/8/10/12 all mixed). Color is hardcoded throughout (`#0B1A2F`, `#5E6779`, `#1E6D29`, `#B43838`, `#B36D14`) rather than CSS tokens, despite tokens existing (`var(--ink)`, `var(--surface)`, `var(--border)` are used only in the mobile recent-row cards). Type sizes drift across 9.5 / 10.5 / 11 / 11.5 / 12 / 12.5 / 13 / 13.5 / 14 / 18 / 28px.

### Data shown
- **Suppliers** — `apiClient.getSuppliers()` → `GET /api/suppliers` (mock: `mockGetSuppliersFn`, 4 hardcoded suppliers). Fields used: `id`, `name`. Drives the `<select>`, "Routes to" chip, `selectedSupplier`.
- **Billing status** — `getBillingStatus()` → `GET /api/billing/status` (mock returns Pilot 5/20 orders, 1/1 supplier). Fields used: `plan`, `canProcessOrders` (→ `isReadOnly`), `ordersThisMonth`, `orderLimit`, `suppliersUsed`, `supplierLimit`, `trialEndsAt`, `isTrialExpired`.
- **Recent orders** — `apiClient.getOrders({ pageSize: 100 })` → `GET /api/orders` (live only; in mock mode the page uses a **hardcoded `DEMO_RECENT` array of 3 rows**, NOT the mock order store). Mapped to `RecentRow`: `id`, `name` (= `poNumber`), `fmt` (from `sourceFormat`), `buyer` (`buyerName`), `supplier` (`supplierName`), `size` ("—" for live), `age` (relative), `status`. Sorted by `createdAt` desc, sliced to 6.
- **Format detection** — `apiClient.detectFormat(file)` → `POST /api/upload/detect-format` (mock: CSV @ 0.92, PO "PO-DETECT-DEMO", 12 lines, seenCount 3). Fields: `format`, `confidence`, `detectedPoNumber`, `estimatedLineCount`, `reasoning[]`, `seenCount`.
- **Onboarding status** — `useOnboardingStatus()` (`["onboarding-status"]`); only `hasUpload` is read, to fire the `first_upload_started` analytics event.
- **Direction labels** — `useOrderDirection()`: swaps "Supplier"→"Customer" display text for inbound orgs (default outbound).

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| Supplier `<select>` (`#upload-supplier`) | `onChange` sets `supplierId` | Enables CTA; updates "Routes to" chip |
| "Add a {supplier}" link (no-suppliers empty state) | Navigate | `/library/suppliers` |
| Dropzone card (`role="button"`) | Click / Enter / Space | Opens native file picker (`fileInputRef.click()`) |
| Dropzone | `onDragOver` / `onDragLeave` / `onDrop` | Sets `dragging`/`dragReject` visual; drop calls `acceptFiles()` |
| Hidden `<input type=file multiple>` | `onChange` | `acceptFiles()` validates extensions, sets `selectedFile`+`extraFiles`, triggers detection |
| "Browse files" / "Change file(s)" button | Click (stops propagation) | Opens native file picker |
| "i" InfoDisclosure (detection reasoning) | Click toggle | Opens inline popover with `reasoning` joined |
| "i" InfoDisclosure (fingerprint) | Click toggle | Opens inline popover explaining layout recognition |
| Primary CTA ("↑ Upload & review" / "↑ Upload N files" / "Choose a file to upload" / "Processing paused") | Click → `handleUpload()` or `handleBatchUpload()` | Single: upload → animate pipeline → `router.push('/inbox/{id}')`; multi: sequential uploads, stays on page; no file: opens picker |
| Per-file "Reading document… · Open →" (batch row, done) | Click → `openOrder()` | `router.push('/inbox/{orderId}')` |
| "View all in inbox ↗" (batch success) | Navigate | `/inbox` |
| "Try with a sample order →" (sample card) | Click → `sample.runSample()` | `POST /api/onboarding/sample-order` → `router.push('/inbox/{id}?sample=1')` |
| "Book a 15-min demo →" (Pilot banner) | External link (new tab) | `NEXT_PUBLIC_BOOK_DEMO_URL` |
| Recent uploads row (desktop `<tr>` / mobile card button) | Click → `openOrder()` | `router.push('/inbox/{id}')` |
| "View all ↗" (recent card header) | Navigate | `/inbox` |
| uploadError banner CTA (contextual) | Link | `/library/suppliers` (supplier_required) · `/settings` (upload_failed) · `/settings?tab=billing` (limits) |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Native OS file picker | Browser file dialog | Dropzone click/Enter/Space, "Browse files"/"Change file(s)" button, or CTA when no file selected | OS file chooser (filtered by `accept=".csv,.xlsx,.pdf,.xml,.cxml,.edi,.x12,.txt"`, `multiple`) | OS dialog Cancel / file chosen (fires `onChange`) |
| Format-detection reasoning popover | Inline popover (`role="tooltip"`, `InfoDisclosure`) | Click the "i" circle next to the "Detected: …" pill | Detection `reasoning[]` joined with " · " | Click "i" again, outside-pointerdown, or **Escape** |
| Fingerprint explanation popover | Inline popover (`InfoDisclosure`) | Click the "i" inside the green "We've seen this layout N times before" chip | Static copy explaining org-scoped layout recognition | Click "i" again, outside-pointerdown, or **Escape** |
| Upload-error banner | Inline alert panel (`role="alert"`, footer) | Set by `handleUpload`/`handleBatchUpload` on 429 / missing supplier / upload failure / read-only | Title + message + contextual CTA link | Cleared on next upload attempt or successful file (no manual dismiss / no X) |
| Inline file-type error | Inline `role="alert"` text under dropzone | `acceptFile`/`acceptFiles` when an unsupported extension is dropped/picked | "{name} isn't a supported file type. We accept …" | Cleared when a valid file is picked / selection reset |
| Sample-error inline text | Inline text in sample card | `sample.error` after a failed sample POST | Error message | Cleared on next `runSample()` |
| Format-detection pill / batch list / pipeline progress | Inline transient panels (not overlays) | Selecting file(s) / uploading | Detection result, per-file status, 3-stage progress | State change (file cleared, upload completes, navigation) |

**Note:** This page has **NO modal, drawer, dialog, sheet, or toast.** Every transient surface is an inline panel or the native file picker. There is **no "more ways to receive" (email / API / SFTP) disclosure** here despite the focus hint — those channels live in Settings/Connectors, not on `/upload`. The only true overlays are the two `InfoDisclosure` "i" popovers (which DO support Escape + outside-click). All errors are inline; none auto-dismiss or have a close affordance.

### States
- **Empty (first-run org, `isEmptyOrg`):** the sample card is promoted ABOVE the intake card; the supplier `<select>` is replaced by a dashed "No suppliers yet. Add a supplier to send orders to." panel with a link to `/library/suppliers`; recent-uploads card is hidden. CTA stays disabled (no supplier).
- **Loading:** route-level `loading.tsx` → `BridgePageLoader` (the animated buyer→supplier "wire" mark, label "Loading upload workbench…", reduced-motion safe). Within the card: "Loading suppliers…" inline row, "Checking plan limits…" inline row, "Detecting format…" pill.
- **Error:** suppliers fetch error → amber inline panel "Could not load suppliers. Check the API connection and try again." Billing error → amber "Plan status is unavailable…" panel. Upload/429/supplier-required → amber footer banner with CTA. Unsupported file → red inline alert. Detection failure is **silently swallowed** (hint only).
- **Success/feedback:** single-file → 3-stage pipeline animation then auto-redirect to `/inbox/{id}`. Multi-file → per-row "Reading document… · Open →" links + "Orders created…" summary + "View all in inbox ↗". Read-only (expired Pilot) → CTA reads "Processing paused", usage block turns amber. No toast system is used.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** intake card capped at 1040px, centered (large empty gutters on HD). 2-col grid (340px supplier rail + fluid dropzone). Recent uploads = full table.
- **Tablet 768:** still desktop 2-col grid (the `lg:` breakpoint is 1024px, so 768 collapses to **single column** — supplier rail stacks above dropzone). Recent uploads = mobile stacked cards (table is `lg:block`).
- **Mobile 390:** fully stacked. Supplier rail → dropzone → footer CTA all single-column. Dropzone padding shrinks (`px-6 py-10`). Sample card stacks button full-width. Recent uploads = stacked card buttons (route shown as buyer → gradient bar → supplier).
- **Known cliffs:** the supplier rail is fixed 340px until 1024px, so the **768–1023px** range is a single narrow column (rail full-width) — acceptable but the dropzone loses its side-by-side context exactly in the tablet band. No drag-canvas here, so no mapper breakage.

### Current UX issues
- **Token bypass / two systems:** the whole card is hardcoded hex inline styles while CSS tokens exist; only the mobile recent rows use `var(--surface/border/shadow-card)`. Violates the single-source design system and makes dark-mode/brand changes impossible without touching this file.
- **Spacing rhythm drift (Bar 1):** mixed radii (6/7/8/10/12), non-4/8 gaps (`gap-2.5`, `py-2.5`), font sizes spanning 9.5–28px with half-pixel values (`11.5`, `12.5`, `13.5`). No single scale.
- **Status-pill inconsistency (Bar 4):** at least three different pill systems — `STATUS_PILL` map (recent uploads, 6 colors), the usage "Ready"/"Processing paused" pill, the detection confidence dot, the fingerprint chip, and the batch-row text statuses. None share shape/size/padding; some are color-only-ish.
- **Number alignment (Bar 3):** PO numbers, KB sizes, and ages in the recent table are not consistently tabular (only `UsageLine` and the detection PO line use mono); the recent table "Size" is always "—" in live mode (dead column).
- **Empty/dead column:** recent-uploads "Size" column shows "—" for every real order (size isn't tracked server-side) — an always-empty column.
- **No primary-action dominance contract (Bar 7):** there are effectively two strong CTAs competing — the blue "Browse files" inside the dropzone and the green footer CTA — plus the navy sample button; a first-timer can't tell which is "the" action.
- **Errors never dismiss (Bar 6):** the footer `uploadError` banner and inline file error have no X/close; they linger until the next attempt. No toast layer at all.
- **Detection failure is invisible:** if `/upload/detect-format` fails it's swallowed silently — the user gets no "format unknown" signal, only the absence of a pill.
- **Decorative sparkle copy:** the tip card uses "✦ AI extraction" — the design bar elsewhere in the repo flags decorative sparkle copy.
- **Icon-only "i" buttons** are 16px (below the 44px target) though they are keyboard/aria-labelled.
- **Mock vs real divergence:** recent uploads in mock mode show a hardcoded `DEMO_RECENT` set unrelated to the mock order store — confusing for design QA.

### Redesign recommendations (for Claude Design)
1. **Re-token the entire card** — replace every inline hex with the navy/violet token set (`--ink`, `--ink-muted`, `--border`, `--surface`, `--brand-green-deep`, `--brand-blue`, status tokens). Keep the locked Bridge-edge gradient and step colors (supplier-green / buyer-blue) but source them from tokens. This is the highest-leverage change.
2. **Collapse to one spacing + type scale (Bars 1–2):** snap all padding/gap/radius to the 4/8 scale and one radius tier (cards 12, controls 8, pills full); flatten font sizes to the documented heading-600 / label-500 / body-400 scale (kill the 9.5/11.5/13.5 half-steps).
3. **Unify the status-badge system (Bar 4):** one pill shape/size/padding with green/amber/red/neutral + icon-or-word for recent-upload status, usage "Ready/Paused", and batch-row results. Render the detection confidence as that same pill (e.g. green "High confidence", amber "Likely", neutral "Unknown") instead of a bare colored dot.
4. **Establish one dominant primary (Bar 7):** make the green footer "Upload & review" the sole green primary (≥44px, dominant). Demote "Browse files" to an outline/ghost secondary and the sample button to a ghost link-style — so the eye lands on one action.
5. **Add a real toast/inline-confirm layer + dismissible errors (Bar 6):** give the footer error banner and file-type error an X and Escape close; show a success toast on multi-file batch completion instead of only inline text. Surface detection failure as an honest neutral "Couldn't detect the format — we'll still try" pill rather than silence.
6. **Tabular figures everywhere (Bar 3):** PO#, KB, age, counts, usage all `font-feature-settings: tnum` / mono so the recent table and usage bars don't jitter.
7. **Fix the recent-uploads table density (Bar 5):** drop the always-empty "Size" column (or track size), give one row height + low-contrast gridlines, sticky header, and an `aria-sort` affordance; keep the buyer→supplier route cell.
8. **Tablet band (768–1023):** consider dropping the 2-col breakpoint to `md` (768) so the supplier rail + dropzone stay side-by-side on tablet, or explicitly design the stacked tablet order.
9. **De-decorate the tip card:** replace "✦ AI extraction" with a Lucide icon + plain "AI extraction" label; keep the honest reconciliation copy.
10. **Consider surfacing the other intake channels** (email / hosted inbound / API / SFTP) as a genuine "More ways to send orders" disclosure linking to Settings/Connectors — today `/upload` implies file-drop is the only path, which under-sells real capabilities (offer ⇔ works in the other direction).

---

### Screenshots — PRODUCTION (8)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/04-base-desktop-1440-upload.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/04-base-hd-1920-upload.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/04-base-mobile-390-upload.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/04-base-tablet-768-upload.png)

**detection-reasoning-popover · desktop-1440**

![detection-reasoning-popover · desktop-1440](screenshots-prod/04-detection-reasoning-popover-desktop-1440-upload.png)

**recent-uploads-mobile-cards · mobile-390**

![recent-uploads-mobile-cards · mobile-390](screenshots-prod/04-recent-uploads-mobile-cards-mobile-390-upload.png)

**recent-uploads-table · desktop-1440**

![recent-uploads-table · desktop-1440](screenshots-prod/04-recent-uploads-table-desktop-1440-upload.png)

**tip-card · desktop-1440**

![tip-card · desktop-1440](screenshots-prod/04-tip-card-desktop-1440-upload.png)

---

## 05. Suppliers (directory list) — `/library/suppliers`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/suppliers/page.tsx` (a 2-line wrapper that renders `<SupplierDockList />`)
- **Key components:**
  - `src/components/bridge/SupplierDockList.tsx` (the entire page — list, add-supplier inline panel, billing banners, loading/empty/error, mobile cards; also defines the sub-components `SupplierTableHeader`, `SupplierTableRow`, `SupplierMobileCard`, `AutoProcessPill`, `NotSetPill`, `CellValue`, `MobileStat`, `SupplierGlyph`)
  - `src/components/bridge/layout/PageShell.tsx` (page canvas + max-width container, `variant="wide"` → 1480px)
  - `src/components/bridge/layout/PageHeader.tsx` (canonical title row + actions slot)
  - Hooks: `src/hooks/useOrderDirection.ts` (Supplier↔Customer label swap), `src/hooks/useQueriesEnabled.ts` (mock || QA-bypass || signed-in gate)
  - Data: `apiClient.getSuppliers` / `apiClient.createSupplier` (`src/lib/api-client.ts`), `listConnections` (`src/lib/api-client.ts`), `getBillingStatus` (re-exported from `src/lib/api/billing.ts`), `getDeliveryConfig` (`src/lib/api/delivery.ts`)
- **Capture URL (mock):** `/library/suppliers`

### What it is & why it exists
This is the buyer's **supplier directory** — the roster of every counterparty the org delivers purchase orders to. It sits in the **learn / configure** band of the workflow (not the per-order parse→deliver run): each row is one supplier whose versioned integration (input mapping, output format, delivery channel, auto-process flag) is set up once and then reused for every order routed to them. A procurement coordinator opens it to see at a glance which suppliers are wired up (Format + Channel set) versus still "Not set", to drill into a supplier to configure it, to reach a supplier's version history, or to add a new supplier before uploading their first PO.

### Who uses it & the primary job
**Persona:** procurement coordinator (the buyer who owns supplier setup), occasionally the integration expert who configures the delivery details. **Primary job:** scan the directory, then either **add a new supplier** (the one primary action) or **open an existing supplier** to configure/inspect its delivery setup. It is a hub/launcher, not a working surface.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width 1480px; gutter ramp 16→24→34px, vertical padding 20→28px; page canvas `--bg #F6F7FA`):

1. **PageHeader** — `h1` "Suppliers" (Bricolage Grotesque display, 28→30px, weight 600, `-0.02em`, ink `#0B1A2F`) + a muted 13px subtitle: `Your suppliers directory — each one's versioned integration lives in Connections. {n} active supplier(s).` (subtitle reads "Loading…" while fetching). Right side: the single **primary action button**.
2. **Primary action** — a blue (`#1E66C9`) pill button, height 34px, 12.5px semibold, plus-icon + "New supplier". When the billing supplier limit is hit it degrades to a disabled neutral (`#F1F3F7`) pill reading "Supplier limit reached" (no plus icon).
3. **Billing limit banner** (conditional) — amber card (`#FFF8EA` fill, `#F0D39A` border, 3px left rule `#B36D14`) shown when `billing.canAddSupplier === false`: "Your {plan} plan includes {limit} suppliers." + helper line + an outline "View billing" button → `/settings`.
4. **Billing-unavailable notice** (conditional) — amber text card when the billing query errored: "Supplier limits could not be checked because the billing API is unavailable."
5. **Add-supplier inline panel** (conditional, toggled by state) — a white card with a green supplier badge tile, "New supplier" heading, a labelled name input with a green Save button, an info hint, and inline error text. (See "What opens / what closes".)
6. **Body — desktop table card** (`hidden sm:block`) — one white rounded-10 card (`#E5E8EE` border, `0 1px 2px rgba(11,26,47,0.04)` shadow) containing a `<table>` with a `<colgroup>` of fixed widths (name = flex, Format 160, Channel 160, Auto-process 170, History 110, chevron 44). Header row: 10.5px uppercase faint labels (`Supplier / Format / Channel / Auto-process` + two blank cols), 1px bottom border. Rows are 14px-padded, clickable, full-width green hover band (`#E9F1EA`).
7. **Body — mobile card list** (`sm:hidden`) — a `<ul>` of rounded-12 cards, one per supplier, with a label/value `<dl>` grid (Format · Channel, then a full-width Auto-process row), chevron, and a History link sitting *outside* the card button.

**Density/type observations:** the table uses a single ~52px row height (14px vertical padding), name cell pairs a 32px green badge tile + 13.5px semibold name + a 10.5px **monospace derived short code** (e.g. "FastParts Inc" → "FI", "ElectroSupply Co" → "EC"). All colours, spacing, and font sizes are **hard-coded hex + px in inline `style`** (a local const palette: `GREEN #2E8E3A`, `GREEN_DEEP #1E6D29`, `GREEN_SOFT #E9F1EA`, `BLUE #1E66C9`, `INK #0B1A2F`, `BORDER #E5E8EE`, etc.), not Tailwind tokens — so this page does not share the design-system token pipeline.

### Data shown
**Entities:** `Supplier` (`{ id, name }`) plus per-row enrichment from its `DeliveryConfig` and `ConnectionSummary`.

| Field shown | Source |
|---|---|
| Supplier name + derived short code | `apiClient.getSuppliers()` → `GET /api/suppliers` (mock `mockGetSuppliersFn` → `MOCK_SUPPLIERS`: FastParts Inc, ElectroSupply Co, GlobalComponents, PrecisionMfg). Code is computed client-side from the name via `shortCode()`. |
| Format | `config.outputFormat` (uppercased) from `getDeliveryConfig(id)` → `GET /api/suppliers/{id}/delivery-config`. `null`/no-config → faint "—". |
| Channel | `config.protocol` mapped through `PROTOCOL_LABEL` (http→HTTP, sftp→SFTP, ftps→FTPS, smtp→Email, erp_erply→Erply ERP, erp_directo→Directo ERP). No config → "—". |
| Auto-process | `config.autoDeliver` → `AutoProcessPill` (On / Off / Not set). |
| History link | presence of a `ConnectionSummary` for that supplier via `listConnections()` → `GET /api/connections` (mock covers only the **first 2** suppliers, so History shows only on FastParts Inc + ElectroSupply Co). Links to `/library/suppliers/{id}?tab=history`. |
| Billing limit / plan / supplier limit | `getBillingStatus()` → `GET /api/billing/status` (mock returns Pilot, `supplierLimit: 1`, `canAddSupplier: false`). |

**Note (offer↔works):** the comment block in the file documents that the "Orders" and "Acceptance" columns the design once showed were **deliberately dropped** because no per-supplier order/delivery-success aggregate is exposed here — so there is currently **no health/acceptance indicator on this list** despite the focus hint. The only "health-ish" signal is the Auto-process pill, which is a config flag, not a success rate.

**Per-row fetch bound:** only the first `DELIVERY_FETCH_CAP = 50` rows fetch their delivery config (each row owns its own query with a 5-min `staleTime`).

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| "New supplier" header button | `setShowAddPanel(true)` | Opens the inline add-supplier panel. Disabled (→ "Supplier limit reached") when `!canAddSupplier`. |
| "View billing" button (limit banner) | `router.push("/settings")` | Navigates to Settings. |
| Add-panel **name input** | `onChange` updates `newName`; `Enter` calls `handleSave()` | Local state; submits on Enter. |
| Add-panel **Save supplier** button | `createMutation.mutate(name)` → `POST /api/suppliers` | On success: invalidates `["suppliers"]`, closes panel, clears field. On error: shows inline error. Disabled + "Saving…" while pending. |
| Add-panel **X / close** button | `setShowAddPanel(false)` + clears name/error | Closes the panel (aria-label "Close add supplier panel"). |
| **Table row** (whole `<tr>`) | `onClick` → `router.push('/library/suppliers/{id}')` | Opens the supplier detail page. Hover paints the green row band. |
| Row **History ›** link | `next/link` → `/library/suppliers/{id}?tab=history` (calls `e.stopPropagation()`) | Opens the supplier's version-history tab without triggering the row click. Only rendered when a connection exists. |
| Row **chevron** | decorative (inside the clickable row) | No separate action. |
| **Mobile card** (`<button>`) | `onClick` → `router.push('/library/suppliers/{id}')` | Opens supplier detail. |
| Mobile **History ›** link | `next/link` → `?tab=history` | Opens version history (sits outside the card button to avoid nested interactives). |
| Empty-state **New supplier** button | `setShowAddPanel(true)` | Opens the add panel (only when `canAddSupplier`). |

There is **no row context-menu, no row dropdown, no rename/delete affordance, no search, no filter, no sort, and no column controls** on this list.

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Add-supplier panel** | Inline panel (NOT a modal/drawer — it's an in-flow white card above the table; no scrim, no portal) | The "New supplier" header button OR the empty-state "New supplier" button (both `setShowAddPanel(true)`) — **only renders when `showAddPanel && canAddSupplier`** | Green supplier badge tile, "New supplier" heading + helper, a "Supplier name *" labelled input (autofocus), a green "Save supplier" button, a green info hint explaining auto-process, and inline red error text on failure | The X button (`setShowAddPanel(false)`), or a successful save (mutation `onSuccess` closes it). **No Esc handler, no backdrop** (it's not an overlay). Enter in the field submits (does not close on failure). |
| **Billing limit banner** | Inline notice (conditional) | Auto-renders when `billing.canAddSupplier === false` | Plan/limit message + "View billing" button | Not dismissible — disappears only when the condition changes. |
| **Billing-unavailable notice** | Inline notice (conditional) | Auto-renders when the billing query errors | "limits could not be checked…" text | Not dismissible. |
| **Inline add error** | Inline text | `createMutation.onError` | Parsed API error (e.g. "A supplier named 'X' already exists.") | Cleared on next open / successful save. |

**Summary:** this page opens **no true overlays** (no Dialog, Sheet, Popover, Dropdown, Tooltip, or Toast). Every transient surface is an **inline, in-flow panel/notice**, and every navigation (open supplier, open history, view billing) happens **in place via the router**. This is the single most notable structural fact for the redesign: the "add supplier" flow is an inline card, not a modal, and there is no row-action menu at all.

### States
- **Empty:** Handled well. A dashed-border white card centered with a green supplier glyph, "No suppliers configured", a one-line next-action explainer, and (when allowed) a "New supplier" button. Honest and actionable.
- **Loading:** Handled inline (no `loading.tsx` in the route folder). Renders the desktop table card frame with the column header and **4 skeleton rows** (pulsing badge tile + two text bars), `role="status" aria-busy`. Per-row Format/Channel/Auto-process show their own pulsing shimmer while their delivery-config query is in flight (`CellValue` / pill shimmer).
- **Error:** Handled. A red card (`#FEF2F2` / `#F1C9C9`): "Could not load suppliers. Check your connection and try refreshing." — but it gives **no retry button** (relies on a manual page refresh). Billing errors degrade gracefully (optimistically allow add + show the amber notice).
- **Success/feedback:** No toast. Add-supplier success simply closes the panel and the new row appears after `["suppliers"]` invalidation. No "Supplier added" confirmation message.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** identical table layout, centered at the 1480px wide container; on 1920 there is large left/right whitespace (table never exceeds 1480px). Fixed col widths mean wide viewports just pad the gutters.
- **Tablet 768:** still the desktop table (the `sm` breakpoint = 640px, so ≥640px shows the table). The header action row goes horizontal. Fixed Format/Channel/History widths can feel sparse but don't break.
- **Mobile 390 (<640px):** switches to the stacked card list; the table is `hidden`. Each supplier is a rounded-12 card with a 2-col label/value grid + full-width Auto-process row + chevron, and the History link below the card. The "New supplier" button goes full-width (`w-full sm:w-auto`). This is a proper stacked layout, not a shrunk table — good.
- **Known cliff:** none structurally; the main mismatch is that the **add-supplier panel and banners are full-width inline cards** that push the whole list down on every viewport rather than overlaying.

### Current UX issues
- **No status-badge / health system for the actual page job.** The focus hint expects a supplier health/acceptance indicator; there is none. The only pill is "Auto-process: On/Off/Not set" (a config flag). A supplier with a broken/failing delivery looks identical to a healthy one — violates "never show healthy when something is failing" because there is simply no truth signal here.
- **Two competing primary colours.** The page primary action is **blue** ("New supplier") but the in-panel confirm CTA is **green** ("Save supplier"), and green is also the supplier-entity accent and the success colour. This breaks DESIGN BAR #7 (one dominant primary) and the green/blue semantics blur (blue = add, green = save/success/entity/auto-on all at once).
- **Everything is hard-coded inline hex + px, off the token system.** ~15 local colour consts and dozens of literal `px` font/padding values mean this page can't inherit the redesign's spacing rhythm or type scale (DESIGN BAR #1/#2). Sizes drift (10.5/11/11.5/12/12.5/13/13.5/15px) instead of a 4/8 + single type scale.
- **Add-supplier as an inline card, not a modal.** It shifts the entire list when it opens/closes, has **no Esc, no scrim, no focus trap** — inconsistent with a real form-entry surface and with DESIGN BAR (modals/drawers get clear close/Esc/scrim).
- **Short code is invented, not real.** The monospace code under each name is derived from the name (`shortCode()`), not a real supplier code from the API — it reads like meaningful data but isn't, which can mislead a coordinator.
- **No retry on the error state.** "try refreshing" with no button (DESIGN BAR #6 wants reason + retry).
- **No table affordances.** No sort, no `aria-sort`, no search/filter — fine for 3–20 suppliers but the header looks like a sortable table without being one. Gridlines/hover are bespoke rather than the shared list density.
- **Numbers aren't tabular-figured.** The "{n} active suppliers" count and the (absent) numeric columns don't use tabular figures; the derived code uses JetBrains Mono but the rest don't (DESIGN BAR #3).
- **History link discoverability.** "History ›" appears only on suppliers that already have a connection (2 of 4 in mock), so the column looks half-empty and the feature is easy to miss.
- **Hover band only on desktop; rows aren't keyboard-operable as a unit.** The `<tr>` is click-only (no `role="button"`/`tabindex`/Enter), so keyboard users can only reach the inner History link, not "open supplier" (DESIGN BAR #9 focus/interaction).
- **Mock-mode trap for QA.** In mock mode billing returns `canAddSupplier:false`, so the add button is disabled and the add panel can't be opened without overriding billing — worth flagging for the capture plan and for anyone QA-ing the add flow.

### Redesign recommendations (for Claude Design)
1. **Add a real per-supplier status/health badge** (most impactful) — surface a single status pill driven by truthful data: e.g. "Configured · Auto" / "Configured · Manual" / "Not set up" / "Delivery failing". If a last-delivery-failed signal is available, show amber/red; never green when failing. This is the column the page is missing and the reason a coordinator scans the list. Keep it in ONE badge system (one shape/size/padding, icon + word, green/amber/red/neutral) shared with the rest of the app.
2. **Resolve the primary-action colour to one system.** Pick green as the *entity/success* accent and make the single page primary action visually dominant in **one** colour (per the brand, green primary ≥44px); demote the in-panel Save to that same primary, not a second hue. Don't run blue "Add" + green "Save" side by side.
3. **Promote add-supplier to a real modal/drawer** with scrim, Esc-to-close, focus trap, and animate-from-trigger — or, if inline is preferred, make it a compact top-anchored form that doesn't reflow the list and still has Esc + a clear close. Add a success toast ("Supplier added").
4. **Re-base the whole page on design tokens.** Replace the inline hex/px consts with the shared spacing scale (4/8), one type scale (heading 600 / label 500 / body 400), token colours (`--ink`, `--ink-muted`, `--border`), and the canonical card radius/border/shadow tier. This alone fixes the size drift and brings it into the system.
5. **Make rows fully keyboard-operable and consistent with the shared table density** — single row height, `gray-200` gridlines, sticky header, focus-visible ring, `role`/`tabindex` so Enter opens the supplier; give the History action a clearer secondary affordance (ghost link or kebab) rather than a sometimes-present text link.
6. **Replace the invented short code** with a real supplier code/identifier when the API exposes one (or drop it). If kept as a display aid, label it as derived so it's not mistaken for a stored code; render it in tabular/mono consistently.
7. **Add a retry button to the error state** and keep the graceful billing-degrade behaviour.
8. **Add lightweight search/filter once the list can grow** (with `aria-sort` if columns become sortable), and tabular figures for any count/number. For mobile, keep the stacked-card pattern (it's already correct) but unify the card radius/shadow with the desktop card tier.
9. **Reconsider surfacing acceptance/order volume** — if a per-supplier orders/acceptance aggregate becomes available, restore those as honest columns (they were dropped for offer↔works reasons); the health badge in #1 is the conservative first step.

---

### Screenshots — PRODUCTION (7)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/05-base-desktop-1440-suppliers-list.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/05-base-hd-1920-suppliers-list.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/05-base-mobile-390-suppliers-list.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/05-base-tablet-768-suppliers-list.png)

**add-supplier-inline-panel · desktop-1440**

![add-supplier-inline-panel · desktop-1440](screenshots-prod/05-add-supplier-inline-panel-desktop-1440-suppliers-list.png)

**add-supplier-panel-mobile · mobile-390**

![add-supplier-panel-mobile · mobile-390](screenshots-prod/05-add-supplier-panel-mobile-mobile-390-suppliers-list.png)

**add-supplier-panel-validation-error · desktop-1440**

![add-supplier-panel-validation-error · desktop-1440](screenshots-prod/05-add-supplier-panel-validation-error-desktop-1440-suppliers-list.png)

---

## 06. Supplier setup hub (detail) — `/library/suppliers/[id]`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/suppliers/[id]/page.tsx` (thin server wrapper → renders `<SupplierDockProfile id={id} />`)
- **Key components:**
  - `src/components/bridge/SupplierDockProfile.tsx` (the whole page: header + 7 tabs; also hosts `AcceptanceTab`, `LiveMappingsTab`, `CatalogTab`, `CatalogPushCard`, `SupplierRuleBindingsPanel`, `LiveEditNotice`, `SrcChip`, `MiniStatusPill`)
  - `src/components/bridge/PoMappingEditor.tsx` (PO Mapping tab — magic auto-map drag/wire editor; also `TemplatePicker`, `ConfidenceChip`, `ColumnCombobox`, `SourceStatus`, `SaveFeedback`)
  - `src/components/bridge/DeliveryConfigEditor.tsx` (Delivery tab)
  - `src/components/bridge/CatalogSourceEditor.tsx` (auto-sync inside Catalog tab; also `TestReport`)
  - `src/components/bridge/ConnectorRequirementsPanel.tsx` (inside Delivery)
  - `src/components/bridge/StandardsFieldPopover.tsx` + `src/components/bridge/StandardsRefList.tsx` (standards mapping popovers/lists)
  - `src/components/connections/SupplierHistoryTab.tsx` → `HistoryContent` (from `HistoryDrawer.tsx`), `useConnectionRevisions`, `ConnectionNotice` + `ConnectionConfirmDialog` (from `ConnectionLifecycleUI.tsx`)
  - `src/components/bridge/layout/PageShell.tsx`, `src/components/bridge/BridgeLoader.tsx` (route `loading.tsx`)
- **Capture URL (mock):** `/library/suppliers/s1` — **important:** in mock mode (`NEXT_PUBLIC_USE_MOCK=true`) the page renders the rich `DEMO_MOCK` ("Acme Components", code `ACME`, health 97%, 1,284 orders) for **any** id, because the header/Overview branch keys off `isApiMockMode` and not the id. The id `s1` matches `DEMO_MOCK.id`; the prompt's UUID `22222222-2222-2222-2222-222222222222` is a backend test fixture, not a frontend mock — it renders identically in mock mode. Append `?tab=overview|mappings|catalog|po-mapping|delivery|acceptance|history` to land on a specific tab.

### What it is & why it exists
This is the per-supplier (or per-customer, for inbound orgs) **setup hub** — the place a coordinator configures everything ProcuLink needs to turn a buyer PO into the exact file this one supplier accepts and deliver it. It sits at the `transform → deliver → learn` end of the workflow: SKU code mappings (learn), PO column layout (normalize), validation rules (the acceptance gate), the output format + delivery channel + credentials (transform/deliver), the product catalog (grounds AI suggestions), and a version history of every saved config. A coordinator opens it during onboarding for a new supplier and whenever a supplier changes its requirements, an endpoint, or a code.

### Who uses it & the primary job
**Procurement coordinator** (with an integration-leaning mindset for the Delivery/credentials tab). The single most important task: **make this supplier deliverable** — set the required output format + a working delivery channel (Delivery tab) and prove it with a test-fire, so orders for this supplier can be sent automatically.

### Layout & structure (current)
`PageShell variant="wide"` on the grey canvas (`#F6F7FA`), a single full-width column, no max-width card; everything is flat panels.

1. **Back link** — `‹ Suppliers` (or `‹ Customers`), ghost button, muted.
2. **Detail header row** (`flex`, stacks on mobile): 48×48 green-soft avatar tile with a `Truck` glyph · `h1` supplier name at 24–26px Bricolage Grotesque · inline meta row beneath (mono code `ACME` · in mock: a format `SrcChip` "cXML" · a neutral channel chip "HTTP" · a green "Auto-process: ON" pill). Right side: **History** button (outline, only when a versioned connection exists) + **Delete supplier** button (outline, red text + `Trash2`).
3. **Tab strip** — horizontal, 44px tall, bottom hairline, horizontally scrollable with a right-edge fade gradient; 7 tabs: **Overview · Mappings · Catalog · PO Mapping · Delivery · Validation rules · History**. Active tab = ink text + 2px blue (`#1E66C9`) underline; inactive = muted, transparent underline.
4. **Tab body** (`pt-4`, scrolls):
   - **Overview:** a 4-up KPI grid (`grid-cols-2 lg:grid-cols-4`) of `.monument` stat cards (Total orders / Avg cycle time / Exception rate / Acceptance; 30px values) — **all show `—` "no data yet" in real mode**; only mock shows real numbers. Below, a 2-col grid (`lg:grid-cols-2`): "Delivery summary" card (key/value rows: Required format, Delivery channel, Endpoint, Standards profile, Saved SKU mappings, Last delivery — mock only; real mode shows a "Configure this supplier in the Delivery tab" link) and "Recent deliveries" card (PO id · amount · `MiniStatusPill` — mock only; real mode "No deliveries yet").
   - **Mappings:** one card; header (Link2 icon, "Saved SKU mappings", "Add mapping" outline link → `/library/mappings`). Body = a table (Buyer code / Supplier code / Source / Confidence) with mono codes, a source `chip` (AI=violet, Manual=neutral, Inherited=blue, Imported=green) and a `conf` chip; on `<sm` it becomes stacked row-cards. Mock adds a Description column; live (`LiveMappingsTab`) drops Description (no backend field).
   - **Catalog:** title "Product catalog · {total}" + helper · green "Import CSV / XLSX" button + outline "Clear" · notice line · search input (when items exist) · a 5-col table (Code / Name / Unit / Price / Barcode) with a "Showing N of total" footer; plus a `<details>` "Keep the catalog in sync automatically" disclosure wrapping `CatalogSourceEditor` + `CatalogPushCard`.
   - **PO Mapping:** a sub-label ("Order file layout") + `LiveEditNotice` + `PoMappingEditor` (a bordered card with a blue→green top edge, header with "Apply starter template ▾" + `SourceStatus`, an optional amber apply-confirm strip, an optional violet AI banner, a "Connect columns → ProcuLink fields" toolbar with a "Show standards" toggle, then a two-panel wire canvas: left = detected source columns, right = canonical fields each with Accept/Edit/Reject + a standards "i" popover + `ConfidenceChip`; a "How we read the source file" options strip; footer with Delete mapping / required-fields status / "Save mapping").
   - **Delivery:** `LiveEditNotice` + `DeliveryConfigEditor` — a card with an "Auto-deliver" checkbox header, a left 220px protocol radio rail (HTTP/SFTP/FTPS/Email(SMTP)/Erply/Directo) + "How sending works" note, and a right form pane whose fields swap by protocol (output format select, conditional cXML credentials block, endpoint/host/port/timeout, auth section, `ConnectorRequirementsPanel`, a dark JSON config `<pre>` preview), and a footer with Delete / Test-fire / Save delivery.
   - **Validation rules** (tab labelled "Validation rules", code key `acceptance`): `LiveEditNotice` + `AcceptanceTab` — profile header card (ShieldCheck, version+status pill, Activate/Edit/Save buttons), a blue "How validation works" info card, a "Rules" card (read = compact table Scope/Field path/Operator/Value/Severity/Blocks; edit = per-rule form rows with a "+ Add common rule…" select and "Add rule"), then `SupplierRuleBindingsPanel` (read-only active bindings with a "Standards" expander).
   - **History:** when a connection exists, `SupplierHistoryTab` (version-history `Card` with `HistoryContent` — version list + Test/Make live/Restore lifecycle controls + live config + replay); otherwise a dashed "No versions yet" empty panel.

Density/type/spacing observations: heavy use of **inline `style={{}}` with hardcoded hex** (the file declares its own token consts that duplicate `globals.css`); font sizes are a scattered ladder (10px/10.5px/11px/11.5px/12px/12.5px/13px/14px/15px) rather than a clean scale; many borders are `1px solid #E5E8EE`/`#E5E8EE`/`var(--border)` referenced three different ways; the three editor children (PoMapping, Delivery, Catalog source) each re-implement their own card chrome, `Field` label component, and button styles independently, so chrome is close-but-not-identical across tabs.

### Data shown
- **Supplier identity** (`apiClient.getSuppliers()` → find by id): `id`, `name` (code is **derived client-side** via `deriveCode(name)`); metrics are honest `—` placeholders in real mode.
- **Versioned connection** (`listConnections()`): `connectionId` = first connection whose `supplierId === id`; gates the History button + `LiveEditNotice` link + History tab.
- **SKU mappings** (`apiClient.getSupplierMappings(id)`): `id`, `buyerItemCode`, `supplierItemCode`, `confidence` (0–1 → %), `source` (manual/imported/suggested/inherited). No `description` on backend.
- **Catalog** (`getSupplierCatalog(id, q, 200)`): `{ total, items:[{ id, code, name, unit, price, barcode }] }`; mutations `importSupplierCatalog`, `clearSupplierCatalog`. `CatalogPushCard` reads `getOrgSettings()` for the ingress slug.
- **Catalog auto-sync** (`getCatalogSource(id)`): protocol, host/port/url, username, remotePath, syncIntervalHours, fileFormat, isEnabled, `hasPassword`/`hasAuthConfig`/`authMethod`, last-sync status.
- **PO mapping**: `getMappingSourceColumns(id)` (detected columns + format + sample + hint + sourceOrderId), `suggestMappingFields(id)` (AI field suggestions + confidence), `getPoMappingTemplates()` (starter templates), `applyPoMappingTemplate`/`upsertPoMapping`/`deletePoMapping`. Config held in local state, not a query.
- **Delivery** (`getDeliveryConfig(id)`): protocol, autoDeliver, outputFormat, `configJson`, `hasCredentials`, `cxmlCredentials` (+ `hasSharedSecret`); mutations `upsertDeliveryConfig`/`deleteDeliveryConfig`/`testFireDelivery` (`DeliveryTestResult`: success, responseCode, errorMessage).
- **Acceptance** (`getAcceptanceProfile(id)`): `versionNo`, `status` (active/draft), `rules:[{ scope, fieldPath, operator, expectedValue, severity, blockOnFail }]`; `saveAcceptanceProfile`/`activateAcceptanceVersion`. Rule bindings via `getSupplierRuleBindings(id)` (`SupplierRuleBinding` + standards refs on `definition`).
- **History** (`useConnectionRevisions(connectionId)`): revisions list, activeRevisionId, liveSummary, testEvidence, lifecycle mutations (publish/rollback/archive/test).

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| `‹ Suppliers` back link | `router.push("/library/suppliers")` | List page |
| Tab buttons ×7 | `setTab(id)` + scrolls active into view | Switches tab body in place (does not write `?tab=` back to URL) |
| **History** header button | `setTab("history")` | History tab (only shown when `connectionId` exists) |
| **Delete supplier** header button | `setConfirm(true)` | Opens delete-confirm modal |
| Overview "Delivery"/"delivery tab" link (real mode) | `setTab("delivery")` | Delivery tab |
| Mappings "Add mapping" link | `href="/library/mappings"` | Global Mapping Editor |
| Mappings empty-state "Mapping Editor" link | `/library/mappings` | Global Mapping Editor |
| Catalog "Import CSV / XLSX" | opens hidden file input → `importSupplierCatalog` | Imports; shows notice; invalidates caches |
| Catalog "Clear" | `confirm()` then `clearSupplierCatalog` | Native confirm → clears catalog |
| Catalog search input | `setQ` → refetch with query | Filters catalog table |
| Catalog "Keep in sync automatically" `<details>` | native disclosure | Reveals `CatalogSourceEditor` + `CatalogPushCard` |
| Catalog source protocol radios | `selectProtocol` (roving radiogroup) | Swaps host vs URL fields |
| Catalog source Test / Save / Delete | `testFetchCatalogSource` / `upsertCatalogSource` / `deleteCatalogSource` | Inline test report / save notice / delete (native confirm) |
| Catalog push "Copy" | `navigator.clipboard.writeText(url)` | Copies ingress URL; "Copied" 1.8s |
| Catalog push Settings/API links | `/settings?tab=api`, `/help/api-and-integrations` | Navigates |
| PO Mapping "Apply starter template ▾" | `TemplatePicker` open dropdown | Pick template → amber apply-confirm strip |
| PO Mapping per-field Accept / Edit / Reject | `handleAccept` / `handleEditStart` / `handleReject` | Mutates mapping state; wires redraw |
| PO Mapping `ColumnCombobox` | choose/confirm a source column | Sets accepted column |
| PO Mapping "Accept all" (AI banner) | `handleAcceptAll` | Accepts all pending suggestions |
| PO Mapping "Re-detect" / "Re-detect columns" | `sourceQuery.refetch()` | Re-fetches source columns |
| PO Mapping standards "i" button (per field) | shadcn `Popover` | Standards-mapping popover |
| PO Mapping "Show/Hide standards" | `setShowStd` | Reveals standards column |
| PO Mapping separator select / "Has header row" | `setSourceOpts` | Re-reads source file |
| PO Mapping "Save mapping" / "Delete mapping" | `onSave(config)` / `onDelete()` | Persists via `upsertPoMapping`/`deletePoMapping` |
| Delivery protocol radios ×6 | `selectProtocol` (roving radiogroup) | Swaps the form fields |
| Delivery "Auto-deliver" checkbox | `setAutoDeliver` | Marks edited |
| Delivery output-format select | `setOutputFormat` | cXML reveals credentials block |
| Delivery auth-type / SFTP auth-method selects | `setAuthType`/`setSftpAuthMode` | Swaps credential fields |
| Delivery SMTP / OAuth "Advanced" `<details>` | native disclosure | Reveals advanced fields |
| Delivery "Save delivery" | `upsertDeliveryConfig` | Saves; shows post-save test nudge |
| Delivery "Test-fire" (footer + nudge "Send a test now") | `testFireDelivery` | Inline verbatim result strip |
| Delivery "Delete" | `window.confirm` then `deleteDeliveryConfig` | Native confirm → delete |
| Validation "Edit rules"/"Add profile" / "Save rules" / "Cancel" | `startEdit`/`handleSave`/`setEditRules(null)` | Enters/saves/exits edit mode |
| Validation "Activate rules" | `activateAcceptanceVersion` | Activates draft version |
| Validation "+ Add common rule…" select / "Add rule" / "Remove" | `addQuickRule`/`addRule`/`removeRule` | Edits rule list |
| Rule-bindings "Standards" expander | `setOpenId` | Inline standards refs |
| History Test / Make live / Restore / Discard | hook mutations | Lifecycle actions → confirm dialog |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Delete supplier confirm** | Modal (custom, `fixed inset-0 z-50`, scrim `rgba(11,26,47,0.45)`) | "Delete supplier" header button | Title "Delete {name}?", body ("Past orders kept for audit, can't be undone"), inline delete-error box, Cancel + red "Delete supplier" | Backdrop click, Cancel button, or successful delete (→ `router.push("/library/suppliers")`). **No X icon, no Esc handler.** |
| **Apply-template confirm** | Inline panel (amber strip inside PoMappingEditor) | Selecting a template in `TemplatePicker` | "Apply the {name} starter?" + warning copy + inline apply-error, Cancel + "Apply template" | Cancel button, or successful apply. Not a true modal (no scrim/Esc). |
| **Starter-template dropdown** | Dropdown (`role="listbox"`, absolute, z-1000, shadow) | "Apply starter template ▾" button | List of templates (name + description) | Pointer-down outside (document listener), or selecting an item. **No Esc handler.** |
| **Standards-field popover** | Popover (shadcn/ui `Popover`) | per-field "i" button (PO Mapping) | Field label + UBL/EDIFACT/X12/cXML reference rows | Click outside / Esc (Radix default), trigger re-click |
| **Rule-bindings "Maps to"** | Inline expander | "Standards" button on a binding row | `StandardsRefList` panel | Re-click the button (`setOpenId(null)`) |
| **History confirm dialog** | Modal (`ConnectionConfirmDialog`, `role="dialog"` `aria-modal`, scrim `#0B1A2F66`, z-80, bottom-sheet on mobile) | Make live / Restore / Discard in History tab | Title ("Make vN live?" etc), body copy, Cancel + primary/danger confirm (with loading) | Cancel button, or confirm action. **No backdrop-click or Esc close wired here.** |
| **Native browser `confirm()`** ×3 | Browser dialog | Catalog "Clear", Catalog-source Delete, Delivery Delete | OS-native confirm text | OK / Cancel |
| **`ColumnCombobox` listbox** | Inline combobox dropdown | Edit a field's source column | Filterable list of detected columns | Select option / blur |
| **`SourceStatus` redetect popover/menu** | Small inline status control | `SourceStatus` in PO Mapping header | format + column count + re-detect | inline |
| Inline transient notices/strips | Inline status (not overlays) | save/import/test actions across tabs | success/error text; some auto-dismiss (`setTimeout` 1.8–3s) | timeout or next edit |

### States
- **Empty:** Handled well per surface. Mappings ("No saved SKU mappings yet" + Mapping Editor link), Catalog ("No products yet. Import a CSV/XLSX…"), Validation ("No validation rules yet…"), Rule bindings ("No active rule bindings…"), History ("No versions yet" dashed panel), PO Mapping ("No source columns detected. Upload a PO…" + Re-detect). Overview in real mode is effectively a **dead empty state** — four `—` cards plus two "configure/no deliveries yet" panels, so a freshly-created supplier's hero tab carries no signal or next action.
- **Loading:** Route-level `loading.tsx` → `BridgePageLoader` (skeleton). Page-level: real-mode initial load shows a bare centered text "Loading supplier…" (no skeleton). Mappings tab = a proper 3-row pulse skeleton. Catalog/Delivery/Acceptance/Rule-bindings = bare text "Loading …" (no skeleton). PO Mapping = "Detecting columns…" text.
- **Error:** Page-level real-mode error → centered "Failed to load supplier" + "← Back to suppliers". Not-found → "Supplier not found" + back link. Per-tab: Mappings (red sentence), Acceptance ("Failed to load acceptance profile."), Rule-bindings (red text + "↻ Retry" button), Delivery/Catalog-source (red error box), PO Mapping apply-template (inline red). Mostly reason-without-retry except rule-bindings.
- **Success/feedback:** Inline strips, not toasts. Catalog import notice, delivery save→test nudge ("Prove the connection with a test payload" + "Send a test now") and verbatim test-fire result with the honesty caveat ("a successful test means their endpoint answered — it doesn't mean an order was accepted"), acceptance "Rules saved as draft" / "Version activated", catalog-source save notice + connect/preview report.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** full layout; Overview 4-up KPIs + 2-col cards; PO Mapping shows the two-panel **wire/SVG canvas**; Delivery shows the 220px protocol rail + form (`lg:grid-cols-[220px_1fr]`); tables full-width.
- **Tablet 768:** KPIs drop to 2-up; the 2-col Overview cards and the Delivery left-rail/right-form collapse toward stacked (`lg:` breakpoints, so they stack below 1024). Tab strip scrolls horizontally with the right-edge fade.
- **Mobile 390:** Header stacks (avatar/name then actions). Tables → stacked row-cards (Mappings, Acceptance rules). **PO Mapping drops the wire canvas entirely** (`isMobile` → no SVG; columns stack and each field spells out "from: {column} · e.g. {sample}" + a "N of M matched" progress chip) — correct behaviour. Delivery/Catalog editors stack to single column; footers become full-width stacked buttons.
- **Cliffs:** The protocol rails and 2-col Overview cards collapse at `lg` (1024px), so the 768–1023px tablet band gets a wide single-column form (a lot of empty right gutter). The tab strip has 7 tabs and **always** relies on horizontal scroll + a fade rather than wrapping — on narrow desktop the last tabs (Validation rules, History) sit off-screen.

### Current UX issues
- **Two competing card systems / ad-hoc chrome (Bar 8).** SupplierDockProfile, PoMappingEditor, DeliveryConfigEditor and CatalogSourceEditor each declare their own hex tokens and re-implement card borders (`#E5E8EE` vs `var(--border)`), radii (6/7/8/10/12px all appear), buttons, and a private `Field` label component. Radius and border colour drift between adjacent panels in the same tab.
- **Spacing is not on one 4/8 rhythm (Bar 1).** Padding values like `py-3.5`, `px-4 py-3`, `py-2.5`, `8px 12px`, `11px 14px`, `6px 10px` are mixed freely; vertical gaps jump 6/8/10/12/14/16/18px.
- **Type scale sprawl (Bar 2).** At least nine font sizes between 10 and 15px; hierarchy is often carried by colour (muted greys, `var(--ink-faint)`) instead of size+weight, and several muted-on-light label rows (10.5px uppercase `#5E6779`/faint) are at risk below 4.5:1.
- **Numbers aren't consistently tabular (Bar 3).** Overview KPI values, the catalog Price column, confidence %s and counts don't all use `tabular-nums` (only the `conf`/`tabular-nums` chips do), so figures jitter and prices don't right-align cleanly.
- **Status/health pills are not one system (Bar 4).** Mock header uses `.pill pill-ready`; Mappings uses `.chip` source pills; Acceptance uses a custom version pill + coloured severity dots; History uses its own lifecycle pills; the catalog-source last-sync uses a coloured dot. No single badge shape/size/semantics.
- **Tables aren't one density (Bar 5).** Mappings table (`px-5 py-3`) vs Catalog table (`6–7px 10px`) vs Acceptance table (`px-4 py-3`) vs the test-report tables all differ in row height, padding and header styling; no sortable affordance/`aria-sort`; gridlines use several different greys.
- **Overview is a weak/dead hero in real mode (Bar 6).** Four `—` cards and two "nothing yet" panels mean the default landing tab shows no actionable next step; a real coordinator's first impression is empty placeholders.
- **No single dominant primary action (Bar 7).** Each editor tab has its own dark-navy "Save …" plus a Test button plus a Delete button, all ~32–34px and similar weight; the page-level "make this supplier deliverable" goal is never visually elevated. Green is reserved for save inside Catalog but navy elsewhere — inconsistent primary colour.
- **Modal/overlay inconsistency & a11y gaps (Bars 8/9).** The delete modal has no X and no Esc/`role=dialog`; the apply-template "confirm" is an inline strip, not a modal; the History confirm dialog has `aria-modal` but no backdrop/Esc close; the template dropdown has no Esc. Three native `confirm()` calls bypass the design system entirely.
- **Tab strip relies on scroll, not wrap (Bar 10 / nav).** 7 tabs + a fade gradient means deeper tabs (Validation rules, History) are easy to miss; there's no breadcrumb beyond the back link, and `?tab=` isn't reflected back into the URL on manual clicks (so deep state isn't shareable).
- **Loading states are mostly bare text (Bar 6).** Only Mappings has a real skeleton; Delivery/Catalog/Acceptance/page-initial use lone "Loading…" strings.
- **Jargon leakage in Validation read-table.** The read-mode rules table shows raw `fieldPath`/`operator` (e.g. `supplierItemCode` / `greater_than`) even though human labels (`OPERATOR_LABELS`, `FIELD_OPTIONS`) exist for the editor — leads with machine names, not human ones.
- **Mock-vs-real divergence.** Overview/Delivery-summary/Recent-deliveries are richly designed only in mock; real mode is far thinner, so the "designed" version a viewer sees in mock overstates the real page.

### Redesign recommendations (for Claude Design)
1. **Unify all panel chrome into one Card primitive** (one radius, one border `gray-200`, one shadow tier) and delete the four private token blocks + per-file `Field` components; reuse the shared `Card`/`DSPrimitives.Button`. Keep navy `#0B1A2F` + violet brand, green=save/success, red=blocking, amber=warning. (Bars 8, 7)
2. **Make Delivery the clear primary path.** Promote one green ≥44px primary "Save delivery" (and a prominent "Test-fire" secondary), demote Delete to a quiet/destructive-separated ghost; carry the page goal ("make this supplier deliverable") as a header status (e.g. "Not yet deliverable / Deliverable ✓ / Tested ✓"). Honour "200 ≠ acceptance" copy already present. (Bars 7, 9)
3. **Redesign Overview into a real status hub.** Replace the four `—` cards with a setup-progress summary (mapping set? rules active? delivery configured + tested? catalog imported?) each linking to its tab, plus the latest delivery; only show numeric KPIs once data exists. Give it a real skeleton. (Bars 6, 5)
4. **One badge system everywhere** — a single pill shape/size/padding for source provenance, acceptance version/status, severity, delivery health and last-sync, each with green/amber/red/neutral + icon-or-word (never colour alone). Replace severity dots and the dim version pill. (Bar 4)
5. **One table density.** Single row height, cell padding, low-contrast `gray-200` gridlines, sticky header, hover, and `aria-sort` affordances applied to Mappings, Catalog, Acceptance and the test-report tables; `tabular-nums` on every code/qty/price/%/count and right-align money. (Bars 3, 5)
6. **Standardise overlays.** One modal component (scrim, animate-from-trigger, X + Esc + backdrop close, focus trap) for the delete confirm, apply-template confirm (convert the inline amber strip into a real modal), and History lifecycle confirm; one popover style for standards. Replace the three native `confirm()` calls with it. (Bars 8, 9)
7. **Fix the tab strip / nav.** Let tabs wrap or use a responsive overflow menu so Validation rules + History are never hidden; reflect the active tab into `?tab=` for shareable deep links; add a breadcrumb (Library › Suppliers › {name}). (Bar 10, nav)
8. **Normalise spacing + type to the 4/8 scale and a 6-step type ladder**, carrying hierarchy via size+weight; audit all muted-grey labels for ≥4.5:1 contrast. (Bars 1, 2)
9. **Lead Validation with human names** in read mode (use the existing `OPERATOR_LABELS`/`FIELD_OPTIONS` labels), keeping the raw `fieldPath` as secondary mono text — matching the editor and the project's "lead with the human field name" rule.
10. **Real loading skeletons** for Delivery, Catalog, Acceptance and the page-initial load, matching the Mappings skeleton pattern. (Bar 6)
11. **Mobile:** keep the PO-Mapping triage fallback (it's already correct), but ensure the tab strip primary actions and the per-editor Save are reachable without horizontal scroll, and stacked footers stay ≥44px. (Bar 10)

---

### Screenshots — PRODUCTION (18)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/06-base-desktop-1440-supplier-detail.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/06-base-hd-1920-supplier-detail.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/06-base-mobile-390-supplier-detail.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/06-base-tablet-768-supplier-detail.png)

**delete-supplier-modal · desktop-1440**

![delete-supplier-modal · desktop-1440](screenshots-prod/06-delete-supplier-modal-desktop-1440-supplier-detail.png)

**tab-catalog-autosync-expanded · desktop-1440**

![tab-catalog-autosync-expanded · desktop-1440](screenshots-prod/06-tab-catalog-autosync-expanded-desktop-1440-supplier-detail.png)

**tab-catalog · desktop-1440**

![tab-catalog · desktop-1440](screenshots-prod/06-tab-catalog-desktop-1440-supplier-detail.png)

**tab-delivery-cxml-credentials · desktop-1440**

![tab-delivery-cxml-credentials · desktop-1440](screenshots-prod/06-tab-delivery-cxml-credentials-desktop-1440-supplier-detail.png)

**tab-delivery · desktop-1440**

![tab-delivery · desktop-1440](screenshots-prod/06-tab-delivery-desktop-1440-supplier-detail.png)

**tab-delivery-mobile · mobile-390**

![tab-delivery-mobile · mobile-390](screenshots-prod/06-tab-delivery-mobile-mobile-390-supplier-detail.png)

**tab-history · desktop-1440**

![tab-history · desktop-1440](screenshots-prod/06-tab-history-desktop-1440-supplier-detail.png)

**tab-mappings · desktop-1440**

![tab-mappings · desktop-1440](screenshots-prod/06-tab-mappings-desktop-1440-supplier-detail.png)

**tab-po-mapping · desktop-1440**

![tab-po-mapping · desktop-1440](screenshots-prod/06-tab-po-mapping-desktop-1440-supplier-detail.png)

**tab-po-mapping-mobile-triage · mobile-390**

![tab-po-mapping-mobile-triage · mobile-390](screenshots-prod/06-tab-po-mapping-mobile-triage-mobile-390-supplier-detail.png)

**tab-po-mapping-standards-popover · desktop-1440**

![tab-po-mapping-standards-popover · desktop-1440](screenshots-prod/06-tab-po-mapping-standards-popover-desktop-1440-supplier-detail.png)

**tab-po-mapping-template-dropdown · desktop-1440**

![tab-po-mapping-template-dropdown · desktop-1440](screenshots-prod/06-tab-po-mapping-template-dropdown-desktop-1440-supplier-detail.png)

**tab-validation-rules · desktop-1440**

![tab-validation-rules · desktop-1440](screenshots-prod/06-tab-validation-rules-desktop-1440-supplier-detail.png)

**tab-validation-rules-editing · desktop-1440**

![tab-validation-rules-editing · desktop-1440](screenshots-prod/06-tab-validation-rules-editing-desktop-1440-supplier-detail.png)

---

## 07. Connections (Versioned Supplier Connections) — `/connections`

- **File:** `src/app/(app)/connections/page.tsx` (thin Server Component wrapper) → renders `src/components/connections/ConnectionsList.tsx` (the actual `"use client"` view)
- **Key components:**
  - `src/components/connections/ConnectionsList.tsx` — the entire list view
  - `src/components/connections/RevisionStatusBadge.tsx` — the status pill ("Live" / "Draft")
  - `src/components/bridge/layout/PageShell.tsx` — page canvas + max-width container (`variant="wide"`, 1480px)
  - `src/components/bridge/layout/PageHeader.tsx` — title row + subtitle + actions slot
  - `src/components/bridge/layout/Card.tsx` — surface used for the error and empty states
  - `src/components/bridge/EmptyState.tsx` — empty-state body (Mark + title + sub + action)
  - `src/components/bridge/DSPrimitives.tsx` — `Button` primitive used for header action / retry
  - `src/hooks/useQueriesEnabled.ts` — gate that lets the TanStack query run (mock / QA-bypass / signed-in)
  - Data: `listConnections` in `src/lib/api-client.ts` (`mockListConnections` in mock mode, `realListConnections` → `GET /api/connections`); types in `src/lib/api/types.ts` (`ConnectionSummary`).
  - *Not rendered by this page but reached by it:* the detail route `/connections/[connectionId]` renders `ConnectionDetail.tsx`, which (via `useConnectionRevisions.ts` + `ConnectionLifecycleUI.tsx`) owns the row-action verbs (Edit mapping / Make live / Test / Restore) and the make-live confirm dialog. The **list page contains none of those** — see "What opens / what closes".

- **Capture URL (mock):** `/connections` (mock mode seeds two rows from `MOCK_SUPPLIERS.slice(0,2)`; row ids are `conn-11111111-1111-1111-1111-111111111111` = "FastParts Inc" with **Live v1**, and `conn-22222222-2222-2222-2222-222222222222` = "ElectroSupply Co" with **Draft / Not live yet**). Detail capture (if needed) uses those ids, e.g. `/connections/conn-11111111-1111-1111-1111-111111111111`.

### What it is & why it exists
This is the directory of **versioned Supplier Connections** — one row per supplier integration, where a "connection" bundles that supplier's input mapping, output template/format, delivery channel, and item-code mappings into one versioned unit (draft → tested → live → previous). It sits at the **learn/remember** end of the parse→normalize→validate→review→transform→deliver→learn workflow: it's where the reusable, reproducible per-supplier setup lives so every future order delivers the same proven way. A coordinator opens it to see, at a glance, which suppliers are wired up, which version is live, and which still need to be published.

### Who uses it & the primary job
Primary persona: **procurement coordinator** (with the integration expert as the power user behind the detail screen). The single most important task on *this* page is **triage + drill-in**: scan which connections are Live vs still Draft, then click into the one you need to edit, test, or make live. All the actual lifecycle actions happen one level down on the detail page; the list is a launchpad.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width 1480px, gutter 16→24→34px, vertical padding 20→28px, page canvas `var(--bg)`):

1. **PageHeader** — `title="Connections"` (Bricolage Grotesque, 28→30px, weight 600, `var(--ink)`), `sub` = "Each supplier integration — input mapping, output template, delivery and item codes — bundled and versioned" (13px, `var(--ink-muted)`). Right-aligned **actions slot** holds a single secondary `Button` "Manage suppliers" → `/library/suppliers`. Header has `mb-5/mb-6`; on mobile the action wraps below the title.
2. **Body** — one of four mutually-exclusive blocks:
   - **Loading:** a `flex flex-col gap-2.5` stack of **3 pulse skeleton bars**, each `height: 76px`, `border-radius 8px`, background+border `var(--border)`, `animate-pulse`, wrapped in `aria-busy="true" aria-label="Loading connections"`.
   - **Error:** a centered `Card` (the one canonical surface) with a red 13px semibold "Could not load connections", a 12px muted "Check the API connection and try again.", and a secondary **Retry** `Button` calling `refetch()`.
   - **Empty:** a `Card` with `min-h-[360px]` centering an `EmptyState` (Mark glyph, "No connections yet", context sub, and a navy action button "Go to Suppliers").
   - **List:** a bare `<ul>` (`flex flex-col gap-2.5`, no bullets) of **row cards**. Each `<li>` is a single full-width `<Link>` to `/connections/{id}`, styled as a card: `var(--surface)` bg, 1px `var(--border)`, a **3px green left accent edge** (`var(--brand-green)`, the Bridge-Layer signature), `var(--radius-md)`, `padding 14px 16px`, `var(--shadow-card)`, `min-height var(--tap-min)`, `transition box-shadow 120ms`. Inside each row: a flexible **identity column** (left) and a fixed **meta cluster** (right), stacking on mobile (`flex-col` → `sm:flex-row sm:items-center`).
     - Identity: supplier **name** (14px, weight 600, `var(--ink)`, truncated) on line 1; a 12px muted sub on line 2 that reads either "Live version **v{N}** · since {date}" (name + version bolded `var(--ink)`) or italic "Not live yet".
     - Meta cluster: a `RevisionStatusBadge` ("Live" green / "Draft" neutral) + a faint chevron `›` (`var(--ink-faint)`, 16px, `aria-hidden`).
3. **No footer / no sticky action bar.**

Density/spacing/type observations: spacing is mostly on a clean 4/8 rhythm (gap-2.5 = 10px, padding 14×16), but the row uses **mixed pixel literals + Tailwind classes** (inline `padding: "14px 16px"`, `gap-2.5`, inline `min-height`), and the type sizes use odd fractional literals elsewhere in the family (12.5px in Card). Numbers (version, date) are **not** forced to tabular figures.

### Data shown
Entity: **`ConnectionSummary`** (one per supplier in V1). Source: `listConnections()` → mock `mockListConnections` or real `GET /api/connections` (org-scoped server-side via the Clerk JWT). Fields actually rendered:

| Field | Rendered as |
|---|---|
| `name` | Row title (supplier name) |
| `activeVersionNo` (number\|null) | "Live version v{N}" — when null → italic "Not live yet" |
| `updatedAt` (ISO) | "since {localized date}" via `formatDate` (`toLocaleDateString`, "—" on null/invalid) |
| `activeRevisionId` (string\|null) | drives `liveStatus()` → badge "Live" (published) vs "Draft" |
| `id` | row `<Link href>` target |
| `supplierId`, `createdAt` | present in payload, **not displayed** |

The badge string the user reads is humanized: internal `published` → **"Live"** (green), absence of a live revision → **"Draft"** (neutral). `RevisionStatusBadge` also knows `test`→"Tested" (info/blue) and `archived`→"Previous" (neutral) but the list only ever passes `published`/`draft`.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| "Manage suppliers" button (header, secondary) | `router.push("/library/suppliers")` | Navigates to Suppliers library |
| Connection **row** (entire `<Link>`) | click / Enter | Navigates to `/connections/{id}` (detail page) |
| "Go to Suppliers" button (empty-state action) | `router.push("/library/suppliers")` | Navigates to Suppliers library |
| "Retry" button (error state) | `refetch()` | Re-runs the `["connections"]` query |
| Status badge / chevron | none (display only, `aria-hidden` chevron) | — (part of the row link) |

### What opens / what closes
**No overlays — navigates in place.** The Connections **list** page opens **zero** modals, drawers, sheets, dialogs, popovers, dropdowns, tooltips, or toasts. Every actionable element is either a router navigation (row link, header button, empty-state button) or a query refetch (Retry). There is no row-action kebab menu and no make-live confirm on this screen.

The lifecycle overlays the FOCUS HINT mentions (Make live / Restore / Discard confirm dialog, the inline notice banner) live **one level down** on the detail route and are documented there: `ConnectionLifecycleUI.tsx` exports `ConnectionConfirmDialog` (a `role="dialog" aria-modal` centered/bottom-sheet, scrim `#0B1A2F66`, opened by the detail page's per-revision verbs, closed by Cancel / confirm action) and `ConnectionNotice` (a `role="status"` inline banner). They are *not reachable from this page* except by first clicking into a row. (For completeness in the handoff this is the most important fact about the list page: it is a pure launchpad.)

### States
- **Empty:** Handled. `Card` (`min-h-[360px]`) + `EmptyState`: Mark glyph, "No connections yet", and a sub that differs by mode — mock: "Connections appear here once a supplier integration exists." / real: "A connection is created the first time you configure a supplier. Add a supplier and set up its mapping, output and delivery — it becomes a versioned connection you can publish and roll back." Plus a navy "Go to Suppliers" action. This is a real next-action empty state. (Minor: the empty-state action button is a bespoke navy `<button>` inside `EmptyState`, not the shared `Button` primitive.)
- **Loading:** Handled with **skeletons** (3 pulse bars, 76px each), not a bare spinner. There is **no** `loading.tsx` route file — loading is purely the `isLoading` branch inside the component, gated by `useQueriesEnabled()` (so before Clerk is ready the skeleton can persist).
- **Error:** Handled. Red title + muted reason + **Retry** button (`refetch`). Reason is generic ("Check the API connection and try again.") rather than surfacing the actual `ApiHttpError` status.
- **Success/feedback:** None on this page (no toasts/inline confirmations) — feedback only happens after navigating into a row.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Container capped at 1480px and centered; rows are full-width single-column cards (the list is one column at all widths — it never becomes a multi-column grid or a true table). Header title 30px, action right-aligned.
- **Tablet 768 (`sm`):** Rows are `sm:flex-row sm:items-center` (identity left, badge+chevron right). Header still title-left / action-right.
- **Mobile 390:** Header stacks (`flex-col`), action wraps below title. Each row stacks **vertically** (`flex-col gap-3`): name + sub on top, then the badge/chevron meta cluster below — meta cluster uses `flex-wrap`. Tap target honored via `min-height: var(--tap-min)`. No drag/mapper canvas here, so nothing breaks on mobile.
- **Known cliffs:** none structural. The page is intentionally simple; the only responsive nit is that the meta cluster on a narrow row left-aligns under the name (chevron loses its "far right" affordance once stacked).

### Current UX issues
- **Not a table, just a stack of link-cards.** For a directory the user will scan/sort (which are live? when last changed? which supplier?), there's **no column structure, no sortable header, no aria-sort, no count**. Violates DESIGN BAR #5 (one table/list density with sortable affordance). At scale (many suppliers) this becomes hard to scan.
- **No counts / no filters / no search.** The header says "Connections" but never says *how many*, and there's no filter for Live vs Draft. A coordinator with 20 suppliers can't quickly find the un-published ones.
- **Numbers aren't tabular.** Version ("v{N}") and the "since {date}" use the default proportional figures; dates are also locale-`toLocaleDateString` so width/format jitters row-to-row. Violates DESIGN BAR #3.
- **Mixed styling system.** Rows are hand-styled with inline `style` objects (padding, border, accent, shadow, min-height) instead of the shared `Card` primitive the empty/error states use — two different "card" implementations on one screen. Violates DESIGN BAR #8 (consistent cards/elevation). Spacing mixes Tailwind classes and pixel literals (DESIGN BAR #1).
- **Green left-edge on every row reads as "all healthy/success".** The 3px `var(--brand-green)` accent is applied to *every* row including **Draft / Not live yet** ones — green is the success/output color, so a never-published draft visually signals "good to go". Borderline "showing healthy when it isn't." Violates the status-color discipline (DESIGN BAR #4 + the "never show healthy when something is failing" rule).
- **Status conveyed partly by a generic chevron.** The badge is fine, but the only "go here" affordance is a faint `›` glyph; there's no hover elevation feedback defined beyond a `box-shadow` transition with no actual hover shadow value set, so hover state is effectively invisible. Violates DESIGN BAR #9 (visible hover/pressed).
- **The only header action points away from the page.** The sole CTA is "Manage suppliers" (navigates to /library/suppliers) — there is **no primary action to create/add a connection here**, which is confusing given a "connection is created when you configure a supplier." No single dominant primary action (DESIGN BAR #7).
- **Error message is generic.** It never differentiates 401/403/timeout/500 — same copy for all (DESIGN BAR #6, error reason).
- **Empty-state button is a one-off** navy `<button>`, not the `Button` primitive — third button style on the page (header secondary, retry secondary, empty-state navy). Inconsistent primary-action treatment.

### Redesign recommendations (for Claude Design)
Keep navy `#0B1A2F` + violet Bridge-Layer brand; green=live/success, neutral=draft. Ranked:

1. **Promote to a real, scannable table/list (DESIGN BAR #5).** One row height, low-contrast `gray-200` gridlines, sticky header, hover row tint, and an aria-sort sortable header. Columns: **Supplier** (name) · **Status** (badge) · **Live version** · **Last changed** · trailing chevron. Keep it card-stacked on mobile (DESIGN BAR #10) but give desktop true columns so version/date align.
2. **Fix the green-edge semantics (DESIGN BAR #4, "never show healthy when it isn't").** Drive the left accent off status: green only for **Live**, neutral/grey for **Draft / Not live yet**, amber if a draft exists on top of a live version (i.e. "changes waiting to go live"). Never paint an unpublished draft green.
3. **Add count + Live/Draft filter + search (DESIGN BAR #6 affordances).** Header subtitle becomes "{n} connections · {m} live" with tabular figures; a simple segmented filter (All / Live / Draft) and a name search box. This is the highest-value scan affordance for a coordinator with many suppliers.
4. **Unify the surfaces (DESIGN BAR #8).** Render rows with the same `Card` primitive (or one shared row component) used by the empty/error states — one radius, one border color, one shadow tier. Remove the inline-style duplication and snap all spacing to the 4/8 scale (DESIGN BAR #1).
5. **Tabular figures everywhere (DESIGN BAR #3).** Version numbers, dates, counts in `font-variant-numeric: tabular-nums`; format the date consistently (e.g. "12 Jun 2026") rather than raw `toLocaleDateString`.
6. **Give the page a real primary action (DESIGN BAR #7).** Either a dominant green "New connection / Add supplier" primary CTA (>=44px) in the header with "Manage suppliers" demoted to ghost/outline, or — if connections are truly only born from suppliers — make that explicit in copy and keep one clear primary.
7. **Visible hover + focus-visible (DESIGN BAR #9).** Define an actual hover elevation/shadow + background tint and a focus-visible ring on each row link (currently the `box-shadow 120ms` transition has no target shadow). Ensure full row is keyboard-focusable with a clear ring.
8. **Specific error copy + retry (DESIGN BAR #6).** Differentiate auth vs network vs server errors and surface the status; keep the Retry button.
9. **Empty-state polish:** replace the bespoke navy `<button>` with the shared `Button` primitive so the page has exactly one button system, and make "Go to Suppliers" the same visual weight as the chosen primary action.

---

### Screenshots — PRODUCTION (5)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/07-base-desktop-1440-connections-list.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/07-base-hd-1920-connections-list.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/07-base-mobile-390-connections-list.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/07-base-tablet-768-connections-list.png)

**row-link-detail-confirm-make-live · desktop-1440**

![row-link-detail-confirm-make-live · desktop-1440](screenshots-prod/07-row-link-detail-confirm-make-live-desktop-1440-connections-list.png)

---

## 08. Connection detail — `/connections/[connectionId]`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/connections/[connectionId]/page.tsx` (thin server wrapper; awaits `params.connectionId`, renders `<ConnectionDetail>`)
- **Key components:**
  - `src/components/connections/ConnectionDetail.tsx` (page body — orchestrator)
  - `src/components/connections/useConnectionRevisions.ts` (data + every lifecycle mutation, shared with the supplier History tab)
  - `src/components/connections/HistoryDrawer.tsx` (right slide-over: `HistoryDrawer` + the extracted `HistoryContent` / `TestEvidenceSummary` / `ConfigSummary`)
  - `src/components/connections/ConnectionLifecycleUI.tsx` (`ConnectionNotice` inline banner + `ConnectionConfirmDialog` confirm modal)
  - `src/components/connections/RevisionStatusBadge.tsx` (lifecycle pill: Draft / Tested / Live / Previous)
  - `src/components/connections/ReplayPanel.tsx` (replay/impact preview, rendered inside the drawer)
  - `src/components/bridge/mapper/MapperWorkbench.tsx` (`variant="connection"` three-pane mapper, read-only over the live revision with an Edit overlay)
  - Layout chrome: `src/components/bridge/layout/PageShell.tsx` (`variant="wide"`, max-width `--container-wide`), `PageHeader.tsx`, `layout/Card.tsx`, `DSPrimitives.tsx` (`Button`)
- **Capture URL (mock):** `/connections/conn-11111111-1111-1111-1111-111111111111` (the prompt's `/connections/conn-1` is NOT a valid mock id — mock ids are `conn-<supplierGuid>`, seeded from `MOCK_SUPPLIERS.slice(0,2)`. This first one = "FastParts Inc", which has the richest state: a **published v1** (so the live summary populates) **plus a draft v2** in history.)

### What it is & why it exists
This is the single per-supplier "connection" surface: it shows exactly how one supplier's incoming orders are mapped, validated and delivered, and it holds the **version history** behind plain verbs. In the `Parse → Normalize → Validate → Review → Transform → Deliver → Learn` loop it owns the *configuration authored once per supplier* (the mapping/output/delivery/item-codes/acceptance bundle) rather than a single order. A procurement coordinator opens it to (a) see what's live for this supplier today, (b) edit the mapping safely (editing clones the live version into a draft), and (c) test, make live, restore an old version, or replay recent orders to see what would change — without ever touching a live order.

### Who uses it & the primary job
**Procurement coordinator** (with an integration-expert overlap for the mapper). The single most important task: **edit the mapping and then make it live** — the header's one primary button toggles between "Create mapping" / "Edit mapping" (when nothing is live yet vs. live exists), and once a draft is open the "Make live" action moves into the drawer's draft row. The whole revision lifecycle (draft/test/publish/archive/rollback) is deliberately disguised behind plain verbs: Edit mapping · Test · Make live · Restore this version · Discard.

### Layout & structure (current)
`PageShell variant="wide"` (centered, max-width `--container-wide`, gutter `px-4 → sm:px-6 → lg:px-[34px]`, vertical `py-5 → sm:py-7`).

1. **PageHeader** (top): title = `connection.name` (e.g. "FastParts Inc"), sub = "How this supplier's orders are mapped, validated and delivered". Right-aligned `actions` cluster, in order:
   - `← All connections` link (outline, `h-44/sm:32`, surface bg + border).
   - `↺ History & advanced ⌄` quiet outline button (only when `connection` loaded; `aria-haspopup="dialog"`, `aria-expanded`). Opens the right drawer.
   - `Create mapping` / `Edit mapping` — **green primary** `Button size="md"`; only shown when **no draft exists** (when a draft is open it hides to avoid a second-draft footgun).
2. **`ConnectionNotice`** — inline ok/err banner (left border accent green/red) rendered under the header whenever `notice` is set.
3. **Body grid** (`grid gap-4 lg:items-start`, effectively single column stacked — note the loading skeleton uses a 2-col grid but the loaded body is one column):
   - **Card "Live version"** (`edge="green"`, sub "What this supplier receives for new orders today"). When something is live: a row of `RevisionStatusBadge status="published"` (renders as "Live") + mono `v{N}`, then a **BundleSummary** definition list (7 label/value rows, see Data), then a footer with an `Open supplier editors` link (`/library/suppliers/{supplierId}`). When nothing is live: a "Nothing live yet" empty block with the same supplier-editors link.
   - **Card "Mapping"** (no edge). Sub copy is context-aware (read-only: "…Click to edit." vs. editable draft: "…Saved automatically; "Make live" to publish."). Contains the `MapperWorkbench variant="connection"`. When the live (published) revision is shown read-only, a full-bleed **semi-transparent overlay button** ("✎ Edit this mapping / You're viewing the live version. Editing opens a draft you can publish.") sits over the mapper; clicking it fires the create-draft mutation. When there is no revision at all, an empty "No mapping yet" block with a green `Start mapping` button.
4. **Overlays** (conditionally mounted at the end): `ConnectionConfirmDialog` (when `confirm` set) and `HistoryDrawer` (when `historyOpen`).

Spacing is mostly an 8px-ish rhythm but realized with **hardcoded fractional px** values throughout (`text-[12.5px]`, `text-[11.5px]`, `py-2`, `gap-px`, `h-[20px]` chips, `12px 14px` card padding inside the drawer) — it drifts from a strict 4/8 scale.

### Data shown
**Connection** (`ConnectionDetail` type, from `getConnection(connectionId)` → `GET /api/connections/{id}`; mock `mockGetConnection` over `_mockConnections`): `id`, `supplierId`, `name`, `activeRevisionId`, `revisions[]`.

**Active (live) revision bundle** (`getConnectionRevision` → `GET /api/connections/{id}/revisions/{revId}`; mock `_mockRevisionBundle`) drives the **BundleSummary** rows:
| Row label | Source field | Example value |
|---|---|---|
| Input mapping | `inputMappingJson` present? | "Configured" / amber "Default / none" |
| Output template | `outputMappingJson` present? | "Custom template" / "Fixed transformer" |
| Output format | `outputFormat` | "CSV" / "Default" |
| Delivery channel | `deliveryProtocol` + `deliveryAutoDeliver` + `hasCredentials` | "HTTP webhook · credentials set" / amber "Not configured" |
| Item mappings | `itemMappings.length` | "1 code" / amber "0 codes" |
| Acceptance rules | `acceptanceProfileId` + `acceptanceVersionNo` | "Bound · v3" / amber "Not bound" |
| Catalog | `catalogMode` | "Live (read at send time)" |

**Revision history rows** (drawer; `connection.revisions[]` = `ConnectionRevisionSummary`): `versionNo`, `status` (draft/test/published/archived), `publishedAt`, `createdAt`. Timestamps via `toLocaleString`.

**Test evidence** (drawer; from `markConnectionRevisionTest` → `POST .../test`): `passed`, `testedAt`, parsed `TestPackSummary` { `replay` {orderCount, outputErrors…}, `conformance` {skipped, passed, profile, errors, warnings}, `error` }.

**Replay** (drawer; `replayConnectionRevision` → `POST .../revisions/{rev}/replay`): per-order `ReplayOrderDiff` (poNumber, outputFormat, outputChanged, validationChanged, outputError, currentOutput/draftOutput, effectiveValueChanges[], validationFlips[]).

**Sample order for the mapper preview**: `apiClient.getOrders({ supplierId, pageSize: 1 })` → newest order id, fed to MapperWorkbench `previewOrderId`.

### Interactive elements
| Control | Action | Result / where it goes |
|---|---|---|
| `← All connections` link | navigate | `/connections` |
| `↺ History & advanced ⌄` button | `setHistoryOpen(true)` | Opens right HistoryDrawer |
| `Create mapping` / `Edit mapping` (header primary) | `createDraftMutation.mutate()` (clone-from-active) | Creates an editable draft; sets ok notice; button hides (draft now open); mapper becomes editable |
| `Open supplier editors` link (live card) | navigate | `/library/suppliers/{supplierId}` |
| `Open supplier editors` link (empty live card) | navigate | `/library/suppliers/{supplierId}` |
| Mapper read-only **Edit overlay** button | `createDraftMutation.mutate()` | Same as Edit mapping; label flips to "Opening an editable copy…" |
| `Start mapping` button (no-revision empty state) | `createDraftMutation.mutate()` | Creates first draft |
| MapperWorkbench (Mapping card) | drag-wire / inline pickers / inline edits | Authors the draft mapping (read-only when over the live revision) |
| **Drawer:** close `×` | `onClose` | Closes drawer |
| **Drawer row:** `Test` (draft/test rows) | `onTest(id)` → `testMutation` | Runs the test pack; inline `TestEvidenceSummary` appears under the row; ok/err notice |
| **Drawer row:** `Make live` (draft/test rows) | `onRequestPublish(id, v)` | Opens publish ConfirmDialog (disabled + tooltip "Run tests — checks must pass…" until tests pass) |
| **Drawer row:** `Restore this version` (archived rows) | `onRequestRollback(id, v)` | Opens rollback ConfirmDialog |
| **Drawer row:** `Discard` (draft/test rows) | `onRequestArchive(id, v)` | Opens discard ConfirmDialog |
| **Drawer replay:** "Revision to test" `<select>` | `setRevisionId` | Chooses which revision to replay |
| **Drawer replay:** "Recent orders to replay" number input | `setRecentLimit` (clamped 1–50) | Sets replay window |
| **Drawer replay:** `Run replay` / `Run again` | `replay.mutate()` | Runs non-destructive replay; renders summary + per-order diff rows |
| **Drawer replay:** an order diff row | `setOpen(toggle)` (only if it has detail) | Expands output diff / field-change table / validation flips |
| **ConfirmDialog:** `Cancel` | `setConfirm(null)` | Closes dialog |
| **ConfirmDialog:** primary (`Make live` / `Restore` / red `Discard`) | publish/rollback/archive mutation | Performs action, closes dialog, sets notice, invalidates queries |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **History & advanced drawer** | Right slide-over (`role="dialog"` `aria-modal`, `z-70`, scrim `rgba(11,26,47,0.32)`, `width: min(460px,100%)`, slide-in 200ms, focus-trapped, focus-restored to trigger) | `↺ History & advanced` header button | Version-history rows (per-revision lifecycle buttons + inline test evidence), read-only **Live configuration** summary (`ConfigSummary`), and the **Replay & impact preview** panel (`edge="blue"` Card) | `×` close button · **Esc** · click on scrim/backdrop (the panel `stopPropagation`s its own clicks) |
| **Publish confirm dialog** | Centered modal on desktop / **bottom sheet on mobile** (`role="dialog"` `aria-modal` `aria-labelledby`, `z-80`, scrim `#0B1A2F66`, `sm:max-w-[440px]`) | drawer row `Make live` → `onRequestPublish` | "Make v{N} live?" + body about new orders using it + Cancel / green **Make live** | `Cancel` · confirm (success) · (NB: no `×`, no Esc handler, **no backdrop-click close** — see issues) |
| **Restore confirm dialog** | same modal | drawer row `Restore this version` → `onRequestRollback` | "Restore v{N}?" + body + Cancel / green **Restore** | `Cancel` · confirm · (no Esc/backdrop) |
| **Discard confirm dialog** | same modal | drawer row `Discard` → `onRequestArchive` | "Discard v{N}?" + body + Cancel / **red Discard** | `Cancel` · confirm · (no Esc/backdrop) |
| **Mapper "Edit this mapping" overlay** | Inline full-bleed overlay button over the read-only mapper (`absolute inset-0 z-10`, white card, soft shadow) | shown automatically whenever the mapper is read-only (viewing the live revision) | "✎ Edit this mapping" + helper line; disabled→"Opening an editable copy…" while pending | Disappears once a draft exists (mapper becomes editable); no manual close |
| **Inline notice banner** (`ConnectionNotice`) | Inline status banner (not transient — no auto-dismiss) | every mutation success/error + create-draft | one sentence (ok green / err red, left border accent) | Replaced by the next notice or `setNotice(null)` on the next action; no dismiss control |
| **Inline test-evidence block** (`TestEvidenceSummary`) | Inline panel under a drawer revision row | `Test` succeeds for that revision | "Checks passed/failed · {time}" + replay/conformance counts + notes | Stays until another test runs / drawer reopens |
| Mapper-internal overlays (output designer, field pickers, preview popovers) | nested in MapperWorkbench | mapper interactions | (out of scope of this page; note the connection variant does **not** mount the OutputStructureDesigner — that's `variant==="order"` only) | per MapperWorkbench |

There is **no toast system** here — all feedback is the persistent inline `ConnectionNotice` banner.

### States
- **Empty (nothing live):** "Live version" card shows a "Nothing live yet" block + supplier-editors link; "Mapping" card shows "No mapping yet" + green `Start mapping`. Drawer version history shows "No versions yet. Edit the mapping to begin." Replay shows an idle hint, and "No recent orders to replay" if the supplier has no order history. Good honest empties.
- **Loading:** `isLoading` renders a **2-column grid of two 280px pulsing skeleton blocks** (`bg: var(--border)`) — note the *loaded* body is single-column, so the skeleton shape doesn't match the real layout. BundleSummary has its own 6-row pulse skeleton. Replay has a 3×56px pulse skeleton. **There is no route-level `loading.tsx`** in the `[connectionId]` folder (only `page.tsx`), so the first paint relies on the in-component skeleton once the client query starts.
- **Error:** `isError` → a centered Card "Could not load this connection" (danger text) + `Retry` button (`refetch`). `!connection` (404) → centered Card "Connection not found" + "Back to connections". Replay distinguishes 4xx (danger "Replay rejected:") from network/5xx (amber "Connection problem:") with a retry-able message. Mutation errors surface in the red `ConnectionNotice` (e.g. the backend's evidence-gate 409 "Run tests on this revision before publishing.").
- **Success/feedback:** persistent inline `ConnectionNotice` ("Live — new orders for this supplier use this version now.", "Restored — v{N} is live…", "Draft discarded.", "Checks passed…"). Per-row `loading` spinners on Test/Restore/Discard (only the acting row spins, via per-action pending ids).

### Responsive behaviour
- **HD 1920 / Desktop 1440:** `--container-wide` centered; both cards full-width stacked. Controls render at the compact `sm:` heights (`32px`/`27px`). Drawer is a fixed 460px right slide-over. Confirm dialog is a centered 440px modal.
- **Tablet 768:** Largely the same single-column stack. The mapper is desktop-first; below `lg` it falls back to its review/triage rendering. Replay controls move from a row (`sm:flex-row`) but stay readable.
- **Mobile 390:** All buttons grow to `h-44` tap targets (header actions, links, drawer rows). Drawer becomes `width: 100%`. Confirm dialog becomes a **bottom sheet** (`items-end`, `rounded-t-[10px]`, stacked full-width buttons). The MapperWorkbench is **not** a drag canvas on mobile — review/triage. Potential cliff: the live-version card footer and header actions wrap; the read-only **Edit overlay** covers the whole mapper which is fine, but the mapper itself behind it is the desktop-first surface.

### Current UX issues
- **No single dominant primary on the loaded screen.** The header primary ("Edit mapping") *disappears* once a draft is open, and the real "Make live" action is buried inside the drawer on a revision row — the most important action in the whole workflow is two clicks deep and visually identical (`size="sm"`) to Test/Discard. Violates DESIGN BAR #7 (one dominant primary, ≥44px, green).
- **Two parallel "make a draft" affordances** (header button + full-bleed mapper overlay) doing the identical mutation; the giant overlay button competes with the header for the primary slot.
- **Confirm dialogs lack Esc + backdrop-close + an `×`.** Only Cancel closes them — inconsistent with the drawer (which has all three) and a violation of the "modals have a clear close/escape + scrim" rule.
- **Loading skeleton shape mismatches the loaded layout** (2-col skeleton → 1-col body), and there's no route `loading.tsx`, so navigation shows a blank frame before the client skeleton.
- **Status badges are inconsistent in shape/size across the page:** `RevisionStatusBadge` (rounded-full dot pill, h-5/h-6), the amber "unconfigured" summary chips (`rounded-full h-[20px]`), the replay format tag (`rounded-[4px] h-[18px]`), and the replay `Chip` (`rounded-full h-[18px]`) are four different pill systems. Violates DESIGN BAR #4 (one badge system).
- **Type scale drift:** the page is littered with fractional, ad-hoc sizes (`12.5px`, `11.5px`, `10.5px`, `13.5px`) and the drawer/header set sizes via inline `style` (18px h2, 11px labels) instead of a shared scale. Violates DESIGN BAR #2.
- **Numbers are not consistently tabular.** Version numbers use `font-mono` (good), but timestamps, replay counts ("20 orders · 3 output changes"), and item-mapping counts use the proportional body font and will jitter. Violates DESIGN BAR #3.
- **Notice banner never dismisses and has no close control** — it persists and gets stale; a long red 409 message sits above the fold indefinitely.
- **Spacing is not on a strict 4/8 grid** (e.g. `12px 14px` card padding, `gap-px`, `py-2`, `mt-2.5`, `pt-3` mixed) — DESIGN BAR #1 drift.
- **The drawer mixes three different card chromes** (its own `HISTORY_CARD_STYLE` inline cards, the design-system `Card` for ReplayPanel, and bare `<section>`s) — inconsistent elevation/radius/border (DESIGN BAR #8).
- **"History & advanced" is jargon-ish** and the `↺` / `⌄` glyphs are raw Unicode, not Lucide icons (the app standard).
- **Replay output diff `<pre>` columns** can overflow horizontally on small widths with no clear affordance, and the diff has no line numbers / legend beyond colour.
- **Icon-only-ish controls use text glyphs** (`×`, `↺`, `⌄`, `▲/▼`) rather than accessible Lucide icons with consistent sizing.

### Redesign recommendations (for Claude Design)
1. **Promote the real primary action.** Surface "Make live" (and, while a draft exists, "Test") as a single dominant green primary at the top of the page — e.g. a sticky action bar under the header that reflects draft state (Test → Make live), instead of hiding it in a drawer row. Keep navy/violet brand; green primary ≥44px (BAR #7). Demote Test/Discard to outline/ghost next to it.
2. **Collapse the duplicate draft entry points.** Keep ONE "Edit mapping" primary in the header; replace the full-bleed mapper overlay with a slimmer, non-blocking "Viewing live version — Edit" inline bar above the mapper so the live mapping stays visible.
3. **Unify the status/health pills into one badge component** (one shape, height, padding, dot/icon + green/amber/red/neutral) used by `RevisionStatusBadge`, the unconfigured summary chips, and all replay chips/format tags (BAR #4). Use a Lucide icon per state.
4. **Fix the confirm dialogs:** add `×`, Esc, and backdrop-click close to match the drawer; keep the destructive Discard visually separated (red, with the most friction) (modals-have-clear-close rule).
5. **One type scale + tabular numerals.** Replace the `12.5/11.5/10.5/13.5px` zoo with the design-system scale (heading 600 / label 500 / body 400) and apply `font-variant-numeric: tabular-nums` to all version numbers, counts, timestamps, and replay metrics (BAR #2, #3).
6. **Make the loading state match the real layout** (two stacked cards, not a 2-col grid) and add a route-level `loading.tsx` so navigation never shows a blank frame (BAR #6).
7. **Normalize spacing to a strict 4/8 grid** for card padding, gaps, and section rhythm across the page, drawer, and dialogs (BAR #1).
8. **Unify card chrome in the drawer** to the canonical `Card` (one radius/border/shadow tier) instead of three different section styles (BAR #8).
9. **Make the notice banner dismissible** (add a close `×`, optionally auto-dismiss success after a few seconds while keeping errors sticky) and consider a toast for transient successes so the persistent area is reserved for blocking errors (e.g. the publish-gate 409).
10. **Replace text glyphs with Lucide icons** (`History`, `ChevronDown`, `X`, `RotateCcw`, `ChevronUp/Down`) at a consistent size with `aria-label`s on icon-only controls (BAR #9 + aria rule).
11. **Polish the replay diff:** add a small legend (removed/added colours), make the `<pre>` columns horizontally scrollable with a visible affordance, lead field-change rows with the **human field name** (already mostly done — keep it, never raw `cbc:ID`/`BEG03`), and keep the "would start failing" danger band prominent (it's the trust payload).
12. **Lighten the "History & advanced" label** to plain language ("Versions & history") and ensure breadcrumbs/back are consistent (the `← All connections` link is good; add a breadcrumb for depth).

---

### Screenshots — PRODUCTION (9)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/08-base-desktop-1440-connection-detail.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/08-base-hd-1920-connection-detail.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/08-base-mobile-390-connection-detail.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/08-base-tablet-768-connection-detail.png)

**discard-confirm-dialog · desktop-1440**

![discard-confirm-dialog · desktop-1440](screenshots-prod/08-discard-confirm-dialog-desktop-1440-connection-detail.png)

**history-advanced-drawer · desktop-1440**

![history-advanced-drawer · desktop-1440](screenshots-prod/08-history-advanced-drawer-desktop-1440-connection-detail.png)

**history-drawer-mobile · mobile-390**

![history-drawer-mobile · mobile-390](screenshots-prod/08-history-drawer-mobile-mobile-390-connection-detail.png)

**make-live-confirm-dialog · desktop-1440**

![make-live-confirm-dialog · desktop-1440](screenshots-prod/08-make-live-confirm-dialog-desktop-1440-connection-detail.png)

**mapper-edit-overlay · desktop-1440**

![mapper-edit-overlay · desktop-1440](screenshots-prod/08-mapper-edit-overlay-desktop-1440-connection-detail.png)

---

## 09. Drafts — `/drafts`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/drafts/page.tsx`
- **Key components:**
  - `src/components/bridge/layout/PageShell.tsx` (page canvas + centered wide container)
  - `src/components/bridge/layout/PageHeader.tsx` (title row + actions slot)
  - `src/components/bridge/layout/Card.tsx` (empty-state surface wrapper)
  - `src/components/bridge/EmptyState.tsx` (bare Mark + Bricolage title + sub + secondary action)
  - `src/components/bridge/MarkSystem.tsx` / `src/components/bridge/DSPrimitives.tsx` (`MarkSystem`, `ProcuLinkMark`, `Button`)
  - `src/app/globals.css` — `.pill` / `.pill-review` / `.pill-failed` design classes used by the draft rows
- **Capture URL (mock):** `/drafts` (mock mode renders 2 demo rows `d1`, `d2`; real/non-mock mode renders the empty state)

### What it is & why it exists
Drafts is the holding pen for orders a coordinator started but did not finish — an order they saved while still resolving it (mapping SKUs, clearing exceptions, or picking a supplier). It sits between **review** and **transform** in the parse → normalize → validate → review → transform → deliver → learn workflow: an order parked mid-review so it does not get lost. A procurement coordinator opens it to resume an order they couldn't complete in one sitting and jump back into its inbox/review screen.

> **Important reality check for the redesign:** there is **no draft-persistence backend yet**. The page is hardwired to a 2-row `DEMO_DRAFTS` constant that only shows when `NEXT_PUBLIC_USE_MOCK=true`. Real users (mock off) always see the empty state — `const DRAFTS = isApiMockMode ? DEMO_DRAFTS : []`. The rows are demo scaffolding, not live data, and the row click routes to `/inbox/${d.id}` (e.g. `/inbox/d1`), which will 404 against the real API. Treat the list layout as a design target to be wired later, not a working feature.

### Who uses it & the primary job
**Persona:** procurement coordinator (the buyer-side operator who uploads POs and resolves them). **Primary job:** *resume an in-progress order* — find the saved draft and reopen it to finish mapping / clearing exceptions / choosing a supplier before sending. Secondary job: start a new order (`New` button → `/upload`).

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width `--container-wide` = 1480px; gutter ramps 16 → 24 → 34px, vertical padding 20 → 28px; canvas background `--bg`):

1. **PageHeader** — title **“Drafts”** (Bricolage Grotesque, 600, 28→30px, `--ink`) with subtitle **“Orders you've saved to finish later”** (13px, `--ink-muted`). Right-aligned **actions slot** holds one button: **`New`** (blue/green `variant="blue"` Button, size `md`, with an inline 24-viewBox plus-icon SVG). On mobile the header stacks (`flex-col`) and the action wraps below.
2. **Body — conditional on `DRAFTS.length`:**
   - **Empty (real users / mock off):** a single `Card` with `flex items-center justify-center min-h-[360px]` wrapping `EmptyState` — bare ProcuLink Mark (52px, hover opacity 0.7→1, 400ms), Bricolage title **“Drafts live here”**, muted sub explaining what a draft is, and a navy secondary action button **“Go to Inbox”** (→ `/inbox`).
   - **Populated (mock):** a vertical stack (`flex flex-col gap-2.5` = 10px gaps) of **draft rows**. Each row is a hand-rolled clickable `div` (NOT the shared `MobileListRow` — the code notes `MobileListRow` doesn't accept a `style` prop, so it re-implements role/tabIndex/keyboard/press behaviour). Row styling: `--surface` background, `1px --border`, **3px amber left border (`--amber`)**, `--radius-md` (8px), padding `14px 16px`, `--shadow-card`, `min-height: --tap-min` (44px), hover shadow swap to `0 4px 14px rgba(11,26,47,0.08)`.
     - Row internal layout: `flex-col gap-3` on mobile → `sm:flex-row sm:items-center sm:gap-4` on desktop.
       - **Left (identity, `flex-1 min-w-0`):** PO number (12px, mono, 600, `--ink`) then a line `buyer → supplier` — buyer in `--ink` 500, the `→` arrow in `--ink-faint`, supplier in `--brand-green-deep` 500, both names `truncate`.
       - **Right (meta cluster, `flex items-center gap-2 flex-wrap`):** a **stage pill** (`.pill .pill-review`, amber, with dot), an optional **exceptions pill** (`.pill .pill-failed`, red, with dot — only when `issues > 0`), and a saved-at timestamp (11px, `--ink-faint`, `min-width 56px`, right-aligned, rendered as `{savedAt} ago`).

**Density/type/spacing observations:** mostly aligned to the system, but the rows are styled almost entirely via inline `style={{}}` objects with hardcoded pixel values (`14px 16px`, `boxShadow`, `minHeight`) rather than the canonical `Card` / `MobileListRow` primitives. The two pill systems differ from the rest of the app's `UnifiedStatusBadge` (these use raw `.pill` CSS classes). Font sizes mix integers and decimals (12px, 11px, 12.5px in Button).

### Data shown
Single entity: a **Draft** (in-progress purchase order). Source: the local `DEMO_DRAFTS` constant in the page file — **not an API** (no draft endpoint exists). Fields per row:

| Field | Mock value example | Render |
|---|---|---|
| `id` | `d1`, `d2` | row key; used in `/inbox/${id}` nav target |
| `po` | `PO-2026-008422`, `AR-2026-1110` | mono PO number, top of identity block |
| `buyer` | `Example Buyer Co.` | left of `→`, `--ink` 500 |
| `supplier` | `Example Supplier Co.` | right of `→`, `--brand-green-deep` 500 |
| `savedAt` | `3m`, `2h` | `{savedAt} ago`, faint timestamp |
| `stage` | `Needs review`, `Ready` | amber `.pill-review` stage pill (note: always `pill-review` styling even when stage text is “Ready”) |
| `issues` | `2`, `0` | red `.pill-failed` exceptions pill, only if `> 0`; pluralized “exception/exceptions” |

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| `New` button (header) | `onClick` | `router.push("/upload")` — start a new order |
| Draft row (whole row, `role="button"`) | `onClick` | `router.push("/inbox/${d.id}")` — opens the draft's inbox/review (mock ids 404 on real API) |
| Draft row | `onKeyDown` Enter / Space | same nav as click (`preventDefault` + push) |
| Draft row | hover (`onMouseEnter`/`Leave`) | swaps `boxShadow` to a deeper card shadow and back |
| `Go to Inbox` button (empty state only) | `onClick` | `router.push("/inbox")` |
| ProcuLink Mark (empty state) | hover | opacity 0.7 → 1 (decorative only, not a control) |

There are **no per-row action menus, no delete/discard control, no filters, no search, no sort, no tabs, and no bulk-select** anywhere on this page.

### What opens / what closes
**No overlays — navigates in place.** This page opens **zero** modals, drawers, sheets, dialogs, popovers, dropdowns, tooltips, or toasts. Every interactive control is a direct `router.push` navigation to another route. The FOCUS HINT's “row actions / delete confirm” surfaces **do not exist** — rows are single click-through nav targets, and because there is no draft-persistence backend there is no delete affordance and therefore no delete-confirm dialog to capture. (This is a gap to design, not an existing surface.)

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| — | — | — | None — all actions navigate to a new route | — |

### States
- **Empty:** Fully handled and well-designed. A `Card` (min-height 360px, centered) holds `EmptyState`: bare 52px Mark, **“Drafts live here”**, an explanatory sub (“Save an order while you are still resolving it — mapping SKUs, clearing exceptions, picking a supplier — and it waits here until you are ready to send it.”), and a navy **“Go to Inbox”** button. This is what every real (non-mock) user sees.
- **Loading:** **Not handled — there is no `loading.tsx` in the drafts folder** and no skeleton/spinner in the page. Because the data is a synchronous local constant there is no async load today, so nothing renders a loading state. When wired to a real endpoint this will be a bare gap.
- **Error:** **Not handled.** No error boundary, no fetch, no retry. With no API call there is nothing to fail today, but the page has no error surface for when drafts are wired up.
- **Success/feedback:** None beyond row hover shadow + native focus ring. No toasts, no inline confirmations, no “saved” feedback (consistent with there being no save/delete actions on this screen).

### Responsive behaviour
- **HD 1920 / Desktop 1440:** content capped at 1480px and centered; gutter 34px. Header is title-left / `New`-right. Draft rows are single-line: identity flexes left, meta cluster (pills + timestamp) pinned right.
- **Tablet 768:** still uses the `sm:` desktop row layout (the row's `sm:flex-row` kicks in at 640px), so rows remain horizontal; gutter 24px.
- **Mobile 390:** header stacks (title over the `New` button which wraps below). Each draft row collapses to `flex-col gap-3`: identity block on top, the meta cluster (pills + timestamp) wraps underneath, left-aligned and `flex-wrap`. Button is forced to 44px tall (`h-[44px]`) for tap targets. No drag/mapper canvas here, so nothing breaks on small screens.
- **Cliffs:** none functionally — but the empty-state `Card`'s fixed `min-h-[360px]` is tall on a 390px viewport, and the row uses inline pixel padding that won't reflow as gracefully as the canonical primitives. The stage pill text “Ready” renders with amber `pill-review` styling regardless of value (a content/colour mismatch, not a breakpoint issue).

### Current UX issues
- **The whole feature is fake data.** Real users only ever see the empty state; the demo rows route to `/inbox/d1` etc. which 404 against the live API. The list UI implies a capability that doesn't exist (violates “offer ⇔ works”). Either ship draft persistence or keep the honest empty state and don't show a populated design as if it works.
- **No row actions at all, despite drafts being the place you'd most want them.** There is no resume/open button, no **discard/delete draft**, no rename, no overflow menu. The FOCUS HINT expected a delete confirm; none exists. A drafts list with no way to clear a stale draft will rot.
- **Status pill system is off-spec.** Rows use raw `.pill .pill-review` / `.pill-failed` CSS classes, not the app-wide `UnifiedStatusBadge`. Worse, the **“Ready” stage is rendered in amber “review” styling** — colour does not match meaning (DESIGN BAR #4: one status system, colour must match semantics). “Ready” should be green.
- **Not using the canonical row primitive.** The row re-implements `MobileListRow` by hand with a large inline `style` object (hardcoded `14px 16px`, custom box-shadow, manual hover handlers). This drifts from the one-list-density rule (DESIGN BAR #5) and duplicates accessibility logic that the shared component already owns.
- **Numbers are not consistently tabular.** PO numbers are mono (good) but the `savedAt` timestamp and `issues` count are not guaranteed tabular figures; the timestamp's `min-width:56px` hack is a symptom of non-tabular jitter (DESIGN BAR #3).
- **No loading or error states** (DESIGN BAR #6) — fine while data is a constant, but the moment this is wired to an endpoint there is nothing. Empty state is the only handled state.
- **Stage as free text** (`"Needs review"`, `"Ready"`) is fragile — not enum-backed, so the pill can't reliably map status → colour/icon.
- **Hover feedback is shadow-only** via JS mouse handlers; no CSS `:hover`, no pressed state beyond `active:bg-surface-2`. JS-driven shadow swaps don't respect reduced-motion intent as cleanly as a CSS transition would.
- **Single primary action is ambiguous.** `New` (header) and `Go to Inbox` (empty state) both compete as “the next thing to do”; on a populated list the dominant action should be resuming a draft, but rows have no explicit primary affordance.

### Redesign recommendations (for Claude Design)
Ranked most-impactful first. Keep navy `#0B1A2F` + violet brand; green=success, amber=warning, red=blocking.

1. **Decide the feature's truth first.** If draft persistence isn't shipping, make the empty state the *only* state and remove the demo list, or clearly label it “preview.” If it is shipping, wire to a real `GET /api/drafts` (or equivalent) and design real loading/error/empty around it. Don't ship a list that 404s.
2. **Add per-row actions with a separated, confirmed destroy.** Give each row a clear primary **“Resume”** (green, the dominant action) and an overflow `⋯` menu (dropdown) with **Rename** and a visually separated **Discard draft** that opens a small confirm dialog (“Discard PO-2026-008422? This can't be undone.” — Cancel / red Discard). This is the missing “delete confirm” surface and the page's most important add. (DESIGN BAR: destructive separated + confirm-before-destroy.)
3. **Replace the hand-rolled row with the canonical `MobileListRow` + `UnifiedStatusBadge`.** One row height, one padding, one hover/focus treatment, gray-200 gridlines, sticky header if it becomes a table. Drop the inline `style` object. (DESIGN BAR #5, #8.)
4. **Fix the status semantics: “Ready” must be green, “Needs review” amber, blocked/exceptions red** — one badge shape/size/padding, always with an icon or word, never colour alone. Make stage an enum, not free text. (DESIGN BAR #4.)
5. **Tabular figures everywhere** — PO#, exception count, and the saved-at timestamp use `font-variant-numeric: tabular-nums` so columns don't jitter; drop the `min-width:56px` band-aid. (DESIGN BAR #3.)
6. **Lead with the human field name.** Keep `buyer → supplier` (good — it's the bridge metaphor and human-readable), and surface the *real* exception summary (“2 unmapped SKUs”) rather than a bare count so the coordinator knows *why* it's parked.
7. **Add real loading (skeleton rows, not a spinner) and error (reason + Retry)** states once wired to an endpoint; reuse the same skeleton the inbox uses. Add a `loading.tsx`. (DESIGN BAR #6.)
8. **One dominant primary per state:** on the empty state keep a single green primary (e.g. “Upload an order” → `/upload`) and demote “Go to Inbox” to ghost; on the populated list let the per-row **Resume** be the dominant affordance and keep header **New** as a secondary/outline. (DESIGN BAR #7.)
9. **Add filter/sort affordances** (by stage, by saved-at, by supplier) with `aria-sort` once there's enough data to warrant it — drafts naturally accumulate. (DESIGN BAR #5.)
10. **Accessibility polish:** ensure the row's `role="button"` keeps the focus-visible ring, give the `New` plus-icon an `aria-label`-backed accessible name (currently text-labelled “New”, which is fine, but the SVG should stay `aria-hidden`), and keep every control ≥44px. (DESIGN BAR #9.)

---

### Screenshots — PRODUCTION (4)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/09-base-desktop-1440-drafts.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/09-base-hd-1920-drafts.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/09-base-mobile-390-drafts.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/09-base-tablet-768-drafts.png)

---

## 10. Inbound Invoices — `/inbound/invoices`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/inbound/invoices/page.tsx`
- **Key components:**
  - `src/app/(app)/inbound/invoices/page.tsx` (whole page — table, mobile cards, status badge, skeletons, row actions all defined inline here)
  - `src/components/bridge/layout/PageShell.tsx` (wide page wrapper, max-width `var(--container-wide)` ≈ 1480px)
  - `src/components/bridge/layout/PageHeader.tsx` (title "Invoices" + count subtitle + actions slot)
  - `src/components/bridge/layout/Card.tsx` (table surface + empty/error surfaces)
  - `src/components/bridge/layout/MobileListRow.tsx` (mobile stacked card body)
  - `src/components/bridge/DSPrimitives.tsx` → `Button` (all CTAs and row actions)
  - `src/app/(app)/inbound/invoices/loading.tsx` → `BridgePageLoader` from `src/components/bridge/BridgeLoader.tsx` (route-level Suspense fallback)
  - Data: `getInvoices` / `uploadInvoice` / `approveInvoice` / `downloadInvoice` / `InvoiceDto` from `src/lib/api-client.ts`
- **Capture URL (mock):** `/inbound/invoices` (no ids/query needed — the list query `getInvoices` returns the two mock rows `inv-001`, `inv-002` in mock mode)

### What it is & why it exists
This is the inbound-direction sibling of the outbound PO loop: a flat list of supplier **invoices** the org has received, so a coordinator can review, approve, and export them (and, longer term, reconcile them against the matching purchase order — the empty-state copy promises "review, approve, and reconcile them against your purchase orders"). It sits at the review/approve stage of the inbound workflow rather than the outbound parse→transform→deliver path. A procurement coordinator opens it to clear the pending-invoice queue: confirm an invoice is acceptable (Approve) and pull a CSV copy for their AP/ERP system (Download CSV).

### Who uses it & the primary job
**Procurement coordinator / AP-adjacent operator.** The single most important task is **clearing pending invoices**: scan the list, find rows with status `pending`, and click **Approve** (the per-row green action that only appears while pending). Secondary jobs are uploading a new invoice file (XML/EDI) and downloading an invoice as CSV.

### Layout & structure (current)
Top-to-bottom inside a `PageShell variant="wide"` (centered, max ~1480px, gutter ramp 16→24→34px, vertical padding 20→28px, canvas `var(--bg)`):

1. **Page header** (`PageHeader`) — `<h1>` "Invoices" in display font (Bricolage Grotesque, 28→30px, weight 600, letter-spacing −0.02em). Subtitle line (13px, `--ink-muted`) shows the live count: `"{n} invoice(s)"`, or "Loading…" while fetching in non-mock mode. Right-aligned **actions slot** holds an "Uploading…" status hint (when a POST is in flight) and a green **"Upload invoice"** primary button — but that button is conditionally rendered only when `invoices.length > 0` (it is suppressed in the empty state, which has its own upload CTA in the card).
2. **Notice banner** (conditional, `mb-4`) — a dismissible inline alert; green-left-accent for success, red-left-accent for failure (chosen by string-matching `"failed"`/`"Failed"` in the message). Contains the message text + a `✕` dismiss button.
3. **Body**, which is one of four mutually-exclusive branches:
   - **Loading** (non-mock only): desktop renders a `Card dense` with the full table header (`Invoice #`, `Supplier`, `Date`, `Amount`, `Lines`, `Status`, ``) and three `SkeletonRow`s (animated gray bars at widths 120/140/80/80/60/60/80); mobile renders three `SkeletonCard`s.
   - **Error** (non-mock only): a `Card` with centered "Failed to load invoices" + a green **Retry** button (re-invalidates the query).
   - **Empty**: a `Card` with centered "No invoices yet", explanatory paragraph, and a green **Upload invoice** button.
   - **List**: desktop `Card dense` table + a mobile stacked-card list (`sm:hidden`).
4. **No footer / no action bar.**

**Desktop table** (`hidden sm:block`, `font-size: 12.5`): single `<table>` with a 2px bottom-border header row of uppercase 10.5px labels (`--ink-faint`, tracking 0.06em): **Invoice #, Supplier, Date, Amount, Lines, Status, Actions**. Body rows have 1px `--surface-2` separators (none on the last row), `px-4 py-3` cells. Invoice # is mono + semibold (`--ink`); Supplier/Date are `--ink-muted`; Amount is semibold `--ink`; Lines is faint; Status is the badge; Actions holds the buttons.

**Mobile cards** (`sm:hidden`): each invoice is a `MobileListRow` wrapped in a relatively-positioned div with a 3px green left-accent strip (the Bridge-Layer signature, clipped to the card radius). Card top row = mono invoice number + supplier name (left) and `StatusBadge` (right); a meta row = date · bold total · "{n} line(s)"; then the action buttons.

**Spacing/type/density observations:** values are a grab-bag of hardcoded literals — header cells `py-2.5`, body cells `py-3`, badge text `10.5px`, table body `12.5px`, mobile card `padding:14`, Card dense `padding:12`, notice text `12.5px`, accent strips `3px`. Numbers are NOT tabular (no `tabular-nums`) — only the invoice number is `font-mono`; amounts and line counts use the default proportional font, so the Amount column does not decimal-align.

### Data shown
**Entity:** `InvoiceDto` (one per received supplier invoice). Fields displayed:

| Column | Field | Notes |
|---|---|---|
| Invoice # | `invoiceNumber` (string\|null) | mono, semibold; `—` when null |
| Supplier | `supplierName` (string\|null) | `—` when null; `supplierId` is null in mock data |
| Date | `invoiceDate` (string\|null) | raw ISO date string, unformatted; `—` when null |
| Amount | `totalAmount` (number\|null) + `currency` (string\|null) | formatted via `Intl.NumberFormat("en-EU", currency)`, default EUR, 2 decimals; `—` when null |
| Lines | `lineCount` (number) | raw integer (mobile pluralizes "line/lines"; desktop shows bare number) |
| Status | `status` (string) | mapped to one of `pending` / `approved` / `rejected` badges; unknown values fall back to a neutral pill showing the raw string |
| (not shown) | `id`, `createdAt` | `id` used for actions/keys; `createdAt` never displayed |

**Source:** `getInvoices()` (TanStack `useQuery`, key `["invoices"]`, `staleTime 30s`) → mock: returns `_mockInvoices` (FastParts Inc `INV-2026-001` €2450 pending 3-line; ElectroSupply Co `INV-2026-002` €890.50 approved 1-line) after 400ms; real: `GET /api/invoices`. Mutations: `POST /api/invoices/upload` (multipart), `POST /api/invoices/{id}/approve`, `GET /api/invoices/{id}/download?format=csv` (binary → object URL).

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| **Upload invoice** (header primary, green; only when list non-empty) | `fileInputRef.current?.click()` | Opens the OS-native file picker (hidden `<input type=file accept=".xml,.edi">`) |
| **Upload invoice** (empty-state card, green) | same `fileInputRef.current?.click()` | Same native file picker |
| Hidden `<input type="file">` `onChange` | `uploadMut.mutate(file)` | `POST /api/invoices/upload`; on success invalidates `["invoices"]`, sets success notice "Invoice {n} uploaded successfully.", clears the input; on error sets failure notice |
| **Approve** (row action, outline green; only when `status==="pending"`) | `approveMut.mutate(inv.id)` | `POST /api/invoices/{id}/approve`; sets `approvingId` (button shows "…", disabled); on success invalidates list + notice "Invoice {n} approved."; on error failure notice |
| **↓ CSV** (row action, outline blue; always shown) | `handleDownload(inv.id)` | `GET …/download?format=csv` → builds an `<a download>` and clicks it to save `invoice-{safeName}.csv`; sets `downloadingId` ("…", disabled). In mock/empty the client returns a `#…` sentinel URL → shows notice "Download isn't available in this preview (no file to export yet)." |
| **Retry** (error card, green) | `queryClient.invalidateQueries(["invoices"])` | Refetches the list |
| **✕ Dismiss notice** (icon button in banner) | `setNotice(null)` | Hides the notice banner |

There are no tabs, no sort controls, no filters, no search, no pagination, no row-level menu/kebab, and no row click-through to a detail view. Each row exposes only Approve (conditional) + Download.

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Notice banner | inline panel (in-flow, not an overlay) | Set automatically by any mutation outcome (upload success/fail, approve success/fail, download-unavailable, download fail) | One sentence of status text + `✕` button; green or red left-accent by string match | `✕` button (`setNotice(null)`), or replaced when the next action sets a new notice; **not** auto-dismissing, **not** Esc-closable |
| OS file picker | native browser dialog (not app UI) | "Upload invoice" buttons → `fileInputRef.click()` | Native file chooser filtered to `.xml,.edi` | OS-native (choose file / cancel) — outside React |
| CSV download | native browser file-save (not app UI) | "↓ CSV" → synthetic `<a download>.click()` | Browser save dialog / direct download of `invoice-{name}.csv` | Browser-native |

**No overlays — navigates in place.** This page opens **no** modal, drawer, sheet, dialog, popover, dropdown, tooltip, or toast component. All feedback is the single in-flow notice banner; all "opening" is delegated to native browser dialogs (file picker, download). This is the single most important structural fact for the redesign: there is currently nowhere to see an invoice's line items, totals breakdown, PO match, or rejection reason — the list is terminal.

### States
- **Empty:** Handled. Centered `Card` — "No invoices yet" (15px semibold) + paragraph ("Upload supplier invoices to review, approve, and reconcile them against your purchase orders.") + green **Upload invoice** button. (In mock mode the list is never empty, so this needs `NEXT_PUBLIC_USE_MOCK=false` + an empty real backend to see.)
- **Loading:** Two layers. Route-level `loading.tsx` → `BridgePageLoader` (animated blue→green wire mark + "Loading invoices…") shows during the Next Suspense/navigation boundary. In-component, while the `useQuery` is loading **and not in mock mode**, a table-shaped skeleton (3 `SkeletonRow`s desktop / 3 `SkeletonCard`s mobile) renders under the real header. In mock mode the loading branch is skipped (`!isApiMockMode`), so the 400ms mock delay shows nothing then pops the list.
- **Error:** Handled (non-mock only). `Card` with "Failed to load invoices" + green **Retry**. Note: the reason/status code is **not** surfaced — just a generic line. Mutation errors (upload/approve/download) surface as the red notice banner with the thrown `err.message`.
- **Success/feedback:** Inline notice banner only (no toast). Upload → "Invoice {n} uploaded successfully."; Approve → "Invoice {n} approved."; download-in-preview → the "not available in this preview" line. In-flight feedback: header "Uploading…" hint, and per-row buttons swap label to "…" and disable.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** `Card dense` table inside the ~1480px-capped `PageShell`. Header actions sit top-right on one row with the title. Full 7-column table.
- **Tablet 768:** Still desktop layout — the table/mobile switch is the Tailwind `sm` breakpoint (640px), so at 768px the `hidden sm:block` table is shown and `sm:hidden` cards are hidden. The 7-column table can feel cramped on a narrow tablet but does not restructure; columns are fluid (no min-widths), so long supplier names compete for space.
- **Mobile 390:** Below 640px the table is hidden and the stacked card list renders (invoice#/supplier + status badge, then date·total·lines, then action buttons). `PageHeader` stacks the actions below the title (`flex-col`). Buttons are `h-[44px]` on mobile (tap-min) and shrink to dense desktop heights at `sm`. **Cliff:** the `sm`-only loading/error gating means at exactly 640–767px a tablet still gets the full desktop table; and the empty-state upload button is the only upload entry on mobile when the list happens to be empty (the header button is suppressed at `length===0`).

### Current UX issues
- **No invoice detail view at all.** Rows are terminal — there is no way to open an invoice to see its line items, tax breakdown, or the PO it should match. The empty-state literally promises "reconcile them against your purchase orders," but no reconciliation/3-way-match surface exists. This is the biggest gap (DESIGN BAR: every data row should have a predictable detail/drill-in).
- **Numbers are not tabular and do not align** (BAR #3). Only `invoiceNumber` is `font-mono`; `Amount` and `Lines` use proportional figures and `Amount` is left-aligned, so the money column jitters and never decimal-aligns down the table. Dates are raw ISO strings (`2026-05-01`), not formatted.
- **Local, off-system status badge** (BAR #4). `StatusBadge` is defined inline with its own shape (`rounded` not `rounded-full`, 10.5px, 5px dot) and is explicitly NOT the app's `UnifiedStatusBadge`. It has a dot + word (good) but diverges in radius/size/padding from every other pill in the app, and only covers `pending`/`approved`/`rejected` (unknown statuses degrade to a gray pill of the raw string).
- **Row actions use glyph/emoji + ad-hoc inline colors** (BAR #4/#7/#8). "↓ CSV" uses a unicode arrow, not a Lucide icon; both row buttons override `borderColor`/`color` via inline `style` per-state instead of using `Button` variants. No single dominant primary per screen on the list rows — Approve (green) and Download (blue) read as co-equal.
- **Loading is invisible in mock/preview** — the in-component skeleton is gated on `!isApiMockMode`, so a designer reviewing in mock mode never sees the skeleton; only `loading.tsx` (a different visual) shows momentarily on navigation. Two different loading visuals for one page.
- **Error state hides the reason** (BAR #6) — "Failed to load invoices" with no status code or detail; Retry is the only affordance.
- **Notice banner is brittle and not a real status system.** Success vs error is decided by substring-matching `"failed"` in the message; it is not auto-dismissing, has no icon, no Esc handling, and is the page's only feedback channel (no toast).
- **Spacing/type literals everywhere** (BAR #1/#2). Padding/sizes are magic numbers (`py-2.5`, `py-3`, `12.5`, `10.5`, `14`, `12`) rather than a 4/8 scale; type hierarchy leans on color (`--ink` / `--ink-muted` / `--ink-faint`) more than weight, with faint `--ink-faint` used for the Lines column (contrast risk).
- **`uploadInvoice` has no mock branch** — in mock/preview, clicking Upload and choosing a file fires a real `POST /api/invoices/upload` against `API_BASE_URL`, which will fail and show a red error notice. Mock behavior is inconsistent with `getInvoices`/`approveInvoice`/`downloadInvoice` (which all mock).
- **Header tells a half-truth in mock mode** — the count subtitle is fine, but the "Loading…" sub and the in-component skeleton are suppressed in mock, so the page feels like it has no loading state.
- **Inbound vs outbound mental model is unmarked.** No breadcrumb / section context tells the coordinator this is the *inbound* (received) side; the parent `/inbound` nav grouping isn't reinforced on the page (BAR: nav active state + breadcrumbs for depth).

### Redesign recommendations (for Claude Design)
1. **Add an invoice detail surface (highest impact).** Make each row open a right **drawer** (or `/inbound/invoices/{id}` route) showing header fields, line items led by the **human field name**, totals/tax breakdown, currency, and — critically — the **PO match / reconciliation** status the empty state already promises. Approve/Reject/Download live in the drawer footer with a clear close (X + Esc + scrim, animate from the row). This is the missing core of the page.
2. **Adopt the app's `UnifiedStatusBadge` (or align this one to it).** One pill shape/size/padding, green/amber/red/neutral semantics with a Lucide icon + word, and add an explicit **Rejected** path in the UI (status exists in the badge map but there's no reject action). Never colour-only.
3. **Make every number tabular and right-align money** (BAR #3). Apply `tabular-nums` to Amount, Lines, dates, and the count; right-align the Amount column and decimal-align; format `invoiceDate` to a locale date (e.g. "1 May 2026"). Keep Invoice # mono.
4. **One row-action system.** Replace "↓ CSV" with a Lucide `Download` icon button + aria-label, use `Button` variants (green primary for the dominant action, ghost/outline secondary for the rest) instead of per-state inline `borderColor`/`color`; show a spinner inside the button on pending, not a "…" string. Make Approve the single dominant per-row action and demote Download.
5. **Unify loading + show it in all modes.** Drop the `!isApiMockMode` gate on the skeleton (or always render the skeleton during fetch), and reconcile `loading.tsx` (`BridgePageLoader` wire) with the in-component table skeleton so the page has one coherent loading story.
6. **Surface the error reason + retry** (BAR #6): include the failing status/detail under "Failed to load invoices," keep the green Retry.
7. **Replace the substring-driven notice with the real toast/feedback system** — typed success/error variants, Lucide icon, auto-dismiss with manual close + Esc, so feedback isn't keyed off the word "failed."
8. **Give the list a toolbar:** sortable columns (aria-sort) for Date/Amount/Status, a status filter (pending/approved/rejected), and search by invoice # or supplier — at any real volume the flat unsortable list is unusable. One row height, gray-200 gridlines, sticky header, hover (BAR #5).
9. **Normalize spacing/type to the 4/8 scale and carry hierarchy by weight** (BAR #1/#2): convert the magic literals to tokens, drop reliance on `--ink-faint` for data (contrast), use 600/500/400 weights. Keep navy/violet brand, green=approved/success, amber=pending, red=rejected.
10. **Fix the mock/preview honesty gaps:** add a `USE_MOCK` branch to `uploadInvoice` so preview uploads don't fire real POSTs, and add a breadcrumb/section header making the **inbound** context explicit with predictable back-nav to `/inbound`.

---

### Screenshots — PRODUCTION (7)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/10-base-desktop-1440-inbound-invoices.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/10-base-hd-1920-inbound-invoices.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/10-base-mobile-390-inbound-invoices.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/10-base-tablet-768-inbound-invoices.png)

**approve-success-notice · desktop-1440**

![approve-success-notice · desktop-1440](screenshots-prod/10-approve-success-notice-desktop-1440-inbound-invoices.png)

**download-preview-notice · desktop-1440**

![download-preview-notice · desktop-1440](screenshots-prod/10-download-preview-notice-desktop-1440-inbound-invoices.png)

**mobile-stacked-cards · mobile-390**

![mobile-stacked-cards · mobile-390](screenshots-prod/10-mobile-stacked-cards-mobile-390-inbound-invoices.png)

---

## 11. Advance Shipping Notices (Inbound ASNs) — `/inbound/asns`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/inbound/asns/page.tsx`
- **Key components:**
  - `src/app/(app)/inbound/asns/page.tsx` (the whole page incl. a locally-defined `StatusBadge`, `SkeletonRow`, `SkeletonCard`)
  - `src/app/(app)/inbound/asns/loading.tsx` → `BridgePageLoader` (`src/components/bridge/BridgeLoader.tsx`)
  - `src/components/bridge/layout/PageShell.tsx` (page canvas + max-width container)
  - `src/components/bridge/layout/PageHeader.tsx` (title + subtitle row)
  - `src/components/bridge/layout/Card.tsx` (the table/empty/error/loading surface)
  - `src/components/bridge/layout/MobileListRow.tsx` (mobile stacked card)
  - `src/components/bridge/DSPrimitives.tsx` → `Button` (only the error-state Retry button)
  - Data: `getAsns`, `AsnDto`, `isApiMockMode` from `src/lib/api-client.ts`
- **Capture URL (mock):** `/inbound/asns` — mock mode (`isApiMockMode`) is on, so `getAsns()` returns `_mockAsns` (two rows: `ASN-2026-001` / FastParts Inc / received, `ASN-2026-002` / GlobalComponents / pending) after a 400 ms delay. No mock ids are needed for any detail route because there is no detail route.

### What it is & why it exists
This is the read-only inbound list of **Advance Shipping Notices** — the EDIFACT DESADV / ASN documents a supplier sends to confirm an upcoming delivery. In ProcuLink's `parse → normalize → validate → review → transform → deliver → learn` loop, ASNs sit on the **inbound** side (documents arriving from suppliers), parallel to inbound invoices. The defining fact of this page today is that **ASN ingestion is not built** — DESADV parsing needs a commercial EDI licence the founder declined, and the backend `DesadvController POST /api/asns/upload` returns `501`. So the page deliberately ships **no upload control** and leads with an honest amber "Coming soon" notice; it only lists ASNs that may have been created by other means.

### Who uses it & the primary job
**Procurement coordinator** (the buyer-side operator). The intended primary job is "see which supplier shipments are inbound and confirmed." In its current state the real job is reduced to **awareness**: confirm whether any ASNs exist and read the honest "not available yet" message. There is no action the user can take on this page.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width `--container-wide` = 1480px; gutter ramp 16→24→34px, vertical padding 20→28px; canvas `var(--bg)`):

1. **Page header** (`PageHeader`) — `h1` "Advance Shipping Notices" (Bricolage Grotesque display, 28→30px, weight 600, letter-spacing -0.02em). Subtitle is a live count: `"{n} notices"` (e.g. "2 notices"), or `"Loading…"` while fetching in non-mock mode. No `actions` slot is passed, so the header is title-only. Bottom margin 20→24px.
2. **"Coming soon" notice** — a full-width amber callout (`mb-4`, radius 8px, `1px` amber-soft border + `3px` amber left border, amber-soft background, amber-deep text, 12.5px). Contains a 6px amber dot + bold "Coming soon." + "ASN / EDIFACT DESADV ingestion isn't available yet. We'll let you know when you can upload advance shipping notices." This renders in **every** state (loading/error/empty/populated) because it sits above the conditional block.
3. **Body** — one of four mutually-exclusive branches:
   - **Loading** (only when `isLoading && !isApiMockMode`): a `Card dense` (hidden < sm) wrapping a 5-column table whose header row is real but body is 3 `SkeletonRow`s (animated pulse bars at widths 120/140/80/60/80px); plus 3 `SkeletonCard`s shown only on mobile (`sm:hidden`).
   - **Error** (`isError && !isApiMockMode`): a `Card` with a centred column (p-10) — "Failed to load advance shipping notices" (14px, 600) + a green primary `Button` "Retry" that calls `queryClient.invalidateQueries(["asns"])`.
   - **Empty** (`asns.length === 0`): a `Card` with centred column — "No advance shipping notices yet" (15px, 600) + muted explainer "ASNs are sent by suppliers to confirm upcoming deliveries. Inbound ASN / EDIFACT DESADV ingestion is coming soon — there's nothing to upload here yet." No CTA (deliberately, since upload isn't built).
   - **Populated**: two parallel renders — a **mobile** stacked list (`flex flex-col gap-3 sm:hidden`) of `MobileListRow`s, and a **desktop** `Card dense` (hidden < sm) wrapping a 5-column table.

**Desktop table** (`overflow-hidden`, 12.5px base font): header row has a `2px` bottom border, columns "ASN #", "Supplier", "Ship date", "Packages", "Status" rendered as 10.5px uppercase faint labels (letter-spacing 0.06em), each `th` padded `px-4 py-2.5`. Body rows: `px-4 py-3` cells, `1px` `--surface-2` bottom divider (none on the last row). Cells: ASN # (mono, 600, ink), Supplier (ink-muted), Ship date (ink-muted), Packages (medium, ink — bare number), Status (the local `StatusBadge`).

**Mobile card** (`MobileListRow`, padding 14, radius `--radius-md`, card shadow, `min-height: --tap-min`): a left `3px` green accent strip; top row = ASN # (13px mono 600) over supplier name (12px muted) on the left, `StatusBadge` on the right; bottom row = "Ship: {date}" + "{n} pkgs" (medium ink).

### Data shown
Single entity: **ASN** (`AsnDto`). Source = `getAsns()` (`GET /api/asns`; in mock mode returns `_mockAsns`).

| Field (DTO) | Column / display | Notes |
|---|---|---|
| `asnNumber: string \| null` | "ASN #" | mono, 600; `"—"` when null |
| `supplierName: string \| null` | "Supplier" | `"—"` when null. (`supplierId` is in the DTO but **never displayed**.) |
| `shipDate: string \| null` | "Ship date" | raw string, no date formatting; `"—"` when null |
| `packageCount: number` | "Packages" | bare integer on desktop; "{n} pkg(s)" with pluralisation on mobile |
| `status: string` | "Status" | drives `StatusBadge`; only `"received"` is special-cased, everything else renders as "Pending" |
| `id` | (none) | React key only — not shown, not linked |
| `createdAt` | (none) | present in DTO/mock, never displayed |

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| "Retry" button (error state only) | `onClick` → `queryClient.invalidateQueries({ queryKey: ["asns"] })` | Refetches `getAsns`; re-renders into loading then list/empty/error |
| Table rows | none | Rows are **not** clickable, not links, no row-actions, no menu, no hover affordance beyond the static divider |
| Mobile `MobileListRow` | none | `onClick` is not passed, so the row is non-interactive (no `role=button`, no tab stop) |
| Header / toolbar | none | No filters, no search, no sort, no column controls, no upload, no pagination |

There are **no** sortable headers (no `aria-sort`), no filters, no search, no bulk actions.

### What opens / what closes
**No overlays — navigates in place.** This page opens **zero** modals, drawers, sheets, dialogs, popovers, dropdowns, tooltips, or toasts. There is no row detail route, no row menu, no upload dialog (intentionally suppressed because `POST /api/asns/upload` returns 501). The only state change is the in-place refetch triggered by the error-state "Retry" button. Nothing here needs an X/Esc/backdrop because nothing transient is ever rendered.

### States
- **Empty:** Handled. Centred `Card` — "No advance shipping notices yet" + an honest explainer. Deliberately **no** next-action CTA because upload isn't built; the "Coming soon" amber notice above carries the forward expectation.
- **Loading:** Handled two ways. (1) Route-level `loading.tsx` → `BridgePageLoader` (the animated buyer→supplier wire mark, "Loading ASNs…", reduced-motion-safe) shown during navigation/suspense. (2) In-page skeleton (table skeleton on desktop, card skeletons on mobile) when `isLoading && !isApiMockMode`. Note: in **mock mode the skeleton is skipped** (`isLoading && !isApiMockMode` is false), so mock renders straight to the populated table after the 400 ms delay.
- **Error:** Handled. Centred `Card` with reason ("Failed to load advance shipping notices") + green "Retry" button. Reason is generic — it does not surface the HTTP status or message.
- **Success/feedback:** None. No toast on retry; the list simply re-renders. No optimistic or confirmation feedback (there are no mutations on this page).

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Container caps at 1480px and centres; the 5-column table fills the `Card`. Columns are auto-width (no fixed widths) so wide screens leave a lot of right-side whitespace after the last "Status" column. Identical layout at both widths (no extra columns appear at HD).
- **Tablet 768:** Still desktop table (the `sm:` breakpoint = 640px, so ≥768 shows the table, not cards). Table is full-width; columns can feel sparse.
- **Mobile 390:** Table is hidden (`hidden sm:block`); the stacked `MobileListRow` cards render instead (green-accent strip, ASN#/supplier/status header, ship-date/packages footer). Skeleton also swaps table→cards. This is a proper stacked-not-shrunk mobile treatment. No known breakpoint cliff.

### Current UX issues
- **Page is a dead end with a contradiction:** it offers an inbound "ASN" list but the only thing it can ever say is "Coming soon / nothing to upload." A populated table can still appear (mock, or ASNs created by other means) **above** a notice that says ingestion isn't available — the populated state + "Coming soon" banner read as contradictory. (Design bar: never show one thing while claiming another.)
- **Bespoke status badge breaks the ONE badge system (bar #4).** `StatusBadge` is defined inline here (10.5px, `px-2 py-0.5`, 5px dot) instead of the canonical `UnifiedStatusBadge`/`Pill`. It also has a **truthiness bug**: only `status === "received"` → green "Received"; **every other value** (including any failure/rejected/error status) silently renders amber "Pending". A failed ASN would display as a benign "Pending" — exactly the "never show healthy when something failed" anti-pattern.
- **No tabular figures (bar #3).** "Packages" count and `shipDate` use the default proportional font; numbers will jitter column-to-column and dates won't align. ASN # is mono (good) but the numeric column isn't `font-variant-numeric: tabular-nums`.
- **Dates are raw strings (bar #3 / clarity).** `shipDate` prints verbatim (`"2026-05-15"`) with no locale formatting or relative hinting.
- **Table density drift (bars #1, #5).** Header uses a `2px` border + `py-2.5`; body rows use `1px var(--surface-2)` dividers + `py-3`. No sticky header, no zebra/hover, no `aria-sort`, no sortable affordance — inconsistent with the canonical table density used elsewhere.
- **No row affordance.** Rows look tabular but do nothing — no detail, no link, no hover. A coordinator can't drill into an ASN's packages/lines.
- **Error message is thin (bar #6).** "Failed to load" gives no status code or cause; Retry is the only recourse.
- **Inline-styled, locally-defined components (consistency).** `StatusBadge`, `SkeletonRow`, `SkeletonCard` and the amber notice are all hand-rolled with inline `style` + hard-coded hex fallbacks (`#FFF4D6`, `#D4900A`, `#7A5700`) rather than shared primitives/tokens — drift risk if the design system changes.
- **Empty/coming-soon overlap.** The empty `Card` explainer repeats the same "coming soon / nothing to upload" message already in the amber banner — redundant copy stacked vertically.

### Redesign recommendations (for Claude Design)
1. **Resolve the "coming soon" contradiction first.** Decide one of: (a) keep it as an honest **placeholder page** — drop the table entirely, show one centred "Inbound ASNs are coming soon" state (icon, one sentence, optional "Notify me"), and don't render rows that can't be acted on; or (b) if ASNs can genuinely arrive, demote the banner to an inline info strip and make the list real. Don't show a populated table beneath a "not available yet" banner.
2. **Replace the local `StatusBadge` with the canonical `UnifiedStatusBadge`/`Pill` (bar #4) and fix the truthiness bug.** Map explicit statuses (received=green, pending=amber, failed/rejected=red, unknown=neutral) with icon+word; never let an unknown/failed status fall through to a benign "Pending".
3. **Apply tabular figures (bar #3)** to Packages, Ship date (and ASN # already mono): `font-variant-numeric: tabular-nums`. Right-align the Packages column so counts line up.
4. **Format `shipDate`** to a consistent locale date (e.g. `15 May 2026`) and consider a relative hint ("in 3 days") since ship dates are the actionable signal on an ASN.
5. **Adopt the canonical table density (bar #5):** single row height, `gray-200` gridlines, sticky header, hover row, and — if rows become actionable — `aria-sort` + sortable headers (by ship date / supplier / status).
6. **Make rows lead with the human field and become drill-in targets** if a detail view is ever built: ASN # + supplier first, status pill right-aligned, packages as a secondary metric — and route the row (and `MobileListRow` via `onClick`) to an ASN detail showing packages/lines.
7. **Strengthen the error state (bar #6):** include the failure reason/status alongside Retry, keep Retry as the single ≥44px green primary.
8. **De-duplicate empty vs banner copy:** if keeping both, let the banner state the capability status once and the empty card focus on "what to do next" (or remove the empty card when the banner already covers it).
9. **Tokenise the amber notice and skeletons** — replace inline hex fallbacks with design-system tokens and reuse a shared `Callout`/`Skeleton` primitive so this page tracks the system automatically.
10. **Keep the navy/violet brand, green=success/output, amber=warning, red=blocking** throughout; the green accent strip on mobile rows and the green Retry primary are consistent with the system and should stay.

---

### Screenshots — PRODUCTION (4)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/11-base-desktop-1440-inbound-asns.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/11-base-hd-1920-inbound-asns.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/11-base-mobile-390-inbound-asns.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/11-base-tablet-768-inbound-asns.png)

---

## 12. Item Code Mappings — `/library/mappings`

- **File:** `src/app/(app)/library/mappings/page.tsx` (1-liner: renders `<MappingEditor />`)
- **Key components:**
  - `src/components/bridge/MappingEditor.tsx` — the entire page (table + filters + `MappingPanel` modal + `SourceTag` / `Field` / `RequiredField` helpers all live in this one file)
  - `src/components/bridge/layout/PageShell.tsx` — `variant="wide"` page wrapper (max-width `var(--container-wide)` ≈ 1480px, grey `var(--bg)` canvas, gutter 16/24/34px)
  - `src/components/bridge/layout/PageHeader.tsx` — canonical title row ("Mappings" + subtitle + actions slot)
  - `src/components/bridge/BridgeLoader.tsx` — `BridgePageLoader` used by `loading.tsx`
  - `src/hooks/useOrderDirection.ts` — swaps the word "Supplier"→"Customer" for inbound orgs (display only)
- **Capture URL (mock):** `/library/mappings` — in mock mode (`NEXT_PUBLIC_USE_MOCK=true`) the page renders 12 `MOCK_ROWS` immediately with no supplier selection required, so the base table, all overlays and the filter states are reachable from this URL with no ids.

### What it is & why it exists
This is the **Learn** loop made visible: a reference library of buyer-item-code → supplier-item-code translations the engine reuses automatically on every future order so coordinators never re-map the same SKU twice. It is the persistent store behind the per-line code resolution that happens during review/transform. A procurement coordinator opens it to audit which translations exist, fix a wrong code, bulk-import a supplier's price-list mapping as CSV, or export the library for a colleague/ERP. It is not part of a single order's flow — it is the cross-order memory.

### Who uses it & the primary job
**Procurement coordinator** (and occasionally an integration expert seeding mappings up front). The single most important task: **find a mapping and trust/correct it** — either confirm the buyer→supplier code pair is right, or click a row to edit/delete it. The secondary high-value task is **bulk Import** a CSV of code pairs for a supplier.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (grey canvas, vertical flex column):

1. **PageHeader row** — `h1` "Mappings" (Bricolage Grotesque, 28→30px, 600). Subtitle reads `Buyer → supplier item code library · {N} saved` (or, in live mode with no supplier picked, `… · select a supplier to view its mappings`). Right side **actions slot**: a 2-col grid on mobile / inline `flex` on desktop containing **Import** (white outline, upload icon) and **Add mapping** (buyer-**blue** `#1E66C9` filled, plus icon; label collapses to "Add" under `sm`). Both are 40px tall on mobile, 34px on `lg`.
2. **Toolbar row** (on the grey canvas, above the card) — a `flex-col → lg:flex-row` strip:
   - Result count: `Showing {filtered} of {total}` (bold ink numbers) — or "No supplier selected".
   - A spacer `flex-1`.
   - **Source filter chips**: `All · AI · Manual · Imported · Inherited` (horizontally scrollable on mobile, scrollbar hidden). Active chip = green-soft fill + green text + green-tinted border.
   - **Supplier route `<select>`** ("All suppliers" default + one option per supplier).
   - **Search input** (300px on `lg`) with a `⌕` glyph prefix; green focus ring.
   - **Export** ghost button (white, grey text) — the design header has none, kept reachable here.
3. **Notice banner** (conditional) — green-soft rounded strip for local/demo confirmations.
4. **Table card** — a white `rounded-[10px]` card with `1px #E5E8EE` border + faint shadow, in a scroll area. It contains exactly one of: the *select-a-supplier* prompt, the *loading skeleton*, the *desktop table / mobile card list*, or an *empty/no-match* state.

**Desktop table** (`min-w-[760px]`, `12.5px`): sticky white header, 6 columns —
`Buyer | Buyer code | Supplier | Supplier code | Source | Used` (Used right-aligned). Header labels are 10.5px uppercase, `tracking-0.07em`, `var(--ink-faint)`. Rows are full-width clickable (`cursor-pointer`, hover `#F7FAFD`), `px-4 py-3.5`. Buyer name + buyer code are **blue** (`#0F4FA8` link / `#0B1A2F` mono code); supplier name + supplier code are **green** (`#1E6D29`). `Used` is a mono `{n}×`.

**Mobile card list** (`md:hidden`): each row becomes a tappable card — buyer name (blue) + SourceTag on the top line, then `buyerCode → supplierCode` with an arrow glyph, then supplier name (green) + `{used}×`.

Density/type/spacing observations: the page is heavily **inline-styled with hard-coded hex constants** (a ~20-line `BLUE/GREEN/…` palette block at the top) and **hard-coded px font sizes** (12 / 12.5 / 13 / 18 / 28px) rather than design tokens. Control heights drift (34px desktop vs 40px mobile vs 30px chips vs 9px-padding rows). Numbers are mono but the page does not request `font-variant-numeric: tabular-nums`.

### Data shown
**Entity:** `SupplierMapping` (`src/types/procurement.ts`) — `{ id, buyerItemCode, supplierItemCode, confidence?, source? }`. The component maps it into a local `MappingRow { id, buyer, buyerCode, supplier, supplierCode, source, used? }`.

- **Columns displayed:** Buyer (org name — **always blank `"—"` from the API mapper, only populated in mock**), Buyer code, Supplier (the selected supplier's name), Supplier code, Source (AI / Manual / Imported / Inherited, derived from `m.source` = `suggested`→AI / `imported` / `inherited` / else Manual), Used (mock-only count; never returned live).
- **Source of data:**
  - Mock mode → `MOCK_ROWS` (12 hard-coded rows in `MappingEditor.tsx`).
  - Live mode → `apiClient.getSupplierMappings(supplierId)` → `GET /api/suppliers/{id}/mappings`. The query is `enabled` only when a supplier is selected (no cross-supplier list endpoint exists). Supplier list comes from `apiClient.getSuppliers()`.
  - Mutations: `createSupplierMapping` → `POST /api/suppliers/{id}/mappings`; `updateSupplierMapping` → `PUT …/mappings/{mappingId}`; `deleteSupplierMapping` → `DELETE …/mappings/{mappingId}`; `importSupplierMappings(file)` → `POST …/mappings/import` (multipart `file`), returns `{ created, updated }`; export is built client-side (CSV blob `buyer_code,supplier_code`).

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| **Import** button (header, outline) | `openPanelForSupplier("import")` | Opens the modal in import mode; if no supplier selected, silently selects the first supplier (or shows a green notice "Add a supplier before saving…") |
| **Add mapping** button (header, blue primary) | `openPanelForSupplier("add")` | Opens the modal in add mode (same first-supplier fallback) |
| **Source filter chips** (All / AI / Manual / Imported / Inherited) | `setSrc(s)` | Filters `filtered` rows in place; active chip turns green-soft |
| **Supplier route `<select>`** | `setSelectedSupplierId` + `setRoute` | Switches which supplier's mappings the live query fetches; "All suppliers" clears selection → shows the select-a-supplier prompt (live mode) |
| **Search input** | `setSearch` | Live client-side filter across buyer/buyerCode/supplier/supplierCode |
| **Export** button (toolbar, ghost) | `openPanelForSupplier("export")` | Opens the modal in export mode |
| **Table row click** (desktop) | `setPanel({ kind: "edit", row })` | Opens the edit modal pre-filled with that row |
| **Mobile card tap** | same `{ kind: "edit", row }` | Opens the edit modal |
| **Modal: Choose file** label/input | `setImportFile(file)` | Stores selected CSV; label text becomes the file name |
| **Modal: Buyer item code / Supplier item code** inputs | `setBuyerCode` / `setSupplierCode` | Edits the pair; both required (mono, `*`) |
| **Modal: primary button** ("Save mapping" / "Validate import" / "Export CSV") | `handleAction()` | Calls the matching API mutation, invalidates `["supplier-mappings", supplierId]`, closes + sets a notice |
| **Modal: Delete** (edit only, left, red outline) | inline `deleteSupplierMapping` | Deletes the mapping, invalidates query, closes with "Mapping deleted." |
| **Modal: Cancel** / **× Close** | `onClose()` | Closes the modal, restores focus to trigger |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Mapping panel — Add** | Modal (`role="dialog" aria-modal`, scrim `#0B1A2F66`; bottom-sheet on mobile, centered 600px card on `sm+`) | Header **Add mapping** button | Blue link-icon eyebrow + title "Add item code mapping"; read-only **Buyer** ("All buyers") + required **Buyer item code** input; read-only **Supplier** (route) + required **Supplier item code** input; blue info banner ("Saved mappings are reused automatically…"); footer Cancel + green **Save mapping** | × button · Cancel · **Esc** · backdrop click · successful Save (`onDone`) |
| **Mapping panel — Edit** | Modal (same shell) | Row click (desktop) / card tap (mobile) | Same code form pre-filled with the row's codes; footer adds a left-aligned red **Delete** button | × · Cancel · Esc · backdrop · Save (update) · Delete |
| **Mapping panel — Import** | Modal (same shell) | Header **Import** button | Title "Import mappings"; green dashed **Drop CSV here** dropzone + "Choose file" file input (accepts `.csv`); helper noting expected `buyer_code, supplier_code` columns; grey info note; footer Cancel + green **Validate import** (disabled until a file is chosen) | × · Cancel · Esc · backdrop · successful import (`Imported: N created, M updated`) |
| **Mapping panel — Export** | Modal (same shell) | Toolbar **Export** button | Title "Export mappings"; read-only **Supplier** context field (the route); blue info banner ("Downloads this supplier's mappings as a CSV…"); footer Cancel + green **Export CSV** (triggers a client-side blob download) | × · Cancel · Esc · backdrop · successful export |
| **Notice banner** | Inline strip (not an overlay) | Any `onDone(message)` or the no-supplier guard | Green-soft strip with a one-line confirmation/error | Replaced/cleared on next panel open (`setNotice(null)`) |

There is **one modal component** (`MappingPanel`) reused in four `kind` modes. It is a hand-rolled fixed-overlay div (not shadcn `Dialog`) but implements its own focus trap, Escape handler, autofocus of the first field, and focus restore on close. There are **no toasts** — all feedback is the inline notice strip. There are no dropdown menus, popovers, or row-action kebabs.

### States
- **Empty (live, no supplier chosen):** dedicated centered prompt inside the card — `⇅` glyph, "Select a supplier to view its mappings", explanatory copy. This is the default live landing because mappings are per-supplier.
- **Empty (supplier chosen, zero mappings):** `⊘` glyph, "No item mappings yet" + "Add mappings to automatically translate your buyer item codes to supplier item codes."
- **Empty (filter excludes all):** `⊘` glyph, "No mappings match your filter".
- **Loading (route-level):** `loading.tsx` → `BridgePageLoader label="Loading mappings…"` (animated blue→green wire mark, reduced-motion frozen).
- **Loading (in-card, after picking a supplier):** 3 grey skeleton bars (`h-9`, `#F0F2F6`) inside the card while `mappingsLoading`.
- **Error:** **Not handled at the list level** — `useQuery` exposes `isLoading` only; a failed `getSupplierMappings` shows no reason/retry (falls through to `liveRows ?? []` → looks like an empty supplier). Modal mutations **do** surface errors: a red `#FBE3E3` strip above the footer ("Delete failed", server message, "Choose a CSV file first.").
- **Success/feedback:** green inline notice strip ("Mapping saved." / "Mapping updated." / "Mapping deleted." / "Imported: N created, M updated." / "Export downloaded."). In mock mode the modal short-circuits to local-only messages.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** full 6-col table inside the wide (≈1480px) card; header actions + toolbar inline on one row; search 300px. Table can `overflow-x` below 760px content width.
- **Tablet 768:** still the desktop table (`md:` breakpoint = 768, so the table shows from 768 up); toolbar starts wrapping at `lg` so chips/select/search/Export may stack into multiple rows between 768–1024px.
- **Mobile 390:** header actions become a 2-col grid (Import | Add); "Add mapping" → "Add"; the table is replaced by the **stacked card list**; filter chips scroll horizontally; select/search/Export go full-width and stack; the modal becomes a **bottom sheet** (`items-end`, `rounded-t-[12px]`, `max-h-92vh`).
- **Known cliffs:** between 768 and ~1024 the toolbar wraps to 2–3 rows (chips + select + search + Export) which is dense/awkward; the `<select>` and `Export` competing for width at `lg` is the tightest point.

### Current UX issues
- **No status-badge system parity (Bar 4).** `SourceTag` is a bespoke pill (`rounded-[6px]`, 11px, hand-picked hex per source) — not the app's one status-badge component, and it carries meaning by colour + word but with its own shape/padding distinct from every other pill in the app.
- **Tokens bypassed (Bars 1, 2, 8).** The whole file is inline `style={{}}` with a private hex palette and raw px font sizes; this drifts from `globals.css`/Tailwind tokens and from the navy/violet system the rest of the app uses. Control heights are inconsistent (34/40/30px; row `py-3.5`; chip `h-9/h-[30px]`).
- **No tabular figures (Bar 3).** `Used` counts and codes are mono but not `tabular-nums`; the right-aligned `Used` column will jitter (e.g. `9×` vs `312×`).
- **Two primary actions compete (Bar 7).** "Add mapping" is blue and "Save mapping"/Import are green — there is no single dominant green primary on the list screen; the header primary is blue, which conflicts with the app's green=primary/commit convention.
- **Error state missing on the data surface (Bar 6).** A failed mappings fetch is invisible — it reads as an empty supplier with no reason/retry. "Never show healthy when something is failing" is violated by silence.
- **Buyer column is dead in live mode.** `apiMappingToRow` always sets `buyer: ""`, so the live desktop table's first column is always `—`; a whole column carries no information except in mock data.
- **"Used" column is mock-only.** Live rows never have `used`, so the column is permanently `—` in production — a column promising reuse evidence that shows nothing real.
- **`<select>` + `⌕`-glyph search look unfinished.** A native `<select>` with `appearance-none` and no chevron, plus a Unicode `⌕` instead of a Lucide `Search` icon, read as placeholder UI next to the polished header.
- **Empty-supplier UX is a dead-end on a per-supplier model.** The default live state ("All suppliers") shows a prompt with no inline supplier picker in the empty card — the only way forward is the toolbar `<select>` higher up.
- **Backdrop-click closes a form modal without confirm.** Clicking the scrim discards unsaved Add/Edit input silently; destructive (Delete) is in the same footer as Cancel/Save without a confirm step.
- **Icon-only × and Export lack consistent affordances.** `×` has `aria-label` (good) but is a tiny 32px control; the toolbar Export ghost button is visually identical-weight to the source chips, muddying hierarchy.

### Redesign recommendations (for Claude Design)
1. **Adopt the one status-badge component for Source** (Bar 4) — same shape/size/padding as the rest of the app, with the existing AI=violet / Manual=neutral / Imported=green / Inherited=blue semantics expressed via tokens, each with a small Lucide icon (sparkle off — keep "AI" word). Keep navy/violet brand.
2. **Replace the inline hex palette with design tokens** (Bars 1, 2, 8) — drive every colour/size from `globals.css`/Tailwind so this page matches the navy `#0B1A2F` + violet system; normalize to ONE control height (recommend 36–40px) and the 4/8px spacing scale; one card radius/border/shadow tier.
3. **Add a real ERROR state to the table card** (Bar 6) — surface `getSupplierMappings` failures with a reason + Retry button instead of a silent empty supplier; never render an empty library when the fetch actually failed.
4. **Make `Used` (reuse count) real and tabular** (Bar 3) — have the API return per-mapping usage and right-align with `tabular-nums` so the column is the trust signal it implies; if usage isn't available, drop the column rather than show permanent `—`.
5. **Resolve the primary-action conflict** (Bar 7) — make ONE dominant green primary. Since the page's core verb is "add/save a mapping", consider green for "Add mapping" (≥44px) and demote Import/Export to outline/ghost; keep buyer=blue / supplier=green only inside the table semantics, not on the global CTA.
6. **Fix the live Buyer column** — either populate buyer name from the API or remove the column in live mode so the table doesn't lead with a dead `—` field; lead each row with the human-meaningful pair (buyer code → supplier code).
7. **Upgrade the toolbar controls** — replace the native `<select>` with a styled supplier combobox (chevron, search) and the `⌕` glyph with a Lucide `Search`; give the toolbar a single rhythm so it collapses cleanly at 768–1024 instead of wrapping into 3 rows (the current cliff).
8. **Put a supplier picker in the empty state** — in the "Select a supplier" card, embed the picker/CTA inline so the next action is in the empty surface (Bar 6's "next action"), not only in the toolbar above.
9. **Guard destructive + unsaved actions** — separate Delete from Cancel/Save with a confirm step; warn (or block) backdrop-dismiss when the Add/Edit form is dirty. Keep Esc/× as the explicit closers and animate the sheet from the trigger.
10. **Make the Import modal a proper dropzone** — wire real drag-and-drop onto the green dashed area (currently the box is decorative; only the file input works), show the parsed row count preview before commit, and keep the write-only nature clear ("existing codes updated, new added").

---

### Screenshots — PRODUCTION (11)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/12-base-desktop-1440-library-mappings.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/12-base-hd-1920-library-mappings.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/12-base-mobile-390-library-mappings.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/12-base-tablet-768-library-mappings.png)

**add-mapping-modal · desktop-1440**

![add-mapping-modal · desktop-1440](screenshots-prod/12-add-mapping-modal-desktop-1440-library-mappings.png)

**edit-mapping-modal · desktop-1440**

![edit-mapping-modal · desktop-1440](screenshots-prod/12-edit-mapping-modal-desktop-1440-library-mappings.png)

**export-mappings-modal · desktop-1440**

![export-mappings-modal · desktop-1440](screenshots-prod/12-export-mappings-modal-desktop-1440-library-mappings.png)

**import-mappings-modal · desktop-1440**

![import-mappings-modal · desktop-1440](screenshots-prod/12-import-mappings-modal-desktop-1440-library-mappings.png)

**mobile-card-list · mobile-390**

![mobile-card-list · mobile-390](screenshots-prod/12-mobile-card-list-mobile-390-library-mappings.png)

**mobile-edit-sheet · mobile-390**

![mobile-edit-sheet · mobile-390](screenshots-prod/12-mobile-edit-sheet-mobile-390-library-mappings.png)

**source-filter-ai-active · desktop-1440**

![source-filter-ai-active · desktop-1440](screenshots-prod/12-source-filter-ai-active-desktop-1440-library-mappings.png)

---

## 13. Rule catalog (Validation rules) — `/library/rules`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/rules/page.tsx`
- **Key components:**
  - `src/components/bridge/ValidationRules.tsx` — the entire screen (table, mobile cards, inline editor, mobile bottom-sheet, loading/error states). It also defines the internal `RuleEditor`, `SeveritySegment`, `CheckRow`, `Field`, and `Toggle` sub-components in-file.
  - `src/components/bridge/layout/PageShell.tsx` — page wrapper (`variant="wide"`, max-width `var(--container-wide)` = 1480px).
  - `src/components/bridge/layout/PageHeader.tsx` — canonical title + subtitle + actions row.
  - `src/hooks/useOrderDirection.ts` — swaps the word "Supplier" → "Customer" for inbound orgs (display only).
  - API: `getRules` / `createRule` / `updateRule` / `toggleRule` / `deleteRule` / `RuleDto` from `src/lib/api-client.ts` (lines 1693–1749).
- **Capture URL (mock):** `/library/rules` (in mock mode `getRules()` returns `[]` and the component falls back to its own hard-coded `RULES` array of 10 rules — see `ValidationRules.tsx` lines 58–69; valid mock row ids are `r1`–`r10`).

### What it is & why it exists
This is the **descriptive catalog of validation checks** an org cares about (currency = EUR, line items need supplier codes, quantity > 0, warn over €50k, etc.). It sits in the *validate* stage of `parse → normalize → validate → review → transform → deliver → learn`, but with an honest caveat baked into the code: **this screen does NOT gate or block delivery.** The backend `ValidationRule` has no executable condition and `IValidationRuleService` is never called by the transform/delivery pipeline (see file-header comment, lines 9–15). The only validation that actually runs is the per-supplier **Acceptance** tab. So this page documents/classifies checks and links out to where enforcement is really configured. A procurement coordinator opens it to see "what checks exist, how severe each is, how often each fired in 30 days," and to author/edit/toggle catalog entries.

### Who uses it & the primary job
**Persona:** procurement coordinator / integration expert (admin-ish, but self-serve). **Primary job:** browse the rule catalog and **edit or create a rule** (name, scope, severity, description, active toggle), with the secondary job of flipping a rule's Active toggle on/off inline.

### Layout & structure (current)
Top-to-bottom on the grey page canvas (`--bg` #F6F7FA), inside `PageShell variant="wide"`:

1. **PageHeader** — title "Rule catalog" (Bricolage Grotesque, 28/30px, weight 600). Subtitle: "A catalog of the checks you want to run · {N} active. Enforcement is configured per supplier — set up blocking checks on each [supplier's Validation rules tab]" (last clause is a blue underlined link to `/library/suppliers`). Right-aligned **+ New rule** primary button (blue `#1E66C9`, 38px tall desktop / 44px mobile, full-width on mobile).
2. **Enforcement callout** (`hidden sm:block`) — a soft-blue info banner (`#F2F7FE` bg, `#D6E2F4` border): bold "This is a catalog, not a gate." + explainer + the same supplier-tab link. Hidden on mobile.
3. **Notice strip** (conditional) — a green inline confirmation pill ("Rule saved." / "Rule created." / "Rule deleted." / error text) with a 6px green dot.
4. **Split-detail body** — a CSS grid: `lg:grid-cols-[minmax(0,1fr)_minmax(340px,400px)]`, `gap-5` (20px). Below `lg` it collapses to one column.
   - **Left / main:** the **rules table** (desktop, `hidden lg:block`) inside a white card (radius 12px, border `#E5E8EE`, shadow `0 1px 3px rgba(16,24,40,0.05)`).
   - **Left (mobile):** the same rules as **stacked row-cards** (`lg:hidden`).
   - **Right:** the **inline `RuleEditor`** card — `lg:sticky lg:top-0`, `hidden lg:block`. Always rendered on desktop for the current selection (defaults to first rule).
5. **Mobile bottom-sheet** (`lg:hidden`, conditional) — a full-height-ish dialog (`top-12 bottom-0`) hosting the same `RuleEditor`, opened only by a card tap or +New rule on mobile.

**Table columns** (header is uppercase 10.5px, `#9AA3B5`, letter-spacing 0.07em): `Rule` | `Scope` | `Supplier` (relabels to "Customer" inbound) | `Severity` | `Triggered 30d` | `Active` (right-aligned). Row height ~ `py-3.5` (14px vertical). Disabled rules render at `opacity 0.62`. Active row = light-blue fill `#EAF0F8` + 2px blue left-border.

Spacing/type/density observations: heavy reliance on **inline `style={{}}` with hard-coded hex + odd px sizes** (10.5px, 11.5px, 12.5px, 13px, 14.5px) rather than tokens or the 4/8 scale. Numbers (Triggered, code) use JetBrains Mono. Severity uses a pill; Scope uses a grey chip.

### Data shown
Entity: **validation rule** (`RuleDto`). Fields displayed/edited:
- `name` (bold rule title; "Untitled rule" italic placeholder if empty)
- `code` — display-only, derived client-side via `codeFor(name, entity)` (e.g. `HEA-PA-YM`, mock uses pretty codes like `GLOBAL-CUR-01`); shown mono under the name.
- `entity` → **Scope** chip; one of `Line item | Header | Supplier | Buyer | Amount`.
- `supplier` — display-only, **always "All suppliers"** for live data (RuleDto has no per-rule supplier binding; mock has a few named ones like "Acme Components", "VanDerBerg Metaal").
- `severity` → pill: `error`=Critical (`#FBE3E3`/`#B43838`), `warning`=Warning (`#FAF1DD`/`#B36D14`), `info`=Info (`#EAF0F8`/`#1E66C9`).
- `triggers` (`triggerCount`) → "Triggered 30d" mono number (`#0B1A2F` if >0, else faint `#CBD0DA`).
- `enabled` → Active toggle.
- `description`, `lastTriggered`, `autoBlock` (autoBlock preserved but UI removed), `createdAt`.

Data source: live `GET /api/rules` via `getRules()` (TanStack Query key `["rules"]`, enabled only when `!isApiMockMode`). In mock mode the component uses its local `RULES` array and mutates it in React state.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| **+ New rule** (header button) | `setNotice(null); setSelId("new"); setEditorOpen(true)` | Loads a blank `NEW_RULE` into the editor (desktop right panel; mobile opens bottom-sheet). |
| "supplier's Validation rules tab" link (subtitle + callout) | `next/link` | Navigates to `/library/suppliers`. |
| Table row click | `setNotice(null); setSelId(r.id)` | Selects that rule → editor panel re-keys to it. |
| **Active toggle** (table cell / mobile card) | `handleToggle(id)` — `stopPropagation` so it doesn't select | Mock: flips local state. Live: `toggleRule(id)` → `PATCH /api/rules/{id}/toggle`, invalidates `["rules"]`. |
| Mobile rule card tap | `setSelId(r.id); setEditorOpen(true)` | Opens the mobile bottom-sheet editor for that rule. |
| Editor **Rule name** input | `defaultValue` ref | Captured on save. |
| Editor **Applies to** `<select>` | native select (Line item/Header/Supplier/Buyer/Amount) | Captured on save (Scope). |
| Editor **Severity** segmented control | `SeveritySegment` — Warning / Critical buttons | Sets local `severity` state; recolors the "Recommended enforcement" banner. (`info` rules display as Warning until the user picks one.) |
| Editor **Condition (WHEN)** box | read-only mono panel | Shows `rule.condition` / falls back to description; not editable. |
| Editor **Recommended enforcement** box | read-only | Severity-colored sentence telling you to set the check up per-supplier (descriptive only). |
| Editor **Description** textarea | `defaultValue` ref | Captured on save. |
| Editor **In catalog (active)** checkbox | `CheckRow` ref | Captured on save as `enabled`. |
| Editor **Save rule / Create rule** (green) | `save()` → `handleSave` | Mock: mutates local array. Live: `createRule` (`POST /api/rules`) or `updateRule` (`PUT /api/rules/{id}`); invalidates `["rules"]`; sets notice; closes mobile sheet. |
| Editor **Delete** (red icon button, `onDelete` only when not new) | `handleDelete(id)` | Fires `window.confirm`; on OK mock-removes or `deleteRule` (`DELETE /api/rules/{id}`); clears selection; sets notice; closes sheet. |
| Mobile sheet **Close (X)** button | `setEditorOpen(false)` | Closes the bottom-sheet. |
| Mobile sheet **backdrop** | `setEditorOpen(false)` | Closes the bottom-sheet. |
| Error-state **Retry** button | `refetch()` | Re-runs the rules query. |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Rule editor (desktop)** | Inline sticky panel (NOT an overlay) | Always present; content swaps on row click or **+ New rule** | Rule name, Applies-to select, Severity segment, read-only Condition + Recommended-enforcement boxes, Description textarea, "In catalog (active)" checkbox, trigger stats, Save + Delete | Never closes — it is a permanent grid column. Selecting another row replaces its content. |
| **Mobile editor bottom-sheet** | Drawer / sheet (`role="dialog" aria-modal="true"`) | Mobile card tap, or **+ New rule** on mobile (`setEditorOpen(true)`) | Sticky header (title "New rule"/"Edit rule" + code, 44×44 close button) over a scrollable `RuleEditor` (`variant="sheet"`) | X button, **Escape** key (window listener), backdrop tap, or a successful Save/Delete (`setEditorOpen(false)`). Body scroll is locked while open. |
| **Delete confirmation** | Native `window.confirm` dialog | Delete (trash) icon button in the editor footer | Browser-native "Delete this validation rule? This cannot be undone." OK/Cancel | OK (proceeds with delete) / Cancel (aborts). Not a styled component. |
| **Notice pill** | Inline toast-like strip (not a true toast) | Successful/failed save, create, delete | "Rule saved." / "Rule created." / "Rule deleted." / "Couldn't save the rule — try again." with a colored dot | Persists until the next row selection or action sets/clears it (`setNotice(null)`); no auto-dismiss, no close button. |

There is **no import/upload panel, no modal dialog on desktop, no dropdown menu, no real toast system, and no popover.** The FOCUS HINT's "rule edit/import panel" maps to the always-on inline `RuleEditor` (edit) — there is no import.

### States
- **Empty:** Handled. Desktop table renders a single full-width cell: "No rules in your catalog yet. Create one to document a check you want to run." Mobile renders the same copy in a white card. (Note: `getRules()` returns `[]` for the live empty case; the message is informative but does not include a prominent CTA beyond the header's +New rule.)
- **Loading:** Handled (live only). Returns a `PageShell` with a pulsing title bar skeleton (`#E5E8EE`, 28×200) + one large pulsing card skeleton (360px tall white card). No bare spinner. (No `loading.tsx` exists for this route — handled in-component.)
- **Error:** Handled (live only). Centered white card: red "Could not load validation rules" + "Check your connection and try again." + a navy **Retry** button calling `refetch()`.
- **Success/feedback:** The green notice pill (above). Save button shows "Saving…" at 0.6 opacity while `saveMutation.isPending`. Important: in live mode the success notice is set in the mutation's `onSuccess` (not synchronously) so a failed save shows the error string instead of a false success.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Two-column split-detail. Content capped at 1480px (`--container-wide`), centered with `34px` gutters. Table on left, sticky editor (340–400px) on right. Enforcement callout visible.
- **Tablet 768:** Still below the `lg` (1024px) breakpoint → **single column**: mobile row-cards stack, the desktop table and inline desktop editor are hidden, and editing happens via the **bottom-sheet**. The `sm:`-only enforcement callout shows; the +New rule button is auto-width.
- **Mobile 390:** Stacked row-cards (name+code, 44×44 toggle, severity pill + scope chip + supplier, description, "Triggered N in the last 30 days" footer). Editor is a full bottom-sheet. Enforcement callout hidden (intentional, so the list isn't pushed below the fold). Form controls grow to 44px tall and 15px text on mobile.
- **Breakpoint cliff:** The editor switches host at **lg (1024px)**, not at the visual table breakpoint, so the 768–1023px range shows mobile cards + sheet while still on a wide-ish viewport — fine, but the desktop table never appears on tablet. The desktop table has `overflow-x-auto`; with 6 columns it can scroll horizontally on narrower desktop widths.

### Current UX issues
- **Token drift / inline-style sprawl (DESIGN BAR 1, 2, 8):** nearly every color, radius, shadow, and font-size is a hard-coded inline hex/px (`#B43838`, `#FBE3E3`, `#EAF0F8`, `#9AA3B5`, sizes 10.5/11.5/12.5/14.5px). Not on the 4/8 spacing scale, not using the semantic CSS variables (`--ink`, `--brand-green`, `--border`) that the rest of the system defines. Severity hexes are duplicated from the global pill classes instead of reusing them.
- **Two parallel status-pill systems (DESIGN BAR 4):** severity uses a bespoke `SEV` map (rounded-full, 22px, dot+word) that doesn't share the global `.pill-*` badge classes; the Scope chip is yet another shape. Editor severity is edited via a 2-option segment (Warning/Critical) that silently collapses the third `info` value — info rules can't be re-selected as info.
- **Honesty tension surfaced but heavy (DESIGN BAR "never show healthy when failing"):** the page correctly admits it's "a catalog, not a gate," but it still shows "Triggered N in last 30 days" stats and severity that imply enforcement. Two near-identical "enforcement is per supplier" explanations (subtitle + callout) repeat the same link, which is redundant.
- **Notice is not a real toast (DESIGN BAR 6):** it's an inline strip with no auto-dismiss and no close affordance; it lingers until the next click. Inconsistent with a proper toast system.
- **Condition box is dead on live data:** the read-only "Condition (WHEN)" shows `description` for live rules (RuleDto has no condition), so it just echoes the description field below it — looks like a real expression but isn't.
- **Toggle accessibility (DESIGN BAR 9):** the `Toggle` is a `role="switch"` button with no visible focus ring styling beyond the global one and no label/`aria-label`; in the table its only context is the column header.
- **Delete uses `window.confirm` (DESIGN BAR confirm-before-destroy):** functional but jarring/native, not matching the styled sheet/dialog language; destructive button is an icon-only trash with only a `title`/`aria-label`.
- **No sortable columns / no `aria-sort` (DESIGN BAR 5):** the table header looks sortable-adjacent but isn't; no filter/search over potentially long catalogs.
- **Editor "code" is fabricated client-side:** `codeFor()` generates codes like `HEA-CU-RR` that look authoritative but are derived, not stored — risks user confusion vs the prettier mock codes.
- **Empty state lacks a strong next action (DESIGN BAR 6/7):** copy is good but there's no in-context primary button; the only CTA is the header +New rule.

### Redesign recommendations (for Claude Design)
1. **Replace inline styles with the design tokens + a shared badge/pill component.** Map severity to ONE status-badge primitive (green/amber/red/neutral, single shape/size/padding, icon+word) reused from the global system; same for the Scope chip. Keep navy `#0B1A2F` + the blue accent for selection, green for the primary Save/CTA. (DESIGN BAR 1, 2, 4, 8.)
2. **Normalize the table to the canonical list density:** one row height, 8px-grid cell padding, `gray-200` gridlines, sticky header, real sortable columns with `aria-sort`, and a search/filter bar (by name, scope, severity, active) — catalogs will grow. Tabular figures for the Triggered count and code. (DESIGN BAR 3, 5.)
3. **Make the "catalog, not a gate" story honest and singular:** collapse the duplicated subtitle + callout into one clear banner; consider visually de-emphasizing trigger stats (or labeling them "observed in review") so the screen never implies it blocks delivery. Keep the single deep-link to the supplier Acceptance tab. (DESIGN BAR "never show healthy when failing".)
4. **Promote one primary action per screen:** keep **+ New rule** as the single dominant primary (green, ≥44px) in the header OR an empty-state CTA; demote it to one place. The editor's Save is the contextual primary inside the panel. (DESIGN BAR 7.)
5. **Fix the severity control to support all three values** (Warning / Critical / Info) as a 3-segment control or radio group, instead of silently dropping `info`. Surface the read-only Condition only when a real machine-readable condition exists; otherwise hide it rather than echoing the description. (DESIGN BAR 2, forms.)
6. **Upgrade feedback + destructive flows:** swap the lingering notice strip for a real auto-dismissing toast; replace `window.confirm` with a styled confirm dialog (scrim, focus trap, animate from trigger, destructive button separated/red-outlined). Give the toggle an explicit `aria-label` and visible pressed state. (DESIGN BAR 6, 9, confirm-before-destroy.)
7. **Strengthen empty + loading:** add an illustrated/iconed empty state with an inline "Create your first rule" button and a one-line link to the supplier Acceptance tab; keep the skeleton but match it to the real two-column layout. (DESIGN BAR 6.)
8. **Mobile sheet polish:** ensure the bottom-sheet animates up from the trigger, keeps Save reachable (sticky footer), and the close affordances (X / Esc / backdrop) are all present (they are — preserve them). Keep the desktop sticky inline editor; it's a good pattern. (DESIGN BAR 10.)

---

### Screenshots — PRODUCTION (8)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/13-base-desktop-1440-library-rules.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/13-base-hd-1920-library-rules.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/13-base-mobile-390-library-rules.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/13-base-tablet-768-library-rules.png)

**mobile-edit-bottom-sheet · mobile-390**

![mobile-edit-bottom-sheet · mobile-390](screenshots-prod/13-mobile-edit-bottom-sheet-mobile-390-library-rules.png)

**mobile-new-rule-sheet · mobile-390**

![mobile-new-rule-sheet · mobile-390](screenshots-prod/13-mobile-new-rule-sheet-mobile-390-library-rules.png)

**new-rule-editor-blank · desktop-1440**

![new-rule-editor-blank · desktop-1440](screenshots-prod/13-new-rule-editor-blank-desktop-1440-library-rules.png)

**rule-selected-editor · desktop-1440**

![rule-selected-editor · desktop-1440](screenshots-prod/13-rule-selected-editor-desktop-1440-library-rules.png)

---

## 14. Rule definitions (validation rule catalog) — `/library/rule-definitions`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/rule-definitions/page.tsx` (thin wrapper — renders `<RuleDefinitionsCatalog />`)
- **Key components:**
  - `src/components/bridge/RuleDefinitionsCatalog.tsx` (the entire page: header, context banner, loading/error/empty states, grouped catalog, the `RuleDefinitionRow` + `SeverityPill` sub-components)
  - `src/components/bridge/StandardsRefList.tsx` (the "Maps to" standards-reference grid + `hasStandardsRefs()` guard, rendered inside an expandable row panel)
  - `src/components/bridge/layout/PageShell.tsx` (wide variant, 1480px max-width, full-height scroll)
  - `src/components/bridge/layout/PageHeader.tsx` (title + subtitle row)
  - `src/components/bridge/layout/Card.tsx` (used for error + empty states)
  - `src/hooks/useQueriesEnabled.ts` (auth/mock query gate)
- **Capture URL (mock):** `/library/rule-definitions` (no ids/query — single static route; mock returns 3 definitions via `mockListRuleDefinitions`)

### What it is & why it exists
This is the org-wide, **read-only** catalog of reusable validation rule *definitions* — the building blocks (e.g. "Order currency is present", "Every line has a supplier item code", "Quantity greater than zero") that a supplier's executable acceptance rules bind to. It sits in the **validate** stage of the parse → normalize → validate → review → transform → deliver → learn loop: it is the reference shelf, not the enforcement surface. Each definition also carries the standards reference it maps to (UBL / EDIFACT / X12 / cXML), which satisfies the project's standards-visibility rule so a 30-year procurement veteran can confirm "this currency check is `cbc:DocumentCurrencyCode` / `CUR02` / `Total/Money/@currency`."

### Who uses it & the primary job
Primarily the **integration expert / operator** setting up suppliers (a procurement coordinator may skim it for orientation). The single most important task is **reference/orientation**: browse the available checks grouped by scope, confirm what each rule means and which standard field it maps to, then go author/enforce them on a specific supplier's Validation rules tab. There is no create/edit/delete here by design — authoring lives per-supplier.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width 1480px; responsive gutter `px-4 sm:px-6 lg:px-[34px]`, vertical `py-5 sm:py-7`):

1. **PageHeader** — `h1` "Rule definitions" (Bricolage Grotesque, 28→30px, weight 600, `-0.02em`). Subtitle is a single 13px muted line concatenating a static sentence + a live count: `"Your reusable validation rule catalog, with the standard each field maps to."` then `"{N} definition(s)"` (joined by a double-space; count omitted while loading/error). No `actions` slot — header is title-only.
2. **Read-only context banner** — a full-width info strip (`mb-4`, `rounded-[8px]`, `px-3.5 py-2.5`, 12px text, border `#D6E2F4`, background `--brand-blue-soft`). Bold lead "Built-in rule definitions." + muted sentence with an inline underlined link **"supplier's Validation rules tab"** → `/library/suppliers`. Stacks vertically on mobile, row on `sm`.
3. **Body — grouped catalog** (`grid gap-4`): one block per scope, ordered **Order-level → Line-level → Header → (other scopes alphabetical)**. Each group block has:
   - A group heading row (`mb-2`): `h2` scope label (13px, weight 600) + a faint count number.
   - A single bordered surface card (`rounded-[12px]`, `--surface`, `1px --border`, `--shadow-card`, `overflow-hidden`) containing the rows for that scope. Rows are divided by a hardcoded `1px solid #F0F2F6` bottom border (NOT a token).

**Row anatomy** (`RuleDefinitionRow`, `flex items-start gap-3 px-4 py-3`):
- Left/main column: title (13px, weight 600, `--ink`) + inline **SeverityPill** + optional **System** chip; second line shows mono `code` · mono `fieldPath operator [expectedValue]` (e.g. `quantity greater_than 0`); optional description paragraph (12px, `--ink-muted`).
- Right: a **Standards** toggle button (28px tall, info-circle Lucide-style icon + word) — only rendered when at least one standards ref is present.
- Expanded panel (when toggled open): a nested `--surface-2` box (`rounded-[8px]`) with an uppercase "Maps to" label and the `StandardsRefList` definition list (`cXML 1.2 / UBL 2.1 / EDIFACT / X12` → mono ref values in a two-column `dl`).

Density/type/spacing observations: heavy reliance on inline `style={{...}}` with **fractional pixel font sizes** (`13px`, `12.5px`, `11.5px`, `10.5px`, `10px`) and CSS-var colors rather than the Tailwind token scale; padding/gaps mix the 4/8 scale (`px-4 py-3`, `gap-3`) with off-scale values (`py-0.5`, `mt-0.5`, `gap-y-0.5`, `pb-3.5`, `px-3.5`).

### Data shown
Entity: **`RuleDefinition`** (mirrors backend `RuleDefinitionDto`). Source: `listRuleDefinitions` → mock `mockListRuleDefinitions` (200ms delay, returns `MOCK_RULE_DEFINITIONS`, 3 rows) or real `GET /api/rule-definitions` (org-scoped server-side; `RuleDefinitionsController.cs`).

Fields displayed per row:
- `title` (e.g. "Order currency is present")
- `defaultSeverity` → SeverityPill (`error` / `warning` / `info`; unknown falls back to neutral)
- `isSystem` → "System" chip when true
- `code` (mono, e.g. `ORDER.CURRENCY.REQUIRED`)
- `fieldPath` + `operator` + `defaultExpectedValue` (mono, e.g. `currency required`, `quantity greater_than 0`)
- `description` (optional paragraph)
- Standards refs `ublRef` / `edifactRef` / `x12Ref` / `cxmlRef` (in the expanded panel)
- `scope` (used for grouping; not shown as a field, surfaced as the group heading)

Fields present in the type but **not displayed**: `id` (key only), `paramHint`, `createdAt`. Mock ids are `rd-1`, `rd-2`, `rd-3`. Mock data: `rd-1` order/currency/required/error; `rd-2` line/supplierItemCode/required/error; `rd-3` line/quantity/greater_than/0/error — so the mock renders an **Order-level** group (1) and a **Line-level** group (2), all severity = error. (A sibling `getSupplierRuleBindings` / `GET /api/suppliers/{id}/rule-bindings` exists for the supplier authoring surface but is NOT used on this page.)

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| "supplier's Validation rules tab" link (context banner) | `next/link` navigate | `/library/suppliers` |
| **Standards** toggle button (per row) | `onClick` toggles local `open` state (`aria-expanded`) | Expands/collapses the inline "Maps to" standards panel below that row. In-place, no navigation. Only present when `hasStandardsRefs()` is true. |
| **↻ Retry** button (error state only) | `refetch()` the TanStack query | Re-runs `listRuleDefinitions`; replaces error with data on success |

No sort controls, no filters, no search, no row click-through, no create/edit/delete, no menus. The page is browse-only.

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Standards "Maps to" panel | Inline expand panel (NOT an overlay — in-flow, per row) | The per-row **Standards** toggle button | `--surface-2` box with "Maps to" label + `StandardsRefList` (cXML 1.2 / UBL 2.1 / EDIFACT / X12 → mono ref values) | Clicking the same **Standards** button again (toggles `open` back to false). No Esc/backdrop — it is in-flow content, not a popover. |

**No true overlays** — the page itself opens NO modal/drawer/sheet/dialog/popover/dropdown/tooltip/toast. The only transient surface is the inline per-row standards disclosure, which is in-document flow (it pushes siblings down), not a floating layer. Navigation to `/library/suppliers` is a plain in-place route change. (The global app shell can open the `HelpSlideover` / SectionGuide drawer for this route from the topbar, but that is owned by the shell, not triggered by anything on this page.)

### States
- **Empty:** Handled. When `total === 0`, renders a `Card` with centered "No rule definitions yet" (16px Bricolage, weight 600) + muted explainer "Your org has no reusable validation rule definitions. They appear here once defined." **Weakness:** no next-action CTA in the empty state (it only explains, doesn't direct the user to `/library/suppliers` or anywhere).
- **Loading:** Handled with a skeleton (not a bare spinner): two `h-44 rounded-[12px] animate-pulse` blocks (`--surface-2`). Shown when `!queryEnabled || (isLoading && data === undefined)` — so it also covers the pre-auth/pre-mock-ready window. Skeleton shape (two tall blocks) does **not** resemble the actual grouped-table layout.
- **Error:** Handled. `Card` with "Couldn't load rule definitions" (`--danger`), muted "This is usually transient.", and an **↻ Retry** button (dark `--ink` fill, 36px tall) calling `refetch()`. Reason is generic ("transient") — does not surface the actual HTTP status from the thrown `rule-definitions: {status}` error.
- **Success/feedback:** No toasts or transient confirmations — success simply renders the grouped catalog and the live "{N} definitions" count in the subtitle. The Standards toggle gives immediate visual feedback (button background flips to `--surface-2` when open).

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Content centered at 1480px max-width with 34px gutters. Single-column stack of group cards (the layout never goes multi-column — rows are full-width within each group card). Lots of empty horizontal space at 1920 since rows are short text + one button.
- **Tablet 768:** Same single-column layout; gutter drops to 24px (`sm:px-6`). Context banner becomes a row (`sm:flex-row`). PageHeader stays title-only.
- **Mobile 390:** Gutter 16px (`px-4`), vertical `py-5`. Context banner stacks (`flex-col`). Header `h1` 28px. Row content wraps via `flex-wrap` on the title line and the code/path line. **Breakpoint risk:** the row uses `flex items-start gap-3` with a `flex-shrink-0` Standards button on the right; long titles/codes wrap in the left column while the button stays pinned — acceptable, but the mono `fieldPath operator value` line can wrap awkwardly and the `#F0F2F6` divider plus dense fractional type read cramped at 390px. No mobile-specific card transform — it is the same table-in-card, just narrower (this page is simple enough that it survives, unlike the mapper).

### Current UX issues
- **Severity pill is a separate, off-spec badge system (DESIGN BAR #4).** `SeverityPill` is `rounded-[4px] px-2 py-0.5 text-[10.5px] uppercase` with its own `SEV_STYLE` map — it does NOT share the app's `UnifiedStatusBadge` shape/size/padding, and it carries meaning by **color + word but no icon**, and the "System" chip is yet another bespoke pill shape. Three different chip treatments on one row.
- **Numbers are not tabular (DESIGN BAR #3).** The subtitle count, per-group counts, and the mono `expectedValue` (e.g. `0`) use default figures; mono helps but no `font-variant-numeric: tabular-nums` is set anywhere.
- **Off-scale spacing + fractional type (DESIGN BAR #1, #2).** Pervasive `0.5` steps (`py-0.5`, `mt-0.5`, `gap-y-0.5`, `pb-3.5`, `px-3.5`) and fractional font sizes (`12.5`, `11.5`, `10.5`, `10`) drift off the 4/8 + clean type scale. Hierarchy is carried partly by tiny size deltas (13 vs 12 vs 11.5px) that compress poorly.
- **Hardcoded colors bypass tokens (DESIGN BAR #5, #8).** Row divider `#F0F2F6` and banner border `#D6E2F4` are literals, not `--border`/gray-200 tokens — inconsistent with the Card's `--border`. Two radii coexist (`rounded-[12px]` group card vs `rounded-[8px]` panels vs `rounded-[4px]`/`rounded-[6px]` chips/button).
- **Standards toggle button is 28px tall — below the 44px hit target (DESIGN BAR #9).** It has `aria-expanded` + `aria-label` (good) and a hover background, but no visible focus-visible ring defined locally and no pressed state beyond the open-background swap.
- **Retry button uses `↻` glyph not a Lucide icon (DESIGN BAR icon consistency).** Same for the loading/empty states using raw text glyphs; the brand uses Lucide elsewhere.
- **Empty state has no next action (DESIGN BAR #6).** It explains but doesn't route the user anywhere — should point to supplier authoring.
- **Skeleton doesn't match layout (DESIGN BAR #6).** Two big `h-44` blocks don't preview the grouped-rows shape, so the loaded layout "jumps."
- **No sort/filter/search affordance.** Even read-only, with a growing catalog there is no way to filter by scope/severity or search by code; grouping is the only organization.
- **Density not the canonical table density (DESIGN BAR #5).** Rows are custom flex blocks inside a card, not the app's standard table (no sticky header, no zebra/hover-row, no aria-sort) — inconsistent with other library list pages.

### Redesign recommendations (for Claude Design)
1. **Unify all three chips onto the single status-badge system.** Replace `SeverityPill` + the "System" pill with the one shared badge (same shape/size/padding) using error→red, warning→amber, info→neutral-blue semantics + a Lucide icon (AlertCircle/AlertTriangle/Info) so severity is never color-alone. Keep navy/violet brand; green stays reserved for success/output, so do NOT color any severity green.
2. **Promote this to the canonical read-only table density.** Render each scope group as a real table section: single row height, consistent cell padding, low-contrast `gray-200` gridlines, row hover, and a sticky group/column header. Even read-only, give it columns (Title · Severity · Field path / operator · Standards) so codes and operators align in tabular columns with `tabular-nums`.
3. **Add a single filter/search bar in the PageHeader `actions` slot.** A scope filter (Order/Line/Header) + severity filter + a code/title search keeps the catalog usable as it grows — this is the one obvious "primary affordance" the page lacks (no destructive primary needed since it's read-only).
4. **Normalize spacing to strict 4/8 and the type scale.** Replace fractional sizes (12.5/11.5/10.5) with the defined scale (label 12/500, body 13/400, meta 11/500), and collapse `0.5`-step paddings to 4/8. Carry hierarchy by size+weight, not by 0.5px deltas.
5. **Replace literal colors with tokens.** Row divider `#F0F2F6` → the same `--border` as the card; banner border `#D6E2F4` → a blue-soft border token. Consolidate to one radius for cards and one for inline panels/chips.
6. **Standards disclosure: keep inline but make it a proper expander.** Bump the toggle to ≥44px hit area, add a chevron that rotates, add focus-visible ring + pressed state, and respect reduced-motion on the expand. Keep it in-flow (not a popover) — that's the right pattern for a reference table.
7. **Make the empty state actionable.** Add a primary CTA "Set up validation on a supplier" → `/library/suppliers` so the nothing-yet state gives the next action (DESIGN BAR #6).
8. **Surface the real error reason + Lucide retry.** Show the HTTP status / a human reason instead of only "transient," and swap the `↻` glyph for a Lucide RefreshCw icon to match the rest of the app.
9. **Reshape the skeleton to the grouped-rows layout** (group heading bar + 3–4 row lines per group) so the loaded view doesn't jump.
10. **Reinforce the read-only → authoring handoff.** Keep the blue context banner but make the link a clear secondary button ("Enforce on a supplier") so the relationship between this catalog and the per-supplier authoring tab is unmistakable.

---

### Screenshots — PRODUCTION (6)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/14-base-desktop-1440-library-rule-definitions.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/14-base-hd-1920-library-rule-definitions.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/14-base-mobile-390-library-rule-definitions.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/14-base-tablet-768-library-rule-definitions.png)

**standards-panel-expanded · desktop-1440**

![standards-panel-expanded · desktop-1440](screenshots-prod/14-standards-panel-expanded-desktop-1440-library-rule-definitions.png)

**standards-panel-mobile · mobile-390**

![standards-panel-mobile · mobile-390](screenshots-prod/14-standards-panel-mobile-mobile-390-library-rule-definitions.png)

---

## 15. Output templates — `/library/templates`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/templates/page.tsx`
- **Key components:**
  - `src/app/(app)/library/templates/page.tsx` — the page + the `TemplatePanel` editor modal + the `Field` + `PreviewLine` helpers (all defined in-file)
  - `src/app/(app)/library/templates/previewModel.ts` — `PREVIEW_BY_FORMAT`, `previewFor()`, `bodyForPreview()` (the per-format envelope skeletons and the export/preview body resolver)
  - `src/components/bridge/layout/PageShell.tsx` — page wrapper (`variant="wide"`, max-width `--container-wide` = 1480px)
  - `src/components/bridge/layout/PageHeader.tsx` — title row ("Output templates" + subtitle + actions slot)
  - `src/components/bridge/EmptyState.tsx` (renders `MarkSystem` from `src/components/bridge/MarkSystem.tsx`) — zero-template state
  - `src/components/bridge/DSPrimitives.tsx` — `SrcChip` (format chip), `Button` (Export / Edit / Save / Cancel / Delete / Retry)
  - `src/app/(app)/library/templates/loading.tsx` — route loader → `BridgePageLoader` (`src/components/bridge/BridgeLoader.tsx`)
  - Data: `getTemplates` / `createTemplate` / `updateTemplate` / `deleteTemplate` / `TemplateDto` from `src/lib/api-client.ts`
- **Capture URL (mock):** `/library/templates` (mock mode renders the 4-card `MOCK_TEMPLATES` array; card `t1` "cXML 1.2.045 — OrderRequest" is auto-selected and shown in the right-hand preview)

### What it is & why it exists
This is the library of **output templates** — the file shape (envelope) each supplier receives when an order is transformed and delivered. It sits in the `transform → deliver` part of the workflow: a template defines the cXML / UBL / EDIFACT / X12 / JSON / CSV body, with `{token}` placeholders that are filled from the canonical order at delivery time. A procurement coordinator opens it to see what format goes out the door, to read/edit a template body, to create a new template for a new supplier standard, or to export/inspect the envelope before assigning it.

### Who uses it & the primary job
Persona: **integration expert / power-user procurement coordinator** (someone who cares which standard a supplier accepts). The single most important task is **opening a template's body editor to view or edit the envelope and `{token}` mappings** — i.e. selecting a card, reading the code preview, then clicking "Edit template" to open the modal body editor and save changes.

### Layout & structure (current)
Top to bottom inside `PageShell variant="wide"` (centered, max 1480px; gutter ramp 16→24→34px, vertical padding 20→28px):

1. **PageHeader row** — `h1` "Output templates" (Bricolage Grotesque, 28/30px, weight 600, `--ink`) with subtitle "The format each supplier receives · N templates" (13px `--ink-muted`). Right-aligned **green "+ New template"** primary CTA (`--brand-green`, white text; 27px tall desktop / 44px full-width mobile).
2. **Notice banner** (conditional) — full-width rounded bar above the grid; green-left-border for success, red-left-border for error. Shown after create/update/delete/export.
3. **Body** — a **two-column split grid**: `lg:grid-cols-[340px_minmax(0,1fr)]`, `gap-4`, `items-start`. Below `lg` it collapses to a single stacked column.
   - **Left column — template cards** (`flex flex-col gap-2`). Each card is a `<button>` (radius 8, `--surface`, 1px `--border`; selected = 1px `SELECT_BLUE`=`--brand-blue` border + `0 0 0 1px` blue ring; hover lifts `-translate-y-[2px]`). Padding `13px 15px 13px 17px`. Each card has a 3px left **accent strip** colored per format family (cXML=violet `#6F4FCE`, UBL=brand-blue, EDI/EDIFACT/X12=amber, JSON=`#A06200`, CSV=ink-muted). Inside: a `SrcChip` (format), an optional green **"Default"** pill (top-right), the template name (13px weight 600), a one-line plain-language description (`FMT_DESC`, 11.5px muted), and a supplier-assignment line (11px faint): "N suppliers: name, name" or italic "Not assigned to a supplier" when count = 0.
   - **Right column — code preview panel** (plain div, radius `--radius-md`=8, 1px `--border`, `--shadow-card`, `self-start`). Three stacked regions:
     - **Header bar** (`px-4 py-3`, bottom border): `</>` glyph + template name (13px weight 600, truncates) on the left; on the right a mono `v{version}` (hidden < sm) and a ghost **"↓ Export"** button.
     - **`<pre>` code body** — the envelope as monospace lines (JetBrains Mono, 11.5px, line-height 1.7, background `#FCFCFD`, text `#345470`). `{token}` segments are highlighted violet (`#6F4FCE`, weight 600) by `PreviewLine`. Horizontally scrollable.
     - **Footer bar** (`px-4 py-3`, top border): a faint hint "`{tokens}` are filled from the order at delivery time" on the left; a secondary **"✎ Edit template"** button on the right (full-width on mobile).

Density/type/spacing observations: spacing is a mix of Tailwind 4px-scale classes (`gap-4`, `px-4`, `py-3`) and many **hand-tuned odd pixel values via inline styles** (card padding `13px 15px 13px 17px`, `marginTop: 8/3/9`, header `py-2.5`, preview `padding 14px 16px`). Font sizes are fractional and ad-hoc (11px, 11.5px, 12.5px, 13px). Colors come from CSS vars but several literals are inlined (`#6F4FCE`, `#FCFCFD`, `#345470`, `#C6CDDA`, `#D5DAEA`, `#0B1A2F`).

### Data shown
Entity: **output template**. Fields per card (left): `name`, `format` (→ `SrcChip` + accent), `version`, `suppliersCount` (+ mock-only `supplierNames[]`), `lastUsed` (defined in mock but **not actually rendered anywhere**), `isDefault` (mock-only → "Default" pill), `config.body`. Preview (right): the resolved body lines from `bodyForPreview()` (authored `config.body` if present, else the static `PREVIEW_BY_FORMAT` skeleton for that format) + `name` + `version`.

Data source: live = `getTemplates()` → `GET /api/templates` (`TemplateDto[]`: `id, name, format, version, suppliersCount, lastUsed, config`), gated by `enabled: !isApiMockMode`. Mock mode (dev only, `NEXT_PUBLIC_USE_MOCK=true`) uses the in-file `MOCK_TEMPLATES` array (4 entries: `t1` cXML/2 suppliers/Default, `t2` UBL/1, `t3` EDIFACT/1, `t4` X12/0/unassigned); `getTemplates` returns `[]` in mock mode but the query is disabled. Mutations: `createTemplate` (`POST /api/templates`), `updateTemplate` (`PUT /api/templates/{id}`), `deleteTemplate` (`DELETE /api/templates/{id}`) — all no-op-to-success in mock mode.

### Interactive elements

| Control | Action | Result/where it goes |
| --- | --- | --- |
| "+ New template" button (header) | `newTemplate()` | Opens `TemplatePanel` modal with a blank `{id:"new"}` template (no Delete button) |
| "+ New template" button (empty state) | `newTemplate()` | Same as above (only visible when 0 templates) |
| Template card (left, each) | `setSelId(t.id)` + clears notice | Selects card → updates right preview panel; no navigation |
| "↓ Export" button (preview header) | `exportTemplate(selected)` | Builds a Blob from `bodyForPreview()`, triggers a browser download (`{name}.{ext}`), shows green "Exported …" notice |
| "✎ Edit template" button (preview footer) | `setEditing(selected)` + clears notice | Opens `TemplatePanel` modal pre-filled with the selected template |
| Retry button (error state) | `refetch()` | Re-runs the `templates` query |
| **Modal — Template name** input | `nameRef` (uncontrolled) | Required; empty → inline validation message |
| **Modal — Standard** `<select>` | `fmtRef` | Options: cXML / UBL / EDI / X12 / JSON / CSV |
| **Modal — Version** input | `versionRef` (mono) | Free text, default "1.0" |
| **Modal — Template body** `<textarea>` | `bodyRef` (dark navy, mono) | The editable envelope; defaults to a cXML snippet for new templates |
| **Modal — Save** button | `handleSave()` | Validates name → calls `createTemplate`/`updateTemplate`, closes modal, fires notice + invalidates query |
| **Modal — Cancel** button | `onClose()` | Closes modal, no save |
| **Modal — Delete** button (edit-only) | `onDelete()` → `deleteMutation.mutate(id)` | Deletes template, closes modal, green notice, invalidates query. **No confirm dialog.** |
| **Modal — × close** button | `onClose()` | Closes modal |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
| --- | --- | --- | --- | --- |
| **Template editor / body editor** (`TemplatePanel`) | Modal (fixed full-screen scrim, centered card desktop / bottom-sheet mobile) | "+ New template" (header), "+ New template" (empty state), or "✎ Edit template" (preview footer) | Header: ▤ blue icon tile + title ("New output template" or the template name) + "The format a supplier receives" subline + × button. Body: intro paragraph, a 3-up field row (Template name / Standard select / Version), a large dark-navy monospace **Template body textarea** (`min-h-180px`), a `{Like_this}` helper note, and (conditionally) a blue validation message. Footer: Delete (edit-only, left) · Cancel · Save (green primary, shows spinner while saving). | × button, Cancel button, or successful Save / Delete (programmatic close via `onClose`/`onSaved`). **No Esc-key handler and no backdrop-click-to-close** — clicking the scrim does nothing. |
| **Success / error notice** | Inline banner (not an overlay; renders in the page flow above the grid) | Save, Delete, Export (success); failed delete/save (error) | One line of feedback text, colored green/red | Auto-replaced/cleared on next card select, new/edit open, or next action. Has **no dismiss control** and **does not auto-dismiss**. |
| **File download** | Browser-native (programmatic `<a download>`) | "↓ Export" | The previewed envelope as a `.xml/.edi/.x12/.json/.csv/.txt` file | N/A (handled by browser) |

No dropdowns, popovers, tooltips, drawers, or toasts. The only true overlay is the `TemplatePanel` modal; everything else is in-place. (Notably the standards-visibility hint is a static footer string, not an info popover.)

### States
- **Empty:** Handled. `EmptyState` with the ProcuLink Mark, title "No output templates", sub "Templates define the format each supplier receives when an order is sent to them.", and a navy "+ New template" action. (Note: the empty-state action button is **navy**, while the header's New-template button is **green** — inconsistent.)
- **Loading:** Handled (two layers). The route `loading.tsx` shows `BridgePageLoader` on first navigation; the in-page `isLoading` branch (live mode only) renders a skeleton — four 104px pulsing card placeholders on the left and a 340px pulsing block on the right (hidden < lg). Good — a real skeleton, not a bare spinner.
- **Error:** Handled (live mode only). A centered red card: "Could not load templates" / "Check the API connection and try again." + a secondary Retry button. Mutation errors surface as the red inline notice ("Delete failed — please retry.", "Save failed — please retry.").
- **Success/feedback:** Inline green notice for create/update/delete/export. Save button shows an inline spinner + "Saving…" label while the mutation is in flight.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Two-column split (`340px` card rail + fluid preview), centered within 1480px. Full header bar with `v{version}` visible. Modal is a centered 680px card with the 3-up field row.
- **Tablet 768:** Below `lg` (1024px) the split grid collapses to **one stacked column** — cards first, then the preview panel below. So at 768 the preview is full-width under the card list. `v{version}` (hidden < sm=640) still shows. Modal still centered.
- **Mobile 390:** Single stacked column. Header CTA becomes full-width 44px. Card list stacks. Preview footer's hint + "Edit template" stack vertically (`flex-col`), Edit button full-width. The modal becomes a **bottom sheet** (`items-end`, `rounded-t-10`, `max-h-92vh` scroll); its field row stacks; footer buttons stack full-width. `v{version}` is hidden.
- Breakpoint notes: the split only exists at `lg`+, so 768–1023px loses the side-by-side affordance (the preview is far below the cards — you must scroll past all cards to see the selected body). No drag canvas here, so no mapper-style mobile cliff.

### Current UX issues
- **Spacing drift (Bar 1):** padding/margins are a grab-bag of off-scale inline pixels (`13px 15px 13px 17px`, `marginTop: 8/3/9`, `py-2.5`, `14px 16px`) instead of one 4/8 rhythm. Card internal padding is asymmetric for no clear reason.
- **Type scale drift (Bar 2):** font sizes span 11 / 11.5 / 12 / 12.5 / 13 / 13.5px with hierarchy partly carried by faint grays (`--ink-faint` #98A0AE on white is borderline for the supplier line and `v{version}`), risking sub-4.5:1 contrast.
- **Two competing accent systems (Bar 4/7):** selection + the modal's primary intent use **brand-blue**, but the page's primary CTA and the "Default" pill use **brand-green**, and format accents add violet/amber. The empty-state action is **navy** while the header action is **green** — no single primary-action color story.
- **No status-badge consistency (Bar 4):** the "Default" marker is a one-off green pill (height 22, radius 4) that doesn't match `Pill`/`UnifiedStatusBadge`. The format `SrcChip` is the only badge that's systemized.
- **Numbers aren't tabular (Bar 3):** `v{version}`, supplier counts, and `lastUsed` use default figures; columns/labels can jitter. `lastUsed` is defined in mock data but never rendered — dead field.
- **Modal accessibility gaps (Bar; modals):** no `Esc`-to-close, no backdrop-click-to-close, no focus trap, no `role="dialog"`/`aria-modal`, and it does not animate from its trigger. The × is a custom 32px button (under the 44px target).
- **Destructive action unguarded (Bar; destructive):** Delete fires immediately with no confirm step and sits in the same footer row as Cancel/Save (only separated by `mr-auto`).
- **Inputs are uncontrolled refs with thin affordances:** border `#C6CDDA`, `h-10`; labels are tiny uppercase 11px faint — visible but low-emphasis. No helper text under fields, validation appears as a blue (not red/amber) box, which mis-signals an error as informational.
- **Honesty gap (Bar; offer⇔works):** the body preview/export is an **illustrative skeleton**, not the actual transform output for a real order — fine as a demo but the panel reads like the real envelope. The standards mapping (which `{token}` ↔ UBL `cbc:ID` / X12 `BEG03`) is implied by the raw code, not surfaced as field-level standards visibility.
- **Notice has no dismiss / no auto-timeout:** it lingers until another action, and it renders mid-flow (pushing the grid down) rather than as a consistent toast.
- **768–1023px usability:** the preview sits far below a tall card list (no side-by-side until 1024), so on tablets selecting a card then finding its body requires a long scroll.

### Redesign recommendations (for Claude Design)
1. **Make the body editor the hero, in a proper modal.** Add Esc-to-close, backdrop-click-to-close (with unsaved-changes guard), focus trap, `role="dialog"`/`aria-modal`, animate-from-trigger, and a 44px close target. Give the textarea real affordances: monospace with line numbers, `{token}` syntax highlighting (reuse `PreviewLine`'s violet), and a live mini-preview pane so authoring and result sit side-by-side.
2. **Resolve the primary-action color conflict.** Pick ONE in-app primary = brand-green for the dominant CTA per screen (header "New template" and modal "Save"); demote selection/intent accents to outline/ghost. Make the empty-state action green too (match the header).
3. **Unify the status/marker system (Bar 4).** Replace the bespoke "Default" pill with the shared `Pill`/badge primitive (one shape, size, padding, icon+word). Consider a small per-card "assigned / unassigned" badge using the same system instead of italic gray text.
4. **One spacing rhythm + one type scale (Bars 1, 2).** Convert all inline odd pixels to the 4/8 scale; collapse font sizes to heading 600 / label 500 / body 400 at fixed steps; raise the supplier line and `v{version}` above 4.5:1 (drop `--ink-faint` for `--ink-muted`). Use tabular figures for version, counts, and any timestamp.
5. **Guard destructive delete (Bar; destructive).** Move Delete to a separated zone and add a confirm step ("Delete template — assigned to N suppliers?") so it can't fire by accident, especially since it's adjacent to Save on mobile.
6. **Surface real standards visibility (CLAUDE.md standards rule).** Next to each `{token}` (or as a hoverable legend), show the canonical field name and its mapping (e.g. `{po}` = Order number → UBL `cbc:ID` / X12 `BEG03` / cXML `orderID`). Lead with the human field name, not the raw tag.
7. **Be honest about preview vs real output (Bar; offer⇔works).** Label the code panel as an "Example envelope — filled from a real order at delivery"; if a sample order exists, offer a "Preview with sample order" toggle that shows actual filled values vs `{tokens}`.
8. **Fix the 768–1023 layout.** Keep the card rail + preview side-by-side from `md` (or make the preview a sticky drawer on tablet) so selecting a card doesn't bury its body below a long list.
9. **Promote the notice to a consistent dismissible toast** (one elevation tier, auto-timeout + manual close), and use red/amber for true errors — switch the modal's validation box from blue to the danger token with the message below the offending field.
10. **Add visible labels + helper text to modal fields** (what "Standard" and "Version" mean), keep focus-visible rings, and ensure every interactive control (×, Export, Edit, card) has a 44px hit area and visible hover/pressed states; add `aria-label`s to icon-only/glyph buttons (`</>`, ↓, ✎, ×).

---

### Screenshots — PRODUCTION (8)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/15-base-desktop-1440-library-templates.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/15-base-hd-1920-library-templates.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/15-base-mobile-390-library-templates.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/15-base-tablet-768-library-templates.png)

**template-body-editor-modal-edit · desktop-1440**

![template-body-editor-modal-edit · desktop-1440](screenshots-prod/15-template-body-editor-modal-edit-desktop-1440-library-templates.png)

**template-body-editor-modal-mobile-sheet · mobile-390**

![template-body-editor-modal-mobile-sheet · mobile-390](screenshots-prod/15-template-body-editor-modal-mobile-sheet-mobile-390-library-templates.png)

**template-body-editor-modal-new · desktop-1440**

![template-body-editor-modal-new · desktop-1440](screenshots-prod/15-template-body-editor-modal-new-desktop-1440-library-templates.png)

**template-export-success-notice · desktop-1440**

![template-export-success-notice · desktop-1440](screenshots-prod/15-template-export-success-notice-desktop-1440-library-templates.png)

---

## 16. Buyers (reference library) — `/library/buyers`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/library/buyers/page.tsx`
- **Key components:**
  - `src/components/bridge/layout/PageShell.tsx` (wide variant, 1480px container)
  - `src/components/bridge/layout/PageHeader.tsx` (title "Buyers" + count subtitle + actions slot)
  - `src/components/bridge/layout/Card.tsx` (create panel + table card)
  - `src/components/bridge/layout/MobileListRow.tsx` (mobile stacked card row)
  - `src/components/bridge/DSPrimitives.tsx` → `Button` (variant `blue`, which is actually green)
  - `src/components/bridge/EmptyState.tsx` → `MarkSystem` (`src/components/bridge/MarkSystem.tsx`)
  - `src/components/bridge/BridgeLoader.tsx` → `BridgePageLoader` (route `loading.tsx`)
  - Local in-file helpers: `BuyerIcon`, `ChannelPill`, `SkeletonTrow`, `SkeletonCard`, `MobileField`
- **Capture URL (mock):** `/library/buyers` (the page reads `isApiMockMode`; in mock mode it renders 3 seeded buyers — `Example Buyer 1 / 2 / 3` from the in-file `MOCK_BUYERS`, NOT the api-client mock names). No detail route exists; rows navigate to `/inbox?buyer=HEI` etc.

### What it is & why it exists
This is the **Buyers reference list** — the directory of the organizations that *send* ProcuLink purchase orders (ProcuLink runs inbound: a buyer is the upstream party whose POs land in the inbox). It is the "learn" / setup memory of the loop: each buyer is the entity ProcuLink fingerprints a layout against so it can auto-parse the next PO from that sender. A procurement coordinator opens it to register a new sender ("a buyer that sends you purchase orders"), to see at a glance how many orders each buyer has sent and in what format, or to jump into the inbox filtered to one buyer's orders.

### Who uses it & the primary job
**Procurement coordinator / operator.** The single most important task is **registering a new buyer** (name + short code) so ProcuLink can start recognising that sender's PO layout — the page's intro note states "After creating, upload a sample PO and ProcuLink learns the buyer's layout automatically." Secondary jobs: scan order volume per buyer and drill into the inbox for one buyer.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (max-width 1480px, gutter 16→24→34px, vertical pad 20→28px, background `var(--bg)`):

1. **PageHeader** — `h1` "Buyers" (Bricolage Grotesque, 28→30px, weight 600, letter-spacing -0.02em) over a 13px muted subtitle that doubles as a count: `"3 buyers · where every order starts"` (or `"Loading…"` while fetching). Right-aligned **action slot** holds the single primary button.
2. **Primary action button** — `Button variant="blue" size="md"` labeled **"New buyer"** with a plus icon. It is a TOGGLE: clicking flips `addOpen`; while the panel is open the same button relabels to **"Cancel"**. Note: `variant="blue"` resolves to brand-GREEN (`#2E8E3A`) per `DSPrimitives` BUTTON_VARIANT — the only blue here is the icon tiles, not the button.
3. **Create-buyer panel** (conditional, `addOpen`) — a `Card` (`mb-[18px]`, 18px padding, 8px radius, 1px border, `--shadow-card`). Contains: panel title "New buyer" (15px/600) + sub "A buyer that sends you purchase orders"; a row that is `flex-col gap-3` on mobile and `sm:flex-row sm:items-end` on desktop with **Buyer name** input (flex:1), **Short code** input (120px, mono, auto-uppercased, maxLength 10), and a green **"Create buyer"** button (full-width on mobile); a buyer-blue info callout ("After creating, upload a sample PO and ProcuLink learns the buyer's layout automatically."); and an inline error paragraph when validation/mutation fails.
4. **Table card** — a single `Card` with `overflow-hidden !p-0` wrapping two mutually-exclusive renderings:
   - **Desktop/tablet (`hidden sm:table`)**: a real `<table>` with `<colgroup>` widths — Buyer (fluid), Primary format (170px), Orders all time (150px), Last order (130px), chevron (44px). Header row is 10.5px uppercase 0.06em-tracked `--ink-faint` labels with a `--border` bottom rule. Body rows are 14px/18px padded, full-row clickable, hover-tinted `--brand-blue-soft`.
   - **Mobile (`sm:hidden`, `flex-col gap-2 p-3`)**: `MobileListRow` cards — identity row (icon + name + mono code + delete button) plus a 2-column grid of labelled fields (Primary format / Orders all time / Last order).

There is no toolbar, no search, no filter, no sort, no pagination, and no footer/action bar.

### Data shown
**Entity:** `BuyerDto` (`src/types/procurement.ts`): `id`, `name`, `code`, `orderCount: number`, `lastOrderAge: string | null`, `formats: string[]`.

**Columns / fields rendered:**
- **Buyer** — soft-blue 32px rounded icon tile (building glyph) + buyer `name` (13.5px/600) + `code` below (10.5px mono, grey `#9196A5`).
- **Primary format** — `formats[0]` rendered as a `.chip` (`ChannelPill`: 22px tall, surface-2 fill, `--ink-muted` text); em-dash if `formats` is empty.
- **Orders (all time)** — `orderCount.toLocaleString()`, right-aligned, mono, 15px/600.
- **Last order** — `lastOrderAge` relative string (e.g. "2m", "14m", "1h"); em-dash + fainter color when null.
- **chevron** — resting right chevron (`#A4ADBD`); a delete (×) button is revealed on row hover just left of it.

**Source:** `getBuyers()` in `src/lib/api-client.ts` → `GET /api/buyers`. Mutations: `createBuyer(name, code)` → `POST /api/buyers`; `deleteBuyer(id)` → `DELETE /api/buyers/{id}`. The query is `enabled: !isApiMockMode`; in mock mode the page substitutes the **in-file** `MOCK_BUYERS` array (Example Buyer 1/2/3), which differs from the api-client's own mock (Heinrich Industries / Nordmark / Steelhouse). Note: `updateBuyer(id, name, code)` → `PUT /api/buyers/{id}` EXISTS in the api-client but is **not imported or used** by this page — there is no edit affordance.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| "New buyer" / "Cancel" button (header) | `setAddOpen(v => !v)` + clears error | Toggles the inline create panel open/closed |
| Buyer name input | `setAddName`; on focus paints blue ring (inline style), Enter triggers save | Local state; no autosave |
| Short code input | `setAddCode(value.toUpperCase())`, maxLength 10, mono; Enter triggers save | Local state; force-uppercased |
| "Create buyer" button (panel) | `handleSaveAdd()` → validates non-empty name+code → `createMut.mutate` | On success: invalidates `["buyers"]`, closes panel, clears fields. On error: sets `addError` |
| Table row (desktop) | `router.push(\`/inbox?buyer=${b.code}\`)` | Navigates to **inbox filtered by buyer code** (title tooltip: "Filter inbox to orders from this buyer") |
| MobileListRow (mobile) | same `router.push(/inbox?buyer=…)`; Enter/Space keyboard-operable | Navigates to filtered inbox |
| Row delete (×) button | `handleDelete` → `e.stopPropagation()` → `window.confirm(...)` → `deleteMut.mutate(id)` | Native browser confirm; on confirm deletes + invalidates list |
| Chevron icon | decorative only (no own handler; row click handles nav) | — |
| "Retry" link (error state) | `refetch()` | Re-runs the buyers query |
| EmptyState "New buyer" action | `setAddOpen(true)` | Opens create panel |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Create-buyer panel | Inline panel (in-flow `Card`, NOT a modal/drawer) | "New buyer" header button, or the EmptyState "New buyer" action | Title + sub, Buyer name input, Short code input, "Create buyer" button, blue info callout, inline error text | "Cancel" (same toggle button), or a successful create (`onSuccess` sets `addOpen=false`). **No X, no Esc, no backdrop** — there is no scrim and Esc does nothing. |
| Delete confirmation | Native `window.confirm()` dialog | Row delete (×) button (desktop hover-revealed; mobile always-visible) | Browser-chrome text: `Delete buyer "{name}"? This cannot be undone.` | OK (proceeds to delete) / Cancel (aborts) — browser-controlled, not styled |
| Row delete (×) button itself | Hover-revealed inline control (desktop) | Hovering a desktop table row (`opacity 0→1`, `pointerEvents` toggled) | Single × icon button | Mouse leaves the row (fades back to opacity 0) |

There are **no toasts, no drawers, no sheets, no popovers, no dropdown menus, and no styled modals** on this page. The only true overlay is the unstyled native `window.confirm`. All other "transient" surfaces are in-flow (the create panel) or hover-state reveals. Success/failure of create + delete is communicated only by list refresh and (for create errors) an inline `<p>` — there is no toast confirming "Buyer created" or "Buyer deleted."

### States
- **Empty:** Handled — when `buyers.length === 0` (and not loading/error) the `EmptyState` renders inside the table card: ProcuLink mark, Bricolage title "No buyers yet", sub "A buyer is an organization that sends you purchase orders, in whatever format they use.", and a navy "New buyer" action button (note: this empty-state button is **navy `#0B1A2F`**, inconsistent with the green header "New buyer").
- **Loading:** Two layers. (1) Route-level `loading.tsx` → `BridgePageLoader label="Loading buyers…"` (animated buyer→supplier wire mark over `#F6F7FA`). (2) In-page skeletons while the query is loading and not in mock mode: 3× `SkeletonTrow` (desktop) / 3× `SkeletonCard` (mobile), pulsing `--surface-2` bars. The header subtitle shows "Loading…".
- **Error:** Handled — when `isError` and not mock, the table body shows a centered single-cell (`colSpan=5`) "Failed to load buyers. **Retry**" with a brand-blue text button calling `refetch()`; mobile shows the same message. No error reason/detail is surfaced beyond "Failed to load buyers."
- **Success/feedback:** Minimal — create success silently closes the panel + clears fields + refetches; delete success silently refetches. Create errors show an inline red `<p>` (and inline "Name/Code is required." validation). The create button shows a spinner + "Creating…" while pending. There is **no positive toast/confirmation** for either action.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Content centered at 1480px max-width. Full data table; chevron column 44px; delete × hidden until row hover.
- **Tablet 768:** Still the `sm:table` desktop table (breakpoint is `sm` = 640px, so 768 already shows the table). Create panel goes horizontal (`sm:flex-row`), inputs shrink to 34px height; short-code field fixed 120px.
- **Mobile 390 (< 640px):** Table swaps to stacked `MobileListRow` cards (`sm:hidden`), each with a 2-column field grid; delete × is always visible (40px tap target) instead of hover-revealed; create-panel row stacks vertically with full-width inputs (44px tall) and a full-width green button; header action button wraps below the title.
- **Breakpoint cliffs:** The single `sm` (640px) breakpoint is the only switch — there is no intermediate density between 640px and 1480px, so on a 1440px screen the table is very sparse (5 columns, lots of whitespace, a 44px-wide trailing chevron column). The header→table transition is clean; no known broken state.

### Current UX issues
- **Misleading primary-button color (DESIGN BAR #4/#7):** The header "New buyer" uses `variant="blue"` which actually renders **green** (`#2E8E3A`), while the EmptyState's "New buyer" renders **navy** (`#0B1A2F`). Two different "New buyer" buttons in two different colors for the identical action.
- **Pervasive magic-number sizing breaks the 4/8 rhythm (DESIGN BAR #1):** Fractional pixel sizes everywhere — `fontSize: 12.5 / 13.5 / 11.5 / 10.5`, `height: 34`, gaps of 13px, padding `14px 18px`, input height `34px`. None of this is on the 4/8 scale; type sizes drift across the page.
- **Type hierarchy carried partly by color and ad-hoc sizes (DESIGN BAR #2):** Header labels are `--ink-faint` uppercase 10.5px; the buyer code is `#9196A5` (a hardcoded grey literal, no token) — likely below 4.5:1 on white. Three named literals (`CODE_GREY`, `BORDER_STRONG`, `CHEVRON`) exist precisely because they have no design token.
- **No styled confirm / destructive pattern (DESIGN BAR: confirm-before-destroy):** Delete uses raw `window.confirm` — unbranded, not focus-trapped, and inconsistent with the rest of the app. Destructive action isn't visually separated; it sits inline next to the navigational chevron.
- **Inline-style focus rings instead of `:focus-visible` (DESIGN BAR #9):** Inputs paint their focus ring via `onFocus`/`onBlur` inline style mutation, not a CSS `:focus-visible` rule; row hover is JS state (`hoverRow`) rather than CSS `:hover`, and there is no visible keyboard focus state on table rows (only mobile cards are keyboard-operable).
- **No table affordances (DESIGN BAR #5):** No sortable headers / `aria-sort`, no sticky header, no zebra/row-rule consistency beyond a flat bottom border, no pagination — fine for 3 buyers, weak at scale.
- **No edit path:** `updateBuyer` exists in the API but the UI offers create + delete only. A user who mistypes a name/code must delete and recreate (the FOCUS HINT's "edit buyer panel" is not implemented).
- **No positive feedback (DESIGN BAR #6):** Create and delete give no toast/inline success; the only signal is the list quietly changing.
- **"Primary format" is a single value with no meaning of "primary":** It just shows `formats[0]`; multiple formats are hidden, and there's no tooltip/expansion.
- **Inline `style={{}}` everywhere** instead of the design-system tokens/classes — the page largely bypasses the shared primitives' styling, making it the odd-one-out vs. neighbouring library pages.

### Redesign recommendations (for Claude Design)
1. **Unify the create surface and add edit (highest impact).** Make "New buyer" open ONE styled overlay (a right `Sheet`/`Drawer` or a centered `Dialog`) with scrim, X, Esc-to-close, and focus trap — reused for **both create and edit** (wire the existing `updateBuyer`). Add a row "Edit" action (kebab menu or pencil) alongside delete. Keep buyer-blue accent for the buyer entity.
2. **One primary-action color, one button.** The header CTA and the empty-state CTA must be the same green primary (`--brand-green`, ≥44px, dominant). Stop using `variant="blue"`/navy for the same "New buyer" action; demote any secondary to outline/ghost.
3. **Replace `window.confirm` with a branded destructive confirm dialog** (red confirm, named buyer, "cannot be undone", focus-trapped), and add a success toast for create/delete/edit. Separate the destructive control from the navigational chevron.
4. **Normalize the type + spacing scale.** Collapse 10.5/11.5/12.5/13.5px into the system scale (e.g. label 12/500, body 13/400, heading 600), put all padding/gaps on 4/8 (e.g. 16px cell padding, 8px gaps), and replace the three hardcoded grey literals (`CODE_GREY`, `BORDER_STRONG`, `CHEVRON`) with tokens that meet 4.5:1.
5. **Tabular figures + real table affordances (DESIGN BAR #3/#5).** Keep mono/`tabular-nums` on `orderCount` (good already) and apply it to `lastOrderAge`; add sortable headers with `aria-sort` (sort by orders / last activity / name), a sticky header, low-contrast `gray-200` row rules, and a consistent single row height.
6. **CSS-driven states over JS hover (DESIGN BAR #9).** Move row hover to CSS `:hover`, add a visible `:focus-visible` ring to table rows (make rows keyboard-operable like the mobile cards), and convert input focus rings to a CSS rule rather than inline `onFocus` mutation.
7. **Make "Primary format" honest.** Either show all `formats` as a chip cluster (capped + "+N") using the canonical `SrcChip` palette, or label the column "Formats" — and surface the standards mapping per the standards-visibility rule when relevant.
8. **Add a search/filter toolbar** above the table for when buyer count grows (name/code search), with the same density as other library pages.
9. **Strengthen the empty + first-run story.** The empty state is good; add a secondary "Upload a sample PO" hint that mirrors the create-panel callout, so the learn-loop intent ("ProcuLink learns the buyer's layout") is reinforced at zero state.
10. **Drop inline styles for shared primitives.** Re-base the table/cards on the canonical `Card`, token spacing, and a shared list/table component so this page matches `/library/suppliers` and the rest of the library cluster.

---

### Screenshots — PRODUCTION (8)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/16-base-desktop-1440-library-buyers.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/16-base-hd-1920-library-buyers.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/16-base-mobile-390-library-buyers.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/16-base-tablet-768-library-buyers.png)

**create-buyer-panel · desktop-1440**

![create-buyer-panel · desktop-1440](screenshots-prod/16-create-buyer-panel-desktop-1440-library-buyers.png)

**create-buyer-panel-mobile · mobile-390**

![create-buyer-panel-mobile · mobile-390](screenshots-prod/16-create-buyer-panel-mobile-mobile-390-library-buyers.png)

**create-buyer-panel-validation-error · desktop-1440**

![create-buyer-panel-validation-error · desktop-1440](screenshots-prod/16-create-buyer-panel-validation-error-desktop-1440-library-buyers.png)

**row-hover-delete-reveal · desktop-1440**

![row-hover-delete-reveal · desktop-1440](screenshots-prod/16-row-hover-delete-reveal-desktop-1440-library-buyers.png)

---

## 17. Standards reference — `/library/standards`

- **File:** `src/app/(app)/library/standards/page.tsx`
- **Key components:**
  - `src/components/bridge/layout/PageShell.tsx` (wide variant — 1480px container)
  - `src/components/bridge/layout/PageHeader.tsx` (title + subtitle + actions slot)
  - `src/components/bridge/EmptyState.tsx` (compact no-results state; renders `MarkSystem`)
  - `src/lib/standards/catalog.ts` (the typed `FIELD_STANDARDS` data + `CanonicalFieldStandards` type — the page's only data source)
- **Capture URL (mock):** `/library/standards`

### What it is & why it exists
This is the conservative "offer ⇔ works" source of truth: a single cross-format reference table showing how each canonical PO field (PO number, order date, buyer, currency, line number, quantity, unit price, etc.) maps to its element/segment path across cXML 1.2, UBL 2.1, EDIFACT, X12, and Peppol BIS. It is not part of an order's live workflow — it sits in the Library as always-on standards documentation. ProcuLink's product rule is that standards visibility is never gated behind a mode, so this screen makes the canonical-model join key auditable to anyone who needs to trust a transform.

### Who uses it & the primary job
Primary persona is the integration expert / 30-year procurement veteran verifying that ProcuLink's field mapping matches the standard a specific supplier expects (e.g. "does PO number really land at cXML `OrderRequestHeader/@orderID` and EDIFACT `BGM 1004`?"). The single most important task: look up a canonical field and read its reference path in the target format, optionally filtering by typing a field name or a standards path.

### Layout & structure (current)
Top-to-bottom, inside a `PageShell variant="wide"` (max-width `var(--container-wide)` = 1480px; gutter ramp `px-4 → sm:px-6 → lg:px-[34px]`, vertical `py-5 → sm:py-7`, page canvas `var(--bg)` #F6F7FA):

1. **PageHeader row** — title "Standards reference" (Bricolage Grotesque, 28→30px, weight 600, `-0.02em`, `var(--ink)` #0B1A2F) over the subtitle "How every order field maps across formats — always visible, never hidden" (13px, `var(--ink-muted)` #5E6779). The header's `actions` slot (right-aligned on `sm`, wraps below the title on mobile) holds the **search input**.
2. **Search input** (in the header actions) — a `<label>` pill: `h-10 w-full` on mobile, `sm:h-8 sm:w-[240px]` on desktop; `var(--surface)` background, `1px var(--border)` border, `var(--radius)` (6px), `0 11px` padding. Inline 15×15 magnifier SVG (stroke `var(--ink-faint)` #98A0AE) + a transparent `<input>` (12.5px, `var(--ink)`), placeholder "Search fields or paths…", `aria-label="Search fields or paths"`.
3. **Single white card** — a plain `<div>` (NOT the `Card` primitive — the code comment explains the table needs zero internal padding for edge-to-edge layout): `var(--surface)` #FFFFFF, `1px var(--border)` #E5E8EE, `var(--radius-md)` (8px), `box-shadow var(--shadow-card)` (`0 1px 2px rgba(11,26,47,0.04)`), `overflow-hidden`.
   - Inside: `overflow-x-auto` wrapper around a `<table>` (`w-full min-w-[760px] border-collapse`).
   - **thead**: one header row. First cell "Canonical field" is `sticky left-0 z-10`, `min-width:180`, with `background var(--surface)`; the other 5 are the reference columns in this exact order: **cXML 1.2 · UBL 2.1 · EDIFACT · X12 · Peppol BIS** (note: cXML-first, defined locally in `REF_COLUMNS`, which intentionally differs from the catalog's `STANDARD_REF_COLUMNS` order that puts UBL first). All header cells: 10.5px, weight 600, uppercase, `tracking-[0.05em]`, color `var(--ink-faint)`, `px-3 py-[9px]`, `1px var(--border)` bottom border, `white-space:nowrap`.
   - **tbody**: one row per canonical field. First cell is sticky (`sticky left-0 z-10`, `var(--surface)` bg) and stacks two lines: the human label (12.5px, weight 600, `var(--ink)`, e.g. "PO number") above the C# canonical field name in mono (10.5px, `var(--font-mono)`, `var(--ink-faint)`, e.g. "PoNumber"). The 5 reference cells: 11px, `var(--font-mono)`, `var(--ink-muted)`, `px-3 py-[11px]`, `white-space:nowrap`, value or "—" if absent. Each row has a bottom `1px var(--border)` divider except the last; full-row hover tints the reference cells (`group-hover:bg-[var(--surface-2)]` #F1F3F7 — note the sticky label cell does NOT get the hover tint, so hover is visually asymmetric).
4. **Request-a-format footer** — `mt-[14px]`, flex-wrap: faint prompt "Need a standard we don't list?" (11.5px, `var(--ink-faint)`) + a ghost link "Request a format" (`<a href="/support">`, 11.5px weight 600, `var(--ink-muted)`, h-8, inline mail-envelope SVG, `hover:bg-[var(--surface-2)]`).

Density/type observations: the page mixes many bespoke fractional sizes (10.5 / 11 / 11.5 / 12.5 / 13px) and bespoke paddings (`py-[9px]`, `py-[11px]`, `mt-[14px]`) rather than a 4/8 scale. Heavy reliance on inline `style={{...}}` with raw CSS-var strings instead of Tailwind utilities/`Card`.

### Data shown
A single entity: **`CanonicalFieldStandards`** rows from the static constant `FIELD_STANDARDS` in `src/lib/standards/catalog.ts`. No API, no hook, no network — the data is a hand-transcribed typed constant (sourced from `ProcuLink/docs/standards-matrix.md`). There are **11 rows** total: header-scope fields `PoNumber, OrderDate, BuyerName, Currency, Lines` and line-scope fields `LineNumber, BuyerItemCode, Description, Quantity, Unit, UnitPrice`. Displayed fields per row: `label`, `canonicalField`, and the 5 reference strings `cxml`, `ubl`, `edifact`, `x12`, `peppolBis`. The richer per-standard support matrix in the same file (`STANDARDS` array, with `parse`/`transform`/`conformance`/`transport`/`referenceUrl`/support-level badges) is **NOT** rendered on this page at all — only the field-mapping table is. The `scope` ("header" vs "line") field exists in the data but is **not** surfaced (no grouping/sectioning on screen).

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| Search input (`#q`, header actions) | Type a query | Client-side filter: lowercased substring match against `label`, `canonicalField`, and all 5 reference values; table re-renders to matching rows live (no submit). Stays on page. |
| Table column headers | None | Static `<th>` — no sort, no `aria-sort`, not clickable. |
| Table rows / cells | Hover only | `group-hover` tints the 5 reference cells (`--surface-2`); rows are not clickable, no row action, no detail/expand. |
| "Request a format" link | Click | Navigates (full `<a href>`) to `/support`. |

### What opens / what closes

**No overlays — navigates in place.** This page opens no modal, drawer, sheet, dialog, popover, dropdown, tooltip, or toast. The only "transient" surface is the inline no-results `EmptyState` rendered conditionally inside the card (not an overlay). The "Request a format" control is a plain anchor navigation to `/support`, not an overlay. (Historically the catalog comments reference a `StandardsFieldPopover` that consumes the same data, but no such component file exists in this repo, and this page never renders one.)

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| No-results message | Inline panel (not an overlay) | Search query matching zero rows | `EmptyState compact` — brand Mark + "No fields match" + `Nothing for "{q}".` | Clearing/changing the search so ≥1 row matches |

### States
- **Empty:** There is no true "no data" state — `FIELD_STANDARDS` is a static non-empty constant, so the table always has 11 rows. The only empty-like state is **no search match**, which renders `<EmptyState compact title="No fields match" sub={'Nothing for "{q}".'} />` *below* the (now header-only) table inside the card. Note: the `<thead>` still renders above the empty state, so on a no-match you see column headers and then the centered empty mark — slightly awkward.
- **Loading:** None. No `loading.tsx` exists in the route folder and there is no async data, so there is no skeleton or spinner — the page is fully static and renders immediately.
- **Error:** Not handled / not applicable — no fetch can fail. If `FIELD_STANDARDS` were ever empty the table body would simply be empty with headers showing (no guard for that case).
- **Success/feedback:** None — read-only reference screen. The only feedback is live row filtering and hover tint.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Content centered in the 1480px wide container; table is well within width so no horizontal scroll; search input is the compact `sm:w-[240px] sm:h-8` pill aligned right in the header.
- **Tablet 768:** Same desktop header layout (search still right-aligned at the `sm` breakpoint, which is 640px). The table's `min-w-[760px]` is right at the edge of usable width minus gutters, so the `overflow-x-auto` may begin to scroll horizontally; the first "Canonical field" column stays sticky-left while the 5 reference columns scroll.
- **Mobile 390:** PageHeader stacks (title/subtitle, then actions below). Search input becomes full-width `h-10`. The table forces horizontal scroll (`min-w-[760px]` >> 390px viewport); the sticky first column keeps the field anchored while you scroll the standards columns sideways. This is the intended "scroll the codes, keep the field" behavior, but it is still a wide horizontal scroller on a phone (not a stacked card list).

No hard breakpoint cliff, but the mobile experience is a side-scrolling spreadsheet rather than a stacked, mobile-native layout.

### Current UX issues
- **Not on a 4/8 spacing rhythm (Bar 1).** Bespoke odd values everywhere: `py-[9px]`, `py-[11px]`, `mt-[14px]`, `px-[10px]`, header `gap-x-3 gap-y-2`. Drift from the strict 4/8 scale.
- **Fractional type scale, not one scale (Bar 2).** 10.5 / 11 / 11.5 / 12.5 / 13 / 28 / 30px coexist. Hierarchy is partly carried by color (`--ink` vs `--ink-muted` vs `--ink-faint`) and the faint reference cells (`--ink-muted` #5E6779 mono on white is fine; `--ink-faint` #98A0AE used for the canonical sub-label and headers is borderline for small text contrast).
- **No tabular figures (Bar 3).** Reference paths contain numbers (`BGM 1004`, `PO101`, `C507/2380`, `cXML 1.2`); they're mono so alignment is OK, but the design system's tabular-figure rule isn't explicitly applied and version labels ("cXML 1.2", "UBL 2.1") sit in headers without consistent figure treatment.
- **Headers look sortable but aren't (Bar 5).** No `aria-sort`, no sort affordance, no hover on headers — yet it's a data table where users expect to sort/group (e.g. by header vs line scope). The `scope` field exists in data but is invisible.
- **Asymmetric hover (Bar 5).** Row hover tints only the 5 scrolling reference cells; the sticky label cell keeps `--surface`, so a hovered row reads as two-toned.
- **No-match state shows headers above an empty mark (Bar 6).** The empty state renders below a still-visible `<thead>`, which looks unfinished. The empty copy is fine but the composition is awkward.
- **The screen under-delivers on its own promise.** The far richer `STANDARDS` support matrix (parse/transform support levels, conformance notes, transport, spec links) is in the same catalog file but never shown. A coordinator wanting to know "is X12 output actually supported?" gets only field paths, not the honest per-format support/badges — a missed offer⇔works opportunity on the very page meant to be that source of truth.
- **No deep-linkable field rows / no copy affordance.** Veterans frequently want to copy a path (e.g. `cac:Item/cac:BuyersItemIdentification/cbc:ID`); there's no copy button and no row anchor.
- **Mobile is a side-scroll spreadsheet, not stacked (Bar 10).** No card/stacked variant; phone users horizontally scroll a 760px table.
- **Heavy inline-style + `--var` strings instead of `Card` + utilities (Bar 8).** The card is a hand-rolled div replicating `Card` styling; consistency depends on manual token copying rather than the shared primitive.
- **Search input height inconsistent with the rest of the app's 44px target (Bar 9).** `sm:h-8` (32px) is below the 44px interactive minimum on desktop; only mobile gets `h-10` (40px), still under 44px.

### Redesign recommendations (for Claude Design)
Ranked most-impactful first. Keep navy #0B1A2F + violet Bridge brand; green=success, amber=warn, red=block; Lucide icons; shadcn/Tailwind.

1. **Merge in the format-support matrix as the page's primary frame (offer⇔works).** Add a top section (or a "Formats" tab) rendering the `STANDARDS` array with ONE status-badge system (Bar 4): per format show parse/transform support as green "Supported" / amber "Partial" / neutral "Planned" / "—" pills with icon+word, plus the one-line `conformance` note and a spec link. This turns the page into the real source of truth instead of only a field-path table. Never render "supported" green where the data says partial/planned.
2. **Make the field table a real, accessible data table (Bars 3, 5).** One row height, one cell padding on a 4/8 grid, gray-200 gridlines, sticky header, sortable columns with `aria-sort`, and group rows by `scope` (Header fields / Line fields) with a subtle section header — surface the `scope` data that already exists. Apply tabular figures to all paths/versions.
3. **Add a per-row copy affordance + field detail.** A copy-icon button on hover for each reference cell (copy the exact path) and/or a click-to-expand row revealing all five mappings stacked with labels using the HUMAN field name first (already done: "PO number" over `PoNumber`). This is the one place an overlay (a lightweight popover or side sheet showing a single field across all formats) would genuinely help — give it a clear close (X / Esc / scrim) and animate from the trigger.
4. **Fix the no-match empty state (Bar 6).** Hide the `<thead>` when zero rows match, center the `EmptyState`, and add a "Clear search" action; consider a "Request a format" CTA inline in that empty state since a missing field is the natural moment to ask.
5. **Standardize spacing & type (Bars 1, 2).** Collapse the fractional sizes onto the system type scale (label 500 / body 400 / heading 600) and snap all padding/gaps to 4/8. Replace `--ink-faint` small text with a token that clears 4.5:1 on white.
6. **Mobile = stacked cards (Bar 10).** Below `sm`, render each canonical field as a card: human label + canonical name as the card title, then a labeled list of the 5 format paths — no horizontal spreadsheet scroll.
7. **Use the `Card` primitive (or a documented table-card variant) (Bar 8).** Replace the hand-rolled div so radius/border/shadow come from one source; if zero-padding is needed, add a `padding="none"` prop to `Card` rather than forking it inline.
8. **Bump interactive sizes (Bar 9).** Search input to ≥44px tall (or document the dense-toolbar exception consistently across the app), with visible focus-visible ring and hover; add an `aria-label`'d clear (×) button inside the input when non-empty.
9. **Add breadcrumb/active-nav context.** Show "Library › Standards" so depth is obvious and back is predictable (Bar: nav/breadcrumbs for depth).

---

### Screenshots — PRODUCTION (7)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/17-base-desktop-1440-library-standards.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/17-base-hd-1920-library-standards.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/17-base-mobile-390-library-standards.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/17-base-tablet-768-library-standards.png)

**filtered-table-quantity · desktop-1440**

![filtered-table-quantity · desktop-1440](screenshots-prod/17-filtered-table-quantity-desktop-1440-library-standards.png)

**mobile-horizontal-scroll-table · mobile-390**

![mobile-horizontal-scroll-table · mobile-390](screenshots-prod/17-mobile-horizontal-scroll-table-mobile-390-library-standards.png)

**no-match-empty-state · desktop-1440**

![no-match-empty-state · desktop-1440](screenshots-prod/17-no-match-empty-state-desktop-1440-library-standards.png)

---

## 18. Exceptions — `/operations/exceptions`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/operations/exceptions/page.tsx`
- **Key components:**
  - `src/components/bridge/ExceptionDetail.tsx` — the expanded per-row what/why/how/status panel (Group V6)
  - `src/components/bridge/layout/PageShell.tsx` — `variant="wide"` page wrapper (1480px)
  - `src/components/bridge/layout/PageHeader.tsx` — title + subtitle + actions row
  - `src/components/bridge/layout/MobileListRow.tsx` — mobile card wrapper
  - `src/components/bridge/DSPrimitives.tsx` — `Button` (variants `blue`/`secondary`)
  - `src/components/bridge/UnifiedStatusBadge.tsx` — canonical order-status pill, used inside `ExceptionDetail`
  - `ExceptionCard` + `SeverityBadge` + `Section` — local components defined inside the two files above
- **Capture URL (mock):** `/operations/exceptions` (mock returns 5 exceptions across states; the default "All" tab shows all 5)

### What it is & why it exists
This is the all-orders exception queue — a single list of every order that is blocked or needs a human decision before it can move forward in the `parse → normalize → validate → transform → deliver → learn` pipeline. Each row is a discrete fault (unresolved supplier code, missing delivery config, supplier HTTP rejection, parse-time assumption, duplicate PO) tied to an owning order. A coordinator opens this page to triage the day's blockers: see what is wrong in plain English, understand why, jump to the owning order to fix the cause, or dismiss noise. The honest model baked into the code is that the backend Reconcile pass is the source of truth — an order-linked exception cannot be manually "resolved" from this list (it would re-open on the next pass), so for those the real action is "Open order".

### Who uses it & the primary job
**Operator / procurement coordinator.** Primary job: triage blocked orders and get to the fix fast — expand a row to read what/why/how-to-fix, then click **Open order** to go fix the cause in the order-detail screen. Secondary jobs: **Ignore** genuine noise, filter by lifecycle state (Open/Resolved/Ignored), and refresh.

### Layout & structure (current)
Top-to-bottom, inside `PageShell variant="wide"` (max-width 1480px, gutter ramp 16→24→34px, vertical 20→28px):

1. **PageHeader** — `h1` "Exceptions" (Bricolage Grotesque, 28/30px, weight 600), subtitle "Every order that needs a human decision before it can be sent.  {N} shown", and a right-aligned **Sync** button (`variant="secondary" size="sm"`, label toggles to "↻ Syncing…" while fetching).
2. **Instructional note** — a single 12px faint line ("Expand a row to see what's wrong, why, how to fix it, and its real delivery status…") pulled up with `-mt-3`.
3. **State filter tabs** — a wrapping flex row of 4 pill-buttons (All / Open / Resolved / Ignored), each 28px tall, 6px radius. Active tab = solid `--ink` (navy) background with white text; inactive = white surface, `--border`, muted text.
4. **Content card** — a raw `div` (NOT the Card primitive — comment explains the Card's 18px padding breaks the flush-edge table) replicating card chrome: `--surface` bg, `1px --border`, `--radius-md` (8px), `--shadow-card`. It is `flex-1 min-h-0 overflow-auto`. Inside it renders ONE of: loading skeleton / error / empty / list.
   - **Desktop table** (`md:` and up): `<table>` `minWidth: 980`, `tableLayout: fixed`, font 12.5px. `colgroup` widths: expand `40`, Severity `96`, Stage `110`, Code `180`, Message `auto`, Raised `96`, actions `176`. Sticky `<thead>` (`position: sticky; top:0; z-index:4`) with 7 columns: "" (chevron), Severity, Stage, Code, Message, Raised, "" (actions, right-aligned). Header cells: 10.5px, weight 700, uppercase, 0.06em tracking, `--ink-faint`.
   - **Mobile cards** (`md:hidden`): vertical stack of `ExceptionCard` (gap 8px, padding 12px).
5. **Footer** — sticky-bottom flex row: "{N} exception(s)" count (11px faint) + client-side pager ("← Prev" / "Page X of Y" mono / "Next →") shown only when `totalPages > 1`. PAGE_SIZE = 25, paginated client-side over the full loaded array.

Density/type/spacing observations: row vertical padding is inconsistent (`9px` on chevron/action cells vs `11px` on data cells in the SAME row); the page uses a mix of pixel values (28px tabs, 24px chevron button, 32px detail buttons) rather than a strict 4/8 scale; numbers (Raised relative time, page count) are mostly NOT tabular except the pager which uses `font-mono`.

### Data shown
**Entity:** `OrderException` (type in `src/types/procurement.ts`). Fields displayed per row:

| Column | Field | Notes |
|---|---|---|
| (expand) | — | chevron toggle |
| Severity | `exc.severity` | `info` / `warning` / `error` / `critical` → `SeverityBadge` |
| Stage | `exc.stage` | parse / validate / transform / deliver (or "—") |
| Code | `exc.code` | machine code in `font-mono` (e.g. `UNRESOLVED_SUPPLIER_CODE`) |
| Message | `exc.message` | human sentence; truncated with ellipsis, click toggles expand |
| Raised | `exc.createdAt` | `relativeTime()` → "12m ago" / "3h ago" / "1d ago" |
| (actions) | `exc.state` | open → Resolve/Open order + Ignore; else shows the state label |

Expanded `ExceptionDetail` additionally lazy-fetches the owning order (`apiClient.getOrderById(exc.orderId)`) and shows: `order.status` (via `UnifiedStatusBadge`), an honest delivery-status line ("Sent — acceptance unconfirmed" for delivered/sent), `order.errorMessage`, and links built from `order.supplierId` (→ `/library/suppliers/{id}`).

**Data source:** `getExceptions(state)` → `GET /api/exceptions?state=open|resolved|ignored` (returns the WHOLE list, no server paging). Mutations: `resolveException(id)` → `PATCH /api/exceptions/{id}/resolve`; `ignoreException(id)` → `PATCH /api/exceptions/{id}/ignore`. Mock: `mockGetExceptions` / `mockResolveException` / `mockIgnoreException` in `src/lib/api/operations.ts`. Mock dataset = 5 exceptions (`exc-001`…`exc-005`) on orders `ord-001`/`ord-002`/`ord-003`, spanning info/warning/error/critical and open/resolved/ignored.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| **Sync** button (header) | `refetch()` | Re-runs the exceptions query; label → "↻ Syncing…" while `isFetching` |
| **All / Open / Resolved / Ignored** tabs | `selectTab(i)` | Sets `activeState`, resets page to 1, collapses any expanded row; re-keys query (`["exceptions", state]`) |
| **Chevron button** (per row, desktop) | `toggleExpanded(id)` | Expands/collapses the in-row `ExceptionDetail`; `aria-expanded`, rotates 90° |
| **Message text button** (per row) | `toggleExpanded(id)` | Same as chevron — clicking the message opens detail (hover underline) |
| **Resolve** button (`variant="blue"`) | `resolveMut.mutate(id)` | Only rendered when `canResolveFromList(exc)` is true (i.e. `!exc.orderId`); invalidates `["exceptions"]` on success. For order-linked rows this is replaced by ↓ |
| **Open order** button (`variant="blue"`) | `router.push('/inbox/{orderId}')` | Navigates to the owning order. Disabled when no `orderId` (wrapped in a `<span title=…>` carrying the why-tooltip) |
| **Ignore** button (`variant="secondary"`) | `ignoreMut.mutate(id)` | Sets state to ignored; invalidates `["exceptions"]` |
| **← Prev / Next →** (footer) | `setPage(±1)` | Client-side page over loaded array; disabled at ends |
| **Open order to fix →** (detail, step 3) | `next/link` to `/inbox/{orderId}` | Same destination as the row "Open order" |
| **Check conformance** (detail, step 3) | `next/link` to `/inbox/{orderId}?tab=conformance` | Order detail, conformance tab |
| **supplier's Validation rules tab** link (detail, step 2) | `next/link` to `/library/suppliers/{supplierId}` | Only shown when the fetched order has a `supplierId` |
| **Mobile card chevron / message** | `onToggle()` | Expand/collapse card detail |
| **Mobile Resolve / Open order / Ignore** | same mutations / `onOpen` | Mirror the desktop row actions |

### What opens / what closes

**No modals, drawers, sheets, or dialogs. The only transient surfaces are inline expand/collapse panels and native browser title tooltips. The page navigates in place (router.push / next/link).**

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Row detail (desktop) | Inline expandable table row | Chevron button OR the Message text button (`toggleExpanded`) | `ExceptionDetail`: step 1 "What's wrong" (message + code · stage), step 2 "Why" (`stageReason` + optional supplier-rules link), step 3 "How to fix" (Open order / Check conformance), step 4 "Status" (UnifiedStatusBadge + honest delivery line + errorMessage) | Clicking the chevron/message again; switching tabs (`selectTab` sets `expandedId=null`). Only one row open at a time (`expandedId` is a single id). No Esc handler |
| Card detail (mobile) | Inline expandable card region | Card chevron/message (`onToggle`) | Same `ExceptionDetail` | Tap chevron/message again; tab switch |
| "Open order" disabled tooltip | Native `title=` on wrapping `<span>` | Hovering the disabled Open-order button when `!exc.orderId` | `NO_ORDER_TITLE`: "This exception isn't tied to an order, so there's no order to open." | Mouse leave (browser-native) |
| "Open order" enabled tooltip | Native `title=` on the button | Hovering Open-order when an order exists | "Open the order to fix the cause. This exception clears itself on the next pipeline pass once the cause is gone." | Mouse leave |
| "Resolve" tooltip | Native `title=` on the button | Hovering Resolve | "Mark this exception resolved. Only available when it isn't tied to an order — order-linked exceptions clear automatically once you fix the cause." | Mouse leave |

There are **no confirmation dialogs** on Ignore or Resolve — they fire immediately and there is no undo. (`mark_chapter`-worthy note for the redesign: Ignore is the closest thing to a destructive action and has no confirm.)

### States
- **Empty:** Handled well. When `exceptions.length === 0`: a green ✓ (32px), "No exceptions — all clear" (20px, weight 600, Bricolage), and helper copy "Nothing is blocked right now. Exceptions appear here when an order needs a decision before it can be sent to a supplier." No next-action button (acceptable — the desirable state is empty).
- **Loading:** Handled — skeleton, not a bare spinner. 6 placeholder rows (`divide-y`), each with a 16px chip + flex bar + 20px bar, `animate-pulse`. `showLoading` also covers the query-not-enabled case so the page never flashes an error before Clerk is ready. The expanded detail's status block has its own pulse skeleton while the order lazy-loads.
- **Error:** Handled — a centered red ⚠ in a 46px `--danger-soft` circle, "Couldn't load exceptions", reassurance copy ("Your orders are safe — this is usually transient."), and a **↻ Retry** button (`refetch`). `retry: 1` on the query.
- **Success/feedback:** Minimal. Mutations invalidate `["exceptions"]` so the list re-fetches and the row drops out of the Open tab, but there is **no toast / inline confirmation**. While a mutation is pending, that row's action buttons are `disabled` (`pendingId === exc.id`) — the only feedback is the disabled (greyed) state; there is no spinner on the button itself.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Full desktop `<table>` inside the 1480px `PageShell`. Sticky header, fixed columns, Message column flexes. Tabs and footer in one row each.
- **Tablet 768:** Still the desktop table (`md:` breakpoint = 768px). Table has `minWidth: 980` with `overflow-x-auto`, so at exactly 768 the table **horizontally scrolls** — a likely cliff (header/actions can be off-screen until you scroll right).
- **Mobile 390:** `md:hidden` swaps to stacked `ExceptionCard`s. Each card: severity badge + stage + relative time on top row, chevron+message, mono code, expand detail, then a row of action buttons (which are `h-[44px]` on mobile via `Button` size logic). Tabs wrap (`flex-wrap`). Footer pager wraps. This is genuinely stacked, not shrunk — good.

### Current UX issues
- **No status-badge unification across the two badge systems.** Severity uses the page-local `SeverityBadge` (rounded-4px rectangle, 10.5px, custom `#F4D5D5`/`#8E1F1F` for critical that the code itself admits has "no exact token match"), while the expanded detail uses the pill-shaped `UnifiedStatusBadge`. Two shapes, two colour sources, two radii for state on one screen — violates the single status-badge system rule.
- **Spacing drift within a single row.** Action/chevron cells use `9px` vertical padding while data cells use `11px`; chevron button is 24px, tab buttons 28px, detail CTAs 32px — no strict 4/8 rhythm.
- **Numbers are not consistently tabular.** "Raised" relative times, the "{N} shown" header count, and "{N} exceptions" footer count use the body font; only the pager is `font-mono`. Counts/timestamps can jitter and don't align.
- **No tabular/sortable affordance.** The table header has no sort controls and no `aria-sort`; rows are fixed-sorted newest-first server-side with no way to sort by severity or stage — the most useful triage axis (severity) isn't sortable.
- **Filter tabs are dead-styled as toggles, not as the canonical Pill/segmented control.** They're bespoke buttons with a navy active fill that doesn't match the app's other tab/segment patterns; counts per state are not shown on the tabs (you can't see "Open 3 / Resolved 1" without switching).
- **Destructive-ish action has no confirm and no undo.** **Ignore** mutates immediately with no toast, no "Undo", and no confirmation — a misclick silently hides a real blocker.
- **No feedback on success.** After Resolve/Ignore the only signal is the row disappearing from the Open filter; there's no toast and no in-place "Ignored ✓". Pending state is just `disabled`, not a visible spinner.
- **Tooltips rely on native `title=`.** The why-explanations for disabled/blue buttons are browser tooltips (slow, untouchable on mobile, inconsistent styling) rather than a styled popover.
- **Tablet table scroll cliff** (`minWidth: 980` from 768px) can hide the right-edge action column behind horizontal scroll.
- **Two primary-coloured buttons compete.** Both "Resolve" and "Open order" use `variant="blue"` (which is actually brand-green per DSPrimitives) AND the detail's "Open order to fix →" uses `--brand-blue`. Green vs blue for the same conceptual action across the row and the panel reads inconsistent; no single dominant primary per screen.
- **"Code" column shows raw machine codes** (`UNRESOLVED_SUPPLIER_CODE`, `SUPPLIER_HTTP_422`) at column width 180 — useful to experts but leads with jargon; the human message is the secondary, ellipsis-truncated column.

### Redesign recommendations (for Claude Design)
1. **Unify the status/severity badge.** Make `SeverityBadge` adopt the one canonical badge shape/size/padding (pill, icon-or-word, never colour alone) shared with `UnifiedStatusBadge`; map critical→danger token (drop the orphan `#F4D5D5`/`#8E1F1F`), error→danger, warning→amber, info→info/blue, with a leading Lucide icon (AlertTriangle / AlertCircle / Info). Keep navy/violet brand intact; green=resolved, red=blocking, amber=warning.
2. **Add per-tab counts and make tabs the canonical segmented control.** Show "Open 3 · Resolved 1 · Ignored 1" inline so triage scope is visible without clicking. Active state via the design-system segment style, not a bespoke navy fill.
3. **Make the table the canonical dense table.** One row height, one cell padding (kill the 9 vs 11px split → single 8/12px), low-contrast `gray-200` gridlines, real `aria-sort` + sortable Severity and Raised columns (severity is the key triage axis). Tabular-figures (`font-variant-numeric: tabular-nums`) on Raised, all counts, and the pager.
4. **Lead each row with the human message; demote the code.** Put the plain-English `message` first (it's the operator's signal) with the machine `code` as a small mono sub-label or an on-demand "show code" affordance — consistent with the "lead with the human field name" rule.
5. **One dominant primary action.** Pick a single primary (green ≥44px) for the row's main action — for order-linked rows that's "Open order"; demote "Ignore" to outline/ghost and visually separate it as the dismissive action. Resolve a single colour story across row and detail (stop mixing brand-green `variant="blue"` with `--brand-blue` link buttons).
6. **Confirm + undo for Ignore.** Add a lightweight confirm (or, better, an optimistic action with a 5s "Ignored — Undo" toast). Add success toasts for Resolve/Ignore and a button-level spinner during the pending mutation instead of bare `disabled`.
7. **Replace native `title=` tooltips with styled popovers** (shadcn Tooltip) for the disabled/blue button explanations, with focus-visible + touch support; keep the honest copy.
8. **Fix the tablet cliff.** Below ~1024px, switch to the stacked card layout (or a 2-column condensed table) so the action column is never hidden behind horizontal scroll; ensure the primary action stays visible.
9. **Elevate the expanded detail consistency.** The 4-step (What/Why/How/Status) panel is excellent and honest (delivered ≠ accepted) — keep it, but give it the canonical card inset, one radius, one border colour, and tabular figures on any numeric status; make the step badges and CTAs use the unified button/badge tokens.
10. **Keep the empty + error states; add a next-action to error.** Empty is good. On the error state, keep Retry but also surface a quiet link to ops health (`/operations/health`) so a persistent failure points somewhere actionable.

---

### Screenshots — PRODUCTION (9)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/18-base-desktop-1440-operations-exceptions.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/18-base-hd-1920-operations-exceptions.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/18-base-mobile-390-operations-exceptions.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/18-base-tablet-768-operations-exceptions.png)

**mobile-card-expanded · mobile-390**

![mobile-card-expanded · mobile-390](screenshots-prod/18-mobile-card-expanded-mobile-390-operations-exceptions.png)

**open-order-disabled-tooltip · desktop-1440**

![open-order-disabled-tooltip · desktop-1440](screenshots-prod/18-open-order-disabled-tooltip-desktop-1440-operations-exceptions.png)

**row-detail-expanded · desktop-1440**

![row-detail-expanded · desktop-1440](screenshots-prod/18-row-detail-expanded-desktop-1440-operations-exceptions.png)

**tab-ignored · desktop-1440**

![tab-ignored · desktop-1440](screenshots-prod/18-tab-ignored-desktop-1440-operations-exceptions.png)

**tab-resolved · desktop-1440**

![tab-resolved · desktop-1440](screenshots-prod/18-tab-resolved-desktop-1440-operations-exceptions.png)

---

## 19. Operations Health — `/operations/health`

- **File:** `src/app/(app)/operations/health/page.tsx`
- **Key components:**
  - `src/components/bridge/layout/PageShell.tsx` (wide variant — 1480px max width)
  - `src/components/bridge/layout/PageHeader.tsx` (title + subtitle row)
  - `src/components/bridge/layout/Card.tsx` (empty/error surfaces)
  - `src/components/bridge/layout/MobileListRow.tsx` (mobile dead-letter cards)
  - `src/components/bridge/DSPrimitives.tsx` → `Button` (the per-row "Try sending again" action)
  - `src/components/bridge/UnifiedStatusBadge.tsx` (status pill in the dead-letter table)
  - `src/components/bridge/BridgeLoader.tsx` → `BridgePageLoader` (route-level `loading.tsx` only)
  - `src/hooks/useQueriesEnabled.ts` (auth/mock data-query gate)
  - API layer: `src/lib/api/operations.ts` (re-exported via `src/lib/api-client.ts`)
- **Capture URL (mock):** `/operations/health` (no ids/query needed — counts and the dead-letter list come from fixed mock functions; the only query param is the in-page `includeFailed` toggle which is client state, not URL state)

### What it is & why it exists
This is the operator's "is the pipeline OK?" dashboard — the failure-side mirror of the inbox. It sits at the tail of the `parse → normalize → validate → review → transform → deliver → learn` workflow and surfaces everything that fell out of the happy path: orders stuck mid-stage, transforms/deliveries that failed, deliveries that exhausted their retries (dead-lettered), supplier rejections, SLA breaches, and a count of open exceptions. The headline element is a worker/engine health banner (a dead Hangfire worker stalls the whole pipeline), and the actionable element is a dead-letter queue where the operator can manually re-attempt delivery of orders that ran out of automatic retries.

### Who uses it & the primary job
**Operator** (the person responsible for keeping POs flowing — often the same procurement coordinator wearing an ops hat). The single most important task: **find an order that couldn't be delivered and requeue it** ("Try sending again") after the underlying cause (supplier endpoint down, timeout, bad config) is believed fixed. Secondary job: confirm at a glance that order processing is running and nothing is silently stuck.

### Layout & structure (current)
Top-to-bottom inside a `PageShell variant="wide"` (1480px max, gutter 16→24→34px, vertical padding 20→28px):

1. **PageHeader** — `h1` "Operations health" (Bricolage Grotesque, 28→30px, weight 600) + muted 13px subtitle "Orders that are stuck, failed, or couldn't be delivered, at a glance."
2. **Worker status banner** — full-width pill-shaped bar (`marginBottom: 14`, `padding: 12px 16px`, `radius-md`). Green soft bg + green border + green dot + "Order processing is running" (weight 700) + "Last checked Ns ago" when `workerHealthy`; flips to danger-soft bg/border + red dot + "Order processing is paused" + a caveat about new uploads waiting when unhealthy.
3. **"Awaiting your review" banner** — a `Link` to `/inbox?status=pending_review`, blue-soft bg (`--brand-blue-soft`), `#D6E3F2` border, 10px radius, `px-4 py-3`, `mb-4`. Big 26px tabular number (`pendingReview ?? 0`) + label "Awaiting your review" + sub "Orders paused for a person to check — not a system problem." Explicitly INFORMATIONAL, styled blue (not red), excluded from `totalProblemOrders`.
4. **Tile grid OR all-clear banner** — if `totalProblemOrders === 0 && openExceptions === 0`, a single green-soft "✓ All clear" banner (`padding 16px 18px`, weight 600). Otherwise a CSS grid `repeat(auto-fill, minmax(168px, 1fr))`, `gap-3` (12px), of 8 count tiles. Each tile is a `Link` (white surface, `--border`, 10px radius, `px-4 py-3`, hover shadow): a 26px count number (faint when 0) + a colored dot + a 12px muted label.
5. **Threshold footnote** — `marginTop 10`, 11.5px faint: "Flagged as stuck after {N} min · auto-refreshes every 45s".
6. **Dead-letter section** (`marginTop 28`) — `h2` "Orders we couldn't deliver" (display font, 18px, weight 600) on the left; a `Include delivery-failed` checkbox label on the right (space-between, wraps on mobile). Optional blue-soft notice bar below the heading after a requeue. Then either an empty `Card` or the data: a desktop `<table>` (`hidden md:block`, white surface, `radius-md`, `overflowX:auto`) and a mobile `<div className="flex flex-col gap-3 md:hidden">` of `MobileListRow` cards.

Density/type/spacing observations: spacing uses a mix of Tailwind scale (`gap-3`, `mb-4`, `px-4 py-3`) and ad-hoc inline pixel values (`marginBottom: 14`, `marginTop: 10/28`, `padding: "12px 16px"`, font sizes `13.5`, `12.5`, `11.5`, `10.5`). Tile numbers are NOT given `tabular-nums` (only the pending-review number and the table Attempts cell are). Table header cells are 10.5px uppercase 700; body cells 13px.

### Data shown
**Entity 1 — `OpsHealth`** (mock `mockGetOpsHealth`, real `GET /api/ops/health`). Fields rendered:
- Banner: `workerHealthy` (bool), `secondsSinceWorkerHeartbeat` (number|null → "Ns/Nm/Nh ago"), `lastWorkerHeartbeatUtc` (string|null), `activeWorkers` (present but **not displayed**).
- Pending-review banner: `pendingReview` (optional → 0 if undefined).
- Tiles (8, each a numeric field + label + inbox filter href):
  - `parsingStuck` → "Stuck reading the file" → `/inbox`
  - `deliveringStuck` → "Stuck delivering" → `/inbox?status=delivering`
  - `transformFailed` → "Transform failed" → `/inbox?status=failed`
  - `deliveryFailed` → "Delivery failed" → `/inbox?status=failed`
  - `deliveryDeadLetter` → "Out of retries" → `/inbox?status=failed`
  - `rejectedBySupplier` → "Rejected by supplier" → `/inbox?status=failed`
  - `slaBreached` → "Overdue" → `/inbox`
  - `openExceptions` → "Open exceptions" → `/operations/exceptions`
  - (`failed` exists in the type and in the red-tone logic but is NOT a tile.)
- `totalProblemOrders` (drives all-clear), `stuckThresholdMinutes` (footnote).

**Entity 2 — `DeadLetterOrder[]`** (mock `mockGetDeadLetterOrders(includeFailed)`, real `GET /api/ops/dead-letter?includeFailed=true`). Table columns: **Order** (`poNumber` or `orderId.slice(0,8)`, links to `/inbox/{orderId}`), **Supplier** (`supplierName ?? "—"`), **Status** (`UnifiedStatusBadge`, with `rejected_by_supplier`→`rejected` normalization), **Attempts** (`deliveryAttempts`, right-aligned tabular), **Last error** (`lastError` + ` (lastResponseCode)`, red, truncated with title tooltip, `maxWidth 280`), **Last attempt** (`relativeTime(lastAttemptAt)`), **Action** ("Try sending again" button).

Mock fixtures: tile counts show `deliveryFailed:1, deliveryDeadLetter:1, openExceptions:2, pendingReview:3, totalProblemOrders:2, stuckThresholdMinutes:30, workerHealthy:true`. Dead-letter rows: `mock-dl-1` (PO-2026-0142, Acme Components, delivery_dead_letter, 3 attempts, "HTTP 503: supplier endpoint unavailable" 503); with `includeFailed` also `mock-dl-2` (PO-2026-0151, BoltWorks BV, delivery_failed, 1 attempt, "Connection timed out", null code).

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| Worker status banner | — (static, not a link) | Display only |
| "Awaiting your review" banner | `Link` | Navigates to `/inbox?status=pending_review` |
| Tile: "Stuck reading the file" | `Link` | `/inbox` |
| Tile: "Stuck delivering" | `Link` | `/inbox?status=delivering` |
| Tile: "Transform failed" | `Link` | `/inbox?status=failed` |
| Tile: "Delivery failed" | `Link` | `/inbox?status=failed` |
| Tile: "Out of retries" | `Link` | `/inbox?status=failed` |
| Tile: "Rejected by supplier" | `Link` | `/inbox?status=failed` |
| Tile: "Overdue" | `Link` | `/inbox` |
| Tile: "Open exceptions" | `Link` | `/operations/exceptions` |
| "Include delivery-failed" checkbox | `setIncludeFailed(bool)` → re-runs `deadLetterQ` with new `includeFailed` key | Widens/narrows dead-letter list (adds/removes `delivery_failed` rows) |
| Order # link (table & mobile) | `Link` | `/inbox/{orderId}` (order detail) |
| "Try sending again" button (table row, `variant="blue"` size sm) | `requeue.mutate(o)` → `POST /api/ops/orders/{id}/requeue-delivery` | Sets inline notice, invalidates `ops-health` + `ops-dead-letter` queries; order returns to "sending". Per-row disabled+"Sending…" while pending |
| "Try sending again" button (mobile card, size md, full-width) | same as above | same |
| Auto-refresh (no control) | `refetchInterval: 45_000` on both queries | Silent background refresh every 45s |

### What opens / what closes

**No modal/drawer/dialog/popover/dropdown/sheet overlays — the page navigates in place and gives feedback via an inline notice bar + a native title tooltip.** This is the page's defining UX gap for an ops surface: the most consequential action (requeue) fires immediately on click with **no confirmation dialog and no detail view**.

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Requeue success/error notice | Inline panel (blue-soft bar above the table) | Successful or failed "Try sending again" mutation (`onSuccess`/`onError` → `setNotice`) | Plain sentence, e.g. "Trying to send PO-2026-0142 again. It will move back to 'sending'." or the error message | **Never explicitly closes** — `notice` is set but never reset; only replaced by the next mutation or a full remount/navigation. No X/Esc. |
| Last-error tooltip | Native HTML `title` tooltip | Hovering the truncated "Last error" cell (`<span title={o.lastError}>`) | The full last-error string (the cell itself is `text-overflow: ellipsis` clipped) | Mouse-out (browser default) |
| Worker-state change | Inline (the banner itself swaps style/text) | `workerHealthy` toggling between fetches | Green "running"/red "paused" copy | Next fetch flips it back |

### States
- **Empty:**
  - *All clear* (no problem orders + no open exceptions): a single green-soft "✓ All clear — no orders in a problem state and no open exceptions." banner replaces the tile grid. The worker banner, pending-review banner, threshold footnote, and dead-letter section still render.
  - *Dead-letter empty:* a `Card edge="none"` with muted 13.5px text "No orders awaiting operator review." + a contextual hint to tick "Include delivery-failed" when that toggle is off. Good empty copy, but no illustration/next-action affordance beyond the hint.
- **Loading:** Two layers. (a) Route-level `loading.tsx` renders `BridgePageLoader label="Loading system health…"` — the canonical animated blue→green wire mark (reduced-motion safe). This only shows during navigation/Suspense. (b) **In-component**, while `!queryEnabled || healthQ.isLoading`, it renders the header + a **bare muted text line "Loading pipeline health…"** — NOT a skeleton. The dead-letter query has no separate loading skeleton; the table simply renders from `deadLetterQ.data ?? []` (empty card until data arrives).
- **Error:** If `healthQ.isError` or data is undefined, the whole body is replaced by a `Card` with red 14px text "Could not load operations health. The API may be unavailable — retry shortly." — **reason given but no retry button** (relies on the 45s auto-refetch / `retry: 1`). The dead-letter query has **no error state of its own** — if it fails it just falls back to an empty list (the page would silently show "No orders awaiting operator review" even on an API error).
- **Success/feedback:** Inline blue notice bar after a requeue (names the PO). Per-row button shows "Sending…" + disabled while that row's mutation is pending (gated on `requeue.variables?.orderId === o.orderId` so only the clicked row spins). Queries invalidate on success so counts/list refresh.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Content centered at 1480px max. Tile grid auto-fills `minmax(168px, 1fr)` (≈7–8 tiles per row at 1440). Desktop dead-letter `<table>` visible (`md:block`), mobile cards hidden.
- **Tablet 768:** At the `md` breakpoint (768px) the table is still shown; tiles wrap to fewer columns. PageHeader/section header rows stay horizontal (`sm:flex-row`). This is the transition point — the table can get cramped just above 768 (7 columns including a 280px error cell) and the page relies on `overflowX:auto` to avoid breaking.
- **Mobile 390:** Below `md`, the `<table>` is hidden and the `flex flex-col gap-3 md:hidden` list of `MobileListRow` cards renders instead — each card stacks PO# + status badge on one row, then "supplier · N attempts · time" line, then a red error line, then a full-width "Try sending again" button (size md = 44px tap height). Buttons enforce 44px min height on mobile via `BUTTON_SIZE`. Tiles collapse toward 1–2 per row (168px min). PageHeader stacks (`flex-col`). The explicit comment in code notes the deliberate Tailwind-class (not inline-style) `display` to avoid the inline-style-beats-media-query double-render bug. No known hard cliff.

### Current UX issues
- **Requeue has no confirm and no detail (DESIGN BAR: confirm-before-destroy / one primary action).** "Try sending again" re-fires a real outbound delivery to a supplier instantly on a single click — a consequential, externally-visible action — with no confirmation step and no way to inspect the failure first. For an ops tool this is the biggest risk.
- **Notice bar never dismisses (DESIGN BAR: modals/transient surfaces need a clear close).** `notice` is set but never cleared; it lingers until the next action or navigation. No X, no auto-timeout, no Esc.
- **No real per-surface loading skeleton (DESIGN BAR #6).** The in-component loading is a bare "Loading pipeline health…" text line, contradicting the "skeleton, not bare spinner/text" rule. The dead-letter table has no loading state at all.
- **Dead-letter error is swallowed (DESIGN BAR #6 + "never show healthy when something is failing").** A failed `getDeadLetterOrders` falls back to `[]`, rendering the reassuring "No orders awaiting operator review" empty state — actively misleading on an API failure.
- **Two competing badge/pill systems.** Tiles use ad-hoc colored dots + numbers (their own `tone()` function with red/amber/neutral), the status column uses `UnifiedStatusBadge`, and the banners use yet another inline pill style. No single status-pill system across the page (DESIGN BAR #4).
- **Inconsistent tabular figures (DESIGN BAR #3).** Tile counts (26px) are not `tabular-nums`; only the pending-review number and the Attempts cell are. Counts and timestamps in the table will jitter/misalign.
- **Spacing/type drift (DESIGN BAR #1 & #2).** Mixed Tailwind scale and raw px (`marginBottom: 14`, `marginTop: 10/28`, `padding: "12px 16px"`), and many off-scale font sizes (13.5/12.5/11.5/10.5). Hierarchy partly carried by color (muted grays) rather than size+weight.
- **Hardcoded hex borders bypass tokens (DESIGN BAR #8).** Banner borders use literal `#BFE3BF`, `#F0B4B4`, `#D6E3F2`, `#D6E3F2` instead of semantic tokens; the empty/error Cards use `--shadow-card` while the banners/tiles use ad-hoc `hover:shadow-md` — no single elevation tier.
- **Focus/hit-area gaps (DESIGN BAR #9).** Tiles and the two banners are `Link`s with hover-shadow only — no visible focus-visible ring defined here, and tile hit areas aren't guaranteed ≥44px. The "Include delivery-failed" checkbox is a raw native `<input type="checkbox">` (unstyled, small target, no custom focus ring).
- **`activeWorkers` fetched but never shown**, and "Order processing is paused" relies on a heuristic heartbeat — risks the "never show healthy when something is failing" rule if heartbeat lags but jobs are truly dead.
- **HTTP code shown as raw `(503)` next to the error string** — fine for an operator but not lead-with-the-human-cause; no acceptance distinction (HTTP 200 ≠ supplier acceptance is correctly handled upstream via `rejected_by_supplier`, but the dead-letter table mixes transport failures and rejections without grouping).

### Redesign recommendations (for Claude Design)
1. **Add a requeue confirmation + detail step (highest impact).** Clicking "Try sending again" should open a small confirm dialog/drawer (navy header, scrim, Esc/X to close) showing the order, supplier, attempt count, full last error, and last response code, with one dominant green primary "Send again" and a ghost "Cancel". This satisfies confirm-before-destroy and gives the operator the failure context that's currently buried in a `title` tooltip.
2. **Promote the dead-letter queue to the page's primary action zone with ONE primary action style.** Make "Try sending again" the single green primary (≥44px, dominant); keep tiles/links as navigation only. Today everything is equally weighted blue/links.
3. **Make the notice a real toast** (dismissible, auto-timeout, success=green / error=red, aria-live) anchored consistently, replacing the persistent inline blue bar.
4. **Unify the status system (DESIGN BAR #4).** One pill shape/size/padding with icon+word for: worker state, tile severity, and dead-letter status. Reuse `UnifiedStatusBadge` semantics (green/amber/red/neutral) for tiles instead of bespoke dot+number tones.
5. **Add proper loading + error states to every surface (DESIGN BAR #6).** Replace "Loading pipeline health…" with tile + table skeletons; give the dead-letter query its own error card with a Retry button instead of silently rendering empty; add a Retry button to the health error card.
6. **Enforce one spacing rhythm and one type scale (DESIGN BAR #1 & #2).** Convert all raw px margins/paddings to the 4/8 scale; collapse 13.5/12.5/11.5/10.5 into the canonical sizes; carry hierarchy via size+weight, keep navy `--ink`/violet for emphasis, drop gray-on-gray.
7. **Tabular figures everywhere (DESIGN BAR #3).** Apply `font-variant-numeric: tabular-nums` to all tile counts, attempts, and timestamps so numbers stop jittering across the 45s auto-refresh.
8. **Tighten the table to one density (DESIGN BAR #5).** Single row height, sticky header, low-contrast `gray-200` gridlines, sortable affordance with `aria-sort` on Attempts/Last attempt, and a dedicated severity/lead-cause column so transport failures vs supplier rejections are visually grouped (keep red for blocking).
9. **Standardize elevation, radius, borders (DESIGN BAR #8).** One card radius/border/shadow tier; replace hardcoded hex borders with the green/red/blue soft+strong token pairs; one popover/dialog shadow tier for the new confirm dialog.
10. **Accessibility polish (DESIGN BAR #9 & forms).** Replace the native checkbox with a labeled, ≥44px, focus-ringed control with helper text; add focus-visible rings to tiles and banners; add `aria-label`s; keep reduced-motion behavior for the pulsing worker dot and `UnifiedStatusBadge` pulse.
11. **Show `activeWorkers` and a precise last-heartbeat timestamp in the worker banner**, and never render "running" green when the heartbeat exceeds the stuck threshold — make the banner the literal source of truth for engine health (don't show healthy when something is failing).
12. **Mobile (DESIGN BAR #10).** Keep the stacked card list (already good); ensure tiles render 2-up, the worker/pending banners stay full-width and legible, and the requeue confirm dialog becomes a bottom sheet on mobile.

---

### Screenshots — PRODUCTION (7)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/19-base-desktop-1440-operations-health.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/19-base-hd-1920-operations-health.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/19-base-mobile-390-operations-health.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/19-base-tablet-768-operations-health.png)

**dead-letter-toggle-off-narrowed · desktop-1440**

![dead-letter-toggle-off-narrowed · desktop-1440](screenshots-prod/19-dead-letter-toggle-off-narrowed-desktop-1440-operations-health.png)

**mobile-dead-letter-cards · mobile-390**

![mobile-dead-letter-cards · mobile-390](screenshots-prod/19-mobile-dead-letter-cards-mobile-390-operations-health.png)

**requeue-notice-after-try-again · desktop-1440**

![requeue-notice-after-try-again · desktop-1440](screenshots-prod/19-requeue-notice-after-try-again-desktop-1440-operations-health.png)

---

## 20. Delivery Log (Audit Trail) — `/operations/log`

- **File:** `src/app/(app)/operations/log/page.tsx` (one-liner; renders `<CrossingsLog />`)
- **Key components:**
  - `src/components/bridge/CrossingsLog.tsx` (the entire screen — table, filters, search, export, expandable rows)
  - `src/components/bridge/layout/PageShell.tsx` (wide variant, max-width 1480px)
  - `src/components/bridge/layout/PageHeader.tsx` (title + subtitle + actions slot)
  - `src/components/bridge/EmptyState.tsx` (filtered-empty state; pulls `MarkSystem`)
  - `src/components/bridge/MarkSystem.tsx` (brand mark in the empty state)
- **Capture URL (mock):** `/operations/log` (no ids/query — single static route; mock mode renders the in-component `MOCK_LOG` fixture, 8 entries)

### What it is & why it exists
This is the append-only **audit trail** for the whole bridge: every parse, edit, validation, and delivery the system records appears here as a timestamped, actor-attributed event. It sits at the end of the `parse → normalize → validate → review → transform → deliver → learn` loop as the system of record — the place a procurement coordinator goes to answer "did this PO actually reach the supplier, when, and who/what touched it?" It is read-mostly: the only outbound actions are export (CSV) and navigating to the underlying order to act there (e.g. resend a failed delivery). It deliberately does **not** retry deliveries in place (the code comment notes the old "Retry delivery" control was a dead control; it now honestly says "Open to resend" and routes to the order).

### Who uses it & the primary job
**Operator / procurement coordinator** (with some integration-expert overlap). Primary job: **confirm a delivery's outcome and trace its history** — find a PO, see whether it delivered/failed/retried, expand the failed event to read the error reason, then jump to the order to resend or fix. Secondary job: export the filtered log to CSV for a record/handover.

### Layout & structure (current)
Top-to-bottom inside `PageShell variant="wide"` (centered, max 1480px, gutter `px-4 → sm:px-6 → lg:px-[34px]`, vertical `py-5 → sm:py-7`):

1. **PageHeader** — `h1` "Delivery log" (Bricolage Grotesque, weight 600, 28/30px, `-0.02em`); subtitle row with a small key icon (Lucide, `--ink-faint`) + text "Append-only audit log · every parse, edit, validation and delivery recorded" (13px, `--ink-muted`). Right-aligned **actions** slot: a single `.btn.btn-secondary` **Export log** (download icon + label), disabled when nothing matches.
2. **Filter / search bar** — flex row, `marginBottom: 14`, `gap: 12`. Left: a row of 7 **filter chips** (`.fchip`, 30px tall, 12px/600) — "All events / Delivered / Failed / Edited / Validated / Parsed / Created". Active chip = `.fchip.active` (green-soft bg `#E9F1EA`, green-deep text, green-tinted border). Right: a **PO search input** — a 200px-wide / 30px-tall pill (`--surface` bg, `--border` border, `--radius` 6px) with a search icon + borderless `<input placeholder="Filter by PO…">` (12.5px).
3. **Body — date-grouped cards**. The filtered events are grouped by calendar date into a `Map`. For each group:
   - A **date eyebrow** label (mono, 10.5px, `--ink-faint`): "Today · 29 June 2026", "Yesterday · 28 June 2026", or a long weekday date. `marginBottom: 8`.
   - A **`.card`** (`--surface`, 1px `--border`, `--radius-lg`, `--shadow-card`, but `padding:0 overflow:hidden`) containing the event **rows**, each separated by a 1px `--border` bottom (last row none).
4. **Each row (desktop)** is a full-width `<button>` laid out as fixed columns at `padding: 11px 16px`:
   `[ time mono 64px ] [ 26px event-icon circle ] [ event label 92px ] [ PO mono 150px ] [ buyer → (arrow) → supplier — grow ] [ actor 110px right ] [ chevron 15px ]`.
   The event label + icon + colors are driven by `canonicalEvent` (not the raw event type): Created (slate/`--surface-2`), Parsed (blue), Validated (green), Edited (violet/`--ai`), Delivered (green), Failed (red). Buyer is blue (`--brand-blue-deep`), supplier is green (`--brand-green-deep`), joined by a small arrow.
5. **Expanded panel** (one row open at a time) renders below the clicked row: `background: --surface-2`, indented `padding: 4px 16px 16px 106px` on desktop (aligns under the content columns). Inside is a nested **`.card`** (`--surface`, `padding 12px 14px`) holding — in order, whichever exist — a **key/value detail grid** (auto-fill `minmax(150px,1fr)`, eyebrow label + mono value), or a free-text `detail` paragraph fallback, an **error banner** (amber if recoverable, red if not), a **field-diff table** (mono, zebra rows, blue field → red from → arrow → green to), and an **action row**: View order (secondary), Open to resend (secondary, failed only), Export entry (ghost).

Type/density observations: extensive **inline `style={{}}`** (almost nothing uses Tailwind here except PageShell/PageHeader). Row heights are not enforced by a single token — desktop rows are `11px 16px` padding but variable height; the icon circle is 26px so effective row height ≈ 48px. Numbers (time, PO) use `.mono` but the codebase mono stack is not declared `font-variant-numeric: tabular-nums` here. Font sizes drift: 11.5, 12, 12.5, 13 all appear within one row.

### Data shown
**Entity:** audit-log events (`LogEntry` internally; `AuditLogEntry` from the API).

| Field (display) | Source field |
|---|---|
| Time (HH:mm:ss) | `ts` ← formatted from `AuditLogEntry.ts` (ISO) |
| Event (Created/Parsed/Validated/Edited/Delivered/Failed) | `canonicalEvent` ← mapped from `AuditLogEntry.action` via `ACTION_TO_EVENT` → `EVENT_TO_CANONICAL` |
| PO number | `po` ← `poNumber ?? orderId ?? "—"` |
| Buyer | `buyer` ← `buyerName ?? "—"` |
| Supplier | `supplier` ← `supplierName ?? "—"` |
| Format (in CSV export + mock details) | `fmt` ← `format ?? "—"` |
| Actor name | `actor.name` ← `actorName`; type ∈ user/system/ai |
| Message | `message` |
| Expanded: detail grid, error banner, field diff | mock-only (`details`, `error`, `recoverable`, `detail`, `diff`) — **not present on API entries** |

**Data source:** `getAuditLog(page=1, pageSize=50)` in `src/lib/api-client.ts` → `GET /api/audit?page=1&pageSize=50` (returns `AuditLogPage { events, total, page, pageSize }`). Fetched via TanStack `useQuery(["audit"])`, `enabled: !isApiMockMode`. In mock mode the component ignores the API and renders the local `MOCK_LOG` constant (8 hand-built entries: PO-DEMO-001, WMT-2026-0341, 850-99201). Note the API's own `getAuditLog` mock (2 entries) is bypassed because the component branches on `isApiMockMode` first.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| **Export log** button (header, secondary) | `handleExport()` | Builds a CSV of the **currently filtered** rows (8 columns) and triggers a browser download `delivery-log-YYYY-MM-DD.csv`. Disabled (`opacity .5`) when `filtered.length === 0`. |
| **Filter chips** ×7 (All / Delivered / Failed / Edited / Validated / Parsed / Created) | `setFilter(key)` | Client-side filter on `canonicalEvent`; active chip turns green. No URL change. |
| **PO search** input | `setSearch(e.target.value)` | Client-side substring filter across PO / buyer / supplier (case-insensitive). |
| **Event row** (button) | `setOpenId(open ? null : c.id)` | Toggles the expanded detail panel for that row (accordion — only the inner mobile button sets `aria-expanded`; the desktop button does **not**). |
| **Chevron** (within row) | (rotates 180° via CSS on open) | Visual affordance only — clicking it clicks the parent row button. |
| **View order** (expanded, secondary) | `router.push('/inbox/{crossingId}')` | Navigates to the order's inbox detail page. |
| **Open to resend** (expanded, secondary, **failed events only**) | `router.push('/inbox/{crossingId}')` | Navigates to the order (resend lives there) — does **not** retry in place. |
| **Export entry** (expanded, ghost) | inline CSV builder | Downloads a single-row CSV `delivery-{po}-{time}.csv`. |
| **Retry** button (error state only) | `refetch()` | Re-runs the audit query after a load failure. |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Row detail** | Inline expand/accordion panel (in-flow, not an overlay) | Clicking any event row button | Nested card: key/value **detail grid**, OR free-text detail; optional **error banner** (amber/red); optional **field-diff table**; **action row** (View order / Open to resend / Export entry) | Clicking the same row again (`openId → null`), or clicking a **different** row (replaces it — only one open at a time). No Esc / backdrop / X. |
| **CSV download** | Browser file download (transient, no UI) | Export log / Export entry buttons | A generated CSV blob via a temporary `<a>` | n/a (resolves immediately; element removed in code) |

**No modals, drawers, sheets, dialogs, popovers, dropdowns, tooltips, or toasts.** The page navigates in place (router.push to `/inbox/{id}`) and uses a single inline accordion for detail. This is the page's defining trait for the redesign: all "depth" is one in-flow expander, and all "actions" leave the page rather than acting here.

### States
- **Empty (filter returns nothing):** Renders a `.card` wrapping `<EmptyState compact title="No matching events" sub="Nothing recorded for this filter yet." />` (brand Mark, Bricolage title, muted sub). There is **no distinct first-run / zero-data empty** — if the API returns `events: []`, the same "No matching events" filter-empty copy shows, which is wrong messaging for a genuinely empty log (no "next action" CTA).
- **Loading:** Only in non-mock mode. Renders a `.card` with **3 `SkeletonRow`s** (shimmer bars via `.skel`, widths 64/26/82/150/220/110). There is **no** route-level `loading.tsx` (the folder has none) — the skeleton is component-internal, so a hard navigation shows nothing until the client component mounts.
- **Error:** Non-mock only. A centered `.card` (`padding 48px 24px`) with a `⚠` glyph (28px, `--danger`), the message "Could not load the delivery log. Check your connection and try again." and a **Retry** secondary button calling `refetch()`. Reason is generic (no status code surfaced).
- **Success/feedback:** None for export (file just downloads silently — no toast/confirmation). No feedback when a filter/search returns results. Navigation is the only "success."

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Identical layout (content capped at 1480px and centered, so 1920 just adds side margin). Fixed-column flat rows; filter chips + search on one line.
- **Tablet 768:** Still uses the **desktop** row layout (the mobile switch is at ≤640px via `useIsMobile`). Filter+search row stays horizontal. Fine, but the search input is a fixed 200px and chips can crowd.
- **Mobile 390 (≤640px):** `useIsMobile()` flips to a **stacked card row**: 4 lines per event — (1) icon + label + time + chevron, (2) PO mono (blue), (3) buyer → arrow → supplier (wraps), (4) actor. Filter+search column-stacks (`flexDirection: column`, items stretch); the chip row becomes horizontal-scroll (`.fchip-row` overflow-x auto, scrollbar hidden), chips grow to 36px, search input to 40px full-width, Export button to full-width 40px. Expanded panel uses full width (`padding 4px 14px 14px`) and the detail grid drops to `minmax(120px,1fr)`; diff rows wrap. Action buttons grow to 36px.
- **Known cliffs:** (a) the **640px** breakpoint means 641–768px renders dense desktop rows on a narrow tablet (PO 150px + 110px actor + grow buyer/supplier can clip with ellipsis). (b) Mobile detection is JS (`matchMedia`), so SSR/first paint always renders desktop then snaps — a layout flash on small screens.

### Current UX issues
- **No status-badge system.** Event state is conveyed by a 26px tinted icon circle + a colored text label only — it is **not** the app's pill badge (`.pill-*` exists in globals.css but isn't used here). It violates the one-badge rule: different shape, no consistent padding, and color does carry meaning but the failed/delivered states don't read as the same family as the rest of the app.
- **"Failed" can hide partial truth.** A row labelled "Failed" with a recoverable error shows an amber banner ("… · auto-retry scheduled") but the **row chip stays red** — there is no "Retrying" state in the canonical event set even though the mock data models retries. HTTP 200 is shown as "Delivered" with "HTTP 200" in the grid, conflating transport success with supplier acceptance (the banner copy in mock e6 even mixes "endpoint timeout (30s)" with "HTTP 504" and an "SFTP timeout" status — inconsistent).
- **Numbers don't use tabular figures.** Times, PO numbers, counts and confidence %s use `.mono` but not `font-variant-numeric: tabular-nums`; with proportional digits, the fixed 64px time column and PO column can still jitter.
- **Spacing drift.** Magic px everywhere (11/12/14/16/18/26/106), font sizes 11.5/12/12.5/13 within a single row — not a strict 4/8 scale, and type hierarchy is partly carried by color (blue/green/violet) rather than size+weight.
- **Type hierarchy via color, contrast risk.** Actor text is `.faint` 11.5px; `--ink-faint` against `--surface` is likely below 4.5:1. The detail "eyebrow" labels are 9px — too small to read comfortably.
- **Accordion accessibility gaps.** The **desktop** row button has no `aria-expanded` (only the mobile button does); the chevron is decorative with no label; the open/close has no Esc handling and no focus management. Rows are buttons but have no visible hover/pressed state beyond the open-row background tint.
- **Filter chips are not a tablist / have no aria-pressed.** Seven look-alike chips with no selected-state semantics for AT; "All events" + "Created"/"Parsed" etc. blur the line between filtering by lifecycle stage vs. delivery outcome (the page is titled "Delivery log" but filters include parse/create events).
- **Export is silent.** No toast/confirmation; a coordinator can't tell if the CSV captured the filtered subset vs everything.
- **Empty/zero-data conflation.** A genuinely empty audit log shows "No matching events / Nothing recorded for this filter yet" with no onboarding/next action.
- **No pagination/load-more.** The query asks for 50 rows; if a tenant has thousands of events there's no way to page (`AuditLogPage.total` is returned but unused) — and no date-range filter, only PO text + event type.
- **Mobile-first-paint flash** from JS breakpoint detection; `≤640px` cliff leaves 641–767px on dense desktop rows.

### Redesign recommendations (for Claude Design)
1. **Adopt the one canonical status badge** for the event/outcome column (`.pill-*` family): same shape/size/padding, green=Delivered, red=Failed (blocking), amber=Retrying/Recoverable, blue=Parsed/In-progress, neutral=Created, violet=Edited — each with icon **and** word, never color alone. Add a real **"Retrying"** state so recoverable failures aren't shown as flat red.
2. **Never conflate HTTP 200 with acceptance.** In the delivered detail grid, separate "Transport: HTTP 200" from "Supplier acceptance: ACK received / pending / rejected." Make the row label reflect business acceptance, not transport. Fix the mock inconsistencies (timeout vs 504 vs SFTP) so copy is internally consistent.
3. **Tabular figures everywhere** — add `font-variant-numeric: tabular-nums` to the mono/number cells (time, PO, counts, %s, sizes) so columns stop jittering and money/counts align.
4. **One table density + sticky header + sortable affordance.** Give rows a single fixed height (≈44–48px), one cell padding, low-contrast `gray-200` gridlines, a real sticky column header row (Time / Event / PO / Buyer → Supplier / Actor) with `aria-sort`, and zebra or hover from one token. Keep the navy/violet brand for buyer/supplier accents but stop using 4 font sizes per row.
5. **Make detail an accessible disclosure.** Both desktop and mobile buttons get `aria-expanded`/`aria-controls`; the chevron gets `aria-hidden`; respect reduced-motion on the rotate; support Esc to collapse and keep focus on the trigger. One radius/border/shadow tier for the nested detail card (it currently nests a `.card` inside a `.card`).
6. **Add real states.** Distinguish **genuinely-empty audit log** (first-run: "No events yet — they'll appear here as you upload, validate and deliver" + a link to Upload) from **filter-empty**. Add a route-level `loading.tsx` skeleton so navigation doesn't show a blank frame. Surface the error status code + a "Retry" that's clearly primary.
7. **One primary action, ≥44px.** "Export log" is the page's primary export action — keep it secondary if export is rare, but ensure every interactive control (chips, search, rows, action buttons) is ≥44px tap target with visible hover/pressed and a focus-visible ring (the global `:focus-visible` exists; verify it shows on these inline-styled buttons).
8. **Group the filters by intent.** Visually separate **delivery outcomes** (Delivered / Failed / Retrying) from **lifecycle events** (Created / Parsed / Validated / Edited), or give the page a primary "Deliveries" view and a secondary "All activity" toggle — the title says "Delivery log" but the default shows all lifecycle events.
9. **Add date-range + pagination/load-more** (the API already returns `total`); a procurement coordinator auditing "did last month's POs deliver" needs a date filter, not only PO text search.
10. **Fix the 640px tablet cliff + first-paint flash.** Move the desktop→stacked switch to a CSS container/media query (or render stacked ≤768px) so 641–767px isn't dense desktop, and avoid the JS-only `matchMedia` snap. On mobile keep the stacked-card row (already good) but raise the 9px eyebrow labels and `.faint` actor text above the contrast floor.
11. **Confirm exports.** Show a toast ("Exported 24 events to CSV") so the coordinator knows the file reflects the current filter, and label the Export button to indicate it exports the *filtered* set.

---

### Screenshots — PRODUCTION (9)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/20-base-desktop-1440-operations-log.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/20-base-hd-1920-operations-log.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/20-base-mobile-390-operations-log.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/20-base-tablet-768-operations-log.png)

**filter-active-failed · desktop-1440**

![filter-active-failed · desktop-1440](screenshots-prod/20-filter-active-failed-desktop-1440-operations-log.png)

**filter-empty-no-matching-events · desktop-1440**

![filter-empty-no-matching-events · desktop-1440](screenshots-prod/20-filter-empty-no-matching-events-desktop-1440-operations-log.png)

**mobile-stacked-row-expanded · mobile-390**

![mobile-stacked-row-expanded · mobile-390](screenshots-prod/20-mobile-stacked-row-expanded-mobile-390-operations-log.png)

**row-detail-delivered-expanded · desktop-1440**

![row-detail-delivered-expanded · desktop-1440](screenshots-prod/20-row-detail-delivered-expanded-desktop-1440-operations-log.png)

**row-detail-failed-error-banner · desktop-1440**

![row-detail-failed-error-banner · desktop-1440](screenshots-prod/20-row-detail-failed-error-banner-desktop-1440-operations-log.png)

---

## 21. Connectors — `/operations/connectors`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/operations/connectors/page.tsx`
- **Key components:** (all defined *inline* in `page.tsx` — there is no separate `ConnectorRequirementsPanel.tsx` file; the requirements panel logic was inlined as `ManifestRequirementsSection`)
  - `ConnectorsPage` (default export) — page container, grid, state
  - `ConnectorCard` — one connector tile
  - `ConnectorStatusPill` — the Connected / Available / Coming soon pill
  - `SkeletonConnectorCard` — loading skeleton tile
  - `ConnectorPanel` — the read-only configuration **modal/slideover** opened by Manage / Connect / Add connector
  - `ManifestRequirementsSection` + `ManifestFieldGroup` + `ManifestFieldRow` — the "What this connector needs" block inside the panel
  - `PanelField`, `PlugIcon`, `PlusIcon` — small primitives
  - Shared: `src/components/bridge/layout/PageShell.tsx`, `src/components/bridge/layout/PageHeader.tsx`, `src/components/bridge/EmptyState.tsx`, `src/components/bridge/BridgeLoader.tsx` (via `loading.tsx`)
  - Data: `getSuppliers`, `testFireDeliveryConfig`, `isApiMockMode` from `src/lib/api-client.ts`; `getConnectorManifest` from `src/lib/api/connectors.ts`
- **Capture URL (mock):** `/operations/connectors` (mock mode renders the 6 hard-coded `MOCK_CONNECTORS` immediately; no id/query needed)

### What it is & why it exists
This is the **deliver-side** channel inventory for the parse→…→deliver→learn loop: it answers "through which transports/ERPs can ProcuLink hand a finished PO to a supplier (and pull orders in)?" It lists integration channels (HTTP/REST, SFTP, Email IMAP/SMTP, Erply/Directo ERP) and ERP connectors (SAP Ariba, Coupa, Dynamics 365 — currently "Coming soon"). A procurement coordinator opens it to review what's wired up and to discover that **actual** delivery endpoints + credentials are configured *per supplier* (on the supplier's Delivery tab) — this page is intentionally a read-only directory/launcher, not the place credentials are entered.

### Who uses it & the primary job
**Integration expert / operator** (occasionally an admin). The single most important task: confirm a transport is available and **jump to the right place to actually configure it** — i.e. click Manage/Connect → read the requirements → "Open supplier Delivery tab", or test-fire an existing supplier's delivery config.

### Layout & structure (current)
Top-to-bottom, inside `PageShell variant="wide"` (max-width `--container-wide` = 1480px; gutter ramp 16→24→34px, vertical 20→28px):

1. **`PageHeader`** — title `Connectors` (Bricolage Grotesque, 28–30px, weight 600); subtitle is conditional: mock mode shows `ERP and channel integrations · {N} connected`, live mode shows just `ERP and channel integrations` (the count is suppressed in live mode because it is not derivable — see Data shown). Right-aligned **green primary** "Add connector" button (`.connectors-addbtn`, 32px tall, brand-green `#2E8E3A`, white text, `+` icon).
2. **Notice banner** (conditional) — green left-border (3px `--brand-green`) success strip; only rendered when `notice` state is set. (In current code `notice` is set to `null` on every action and never set to a message — it is effectively dead UI.)
3. **Error banner** (conditional, live mode only) — red/danger strip "Failed to load connectors. [Retry]".
4. **Body** — either: loading skeleton grid (3 skeleton cards), `EmptyState`, or the **connector card grid**.
   - Grid: inline `<style>` block — `repeat(3, 1fr)` with `gap: 16px`; collapses to `1fr 1fr` ≤1100px and `1fr` ≤640px.
   - Each card: white surface, 1px `--border` (#E2E6EE), radius `--radius-md` (8px), **padding 18px**, flex column. Top row = 40×40 grey icon tile (Lucide plug icon) + status pill. Then name (14px / 600) + description (12px muted). A 1px top-border footer (margin-top 14, padding-top 12) holds the usage count (left) + single action button (right).

Density/type/spacing observations: this page is built almost entirely with **inline `style={{}}`** and hard-coded pixel values (18px card padding, 14px gaps, 11.5/12/12.5/14/16px font sizes, `height: 27` action buttons, `height: 32` header/footer buttons) rather than Tailwind tokens — it drifts off the 4/8 scale (27px buttons, 11.5px text, gap 5/6/7/14 mixed). Numbers (the "3 suppliers" count) are not tabular-figured.

### Data shown
- **Mock mode (`isApiMockMode`):** a static array `MOCK_CONNECTORS` (6 rows) with fields: `id`, `type` (e.g. "cXML PunchOut", "ERP (REST)", "EDI (SFTP)", "Email (IMAP)", "ERP — Erply"), `name` (SAP Ariba, Coupa, Microsoft Dynamics 365, Generic SFTP, Email (IMAP), Erply), `status` ("coming_soon" | "connected" | "available"), `desc`, `docks` (supplier count), `direction` ("in" | "out"). `connectedCount` = count of `connected`/`ok` rows.
- **Live mode:** `GET /api/suppliers` via `getSuppliers` (`realGetSuppliersFn`). The endpoint returns only `{ id, name }`. The page maps **each supplier** into a connector row with `type: "API (REST)"`, `status: "available"` (always — never "connected"), `desc: "Supplier delivery endpoint"`, `docks: -1` (unknown → the usage footer line is hidden), `direction: "out"`. `connectedCount` is forced to `0` and the subtitle count is dropped — an explicit **offer⇔works honesty** decision because the list payload carries no delivery-config signal.
- **Inside `ConnectorPanel`:** `getConnectorManifest(key)` → `GET /api/connector-manifests/{key}` (`connectors.ts`). Returns `ConnectorManifest` { key, displayName, transport, authType, capabilities, docsRef, fields[] }, each field `{ name, label, type, required, secret, helpText }`. Manifest key is derived from the connector's free-text `type` via `resolveManifestKey()` (sftp/ftps/smtp/erp_erply/erp_directo/http; null = section hidden). Test-fire uses `testFireDeliveryConfig(connector.id)` → `POST /api/suppliers/{id}/delivery-config/test-fire`.

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| "Add connector" button (header, green primary) | `setSelected({ id:"new", … })` | Opens `ConnectorPanel` in **new/Add** mode |
| Connector card "Connect" button (status `available`) | `onManage(connector)` → `setSelected(c)` | Opens `ConnectorPanel` for that connector |
| Connector card "Manage" button (status `connected`, ghost text) | `onManage(connector)` → `setSelected(c)` | Opens `ConnectorPanel` for that connector |
| Connector card "Request access" link (status `coming_soon`) | `mailto:sales@proculink.eu?subject=Connector request: {name}` | Opens the OS mail client (not connectable — honest dead-end alternative) |
| Error banner "Retry" button | `queryClient.invalidateQueries(["suppliers"])` | Re-fetches the suppliers query |
| EmptyState "Add connector" button | `setSelected({ id:"new", … })` | Opens `ConnectorPanel` in Add mode |
| Panel "×" close (top-right, `aria-label="Close"`) | `onClose()` → `setSelected(null)` | Closes the panel |
| Panel "Cancel" (footer, outline) | `onClose()` → `setSelected(null)` | Closes the panel |
| Panel "Test fire" (footer) | `handleTestFire()` → `testFireDeliveryConfig(id)` (or informational message if `id==="new"`) | Shows inline test-result banner inside the panel; closes nothing |
| Panel "Open supplier Delivery tab" (footer, green primary `<Link>`) | navigates + `onClose()` | `/library/suppliers` (new) or `/library/suppliers/{id}?tab=delivery` |
| Manifest "{displayName} documentation" link (if `docsRef`) | opens `docsRef` in new tab | External (e.g. learn.erply.com, wiki.directo.ee) |
| Panel read-only inputs (Connector type / Name or endpoint / Direction / Status) | none (`readOnly`) | Display only — greyed surface-2 fields |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **ConnectorPanel** | Modal overlay (fixed full-screen scrim, centered card on desktop / bottom-sheet on mobile) | "Add connector" (header), "Connect"/"Manage" (card), "Add connector" (EmptyState) — all `setSelected(...)` | Header (plug icon tile + name + "Connector configuration"); blue-bordered info note ("Delivery endpoints … configured per supplier … This panel is read-only"); 4 read-only fields (Connector type, Name or endpoint, Direction, Status); the **ManifestRequirementsSection** (if a manifest key resolves); a test-result banner (after Test fire); footer with Cancel / Test fire / Open supplier Delivery tab | "×" button, "Cancel" button, or "Open supplier Delivery tab" (navigates + closes). **No Esc handler and no backdrop-click handler** — clicking the scrim does NOT close it |
| **ManifestRequirementsSection** | Inline panel *inside* ConnectorPanel (not separately dismissible) | Auto-renders when `resolveManifestKey()` returns non-null | "What this connector needs · {authType}"; field groups **Required / Credentials (stored encrypted) / Optional**, each row = monospace field `name` + `label` + Required/Secret/type chips; optional docs link | Closes with the parent panel only |
| **Test-result banner** | Inline status strip (`role="status"`, `aria-live="polite"`) inside the panel | "Test fire" button | success (green) / failure (danger `Failed — …`) / informational (neutral blue, for `id==="new"`) message | Replaced on next Test fire; cleared when panel closes |
| Notice banner (green) | Inline strip above the grid | `setNotice(...)` — **never actually called with a string** in current code (dead) | n/a (would be a success message) | n/a |
| Error banner (red) | Inline strip above the grid | live-mode query error | "Failed to load connectors. [Retry]" | disappears on successful refetch |

No toasts, no dropdowns, no tooltips, no popovers. There is **one** real overlay: `ConnectorPanel`.

### States
- **Empty:** handled — `EmptyState` ("No connectors configured" + sub + "Add connector" button) renders when `connectors.length === 0`. In live mode this means a real org with zero suppliers; mock mode never hits it (6 hard-coded rows). Note the EmptyState's own button is **navy** (`--navy #0B1A2F`), inconsistent with the page's green primary "Add connector".
- **Loading:** handled — skeleton **only in live mode** (`isLoading && !isApiMockMode`) renders 3 `SkeletonConnectorCard`s (pulse animation `skel-pulse`). `loading.tsx` (route-level `BridgePageLoader` "Loading connectors…") shows during the Next.js route transition before the client component mounts. Mock mode shows no loader (synchronous).
- **Error:** handled (live only) — red banner with Retry. Manifest fetch failures inside the panel **degrade silently** (`if (!manifest) return null;`) — no error shown.
- **Success/feedback:** the in-panel test-result banner (green/red/blue) is the only confirmation. The dead `notice` banner was clearly intended for "saved"/"connected" feedback but is never populated.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** grid `repeat(3,1fr)`, gap 16; panel centered at `max-w-[540px]`, radius 10, scrim `rgba(11,26,47,0.42)` + `blur(3px)`.
- **Tablet 768 (≤1100px):** grid drops to **2 columns** (`1fr 1fr`).
- **Mobile 390 (≤640px):** grid is **1 column** (cards stack — good). The panel becomes a **bottom sheet** (`items-end`, `rounded-t-[10px]`, `max-h-[92vh]`). Card action buttons get `min-height: 40px` and "Add connector" goes full-width (`flex: 1 0 100%`, height 40). The 27px desktop action height is below the 44px touch target on desktop; the mobile override only lifts it to 40px (still <44).
- Cliffs: the visible action chrome stays 27px tall on desktop/tablet (small touch target); the panel footer (Cancel / Test fire / Open Delivery tab, 3 buttons) `flex-wrap`s and can crowd at narrow widths.

### Current UX issues
- **Two competing concepts (#7 one primary action / honesty):** the page is titled/structured as a connector *config* surface, but it's actually read-only — every path funnels to "Open supplier Delivery tab." "Connect"/"Add connector" imply you can configure here; you can't. This is the biggest comprehension gap.
- **Status taxonomy isn't one system (#4):** three pill styles — `.pill-ready` (green "Connected"), `.pill-new` (neutral "Available"), and a **bespoke inline amber** "Coming soon" pill (not a shared class, different padding 2px 9px / fontSize 11.5 vs the `.pill` 21px height / 11px). Coming-soon should be a real neutral/disabled token, not a one-off.
- **Modal accessibility gaps (#9 + modal rules):** no `Esc` to close, no backdrop-click to close, no focus trap, no `role="dialog"`/`aria-modal`. The only exits are the "×", Cancel, and the navigating CTA.
- **Inline-style drift / no token discipline (#1, #2):** the whole page is hand-styled with magic pixels (27/32/34/40px heights; 5/6/7/14px gaps; 10.5/11/11.5/12/12.5/14/16px font sizes). No shared Button/Card/Pill components — diverges from the unified design system primitives.
- **Card action targets too small (#9):** 27px-tall "Connect"/"Manage"/"Request access" controls; below the 44px bar except where the ≤640px override bumps to 40px.
- **Dead "notice" banner (#6):** wired up but never populated — code path that renders nothing, signalling unfinished feedback design.
- **Inconsistent primary color (#7):** header "Add connector" = green; EmptyState "Add connector" = navy; Test fire = ghost green-text outline. No single visually-dominant primary per surface.
- **Required/Secret/type chips are dense and mono (#2):** field rows lead with monospace `field.name` (e.g. `remotePath`, `toAddresses`) — machine keys before the human label, against the "lead with the human field name" rule.
- **Usage count not tabular (#3):** "3 suppliers" / "Not in use" use the default proportional figures; live mode hides the count entirely, so cards lose a useful signal.
- **`docs/coming-soon` ERP tiles are visually identical to live ones** apart from the amber pill — at a glance SAP Ariba/Coupa look as wired as Generic SFTP.

### Redesign recommendations (for Claude Design)
1. **Reframe the page honestly as a "Channels & connectors" directory (most impactful).** Make it unmistakable that real config lives per-supplier: rename the panel to "Connector overview", keep the blue per-supplier note as the panel's lead, and make "Open supplier Delivery tab" the single dominant green primary (#1 primary-action rule). Demote "Connect"/"Manage" wording to "View" since nothing is configured here.
2. **Unify the status pill into ONE badge system** (#4): one shape/size/padding for Connected (green + check), Available (neutral + word), Coming soon (neutral/amber "soon" + clock), Failed (red) — each with icon+word, never color alone. Replace the bespoke inline amber pill with a token-driven class. Keep navy/violet brand.
3. **Fix the modal (#9 + modal rules):** add `role="dialog" aria-modal`, `Esc`-to-close, backdrop-click-to-close, focus trap, and animate-from-trigger; keep the bottom-sheet on mobile. Move the "×" to a 44px target.
4. **Replace inline styles with the shared primitives** (Card, Button, Pill, FieldRow) on the 4/8 spacing scale and the one type scale (heading 600 / label 500 / body 400). Buttons ≥44px with focus-visible ring, hover, pressed states (#1, #2, #9, #8).
5. **Lead field rows with the human label, demote the machine key** (#2/transform rule): show "Endpoint URL" prominently, `url` as a muted monospace caption — not the reverse.
6. **Standardize cards & elevation** (#8): one radius (8px), one border (`--border`), one shadow tier for cards and a separate tier for the panel; remove the ad-hoc `0 8px 24px` shadow if it doesn't match the system token.
7. **Make "Coming soon" tiles visibly distinct** (lower elevation / reduced opacity / lock affordance) so unbuilt ERPs (SAP Ariba, Coupa, Dynamics 365) never read as wired — reinforces offer⇔works.
8. **Restore a real usage signal** once the suppliers list (or a dedicated connectors endpoint) exposes delivery-config presence: show a tabular-figure count and a true Connected pill. Until then keep the honest "Available / needs setup" state but add a clear "Set up in supplier →" affordance per card.
9. **Replace the dead `notice` banner** with a real toast/inline confirmation system (success on test-fire, error on failure) so feedback isn't only buried in the panel.
10. **Add a real empty AND loading state for mock parity**: align the EmptyState button color to the page primary (green), and ensure the skeleton matches the final card geometry (it currently uses 20px padding vs the card's 18px — flag this drift, #1).

---

### Screenshots — PRODUCTION (8)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/21-base-desktop-1440-operations-connectors.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/21-base-hd-1920-operations-connectors.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/21-base-mobile-390-operations-connectors.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/21-base-tablet-768-operations-connectors.png)

**add-connector-panel · desktop-1440**

![add-connector-panel · desktop-1440](screenshots-prod/21-add-connector-panel-desktop-1440-operations-connectors.png)

**connector-panel-manage-connected · desktop-1440**

![connector-panel-manage-connected · desktop-1440](screenshots-prod/21-connector-panel-manage-connected-desktop-1440-operations-connectors.png)

**connector-panel-mobile-sheet · mobile-390**

![connector-panel-mobile-sheet · mobile-390](screenshots-prod/21-connector-panel-mobile-sheet-mobile-390-operations-connectors.png)

**connector-panel-test-fire-result · desktop-1440**

![connector-panel-test-fire-result · desktop-1440](screenshots-prod/21-connector-panel-test-fire-result-desktop-1440-operations-connectors.png)

---

## 22. Webhooks (outbound events) — `/operations/webhooks`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/operations/webhooks/page.tsx`
- **Key components:** all defined inline in the page file above —
  - `WebhooksLayout` (shared shell for mock + live), `EndpointsCard`, `EndpointPill`, `DeliveriesCard`, `WebhookPanel` (the add/edit modal), `CardHead`, `SkeletonEndpointRow`, inline SVG icons (`PlusIcon`, `WebhookIcon`, `CheckIcon`, `SendIcon`), `MockWebhooksPage`, `LiveWebhooksPage`.
  - Imported primitives: `EmptyState` (`src/components/bridge/EmptyState.tsx` → `MarkSystem`), `PageShell` (`src/components/bridge/layout/PageShell.tsx`, `variant="wide"` → 1480px), `PageHeader` (`src/components/bridge/layout/PageHeader.tsx`).
  - Status logic: `deriveWebhookStatus()` from `./webhookStatus.ts` (pure, unit-tested).
  - Data: `getIntegrations` / `createIntegration` / `toggleIntegration` / `deleteIntegration` / `IntegrationSubscription` / `isApiMockMode` from `src/lib/api-client.ts`.
- **Capture URL (mock):** `/operations/webhooks` — in mock mode the page renders `MockWebhooksPage`, which is pre-seeded with three local `MOCK_WEBHOOKS` rows (`w1`, `w2`, `w3`) and five `MOCK_DELIVERIES`, so no ids/query are needed. (Note: the **live** `api-client` mock array `_mockIntegrations` is empty, but this page does NOT use it — `MockWebhooksPage` keeps its own in-component state, so the page is fully populated under mock.)

### What it is & why it exists
This is the outbound side of the `learn` loop: instead of pulling order state, ProcuLink **pushes** lifecycle events (`order.created`, `order.delivered`, `order.failed`) to a buyer's own systems (their ERP, an ops ingest endpoint, etc.) over signed HTTP webhooks. It lets a customer wire ProcuLink into their internal automation so a delivered/failed PO can trigger downstream work without anyone watching the inbox. The page is both the **configuration surface** (add/enable/disable/delete endpoints) and a lightweight **monitor** (per-endpoint health pill + a recent-deliveries log).

### Who uses it & the primary job
Persona: **integration expert / operator** (a developer or technical procurement ops person), not the everyday coordinator. Primary job: **register an HTTP endpoint that should receive order events, with an optional HMAC signing secret, and confirm it's healthy.** Secondary job: glance at the deliveries log / health pills to spot a failing endpoint.

### Layout & structure (current)
Top-to-bottom, inside `PageShell variant="wide"` (max-width 1480px, gutter ramp `px-4 / sm:px-6 / lg:px-[34px]`, vertical `py-5 / sm:py-7`):

1. **`PageHeader`** — title "Webhooks" (Bricolage Grotesque, ~28–30px, weight 600), subtitle `Push order events to your systems · {N} endpoint(s)` (13px muted), and a right-aligned **green primary "Add endpoint" button** (height 32px, `--brand-green`, Plus icon). On mobile the actions slot wraps below the title.
2. **Notice banner** (conditional) — full-width blue info strip (`--brand-blue-soft` bg, 3px blue left border) showing transient feedback like "Webhook deleted." / "Endpoint added — test ping sent to …".
3. **Error banner** (conditional, live mode only) — red strip (`#F5B8B8` border, `--danger-soft` bg, `#7B1C1C` text) "Failed to load webhooks." with an underlined inline **Retry** button.
4. **Two-column split** (`.webhooks-split`, `grid-template-columns: 1fr 1fr; gap:16px; align-items:start`):
   - **Left — "Endpoints" card** (`EndpointsCard`): white card, `--radius-md` (8px), 1px `--border`. A `CardHead` (webhook icon + "Endpoints" 14px/600). Then one **row per endpoint** (padding `13px 16px`, bottom hairline between rows): line 1 = monospace URL (11.5px/600, ellipsis, `max-width:240px`) + a right-aligned **status pill**; line 2 = wrapping **event chips** (mono 10px, surface-2 bg); line 3 = "Last delivery: {x}" (11px faint) + a **hover-revealed action cluster** (Edit*/Disable·Enable/Delete).
   - **Right — "Recent deliveries" card** (`DeliveriesCard`): white card with a `CardHead` (send icon + "Recent deliveries" / sub "Last 5 attempts"). On desktop a **5-column table** (Time · Event · Order · Status · Latency); on phones (≤560px) the same rows render as **stacked row-cards**.
5. **No footer / no global action bar** — the only primary action is the header "Add endpoint" button.

Spacing/type/density observations: almost everything is **inline-styled with hardcoded px + literal hex fallbacks** rather than the shared primitives — e.g. row padding `13px 16px`, card-head `14px 16px`, font sizes `11px / 11.5px / 12px / 12.5px / 13px` mixed freely. Action buttons are 27px tall (below the 44px target). Pills use the canonical ported `.pill` classes (height 21px). The two cards are forced to equal `1fr 1fr` widths even though the deliveries table is denser than the endpoint list.

### Data shown
- **Endpoints** (`WebhookRow`): `url` (→ `targetUrl`), `events` (live mode = a single `eventType` per subscription, wrapped in an array), `status` (`healthy | failing | paused`, derived), `failureCount`, `lastDelivery` (relative time from `updatedAt`).
  - **Live source:** `GET /api/integrations` via `getIntegrations()` → `IntegrationSubscription[]` (`id, platform, eventType, targetUrl, isActive, failureCount, createdAt, updatedAt`), mapped by `toRow()`. Status = `deriveWebhookStatus(isActive, failureCount)`: `!isActive → paused`; `isActive && failureCount>0 → failing`; else `healthy`.
  - **Mock source:** local `MOCK_WEBHOOKS` (`w1` erp.company.com healthy 2m, `w2` ops.company.com healthy 8m, `w3` legacy.example failing "3 retries · 1h ago").
- **Recent deliveries** (`DeliveryRow`): `time`, `event`, `po`, `status` (HTTP code), `dur` (latency), `fail?`.
  - **Mock source:** `MOCK_DELIVERIES` (5 rows; one `503 timeout` flagged `fail:true`).
  - **Live source:** **none** — `LiveWebhooksPage` passes `deliveries={null}`, so the card always shows the "No deliveries yet" empty state in production (there is no delivery-history API).
- **Backend event vocabulary** (`WEBHOOK_EVENT_TYPES`): `order.created`, `order.delivered`, `order.failed`, each with a plain-language label.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| Header **"Add endpoint"** button (green primary) | `onAdd` | Clears notice, opens `WebhookPanel` in "new" mode (modal) |
| **Endpoint row** (`.wh-row`) | hover / focus-within | Reveals the per-row `.wh-actions` cluster (opacity 0→1) |
| Row **"Edit"** button *(mock only — hidden when `allowEdit:false` in live)* | `onEdit(row)` | Opens `WebhookPanel` pre-filled with that endpoint |
| Row **"Disable" / "Enable"** button (blue outline) | `onToggle(id)` | Live: `PATCH /api/integrations/{id}/toggle`, invalidates `["integrations"]`, notice "Webhook status updated." Mock: flips healthy↔failing. Shows "…" while pending (`togglingId`) |
| Row **"Delete"** button (red outline) | `window.confirm("Delete webhook for {url}?")` → `onDelete(id)` | Native confirm; on OK live `DELETE /api/integrations/{id}`, invalidate, notice "Webhook deleted." Shows "…" while pending (`deletingId`) |
| Error banner **"Retry"** link | `onRetry` | `queryClient.invalidateQueries(["integrations"])` (live only) |
| Modal **Endpoint URL** input (mono) | text entry | Sets `url`; required — empty disables the save button |
| Modal **Events** `<select>` | choose event type | Sets `eventType` (one of the 3 backend types) |
| Modal **Signing secret** input (mono, `whsec_••••` placeholder) | text entry | Sets `secret` (optional, sent on create) |
| Modal **Cancel** button | `onClose` | Closes panel, no save |
| Modal **"Add endpoint" / "Save changes"** button (green primary, Check icon) | `onSave(url, eventType, secret)` | Live new: `POST /api/integrations` (`platform:"webhook"`). Live edit: **no-op** — sets notice "Editing an existing endpoint isn't supported yet — delete and add a new one…" and closes. Mock: simulates 400ms then inserts/updates the row. Disabled while saving or when URL empty |
| Modal **× / Close** button (top-right, `aria-label="Close"`) | `onClose` | Closes the panel |

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Add / Edit webhook panel** (`WebhookPanel`) | Modal dialog (fixed full-screen scrim, centered ≤540px card; bottom-sheet on mobile via `items-end sm:items-center`) | Header **"Add endpoint"** (new) OR a row **"Edit"** button (mock only) | Blue icon-chip + title ("Add/Edit webhook endpoint") + subtitle; **Endpoint URL** input (required); **Events** select; **Signing secret** input + HMAC helper text; **blue info banner** ("We'll send a test ping on save… reads as failing if recent attempts don't succeed"); footer Cancel + Add/Save | **×** button, **Cancel** button, or a successful **save** (`onClose`/`setPanel(null)`). **No Esc-to-close, no backdrop-click-to-close** — the scrim is non-interactive. Scrim `rgba(11,26,47,0.42)` + `blur(3px)`; card animates in via `modal-pop` 220ms |
| **Delete confirmation** | Native `window.confirm()` dialog | Row **"Delete"** button | Browser-native "Delete webhook for {url}?" prompt | OK (proceeds to delete) / Cancel (browser-controlled; not styled) |
| **Notice banner** | Inline panel (not an overlay) | Any successful mutation, toggle, delete, or the live edit-unsupported path | Single line of feedback text | Auto-replaced by the next action; **no dismiss control and no auto-timeout** — it persists until another notice overwrites it |
| **Native browser tooltip** | Tooltip | Hovering a truncated endpoint URL (`title={w.url}`) | Full URL string | Mouse-out (browser-controlled) |

There is **one custom modal** (the webhook panel) plus the native confirm and the inline notice. No drawers, sheets, dropdowns (the Events picker is a plain `<select>`, native popup), or toasts.

### States
- **Empty:**
  - Endpoints with no rows → `EmptyState compact` "No endpoints yet" / "Add an endpoint to start receiving order events." (centered `MarkSystem` mark, Bricolage title). Note: the "next action" (Add endpoint) lives only in the page header, **not** wired into the empty state's `action` slot.
  - Deliveries with `null`/empty → `EmptyState compact` "No deliveries yet" / "Delivery attempts will appear here once webhooks start firing." This is **always shown in live mode** (no delivery API).
- **Loading:** Endpoints card swaps to **two `SkeletonEndpointRow`s** (pulse animation `skel-pulse`) while `isLoading`. There is **no `loading.tsx`** for this route (none exists in `operations/` or anywhere under `(app)`), so the initial route transition shows no skeleton — only the in-card skeleton once mounted. Deliveries card has **no loading skeleton**.
- **Error:** Live mode `isError` → the red banner with inline Retry (above). Mutation errors (`createIntegration`/`toggle`/`delete`) surface as a **notice** ("Failed to add endpoint — {msg}", "Toggle failed — {msg}", "Delete failed — {msg}"), reusing the blue notice strip — **a failure is shown in blue, not red.**
- **Success/feedback:** Inline blue notice strip for every successful op; per-button "…" pending text on toggle/delete; modal button text changes to "Saving…".

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Two equal `1fr 1fr` columns inside the 1480px shell; deliveries render as the full 5-column table; row actions hidden until hover.
- **Tablet 768:** Still two columns until the `720px` breakpoint — so at 768 it is borderline; the `1fr 1fr` split is preserved (deliveries table can feel cramped against the endpoint list at this width).
- **≤720px:** `.webhooks-split` collapses to a single column (Endpoints stacked above Recent deliveries).
- **≤560px:** Deliveries table is replaced by **stacked row-cards** (`.wh-deliv-cards`) to avoid horizontal overflow; the endpoint row meta-row stacks (`.wh-metarow` column) and actions become **always-visible, full-width, 40px tall** (`.wh-actionbtn flex:1; height:40px`). The modal becomes a **bottom sheet** (`items-end`, `rounded-t-[10px]`).
- **`@media (hover:none)`** (touch): row actions are always visible.
- Known cliff: the 720px single-column switch leaves the 720–768px band rendering two tight columns; and the 27px action buttons on desktop only grow to 44px-class size on phones, so **mid-size touch (tablet) gets the small targets**.

### Current UX issues
- **Sub-44px touch targets on desktop/tablet:** row Edit/Disable/Delete are **27px tall**; modal close is 32px; only phones (<560px) bump to 40px (still under 44px). Violates the ≥44px / visible-pressed-state bar.
- **Hover-only row actions = discoverability + a11y gap:** Edit/Disable/Delete are `opacity:0` until row hover/focus-within. On a touchpad/keyboard this hides the primary per-row operations; there's a `:focus-visible` reveal but the cluster is still invisible at rest, which reads as "nothing to do here."
- **Modal can't be dismissed by Esc or backdrop click** — only ×/Cancel. The scrim looks dismissible (blurred, dark) but isn't. Breaks the "modals have a clear escape" expectation.
- **Errors are shown in blue, not red:** mutation failures ("Toggle failed — …", "Failed to add endpoint — …") reuse the **blue info notice**, conflating failure with neutral feedback. Violates the one-status-system / red=blocking bar.
- **Notice never dismisses and never times out:** a stale "Webhook deleted." lingers until the next action; no close affordance. No real toast system.
- **Delete uses `window.confirm()`** — an unstyled native dialog, off-brand, not focus-trapped, inconsistent with the custom modal. Destructive confirm should be in-product.
- **Token drift / hardcoded literals everywhere:** the file hardcodes `--ink-faint,#5B6980` and `--brand-blue-soft,#E3EDFB` as fallbacks, but the **real tokens are `--ink-faint:#98A0AE` and `--brand-blue-soft:#EAF0F8`** — the fallbacks disagree with the design system. Dozens of inline `px`/hex values instead of the 4/8 scale (13px, 11.5px, 10.5px, 27px, 21px, etc.).
- **No tabular figures** on the deliveries table (Time, Status code, Latency) or on the "N endpoints" count — numbers are mono-font but not `font-variant-numeric: tabular-nums`, so latency/status columns can jitter.
- **Three pill states but inconsistent semantics:** "Paused" uses the **neutral** `pill-new` (grey) even though a *paused* endpoint silently drops events — and an **auto-killed-after-3-failures** endpoint also reads as the same benign grey "Paused", hiding a real problem (the very thing `deriveWebhookStatus` was written to avoid is partly re-introduced at the pill level).
- **"HTTP 200 ≠ acceptance" trap, inverted:** the deliveries log treats any non-`fail` 200 as success and the modal's info banner correctly warns about real-delivery health, but the mock data shows `order.failed` events returning `200` — fine for a webhook, yet the column header is just "Status" with no hint that 200 means "we delivered the *event*", not "the supplier accepted the *order*".
- **Cards forced to equal width:** the dense 5-column deliveries table is squeezed into the same `1fr` as the sparse endpoint list; the table wants more width.
- **No `loading.tsx`** → route-level navigation shows a blank/empty frame before the client component mounts its in-card skeleton.
- **Live "Edit" is a dead-end honesty patch:** Edit is hidden in live, and if reached it just tells the user editing isn't supported — functional gap surfaced as copy rather than a real edit/rotate-secret flow.

### Redesign recommendations (for Claude Design)
1. **Unify the feedback system into one toast/banner with semantic color + icon** — green=success, red=failure, amber=warning, neutral=info; auto-dismiss with a close button; stop rendering errors in blue. Move "Toggle failed / Delete failed / Failed to add" to the red variant. (Bars 4, 6.)
2. **Make per-row actions always visible and ≥44px** — replace the hover-reveal cluster with a persistent compact action group (or a kebab `⋮` overflow menu opening a small popover) at full size on every breakpoint; keep navy/violet focus ring. (Bars 7, 9, 10.)
3. **Replace `window.confirm` with an in-product destructive confirm dialog** — small modal, focus-trapped, red primary "Delete endpoint", clearly separated from the neutral close, naming the URL. (Destructive-action bar.)
4. **Give the modal Esc + backdrop-click close and animate-from-trigger semantics** — keep the scrim, but make it dismissible; trap focus; on save show the success in the unified toast, not a persistent banner. (Modal bar.)
5. **Fix the pill semantics so a problem never reads as benign** — keep one badge system (one shape/size/padding), but distinguish **operator-paused (neutral grey)** from **auto-deactivated after failures (red or amber "Auto-disabled — N failures")**; never let a silently-broken endpoint show grey. Add an icon/word, not color alone. (Bars 4, "never show healthy when failing".)
6. **Purge the hardcoded hex fallbacks and adopt the real tokens** — `--ink-faint:#98A0AE`, `--brand-blue-soft:#EAF0F8`, etc.; move all inline px to the 4/8 spacing scale and the canonical type scale (heading 600 / label 500 / body 400). Carry hierarchy by size+weight, not by the many faint grays currently below 4.5:1. (Bars 1, 2.)
7. **Apply tabular figures** to Time, Status, Latency, and the endpoint count so columns stop jittering and codes line up. (Bar 3.)
8. **Add a `loading.tsx` route skeleton** mirroring the two-card split, and a delivery-card loading skeleton, so navigation never shows a blank frame. (Bar 6.)
9. **Right-size the columns** — give the deliveries table more width than the endpoint list (e.g. `minmax(360px,1fr) minmax(420px,1.2fr)`), and adopt one table density (single row height, low-contrast `gray-200` gridlines, sticky header, hover) shared with other operations tables. (Bar 5.)
10. **Clarify "Status" in the deliveries log** — rename/annotate the column or add a tooltip making clear the HTTP code is the **webhook delivery** result, not supplier order acceptance; consider a green/red `.conf`-style badge with an icon. (Bar 4 + the 200≠acceptance rule.)
11. **Turn the live "edit not supported" dead-end into a real flow** — at minimum support delete-and-recreate inline ("Replace endpoint"), and a write-only **rotate signing secret** affordance (mask existing, "leave blank to keep"). Until the backend PUT exists, label the action honestly as "Replace" rather than "Edit". (Forms / write-only credentials bar.)
12. **Make the Empty state actionable** — wire the endpoints empty state's `action` slot to open the Add panel ("Add your first endpoint") instead of relying solely on the header button. (Bar 6, 7.)

---

### Screenshots — PRODUCTION (9)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/22-base-desktop-1440-operations-webhooks.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/22-base-hd-1920-operations-webhooks.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/22-base-mobile-390-operations-webhooks.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/22-base-tablet-768-operations-webhooks.png)

**add-webhook-bottom-sheet-mobile · mobile-390**

![add-webhook-bottom-sheet-mobile · mobile-390](screenshots-prod/22-add-webhook-bottom-sheet-mobile-mobile-390-operations-webhooks.png)

**add-webhook-modal-filled · desktop-1440**

![add-webhook-modal-filled · desktop-1440](screenshots-prod/22-add-webhook-modal-filled-desktop-1440-operations-webhooks.png)

**add-webhook-modal-open · desktop-1440**

![add-webhook-modal-open · desktop-1440](screenshots-prod/22-add-webhook-modal-open-desktop-1440-operations-webhooks.png)

**edit-webhook-modal-open · desktop-1440**

![edit-webhook-modal-open · desktop-1440](screenshots-prod/22-edit-webhook-modal-open-desktop-1440-operations-webhooks.png)

**row-actions-revealed-on-hover · desktop-1440**

![row-actions-revealed-on-hover · desktop-1440](screenshots-prod/22-row-actions-revealed-on-hover-desktop-1440-operations-webhooks.png)

---

## 23. Settings — `/settings`

- **File:** `src/app/(app)/settings/page.tsx` (1,506 lines — the whole hub plus most section components live inline in this one file)
- **Key components:**
  - `src/app/(app)/settings/page.tsx` — `SettingsPage` shell + left nav + 5 inline sections: `OrgSection`, `OrderDirectionSetting`, `EmailSettingsSection`, `ApiKeysSection`, `ConnectorsSection`, plus `IngressEndpointRow`, `SettingsRow`, `InlineConfirm`, `ToggleSwitch`, `FormField`
  - `src/components/settings/SettingsPrimitives.tsx` — shared `SettingsGroup` card shell + `primaryGreenButton` style
  - `src/components/settings/PullIngressSettings.tsx` — `SftpPullSettings` + `S3PullSettings` (the SFTP and S3/R2 tabs) and their shared `Shell`/`Field`/`Toggle`/`Notice`/`UpgradeNotice`/`SaveBar`/`LoadingShell`/`ErrorShell` helpers
  - `src/components/bridge/BillingSection.tsx` — `BillingSection` (the Billing & plan tab: plan card, usage bars, upgrade ladder, payment method)
  - `src/components/bridge/layout/PageShell.tsx`, `PageHeader.tsx` — page chrome
  - `src/components/bridge/BridgeLoader.tsx` — `BridgePageLoader` for `loading.tsx`
  - `src/lib/tab-param-sync.ts` — `useTabParamSync` deep-link `?tab=` handling
- **Capture URL (mock):** `/settings` (org tab default). Per-tab deep links: `/settings?tab=billing`, `/settings?tab=email`, `/settings?tab=sftp`, `/settings?tab=s3`, `/settings?tab=api`, `/settings?tab=connectors`

### What it is & why it exists
Settings is the configuration hub that sits OUTSIDE the per-order parse→normalize→validate→review→transform→deliver→learn loop but governs three of its endpoints: how orders get IN (email IMAP, SFTP pull, S3/R2 pull, REST ingress + API keys), how events flow OUT (webhook connectors to Zapier/Make.com/custom), and the commercial envelope around all of it (billing plan, order/supplier quotas). It also holds the workspace identity (org name) and the single direction switch that relabels "supplier" vs "customer" across the whole app. A procurement coordinator opens it to wire up an automated intake channel, mint an API key, upgrade off Pilot, or flip how parties are labelled.

### Who uses it & the primary job
Primarily the **operator / admin** (the person who owns the workspace and its billing), with an **integration expert** doing the API-key + webhook + SFTP/S3 wiring. The single most important task is **turning on a real automated intake channel** (email IMAP, SFTP, or S3/R2 — all gated on a paid plan and a default supplier) so orders flow in without manual upload; the second is **billing** (upgrade off Pilot to unlock those channels).

### Layout & structure (current)
Top to bottom:

1. **`PageHeader`** — title "Settings", subtitle `"{orgName} · {planLabel}"` (e.g. "Acme Procurement · Pilot plan"). Org name from Clerk `useOrganization()`, plan from `getBillingStatus`. Both fall back to `"…"` while loading.
2. **Two-column body** — a CSS grid, `gridTemplateColumns: "200px minmax(0,1fr)"`, `gap: 28`, `alignItems: start`:
   - **Left nav (`<nav>`, 200px)** — 7 vertical tab buttons, each `rounded-[6px]`, ~`13px`, with a Lucide icon. Active tab = white `--surface` card + `1px --border` + a `2px --brand-blue` left accent bar + blue icon + `--shadow-card` + `aria-current="page"`; inactive = transparent, `--ink-muted`, faint icon. Tabs: **Organization** (`Building`), **Billing & plan** (`Euro`), **Email intake** (`Mail`), **SFTP pull** (`HardDrive`), **S3 / R2 pull** (`Database`), **API keys** (`Key`), **Connectors** (`Plug`).
   - **Content column (`minmax(0,1fr)`)** — renders exactly one section based on `tab` state. Every section is framed by `SettingsGroup` (white card, `1px --border`, `--radius-md` = 8px, `--shadow-card`, header band with 14.5px/600 title + 12px muted sub, body padded `16px 18px`).

Section internals:
- **Organization** = two stacked `SettingsGroup` cards. Card 1 "Organization": workspace-name `<input>` (max-width 420), a `Members` row (real Clerk `membersCount`), an "About this workspace" sub-block (surface-2 box with a "FIXED" uppercase pill, showing `Default currency = EUR — Euro` and `Workspace region = EU` as a `<dl>`, non-editable), then a green "Save changes" button + inline feedback. Card 2 "How do you use ProcuLink?": a `role="radiogroup"` of two large radio cards (outbound "We send purchase orders to our suppliers" / inbound "We receive purchase orders from our customers") with arrow-key roving tabindex.
- **Billing & plan** = `BillingSection`: optional blocking `LimitBanner` (Pilot only), non-blocking `OverageNotice` (paid), a "Current plan" card containing the highlighted `PlanCard` (label, sub, big mono price, billing interval, status, "Change plan"/trial countdown) and two `UsageBar`s (Orders, Suppliers — gradient fill, mono `used / limit`), then plan-state-specific upgrade actions (Pilot: green "Upgrade to Growth" + monthly/annual `IntervalToggle` + Operations/Integration/Distributor secondary chips + contact-sales/book-demo links; Paid: "Upgrade to {nextPlan}"; Enterprise: contact-support paragraph), then a "Payment method" card (Manage in Stripe / Billing email) for paid+enterprise.
- **Email intake** = one `SettingsGroup` "Email intake": amber upgrade gate (when Pilot), an enable-toggle row ("Poll inbox for orders" + `ToggleSwitch`), an "IMAP mailbox" form (host/port/SSL grid `minmax(260px,1fr) 150px 150px`, then username/password 2-col, then folder/default-supplier 2-col), and a footer with a `ShieldCheck` security/last-poll note + validation/error/saved feedback + green "Save email".
- **SFTP pull** / **S3 / R2 pull** = `Shell` (= `SettingsGroup`) cards from `PullIngressSettings.tsx`: amber `UpgradeNotice` (when Pilot), a checkbox `Toggle`, credential fields (host/port/user/pwd/remote-dir for SFTP; bucket/prefix/region/access-key/endpoint/secret for S3), a `SupplierSelect` (default supplier, with no-suppliers empty state), inline `Notice`, and a `SaveBar` (hint + green Save).
- **API keys** = one `SettingsGroup` "API keys": an `IngressEndpointRow` (read-only ingress URL `{apiBaseUrl}/api/ingress/{slug}/orders` + Copy + `X-ProcuLink-Key` auth note), an optional green "new key created — copy now" banner, the keys table (desktop) / stacked cards (mobile), empty state, and a "Create key" button that reveals an inline create form.
- **Connectors** = one `SettingsGroup` "Connectors": two info rows ("REST API & webhooks" working path; "Native Zapier & Make.com apps" with a grey "Coming soon" pill), a "Webhook subscriptions" header with "Add webhook", an inline new-webhook form (platform/event/target-url/secret), error/loading/empty states, and a list of subscription rows.

**Spacing/type/density observations:** Almost entirely **inline `style={{}}`** with hardcoded numbers, not Tailwind utilities or tokens. Font sizes drift across half-pixel values: `13`, `13.5`, `12.5`, `11.5`, `10.5`, `14`, `14.5`, `19`, `24`, `28`. Paddings are ad-hoc (`11px 0`, `14px 16px`, `13px 15px`, `16px 18px`, `14px 18px`, `12px 14px`). Several control heights coexist: inputs `40` (page) vs `36` (PullIngress) vs `32` (connectors form); buttons `38`, `34`, `32`, `30`, `28`. Two different color systems are in use simultaneously: the page uses CSS variables (`--ink`, `--brand-green`, etc.), while `BillingSection.tsx` and `PullIngressSettings.tsx` hardcode hex literals (`#0B1A2F`, `#5E6779`, `#2E8E3A`, `#E5E8EE`, `#FFFFFF`, `#CBD5E1`).

### Data shown
- **Organization** — `organization.name`, `organization.membersCount` (Clerk `useOrganization()`); fixed strings "EUR — Euro" / "EU". Direction from `getOrgSettings()` → `OrgSettings { direction, slug }` (mock: `{ direction:"outbound", slug:"demo-workspace" }`); saved via `updateOrgSettings(direction)`.
- **Billing** — `getBillingStatus()` → `BillingStatus`: `plan`, `accountStatus`, `ordersThisMonth`, `orderLimit`, `suppliersUsed`, `supplierLimit`, `trialStartedAt`, `trialEndsAt`, `isTrialExpired`, `isOrderLimitReached`, `isSupplierLimitReached`, `canProcessOrders`, `canAddSupplier`, `stripeCustomerId`, `stripeSubscriptionId`, `overageOrders`, `overageAmountEur`, `nearLimit`, `atLimit`, `billingInterval`. Endpoint `GET /api/billing/status`. **Mock returns Pilot** (`plan:"pilot"`, 5/20 orders, 1/1 suppliers, 9 days trial left). Checkout via `createCheckoutSession(plan, interval)` → `POST /api/billing/checkout`; portal via `createPortalSession` → Stripe portal.
- **Email** — `getEmailSettings()` → `EmailSettings { enabled, host, port, useSsl, username, folder, defaultSupplierId, hasPassword, passwordDisplay, lastPolledAt, updatedAt }`, `GET /api/settings/email`, saved via `updateEmailSettings` (`PUT`). Supplier list from `apiClient.getSuppliers()` (`GET /api/suppliers`; mock = 4 suppliers: FastParts Inc, ElectroSupply Co, GlobalComponents, PrecisionMfg).
- **SFTP** — `getSftpSettings()` → `SftpIngressSettings { enabled, host, port, username, remoteDirectory, defaultSupplierId, hasPassword, passwordDisplay, updatedAt }`, `GET/PUT /api/settings/sftp`.
- **S3/R2** — `getS3Settings()` → `S3IngressSettings { enabled, bucketName, keyPrefix, region, accessKeyId, defaultSupplierId, hasSecretKey, secretKeyDisplay, updatedAt, serviceUrl }`, `GET/PUT /api/settings/s3`.
- **API keys** — `getApiKeys()` → `ApiKey[] { id, label, keyPrefix, isActive, createdAt, lastUsedAt, expiresAt }`, `GET /api/api-keys`. Create → `createApiKey(label)` returns `{ ...ApiKey, rawKey }` (`POST`); revoke → `revokeApiKey(id)` (`DELETE`). Ingress URL built from `apiBaseUrl` + `orgSettings.slug`. **Mock seeds an EMPTY keys array.**
- **Connectors** — `getIntegrations()` → `IntegrationSubscription[] { id, platform, eventType, targetUrl, isActive, failureCount, createdAt, updatedAt }`, `GET /api/integrations`. Create `POST /api/integrations`, toggle `PATCH /api/integrations/{id}/toggle`, delete `DELETE`. **Mock seeds EMPTY.** Event labels: `order.created`, `order.delivered`, `order.failed`. Platform labels: Zapier, Make.com, Custom.

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| Left-nav tab buttons ×7 | `setTab(id)` | Swaps the rendered section in place; sets `aria-current`. Does NOT write `?tab=` to the URL |
| **Org:** Workspace name input | controlled `setName` | Local state; disabled until Clerk hydrates |
| **Org:** "Save changes" (green) | `organization.update({ name })` via Clerk | Inline ok/err feedback; rejects empty / no-change |
| **Org:** Direction radio cards ×2 | `choose(direction)` → `updateOrgSettings` mutation | Persists; invalidates `["org-settings"]`; relabels app; arrow-key navigable |
| **Billing:** "Change plan" / "Manage in Stripe" / "Edit" (billing email) | `portalMutation.mutate()` | `window.location.href` → Stripe customer portal (leaves app) |
| **Billing:** Monthly/Annual `IntervalToggle` | `setCheckoutInterval` | Local; feeds checkout |
| **Billing:** "Upgrade to Growth"/"Upgrade to continue" (green) | `checkoutMutation.mutate({plan:"growth", interval})` | `window.location.href` → Stripe Checkout |
| **Billing:** Operations / Integration / Distributor chips | `checkoutMutation.mutate({plan, interval})` | Stripe Checkout |
| **Billing:** "Upgrade to {nextPlan}" (paid) | `checkoutMutation.mutate` | Stripe Checkout |
| **Billing:** "Contact sales" / "Book a 15-min demo →" | mailto / external link | Opens mail client / demo URL (env-gated) |
| **Billing:** Retry (error state) | `refetch()` | Re-runs billing query |
| **Email:** "Poll inbox for orders" `ToggleSwitch` | `update("enabled", v)` | Local; disabled if Pilot or no suppliers (when off) |
| **Email:** host/port/SSL/username/password/folder inputs | controlled updaters | Local form state |
| **Email:** password "Clear" button | clears + `hasPassword=false` | Write-only credential reset |
| **Email:** Default-supplier `<select>` | `update("defaultSupplierId")` | Local; empty state links to `/library/suppliers` |
| **Email:** "Save email" (green) | client-validate → `mutation.mutate` | `PUT /api/settings/email`; inline saved/err |
| **Email:** "Upgrade to Growth (€149/mo)" link | `next/link` | `/settings?tab=billing` |
| **Email:** Retry (error) | `refetch()` | Re-run email-settings query |
| **SFTP/S3:** enable checkbox `Toggle` | `setEnabled` | Local; disabled if Pilot/no-suppliers (when off) |
| **SFTP/S3:** all credential inputs | controlled setters | Local form state |
| **SFTP/S3:** `SupplierSelect` | `setDefaultSupplierId` | Local; no-suppliers empty links to `/library/suppliers` |
| **SFTP/S3:** "Save" (green) | client-validate → `save.mutate` | `PUT /api/settings/sftp` or `/s3`; inline `Notice` |
| **SFTP/S3:** "Retry connection" (error) | `refetch()` | Re-run query |
| **API keys:** ingress "Copy" | `navigator.clipboard.writeText(endpoint)` | "Copied!" 2s; disabled if no slug |
| **API keys:** new-key banner "Copy" | clipboard | "Copied!" 2s |
| **API keys:** "I've saved it, dismiss" | `setNewKey(null)` | Hides one-time key banner |
| **API keys:** "Create key" button | `setShowCreate(true)` | Reveals inline create form |
| **API keys:** create-form label input | `setNewLabel` + Enter-to-submit | Feeds create |
| **API keys:** "Create key" (form, green) | `create.mutate(label)` | `POST /api/api-keys`; shows raw-key banner; closes form |
| **API keys:** create-form "Cancel" | `setShowCreate(false)` | Closes form |
| **API keys:** "Revoke" (row) | `InlineConfirm` → `revoke.mutate(id)` | Inline confirm pair → `DELETE`; row dims + "Revoked" pill |
| **API keys:** Retry (error) | `refetch()` | Re-run query |
| **Connectors:** "Add webhook" (header + empty-state) | `setShowForm(v=>!v)` / `true` | Reveals inline webhook form |
| **Connectors:** platform/event `<select>`, target-URL/secret inputs | controlled setters | Local form |
| **Connectors:** "Save webhook" (green) | `create.mutate()` | `POST /api/integrations`; closes form |
| **Connectors:** form "Cancel" | `setShowForm(false)` | Closes form |
| **Connectors:** row "Pause"/"Resume" | `toggle.mutate(id)` | `PATCH .../toggle`; flips status pill |
| **Connectors:** row trash icon | `InlineConfirm` → `remove.mutate(id)` | Inline confirm → `DELETE` |
| **Connectors:** Retry (error) | `refetch()` | Re-run query |

### What opens / what closes
This page has **no true modals, drawers, sheets, popovers, dropdowns, or toasts**. Every "transient" surface is an **inline expand/collapse panel or an inline confirm swap** rendered within the content column — there is no scrim, no portal, no Escape handling, no focus trap, and no animate-from-trigger. The one-time API key reveal — which a normal app would model as a modal — is an inline banner.

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| Create-API-key form | Inline panel (bordered box, `showCreate` state) | "Create key" button | Label `<input>` (autofocus, Enter-submits), "Create key" + "Cancel" buttons, create error line | "Cancel", or successful create (`onSuccess` sets `showCreate=false`). **No Esc / no backdrop** |
| New-API-key reveal banner | Inline banner (green, `newKey` state) | Successful `createApiKey` | "API key created — copy it now", warning it can't be retrieved, mono raw key, "Copy" button, "I've saved it, dismiss" link | "I've saved it, dismiss" only. **No Esc, persists until dismissed; navigating tabs while mounted keeps state** |
| Add-webhook form | Inline panel (`showForm` state) | "Add webhook" (header toggle, or empty-state CTA) | Platform `<select>`, Event `<select>`, Target URL input, Signing-secret password input, "Save webhook" + "Cancel", create error | "Cancel", header toggle again, or successful create. **No Esc / no backdrop** |
| Revoke-key confirm | Inline confirm swap (`InlineConfirm`, `role="group"`) | "Revoke" link/button in a key row | Prompt "Break any integration using it?" + red "Revoke" + "Cancel" | "Cancel" (reverts to trigger) or "Revoke" (fires + reverts). **No Esc** |
| Delete-webhook confirm | Inline confirm swap (`InlineConfirm`) | Trash icon in a subscription row | Prompt "Delete this webhook?" + red "Delete" + "Cancel" | "Cancel" or "Delete". **No Esc** |
| Tooltips | Native `title=""` only | Hover on revoke button (`title="Revoke key"`), trash (`title="Delete webhook subscription"`), truncated webhook URL (`title={targetUrl}`) | Browser-native tooltip text | Mouse-out (browser-controlled) |
| Stripe Checkout / Portal | Full navigation (NOT an overlay) | Upgrade / Change plan / Manage in Stripe | External Stripe-hosted page | Leaves the app via `window.location.href` |

Inline transient feedback (not overlays): copy "Copied!" labels (2s timeout), org/email/SFTP/S3 "Saved" inline text, validation `role="alert"` lines, button label swaps ("Saving…", "Creating…", "Opening…").

### States
- **Empty:**
  - API keys: dashed-border centered empty card — `Key` icon, "No API keys yet", "Create a key to post orders to the REST API or a custom webhook integration." (shown only when not creating).
  - Connectors: dashed-border centered card — "No webhooks yet" + explanatory line + a green "Add webhook" CTA (the empty state HAS a primary action — good).
  - Default-supplier (Email/SFTP/S3 when org has zero suppliers): dashed inline box "No suppliers yet — add one first →" linking to `/library/suppliers`. (In mock mode suppliers are populated, so this empty state does NOT appear under `?tab=email`.)
  - Org "About this workspace" is a fixed info block, never empty.
- **Loading:**
  - Route-level `loading.tsx` → `BridgePageLoader label="Loading settings…"` (full-page brand loader on first navigation).
  - Email: bespoke skeleton (title bar + 3-col skeleton boxes), `role="status" aria-busy`.
  - SFTP/S3: `LoadingShell` skeleton (title + stacked field boxes).
  - Billing: bespoke skeleton (title bar + 2 pill rows).
  - API keys: 2 skeleton row-boxes, `role="status"`.
  - Connectors: a bare text line **"Loading webhooks…"** (the weakest — no skeleton).
- **Error:** Each data section has a dedicated "unavailable" panel (white card, `--danger-soft` border + 3px `--danger` left rule, reassuring "your data is safe", Retry button). Email = "Email settings are unavailable", API keys = "API keys unavailable", SFTP/S3 = "{title} settings are unavailable", Billing = "Billing is temporarily unavailable", Connectors = inline "Webhooks unavailable" panel. Mutation errors render inline (`role="alert"` or red `<p>`). Org-name save errors show inline next to the Save button.
- **Success/feedback:** All inline (no toast system). Org: "Workspace name updated." Direction: "Saved. Labels updated across the app." Email: "Email settings saved." (auto-clears after 4s). SFTP/S3: green `Notice` "…settings saved." Copy actions: "Copied!" (2s). New API key: persistent green banner until dismissed.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Two-column layout, 200px left nav + fluid content. `PageShell` is the **narrow** variant (`--container-narrow`), so content is centered and capped — at 1920 there is wide empty gutter on both sides. Desktop API-keys uses the dense `<table>`.
- **Tablet 768:** Still desktop layout above the 767px breakpoint (the media query is `max-width:767px`), so at exactly 768 the two-column grid and the table persist. The IMAP host/port/security 3-col grid can feel cramped between ~768–900px.
- **Mobile 390 (`max-width:767px`):** Left nav transforms from a vertical list into a **2-column command grid** (surface-2 rounded box, 5px padding, centered tab labels min-height 40px, last odd item spans full width). Content stacks to 1 column (`gap:16`). All inputs forced to `16px` font (prevents iOS zoom). IMAP connection grid collapses to 1 column. Connector rows wrap and the action goes full-width. API-keys table is `hidden md:table`; mobile shows stacked row-cards with a full-width "Revoke" button. Create-key and webhook form buttons go full-width.
- **Known cliffs:** `PullIngressSettings.tsx` uses Tailwind `sm:` breakpoints (640px) for its field grids while the page nav switches at 767px — so the SFTP/S3 forms reflow to multi-column at 640px, but the page is still in single-column "mobile" nav mode between 640–767px (mismatched breakpoint systems). The Billing tab is not inside `PageShell`'s mobile-input font override scope in the same way (it's a separate component with its own hex styles), so its controls don't get the `16px` mobile bump.

### Current UX issues
- **No real overlay system.** The one-time API-key reveal — a security-critical "copy this now, it's gone forever" moment — is an inline banner with no scrim, no focus trap, and no Escape. It can be scrolled past and the page can be tab-switched while it's showing. This is the single most important thing to elevate to a proper modal (BAR: modals have scrim + clear close + animate-from-trigger). Same for the create-key and add-webhook forms, which should be modals or at least properly framed disclosures.
- **Two parallel color systems.** The page uses CSS variables; `BillingSection.tsx` and `PullIngressSettings.tsx` hardcode hex literals for the same colors (`#0B1A2F` = `--ink`, `#5E6779` = `--ink-muted`, `#E5E8EE` = `--border`, `#2E8E3A` = `--brand-green`, `#CBD5E1`/`#CBD2DE`/`#D5DAEA`/`#D5DAE5` for disabled/border greys). Disabled buttons use `#CBD5E1` in some places and `var(--ink-faint)` in others — visibly inconsistent. (BAR: one token set, no gray-on-gray drift.)
- **Spacing & type scale drift.** Font sizes span `10.5/11/11.5/12/12.5/13/13.5/14/14.5/19/24/28`; control heights span `28/30/32/34/36/38/40`; input heights differ per section (40 vs 36 vs 32). No strict 4/8 rhythm. (BAR: one spacing rhythm, one type scale.)
- **Inconsistent status pills.** Connectors uses three different pill shapes for state: a rounded-999 green dot "Active" pill, a square `radius:4` grey "Paused" pill, and a square red "{n} failures" pill. API keys uses yet another square grey "Revoked" pill. The "FIXED" org pill is a fourth shape. There is no single badge system. (BAR: one badge shape/size/padding with green/amber/red/neutral semantics + icon-or-word.)
- **Numbers aren't uniformly tabular.** Billing usage figures and prices use `'JetBrains Mono'` (good), but the API-keys table dates (`createdAt`, `lastUsedAt`) and connector counts are default proportional figures, so columns can jitter. (BAR: tabular figures for all numbers/timestamps/counts.)
- **Weak loading state on Connectors.** Bare "Loading webhooks…" text vs skeletons everywhere else. (BAR: skeleton, not bare text/spinner.)
- **Two tab systems for what's conceptually one thing.** Email / SFTP / S3 are three separate top-level tabs that are all "automated intake channels" with near-identical forms (enable toggle + credentials + default supplier + save + paid-gate). This is a lot of left-nav weight for one concept and forces the user to hunt across tabs. (BAR: consolidate; one density.)
- **No primary-action dominance per screen.** Each section has its own green Save; the left nav, Stripe links, secondary plan chips, and copy buttons compete. There's no single dominant action on the hub. (BAR: one primary action per screen, ≥44px.)
- **Tap targets below 44px.** Many controls are 28–34px tall (connector Pause/Resume = 28, "Edit" billing email = 27, revoke text button has tiny padding). Only some have `min-height: var(--tap-min)`. (BAR: ≥44px interactive + visible hover/pressed.)
- **`?tab=` is not written on manual clicks.** Switching tabs via the nav doesn't update the URL, so a chosen tab isn't shareable/bookmarkable/back-restorable unless arrived at via a deep link. Mild predictability gap.
- **Honesty caveat handled well but visually buried.** "Native Zapier & Make.com apps — Coming soon" is correct (offer⇔works), but the grey "Coming soon" pill is easy to miss; the distinction between the working REST path and the not-yet path relies on subtle grey vs white card backgrounds.
- **`PullIngressSettings` field labels are 11px uppercase grey** (`#98A0AE` faint) — below comfortable contrast for label text and inconsistent with the page's 12.5px/600 `--ink-muted` `fieldLabelStyle`.

### Redesign recommendations (for Claude Design)
1. **Promote the one-time API-key reveal to a real modal** (navy/violet scrim, focus-trapped, Esc-closable, animate-from-trigger), with the key in a mono field, a single dominant green "Copy key" primary, and an explicit "I've saved it" to close. This is security-critical and currently the weakest pattern on the page. Model the create-key and add-webhook flows as the same modal component.
2. **Consolidate Email + SFTP + S3/R2 into a single "Intake channels" tab** (one section with three channel cards or sub-tabs sharing one form skeleton: enable toggle → credentials → default supplier → save). Shrinks the 7-tab nav, removes triplicated UI, and matches the "one density" bar. Keep API keys + Connectors as the "Outbound / API" grouping.
3. **Kill the second color system.** Refactor `BillingSection.tsx` and `PullIngressSettings.tsx` to use the same CSS variables as the page (`--ink`, `--ink-muted`, `--border`, `--brand-green`, `--surface`, disabled = one token). No raw hex. Fixes the visible disabled-button and label-grey inconsistencies.
4. **Adopt one status-badge component** for every state pill (Active / Paused / Revoked / Coming soon / Fixed / failure-count): one shape (rounded-full), one size/padding, green/amber/red/neutral semantics, always an icon + word, never color alone. Use red+warning-icon for "{n} failures" and never show a green "Active" when `failureCount > 0`.
5. **Standardize the form system:** one input height (40), one label style (13px/600 `--ink-muted` with helper text below and error below the field), write-only credential affordance ("•••• — leave blank to keep" + a Clear button) applied uniformly to Email/SFTP/S3 passwords. Enforce a strict 4/8 spacing scale for all paddings/gaps.
6. **Make the API-keys table the canonical dense table** (single row height, low-contrast `gray-200` gridlines, sticky header, tabular-figure dates, hover) and reuse that table for the webhook subscriptions list instead of bespoke flex rows.
7. **Apply tabular figures everywhere numbers appear** — API-key dates, last-used, failure counts, member count, usage bars (already mono), trial days. So columns and money never jitter.
8. **Raise every interactive control to ≥44px** with visible hover + pressed + focus-visible ring (the global `--brand-blue` ring exists; ensure it lands on all the inline-styled buttons). Fix the 27/28px Pause/Resume/Edit controls.
9. **Give each section one dominant primary action** (green Save, ≥44px, bottom-right of its card) and demote Stripe/secondary plan chips and copy buttons to outline/ghost. On the hub level, consider a sticky per-section save bar.
10. **Replace the bare "Loading webhooks…" text with a skeleton** matching the rest, and make every data surface's empty/loading/error states consistent (the dashed-empty + danger-rule-error + skeleton triad already exists in most sections — standardize it as one set of components).
11. **Write `?tab=` on manual tab clicks** (replaceState) so any settings sub-screen is shareable and back/forward works predictably; keep the existing deep-link sync. Add active-state + a breadcrumb-style indicator if the hub gains nested depth.
12. **Strengthen the offer⇔works distinction visually:** keep "Coming soon" but give the not-yet-published native connector a clearly disabled treatment (muted card + lock/clock icon + the badge) so it never reads as clickable, while the working REST/webhook path leads with the green primary.

---

### Screenshots — PRODUCTION (14)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/23-base-desktop-1440-settings.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/23-base-hd-1920-settings.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/23-base-mobile-390-settings.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/23-base-tablet-768-settings.png)

**api-key-one-time-reveal-banner · desktop-1440**

![api-key-one-time-reveal-banner · desktop-1440](screenshots-prod/23-api-key-one-time-reveal-banner-desktop-1440-settings.png)

**api-keys-create-form-open · desktop-1440**

![api-keys-create-form-open · desktop-1440](screenshots-prod/23-api-keys-create-form-open-desktop-1440-settings.png)

**connectors-add-webhook-form-open · desktop-1440**

![connectors-add-webhook-form-open · desktop-1440](screenshots-prod/23-connectors-add-webhook-form-open-desktop-1440-settings.png)

**mobile-nav-command-grid · mobile-390**

![mobile-nav-command-grid · mobile-390](screenshots-prod/23-mobile-nav-command-grid-mobile-390-settings.png)

**tab-api-keys-empty · desktop-1440**

![tab-api-keys-empty · desktop-1440](screenshots-prod/23-tab-api-keys-empty-desktop-1440-settings.png)

**tab-billing · desktop-1440**

![tab-billing · desktop-1440](screenshots-prod/23-tab-billing-desktop-1440-settings.png)

**tab-connectors-empty · desktop-1440**

![tab-connectors-empty · desktop-1440](screenshots-prod/23-tab-connectors-empty-desktop-1440-settings.png)

**tab-email-intake-form · desktop-1440**

![tab-email-intake-form · desktop-1440](screenshots-prod/23-tab-email-intake-form-desktop-1440-settings.png)

**tab-s3-pull · desktop-1440**

![tab-s3-pull · desktop-1440](screenshots-prod/23-tab-s3-pull-desktop-1440-settings.png)

**tab-sftp-pull · desktop-1440**

![tab-sftp-pull · desktop-1440](screenshots-prod/23-tab-sftp-pull-desktop-1440-settings.png)

---

## 24. Owner Admin — Revenue, Customers & Manual Invoicing — `/admin`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/admin/page.tsx`
- **Key components:**
  - `src/app/(app)/admin/page.tsx` (page; also defines local `MetricCard`, `SortableTh`, `StatusBadge`, `PlanBadge` helpers)
  - `src/app/(app)/admin/CreateInvoiceModal.tsx` (overlay)
  - `src/app/(app)/admin/AdjustLimitsModal.tsx` (overlay)
  - `src/components/bridge/layout/PageShell.tsx` (`variant="wide"`, 1480px)
  - `src/components/bridge/layout/PageHeader.tsx`
  - `src/components/bridge/layout/Card.tsx` (`dense` for metric cards)
  - `src/components/bridge/layout/MobileListRow.tsx` (mobile org cards)
  - `src/components/bridge/DSPrimitives.tsx` (`Button`)
  - Data: `src/lib/api/billing.ts` (re-exported via `src/lib/api-client.ts`)
- **Capture URL (mock):** `/admin` — **NOTE:** in mock mode this page renders the **non-access error state**, not the populated dashboard. `getAdminOverview()` / `getAdminOrganisations()` in `src/lib/api/billing.ts` have **no `USE_MOCK` branch** (only `setOrgLimits` is mocked). With `NEXT_PUBLIC_USE_MOCK=true` and no backend, the overview query fails its non-`AdminAccessError` path and the page shows "Could not load the admin overview." To capture the real dashboard you must run against a live API where the signed-in user is on the server-side admin allowlist. Overlay captures below assume a live-admin session OR a temporary mock that returns data.

### What it is & why it exists
This is the **platform owner's** console — it sits outside the per-order `parse → … → deliver → learn` workflow and instead reports on the business running the workflow: monthly/annual recurring revenue, account-status counts, trial→paid conversion, a per-customer table, and two operator actions (create a manual Stripe invoice, adjust an org's effective limits / extend its pilot). The owner opens it to reconcile MRR against Stripe, see which orgs are trialing/past-due/read-only, and manually intervene on a specific customer's billing or caps.

### Who uses it & the primary job
**Operator / platform owner** (not the procurement coordinator who uses the rest of the app; the page is a UX-only shell — the real gate is the backend, which 401/403s every `/api/admin/*` call). The single most important task: **monitor revenue + customer health and take a manual billing/limits action on a specific org** (create invoice, or adjust limits / extend pilot).

### Layout & structure (current)
Wrapped in `PageShell variant="wide"` (max-width `var(--container-wide)` = 1480px; gutter ramp 16→24→34px, vertical 20→28px on the `var(--bg)` canvas). Top to bottom:

1. **`PageHeader`** — title "Admin", subtitle "Revenue, customer health, and manual invoicing for the platform owner.", and a right-aligned **`+ Create invoice`** primary button (`Button variant="blue"` → actually renders brand-green per DSPrimitives, `size="md"`; disabled when there are zero orgs). On mobile the action wraps below the title.
2. **Overview metric grid** — `grid grid-cols-2 gap-3 lg:grid-cols-4` of **8 `MetricCard`s** (each a `Card dense`): MRR (with reconcile sub-line), ARR, Active, Trialing, Trial expired, Read-only, New orgs (mo), Trial → paid. Each card = uppercase 11px faint label, 24px bold tabular-nums value, optional 11.5px sub-line tinted by tone (muted / green "ok" / red "warn").
3. **Customers section** (`mt-7`) — a header row ("Customers" 18px display heading + a faint right-aligned `{n} orgs` / "Loading…" count), then EITHER an error panel, an empty panel, or the data:
   - **Desktop table** (`hidden md:block`, surface card, radius 12, `overflow-x:auto`, `min-width:920px`). Columns: Organisation (name + slug stacked), Plan, Status, MRR (right), 30d orders (right), Suppliers (right), Created, Last activity, Stripe, Actions. Header row has `var(--bg)` background; rows separated by `1px solid var(--surface-2)` top borders. `th` padding 9×14px / 10.5px uppercase 700; `td` padding 10×14px.
   - **Mobile cards** (`md:hidden flex flex-col gap-3`) — one `MobileListRow` per org: name+slug + status badge on top row; a wrapped meta row (plan badge, MRR, orders/30d, suppliers); a faint "Created … · last activity …" line; an actions row ("Adjust limits / extend pilot" + "View in Stripe ↗").
4. **No persistent footer / action bar.** Overlays render at the end of the tree.

Spacing/type/density observations: heavily **inline-styled** with hardcoded font sizes (10.5 / 11.5 / 12.5 / 13 / 18 / 24px) and pixel padding rather than the 4/8 scale; the modals hardcode hex colors (`NAVY = "#0B1A2F"`, `BLUE = "#1E66C9"`, `#56627A`, `#D9DEE8`, `#EEF0F4`, `#FBE3E3`, green `#E2F1E2/#1E6D29`) instead of CSS tokens, diverging from the page's own tokenized badges.

### Data shown
- **`AdminOverview`** (`GET /api/admin/overview`): `mrr`, `arr`, `stripeMrr` (nullable), `reconciled`, `countsByAccountStatus` (record keyed by `active`/`trialing`/`trial_expired`/`past_due`/`read_only`/`cancelled`), `newOrgsThisMonth`, `trialToPaidConversion` (fraction → `pct()`). The MRR sub-line is a reconcile note: null Stripe MRR → muted "Stripe MRR unavailable"; `reconciled` → green "Reconciled with Stripe (€X)"; else red "DB €X vs Stripe €Y — mismatch".
- **`AdminOrganisation[]`** (`GET /api/admin/organisations`): `id`, `name`, `slug`, `plan`, `accountStatus`, `stripeCustomerId` (nullable → "—" or "View ↗" Stripe link to `dashboard.stripe.com/test/customers/{id}`), `stripeSubscriptionId`, `mrrContribution` (`eurCents`), `createdAt` (`shortDate`), `lastOrderActivity` (nullable → `relativeTime`), `orderVolume30d`, `supplierCount`. Sorted client-side by `mrrContribution` desc by default.
- **`CreateAdminInvoiceResult`** (`POST /api/admin/invoices`): `invoiceId`, `hostedInvoiceUrl` (nullable), `status`.
- **`OrgLimitsResponse`** (`POST /api/admin/organisations/{id}/limits`): `effectiveOrderLimit`, `effectiveSupplierLimit`, `effectiveTrialEndsAt`, `orderLimitOverride`, `supplierLimitOverride` (this one IS mocked via `setOrgLimits` in billing.ts).

### Interactive elements

| Control | Action | Result/where it goes |
|---|---|---|
| `+ Create invoice` button (header) | `setInvoiceOrgId(null); setShowInvoice(true)` | Opens **CreateInvoiceModal** with no preselected org. Disabled when 0 orgs. |
| Metric cards (×8) | None | Static display only — not interactive. |
| `SortableTh` headers: Organisation, Plan, Status, MRR, Created, Last activity | `toggleSort(col)` | Re-sorts the org table client-side; same col toggles asc/desc; arrow glyph (▲/▼/↕) shows state. `aria-label="Sort by {label}"`. (30d orders / Suppliers / Stripe / Actions are **not** sortable.) |
| "View ↗" Stripe link (desktop row) | `target="_blank"` link | Opens that org's Stripe customer page in a new tab (TEST dashboard). "—" when no `stripeCustomerId`. |
| "Adjust limits" button (desktop row) / "Adjust limits / extend pilot" (mobile) | `setLimitsOrg(org)` | Opens **AdjustLimitsModal** for that org. |
| "View in Stripe ↗" link (mobile card) | `target="_blank"` link | Same as desktop Stripe link; only shown when `stripeCustomerId` present. |
| Access-gate CTA ("Go to sign-in" / "Back to dashboard") | `next/link` | Navigates to `/sign-in` (401) or `/bridge` (403). Only in the access-error state. |

**CreateInvoiceModal controls:** Organisation `<select>` (options labelled `name (plan)` + "— no Stripe customer"); per-line Description input, Amount-EUR input (`inputMode=decimal`), Quantity input (`inputMode=numeric`), per-line "✕" remove button (disabled at 1 line); "+ Add line item" dashed button; Currency input (uppercase, default "eur"); live Total (tabular-nums); Cancel / Create invoice footer buttons; on success → green confirmation, Invoice ID (mono), "Open hosted invoice ↗" link, "Done" button. Close "✕" top-right.

**AdjustLimitsModal controls:** Order-limit-override `NumField` (+ "Clear" checkbox), Supplier-limit-override `NumField` (+ "Clear" checkbox), Pilot/trial-window `<select>` (Leave unchanged / Extend by N days / Set an end date), conditional Extend-days input OR date input, "Clear trial end" checkbox (mutually exclusive with the select), error panel, Cancel / Save limits footer; on success → green confirmation with effective orders/suppliers/trial-ends + override rows, "Done". Close "✕" top-right.

### What opens / what closes

| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Create-invoice dialog** | Modal (`role="dialog" aria-modal="true"`, fixed inset, `rgba(11,26,47,0.55)` scrim, white panel max-w 560px, `boxShadow 0 24px 60px rgba(11,26,47,0.28)`, body-scroll lock, focus-in on first field) | `+ Create invoice` header button (`setShowInvoice(true)`) | Org `<select>`, repeatable line-item rows (desc/amount/qty/remove), "+ Add line item", currency, live total, validation error panel; on submit → success view (Invoice ID + hosted-invoice link) | Top-right "✕", **Cancel** button, **Esc** key (`keydown` handler), **backdrop mousedown** (`onMouseDown` target check); after success → **Done** button. All call `onClose` → `setShowInvoice(false)`. |
| **Adjust-limits dialog** | Modal (same a11y/scrim/shadow pattern, max-w 520px) | Row "Adjust limits" / "Adjust limits / extend pilot" button (`setLimitsOrg(org)`) | Header shows `org.name · plan · status`; order/supplier override fields with Clear checkboxes; pilot-window select + conditional inputs + Clear-trial checkbox; error panel; on submit → success view with effective limits; calls `onSaved()` → `orgsQ.refetch()` | Top-right "✕", **Cancel**, **Esc**, **backdrop mousedown**; after success → **Done**. All → `onClose` → `setLimitsOrg(null)`. |
| **Native `<select>` dropdowns** | Browser-native dropdown | Clicking the Org / Currency / Pilot-window selects inside modals | OS-rendered option list | Native (selecting an option / blur / Esc). |
| **Native date picker** | Browser-native popover | "Set an end date" → `<input type="date">` in AdjustLimitsModal | OS date picker | Native. |

There are **no toasts, no app-level drawers/sheets, no tooltips, and no row dropdown menus** — all feedback is inline within the two modals. The base page itself navigates in place / re-sorts in place; the only overlays are the two modals (plus native form widgets).

### States
- **Empty (no orgs):** Customers section renders a bordered surface panel "No organisations yet." (the `+ Create invoice` button is also disabled). Metric cards still render with zeroes.
- **Empty (access denied):** dedicated **access-gate view** — centered 480px card, 🔒 emoji in a circle, heading ("Please sign in" for 401 / "You don't have access to the admin area." for 403), explanatory paragraph, and a blue link CTA to `/sign-in` or `/bridge`. Triggered when either query throws `AdminAccessError`.
- **Loading:** **bare text** "Loading admin overview…" (muted, 14px) while `!queryEnabled || overviewQ.isLoading` — **no skeleton, no spinner**. The Customers count shows "Loading…".
- **Error (overview):** if overview errors with a non-access error (or returns undefined), a red-bordered surface panel: "Could not load the admin overview. The API may be unavailable — retry shortly." (**no retry button**). **This is the state mock mode lands in** (see capture note).
- **Error (orgs only):** if only the orgs query fails (non-access), the Customers section shows a red-bordered "Could not load organisations." panel while metric cards still render.
- **Success/feedback:** purely inline inside modals — green confirmation banner ("✓ Invoice created — status X" / "✓ Limits updated for X") then a result detail block + "Done". No global toast. After AdjustLimits success the table refetches via `onSaved`.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** content capped at 1480px and centered; metric grid is 4-up (`lg:grid-cols-4`); full org table visible (min-width 920px, no horizontal scroll until very narrow).
- **Tablet 768:** at the `md` breakpoint the desktop table is shown (`hidden md:block`) but the metric grid is still 2-up (4-up only kicks in at `lg` ≈1024px), so 768–1023px shows a 2-column metric grid above a full-width table that may need horizontal scroll near 768px (table min-width 920px > viewport).
- **Mobile 390:** metric grid collapses to 2-up; org table is replaced by stacked `MobileListRow` cards; header action wraps below the title; modals switch to top-aligned (`items-start`) full-width-minus-16px panels with `max-height 70vh` scroll. Buttons hit the 44px `--tap-min` on small screens.
- **Breakpoint cliff:** the **768–1023px band** shows a 920px-min-width table inside a 768px viewport, forcing horizontal scroll on the table even though it's the "desktop" view — the metric grid (2-up) and table (wide) are out of step because the table flips at `md` but the grid only goes 4-up at `lg`.

### Current UX issues
- **Loading is a bare text line** ("Loading admin overview…"), violating the skeleton requirement (bar 6). No `loading.tsx` exists for the route.
- **Error states have no retry affordance** — both the overview and orgs error panels are dead text; the user must reload the page (bar 6: error = reason + retry).
- **Mock mode shows the error state, not the dashboard** — because the admin GETs have no `USE_MOCK` branch. This makes the page un-demoable without a live admin backend and is a trap for the redesign capture pass.
- **Two badge systems, neither matching the app's unified badge** — `StatusBadge`/`PlanBadge` here are local 10.5px uppercase squared pills, separate from the app's `UnifiedStatusBadge`. They use color but the icon/word rule is satisfied only by text (no icon), and PlanBadge is color-flat neutral, so plan tier carries no visual weight (bar 4).
- **Modals are entirely off-token** — `CreateInvoiceModal`/`AdjustLimitsModal` hardcode hex (`#0B1A2F`, `#1E66C9`, `#56627A`, `#D9DEE8`, `#FBE3E3`, etc.) and ad-hoc radii (8/10/14) + a one-off shadow `0 24px 60px`, diverging from `var(--shadow-modal)`/tokens used elsewhere and from the page's own tokenized badges (bar 8).
- **Disabled-primary uses a custom muted blue** (`#9FB6DC`) rather than the `disabled:opacity-50` convention in `Button`, and the modal submit buttons are hand-rolled `<button>`s, not the `Button` primitive — inconsistent height, focus ring, and hover/pressed states (bars 7, 9).
- **Spacing/type drift** — values like 10.5/11.5/12.5px font sizes and 9px/14px paddings are off the 4/8 rhythm; the page mixes Tailwind classes with inline `style` for the same properties (bars 1, 2).
- **Close "✕" / remove "✕" / Stripe "↗" / sort arrows are glyph characters**, not Lucide icons; the close buttons are ~28px (`h-7 w-7`) — below the 44px tap target, and icon-only without a hover/pressed treatment (bars 9, 10).
- **Access-gate uses an emoji 🔒** rather than a Lucide lock icon, inconsistent with the rest of the icon system.
- **Sortable headers lack `aria-sort`** — they have `aria-label="Sort by X"` and a visual arrow, but no `aria-sort="ascending|descending"` on the `<th>` (bar 5).
- **MRR reconcile mismatch is shown in red** under a metric but the "Active"/"Trialing" cards never warn — health signals (past_due, cancelled, read_only) are buried as plain count cards or as a sub-line ("X past due · Y cancelled"), so a failing business state can read as neutral (bar: never show healthy when something is failing).
- **No "destructive separated / confirm-before-destroy"** treatment: AdjustLimits "Save limits" can shorten a trial or change caps with no confirm step; it's framed identically to the benign invoice flow (bar: destructive actions separated + confirm).
- **No breadcrumb / active-nav indication on the page itself** for a deep operator area (bar: nav active state + breadcrumbs for depth).

### Redesign recommendations (for Claude Design)
1. **Replace bare-text loading with a real skeleton** — 8 metric-card skeletons + a table skeleton (sticky header + ~6 ghost rows). Add a route `loading.tsx` mirroring the `PageShell`+`PageHeader` so the chrome is stable.
2. **Give both error panels a Retry button** wired to `overviewQ.refetch()` / `orgsQ.refetch()`, and state the reason + last-checked time. Keep red `--danger` token, navy/violet brand intact.
3. **Fix the mock path** so the admin GETs return seed data under `USE_MOCK` (mirror the `setOrgLimits` mock), letting the dashboard be captured/demoed without a live admin backend. (Non-design but blocks the handoff capture.)
4. **Unify the badges** — fold `StatusBadge`/`PlanBadge` into the app's one badge system: one shape/size/padding, green/amber/red/neutral semantics, a Lucide icon **or** word (never color alone), tabular figures. Map org statuses: active=green, trialing=blue/neutral, trial_expired/past_due/cancelled=amber/red, read_only=neutral. Give the plan a subtle tier ramp instead of flat neutral.
5. **Tokenize the modals** — convert `CreateInvoiceModal` + `AdjustLimitsModal` to CSS variables (navy/blue/green/danger, `--radius-md`, a single modal shadow token), and swap their hand-rolled footer/close/remove buttons for the `Button` primitive (green primary `Save`/`Create`, ghost/outline `Cancel`, ≥44px, focus-visible ring, hover/pressed). Replace `#9FB6DC` disabled with the standard `disabled:opacity-50`.
6. **Enforce one spacing + type rhythm** — round all paddings/gaps to the 4/8 scale, move inline font sizes to the type scale (heading 600 / label 500 / body 400), and carry hierarchy by size+weight not color; ensure all numeric cells (MRR, ARR, counts, 30d orders, suppliers, %s) stay `tabular-nums` (most already are — keep it).
7. **One table density + sortability** — single row height, low-contrast `gray-200` gridlines, sticky header, zebra/hover, and add `aria-sort` to the active `<th>` alongside the existing arrow. Replace the ↕/▲/▼ glyphs with Lucide `ChevronsUpDown`/`ChevronUp`/`ChevronDown`.
8. **Surface failing business health prominently** — promote past_due / cancelled / read_only and an MRR-mismatch into a clearly amber/red treatment (e.g. a small alert strip above the grid when `!reconciled` or when past_due>0), so the owner never reads a failing state as neutral.
9. **Separate + confirm destructive limit changes** — when AdjustLimits would *shorten* a trial or *lower* a cap, show a confirm step and visually distinguish it (amber), keeping benign extensions one-click.
10. **Replace glyphs with Lucide icons** (Plus for create-invoice, X for close/remove, ExternalLink for Stripe, Lock for the access gate), give icon-only buttons aria-labels (close already has one; remove-line has one — keep), and bump the modal close button to ≥44px tap area with visible hover/pressed.
11. **Resolve the 768px table cliff** — either flip the org list to `MobileListRow` cards through `lg` (so the wide table only appears ≥1024px where the metric grid is also 4-up), or make the table fluid below 920px; keep the metric grid and table breakpoints in step.
12. **Add a fixed-position bottom action bar on mobile for the primary action** (`+ Create invoice`) instead of relying on it wrapping under the title, keeping the one dominant green primary always reachable (bar 7/10).

---

### Screenshots — PRODUCTION (11)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/24-base-desktop-1440-admin.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/24-base-hd-1920-admin.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/24-base-mobile-390-admin.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/24-base-tablet-768-admin.png)

**adjust-limits-extend-trial · desktop-1440**

![adjust-limits-extend-trial · desktop-1440](screenshots-prod/24-adjust-limits-extend-trial-desktop-1440-admin.png)

**adjust-limits-modal · desktop-1440**

![adjust-limits-modal · desktop-1440](screenshots-prod/24-adjust-limits-modal-desktop-1440-admin.png)

**adjust-limits-modal-mobile · mobile-390**

![adjust-limits-modal-mobile · mobile-390](screenshots-prod/24-adjust-limits-modal-mobile-mobile-390-admin.png)

**create-invoice-modal-empty · desktop-1440**

![create-invoice-modal-empty · desktop-1440](screenshots-prod/24-create-invoice-modal-empty-desktop-1440-admin.png)

**create-invoice-modal-mobile · mobile-390**

![create-invoice-modal-mobile · mobile-390](screenshots-prod/24-create-invoice-modal-mobile-mobile-390-admin.png)

**create-invoice-modal-multiline · desktop-1440**

![create-invoice-modal-multiline · desktop-1440](screenshots-prod/24-create-invoice-modal-multiline-desktop-1440-admin.png)

**create-invoice-validation-error · desktop-1440**

![create-invoice-validation-error · desktop-1440](screenshots-prod/24-create-invoice-validation-error-desktop-1440-admin.png)

---

## 25. Confirm item codes (magic-mapping preview) — `/upload/preview/[orderId]`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/upload/preview/[orderId]/page.tsx`
- **Key components:**
  - `src/components/bridge/MagicMappingPreview.tsx` (the entire page body — ~1,435 lines; renders all states, rows, bulk-accept bar, commit bar, and the only overlay)
  - `src/components/bridge/layout/PageShell.tsx` (narrow 1040px page wrapper)
  - `src/components/bridge/layout/PageHeader.tsx` (canonical title + subtitle)
  - `src/components/bridge/parseStall.ts` (`isParseStalled` / `PARSE_STALL_THRESHOLD_MS = 90_000` — worker-down escalation logic)
  - `src/components/bridge/magicBulkAcceptSelection.ts` (`bulkAcceptCount` / `bulkAcceptLines` / `bulkAcceptResolutions` — shared count↔action selection)
  - `src/hooks/useOrderDirection.ts` (swaps "Supplier" ↔ "Customer" copy per org direction)
  - `src/hooks/use-mobile.ts` (`useIsMobile` — switches the row grid to stacked cards)
  - In-file sub-components: `SourceCell`, `ArrowBridge`, `MapsToAffordance`, `InfoDisclosure`, `confidencePill`, `rowReducer`
- **Capture URL (mock):** `/upload/preview/ord-002`
  - **`ord-002` is the correct id, not `ord-001`.** `ord-002` is `pending_review` with 4 lines: 2 already-resolved (green), 2 unresolved with AI suggestions (confidence 0.84 and 0.72) — it exercises the Accept/Edit/Reject controls, the bulk-accept bar, the confidence pills, and the InfoDisclosure popover. `ord-001` is `ready` (all 3 lines resolved) → renders the fully-mapped/no-action variant. Mock data: `mockGetMappingPreview()` → `mockGetOrderById()` in `src/lib/api-client.ts` (`mockOrders` array, lines 91–185).

### What it is & why it exists
This is **step 1 of a two-step review handoff** (breadcrumb: Upload › **Confirm codes** › Review & send), sitting between `Parse` and `Validate/Review` in the parse→normalize→validate→review→transform→deliver→learn loop. Right after a file is uploaded and parsed, it shows the procurement coordinator exactly how each uploaded order line maps to the supplier's item code, surfacing AI suggestions (with confidence + provenance) that must be explicitly Accepted, Edited, or Rejected before anything is committed. Its whole job is trust: "here is precisely what we'll send — nothing is sent yet — confirm the codes." On commit it routes the user to `/inbox/[orderId]` (step 2, full order review + send).

### Who uses it & the primary job
**Procurement coordinator** (the buyer-side approver). The single most important task: **resolve every unresolved order line to a valid supplier item code** — either by accepting/editing the AI suggestion, typing the code by hand, or bulk-accepting all suggestions — then click the dark primary button to commit and continue to the full order review.

### Layout & structure (current)
Wrapped in `<PageShell>` (narrow, max-width 1040px, gutter 16→24→34px, vertical padding 20→28px, page bg `#F6F7FA`). Top to bottom:

1. **Breadcrumb nav** (in page.tsx, not a component) — `text-[12px]`, three crumbs: `Upload` (green `#2E8E3A` link) › **Confirm codes** (navy `#0B1A2F`, weight 600, current) › Review & send (faint `--ink-faint #98A0AE`). Separators are literal `›` glyphs.
2. **PageHeader** — title "Confirm item codes" (Bricolage Grotesque, 28→30px, weight 600), subtitle "Confirm how your order lines map to supplier codes. Next, you'll review the full order and send it." (13px, `--ink-muted`).
3. **MagicMappingPreview card** — a single rounded container (`#F6F7FA` bg, 8px radius, 1px `#E5E8EE` border, `overflow:hidden`) containing four stacked regions:
   - **3a. Inner header** (`#FFFFFF`, 16/20/14px padding, bottom border): `<h2>` "Review your order mapping" (Bricolage Grotesque, 16px/600); a 12.5px muted descriptor "Here's exactly how we'll map your order to **your supplier**. Review and confirm — nothing is sent yet." with an inline uppercase **source-format pill** (`CSV`/`PDF`/`XLSX`/`CXML`/`EDI`, `#F1F3F7` bg, gray). Below it, the **bulk-accept + stats row**: purple primary "**Accept all AI suggestions**" button with a count badge, an optional outline "**Accept ≥85% only**" secondary with count, a `role="status"` bulk notice, and a right-side "`{resolved}/{total} mapped · {n} need attention`" counter (amber `#B36D14` for the unresolved fragment).
   - **3b. Column header strip** (`#F1F3F7`, bottom border, 8/16px padding). **Desktop**: a 5-column CSS grid `1fr 36px 120px 36px 1fr` → "SOURCE FIELD & VALUE" | (arrow gap) | "ORDER FIELD" (centered) | (arrow gap) | "{SUPPLIER} CODE", all 11px/700 uppercase gray. **Mobile**: collapses to a single "ORDER LINES" label.
   - **3c. Line rows** (scroll region: `overflowY:auto`, `overflowX:auto`, `maxHeight:480`). Each line is the same `1fr 36px 120px 36px 1fr` grid on desktop (stacked flex-column card on mobile). Columns: **Source cell** (`SourceCell` — buyer item code in mono blue `#1E66C9`/600, description 11.5px gray, then `×qty unit · unitPrice` in faint) → **ArrowBridge** (28×12 blue→green gradient SVG arrow) → **canonical-field label** ("SUPPLIER / ITEM CODE" + a tiny gradient "spine node" dot) → **ArrowBridge** → **supplier-code cell** (the interactive column; renders one of: already-resolved green chip + "✓ resolved", AI-suggestion block, accepted-code chip, inline edit input, or a dashed "+ Enter supplier code" button). Row background is semantic: resolved/accepted = `#F4FBF4` green tint w/ `#C8E6C9` border; has-AI-suggestion = `#FFFFF8` w/ `#E5E8EE`; no-suggestion-unresolved = `#FFFBF2` amber tint w/ `#F0D9A0` border.
   - **3d. Sticky commit bar** (`position:sticky; bottom:0`, `#FFFFFF`, top border, 14/20px). Left: "`{resolved} of {total} lines mapped — {n} still need a supplier code`" plus any commit error / hint / success line. Right: the **one dark primary button** ("Confirm mapping → review order" / "Continue to review (N unmapped)" / "Committing…" / "Committed ✓").

**Density/type/spacing observation:** almost everything is **inline `style={}` with hardcoded hex + non-4/8 px values** (1px, 11.5px, 12.5px, 9.5px, padding "2px 9px", border 1.5px, radius 4/6/8/10 mixed). It does NOT use the design tokens that `PageShell`/`PageHeader` use (`--ink`, `--ink-muted`, `--surface-*`). Mono font is `'JetBrains Mono'`; display is `'Bricolage Grotesque'`; body `'Inter'`.

### Data shown
- **Entity:** `MappingPreview` (`{ orderId, orderStatus, sourceFormat, detectedConfidence, lines[] }`) and `MappingPreviewLine[]`.
- **Per-line fields displayed:** `lineNumber`, `buyerItemCode`, `sourceFields.{description, quantity, unit, unitPrice}`, `canonicalField` (="supplierItemCode"), `resolvedSupplierCode`, `aiSuggestedSupplierCode`, `confidence` (→ pill %), `provenance` + `reason` (→ InfoDisclosure popover), `status` (`suggested`/`resolved`/`unresolved`).
- **Header fields displayed:** `sourceFormat` (uppercased pill), derived `resolved`/`unresolved`/`total` counts.
- **Source:** `apiClient.getMappingPreview(orderId)` → `GET /api/orders/{orderId}/mapping-preview` (real) / `mockGetMappingPreview` (mock). Mutations: `apiClient.commitMappings()` → `POST` resolve with `saveMappings:true` (real `realResolvePurchaseOrder`); `apiClient.acceptAiSuggestions(orderId, minConfidence)` → `POST /api/orders/{id}/accept-ai-suggestions?minConfidence=`. TanStack Query key `["mapping-preview", orderId]` (retry 1, staleTime 60s).

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| Breadcrumb "Upload" link | `next/link` | Navigates to `/upload` |
| "Accept all AI suggestions" (purple, count badge) | `bulkAccept(0)` | Commits any local edits first, then accepts+saves every suggested line server-side; invalidates `mapping-preview`/`order`/`orders`; sets bulk notice |
| "Accept ≥85% only" (outline, count badge) | `bulkAccept(0.85)` | Same path at minConfidence 0.85; only shown when `highConf > 0 && highConf < suggestedUnresolved` |
| Per-row **Accept** (green) | `dispatch({type:"accept", code: aiSuggestedSupplierCode})` | Row → `accepted` state, green code chip (local only until commit) |
| Per-row **Edit** (outline) | `dispatch({type:"edit", draft: suggestion})` | Row → inline text input with the suggestion pre-filled |
| Per-row **Reject** (red) | `dispatch({type:"reject"})` | Row → rejected; shows "Rejected ·" + "+ Enter supplier code"; excluded from bulk-accept count (B11) |
| Edit input (`type=text`, mono) | `onChange` → `set_draft`; Enter → `confirm_edit`; Esc → `reset` | Live draft; Enter confirms to accepted, Esc cancels |
| Edit **Confirm** (green, disabled when blank) | `dispatch({type:"confirm_edit"})` | Row → accepted with trimmed draft |
| Edit **Cancel** (ghost) | `dispatch({type:"reset"})` | Row → idle |
| Accepted-row **Edit** (small ghost) | `dispatch({type:"edit", draft: accepted code})` | Re-opens inline editor |
| "+ Enter {supplier} code" (dashed) | `dispatch({type:"edit", draft:""})` | Opens empty inline editor for manual entry |
| **InfoDisclosure "i"** button (per suggestion) | `setOpen(!open)` | Toggles provenance/reason popover (see overlays) |
| Confidence pill | display-only | No action (green ≥80% / amber ≥50% / gray below) |
| "check system health" link (stall state only) | `next/link` | Navigates to `/operations/health` |
| **Retry** button (error state) | `refetch()` | Re-runs the preview query |
| **Primary commit button** | If `toResolve.length===0` → `onCommitted(orderId)` (advance, no POST); else `commit(toResolve)` | On success routes to `/inbox/[orderId]`; or advances "Continue to review (N unmapped)" without committing |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **AI provenance popover** (`InfoDisclosure`) | Inline absolute-positioned popover (`role="tooltip"`) | The 16×16 circular "i" button next to a suggestion (only present when `provenance` or `reason` exists) | `provenance` + `reason` joined by newline, max-width 260px, white card, 1px `#E5E8EE` border, `0 4px 14px rgba(11,26,47,0.12)` shadow | Click the "i" again (toggle) · click outside (`pointerdown` outside `wrapRef`) · **Esc** key. No backdrop/scrim. |
| Inline edit input | Inline-panel (replaces the suggestion block in the cell) | row Edit / "+ Enter code" / accepted-row Edit | mono text input + Confirm + Cancel | Confirm (→accepted) · Cancel/Esc (→idle/reset) · Enter (→confirm) |
| Bulk notice | Inline `role="status"` text (not a toast) | `bulkAccept` success/error | "N suggestions accepted and saved · N manual codes saved." or "Accept failed: …" | Persists until next bulk action / unmount (no dismiss control) |
| Commit feedback | Inline text in the commit bar | commit success/error | "✓ Mappings committed…" (green) / error (red) / "Unmapped lines will stay flagged…" hint | Replaced on next state change; navigation away |

**There are NO true modals, dialogs, drawers, sheets, dropdowns, or toasts on this page.** The only floating overlay is the `InfoDisclosure` popover (one per suggestion); everything else is inline state swapping within a row or the bars. The page **navigates in place** to `/inbox/[orderId]` on commit and to `/operations/health` from the stall link.

### States
- **Empty:** Two distinct empties. (a) **Parse-in-progress** (`orderStatus === "parsing"`): white card, spinning blue→green gradient SVG, "Parsing your order…" + "We're reading your file… the mapping preview will appear automatically." Polls every 1.5s via `refetch`. (b) **Genuinely no lines** (parse finished, `totalLines === 0`, not failed): red "No order lines were found" + "Upload a corrected file with at least one purchase-order line." — **no actionable button/link** (dead end; you must back out manually).
- **Loading:** **A bare centered spinner** ("Loading mapping preview…", 28px gradient circle) — NOT a skeleton. Same spinner reused for parsing and commit-button states. **There is no `loading.tsx`** in this route folder (none exists anywhere under `(app)`).
- **Error:** Query error / `!preview`: centered red "Could not load mapping preview" + `error.message` + a white outline **Retry** button calling `refetch()`. Good (reason + retry).
- **Parse-stalled (>90s):** While still "parsing", an amber `role="status"` banner appears: "This is taking longer than usual. Order processing may be paused — **check system health**. We'll keep checking automatically." Polling continues; never claims failure (honesty contract G9). `failed` status auto-routes to `/inbox` via `onParseFailed`.
- **Success/feedback:** Bulk-accept inline notice (green) and commit success line "✓ Mappings committed — order is ready for processing." Then `onCommitted` → `/inbox/[orderId]`. No toast system.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** Identical — content is centered at max-width **1040px** (`PageShell` narrow), so it never uses the extra width; the card sits in a wide empty canvas. The 5-column `1fr 36px 120px 36px 1fr` grid renders with both arrows + canonical spine label.
- **Tablet 768:** `useIsMobile` (breakpoint ~768) is the switch. At/above it the desktop grid holds; the inner header bulk row uses `flex-wrap`. The fixed 120px center column + dual 36px arrow columns can crowd long codes/descriptions on narrower desktops — hence the `overflowX:auto` floor on the scroll region.
- **Mobile 390:** Each line becomes a **stacked card** (`flex-column`, gap 10): Source cell on top, a compact `MapsToAffordance` ("→ SUPPLIER ITEM CODE" mini-arrow) replacing the arrows + canonical column, then the full-width supplier-code control. Action buttons grow to `minHeight:40` / larger padding (touch). Column header collapses to "ORDER LINES". Commit bar wraps (`flex-wrap`).
- **Cliffs:** The hard `maxHeight:480` scroll region means long orders scroll *inside* a 480px box while the sticky commit bar floats — on small screens this double-scroll (page + inner) is awkward. The 1040px narrow shell wastes large-monitor space for what is effectively a wide mapping table.

### Current UX issues
- **Token bypass / inconsistent system:** the entire component is hardcoded inline hex (`#5E6779`, `#0B1A2F`, `#E5E8EE`, `#6F4FCE`, `#B36D14`…) and ad-hoc sizes, while the page wrapper uses CSS variables. It won't track theme/token changes and drifts from the rest of the app. (Bar 2, 8)
- **Spacing not on a 4/8 grid:** padding values like `2px 9px`, `5px 12px`, `1px 7px`, `16px 20px 14px`, borders `1.5px`/`3px`, radii `4/6/8/10` mixed. No single rhythm. (Bar 1)
- **Type scale sprawl:** font sizes 9.5, 10, 10.5, 11, 11.5, 12, 12.5, 13, 13.5, 16 px all appear. Hierarchy is carried partly by tiny size deltas + color rather than a clean size+weight scale. (Bar 2)
- **Numbers are not tabular:** quantities, unit prices, confidence %, and "{n}/{total} mapped" counts use proportional figures (no `font-variant-numeric: tabular-nums`), so the mono codes line up but the human numbers jitter. (Bar 3)
- **Two competing badge/pill vocabularies:** the source-format pill, the confidence pill (green/amber/gray with dot), the purple "AI" tag, the count badges, and the "✓ resolved" text-pill are five different shapes/paddings — not one status-badge system. (Bar 4)
- **Loading is a bare spinner, no skeleton; no `loading.tsx`.** Three different spinner treatments (page load, parsing, committing). (Bar 6)
- **"No order lines" empty state is a dead end** — red text with no button to re-upload or go back. (Bar 6)
- **Bulk notice + commit feedback are static inline strings with no dismiss and no toast** — easy to miss; "Accept failed: …" reads like body text, not an error surface. (Bar 6)
- **Primary action ambiguity:** there are arguably *two* primaries competing for the eye — the **purple** "Accept all AI suggestions" up top and the **dark navy** commit button at the bottom. Neither is green (the project's success/primary color) and neither is ≥44px tall on desktop. The most-repeated row actions (Accept/Edit/Reject) are 2px-padded ~20px-tall buttons — well under the 44px touch target on desktop. (Bar 7, 9)
- **InfoDisclosure is the only "tooltip" but provenance is critical trust copy** buried behind a 16px "i"; the green/amber `title=` attributes on the bulk buttons are native tooltips that never appear on touch. (Bar 9)
- **Reject has no confirm and is irreversible-feeling** (sits next to Accept at identical size), though it's only local state. Destructive-styled (red) action not separated. (Bar: destructive separation)
- **The canonical "ORDER FIELD" column always reads "SUPPLIER / ITEM CODE"** for every row — a constant label dressed up as data, adding visual noise without information; and it leads with a generic field name rather than the human source description. (Bar: lead with human field name)
- **Mobile inner-scroll (maxHeight 480) + sticky bar** creates nested scrolling on small screens.

### Redesign recommendations (for Claude Design)
1. **Rebuild on tokens, one type scale, one spacing rhythm.** Replace every inline hex with `--ink/--ink-muted/--ink-faint/--surface-*` and the navy `#0B1A2F` + violet brand vars; collapse the font sizes to the heading-600 / label-500 / body-400 scale; snap all padding/gap/radius to 4/8px. This alone makes the screen read "finished." (Bars 1, 2, 8)
2. **Pick ONE primary action.** Make the bottom commit button the single dominant, green, ≥44px primary ("Confirm mapping → review order"). Demote "Accept all AI suggestions" to a clearly-secondary outline/ghost action (keep violet as the AI accent, not as a second primary). Keep its count badge. (Bar 7)
3. **One status/confidence badge system.** Unify the confidence pill, "AI" tag, "resolved" marker, format pill, and count badges into a single pill primitive (one shape/size/padding; green/amber/red/neutral + icon-or-word). Confidence should never be color-only. (Bar 4)
4. **Make the line list one real table density.** Single row height, consistent cell padding, `gray-200` gridlines, sticky header (already gray strip — formalize it), tabular-nums on qty/price/%/counts, and a calmer arrow/spine treatment (the dual 36px arrow columns + constant "supplier item code" label are decorative — consider collapsing to one subtle connector). Lead each row with the human description, not the constant canonical field. (Bars 3, 5)
5. **Replace bare spinners with skeletons + add a real `loading.tsx`.** Skeleton the header + 3–4 row placeholders; keep the honest "Parsing…" and the 90s stall escalation, but give them the skeleton frame so the layout doesn't jump. (Bar 6)
6. **Give the empty "no lines" state an action** (Re-upload / Back to upload button) and the parse-failed path a visible reason, not just a silent route to `/inbox`. (Bar 6)
7. **Promote provenance/confidence trust copy.** Keep the violet "AI" treatment but surface reason inline (or in a larger, accessible popover with a clear close), since this is the trust core; ensure the "i" affordance is ≥24px hit area and keyboard/touch reachable (it already toggles + Esc-closes — keep that). Drop the native `title=` tooltips on the bulk buttons. (Bars 4, 9)
8. **Bump every interactive control to ≥44px with visible hover/pressed + focus-visible rings.** The Accept/Edit/Reject/Confirm/Cancel row buttons are the most-used controls and are currently ~20px tall on desktop. Add a confirm (or easy undo) affordance to Reject and visually separate it from Accept. (Bars 7, 9)
9. **Move feedback into the app's toast/status system** (or a fixed inline alert region) so "accepted and saved" / "Accept failed" / commit success aren't lost as inline body text. (Bar 6)
10. **Reconsider width + nested scroll.** Either use the wide (1480px) shell and let the table breathe, or keep narrow but remove the inner `maxHeight:480` so the page scrolls once with the sticky commit bar — especially on mobile where double-scroll is rough. Keep mobile as stacked cards (already correct), just on the unified token/badge system. (Bar 10)

---

### Screenshots — PRODUCTION (9)

**base · desktop-1440**

![base · desktop-1440](screenshots-prod/25-base-desktop-1440-upload-preview.png)

**base · hd-1920**

![base · hd-1920](screenshots-prod/25-base-hd-1920-upload-preview.png)

**base · mobile-390**

![base · mobile-390](screenshots-prod/25-base-mobile-390-upload-preview.png)

**base · tablet-768**

![base · tablet-768](screenshots-prod/25-base-tablet-768-upload-preview.png)

**ai-provenance-popover-open · desktop-1440**

![ai-provenance-popover-open · desktop-1440](screenshots-prod/25-ai-provenance-popover-open-desktop-1440-upload-preview.png)

**fully-mapped-variant · desktop-1440**

![fully-mapped-variant · desktop-1440](screenshots-prod/25-fully-mapped-variant-desktop-1440-upload-preview.png)

**mobile-stacked-cards · mobile-390**

![mobile-stacked-cards · mobile-390](screenshots-prod/25-mobile-stacked-cards-mobile-390-upload-preview.png)

**row-accepted-state · desktop-1440**

![row-accepted-state · desktop-1440](screenshots-prod/25-row-accepted-state-desktop-1440-upload-preview.png)

**row-inline-edit-input · desktop-1440**

![row-inline-edit-input · desktop-1440](screenshots-prod/25-row-inline-edit-input-desktop-1440-upload-preview.png)
