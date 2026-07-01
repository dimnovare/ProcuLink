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
