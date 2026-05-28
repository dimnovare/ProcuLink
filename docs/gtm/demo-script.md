# ProcuLink — 15-Minute Live Demo Script

_Last updated: 2026-05-28. Run this verbatim until you've done 10 demos. Every minute earns its place._

---

## Pre-call setup (3 min before)

- ProcuLink open at `/bridge`, signed in as a clean demo org.
- `webhook.site` tab open with a unique URL ready.
- One supplier ("Demo Supplier A") pre-created with a CSV mapping template and HTTP webhook delivery pointing at the webhook.site URL.
- Screen share ready. Camera on. Phone silenced.

---

## 0:00–0:30 — Set the frame

**Objective:** Make them the protagonist; get their file on screen within 30 seconds.

**Do:** Skip intro slides. Open with screen share already up.

**Say:**
> "Quick frame: I'm not going to pitch. I'm going to run one of your actual purchase orders through ProcuLink live. Do you have a recent PO file you can drop in chat — any format, CSV, Excel, PDF? The messier the better. I want your worst one, not your cleanest."

If they don't have a file ready, use a pre-loaded "ugly-real" demo PO. Never use a clean sandbox file.

---

## 0:30–3:00 — Drop their file, watch it parse

**Objective:** They see their data, in their words, on screen in under 60 seconds.

**Do:**
- Navigate to `/upload`.
- Drop the file. Select "Demo Supplier A" as destination.
- Click upload. Status moves: `pending_parse` → `parsing` → `pending_review`.
- Page auto-routes to `/orders/{id}` or `/inbox/{id}`.
- Walk through the canonical view: PO number, order date, line count, currency.

**Say:**
> "What you're seeing is the canonical model. Same shape regardless of whether you uploaded a CSV, Excel, PDF, or cXML. This is what the engine works on — everything downstream sees this structure. Your file is now stored, audited, and replayable. If we re-process this PO in six months, we get the same output."

---

## 3:00–6:00 — Resolve AI suggestions, show audit trail

**Objective:** Show that exceptions get caught BEFORE the supplier sees them.

**Do:**
- Point at any line with `NeedsReview = true`.
- If there's an AI suggestion, click "Use suggestion" and explain the confidence/reason/provenance fields visible on the line.
- If there's no suggestion, type the supplier code manually.
- Save the resolution.
- Scroll to the audit/log section showing who resolved what, when.

**Say:**
> "Two things matter here. One: any line that doesn't map deterministically to your supplier's catalog gets stopped before transform — your supplier never sees a malformed code. Two: the AI suggests a match with confidence and reason, but it never auto-applies. A human always confirms. And every confirmation is logged with the operator's name and timestamp — when you have a delivery dispute six weeks from now, you can prove what was sent."

---

## 6:00–9:00 — Configure ERP delivery

**Objective:** Show that delivery is configured per supplier, not custom-coded.

**Do:**
- Open the Demo Supplier's `Delivery` tab.
- Show the protocol selector: HTTP, Erply, Directo, (SFTP coming).
- Show the auth fields (credentials masked).
- Click "Test fire" — webhook.site receives a payload in their view.
- Switch to webhook.site tab, point at the received payload. Show it's the supplier-ready format.

**Say:**
> "This is per-supplier configuration, not per-customer code. HTTP webhook for one supplier, Erply for another, Directo for the third — all running on the same engine. Credentials are AES-GCM encrypted. Every delivery attempt — success or failure — is written to the audit log. If a delivery fails, you see why, you replay it. You never have to email someone asking 'did you get the PO?'"

---

## 9:00–12:00 — Show the second order auto-flowing

**Objective:** Prove the setup happens ONCE.

**Do:**
- Drop a SECOND PO (also their real file, or a second variant of the demo PO).
- Don't touch anything. Watch it flow: parse → review → transform → deliver.
- If all lines map deterministically (they should, on the second file with the same supplier), it goes end-to-end with zero clicks.

**Say:**
> "First order, you spent two minutes resolving codes. This is the second order. Same supplier, same item codes — zero touches. That's what 'set up once' actually means. Order three through three hundred run the same way. Your team intervenes only when something is genuinely new — a new SKU, a new supplier, a new format."

---

## 12:00–15:00 — Pricing + setup + 14-day Pilot

**Objective:** Get a "yes" or a clear "no" on the Pilot. Do not leave with "we'll think about it."

**Do:**
- Open the pricing page on a second tab.
- Walk through Pilot (14 days, 20 orders, free) → Growth (€149) → Operations (€399) → Integration (€999).
- Most prospects belong on Operations (€399). Anchor there.

**Say:**
> "Pilot is 14 days, 20 orders, one supplier — free, no card. That's enough to run real volume through and prove it on your data, not mine. If it works, Operations is €399/month for 500 orders and 10 suppliers — which fits most teams at your size. Setup is €500 one-time, waived if you confirm in this call."

---

## Objection responses

**"We already have X." (an ERP, a portal, an EDI provider)**
> "Good — keep X. ProcuLink doesn't replace your ERP or your supplier portals. It sits between your buyer PO and each supplier's required output. If X already converts your POs into every supplier's format and delivers them, you don't need us. If your team still touches each PO before it goes out — that's the gap."

**"How much does it cost?"**
> "Pilot is free for 14 days. Operations — which fits your volume — is €399/month plus a €500 one-time setup. I'll waive the setup if you commit on this call. Total year-one cost: €4,788. Compare that to one person's loaded cost spending 20 min/order reformatting."

**"What if it makes a mistake?"**
> "AI suggestions never auto-apply — a human confirms every unresolved code. Deterministic mappings can be wrong, but they're wrong consistently — fix the mapping once, every subsequent order is right. And every dispatch is logged with the exact payload sent. You always know what went out, when, and why."

**"We need to talk to IT first."**
> "Fine. There's nothing for IT to do in the Pilot — no integration, no install. We're a web app and a webhook. The only IT conversation comes when you want delivery to land inside your ERP — Erply or Directo — and that's a 30-min config session, not a project. Should I send your IT lead a one-pager and we book the demo for after?"

**"We'd need to integrate with our ERP."**
> "Erply and Directo connectors are already built — config session, not a build. For SAP B1 or Business Central, the Pilot covers HTTP webhook delivery into whatever your IT team accepts. Custom ERP integration is part of the Integration tier (€999) and gets scoped in week 2."

---

## The close (last 60 seconds)

**Say:**
> "Two pilots ahead of you, four slots available this quarter. €500 setup, €399/month, 14-day trial. If you say yes today we kick off Monday. Want me to send the agreement?"

Then **stop talking**. Wait. The first one to speak loses leverage. If they ask a clarifying question, answer in one sentence and re-ask. If they say "let me think about it" — push: "Think about what specifically? I'd rather answer it now than in an email next week."

---

## Demo failure modes to plan for

- **Their PDF is scanned, not text-layer.** Be honest: "OCR isn't in production yet. Email me the file and I'll process it manually for the Pilot. Production OCR is on the Integration tier roadmap."
- **Their file has columns you've never seen.** Use the mapping editor live. "This is exactly what the mapping editor is for. Watch — 60 seconds."
- **The delivery test-fire times out.** Skip to webhook.site and pre-populate. Never apologise more than once. "Webhook test endpoints are flaky — let me show you a confirmed delivery from this morning instead."
- **They ask about a format you don't support (X12, EDIFACT).** "Not yet. cXML is in, X12 and EDIFACT are on the Integration roadmap. If that's a deal-blocker for you, tell me now."
