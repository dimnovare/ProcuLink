# DB-1 — Information architecture and the nine-noun model

_Design spec produced 2026-07-30 from 02-DESIGN-BRIEFS.md. Feeds the packets named in the brief._

## Code actually read

- src/components/bridge/BridgeSidebar.tsx:52-96 — NAV_MAIN: 10 items across 4 group headers (Dashboard / Workbench: Inbox, Drafts, Inbound / Library: Partners, Rules & formats / Monitor: Operations, Integrations) + tail Admin, Help & support, Settings. Comment at :73-77 admits the 'Operations' group header was renamed to 'Monitor' only because it collided with the 'Operations' item directly below it.
- src/components/bridge/BridgeSidebar.tsx:111-141 — buildVisibleNav(counterpartyPlural, isAdmin, opts): filters by INBOUND_ENABLED, admin probe, LAUNCH_CORE_HREFS; relabels /library/suppliers to the direction word (display only).
- src/components/bridge/BridgeSidebar.tsx:149-160 — hubTooltip() joins a hub's tab labels; isItemActive() lights hub items for any route in the hub via hubForPath.
- src/components/bridge/layout/HubTabs.tsx:32-65 — HUB_LABELS + HUB_TABS for five hubs: partners (Suppliers/Buyers/Connections), rules-formats (Mappings/Rules/Output templates/Standards), operations (System health/Exceptions/Delivery log), integrations (Connectors/Webhooks), inbound (Invoices/Shipping notices).
- src/components/bridge/layout/HubTabs.tsx:74-87 — hubShowsTabs() suppresses a lone-tab strip (the founder-reported 'double navbar'); hubForPath() also matches each tab's `match` aliases.
- src/components/bridge/BridgeTopbar.tsx:592-603, 838-862, 864-874 — three stacked rows: Row 1 utility 52px, Row 2 primary nav 42px (hidden md:flex, overflow-x auto with a mask fade), Row 3 context 38px. Comment at :838-843 says the 6 items + 'Rules & formats' need ~540px and cannot fit in Row 1.
- src/components/bridge/BridgeTopbar.tsx:542-550 — onOrderWorkshop and loneCrumbRow already suppress the context row where it would duplicate the nav.
- src/components/bridge/BridgeTopbar.tsx:340-347, 356-358 — notification badge uses #B36D14 as a 9.5px background AND as text ('{unread} need action') on white = 4.11:1, below AA.
- src/components/bridge/SupplierDockProfile.tsx:105-115 — TABS: Overview, Mappings, Catalog, PO Mapping, Delivery, 'Validation rules' (id acceptance), History.
- src/components/bridge/SupplierDockProfile.tsx:1630-1641 — the PO Mapping sub-label: 'Order file layout … Tell ProcuLink how to read this supplier's order files … For per-item code translations, use the Mappings tab instead.' — the UI apologising for its own IA.
- src/components/bridge/SupplierDockProfile.tsx:1374-1399 — the tab strip is a bare div of buttons: no role=tablist/tab, no aria-selected, no arrow-key handling, no explicit focus ring; a right-edge fade signals horizontal overflow at 7 tabs.
- src/components/bridge/SupplierDockProfile.tsx:230-244, 580, 1535 — visible strings 'Active rule bindings', 'Loading rule bindings…', "Couldn't load rule bindings.", 'Warning' in #B36D14 (4.11:1), 'Saved SKU mappings'.
- src/app/(app)/settings/page.tsx:35-45 — seven tabs: Organization, Billing & plan, Email intake, SFTP pull, S3 / R2 pull, API keys, Connectors.
- src/app/(app)/settings/page.tsx:1370 — the settings 'Connectors' group is event integrations ('send real-time events to Zapier, Make.com, or any webhook URL'), duplicating /operations/webhooks.
- src/app/(app)/settings/page.tsx:951 — 'Your ingress endpoint' rendered to users.
- src/app/(app)/settings/page.tsx:53-66, 80-109 — useTabParamSync validates ?tab= against the union; the left nav is a 200px column at md+, horizontal scroll on mobile.
- scripts/check-vocabulary.mjs:43-46 — SCAN_DIRS is only src/app/(app) and src/app/(marketing); src/components and src/lib label registries are invisible to the gate.
- scripts/check-vocabulary.mjs:50-65, 71-81 — RETIRED is the seven metaphor words; FILE_ALLOWLIST and PHRASE_ALLOWLIST are both empty, i.e. the metaphor purge is finished and the gate now guards nothing live.
- scripts/check-vocabulary.mjs:123-182 — visibleSpans() extracts JSX text, aria-label/title/placeholder/alt/label attrs, `label:` values and toast strings; visibleSpansMdx() tests startsWith('```') per line, so lines inside a fence are still scanned as prose.
- src/components/bridge/UnifiedStatusBadge.tsx:30-35, 92-138 — documents the load-bearing distinction: ready → 'Normalized', ready_to_deliver → 'Ready to send'; they were previously both 'Ready', which made the 'Ready to send' chip read 0 while rows said 'Ready'.
- src/components/bridge/InboxView.tsx:78-105 — STATUS_PRESENTATION: New, Extracting, 'Needs supplier' (unrouted), 'Needs review', 'Normalized' (ready), 'Ready to send' (delivering slot), Sending, Delivered, 'Delivery paused', 'Delivery unknown', Failed.
- src/components/bridge/InboxView.tsx:383, 425-435 — FILTER_CHIPS: All orders / Needs review / Ready to send / Delivered / Failed; '?status=failed is expanded SERVER-side against the backend's set' — the mechanism a renamed 'Ready to send' chip needs.
- src/components/bridge/BridgeSidebar.test.tsx:187-205 — the reachability guard: for 22 LEGACY_ROUTES, direct || viaHub || viaPinned must hold. Also :144-157 pins the exact group/label structure and :229-235 pins hubTooltip strings, both of which a nav redesign must update.
- src/lib/launch-flags.ts:15-53 — LAUNCH_CORE_ONLY (default on) + LAUNCH_CORE_HREFS narrow the nav to 12 hrefs; INBOUND_ENABLED gates /inbound separately.
- src/components/bridge/OnboardingWizard.tsx:455-540 — role=dialog aria-modal with backdropFilter blur(4px); no Escape handler and no focus trap (grep for Escape/focus in the file returns nothing).
- src/components/bridge/OnboardingWizard.tsx:145-165 — the first question a new user is asked is 'How do you use ProcuLink?' with the explanation 'This sets how parties are labelled…' — a data-model question, not a work question.
- src/components/bridge/buildChecklistSteps.ts:1-120 — six server-verified setup steps (supplier, catalog, upload, resolve, delivery, send) with per-step href/cta and honesty rules; reusable as the Setup screen's model.
- src/components/bridge/StandardsFieldPopover.tsx:11-60 — the existing inline-explainer: a Radix Popover behind a 15x15px circled 'i' whose only affordance is a hover colour change (fails the 44px target and is invisible on touch).
- src/components/bridge/layout/PageHeader.tsx:26-45, 88-91 — titleHidden keeps an sr-only h1 when the topbar tab already names the page; HubEyebrow prints the hub word a third time in-content.
- src/components/bridge/layout/HubEyebrow.tsx:16-33 — renders HUB_LABELS[hub] as a 9.5px uppercase eyebrow on every hub route, duplicating the topbar's hub prefix crumb.
- src/components/bridge/breadcrumb.ts:17-43 — CRUMB_LABELS maps segments to the old vocabulary (bridge→Dashboard, rules→'Validation rules', templates→'Output templates', log→'Delivery log', health→'System health', asns→'ASNs'); :155-163 marks library/operations/inbound as unlinked group roots because they 404.
- src/app/(app)/operations/connectors/page.tsx:1-40 — a read-only grid of channel TYPES (SAP Ariba/Coupa marked coming_soon, SFTP/IMAP/Erply real); no configuration happens here — supplier Delivery owns it.
- src/components/bridge/MappingEditor.tsx:196-210 — /library/mappings is the buyer→supplier item-code library ('Buyer item codes (like HX-4410) are auto-translated to each supplier's codes (like ACM-PL-22)').
- src/components/bridge/ValidationRules.tsx:309-318 — /library/rules is 'Rule catalog … Enforcement is configured per supplier', i.e. a catalog whose teeth live on the supplier tab.
- src/components/bridge/RuleDefinitionsCatalog.tsx:145-165 — /library/rule-definitions is a read-only 'reusable validation rule catalog' — the building blocks the supplier rules bind to, shipped as a sibling page under the same tab.
- src/app/(app)/drafts/page.tsx:36-61 + src/lib/section-guides.ts — /drafts ships with copy admitting 'saving drafts isn't available yet'.
- src/components/connections/SupplierHistoryTab.tsx:48 and src/components/connections/ReplayPanel.tsx:139,227,519 — the History tab is titled 'Version history'; visible copy already says 'No versions yet' / 'This version' (revision→version is largely done), but aria-label='Replaying orders' still leaks 'replay'.
- src/components/bridge/OutputStructureDesigner.tsx:547,587,633 — user-visible 'Node name', 'Remove node', "Click to edit this element's XML namespace".
- src/hooks/useOrderDirection.ts:36-58 — partyLabels() swaps Supplier/Customer display words only; routes and entity names stay 'supplier'.
- src/app/globals.css:33-64,117 — --surface-2 #F1F3F7, --border #E5E8EE, --ink-muted #5E6779, --ink-faint #667085, --amber-text #8A5310, --danger #B43838, --tap-min 44px.
- next.config.ts:42-66 — the existing legacy-redirect pattern (/dashboard→/bridge, /orders→/inbox, /orders/:id→/inbox/:id, /help/delivery-config→/help/delivery-setup) that /drafts and /library/rule-definitions should follow.
- tests/e2e/no-mock-residue.spec.ts:43 and tests/e2e/live-full-e2e.spec.ts:481 — e2e specs that name /library/mappings and other deep routes and will need their path lists reviewed.
- Measured jargon scan (my script over src/app + src/components + src/lib, reusing the gate's visibleSpans heuristic, 282 files): ubl 100, token 65, poll 63, endpoint 62, parse 57, cxml 48, x12 34, transform 32, webhook 30, revision 24, edifact 21 — concentrated in (marketing)/help/**.mdx and (app)/admin/guides. 'ast' produced 51 hits, all substring false positives ('Past due', 'Last activity'); 'diff' 15 hits, all 'different'.

## Founder decisions this spec cannot make

- Locked billing copy conflicts with the nine-noun budget. CLAUDE.md §11.5 pins the supplier-limit banner as 'Your plan includes 1 supplier. Upgrade to Growth to add more supplier flows.' — 'supplier flow' is a tenth noun (it was the replacement for the retired 'lane'). This spec writes '…to add more suppliers.' Confirm the locked string may change, or the --nouns gate has to allowlist 'supplier flow'.
- Does the Dashboard lose its top-level slot? This spec follows the brief's four destinations and makes the Wire Topology 'Activity ▸ Overview' (the brand mark still links straight to it). If the topology is a sales/demo asset you show live, it may deserve to stay top-level — which means five items, and 'Orders · Overview · Suppliers · Activity · Settings' still fits the merged topbar row at 1024px (computed 919px + ~150px).
- 'Delivered' vs 'Sent'. Kept as 'Delivered' because the help content is built on 'delivered ≠ accepted' and the distinction is load-bearing for support. If you prefer the plainer 'Sent', it is a one-line change in STATUS_META plus three help articles.
- Should the 'Problems' inbox chip absorb Delivery paused and Delivery unknown? Today the 'Failed' chip expands to five red statuses only, so an amber held/unknown order appears under no chip but 'All orders'. Widening it is more honest but changes which rows the bulk-send selector offers.
- Do you want /drafts deleted or parked? This spec redirects it to /inbox because the page itself says the feature does not exist. If drafts are near-term roadmap, say so and it stays as a hidden Orders route with an honest 'coming soon' state instead.
- Order direction is moved out of first-run and defaulted to outbound (Settings ▸ Workspace). That is right for the launch ICP but silently mislabels the app for an inbound org until they find the setting. Acceptable, or should the setting be surfaced once in a dismissible strip on the Setup screen?
- How strict should the WARN tier be at launch? Promoting 'webhook / endpoint / parse / transform / poll / retry' from WARN to BLOCK would force a copy pass over roughly 300 user-visible strings. Recommend shipping them as WARN and setting a date to promote.
- Inbound (Invoices / Shipping notices) stays behind INBOUND_ENABLED as two Orders tabs. Confirm it should not appear at all at launch — if it ships, Orders grows to four visible tabs and the mobile strip starts scrolling.

---

# DB-1 — Information architecture and the nine-noun model

Spec for WP-25 / WP-26. Everything below is grounded in the files listed in "Code read".
Every route that exists today still resolves. The reachability assertion in
`BridgeSidebar.test.tsx:187-205` passes **unchanged** (verified route-by-route in §1.4).

---

## 0. What is actually wrong (measured, from the code)

| Finding | Evidence |
|---|---|
| The nav teaches 10 top-level words + 4 group headers for 4 jobs | `BridgeSidebar.tsx:52-96` — items: Dashboard, Inbox, Drafts, Inbound, Partners, Rules & formats, Operations, Integrations, Admin, Help & support, Settings. Group headers: Workbench, Library, **Monitor** (a header renamed only because it collided with the item directly under it — `:73-77`) |
| Three nav words are containers with no meaning of their own | "Partners" = Suppliers·Buyers·Connections; "Rules & formats" = Mappings·Rules·Output templates·Standards; "Integrations" = Connectors·Webhooks (`HubTabs.tsx:40-65`) |
| "Mapping" means three different things on one page | `SupplierDockProfile.tsx:105-115` — tabs `Mappings`, `Catalog`, `PO Mapping`; the UI apologises in body copy at `:1639`: *"For per-item code translations, use the Mappings tab instead."* |
| Two surfaces own "notify another system" | `/operations/webhooks` (endpoints + recent deliveries) and `settings?tab=connectors` — *"Connectors — ERP and channel integrations — send real-time events to Zapier, Make.com, or any webhook URL"* (`settings/page.tsx:1370`) |
| A shipped page admits its feature doesn't exist | `/drafts` — *"A page reserved for orders saved mid-review — saving drafts isn't available yet"* (`section-guides.ts`), and `drafts/page.tsx:60` empty state |
| The vocabulary gate is aimed at a finished war and is blind where the words live | `check-vocabulary.mjs:43-46` scans only `src/app/(app)` + `src/app/(marketing)` `.tsx/.mdx`. Both allowlists are **empty** (`:71-81`) — the metaphor purge is done. It cannot see `src/components/**` (where "Active rule bindings", "Remove node", "Loading rule bindings…" live) or `src/lib/**` label registries (`STATUS_META`, `CRUMB_LABELS`, `SECTION_GUIDES`) |
| The first-run screen is a modal wizard with a blurred backdrop, no focus trap and no Escape | `OnboardingWizard.tsx:455-540` — `role="dialog" aria-modal="true"`, `backdropFilter: blur(4px)`; grep for `Escape`/focus-trap in that file returns nothing. Both a banned pattern and an a11y failure |
| Two amber text colours exist and the one in use fails AA | `--amber-text: #8A5310` (globals.css:63) = **6.31:1** on white, but `#B36D14` is used as *text* in `BridgeTopbar.tsx:358` ("{unread} need action") and `SupplierDockProfile.tsx:580` ("Warning") = **4.11:1 — fails** |

---

## 1. The four-item nav

### 1.1 Tree

```
┌ PRIMARY (4, always visible, never gated) ────────────────────────────────┐
│                                                                          │
│  Orders          → /inbox            hub: "orders"                       │
│      Orders               /inbox                          (visible tab)  │
│      Buyers               /library/buyers                 (visible tab)  │
│      Invoices             /inbound/invoices    (visible only INBOUND_ENABLED)
│      Shipping notices     /inbound/asns        (visible only INBOUND_ENABLED)
│      · /upload, /upload/preview/[id], /inbox/[id], /drafts   (owned, hidden)
│                                                                          │
│  Suppliers      → /library/suppliers  hub: "suppliers"                   │
│      Suppliers            /library/suppliers              (visible tab)  │
│      Item codes           /library/mappings               (visible tab)  │
│      Rules                /library/rules                  (visible tab)  │
│      Output layouts       /library/templates               (visible tab) │
│      · /library/suppliers/[id], /library/standards,                      │
│        /library/rule-definitions, /connections, /connections/[id],       │
│        /operations/connectors                            (owned, hidden) │
│                                                                          │
│  Activity       → /bridge             hub: "activity"                    │
│      Overview             /bridge                         (visible tab)  │
│      Deliveries           /operations/log                 (visible tab)  │
│      Issues               /operations/exceptions          (visible tab)  │
│      · /operations/health, /operations/webhooks           (owned, hidden)│
│                                                                          │
│  Settings       → /settings           (not a hub — own left nav)         │
│      Workspace · Plan & billing · Order intake · API keys · Notifications │
│                                                                          │
└──────────────────────────────────────────────────────────────────────────┘

PINNED ACTION (not a nav item):  [↑ Upload order] → /upload
RIGHT CLUSTER (icon-only):       Search ⌘K · Notifications · Admin (gated) · Help
ACCOUNT MENU:                    Profile · Settings · Sign out
```

Groups (`Workbench` / `Library` / `Monitor`) are **deleted**. Four items need no headers;
`NAV_MAIN` becomes one flat section.

### 1.2 Why these four, and why the Dashboard is not one of them

- `Orders` is the shift. It is where a coordinator lives, so the root redirect lands
  established users here (§5), and it carries the only numeric badge in the product.
- `Suppliers` is the setup. Everything that can be wrong with a supplier is one click
  from the supplier's name.
- `Activity` is the receipt. "Did it go? What broke? Is the system up?"
- `Settings` is the workspace.

The Wire Topology dashboard is a locked visual signature (CLAUDE.md §2.2) and stays at
`/bridge`, but it is **not a coordinator's daily destination** — the queue is
(CLAUDE.md §12 anti-pattern: *"operators want the queue"*). It becomes `Activity ▸ Overview`,
the hub's first tab, so the `Activity` nav item href is `/bridge`. The brand mark keeps
linking to `/bridge` (conventional "logo goes home") — unchanged from today.

### 1.3 Where every existing destination lands

26 app routes + 7 settings tabs + 7 supplier tabs.

| # | Route today | Lands as | Nav path a user actually walks |
|---|---|---|---|
| 1 | `/bridge` | visible tab **Overview** | Activity ▸ Overview |
| 2 | `/inbox` | visible tab **Orders** | Orders |
| 3 | `/inbox/[orderId]` | child of Orders | click a row |
| 4 | `/upload` | pinned action | **Upload order** button |
| 5 | `/upload/preview/[orderId]` | child of Upload | automatic after upload |
| 6 | `/drafts` | **redirect → `/inbox`** | — (see §6, row "Drafts") |
| 7 | `/inbound/invoices` | flag-gated tab **Invoices** | Orders ▸ Invoices |
| 8 | `/inbound/asns` | flag-gated tab **Shipping notices** | Orders ▸ Shipping notices |
| 9 | `/library/suppliers` | visible tab **Suppliers** | Suppliers |
| 10 | `/library/suppliers/[id]` | child, 7 tabs (§3) | click a supplier |
| 11 | `/library/buyers` | visible tab **Buyers** | Orders ▸ Buyers |
| 12 | `/connections` | hidden; content = supplier ▸ **Changes** | Suppliers ▸ *a supplier* ▸ Changes |
| 13 | `/connections/[connectionId]` | hidden, still renders | deep links / advanced |
| 14 | `/library/mappings` | visible tab **Item codes** | Suppliers ▸ Item codes |
| 15 | `/library/rules` | visible tab **Rules** | Suppliers ▸ Rules |
| 16 | `/library/rule-definitions` | **redirect → `/library/rules#building-blocks`** | Suppliers ▸ Rules ▸ *Building blocks* section |
| 17 | `/library/templates` | visible tab **Output layouts** | Suppliers ▸ Output layouts |
| 18 | `/library/standards` | hidden — **Format reference** | link at foot of Output layouts + every `<WhatIsThis>` popover on a format |
| 19 | `/operations/health` | hidden — **System status** | Activity health banner ▸ "View system status" |
| 20 | `/operations/exceptions` | visible tab **Issues** | Activity ▸ Issues |
| 21 | `/operations/log` | visible tab **Deliveries** | Activity ▸ Deliveries |
| 22 | `/operations/connectors` | hidden — **Delivery channels** | supplier ▸ Delivery ▸ "See all channels" |
| 23 | `/operations/webhooks` | hidden; content also rendered by Settings ▸ **Notifications** | Settings ▸ Notifications |
| 24 | `/settings` | primary item **Settings** | Settings |
| 25 | `/admin`, `/admin/guides*` | tail, admin-gated icon | lock icon (allowlisted users) |
| 26 | `/help` (marketing layout) | tail, icon, new tab | "?" icon → slideover → Help centre |
| S1 | settings `org` | **Workspace** | Settings ▸ Workspace |
| S2 | settings `billing` | **Plan & billing** | Settings ▸ Plan & billing |
| S3 | settings `email` | merged into **Order intake** | Settings ▸ Order intake ▸ *Email* card |
| S4 | settings `sftp` | merged into **Order intake** | Settings ▸ Order intake ▸ *SFTP folder* card |
| S5 | settings `s3` | merged into **Order intake** | Settings ▸ Order intake ▸ *Cloud folder* card |
| S6 | settings `api` | **API keys** | Settings ▸ API keys |
| S7 | settings `connectors` | merged with `/operations/webhooks` → **Notifications** | Settings ▸ Notifications |

`?tab=` deep links keep working: `useTabParamSync` already validates against the tab
union (`settings/page.tsx:53-66`). Add aliases so old links don't dead-end —
`?tab=email|sftp|s3` → `intake` (scroll to that card), `?tab=connectors` → `notifications`.

### 1.4 The reachability guard stays green — route by route

`BridgeSidebar.test.tsx:187-205` passes when, for every `LEGACY_ROUTES` entry,
`direct || viaHub || viaPinned` is true. With the tree above:

| Route | Passes via |
|---|---|
| `/bridge` | `viaHub` — activity |
| `/upload` | `viaPinned` — `PINNED_ACTION_HREF` |
| `/inbox` | `direct` (Orders item href) + `viaHub` |
| `/drafts` | `viaHub` — orders (hidden entry) |
| `/library/suppliers` | `direct` + `viaHub` |
| `/library/buyers` | `viaHub` — orders |
| `/connections` | `viaHub` — suppliers (hidden) |
| `/library/mappings` | `viaHub` — suppliers |
| `/library/rules` | `viaHub` — suppliers |
| `/library/rule-definitions` | `viaHub` — suppliers (hidden) |
| `/library/templates` | `viaHub` — suppliers |
| `/library/standards` | `viaHub` — suppliers (hidden) |
| `/operations/health` | `viaHub` — activity (hidden) |
| `/operations/exceptions` | `viaHub` — activity |
| `/operations/log` | `direct` (Activity item href is `/bridge`, so via hub) |
| `/operations/connectors` | `viaHub` — suppliers (hidden) |
| `/operations/webhooks` | `viaHub` — activity (hidden) |
| `/inbound/invoices` | `viaHub` — orders |
| `/inbound/asns` | `viaHub` — orders |
| `/admin` · `/settings` · `/help` | `direct` |

**Hidden tabs — the one code change that makes this work.** `HubTab` gains
`hidden?: true`. Hidden entries participate in `hubForPath()` (so the nav item lights and
the guard passes) but are excluded from `HubTabs` rendering, from `hubShowsTabs()`, and
from `hubTooltip()`. Without this flag an advanced route can only stay reachable by
appearing in a tab strip a coordinator must read past.

```ts
// HubTabs.tsx
export const VISIBLE = (hub: HubKey) => HUB_TABS[hub].filter(t => !t.hidden);
export function hubShowsTabs(hub: HubKey) { return VISIBLE(hub).length >= 2; }
// hubForPath() keeps iterating ALL entries, hidden included — unchanged behaviour.
```

### 1.5 Tests that must be **updated** (not the reachability one)

`BridgeSidebar.test.tsx`:

- `"renders the consolidated core entries…"` → new expectations: `Orders`→`/inbox`,
  `Suppliers`→`/library/suppliers`, `Activity`→`/bridge`, `Settings`→`/settings`,
  `Help & support`→`/help`, `Upload order`→`/upload`.
- `"produces the v2 grouped structure in order"` → `nav.main.map(s => s.group ?? null)`
  is `[null]`; labels `[["Orders","Suppliers","Activity","Settings"]]`; tail
  `["Admin","Help & support"]` (Settings is promoted to primary; keep it in the account menu too).
- `"hub items link to their hub's FIRST tab route"` → first **visible** tab:
  orders→`/inbox`, suppliers→`/library/suppliers`, activity→`/bridge`.
- `"every hub exposes at least two tabs"` → assert on `VISIBLE(hub).length >= 2`
  (orders 2, suppliers 4, activity 3).
- `hubTooltip` → `Orders: "Orders · Buyers"`, `Suppliers: "Suppliers · Item codes · Rules · Output layouts"`,
  `Activity: "Overview · Deliveries · Issues"`.
- `LAUNCH_CORE_ONLY` test → retire the flag at the top level (all four items always show;
  `LAUNCH_CORE_HREFS` is deleted). `INBOUND_ENABLED` survives and now gates two **tabs**,
  not a nav item. Replace the exact-href assertion with:
  `expect(items.map(i => i.href)).toEqual(["/inbox","/library/suppliers","/bridge","/settings","/admin","/help"])`.
- Add: `it("renders exactly four primary nav items")` — a standing guard against creep.
- Add: `it("no visible tab label introduces an unapproved noun")` — see §7.4.

`HubTabs.test.tsx` — update the tab fixtures; add a case that a `hidden` entry is claimed
by `hubForPath` and absent from the rendered strip.

---

## 2. Annotated layout per viewport

Only the chrome and the two screens this brief changes are specified. The Order Workshop
three-column structure is untouched (locked).

### 2.1 Desktop ≥1024px — the nav row disappears into the utility row

Today the topbar is three stacked rows: 52 (utility) + 42 (nav) + 38 (context) = **132px**
of chrome before content (`BridgeTopbar.tsx:603,844,872`). Four items fit in the utility row:

```
Orders(6) + Suppliers(9) + Activity(8) + Settings(8) = 31 chars
31 × 6.6px (Inter 13/600) ≈ 205px  +  4 × 20px padding = 285px
left/right chrome: brand+wordmark 150 · org switcher 110 · Upload 130
                 · 4 icon buttons 176 · avatar 44 · gaps 24  ≈ 634px
285 + 634 = 919px  ≤ 1024px   (105px slack; the row keeps overflow-x:auto as a net)
```

```
┌ ROW 1 · 52px · navy #0B1A2F ────────────────────────────────────────────────────┐
│ ▣ ProcuLink │ Acme GmbH ▾ │ Orders 12  Suppliers  Activity  Settings │ ↑ Upload order  🔍 🔔 🔒 ? ◉ │
│                             ▔▔▔▔▔▔▔ 2px #1E66C9                                  │
├ ROW 2 · 38px · navy, 1px #14253D top rule ──────────────────────────────────────┤
│ Orders / [ Orders* │ Buyers ]                                                    │
├ 2px link-spine gradient ────────────────────────────────────────────────────────┤
│                                                                                  │
│  work area · #F6F7FA · EdgeRails on order-handling routes                        │
```

- Nav sits **left of centre**, immediately right of the org switcher; the utility cluster
  is flush right (`ml-auto`). Row 2 is the existing context row, now the only place hub
  tabs live.
- Row 2 is suppressed when it would carry a single unlinked crumb (`isLonePageCrumb`,
  already implemented) and on `/inbox/[orderId]` (`onOrderWorkshop`, already implemented).
  On `/settings` it is suppressed too — Settings owns a 200px left nav.
- Net: **90px** of chrome on hub routes, **52px** on `/inbox` and `/settings`. 42px returned
  to the work area on every screen.
- Delete `HubEyebrow` from `PageHeader`. With Row 2 naming the hub above the fold, the
  eyebrow is the third printing of the same word.

### 2.2 Tablet 768–1023px

Nav returns to its own 42px row (`hidden md:flex lg:hidden` on the standalone row;
`hidden lg:flex` on the inline copy). Org switcher collapses to the initial-avatar form.
Upload order keeps its label (130px is affordable). Rows: 52 + 42 + 38 = 132px, as today.

### 2.3 Mobile ≤767px

Row 1: `[☰ 44×44] ▣ ProcuLink · · · 🔍 🔔 ◉`. Row 2 keeps the hub strip (it is the only
thing naming the section — page `h1`s are `sr-only` via `titleHidden`).

Drawer (`BridgeSidebar` `fullWidth`), 100% width, opens from the left:

```
┌────────────────────────────────────┐
│ ▣ ProcuLink                    [✕] │  56px, 44×44 close
├────────────────────────────────────┤
│ Acme GmbH                          │
│ Growth plan                        │  workspace card
├────────────────────────────────────┤
│ [ ↑  Upload order            ]     │  48px, #1E66C9
├────────────────────────────────────┤
│ ▤  Orders                   ⟨12⟩   │  48px rows
│ ⚑  Suppliers                       │
│ ⚡  Activity                    ●   │  dot, not a number
│ ⚙  Settings                        │
├────────────────────────────────────┤
│ 🔒 Admin        (allowlisted only) │
│ ?  Help & support                  │
├────────────────────────────────────┤
│ ◉  Maria Kask · Admin          ▾   │
└────────────────────────────────────┘
```

Second-level items are **not** duplicated in the drawer — tapping a primary item lands on
its first tab and the Row 2 strip takes over. One list, no accordions.

Row heights: 48px (> the 44px `--tap-min`). Every drawer row is a full-width link.

### 2.4 Suppliers ▸ Suppliers, and the supplier detail strip

```
DESKTOP ≥1024
┌ Row 2 ─ Suppliers / [ Suppliers* │ Item codes │ Rules │ Output layouts ] ────┐
├──────────────────────────────────────────────────────────────────────────────┤
│  4 suppliers · 2 need setup                          [ + Add supplier ]      │
│  ┌────────────────────────────────────────────────────────────────────────┐  │
│  │ ▌ Acme Components   ACME   ● Ready        142 orders   last sent 26m ago│  │  3px green edge
│  │ ▌ Vandenberg BV     VDB    ▲ Needs delivery setup   0 orders            │  │  amber edge
│  └────────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────┘

SUPPLIER DETAIL — tab strip, 44px tall, role="tablist"
[ Overview* │ Item codes │ Order layout │ Output │ Delivery │ Rules │ Changes ]
   8ch        10ch          12ch          6ch      8ch       5ch     7ch
 = 56 chars ≈ 370px text + 7×32px padding = 594px  →  fits ≥768px; scrolls at 390px
```

Mobile: the strip scrolls horizontally, keeps its right-edge fade
(`SupplierDockProfile.tsx:1394`), each tab ≥44px tall and ≥64px wide.

---

## 3. The supplier detail tab set

### 3.1 The working proposal, challenged

> Overview / Item codes / How we read their files / What we send them / Delivery / Rules / History

| Proposed | Verdict |
|---|---|
| `Item codes` (merging Mappings + Catalog) | **Adopt.** This is the single highest-value rename in the brief. It also deletes the apology at `SupplierDockProfile.tsx:1639`. |
| `How we read their files` | **Reject the label, keep the idea.** (a) 23 characters — the 7-tab strip is already at its scroll limit; two sentence-tabs push it past 900px and bury Rules and Changes behind a fade on tablet. (b) In outbound mode it is factually wrong: the order file is the **buyer's own** ERP export, not the supplier's. `PoMappingEditor` is per-supplier because different suppliers' orders come from different exports (`SupplierDockProfile.tsx:1637` already says "this supplier's order files" and is already imprecise). → **`Order layout`**, 2 words, one of the nine nouns, with the sentence demoted to the helper line where it has room to be accurate. |
| `What we send them` | **Reject the label.** 17 characters; "them" has no antecedent when the same concept appears in the cross-supplier library. → **`Output`** (a taught noun), helper line does the explaining. |
| `Delivery` | **Adopt.** |
| `Rules` | **Adopt** (drop "Validation"). |
| `History` | **Reject → `Changes`.** "History" reads as *past orders*, which is what the Overview's recent-orders list already is. This tab is the version history of the supplier's **setup** (`SupplierHistoryTab.tsx:48` — `title="Version history"`). "Changes" answers "what changed, when, by whom". |

Merging `Output` into `Delivery` was considered and rejected: they fail separately and the
failure copy must point at exactly one of them (*"No delivery set up"* vs *"Output layout
is invalid"*), and Delivery holds credentials, which deserves its own boundary.

### 3.2 Final tab set — exact copy

| Tab | Helper line (13px, `--ink-muted`, under the tab body's first heading) | What it contains |
|---|---|---|
| **Overview** | `Everything set up for Acme Components, and the orders you've sent them.` | Setup progress (n of 6), health KPIs, recent orders, identity card |
| **Item codes** | `What this supplier calls the things you buy. Your code goes in, theirs comes out.` | §**Code translations** (was Mappings) + §**Their product list** (was Catalog) |
| **Order layout** | `How we read the order files for this supplier — which column holds the PO number, the quantity, the price.` | `PoMappingEditor` |
| **Output** | `The file this supplier receives, and the layout it has to follow.` | format + template picker, preview, "Customise layout" |
| **Delivery** | `Where the finished file goes, and the login it needs.` | `DeliveryConfigEditor` + guided setup + "Send a test" |
| **Rules** | `Checks that run before an order is sent. Errors stop the send; warnings only flag.` | `AcceptanceTab` + §**Rules in use** |
| **Changes** | `Every change to this supplier's setup, newest first. Open a version to see what it looked like.` | `SupplierHistoryTab` |

Tab-bar a11y (missing today — `SupplierDockProfile.tsx:1374-1391` is a bare `div` of
`<button>`s): add `role="tablist"` on the container, `role="tab"` +
`aria-selected` + `aria-controls` on each button, `role="tabpanel"` + `aria-labelledby`
on the body, ←/→ arrow-key movement, `tabIndex={active ? 0 : -1}`, and a visible
`:focus-visible` ring (`2px solid #1E66C9`, `outline-offset: 2px` — **5.53:1** on white,
comfortably over the 3:1 non-text floor).

Overview's setup strip — exact copy, reusing `buildChecklistSteps`:

```
Setup · 4 of 6 done
✓ Supplier added        ✓ Item codes added     ✓ First order uploaded
✓ Item codes resolved   ▲ Delivery not set up  ○ First order sent
                          [ Set up delivery ]
```

`?tab=` aliases so existing deep links (checklist, help slideover, onboarding) never break:
`mappings|catalog` → `item-codes`, `po-mapping` → `order-layout`, `acceptance` → `rules`,
`history` → `changes`.

---

## 4. "What is this?" — the inline concept affordance

### 4.1 What exists, and why it isn't the pattern yet

`StandardsFieldPopover.tsx` is the right shape (circled "i" → Radix `Popover`, self-hides
when it has nothing to say) but: the trigger is **15×15px** (fails the 44px floor), it is
hard-wired to the standards catalogue, and its only affordance is a hover colour change —
invisible on touch.

### 4.2 The primitive

`<WhatIsThis term="item-code" />` — `src/components/bridge/WhatIsThis.tsx`

- **Trigger.** Renders inline after the label it explains: a 16px circled `i`
  (JetBrains Mono, 1.5px border, `currentColor`) inside a transparent **44×44** hit box
  (`min-width/height: var(--tap-min)`, negative margin so it doesn't disturb baseline).
  Colour `--ink-faint` → `--brand-blue` on hover/focus. `aria-label="What is an item code?"`.
- **Behaviour.** Radix `Popover` — focus moves into the panel, `Escape` closes and returns
  focus to the trigger, click-outside closes, focus is trapped while open. Never a modal,
  never a tooltip (tooltips die on touch and can't hold a link).
- **Panel.** 300px max, `--surface`, 1px `--border`, radius 8, `0 8px 24px rgba(11,26,47,.12)`.
  Four fixed slots, in this order — no free-form prose:

```
ITEM CODE                          ← 11px/700 uppercase, letterspacing .06em, --ink-faint
The number a supplier uses for a   ← 13px/1.5 --ink : ONE sentence, no clauses
product. Yours is different.
Yours HX-4410 → theirs ACM-PL-22   ← 12px JetBrains Mono, blue → green (semantic law)
Where it comes from: you enter it  ← 12px --ink-muted, always present
once, or import their product list.
Full guide →                       ← 12.5px --brand-blue, opens the help slideover
```

- **Content registry** — `src/lib/concepts.ts`, one entry per concept, so a word is defined
  once and every surface reads the same definition:

```ts
export interface Concept {
  term: string;            // "Item code"
  what: string;            // one sentence
  example?: string;        // mono, blue → green
  where: string;           // "Where it comes from: …"
  guideSlug?: string;      // opens HelpSlideover at that article
}
```

### 4.3 The registry — full copy for launch

| key | term | what | example | where |
|---|---|---|---|---|
| `order` | Order | One purchase order on its way to one supplier. | `PO-2026-008412` | Where it comes from: a file you upload, or an order that arrives by email, folder or API. |
| `supplier` | Supplier | A company you send orders to. Each one has its own codes, layout and delivery. | — | Where it comes from: you add it once, on the Suppliers page. |
| `item-code` | Item code | The number a supplier uses for a product. Yours is usually different. | `HX-4410 → ACM-PL-22` | Where it comes from: you enter it once, or import the supplier's product list. |
| `order-layout` | Order layout | Which column or field in your order file holds which piece of information. | `Column C → Quantity` | Where it comes from: you set it up once per supplier, after your first upload. |
| `output` | Output | The file the supplier receives, in the exact layout they asked for. | `cXML 1.2 · OrderRequest` | Where it comes from: we build it from the order when you send. |
| `delivery` | Delivery | How the finished file reaches the supplier, and the login it needs. | `HTTPS · sftp · email` | Where it comes from: the supplier's Delivery tab. |
| `rule` | Rule | A check that runs before an order is sent. Errors stop the send; warnings only flag. | `Currency must be EUR` | Where it comes from: you switch rules on per supplier. |
| `issue` | Issue | Something we couldn't finish on our own. It waits here until you clear it. | — | Where it comes from: we open one automatically when an order can't move. |
| `workspace` | Workspace | Your company's account. Orders, suppliers and people all live inside it. | `Acme GmbH` | Where it comes from: created when you signed up. Switch with the name at the top left. |
| `needs-review` | Needs review | We read the order but couldn't be sure about at least one line. | — | Where it comes from: usually an item code we've never seen for this supplier. |
| `ready-to-send` | Ready to send | The order is clean and waiting for you to send it. Nothing is wrong. | — | Where it comes from: every check passed and no line is unresolved. |
| `format` | Format | The document standard a supplier requires. Their IT team names it. | `cXML · UBL · EDIFACT · X12` | Where it comes from: ask your supplier contact; we support nine. |
| `version` | Version | A saved snapshot of one supplier's setup. Older versions stay readable. | `v3 · saved 12 May` | Where it comes from: we save one every time you change that supplier's setup. |
| `test-send` | Test send | We send one real file to the supplier's channel to prove the login works. | — | Where it comes from: the "Send a test" button on the Delivery tab. |
| `sample-order` | Sample order | A practice order we supply. It never counts toward your plan. | — | Where it comes from: "Try with a sample order" on the Upload page. |

**Where triggers go (exhaustive, so nothing is decorative):** `Item code` on the Item codes
tab heading, the Order Workshop line column, and the Upload preview; `Order layout` and
`Output` and `Delivery` and `Rule` on their supplier tab headings; `Issue` on the Activity ▸
Issues heading; `Needs review` / `Ready to send` on the inbox filter chip row (one trigger
after the chips, not one per chip); `Format` on the Output layouts page and the supplier
Output tab; `Version` on the Changes tab; `Workspace` on the org switcher menu header.
**Nowhere else.** A concept gets at most one trigger per screen.

**Ceiling.** If `concepts.ts` grows past 20 entries, that is evidence the product has grown
a tenth noun — the `--nouns` gate in §7.4 will already have failed by then.

---

## 5. The one screen a new user lands on

### 5.1 Routing

`/` (signed in) → `hasSupplier || hasUpload ? "/inbox" : "/bridge"`, decided from
`GET /api/onboarding/status` (already fetched by `useOnboardingStatus`). Never land a
brand-new workspace on an empty queue or an empty network diagram.

### 5.2 `/bridge` in **Setup** state — replaces the modal wizard

Delete `OnboardingWizard` as a modal. It is a modal wizard with a
`backdropFilter: blur(4px)` backdrop (both banned), it has no focus trap and no Escape
handler, and its first question — *"How do you use ProcuLink?"* (`OnboardingWizard.tsx:158`)
— asks a coordinator about **our data model** before they have seen anything.

Order direction moves to `Settings ▸ Workspace` and defaults to outbound (the launch ICP).
Copy there: **`Which way do orders flow?`** / `You send orders out to suppliers` (selected) ·
`You receive orders from customers` / helper: `This only changes wording — "Supplier" or
"Customer" — across the app.`

The Setup screen is one column, 560px max, centred, on `--bg`, no topology, no KPIs, no
fabricated zeroes:

```
┌──────────────────────────────────────────────────────────────┐
│  ▌ Get your first order out the door                         │  3px bridge-gradient edge
│    Four steps. About ten minutes.                            │
│                                                              │
│  ①  Add the supplier you send the most orders to             │  ← active
│      Supplier name                                           │
│      [ Acme Components                            ]  [ Add ] │  48px input, 16px text
│      You can add the rest later.                             │
│                                                              │
│  ②  Upload one of their orders                        Locked │
│  ③  Check the item codes we couldn't match             Locked │
│  ④  Tell us where to send it                           Locked │
│                                                              │
│  ─────────────────────────────────────────────────────────── │
│  Not ready? [ Try with a sample order ]  It's free and never  │
│  counts toward your plan.                                    │
└──────────────────────────────────────────────────────────────┘
```

**The first ask is one text field: the name of a supplier.** Not a direction, not a format,
not a file. It is the only question whose answer a purchasing coordinator has in their head
before they open the product.

Exact copy:

| Element | Copy |
|---|---|
| Title | `Get your first order out the door` |
| Sub | `Four steps. About ten minutes.` |
| Step 1 label | `Add the supplier you send the most orders to` |
| Step 1 field label | `Supplier name` |
| Step 1 placeholder | `e.g. Acme Components` |
| Step 1 button | `Add` → `Adding…` |
| Step 1 helper | `You can add the rest later.` |
| Step 1 error (empty) | `Enter a supplier name to continue.` |
| Step 1 error (network) | `Couldn't save that supplier. Check your connection and try again.` |
| Step 1 error (duplicate) | `You already have a supplier called "Acme Components".` |
| Step 2 label | `Upload one of their orders` |
| Step 2 helper | `CSV, Excel, PDF, XML, EDIFACT or X12 — whatever your system exports.` |
| Step 2 button | `Choose a file` |
| Step 3 label | `Check the item codes we couldn't match` |
| Step 3 helper | `We'll only ask about lines we're unsure of.` |
| Step 3 button | `Open the order` |
| Step 4 label | `Tell us where to send it` |
| Step 4 helper | `An address, a folder, or their system's API. We'll send one test first.` |
| Step 4 button | `Set up delivery` |
| Locked step helper | `Finish step 1 first.` |
| Escape hatch | `Not ready?` + `Try with a sample order` + `It's free and never counts toward your plan.` |
| On completion (once) | `Your first order is on its way to Acme Components.` + `See it in Orders →` |
| Skip link (footer) | `Skip setup for now` → `/inbox` |

After step 1 succeeds the screen keeps its shape and step 2 activates in place — no route
change, no modal, no confetti. Once `hasSupplier && hasUpload`, `/bridge` graduates to the
normal Overview (topology + KPIs + the residual checklist band, exactly as
`BridgeDashboard.tsx:1136-1144` does today).

---

## 6. The rename table

Verdicts: **KEEP** · **RENAME** · **MERGE** · **HIDE** (route lives, off the nav; reachable
from context) · **DELETE** (word leaves the product).

### 6.1 Navigation and page names

| # | Today | Verdict | New | Grounded in what it does |
|---|---|---|---|---|
| 1 | Dashboard | RENAME | **Overview** | It is the workspace's live picture, and it is now a tab inside Activity. |
| 2 | Inbox | RENAME | **Orders** | "Inbox" is a mail metaphor; the thing in it is an order. Route `/inbox` unchanged. |
| 3 | Workbench *(group)* | DELETE | — | Four items need no group headers. |
| 4 | Library *(group)* | DELETE | — | Same. |
| 5 | Monitor *(group)* | DELETE | — | Existed only to stop colliding with the "Operations" item below it (`BridgeSidebar.tsx:73-77`). |
| 6 | Partners | DELETE | — | A container. Suppliers goes top-level; Buyers moves under Orders. |
| 7 | Rules & formats | DELETE | — | A bucket of four unrelated engines. Split into Item codes / Rules / Output layouts. |
| 8 | Operations | RENAME | **Activity** | The three pages under it answer "what happened", not "how do I operate". |
| 9 | Integrations | DELETE | — | Its two children go to different places (Delivery channels → hidden; Webhooks → Settings ▸ Notifications). |
| 10 | Drafts | DELETE | — | The page states saving drafts is not available (`drafts/page.tsx:60`). Redirect `/drafts → /inbox`. When drafts ship, they return as an Orders filter, not a page. |
| 11 | Inbound *(group)* | DELETE | — | Its two children become Orders tabs behind `INBOUND_ENABLED`. |
| 12 | Invoices | KEEP | Invoices | Flag-gated tab under Orders. |
| 13 | Advance Shipping Notices / ASNs | RENAME | **Shipping notices** | "ASN" is trade jargon; the plain phrase is unambiguous. |
| 14 | Suppliers | KEEP | Suppliers | One of the nine. Still relabels to **Customers** for inbound orgs (display only). |
| 15 | Buyers | KEEP (move) | Buyers | It is a directory of who *issued* the orders, with a "filter the inbox" action — a facet of Orders, not of setup. Move to Orders ▸ Buyers. |
| 16 | Connections | HIDE | — | Its whole job is the versioned setup of one supplier — already the **Changes** tab. Two names for one thing. |
| 17 | Mappings | RENAME | **Item codes** | It is a buyer-code → supplier-code lookup table (`MappingEditor.tsx:201`). |
| 18 | Catalog | MERGE → Item codes | *Their product list* (section) | The supplier's valid product codes. Same question, same tab. |
| 19 | PO Mapping / "Order file layout" | RENAME | **Order layout** | Column → field for the order file. One of the nine. |
| 20 | Output templates | RENAME | **Output layouts** | Cross-supplier library of the layouts you can send. |
| 21 | Rule catalog *(page title)* | DELETE | — | The tab already names the page; `titleHidden` proves it was redundant. |
| 22 | Rules | KEEP | Rules | One of the nine. |
| 23 | Rule definitions | MERGE → Rules | *Building blocks* (section) | Read-only built-ins that supplier rules bind to. A section, never a sibling page. |
| 24 | Standards / Standards reference | RENAME + HIDE | **Format reference** | A read-only field↔standard matrix nobody opens cold; its value is delivered inline by `<WhatIsThis term="format">` and `StandardsFieldPopover`. |
| 25 | System health | RENAME + HIDE | **System status** | Ambient, not a destination: surfaced as a banner on Activity and the existing health chip. |
| 26 | Exceptions | RENAME | **Issues** | One of the nine. "Exception" is a programming word. |
| 27 | Delivery log | RENAME | **Deliveries** | It lists delivery attempts. "Log" adds nothing. |
| 28 | Connectors *(operations)* | RENAME + HIDE | **Delivery channels** | A read-only grid of channel types derived from supplier delivery configs (`operations/connectors/page.tsx`). No configuration happens here. |
| 29 | Webhooks | MERGE → Settings ▸ Notifications | — | Same job as `settings?tab=connectors` (`settings/page.tsx:1370`). |
| 30 | Admin | KEEP | Admin | Internal, allowlisted, icon-only. |
| 31 | Help & support | KEEP | Help & support | Icon + slideover. |
| 32 | Settings | KEEP | Settings | Now a primary item. |
| 33 | Upload order | KEEP | Upload order | Pinned action, not a nav item. |

### 6.2 Supplier tabs and in-page headings

| # | Today | Verdict | New |
|---|---|---|---|
| 34 | Overview | KEEP | Overview |
| 35 | Mappings *(tab)* | RENAME | **Item codes** |
| 36 | Catalog *(tab)* | MERGE | → Item codes ▸ *Their product list* |
| 37 | PO Mapping *(tab)* | RENAME | **Order layout** |
| 38 | Delivery *(tab)* | KEEP | Delivery |
| 39 | Validation rules *(tab)* | RENAME | **Rules** |
| 40 | History *(tab)* | RENAME | **Changes** |
| 41 | "Saved SKU mappings" (`:1535`) | RENAME | **Code translations** |
| 42 | "Active rule bindings" (`:230`) | RENAME | **Rules in use** |
| 43 | "Loading rule bindings…" (`:239`) | RENAME | `Loading rules…` |
| 44 | "Couldn't load rule bindings." (`:244`) | RENAME | `Couldn't load this supplier's rules.` |
| 45 | "Delivery summary" / "Recent deliveries" | KEEP | unchanged |
| 46 | "For per-item code translations, use the Mappings tab instead." (`:1639`) | DELETE | The merge removes the need to apologise. |

### 6.3 Settings

| # | Today | Verdict | New |
|---|---|---|---|
| 47 | Organization | RENAME | **Workspace** (one of the nine) |
| 48 | Billing & plan | RENAME | **Plan & billing** (plan is what they came for) |
| 49 | Email intake | MERGE | → **Order intake** ▸ *Email* |
| 50 | SFTP pull | MERGE + RENAME | → Order intake ▸ *SFTP folder* ("pull" is ours, not theirs) |
| 51 | S3 / R2 pull | MERGE + RENAME | → Order intake ▸ *Cloud folder (S3 or R2)* |
| 52 | API keys | KEEP | API keys |
| 53 | Connectors *(settings)* | RENAME | **Notifications** |
| 54 | "Your ingress endpoint" (`:951`) | RENAME | **Your order intake URL** |
| 55 | "Webhook subscriptions" | RENAME | **Where we send events** |
| 56 | "REST API & webhooks" | RENAME | **API & events** |

### 6.4 Order statuses

The founder's instruction — *`Normalized` → `Ready to send`* — is right about what the user
sees, but taken literally it re-creates a bug the code documents twice
(`InboxView.tsx:78-88`, `UnifiedStatusBadge.tsx:30-35`): `ready` and `ready_to_deliver`
both read "Ready", so the "Ready to send" chip counted 0 while rows said "Ready".
Resolution below keeps two **distinct** labels: the human's turn vs the machine's turn.

| # | Status key | Today | Verdict | New |
|---|---|---|---|---|
| 57 | `ready` | Normalized | RENAME | **Ready to send** — clean, waiting for you |
| 58 | `ready_to_deliver` | Ready to send | RENAME | **Queued to send** — file built, we're sending |
| 59 | `pending_parse` | Queued | RENAME | **Waiting** |
| 60 | `parsing` / `extracting` | Extracting | KEEP | Extracting |
| 61 | `normalizing` | Normalizing | MERGE → Extracting | — (an internal sub-step; the user cannot act on it) |
| 62 | `transforming` | Transforming | RENAME | **Preparing output** |
| 63 | `transformed` | Transformed | MERGE → `Queued to send` | — |
| 64 | `unrouted` | Needs supplier | KEEP | Needs supplier (label already plain; only the key is jargon) |
| 65 | `pending_review` | Needs review | KEEP | Needs review |
| 66 | `delivering` | Sending | KEEP | Sending |
| 67 | `delivered` / `sent` | Delivered | KEEP | Delivered — the help content is built on "delivered ≠ accepted"; renaming to "Sent" would cost accuracy for no gain |
| 68 | `delivery_held` | Delivery paused | KEEP | Delivery paused |
| 69 | `delivery_unconfirmed` | Delivery unknown | KEEP | Delivery unknown |
| 70 | `failed` | Failed | RENAME | **Couldn't read file** (this key is the parse-terminal status) |
| 71 | `parse_failed` | Parse failed | RENAME | **Couldn't read file** |
| 72 | `transform_failed` | Transform failed | RENAME | **Couldn't build output** |
| 73 | `delivery_failed` | Delivery failed | RENAME | **Couldn't send** |
| 74 | `delivery_dead_letter` | Dead-lettered | RENAME | **Retry needed** — the founder's "needs your attention" is right in spirit but under-specifies; this one names the action (3 automatic retries are spent; a human must resend) |
| 75 | `rejected_by_supplier` | Rejected | RENAME | **Supplier rejected** |
| 76 | `cancelled` · `archived` · `live` · `draft` | as-is | KEEP | unchanged |

Inbox filter chips (`InboxView.tsx:425-435`): `All orders` · `Needs review` ·
**`Ready to send`** · `Delivered` · **`Problems`** (was "Failed"). The `Ready to send` chip
must now expand server-side to `ready` **+** `ready_to_deliver` — the same
multi-status expansion the `failed` chip already uses (`InboxView.tsx:383`). Without that
expansion the chip reads 0 while rows say "Ready to send" — the exact regression the
comments warn about.

### 6.5 Engine nouns that leak into user-facing copy

| # | Today | Verdict | New |
|---|---|---|---|
| 77 | revision | RENAME | **version** (largely done already: `ReplayPanel.tsx:139,519` say "No versions yet", "This version") |
| 78 | canonical (field / order model) | DELETE | **ProcuLink fields** (or "standard fields") |
| 79 | passport / PO Passport | RENAME | **Order record**; export button `Download order record (JSON)` |
| 80 | replay | RENAME | **Test with recent orders**; `aria-label="Replaying orders"` → `Testing recent orders` |
| 81 | test pack | RENAME | **Pre-publish checks** |
| 82 | bundle (verb/noun) | DELETE | "A version includes …" |
| 83 | artifact | RENAME | **output file** |
| 84 | idempotency / Idempotency-Key | KEEP (API reference only) | It is a literal HTTP header name; allowlisted to the API articles |
| 85 | dead-letter | RENAME | **Retry needed** (status) / **needs a manual resend** (prose) |
| 86 | node / "Node name" / "Remove node" (`OutputStructureDesigner.tsx:547,633`) | RENAME | **Element name** / **Remove element** — the real XML noun |
| 87 | namespace | KEEP (format articles + XML designer) | Factual XML term; gloss on first use |
| 88 | ingress | RENAME | **order intake** |
| 89 | egress / "no-egress workspaces" | RENAME | **Documents never leave your region** |
| 90 | conformance / "Conformance format" (`ConformancePanel.tsx:136`) | RENAME | **Check against standard** |
| 91 | acceptance / acceptance profile | RENAME | **Rules** / **rule set** |
| 92 | binding(s) | RENAME | **in use** |
| 93 | provenance | RENAME | **Where this came from** |
| 94 | operator *(rule operator)* | RENAME | **Check** (the human labels in `OPERATOR_LABELS` are already good — keep them) |
| 95 | field path / `fieldPath` | RENAME | **Field** |
| 96 | scope *(rule scope)* | RENAME | **Applies to** · values **Whole order** / **Each line** |
| 97 | upsert | DELETE | "Re-importing updates existing rows instead of adding duplicates." |
| 98 | tenant | DELETE | **workspace** / **customer** |
| 99 | exception | RENAME | **issue** |
| 100 | spine · dock · crossing · lane · wire · traveller | KEEP as code only | Already enforced by the existing gate; keep that tier (§7) |

---

## 7. The vocabulary gate, re-aimed

`scripts/check-vocabulary.mjs` today polices seven retired **metaphor** words over two
directories, with both allowlists empty. It is green and it is looking in the wrong place.

### 7.1 Fix the scope first (the biggest single win)

| Add | Why |
|---|---|
| `src/components/**/*.tsx` | Where the jargon actually renders: `"Active rule bindings"`, `"Loading rule bindings…"`, `"Remove node"`, `"Conformance format"` are all invisible to the gate today |
| `src/lib/*.ts` label registries | `section-guides.ts`, `help-articles.ts`, `breadcrumb.ts` (`CRUMB_LABELS`), `InboxView`'s `STATUS_PRESENTATION`, `UnifiedStatusBadge`'s `STATUS_META` — the vocabulary's source of truth is in `.ts` files the gate never opens |

| Exclude | Why (measured) |
|---|---|
| `src/app/(marketing)/help/**/*.mdx` from the BLOCK tier | Reference documentation for a technical reader; it *must* say cXML, UBL, `Idempotency-Key`. My scan: of ~700 jargon hits in user-visible spans, the overwhelming majority are here (`token` 65, `poll` 63, `endpoint` 62, `parse` 57) |
| `src/app/(app)/admin/**` | Our own staff runbook, not customer copy |
| `src/app/(marketing)/{dpa,terms,privacy,aup,subprocessors}` | Legal text legitimately uses "scope", "query", "tenant", "JWT" |
| `**/*.test.*`, `src/mocks/**`, `src/lib/standards/catalog.ts` | Fixtures and a standards fact table |

### 7.2 Two severities

**BLOCK (exit 1)** — never user-facing, no legitimate reading:

```
revision, revisions, canonical, passport, artifact, artifacts,
replay, replays, replayed, replaying, dead-letter, "dead letter", dead-lettered,
idempotency, idempotent, unrouted, upsert, ingress, egress, tenant, tenants,
"test pack", provenance, binding, bindings, conformance, fieldPath, "field path",
normalized, normalised, normalizing, org_id, nonce, mutation, hydrate,
serialize, deserialize, enum, boolean, UUID, payload,
+ the existing metaphor tier (bridge, crossing(s), dock(s), lane(s), spine,
  wire(s), wiring, traveller(s)) kept as a regression guard
```

**WARN (exit 0, printed, must be glossed within the same block)**:

```
webhook, endpoint, "API key", cXML, UBL, EDIFACT, X12, Peppol, SFTP, FTPS, IMAP,
namespace, schema, transform, parse, parsing, poll, polling, retry, sync, diff,
profile, standard, artifact-free synonyms of the above
```

A WARN is satisfied when a `<WhatIsThis>` trigger or a parenthetical gloss appears within
the same JSX element or MDX paragraph. Report-only at first; promote to BLOCK once the
count reaches zero.

### 7.3 False-positive risks — call these out or the gate gets allowlisted to death

1. **Substring matching.** Must be `\b…\b`. `AST` inside "P**ast** due" / "L**ast** activity
   produced **51 false hits** in my scan — drop 3-letter acronyms from the list entirely.
   Same class: `node` in "Node.js", `diff` in "different"/"difference" (15/15 hits false),
   `sync` in "asynchronous", `scope` in "scoped".
2. **"version" is legitimate and must never be policed.** It is the *replacement* for
   "revision", and it is correct in a changelog, in "Version history", "This version", "v3",
   and in `/changelog`. Police `revision` only.
3. **Standards names are facts, never jargon.** cXML 1.2, UBL 2.1, EDIFACT ORDERS, X12 850,
   Peppol BIS — renaming them makes the copy *wrong*, because they are literally what the
   supplier's IT team asked for. WARN-only, forever.
4. **Ordinary-English collisions.** "operator" (a person), "profile" (a supplier profile),
   "query" and "scope" (legal text), "record", "bundle" (a price bundle) — scope by directory
   (§7.1) rather than by growing `PHRASE_ALLOWLIST`.
5. **Proper nouns.** Extend `PROPER_NOUN_MASKS` beyond Proton Bridge: `Node.js`, `Zapier`,
   `Make.com`, `Erply`, `Directo`, `Amazon S3`, `Cloudflare R2`.
6. **MDX code fences are not skipped properly.** `visibleSpansMdx` (`:175-182`) tests
   `startsWith("```")` **per line**, so lines *inside* a fence (e.g.
   `POST /api/ingress/{slug}/orders`) are still scanned as prose. Track fence state.
7. **Keys vs values in `.ts` registries.** `STATUS_META` has keys like
   `delivery_dead_letter:` — scanning keys would fail the gate on the status *identifier*,
   which is legitimate code. Match only the value side of
   `\b(label|title|sub|purpose|text|cta|description|blurb|placeholder)\s*:\s*"…"`.
8. **`title=` on decorative elements** is correctly treated as visible (it renders as a
   tooltip) — expect and accept those hits.

### 7.4 The mode that actually enforces the brief: `--nouns`

Blocklists say what not to say. The brief asks for a **budget of nine**. Add a positive
check, cheap because the vocabulary is centralised in five registries:

```
node scripts/check-vocabulary.mjs --nouns
```

Reads `APPROVED_NOUNS` from a new `src/lib/vocabulary.ts` and fails when any label in
`NAV_MAIN`, `HUB_TABS` (visible entries), `SupplierDockProfile.TABS`,
`settings/page.tsx TABS`, `STATUS_META`, or `FILTER_CHIPS` introduces a head noun outside it.

```ts
// src/lib/vocabulary.ts — the nine, plus the small closed set of place words
export const APPROVED_NOUNS = [
  "order", "supplier", "item code", "order layout", "output",
  "delivery", "rule", "issue", "workspace",
] as const;
export const APPROVED_PLACES = ["overview", "activity", "settings", "buyer", "change"] as const;
```

Wire it as `bun run lint:vocab` (both modes) in CI. This is the guard that would have caught
"Partners", "Rules & formats", "Integrations", "PO Mapping", "Normalized", "dead-lettered",
"Active rule bindings" and "Your ingress endpoint" **before** they shipped.

---

## 8. State matrix

### 8.1 Nav shell (all four items)

| State | Behaviour + exact copy |
|---|---|
| Loading | All four labels render immediately (static). Badges render nothing until `orders-summary` resolves — never `0`, never a skeleton pill. Org switcher plan line: `Loading…` (existing). |
| Empty workspace | All four items visible and enabled. `Orders` shows no badge. Setup screen (§5) is the landing page. |
| Error | Nav is static and unaffected by fetch failures — `checkAdminAccess`, `getBillingStatus` and `getOrdersSummary` already degrade to `undefined` (mocked in `BridgeSidebar.test.tsx:48-50`). Admin icon hidden on probe failure (correct: the page re-gates server-side). |
| Read-only (Pilot ended) | Nav unchanged. **Upload order** button disabled with `aria-disabled="true"`, `title="Your Pilot has ended. Upgrade to Growth to process new orders."`. Workspace card badge: `Pilot ended · Processing paused` (locked copy). A dismissible banner above the work area: `Your Pilot has ended. You can still view previous orders, but new processing is paused. Upgrade to Growth to continue.` (locked copy) + `See plans` → `/settings?tab=billing`. |
| Plan-gated | Order limit banner: `You've reached your plan's order limit. Upgrade to continue processing new orders this month.` (locked copy). Supplier limit: `Your plan includes 1 supplier. Upgrade to Growth to add more suppliers.` — see Open questions: the locked string says "supplier flows", a tenth noun. |
| Success | Active item: white text + `600` weight + 2px `#1E66C9` bottom border. Never colour alone. |

### 8.2 Setup screen (`/bridge`, new workspace)

| State | Copy |
|---|---|
| Loading | Card skeleton at the real height (no layout shift). No step numbers until status resolves. |
| Empty | The default state — this screen only exists when the workspace is empty. |
| Error (status unreachable) | `We couldn't check your setup progress.` / `Your data is safe — this is a display problem.` / `[ Try again ]` / `Or start here: [ Add a supplier ]`. Never render a fabricated `0 of 4`. |
| Read-only (Pilot ended before finishing setup) | Steps render as read-only with a leading banner: `Your Pilot has ended. Upgrade to Growth to finish setting up.` Sample-order button hidden. |
| Plan-gated (supplier limit on Pilot) | Step 1 input disabled; `Your plan includes 1 supplier. Upgrade to Growth to add more suppliers.` + `See plans`. |
| Success (step) | Step row collapses to a green check + past-tense label (`Supplier added`), the next step activates in place, and a polite live region announces `Step 1 done. Next: upload one of their orders.` |
| Success (all) | One-time card: `Your first order is on its way to Acme Components.` + `See it in Orders →`. Then `/bridge` graduates to the normal Overview. |

### 8.3 Suppliers ▸ Suppliers

| State | Copy |
|---|---|
| Loading | 3 row skeletons at row height; header count omitted (not `0`). |
| Empty | `No suppliers yet` / `Add the supplier you send the most orders to. You can add the rest later.` / `[ Add supplier ]` |
| Error | `Couldn't load your suppliers.` / `[ Try again ]` |
| Read-only | List renders; `Add supplier` disabled + `Pilot ended · Processing paused` badge. |
| Plan-gated | `Add supplier` disabled + `Your plan includes 1 supplier. Upgrade to Growth to add more suppliers.` + `See plans`. Distinct from "billing unavailable" (already handled — Group I pass 8). |
| Success | New supplier row appears at the top with an amber `Needs delivery setup` chip and a `Finish setup` link. |

### 8.4 Supplier ▸ any tab

| State | Copy |
|---|---|
| Loading | Tab strip renders immediately (static); body skeletons. |
| Empty — Item codes | `No item codes yet` / `Add a translation, or import this supplier's product list.` / `[ Add a code ]` `[ Import a list ]` |
| Empty — Order layout | `No order layout yet` / `Upload one order for this supplier and we'll suggest the layout.` / `[ Upload an order ]` |
| Empty — Output | `No output layout chosen` / `Ask your supplier contact which format they need. We support nine.` / `[ Choose a layout ]` + `<WhatIsThis term="format" />` |
| Empty — Delivery | `Delivery isn't set up` / `Orders for this supplier will stop here until you tell us where to send them.` / `[ Set up delivery ]` `[ Walk me through it ]` |
| Empty — Rules | `No rules yet` / `Rules catch problems before an order goes out. Start with a ready-made one.` / `[ Add a rule ]` |
| Empty — Changes | `No changes yet` / `Every edit to this supplier's setup will appear here.` |
| Error | `Couldn't load this supplier's <tab noun>.` / `[ Try again ]` |
| Read-only | All inputs disabled; one banner per tab: `Pilot ended — you can read this, but not change it.` + `See plans` |
| Success | Inline, next to the control that saved: `Saved.` for 3s, in `--brand-green-deep`, `role="status"`. |

### 8.5 Activity

| State | Copy |
|---|---|
| Loading | Tab strip static; table skeletons. |
| Empty — Deliveries | `Nothing sent yet` / `Every send attempt will be listed here, with the supplier's reply.` |
| Empty — Issues | `No open issues` / `Nothing needs you right now.` |
| Error | `Couldn't load activity.` / `[ Try again ]` |
| Degraded system | Amber banner above the tabs: `Orders are moving slower than usual.` + `View system status →` (`/operations/health`). Rendered **only** on a genuine degraded signal — never a green "all systems normal" strip, which is noise. |
| Read-only | Full read access (Pilot read-only explicitly permits viewing history). No banner. |
| Success (issue resolved) | Row animates out; `role="status"`: `Issue cleared.` |

---

## 9. Token map — every colour used above

| Where | Value | Token |
|---|---|---|
| Topbar / drawer background | `#0B1A2F` | `navy.DEFAULT` / `--navy` |
| Drawer gradient | `#0D2038 → #091422` | existing `BridgeSidebar` chrome |
| Row rules inside chrome | `#14253D`, `#1F3252` | `navy.surface`, `navy.border` |
| Nav label, inactive | `#C8D1E0` | `navy.text` |
| Nav label, active | `#FFFFFF` | — |
| Active underline / focus ring | `#1E66C9` | `brand.blue` |
| Breadcrumb + hub tab, inactive | `#7C8DA6` | `navy.muted` |
| Hub tab count, active | `#7FA8E0` | existing `HubTabs` |
| Work area | `#F6F7FA` | `bg` |
| Cards / popovers | `#FFFFFF` | `surface` |
| Section fills | `#F1F3F7` | `surface2` |
| Hairlines | `#E5E8EE` | `border` |
| Control outlines | `#CBD0DA` | `borderStrong` |
| Body ink | `#0B1A2F` | `ink.DEFAULT` |
| Helper lines, subs | `#5E6779` | `ink.muted` |
| Eyebrows, mono secondary | `#667085` | `ink.faint` |
| Buyer side, links, primary button | `#1E66C9` / hover `#0F4FA8` | `brand.blue` / `brand.blueDeep` |
| Supplier side, "done" | `#1E6D29` text · `#297F34` button fill · `#E9F1EA` tile | `brand.greenDeep` / `brand.greenSoft` |
| Needs-attention text | `#8A5310` | `--amber-text` |
| Needs-attention dot/border only | `#B36D14` | `amber` |
| Needs-attention fill | `#FAF1DD` / `#FFF8EA` | `amberSoft` |
| Failure text | `#B43838` · fill `#FBE3E3` | `danger` / `dangerSoft` |
| AI-generated only | `#6F4FCE` · fill `#F0EAFB` | `ai` / `aiSoft` |
| Card cross-section edge | blue = buyer · green = supplier · gradient = both | `bg-bridge-deck` |

No new tokens. No per-screen themes.

---

## 10. Accessibility — computed, not asserted

Ratios below are computed with the WCAG 2.x relative-luminance formula.

**Navy chrome on `#0B1A2F`**

| Pair | Ratio | Requirement | Verdict |
|---|---|---|---|
| `#C8D1E0` inactive nav label | **11.35:1** | 4.5 (text) | pass |
| `#C8D1E0` on drawer darkest `#091422` | **12.03:1** | 4.5 | pass |
| `#FFFFFF` active nav label | **17.46:1** | 4.5 | pass |
| `#7C8DA6` breadcrumb / inactive hub tab | **5.17:1** | 4.5 | pass |
| `#7C8DA6` on drawer top `#0D2038` | **4.85:1** | 4.5 | pass |
| `#1E66C9` 2px active underline | **3.16:1** | 3.0 (non-text) | pass — thin margin, so active state also carries white text + weight 600. Never use `#1E66C9` as text on navy. |
| `#5E6779` on navy | **3.07:1** | 4.5 | **fails — forbidden in chrome.** `--ink-muted` is a light-surface token only. |

**Light work area**

| Pair | Ratio | Verdict |
|---|---|---|
| `#0B1A2F` on `#FFFFFF` / `#F6F7FA` / `#F1F3F7` | 17.46 / 16.29 / 15.71 | pass |
| `#5E6779` on `#FFFFFF` / `#F6F7FA` / `#F1F3F7` | 5.69 / 5.31 / 5.12 | pass |
| `#667085` on `#FFFFFF` / `#F6F7FA` | 4.97 / 4.64 | pass |
| `#667085` on `#F1F3F7` | **4.48:1** | **fails (needs 4.5).** Rule: never `--ink-faint` on `--surface-2`; use `--ink-muted` there. |
| `#1E66C9` on `#FFFFFF` / `#F6F7FA` / `#EAF0F8` | 5.53 / 5.16 / 4.82 | pass |
| `#1E6D29` on `#FFFFFF` / `#E9F1EA` | 6.41 / 5.57 | pass |
| `#2E8E3A` as text on white | **4.16:1** | **fails.** Green text must be `#1E6D29`; `#2E8E3A` is fills/strokes only. `#FFFFFF` on `#297F34` = 5.02 (the existing button fill is correct). |
| `#8A5310` on `#FFFFFF` / `#FAF1DD` / `#FFF8EA` | 6.31 / 5.62 / 5.97 | pass |
| `#B36D14` as text on white / `#FFF8EA` | **4.11 / 3.88** | **fails — two live bugs to fix**: `BridgeTopbar.tsx:358` (`"{unread} need action"`) and `SupplierDockProfile.tsx:580` (`"Warning"`). Swap to `--amber-text` `#8A5310`. |
| `#B43838` on `#FFFFFF` / `#FBE3E3` | 5.89 / 4.83 | pass |
| `#6F4FCE` on `#FFFFFF` / `#F0EAFB` | 5.70 / 4.85 | pass |

**Targets, inputs, focus, motion, dialogs**

- Every nav row, tab and icon button ≥ **44×44** via `min-width/height: var(--tap-min)`
  (already defined, globals.css:117), with the visible chip inset — the existing
  32px-chip-in-44px-button pattern is correct and must be reused for the new
  `<WhatIsThis>` trigger (whose current ancestor is 15×15).
- Setup screen supplier input: `font-size: 16px` (iOS zoom floor), height 48px.
- Focus visible on **everything**: `outline: 2px solid #1E66C9; outline-offset: 2px`
  (5.53:1 on white, 3.16:1 on navy — both over the 3:1 non-text floor). The current nav
  items style only `:hover` inline and rely on the UA ring; make it explicit.
- Supplier tab strip: `role="tablist"`, ←/→ movement, roving `tabIndex`, `aria-selected`,
  `aria-controls`/`aria-labelledby` (none of this exists today).
- Hub strip and drawer already scroll horizontally; keep `scrollbar-width: none` but ensure
  keyboard focus scrolls the active tab into view.
- `prefers-reduced-motion: reduce` — the "Saved." fade, step-row collapse, link-spine
  animation and topology travellers all become instant state changes.
- Dialogs: the **only** remaining modal after this spec is `CommandPalette`, which already
  traps focus and closes on Escape (`CommandPalette.tsx:213,219-249`). `HelpSlideover` has
  Escape + focus-on-open (`:255,282`) but **no focus trap** — add one.
  `OnboardingWizard` is deleted (it had neither).
- One numeric badge in the product (Orders / needs review). Activity carries a **dot**, not
  a count, and the dot is never the only signal — the Issues tab shows the number in-page.

---

## 11. Implementation order (small, shippable slices)

1. `HubTabs.tsx` — `hidden?: true`, `VISIBLE()`, rewrite `HUB_TABS`/`HUB_LABELS` to the three
   hubs. `hubShowsTabs` and `hubTooltip` read `VISIBLE()`; `hubForPath` unchanged.
2. `BridgeSidebar.tsx` — one flat `NAV_MAIN` of four items; Settings promoted from `tail`;
   delete `LAUNCH_CORE_ONLY`/`LAUNCH_CORE_HREFS`; keep `INBOUND_ENABLED` (now gates tabs).
3. `BridgeTopbar.tsx` — inline the nav into Row 1 at `lg`; keep the standalone row
   `md→lg`; drop `HubEyebrow` from `PageHeader`.
4. `breadcrumb.ts` — `CRUMB_LABELS` to the new words (`bridge: "Overview"`,
   `inbox: "Orders"`, `mappings: "Item codes"`, `rules: "Rules"`,
   `templates: "Output layouts"`, `standards: "Format reference"`,
   `log: "Deliveries"`, `exceptions: "Issues"`, `health: "System status"`,
   `connectors: "Delivery channels"`, `asns: "Shipping notices"`);
   delete `WORKBENCH_GROUP_ROOTS`; `UNLINKED_GROUP_ROOTS` keeps `library`/`operations`/`inbound`.
5. `next.config.ts` — add `/drafts → /inbox` and
   `/library/rule-definitions → /library/rules` (permanent), matching the existing
   `/orders → /inbox` pattern.
6. `SupplierDockProfile.tsx` — new `TABS` + `?tab=` aliases; merge Catalog into Item codes as
   a second section; delete the `:1639` apology; add tablist a11y; add the Overview setup strip.
7. `settings/page.tsx` — five tabs; merge email/sftp/s3 into `intake`; merge
   `connectors` + webhooks UI into `notifications`; add `?tab=` aliases.
8. `UnifiedStatusBadge.tsx` + `InboxView.tsx` — status renames (§6.4) in **one** commit, plus
   the server-side `ready` chip expansion. These two files must change together or the
   chip/row disagreement regresses.
9. `WhatIsThis.tsx` + `concepts.ts`; retrofit `StandardsFieldPopover`'s trigger to the 44px box.
10. Setup screen; delete `OnboardingWizard`; move direction to Settings ▸ Workspace; root redirect.
11. `check-vocabulary.mjs` — new scope, two tiers, fence-state fix, `--nouns` mode,
    `src/lib/vocabulary.ts`.
12. Tests — `BridgeSidebar.test.tsx` per §1.5 (**reachability block untouched**),
    `HubTabs.test.tsx`, `breadcrumb.test.ts`, plus `no-mock-residue.spec.ts` route list
    (`tests/e2e/no-mock-residue.spec.ts:43`) and `live-full-e2e.spec.ts:481`.

---

## 12. Deliberately left out, and why

1. **A fifth nav item for the Dashboard.** The topology is a locked signature but not a daily
   destination; the queue is. It lives as Activity ▸ Overview. Cost: one extra click for a
   user who wants the topology from a non-Activity screen. Accepted — the brand mark still
   links straight to it.
2. **Merging supplier `Output` + `Delivery` into one "Sending" tab.** Tempting (6 tabs, one
   mental step) but they fail independently and the failure copy must point at exactly one;
   Delivery also holds credentials.
3. **Renaming `/inbox`, `/bridge`, `/library/*`, `/operations/*` routes.** Zero user value,
   breaks every bookmark, deep link, help article, `?tab=` alias and e2e path. Labels change;
   URLs don't.
4. **Turning Settings into a hub with routed tabs.** `hubForPath` matches pathnames, so
   `?tab=` tabs would all light at once. Settings keeps its own 200px left nav.
5. **A "system健康 all-clear" strip on Activity.** Green "everything normal" banners train
   people to ignore banners. The strip appears only when something is degraded.
6. **Numeric badges on Suppliers and Activity.** One number to chase. Activity gets a dot.
7. **Rewriting `/help` MDX to the nine nouns.** Those articles are reference material for a
   technical reader and must name cXML, `Idempotency-Key`, namespaces. They are excluded
   from the BLOCK tier instead — with the gloss requirement (WARN tier) applied.
8. **Deleting `/connections`, `/library/standards`, `/operations/{health,connectors,webhooks}`.**
   All are real, working surfaces someone will need during an incident. Hidden, not removed —
   and each has exactly one in-context entry point named in §1.3.
9. **A guided tour / coach marks / product tour.** The `<WhatIsThis>` popovers plus the
   already-shipped route-scoped help slideover cover first-encounter learning without a
   sequence that blocks the screen.
10. **Order Workshop changes.** DB-1 is IA. The three-column structure is locked and belongs
    to a different brief.
11. **A "Team / Members" settings tab.** Clerk organisation membership exists but there is no
    UI for it in this repo. Inventing a tab for it would be a spec for a feature, not IA.

