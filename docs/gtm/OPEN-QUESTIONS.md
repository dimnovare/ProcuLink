# GTM open questions — answer when GTM becomes relevant

> **Status: PARKED.** Per founder direction on 2026-05-28, GTM execution is on hold until product is closer to shippable. These questions don't need answers now. Re-read them when you're 2–4 weeks from first paid pilot.

The GTM agent flagged six decisions that anchor the playbooks in `docs/gtm/` to your actual situation. Until these are answered, treat the GTM docs as scaffolding, not commitments.

---

## 1. What is Day 0?

The plans in `first-100-users-strategy.md` count months from "Day 0," which assumes Group J live QA has passed (Railway env vars set, Stripe/Clerk/IMAP verified live). Group J is currently in progress.

**Decide:** What date does Day 0 anchor to — the day Stripe Checkout works end-to-end against a real test card on Railway, or some earlier soft launch?
**Answer:** We will test everything end-to-end first, make the product ideal, quick and nice. And then if everything is done, we go live.

## 2. ERP-consultant partnership margin

`first-100-users-strategy.md` Phase 2 prescribes one ERP-consultant partnership offering them **20% of year-one revenue** from referrals.

**Decide:**
- Are you willing to give up 20% margin? (Some founders cap at 15%; some go to 25% for an exclusive Erply/Directo partner.)
- Is there already a specific Estonian Erply or Directo consultant on the radar, or is this aspirational?
**Answer:**
- Yes
- No

## 3. Entry-tier pricing posture

Demo script and pilot checklist anchor on **€399 Operations** as the default sell and treat **€199 Growth** as a downgrade. The strategy doc explicitly rejects "Growth-first" outreach.

**Decide:** Confirm or flip?
- **Stay:** Operations-first means higher ACV, slower pipeline, fewer logos. Demos take 25 min.
- **Flip to Growth-first:** Lower entry bar (€199), more logos but longer time-to-€10k MRR, easier to land junior buyers.
**Answer:** Confirm.

## 4. Churn tripwire severity

Tripwire #3 in `first-100-users-strategy.md` says: **"halt all new acquisition if churn >5%/month at <50 customers."**

**Decide:** Is 5% truly a halt signal, or noise at small N?
- 1 cancellation out of 18 paid is 5.5%. If a customer's procurement director leaves, that's not a product signal.
- Alternative threshold: "halt if 3 consecutive monthly churns OR >10% in any 60-day window."
**Answer:** No, we do not halt. We will build the ideal SaaS that works on it's own and can generate revenue/profit "silently" in the background not requiring us to do much other than market and sell.

## 5. ICP weighting — IT distributors vs industrial manufacturers

`icp-target-list-template.md` is a Baltic distributor/manufacturer mix. Two ICP archetypes pull in opposite directions:

| Archetype | Decision speed | ACV | Cycle |
|---|---|---|---|
| IT distributor (Markit, Also, Topdata-style) | Fast (4–6 wk) | €399–€999 | Short |
| Industrial manufacturer (Krimelte, Konesko-style) | Slow (3–6 mo) | €1,499–€3,500 | Long |

**Decide:** Weight the first 50 prospects toward which? Mixing both is the default, but it burns founder time on context-switching.
**Answer:** We will mix.

## 6. cXML in demos

`demo-script.md` references cXML as a live capability. The cXML parser + transformer sit on the **`feat/group-k-standards` branch**, not yet merged to `main`.

**Decide:**
- **Merge before any external demo** — Group K parser/transformer get reviewed and merged.
- **Or update the demo** — drop the cXML mention; talk about CSV/XLSX/PDF only until Group K lands.
**Answer:** We need as many transformers, parsers as possible for almost any type/standard.

---

## How to use this file

When you're ~14 days out from your first real pilot conversation:
1. Open this file.
2. Answer each question in one sentence at the bottom.
3. Update the GTM docs that reference each decision.
4. Delete or archive this file.

Until then: don't optimize GTM. Build the product.
