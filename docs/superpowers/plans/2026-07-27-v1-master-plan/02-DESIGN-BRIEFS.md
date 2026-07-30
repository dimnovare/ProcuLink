# Design Briefs — DB-1 … DB-7

Seven surfaces need design work, not just implementation. Each brief below is written to be **pasted directly into Claude Design** (or run through `ui-ux-pro-max` + `frontend-design` locally).

---

## Constraints that apply to EVERY brief — paste these with each one

> **Product.** ProcuLink is an outbound procurement bridge. A buyer's procurement team sends purchase orders to many suppliers, each of which requires a different file format, different item codes, and a different delivery channel. ProcuLink ingests the buyer's PO in any of nine formats, normalises it, translates buyer SKUs to supplier SKUs, surfaces only the lines it is unsure about, renders the supplier's exact required output, delivers it, and keeps per-attempt evidence.
>
> **User.** A purchasing coordinator. Not technical. Knows supplier SKUs, order volumes, and which suppliers are difficult. Does *not* know XML namespaces, ASTs, revisions, or idempotency. Works on a desktop most of the day and a tablet or phone occasionally.
>
> **LOCKED — do not redesign:**
> - The **Bridge Layer** visual direction: navy app chrome (`#0B1A2F` sidebar/topbar), warm light work area (`#F6F7FA`), blue (`#1E66C9`) = buyer/incoming, green (`#2E8E3A`) = supplier/outgoing, violet (`#6F4FCE`) = AI-generated content ONLY.
> - Fonts: Inter (UI/body 13–14px), Bricolage Grotesque (page titles, KPIs), JetBrains Mono (SKUs, PO numbers, code).
> - Radii 6/8/12px. Cards use borders + `0 1px 2px rgba(11,26,47,0.04)`, never heavy drops.
> - The **three-column Order Workshop** structure (source | canonical | output). Improve hierarchy, density and states. Do not replace the layout.
>
> **BANNED:** decorative gradient backgrounds, sparkle icons, illustrated mascots, glassmorphism, big editorial serif inside the app, modals where a drawer or inline editor works, modal wizards that hide the source document during review, "Good morning, [name]" greetings, auto-applying AI without a visible accept step, per-screen colour themes, hand-rolled icons that break the system's construction language, emoji used as icons.
>
> **Vocabulary — the only nine nouns a customer may be taught:** Order · Supplier · Item code · Order layout · Output · Delivery · Rule · Issue · Workspace. Never surface: revision, canonical, spine, dock, crossing, fingerprint, artifact, passport, AST, node, idempotency, dead-letter, unrouted, test pack, bundle.
>
> **Accessibility is a requirement, not a polish pass:** WCAG AA on text and non-text; 44px minimum tap targets; 16px minimum font on inputs (iOS zoom floor); visible focus on everything; `prefers-reduced-motion` respected; every dialog traps focus and closes on Escape.
>
> **Deliverables I need back:** (1) an annotated layout for each viewport listed; (2) the exact copy for every label, button, empty state, error state and helper line — copy is the deliverable, not lorem; (3) the state matrix (loading / empty / error / read-only / plan-gated / success); (4) which existing token each colour maps to; (5) what you deliberately left out and why.

---

## DB-1 — Information architecture & the nine-noun model
**Feeds:** WP-25, WP-26 · **Priority: highest design item in the plan**

**The problem.** The product currently teaches ~50 nouns for a job that needs 9. Six top-level nav items, of which only "Inbox" names something the user does. "Rules & formats" is a bucket of four unrelated engines. "Mapping" means three different things on the same supplier page — the UI literally apologises in body copy: *"For per-item code translations, use the Mappings tab instead."*

**Current nav (to be replaced):** Dashboard · Inbox · Drafts · Inbound (Invoices, Shipping notices) · Partners (Suppliers, Buyers, Connections) · Rules & formats (Mappings, Rules, Output templates, Standards) · Operations (System health, Exceptions, Delivery log) · Integrations (Connectors, Webhooks) · Admin · Help · Settings.

**Target:** four top-level destinations — **Orders · Suppliers · Activity · Settings** — with everything else nested. Every existing route must remain reachable (as a nested tab or a redirect); nothing may 404.

**Design this:**
1. The four-item nav, with the second level for each, and where each of the ~25 existing destinations lands.
2. The supplier detail page's tab set. It currently has seven tabs: Overview, Mappings, Catalog, PO Mapping, Delivery, Validation rules, History. Three of those are mapping-ish. Propose a reduced set using the nine-noun vocabulary — my working proposal is **Overview · Item codes · How we read their files · What we send them · Delivery · Rules · History**, but challenge it.
3. A "what is this?" affordance pattern for any concept a coordinator meets for the first time — inline, not a help-centre trip.
4. The one screen a brand-new user lands on, and what it asks them to do first.

**Give me:** the nav tree, the supplier tab set, a mapping table of old destination → new location, and the exact label copy for all of it.

---

## DB-2 — The Output Designer
**Feeds:** WP-15, WP-16 · **This is the product's differentiator. Treat it as the flagship screen.**

**The job to be done.** "My supplier REDACTED-PARTY needs a CSV with *these* columns in *this* order, with their item codes, dates as DD/MM/YYYY, CRLF line endings, and a total line only when the order is over €5,000. I want to set that up once and never think about it again."

**What exists today and works — keep it.**
- A visual tree editor over an `OutputNode` AST: nesting, repeating line groups, XML attributes, custom element names.
- **Paste a supplier's sample file → the structure is inferred.** Deterministic, no AI, no data egress, correctly identifies which repeated element is the per-line group.
- A **live preview that is byte-identical to what will be delivered** — same emitter, same data, same catalog lookup. Test-pinned.
- A raw Scriban escape hatch with an in-product expression tester.
- Date/number/currency presets (8 of them) so nobody hand-writes a format string.

**What is broken or missing — design these.**
1. **Reuse.** Today a design applies to one order and is gone on the next. There must be an unmistakable "this is now how we build every order for this supplier" moment, and a way to see *which* supplier a design governs. This is the single most important interaction on the screen.
2. **Reordering.** Nodes cannot be moved. Column order and element order can only be changed by deleting and re-adding. Needs drag *and* keyboard.
3. **Conditionals.** Currently a raw Scriban predicate typed into a text box. Needs a structured builder: *include this when [field] [is / is not / is empty / is greater than] [value]* — with a raw-expression escape for the 5% case.
4. **XML namespaces.** Currently hand-typed prefixes and URIs. Needs presets (UBL 2.1, cXML 1.2, Peppol BIS 3, custom) and an explanation of what a namespace is *in one sentence a coordinator understands*.
5. **CSV dialect.** Delimiter, quoting, encoding, line ending are hardcoded today. Needs a panel. Line ending especially — it is currently whatever OS the server runs on, which is a real interop bug.
6. **Transforms per field.** Eight transforms exist in the codebase (trim, replace, date format, concatenate, fallback, split, multiply, divide) and none are reachable from the designer. Design how a coordinator adds "join these two fields with a dash" without leaving the tree.
7. **Typed values.** Every JSON leaf currently emits as a string. `"quantity": "10"` should be able to be `"quantity": 10`.
8. **The empty state.** A user opening this for the first time with no supplier sample. What do they see?
9. **The error state.** A design that would produce an invalid document must fail *here*, at design time, not silently at delivery.

**Hard constraint you must design around:** the tree can emit **JSON, XML and CSV**. It deliberately cannot emit cXML, UBL or X12, because those need envelopes and profile identifiers that a generic tree would produce well-formed but receiver-rejected. Those formats use dedicated transformers, configured on the supplier's Delivery tab. **Design how a user discovers this without feeling blocked** — right now the format dropdown just silently omits them, and opening an existing cXML design silently rewrites it to generic XML.

**Viewports:** 1440px primary, 1024px, and an honest reduced surface at 390px (it currently collapses to single-column below 860px).

**Give me:** the full screen at each viewport; the reuse/promote interaction in detail; the conditional builder; the namespace and CSV panels; the per-field transform affordance; every state; and all copy.

---

## DB-3 — First run: sign-up to a delivered order
**Feeds:** WP-27

**The problem.** First run is 6+ screens and **dead-ends**. Path today: sign-up → select organisation → dashboard → 2-step wizard → 6-step checklist → supplier catalog tab → upload → order review → supplier delivery tab → back to the order. Step 5 (delivery) requires a supplier endpoint, credentials, or an SFTP host — i.e. it requires *another company's cooperation*, so a new user cannot finish in one sitting and never sees the product work.

**The fix to design around.** Add a terminal delivery channel that needs zero supplier cooperation — **"Email the file to an address"** and **"Download the file"** — and make one of them the default. The user should reach a *delivered* supplier-ready file on day one.

**Design this:** the shortest honest path from sign-up to a delivered file. Target ≤3 screens after sign-up. Cover: what we ask before we've earned it (as little as possible), the sample-order option vs uploading their own file, what "done" looks like, and how the user is invited to do the real thing next.

**Also design:** the checklist that persists afterwards. It currently has six steps and refuses to fabricate progress (good — keep that honesty). But it should shrink as the user completes it, not sit at the top of the dashboard forever.

**Viewports:** 1440px and 390px.

---

## DB-4 — Order Workshop density
**Feeds:** WP-28 · **Three-column layout is LOCKED — this is a hierarchy and density pass**

**The problem.** Up to **seven stacked chrome bands** render before any order data. The issue list — the reason the user opened this screen — hides behind a collapsible column. A previous pass already cut five rows to two; the remaining bands crept back.

**Design this:** the vertical budget above the three columns. My target is ≤2 bands and order data beginning within 160px of the content top at 1440px. What is in those two bands, what moves into the columns, and what is deleted.

**Also design:**
- The issue list as an always-present element on desktop, without stealing width from the three columns.
- The relationship between the three columns when the user hovers a field: today source ↔ canonical ↔ output highlighting exists; make it obvious.
- The send bar: what it says when the order is clean, when it is blocked, and when the user has an override available.

**Viewports:** 1440px, 1024px, and 390px (a purpose-built reduced surface already exists below `lg` — critique and improve it, do not replace it with a squeezed desktop).

---

## DB-5 — Inbox: make the valuable state visible
**Feeds:** WP-29

**The problem.** When a recurring PO auto-resolves every line — the product's entire economic premise — the row is labelled **"Normalized"**, has no filter chip, and offers no primary action. The most valuable thing ProcuLink does is its least visible state.

Two further defects to design out:
- The dashboard and the inbox print **different numbers under the identical label "Ready to send"** (one sums two statuses, the other one).
- The "Pipeline" column is **five unlabelled dots in 184px** with no legend, tooltip, or accessible name.

**Design this:** the inbox row and its filter set, with "Ready to send" as a first-class, visually primary state carrying a one-click send. The pipeline indicator, made legible and accessible at 184px — including what it does at 390px. The relationship between the dashboard's counts and the inbox's filters so a number is never ambiguous.

**Statuses that must be representable:** new · parsing · needs review · unrouted (no supplier identified) · ready to send · sending · delivered · delivery failed · rejected by supplier · needs your attention (dead-lettered) · delivery unconfirmed · billing paused.

**Viewports:** 1440px and 390px (a mobile card variant already exists).

---

## DB-6 — The failure & recovery system
**Feeds:** WP-24, WP-36

**The problem.** ProcuLink's delivery engine models failure carefully — twelve-plus distinct states, retries, dead-letter, an "unknown outcome" park. The UI does not keep up. A `transform_failed` order's only CTA links to itself. A dead-lettered order tells the operator to click a button that returns 400. Every deep link from the health page is inert.

**Design this as a system, not per-screen.** For every failure state: what happened (in the coordinator's words, never the engine's), whose fault it is, what happens next automatically, what the operator can do now, and what it costs them if they do nothing.

**The states, in the operator's language:**

| Internal | What the coordinator needs to understand |
|---|---|
| `failed` (parse) | We could not read this file |
| `unrouted` | We do not know which supplier this is for |
| `transform_failed` | We could not build the file this supplier needs |
| `delivery_failed` | We could not reach the supplier — we are retrying |
| `delivery_dead_letter` | We stopped retrying; you need to look |
| `rejected_by_supplier` | The supplier refused it, and why |
| `delivery_unconfirmed` | We sent it but did not get confirmation — we will not re-send without you |
| `delivery_held` | Paused because of your plan |

**Give me:** one reusable failure-panel component spec covering all eight, the copy for each, and the escalation pattern for "this needs the founder" vs "you can fix this yourself".

---

## DB-7 — Marketing truth pass
**Feeds:** WP-10 · **Copy work, minimal layout change**

**The problem.** Two pages make claims the business cannot keep.
- `/security` says *"All order data is processed and stored in EU-region infrastructure. No data leaves the region without an explicit, contracted subprocessor agreement."* Four named subprocessors are US-based, and PO line text is sent to `api.openai.com` for extraction. The landing hero stat reads `EU · Data residency`.
- `/customers` describes two pilot engagements — "Mid-market wholesaler · ~120 POs/month", "Industrial distributor · ~500 POs/month" — that the production inventory contradicts.

**Design this:** residency copy that is *checkable* — order files and the database are EU-region; named US subprocessors process specific categories under SCCs, listed on `/subprocessors` — while staying a selling point rather than a disclaimer. And a `/customers` page that is honest about having no public references yet without reading as a weakness.

**Constraint:** the landing page currently carries **no** fabricated statistics, no invented testimonial, and no fake logo wall. That restraint was a deliberate earlier cleanup and it has held. Do not reintroduce any of it.

**Also fix while here:** the landing page hardcodes nine stale palette values and ships an amber capability tile at **2.93:1 contrast** — below AA for text and below 3:1 for non-text. And its hero SVG animates forever under `prefers-reduced-motion`.
