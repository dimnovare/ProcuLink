# ProcuLink — Pilot Onboarding Checklist

_Last updated: 2026-05-28. Maximum 4 founder-hours per Pilot. Anything beyond is the customer's job._

---

## Goal

In 14 days, the customer's ops team runs 5+ of their own POs through ProcuLink end-to-end with zero founder intervention. If that's true, they convert to Operations (€399/mo).

---

## Hour 1 — Kickoff + tenant setup + first supplier (60 min)

**0:00–0:30 — Kickoff call (Zoom, recorded with consent)**

- Confirm decision-maker is on the call. If not, abort and reschedule.
- Confirm the ONE supplier we're configuring for the Pilot. Not three. One.
- Confirm the supplier's required output format (CSV/XML/cXML/JSON) and delivery channel (HTTP webhook / Erply / Directo / email).
- Get a real recent PO file from the customer right now, on the call. If they can't produce one in 5 minutes, the Pilot isn't real — exit gracefully.
- Confirm success criterion in writing (Slack/email same day): "3 of your POs delivered to {supplier} via {channel} without founder touching the system. If yes, we convert to Operations on day 14."

**0:30–1:00 — Tenant setup + first supplier mapping**

- Create their Clerk org. Give them admin access.
- Create the one supplier in `/library/suppliers`.
- Configure the PO mapping in `Supplier → PO Mapping` tab using the file they just sent. Aim for 80% coverage — don't perfectionist this.
- Configure the delivery endpoint in `Supplier → Delivery` tab. HTTP webhook to a controlled URL first (webhook.site or the customer's staging endpoint). DO NOT point at production on day one.
- Run the "Test fire" button. Confirm a payload arrives at the destination.

**Exit criteria for Hour 1:** Customer is logged in, sees their dock, has one supplier configured, and we've successfully test-fired an empty delivery to the configured endpoint.

---

## Hour 2 — Test-fire 3 of their real recent POs (60 min)

**Same day if possible; latest next morning.**

- Upload their 3 most recent POs to that supplier — yours hands, screen-share, customer watching.
- Resolve any AI suggestions or unmapped item codes live. Each resolution should auto-learn for the next PO (`ItemMappingService` persists the mapping).
- Run each through transform → delivery. Confirm delivery payload arrives at the destination endpoint.
- For each successful delivery, screenshot the audit log entry. Save to a shared folder.
- If any PO fails, log the exact failure mode (parse error, mapping gap, transform validation, delivery 4xx/5xx). Fix it before moving on.

**Exit criteria for Hour 2:** 3 POs delivered successfully. Customer has seen the full crossing twice. Customer can name the steps without prompting.

---

## Hour 3 — Customer-side live run (45 min)

**Day 3 or 4. NOT the same day as Hour 2 — they need time to digest.**

- Customer's ops person screen-shares THEIR machine.
- They upload a fresh real PO to the configured supplier.
- They click through resolve/transform/deliver themselves. You watch and answer questions only.
- If they fumble, do NOT take over. Say: "What would you click next? Try that." Note where they stumble — those are UX bugs, not training problems.
- Send a 1-page "How to run a PO" cheat sheet after the call (screenshot-based, not text-heavy).

**Exit criteria for Hour 3:** Customer ops person ran one PO end-to-end without you touching their keyboard. You have a list of every UX rough edge they hit.

---

## Hour 4 — Handoff (30 min on Slack/email, no call)

**Day 5-6.**

- Slack message: "You're set. Run 5 more of your real POs this week. I'll watch the audit log silently. Don't ping me unless something is genuinely broken — friction is expected, and I want to see where it shows up. Day 14 we have the convert-or-decline conversation."
- Add yourself to their `purchase_orders` audit feed (read-only). Watch from a distance.
- Do NOT chase them. Do NOT offer to "jump on a call." They need to feel ownership.

**Exit criteria for Hour 4:** Customer is on their own for 8-9 days. Founder hours used: 4. Total.

---

## Week 1 success criteria

- 3+ of customer's own POs delivered successfully without founder touching the system.
- Zero "ProcuLink is broken" support requests (UX friction is fine, true breakage is not).
- Customer's ops person has run at least one PO end-to-end solo.

If any of these miss, the Pilot is at risk. Diagnose root cause before day 14, not on day 14.

---

## Day 14 — Convert-or-decline conversation (30 min call)

**Open with:**
> "We had 14 days. You ran {N} POs through. Talk me through what went well, what didn't, and whether ProcuLink is solving the problem we agreed on at kickoff."

**Listen for 5 minutes. Don't pitch.**

**Then one of three paths:**

**A. They love it.**
> "Then we move to Operations on the 1st. €399/month, 500 orders, 10 suppliers. The setup fee is waived because you ran the Pilot. I'll send the agreement and the Stripe Checkout link today. Want to add supplier #2 this week?"

**B. They like it but are hesitant.**
> "What's the one thing that, if I fixed it, would make this a yes today?"
>
> Listen. If it's reasonable and 1-week build, commit verbally (NEVER in writing) and extend Pilot by 7 days. If it's "we want feature X" with no urgency, push to convert: "Operations is €399 for the next 30 days. Cancel anytime. If feature X isn't shipped by day 30, we refund. Fair?"

**C. They decline.**
> "Why specifically?"
>
> Take the answer at face value. Do NOT negotiate. Do NOT discount below €399. Thank them, ask if you can use the Pilot data as an anonymous reference, and ask for one referral. "Who else at a Baltic company you know has this problem?"

---

## What NOT to do during the Pilot

- **Don't customize the engine for one customer.** No new field manipulators, no per-customer code branches, no exception logic that only runs for them. If the engine can't handle their file with the existing 8 manipulators + AI suggestions, that's a real signal — listen to it, but don't fork the codebase.
- **Don't promise features in writing.** Verbal commitments are flexible, written ones are contracts. "We'll consider adding X" stays verbal. Any roadmap commitment goes in the Operations contract or it doesn't exist.
- **Don't drop the price below €399.** Pilot is free. Operations is €399. If they can't afford €399, they can't afford the implementation pain of switching, and they'll churn in month 3.
- **Don't extend the Pilot more than once, and never more than 7 days.** "We need another month to evaluate" means they're not buying. Force the decision at day 21 max.
- **Don't onboard supplier #2 during the Pilot.** One supplier, end to end, repeated. Adding a second supplier mid-Pilot dilutes the signal and triples the founder hours.
- **Don't run their POs for them.** If you do, you'll do it forever. The Pilot is them proving they can self-serve, not you proving the tool works on their data.
- **Don't add them to a "case study pipeline" before they pay.** No quotes, no testimonials, no logos on the landing page until €399 has hit Stripe at least twice.

---

## Pilot scorecard (track for every Pilot)

| Metric | Target | Actual | Notes |
|---|---:|---:|---|
| Founder hours used | ≤4 | | |
| POs delivered in week 1 | ≥3 | | |
| Customer-driven POs (week 2) | ≥5 | | |
| Support touches from customer | ≤3 | | |
| UX rough edges captured | log all | | |
| Day 14 outcome | convert | | A / B / C |

Aggregate this monthly. If conversion is below 50% after 6 Pilots, the product is the problem, not sales execution.
