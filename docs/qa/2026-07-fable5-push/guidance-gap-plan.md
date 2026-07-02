# Guidance-gap plan (2026-07-02)

# ProcuLink Guidance-Gap Consolidation — Ranked, Additive-Only

**Scope:** 111 raw findings → deduped to the **genuinely-missing additive fixes** after verifying current source. Roughly **40% of raw findings are already shipped** (see "Already covered" per screen) — the section-guide registry (`src/lib/section-guides.ts`), help-article registry (`src/lib/help-articles.ts`), the ValidationRules "catalog, not a gate" callout, the SupplierDockProfile "How validation works" banner, the reworked Operations Health page, and the Webhooks "test ping on save" copy already resolve many of them. Those are marked and excluded from partitions.

**Excluded per instruction (owned by another agent, appear in NO partition):** `DeliveryConfigEditor.tsx`, `CatalogSourceEditor.tsx`, `PoMappingEditor.tsx`, `OutputMappingEditor.tsx`.

**Ranking key:** P1 = actively blocks/confuses a non-technical procurement user on a core path (upload → review → send, or first supplier setup). P2 = confuses on a secondary/config surface. P3 = polish.

---

## Screen group A — Order Workshop (core send path) — HIGHEST IMPACT

Every non-technical user hits this on their first real order. All copy below is confirmed still-present in source.

### A1 · `OrderWorkshop.tsx` — reframe "Map this order" heading — **P1**
- **File:** `src/components/bridge/workshop/OrderWorkshop.tsx` (~line 503)
- **Gap:** Heading "Map this order" frames the task as infrastructure, not the user's job (review + send).
- **Fix copy:** `Review and send this order`

### A2 · `OrderWorkshop.tsx` — parsing-state jargon — **P1**
- **Lines ~396, ~462**
- **Gap:** "Parsing the document and matching the supplier's fields." / "We're extracting the line items and matching them to {supplier}." — "extracting"/"matching" are unexplained verbs.
- **Fix copy (sub, ~396):** `Reading your file and preparing it for review.`
- **Fix copy (body, ~462):** `We're reading your file and preparing it for review. This usually takes a few seconds — the page updates on its own.`

### A3 · `OrderWorkshop.tsx` — Send-disabled tooltip + delivery-failed badge — **P1**
- **Lines ~603, ~518**
- **Gap 1 (~603):** "Fill {N} required field(s) below first — they're highlighted in what we send" doesn't tell the user the Issues list is clickable.
- **Fix copy (~603):** `Fix the {N} issue(s) below — tap each one to jump to its field. Everything must be filled before you can send.`
- **Gap 2 (~518):** "⚠ Delivery failed — automatic retries used up. Open to resend." — "retries used up" is cryptic.
- **Fix copy (~518):** `⚠ Delivery didn't reach the supplier. Open the order and click "Send again" to retry.`

### A4 · `OrderWorkshop.tsx` — invoice badge consequence — **P2**
- **Lines ~112, ~116**
- **Gap:** Badge "Looks like an invoice" + title "Detected as an invoice and held for review." doesn't state the consequence or next step.
- **Fix copy (title):** `This looks like an invoice, not a purchase order. Review it carefully before sending — the supplier may reject it if they expect a PO.`

### A5 · `IssuesPanel.tsx` — jargon labels + AI-action clarity — **P2**
- **Lines ~161, ~176, ~196**
- **Gap:** "every blocker is cleared" and "Issues to resolve" use "blocker" jargon; "Resolve all suggested" hides that it's AI and whether it's safe.
- **Fix copy (~161):** `No open issues — every required field is filled and checked.`
- **Fix copy (~176):** `Before you send`
- **Fix copy (~196 button):** `Accept all AI suggestions ({N})` — add `title`: `Auto-fills supplier codes for all items using AI. You can still edit any of them afterward.`

### A6 · `SendReadinessStrip.tsx` — count tooltip — **P2**
- **Line ~100**
- **Gap:** "{N} fields to fill before sending" doesn't say which/where.
- **Fix:** add `title` on the count: `These required fields are missing or invalid. Tap each chip below to jump to its field in the mapper.`

### A7 · `MobileTriage.tsx` — summary-card labels + advanced-editing note — **P2**
- **Lines ~125, ~206, ~216, ~233, ~286**
- **Gap:** "What we received" / "What we will send" are abstract; "map output fields" is jargon; "Fix {N} to send" is terse.
- **Fix copy (~216 title):** `From your file` · (~233 title): `To the supplier`
- **Fix copy (~206 banner):** `You can check and send the order here. For advanced editing (changing how data is sent to the supplier), open this on a laptop or desktop.`
- **Fix copy (~125 / ~286 button):** `{N} issues to resolve` with helper `Tap each issue below to fix it.`

### A8 · `WorkshopStepper.tsx` — pipeline-stage jargon — **P2**
- **Line ~12**
- **Gap:** `["Parse","Normalize","Validate","Transform","Deliver"]` are infra stages.
- **Fix:** keep technical names as `title` tooltips, add plain labels: `Read` (title: reading your file) · `Check` (checking values) · `Verify` (running your rules) · `Prepare` (building the supplier's format) · `Send` (delivering to supplier).

**Already covered on this screen:** the Ready-bar green state exists; issue-status line "{N} issues · {N} blocking" already reads clearly.

---

## Screen group B — Mapper Workbench (drag-drop editor) — **P1 orientation**

Non-technical users have no reference frame for the 3-column drag-wire model. Confirmed: no top-level plain-language help exists today.

### B1 · `MapperWorkbench.tsx` — add an orientation helper row — **P1**
- **File:** `src/components/bridge/mapper/MapperWorkbench.tsx`
- **Gap:** No explanation of Incoming | Output | Preview, "wires", or the collapse chevrons.
- **Fix:** additive one-line helper above the columns (collapsible/dismissible, no layout change): `Left = the fields in this {supplier}'s order. Middle = where each one goes in the output — drag a left field onto a middle field to connect it. Right = a live preview of what the {supplier} receives. Use the ⌃ chevrons to collapse a column.`

### B2 · `IncomingPane.tsx` — pane sub-header — **P2**
- **Gap:** Users don't know incoming fields are read-only source data.
- **Fix:** pane sub-header: `These are the fields in the {supplier}'s order. You don't edit them here — you map them to the output on the right.`

### B3 · `OutgoingPane.tsx` — pane sub-header + unmapped-field tooltip — **P1**
- **Gap:** "promote/fixed value/expression" jargon; unmapped required fields flagged without a how-to-fix.
- **Fix (sub-header):** `The output the {supplier} receives. Map an incoming field to each one (drag from the left), or set a fixed value. Fields marked * are required.`
- **Fix (per unmapped required field `title`):** `This field must be set before going live — map an incoming field to it or enter a fixed value.`

### B4 · `MapperPreviewPane.tsx` — format-toggle + "unavailable" copy — **P2**
- **Gap:** Format dropdown (CSV/JSON/XML/cXML) unexplained; "unavailable in {format}" confusing.
- **Fix (label sub):** `This is exactly what the {supplier} receives. Switch formats to preview a different output type.`
- **Fix (unavailable note):** `This field isn't part of {format} output — it only applies to other formats.`

---

## Screen group C — Connections + version lifecycle — **P1 mental model**

The section guides for `/connections` and `/connections/[connectionId]` already exist and already carry `articleSlugs: ["connections"]`, and the `connections` help article exists. So "add section guide" / "add articleSlugs" raw findings are **already covered — dropped.** What remains are the in-component labels a user reads on the page itself.

### C1 · `ConnectionDetail.tsx` — humanize the read-only edit overlay — **P1**
- **File:** `src/components/connections/ConnectionDetail.tsx` (~line 298)
- **Gap:** "You're viewing the live version. Editing opens a draft you can publish." — "draft/publish" unexplained.
- **Fix copy:** `This is the live version, sending orders now. Editing makes a test copy — check it, then switch to it safely. Older versions are kept so you can go back anytime.`

### C2 · `ConnectionDetail.tsx` — BundleSummary label tooltips + friendlier values — **P1**
- **Lines ~411–444 (`BundleSummary`/`SummaryRow`)**
- **Gap:** "Input mapping / Output template / Delivery channel / Acceptance rules / Catalog" and the value "Fixed transformer" are unexplained.
- **Fix:** add `title` tooltips per row and soften values:
  - `Input mapping` → title `Whether the incoming order format is translated first (rare — usually not needed).`
  - `Output template` value `Fixed transformer` → `Standard format` (title: `The standard way we format this {supplier}'s orders — you rarely change this.`)
  - `Delivery channel` → title `Where and how the finished order is sent.`
  - `Acceptance rules` → title `The checks run before sending. "Bound" means checks are active.`
  - `Catalog` value `Live (read at send time)` → title `The {supplier}'s current product list is used each time an order is sent.`

### C3 · `HistoryDrawer.tsx` — drawer header + version-row/action tooltips — **P1**
- **File:** `src/components/connections/HistoryDrawer.tsx` (~lines 465, 201/222/234/248)
- **Gap:** Header "Every version, checks, restore, and replay" is jargon; `Test`/`Make live`/`Restore this version` buttons and `RevisionStatusBadge` states lack guidance.
- **Fix (header sub, ~465):** `See every saved version, test changes, or go back to an older one. Nothing here touches live orders — every change is reversible.`
- **Fix (Test button `title`):** `Runs a safety check against your recent orders to catch problems. Nothing is actually sent.`
- **Fix (Make live button — currently has "Run tests…" title; keep, extend):** `Applies this version to new orders. Orders already sent keep their original format. You can revert anytime.`
- **Fix (Restore button label ~248):** `Use this version` + `title`: `Makes this older version live for new orders again — just like "Make live".`
- **Note:** status-badge tooltips (Draft/Tested/Live/Previous) belong to `RevisionStatusBadge.tsx` (see C6).

### C4 · `ConnectionsList.tsx` — status-badge tooltips + header sub — **P2**
- **File:** `src/components/connections/ConnectionsList.tsx` (~lines 57, 153)
- **Gap:** "input mapping, output template, delivery and item codes — bundled and versioned" sub is jargon; `live`/`draft` badges unexplained.
- **Fix (header sub, ~57):** `Each {supplier}'s complete setup — how their orders are mapped, checked and delivered — with safe version history.`
- **Fix:** pass `title`/`aria-label` to the status badge — live: `New orders are using this version.` · draft: `A work-in-progress — not processing orders yet.`

### C5 · `ConnectionLifecycleUI.tsx` — confirm-dialog bodies — **P2**
- **File:** `src/components/connections/ConnectionLifecycleUI.tsx` (~lines 52–56)
- **Gap:** Publish/Rollback/Discard bodies use "version" abstractly; "Discard" scope unclear.
- **Fix (publish body):** `Orders from now on use this setup. Orders you've already sent keep their original format — they won't change mid-delivery. This is reversible: you can go back to a previous version anytime.`
- **Fix (rollback body):** `Go back to this older version for new orders — it becomes live again, exactly as it was. You can switch forward to a newer version later, so nothing is lost.`
- **Fix (discard title → `Delete this draft?`, body):** `Removes this test copy and its unsaved changes. Your live version stays unchanged. You can start a new test copy anytime.`

### C6 · `RevisionStatusBadge.tsx` — per-status tooltips — **P2**
- **File:** `src/components/connections/RevisionStatusBadge.tsx`
- **Gap:** Draft/Tested/Published/Archived badges shown across the drawer + list with no meaning.
- **Fix:** add `title` per status — Draft: `A work-in-progress you're editing. Test it before making it live.` · Tested/Test: `Passed its checks — ready to make live.` · Published: `Orders are using this version now.` · Archived: `An older version you can restore if needed.`

### C7 · `ReplayPanel.tsx` — plain-language result summary + row legend — **P2**
- **File:** `src/components/connections/ReplayPanel.tsx` (~lines 124, 184, ReplaySummaryHeader ~271)
- **Gap:** Card copy is already decent ("nothing is delivered or saved") but result metrics ("render errors", "Conformance") and danger-row highlighting are unexplained.
- **Fix (helper below controls, ~184 area):** `Pick a version and how many recent orders to test against. More orders = more confidence but takes longer. Start with 10 if unsure.`
- **Fix (add plain summary line above metrics in ReplaySummaryHeader):** on pass → `Good news: these recent orders would process the same way under this version — safe to go live.`; on change → `This version changes the output for {N} order(s). Review the details below before going live.`
- **Fix (add legend above the diff list):** `Red = an order that passes today would start failing. Yellow = its output would change. No colour = no impact.`

**Already covered (dropped):** section-guide entries for both connection routes; `articleSlugs:["connections"]`; the `connections` help article; the "Nothing live yet"/"Start mapping" empty state already reads clearly; the "What is a connection?" list empty state already exists in `ConnectionsList.tsx`.

---

## Screen group D — Library: item mappings + upload intake — **P1/P2**

### D1 · `MappingEditor.tsx` — import panel: concrete example + constraints — **P1**
- **File:** `src/components/bridge/MappingEditor.tsx` (~lines 807–819)
- **Gap:** "Expected columns: buyer_code, supplier_code. Existing buyer codes are updated, new rows are added." — no example, no format/encoding/extra-column answers. Confirmed still bare in source.
- **Fix copy (replace the helper `<p>`):**
  `Two columns — your buyer code, then this {supplier}'s code. A header row is optional. Example:` + a mono example block `HX-4410,ACM-PL-22` / `HX-4412,ACM-FL-08`
  `CSV or Excel (XLSX, first sheet). UTF-8 recommended. Extra columns and blank lines are ignored. New codes are added; a repeated buyer code replaces its old {supplier} code.`

### D2 · `MappingEditor.tsx` — page sub + add/edit modal sub explain the "why" — **P1**
- **Lines ~197 (`sub`), ~704 (modal subtitle)**
- **Gap:** Page never states why mappings exist or what happens after saving.
- **Fix (page sub, ~197):** `Buyer item codes (like HX-4410) are auto-translated to each {supplier}'s codes (like ACM-PL-22) on every order — set them up once and skip manual lookups. Pick a {supplier} above to start.`
- **Fix (modal subtitle, ~704):** `Connect a buyer item code to a {supplier} code. Once saved, ProcuLink applies it automatically on every future order for this {supplier}.`

### D3 · `MappingEditor.tsx` — "Source" column legend — **P3**
- **Lines ~490–495 (desktop header), ~427–474 (mobile cards)**
- **Gap:** AI/Manual/Imported/Inherited pills unexplained; confidence % context missing.
- **Fix:** add an info-icon `title` on the Source header: `How the mapping was made — AI (ProcuLink suggested it), Manual (you typed it), Imported (from a file), Inherited (reused from another {supplier}). For AI: 90%+ is high confidence.`

### D4 · `UploadWorkbench.tsx` — file-type error grouped by purpose — **P3**
- **Lines ~541, ~567**
- **Gap:** "We accept CSV, Excel, PDF, XML, EDIFACT, JSON" — EDIFACT/X12 are jargon.
- **Fix copy:** `{name} isn't supported. We read spreadsheets (Excel, CSV), PDFs, and order files (XML, EDI). Try a different file.`

### D5 · `UploadWorkbench.tsx` — sample-order card + multi-file hint — **P3**
- **Lines ~686–690, dropzone helper ~1105–1110**
- **Gap:** "Run one with an example CSV" is vague; no guidance that multi-file = one order per file.
- **Fix (sample card):** `Try it free with a sample order. We give you an example purchase order to test the whole flow — no file of your own needed, and it won't use your quota.`
- **Fix (multi-file hint under dropzone):** `Uploading several files? We create a separate order for each — all sent to the same {supplier}.`

---

## Screen group E — Supplier detail tabs + settings + operations copy — **P2/P3**

### E1 · `SupplierDockProfile.tsx` — Catalog empty state: why + example — **P1**
- **File:** `src/components/bridge/SupplierDockProfile.tsx` (~line 990)
- **Gap:** "No products yet. Import a CSV/XLSX (columns: code, name, unit, price, barcode)..." — doesn't say why a catalog matters or which columns are required.
- **Fix copy:** `No products yet. Upload this {supplier}'s product list (CSV or XLSX). ProcuLink uses it so AI only suggests real {supplier} codes and flags unknown ones. Only "code" is required; name, unit, price, barcode are optional. Example: ACM-PL-22, Hydraulic seal kit, box, 24.50.`

### E2 · `SupplierDockProfile.tsx` — Mappings-tab "what/why" eyebrow — **P2**
- **File:** `src/components/bridge/SupplierDockProfile.tsx` (Mappings tab card header)
- **Gap:** "SKU mapping" never explained on the supplier profile.
- **Fix copy (helper eyebrow above the card):** `Saved translations between your buyer codes and this {supplier}'s codes. Once saved, ProcuLink applies them automatically on every future order. Mappings are internal — never sent to the {supplier}.`

### E3 · `SupplierDockProfile.tsx` — PO-mapping "Order file layout" sub-label with example — **P2**
- **File:** `src/components/bridge/SupplierDockProfile.tsx` (~line 1605; the sub-label is in this file, the excluded `PoMappingEditor` is only mounted below it)
- **Gap:** "Map this {supplier}'s columns to ProcuLink fields" — "columns/fields" jargon, no example.
- **Fix copy:** `Tell ProcuLink how to read this {supplier}'s order files — e.g. if column A is the PO number and column C is quantity, connect each one. Set this up after uploading a sample order. For per-item code translations, use the Mappings tab instead.`

### E4 · `settings/page.tsx` (Email intake tab) — app-password + folder helpers — **P2**
- **File:** `src/app/(app)/settings/page.tsx` (~lines 697, 724, 746)
- **Gap:** IMAP host has a partial hint but password field only says "App password" (no why), folder has no helper.
- **Fix (host helper, ~697 — extend existing):** `Your provider's IMAP server — imap.gmail.com (Gmail), imap-mail.outlook.com (Outlook), or ask your IT team.`
- **Fix (add helper under password, ~724):** `Gmail and Outlook need an app-specific password, not your normal login. Generate one in your email provider's security settings.`
- **Fix (add helper under folder, ~746):** `Usually INBOX. Enter another folder name to poll it instead.`

### E5 · `operations/connectors/page.tsx` — header sub clarifies per-supplier + read-only — **P2**
- **File:** `src/app/(app)/operations/connectors/page.tsx` (~line 345)
- **Gap:** "ERP and channel integrations" doesn't explain this is a read-only per-{supplier} overview.
- **Fix copy:** `ERP and channel integrations — one delivery setup ("connector") per {supplier}. This page is read-only; set up the real endpoint on the {supplier}'s Delivery tab, then test-fire here.`

### E6 · `operations/webhooks/page.tsx` — signing-secret plain-language + status legend — **P2**
- **File:** `src/app/(app)/operations/webhooks/page.tsx` (~lines 706, 211–227)
- **Gap:** "We sign every payload with HMAC-SHA256 using this secret." assumes the reader knows what that buys them; Healthy/Failing/Paused pills unexplained.
- **Fix (secret helper, ~706):** `Optional but recommended. If you set a secret, we sign every message so your system can confirm it really came from ProcuLink.`
- **Fix (add `title` to EndpointPill):** Healthy: `Recent deliveries succeeded.` · Failing: `Recent attempts returned errors.` · Paused: `You disabled it, or it auto-paused after 3 failures.`

**Already covered (dropped from this group):** Webhooks "test ping on save" banner and the event-type dropdown labels; the SupplierDockProfile validation-tab "How validation works" banner (Error blocks / Warning flags / gate / examples) already exists; ValidationRules "catalog, not a gate" callout + supplier link; RuleDefinitions "Bind and enforce on the supplier's Validation rules tab" callout; Operations Health "Order processing is running/paused", "Stuck reading the file", "Try sending again" vs "Open to fix & resend"; Exceptions "Fixing the cause clears the exception…" row copy; Buyers "After creating, upload a sample PO…"; the Connections-list empty state; Standards/Templates page subs are acceptable (minor, deprioritized as P3-not-worth-a-partition).

---

## Deprioritized / dropped (verified already-shipped or duplicate)
- All "add a section-guide entry for X" and "add articleSlugs: [connections]" findings — the registry already contains them.
- Validation rule-editor field-by-field operator glossary (`SupplierDockProfile.tsx` rule editor) — the "How validation works" banner already frames Error vs Warning; a full operator glossary is a larger doc/help-article change, out of additive scope → route to the `validation-rules` help article instead, not an inline UI change.
- Two-mapping-concepts explainer on `/library/mappings` — the section guide + D2 page-sub already distinguish item-code vs PO-field mappings.
- Health "dead-letter transient vs rejection" — already handled by the `canRedeliver` split (`Try sending again` vs `Open to fix & resend`).


## Partitions

```json
[
  {
    "batch": "Order Workshop send-path copy (P1/P2 — highest user impact, all in one component tree, no overlap with other batches)",
    "files": [
      "src/components/bridge/workshop/OrderWorkshop.tsx",
      "src/components/bridge/workshop/IssuesPanel.tsx",
      "src/components/bridge/workshop/SendReadinessStrip.tsx",
      "src/components/bridge/workshop/MobileTriage.tsx",
      "src/components/bridge/workshop/WorkshopStepper.tsx"
    ],
    "fixes": [
      "A1: heading 'Map this order' → 'Review and send this order'",
      "A2: parsing sub + body copy remove 'extracting/matching' jargon",
      "A3: send-disabled tooltip names clickable Issues list; delivery-failed badge explains state+action",
      "A4: invoice badge title states consequence + next step",
      "A5: IssuesPanel 'blocker' jargon → plain; 'Resolve all suggested' → 'Accept all AI suggestions (N)' + safety tooltip",
      "A6: SendReadinessStrip count tooltip",
      "A7: MobileTriage 'From your file'/'To the supplier' + advanced-editing note + button copy",
      "A8: WorkshopStepper plain labels with technical-name tooltips"
    ]
  },
  {
    "batch": "Mapper Workbench orientation + pane sub-headers (P1 — separate mapper/ dir, no overlap; excludes PoMappingEditor/OutputMappingEditor which are owned elsewhere)",
    "files": [
      "src/components/bridge/mapper/MapperWorkbench.tsx",
      "src/components/bridge/mapper/IncomingPane.tsx",
      "src/components/bridge/mapper/OutgoingPane.tsx",
      "src/components/bridge/mapper/MapperPreviewPane.tsx"
    ],
    "fixes": [
      "B1: additive collapsible orientation helper above the 3 columns (Incoming/Output/Preview + wires + chevrons)",
      "B2: IncomingPane sub-header — fields are read-only source",
      "B3: OutgoingPane sub-header + per-unmapped-required-field title tooltip",
      "B4: MapperPreviewPane format-toggle sub + 'unavailable in {format}' plain copy"
    ]
  },
  {
    "batch": "Connections version-lifecycle copy (P1/P2 — all under connections/ dir, self-contained)",
    "files": [
      "src/components/connections/ConnectionDetail.tsx",
      "src/components/connections/HistoryDrawer.tsx",
      "src/components/connections/ConnectionsList.tsx",
      "src/components/connections/ConnectionLifecycleUI.tsx",
      "src/components/connections/RevisionStatusBadge.tsx",
      "src/components/connections/ReplayPanel.tsx"
    ],
    "fixes": [
      "C1: humanize read-only edit overlay (draft→test copy language)",
      "C2: BundleSummary label title tooltips + 'Fixed transformer'→'Standard format'",
      "C3: HistoryDrawer header sub + Test/Make-live/Restore('Use this version') tooltips+label",
      "C4: ConnectionsList header sub + live/draft badge tooltips",
      "C5: ConnectionLifecycleUI publish/rollback/discard bodies rewritten plain; discard title→'Delete this draft?'",
      "C6: RevisionStatusBadge per-status title tooltips",
      "C7: ReplayPanel controls helper + plain-language pass/change summary + red/yellow row legend"
    ]
  },
  {
    "batch": "Library mappings + upload intake copy (P1/P3 — MappingEditor + UploadWorkbench, distinct bridge components)",
    "files": [
      "src/components/bridge/MappingEditor.tsx",
      "src/components/bridge/UploadWorkbench.tsx"
    ],
    "fixes": [
      "D1: import panel concrete CSV example + format/encoding/extra-column constraints",
      "D2: page sub + add/edit modal subtitle explain why mappings exist + what happens on save",
      "D3: 'Source' column legend tooltip (AI/Manual/Imported/Inherited + confidence)",
      "D4: file-type error grouped by purpose (spreadsheets/PDFs/order files)",
      "D5: sample-order card reworded + multi-file 'one order per file' hint"
    ]
  },
  {
    "batch": "Supplier tabs + settings + ops page copy (P1/P2 — SupplierDockProfile self-guidance, settings email helpers, ops connectors/webhooks subs; disjoint file set)",
    "files": [
      "src/components/bridge/SupplierDockProfile.tsx",
      "src/app/(app)/settings/page.tsx",
      "src/app/(app)/operations/connectors/page.tsx",
      "src/app/(app)/operations/webhooks/page.tsx"
    ],
    "fixes": [
      "E1: Catalog empty state — why a catalog matters + required-vs-optional columns + example",
      "E2: Mappings-tab 'what are saved SKU mappings' helper eyebrow",
      "E3: PO-mapping 'Order file layout' sub-label with concrete column example (parent-level copy only; PoMappingEditor untouched)",
      "E4: Email tab — extend host hint, add app-password + folder helpers",
      "E5: connectors header sub — per-supplier + read-only clarification",
      "E6: webhooks signing-secret plain-language helper + Healthy/Failing/Paused pill tooltips"
    ]
  }
]
```
