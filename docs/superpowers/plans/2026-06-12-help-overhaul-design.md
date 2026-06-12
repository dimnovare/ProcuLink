# ProcuLink Help System Overhaul — Design (implementation-ready)

Repo: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink` (FE) · article copy truth checked against `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink` backend.
Principles applied throughout: calm (no tours/modals/popovers/auto-opens — text links and the existing "?" slideover only), offer⇔works honesty (never document an unavailable feature; remove false claims), single registry as source of truth, Bridge Layer tokens only.

⚠ Coordination note: a concurrent session holds uncommitted changes in `HelpSlideover.tsx`, `section-guides.ts`, `(app)/layout.tsx`, `BridgeTopbar.tsx` (and deleted `SectionGuide.tsx`). The FE agent must start from that working tree / its commit, not from `f4505a3`.

---

## 1. HelpSlideover v2

File: `src\components\bridge\HelpSlideover.tsx` (rebuild in place; keep `Props`, ESC handling, overlay, 380px aside, analytics hook).

### Layout when opened (top → bottom)

1. **Header** — "Help" + close (unchanged). Focus moves to the search input on open; `BridgeTopbar` restores focus to the "?" button on close.
2. **Search input** — placeholder "Search help…", debounced 150 ms, shared Fuse index (§3). While a query is active, sections 3–5 are replaced by up to 6 results (article title + category chip + read time); empty state: "No matches — open the help center or contact support." Each result opens `/help/<slug>` in a **new tab** (preserves operator work-in-progress; matches the existing "Help opens new tab" convention).
3. **"This screen"** — existing `SectionGuideBody` (purpose / bullets / Start here). Unchanged content; in-app guide hrefs (`?tab=…`) navigate in place and close the slideover, as today.
4. **"Related reading"** — replaces the single `CONTEXTUAL_LINKS` entry with up to 3 article rows (title + read time, new tab). Data source: a new `articleSlugs?: string[]` field on `SectionGuideEntry` in `src\lib\section-guides.ts` — **one registry, one matcher**; delete `CONTEXTUAL_LINKS` from the slideover. Resolver rule: slugs are looked up in `HELP_ARTICLES` and **silently skipped if absent** — this lets the FE pass wire slugs for articles the content pass hasn't written yet without ever rendering a dead link.
5. **"Watch the walkthrough"** row — `▷ Watch the 3-minute walkthrough` → `/watch` (new tab). Rendered **only** when `NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL` or `NEXT_PUBLIC_WALKTHROUGH_LOOM_URL` is set (new helper `src\lib\walkthrough.ts: walkthroughConfigured()`). Env-gated = offer⇔works; today the video is invisible from the entire help system.
6. **Footer nav** (always visible, incl. during search): "Open help center ↗" → `/help`, "Contact support ↗" → `/support`, "Report a bug ↗" → `/support#report-a-bug` — all new tab. Fix the broken anchor by adding `id="report-a-bug"` to the "Report a problem" heading in `src\app\(marketing)\support\page.tsx`.
7. **No-guide fallback** (route with no registry match, e.g. `/admin`): instead of an empty panel, show "Popular articles" — top 3 curated slugs — plus search + footer.

Styling: keep the current calm card pattern; replace remaining hex literals with tokens (`var(--border)`, `var(--surface)`, `var(--ink…)`, `var(--brand-green-soft)`); 44px tap minimum on rows.

Analytics: keep `help_slideover_opened`; add `help_search {query_len, results, surface}`, `help_article_click {slug, surface, route}`, `help_watch_click {surface}`.

Amber-dot cue + `guideSeenKey` localStorage behavior: unchanged (founder-decided calm pattern; explicitly not adding stronger cues).

### Route → related-articles map (goes into `section-guides.ts` `articleSlugs`)

| Route | articleSlugs (display max 3) |
|---|---|
| `/bridge` | dashboard-and-statuses, first-upload |
| `/upload` | first-upload, order-intake-options |
| `/upload/preview/[orderId]` *(new guide entry — see Task 3)* | first-upload, troubleshooting |
| `/inbox` | dashboard-and-statuses, item-codes |
| `/inbox/[orderId]` | output-mapping-editor, item-codes, delivery-setup |
| `/drafts` | *(none — feature unavailable; guide only)* |
| `/inbound/invoices` | order-intake-options |
| `/inbound/asns` | *(none — intake unavailable; guide only)* |
| `/library/suppliers` | delivery-setup, item-codes |
| `/library/suppliers/[id]` | delivery-setup, item-codes, mapping-basics |
| `/library/buyers` | inbound-mode |
| `/connections` | connections |
| `/connections/[connectionId]` | connections, output-mapping-editor |
| `/library/mappings` | mapping-basics, output-mapping-editor, item-codes |
| `/library/rules` | validation-rules |
| `/library/rule-definitions` | validation-rules |
| `/library/templates` | output-templates, delivery-setup |
| `/library/standards` | output-templates, order-intake-options |
| `/operations/exceptions` | exceptions-and-stuck-orders, troubleshooting |
| `/operations/health` | exceptions-and-stuck-orders |
| `/operations/log` | dashboard-and-statuses, delivery-setup |
| `/operations/connectors` | delivery-setup, api-and-integrations |
| `/operations/webhooks` | api-and-integrations |
| `/settings` | billing-faq, email-polling, api-and-integrations, inbound-mode |

After the content pass, every sidebar screen has ≥1 article except `/drafts`, `/inbound/asns` (honestly unavailable — no docs until shipped) and `/admin` (intentional).

---

## 2. Article plan (keep / update / delete + new)

Registry: `src\lib\help-articles.ts`. MDX: `src\app\(marketing)\help\<slug>\page.mdx`.

### Existing 10

| Slug | Verdict | Content-pass work |
|---|---|---|
| `item-codes` | **KEEP + extend** | Add "Catalog file requirements" section (accepted columns, formats, size limits; API/SFTP/FTP catalog channels only if actually live — verify before claiming). Add keywords. |
| `delivery-setup` | **KEEP** (becomes the single delivery article) | Absorb unique delivery-config content: protocol choice detail, HTTP auth options, ERP connectors (Erply/Directo, honest "delivers generated artifacts" framing). Keep HTTP-200≠acceptance section verbatim. |
| `billing-faq` | **KEEP** | Keywords only (overage, soft cap, 429, Pilot read-only, Distributor). |
| `troubleshooting` | **KEEP** | Add cross-link to exceptions-and-stuck-orders; keywords (BOM, comma decimal, scanned PDF, IDoc). |
| `order-intake-options` | **UPDATE** | Fix plan gating: email/SFTP/S3 ingestion = **Growth and up** (`ProcuLink.Core\Constants\PlanConstants.cs`), not Integration. Add SAP IDoc ORDERS05 to the format list. Link api-and-integrations + inbound-mode. |
| `first-upload` | **UPDATE (minor)** | Mention format auto-detection + PO-number preview on /upload; neutral "trading partner" phrasing or inbound note; link upload-formats reference. |
| `mapping-basics` | **UPDATE** | All **8 manipulators** (Replace, Trim, DateFormat, Concat, Split, Divide, Multiply, Fallback) + Scriban `Expression` escape hatch (framed as advanced); starter templates (Erply/Directo apply-template) + AI schema inference; link output-mapping-editor. |
| `ai-suggestions` | **UPDATE** | Catalog grounding (allow-list — suggestions can only propose real catalog codes); per-plan monthly AI token budget/cap behavior; no-egress orgs note (AI gated → human review). |
| `email-polling` | **UPDATE (worst offender)** | Fix plan gating to Growth+/all paid (currently contradicts its own blurb, the section guide, the backend, and pricing). |
| `delivery-config` | **DELETE** | Remove registry entry + MDX dir; permanent redirect `/help/delivery-config → /help/delivery-setup` in `next.config.*`. **The false claim "Production traffic is paused on the supplier until at least one test-fire succeeds" must not survive the merge** — no such gate exists (`DeliveryService.TestFireAsync` only writes an `OrderId==null` attempt). |

### New articles — P1 (write first; money-maker + north-star + honesty gaps)

1. **`connections`** — *"Supplier connections: draft, test, publish, roll back"* — category **Connections** (new). Outline: what a connection bundles (mapping + rules + template + delivery + catalog refs); revision lifecycle draft → test → publish → archive; every order pins a `ConnectionRevisionId` (reproducibility/replay); test packs + replay (informational differences vs publish-gating failures); rollback; when to use connection editing vs supplier tabs. Linked from: `/connections`, `/connections/[connectionId]`.
2. **`output-mapping-editor`** — *"Editing the output your supplier receives"* — category Mapping. Outline: where it lives (order review); per-order override vs supplier-level mapping (precedence; default is byte-identical); source field selection; manipulator chains; Scriban expressions; live preview; add-field; what transform/deliver then produce. Linked from: `/inbox/[orderId]`, `/library/mappings`, `/connections/[connectionId]`.
3. **`api-and-integrations`** — *"API keys, inbound API, webhooks, and connectors"* — category **Integrations** (renamed from Email). Outline: `plk_` API keys (create/revoke, one-time display); org slug + ingress endpoint (`POST /api/ingress/{slug}/orders`, ping); payload expectations; outbound webhooks + HMAC-SHA256 `X-ProcuLink-Signature` verification snippet; auto-deactivate after 3 failures; Zapier/Make described **conservatively** as "via custom webhook" (link-outs were removed; do not claim native apps until listed). Linked from: `/settings`, `/operations/webhooks`, `/operations/connectors`.
4. **`exceptions-and-stuck-orders`** — *"Working the exceptions queue"* — category Troubleshooting. Outline: what lands there (review-flagged lines incl. all scanned-PDF lines, parse failures, delivery failures); ignore vs resolve semantics; dead-letter + requeue from System health; "is it slow or dead" — when to check `/operations/health`; escalating to support. Linked from: `/operations/exceptions`, `/operations/health`, `/operations/log`, SpineReview stuck strip (§4).
5. **`inbound-mode`** — *"Receiving orders from your customers (inbound mode)"* — category Getting started. Outline: per-org direction choice; what relabels (Supplier→Customer) vs what stays identical (pipeline, transform+deliver); switching in Settings; intake channels for inbound. Linked from: `/settings`, `/library/buyers`; cross-referenced from order-intake-options.

### New articles — P2

6. **`dashboard-and-statuses`** — *"Order statuses from upload to delivered"* — Getting started. Status vocabulary (received / parsing / needs review / Normalized / ready_to_deliver / delivering / delivered / delivery_failed / returned), HTTP-200≠acceptance reminder, where each surfaces (dashboard, inbox, delivery log). Linked from: `/bridge`, `/inbox`, `/operations/log`.
7. **`validation-rules`** — *"Validation rules and rule definitions"* — Mapping. Rules vs rule-definitions catalog (**catalog is non-enforcing — keep the honesty from the section guide**), severities, where failures surface. Linked from: `/library/rules`, `/library/rule-definitions`.
8. **`output-templates`** — *"Output templates and formats"* — Delivery. Templates per supplier/format; supported output formats per the conservative `/library/standards` catalog (the offer⇔works SoT); Scriban template body; preview/test. Linked from: `/library/templates`, `/library/standards`.

### P3 (optional, after the above)

9. `invoices-and-asns` — honest early-capability scope (invoice upload works; EDIFACT invoice/DESADV are stubs; ASN intake unavailable). Only write if founder wants inbound-docs surface area documented.

**Explicitly NO articles for:** Drafts (unavailable), Admin (internal). Documenting them would violate offer⇔works.

---

## 3. /help center improvements

File: `src\app\(marketing)\help\page.tsx` + `src\lib\help-articles.ts`.

1. **Registry becomes the single source**: add `keywords: string[]` and `readMin: number` to `HelpArticle`; delete the page-local `READ_MIN` map (root cause of the missing item-codes/delivery-setup read times — a hand map can't drift if it doesn't exist).
2. **Categories** → 8, in this order: `Getting started, Connections, Mapping, Delivery, Integrations, AI, Billing, Troubleshooting`. Rename Email→Integrations (absorbs email-polling + api-and-integrations). Add `CATEGORY_META.Connections` (use the brand blue accent + blue soft). **Fix `Billing.soft`** — currently `#DCFCE7` (green, duplicates Getting started); change to the blue soft tint used elsewhere in the Bridge token set.
3. **Search scope**: new `src\lib\help-search.ts` exporting `buildHelpFuse()` — weighted keys `title 0.5, blurb 0.3, keywords 0.15, category 0.05`, threshold 0.4 — used by **both** /help and the slideover. Curated `keywords` per article (content pass) fixes "OAuth2 / test-fire / BOM / IDoc / FTPS / HMAC / Scriban / 429 return nothing" without a build-time MDX indexing pipeline (that's v2, out of scope).
4. **Ordering**: registry array order = reading order (drives the prev/next pager) — reorder into the learning path Getting started → Connections → Mapping → Delivery → Integrations → AI → Billing → Troubleshooting.
5. **Popular articles**: curate to first-upload, connections, delivery-setup, output-mapping-editor, billing-faq.
6. **/watch in the hero**: "Prefer watching? 3-minute walkthrough →" under the search box, rendered only when `walkthroughConfigured()`.
7. **Discoverability**: add Help to the marketing footer (it's currently only reachable from in-app surfaces + sitemap).

---

## 4. Calm contextual guidance beyond the slideover

One new component: `src\components\help\HelpLink.tsx` — a quiet inline text link (12–13px, `var(--ink-muted)`, trailing ↗, `target="_blank" rel="noopener"`, fires `help_inline_link_click {slug, route}`). Static, never animated, never auto-opening. Place it in surfaces that already exist:

| Surface | Link | Why |
|---|---|---|
| `SpineReview.tsx` stuck strip (~L2257) | "Check system health" → `/operations/health` **(in-app)** + HelpLink → exceptions-and-stuck-orders | Fixes guidance-gap #5 — the cheapest, highest-value fix in the list: an operator staring at a stuck order is currently told "re-upload or contact support" with no health route. |
| `/connections` empty state | HelpLink → connections | North-star concept gets day-1 explanation without a tour. |
| Inbox empty state (next to the practice-order CTA) | HelpLink → first-upload | |
| `/operations/webhooks` empty state | HelpLink → api-and-integrations | |
| `/library/templates` empty state | HelpLink → output-templates | |
| `/library/rules` empty state | HelpLink → validation-rules | |
| Delivery-failed panel (`FailedPanels`) | HelpLink → delivery-setup (beside the existing "Set up delivery" CTA) | |
| Settings email/SFTP section headers | HelpLink → email-polling / order-intake-options | |
| Upload 429/limit panel | HelpLink → billing-faq | |

Rule for the FE agent: HelpLink only ever appears inside an **existing** empty state, error panel, or section header — never as a new floating element, badge, dot, or card.

---

## 5. Explicit out of scope

- Tours, coachmarks, modals, auto-opening popovers, first-visit cards (founder rejected; `f4505a3` removed them — do not reintroduce).
- Server-side persistence of guide-seen/dismissal state (explicit non-goal; localStorage stays).
- `/welcome` redirect + walkthrough video production (founder config: Clerk redirect, video URLs).
- Build-time MDX body-text search indexing (curated keywords are v1).
- In-app docs viewer (articles stay on marketing routes, opened in new tabs).
- Connections onboarding-checklist rework (gap G11) and direction-prompt placement (gap #6) — separate tasks.
- Native Zapier/Make app documentation (not listed yet).
- Drafts / ASN-intake articles (features unavailable), Admin docs, localization, per-article videos.

---

## 6. File plan + ordered tasks

### Phase A — FE implementation (ONE agent, ~9 tasks, ships independently of content)

Decoupler: the related-articles resolver skips slugs missing from `HELP_ARTICLES`, so all route wiring lands now; new articles "light up" as the content pass adds them.

1. **Registry rework** — `src\lib\help-articles.ts`: add `keywords`/`readMin` fields (populate for the 9 surviving articles from current READ_MIN + obvious terms; content pass refines); rename Email→Integrations; add Connections category meta/order; remove `delivery-config`; reorder to the learning path. Update any `HelpCategory` consumers.
2. **delivery-config removal** — delete `src\app\(marketing)\help\delivery-config\`; add permanent redirect in `next.config.*`; merge-flag for content pass.
3. **Section-guides** — `src\lib\section-guides.ts`: add `articleSlugs?: string[]` per the §1 table; add `/upload/preview/[orderId]` guide entry (waiting/parse-preview orientation); update `section-guides.test.ts` (coverage + resolver-skips-unknown-slug test).
4. **Shared search** — new `src\lib\help-search.ts` (`buildHelpFuse()`); new `src\lib\walkthrough.ts` (`walkthroughConfigured()`).
5. **HelpSlideover v2** — rebuild per §1 (search, related list from `articleSlugs`, watch row, popular-articles fallback, new-tab policy, focus management, analytics). Delete `CONTEXTUAL_LINKS`.
6. **/help page** — §3 items 1–6 (registry-driven readMin, shared fuse, category fixes incl. Billing soft color, reading order, popular list, env-gated /watch hero link).
7. **Anchors + footer** — `id="report-a-bug"` in `src\app\(marketing)\support\page.tsx`; Help link in the marketing footer.
8. **HelpLink + placements** — `src\components\help\HelpLink.tsx` + the 9 placements in §4 (SpineReview stuck-strip health link is part of this task).
9. **Verify** — `bun run build`; run section-guide tests; click-through `/help`, slideover on 5 representative routes (incl. a no-guide route), redirect, anchor.

### Phase B — content pass (writer agent, MDX only; each article = MDX + registry entry with blurb/category/readMin/keywords)

Order: (1) email-polling fix, (2) order-intake-options fix, (3) delivery-setup merge (kill the false test-fire-gate claim), (4) mapping-basics, (5) ai-suggestions, (6) item-codes catalog section — then new P1: connections, output-mapping-editor, api-and-integrations, exceptions-and-stuck-orders, inbound-mode — then P2: dashboard-and-statuses, validation-rules, output-templates. Every claim verified against the backend (`PlanConstants.cs`, `ProcuLink.Transform\Mapping\Manipulators\`, `/library/standards` catalog) before it ships — offer⇔works cuts both ways: no overclaim, and no documenting gates that don't exist.