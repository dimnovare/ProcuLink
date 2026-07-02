# ProcuLink — naive-user journey UX assessment

**Date:** 2026-07-02 · **Method:** live production walkthrough (Claude-in-Chrome) as a first-time user who's never seen ProcuLink, cross-referenced with a full code audit of onboarding/help/wizard coverage.
**Scope tested live:** marketing home → sign-up entry → /welcome → dashboard → suppliers → add supplier → delivery config → test-fire (real send verified at receiver). Sign-up form itself observed only (creating an account/entering a password is off-limits for me). Stripe checkout walked to the pay page only (LIVE mode — no real purchase).

## Verdict: **genuinely usable, professional, and honest. The friction is concentrated in the technical setup forms, not the core flow.**

A procurement person can land, understand the value, sign up, and get to a delivered order without hand-holding. The onboarding, dashboard, empty states, and help coverage are strong. Where a non-technical user will struggle is the **connection setup forms** (delivery, catalog, PO mapping) — dense fields with jargon and no examples. That's the place to invest in wizards/examples.

---

## What already works well (keep it)

1. **Marketing homepage** — clear headline ("Send every purchase order to any supplier, any format"), plain subhead, honest stats (10 in / 6 out / 6 channels / EU — matches what I verified live), "built for procurement teams that don't want an integration project" nails the audience.
2. **Post-signup `/welcome`** — personalized, 4 concrete plain-language steps (add supplier → upload PO → confirm mapping → send). A newcomer knows exactly what to do.
3. **Dashboard onboarding checklist** — server-truth, progress-aware (6 steps, unlocks as you go, omits steps it can't verify rather than faking "0/6"), and evolves to "here's where teams go next" once you've delivered. Excellent.
4. **Add supplier** — asks for a name ONLY, defers complexity, proactively explains auto-process. Textbook low-friction.
5. **Delivery config core (HTTP)** — protocol picker, output-format dropdown with a plain hint, a "what this connector needs" panel, save → **prominent** "Send a test now", and a test result with a first-class honesty caveat: *"a successful test means their endpoint answered — it doesn't mean an order was accepted."* This is better than most competitors.
6. **Guidance coverage** — 25/25 app routes have a contextual SectionGuide; 19 help articles (10 with video) cover essentially every core task; HelpSlideover is one click from every screen; empty states mostly teach + link to the next action.
7. **The 3-column Order Workshop** — received | send | live preview, with pipeline progress and validation. Clear.

---

## Where a non-technical user will get stuck (ranked)

| # | Screen | The problem for a newcomer | Fix |
|---|--------|---------------------------|-----|
| 1 | **Delivery config — auth + raw JSON** | Picking `apikey / bearer / basic / oauth2` gives fields with **no example values**; the raw `{"url":…}` JSON blob at the bottom is dev noise that intimidates. cXML = 5 credential fields + DTD IDs with no vendor docs; OAuth2 = 7 fields. | Hide raw JSON behind an "Advanced / show raw config" toggle; add example placeholders per auth type (e.g. header `X-Api-Key`, value `sk_live_…`); keep cXML/OAuth2 collapsed under "Advanced". |
| 2 | **Catalog import** | No sample file — the user must guess the CSV/XLSX shape; "canonical fields" (code/name/price…) undefined; `code` required but blank with no example. | Add a **"Download CSV template"** with the canonical header + 2 example rows; one line explaining "we match your order lines to these codes". |
| 3 | **PO mapping / output override — Scriban** | Expressions + manipulator chips (`trim/split/regex`) with no inline syntax help or tester; user must trial-and-error or dig into Help. | Inline syntax hint + a "Formula help" popover with 3–4 copyable examples; link the `mapping-basics` article. |
| 4 | **Which delivery channel / format do I pick?** | "HTTP vs SFTP vs Email" and "XML vs cXML vs UBL vs X12" are unexplained — a procurement user doesn't know which their supplier needs. | One-line helper under each picker ("Use HTTP if your supplier gave you a webhook URL; SFTP if they gave you a server + login") + a link to the standards page. |
| 5 | **Small jargon** | Suppliers subtitle "each one's versioned integration lives in Connections" references a concept the user hasn't met. | Soften to plain language on first-run surfaces. |

**None of these block the happy path** — HTTP delivery, the most common channel, is already usable. They're clarity investments that reduce setup abandonment for the harder channels.

---

## Recommendations (in priority order)

**Ship now (low-risk, additive, no redesign):**
1. Delivery config: collapse the raw JSON + advanced auth (cXML/OAuth2) behind an "Advanced" disclosure; add example placeholders per auth type; keep the good HTTP defaults.
2. Catalog import: "Download CSV template" + example rows + one-line "what this is for".
3. Per-picker helper text for delivery channel + output format ("which one do I pick?").

**Next (small content):**
4. Scriban formula help popover with copyable examples on the PO-mapping and output-override editors.
5. Two new help snippets: "Which delivery channel should I choose?" and "Catalog file format".

**Optional (larger):**
6. A guided **Delivery setup wizard** (protocol → protocol-specific fields with examples → mandatory test) — only worth it if analytics show drop-off on the delivery tab; the current form + post-save test CTA is already decent.

The founder's instinct is right: the product doesn't need a redesign, it needs **examples, defaults, and "which one do I pick?" nudges on the 3–4 technical setup forms**. Everything around them (onboarding, help, dashboard, empty states, honesty copy) is already strong.

---

## Live loop proven this pass
Created "Nordic Office Supplies AS" from scratch in the UI → configured HTTP delivery to a real receiver → **test-fire 200, payload verified at the receiver**. The naive-user setup path works end to end.

---

## Guidance-gap sweep + fixes SHIPPED (2026-07-02)

A 7-reviewer parallel sweep (non-technical-user lens, ui-ux-pro-max + web-design-guidelines) found **111 raw findings**; ~40% were already covered by the existing section-guide/help-article system. The genuinely-missing additive fixes were implemented copy-only (no redesign, layout locked) across 6 batches and merged to `main`:

- **Setup forms (top-3):** delivery config — raw JSON behind "Advanced" disclosure, example placeholders per auth type, "which channel/format do I pick?" helpers; catalog — "Download CSV template" + canonical-field explainer; output override — "Formula help" popover with copyable Scriban examples.
- **Order Workshop (A1-A8):** "Review and send this order"; plain parsing copy; send-disabled + delivery-failed messages that name the action; invoice-badge consequence; "Before you send" + AI bulk-accept safety tooltip; plain stepper labels (Read/Check/Verify/Prepare/Send) with technical tooltips; mobile advanced-editing note. (Kept locked-test labels; added tooltips instead.)
- **Mapper workbench (B1-B4):** dismissible one-line orientation helper above the 3 columns (layout unshifted); incoming/outgoing/preview pane sub-headers; unmapped-required-field how-to-fix tooltip.
- **Connections lifecycle (C1-C7):** humanized the whole draft→test→publish→rollback vocabulary — read-only overlay, bundle-summary tooltips, HistoryDrawer ("Use this version"), plain publish/rollback/discard dialog bodies, per-status badge tooltips, replay pass/change summary + red/yellow legend. This was the highest-jargon area.
- **Library + upload (D1-D5):** mapping import CSV example + constraints, "why mappings exist" copy, Source-column legend; upload file-type error grouped by purpose, sample-order reword, multi-file hint.
- **Supplier tabs + settings + ops (E1-E6):** catalog empty-state explainer, mappings-tab eyebrow, PO-mapping order-file example; email intake host/app-password/folder helpers; connectors read-only clarification; webhooks signing-secret plain helper + status tooltips.

Also fixed a latent tsc error (`catalogSourceHelpers.test.ts` missing `columnMapping`) and a merge-time agent-collision artifact in `MapperWorkbench.tsx`. Full plan: `docs/qa/2026-07-fable5-push/guidance-gap-plan.md`.

Verification: `bunx tsc --noEmit` clean, `bun run build` green (62 pages), unit suites green per batch.
