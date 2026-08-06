# /pricing + /security after SSO and Peppol BIS 3 were removed

No invented capabilities. Every bullet must map to something the backend enforces.

## 1 · Do the plan cards still balance?

Symptom to fix: a tier that lost a bullet now looks thinner than the one below it, which
reads as *worse value*, not as honesty.

**Stop selling by bullet count.** Six tiers × a bullet list is where this failure comes
from. Restructure each card as:

1. **One capability line** (display type) — what this tier *is for*.
2. **The two limits that actually gate the tier** — orders/month, connected suppliers.
   Mono figures, same two rows in every card, so tiers compare on a fixed axis.
3. **"Everything in {previous}, plus"** + **max 3** differentiating bullets.

With a fixed comparison axis and a capped bullet list, removing one bullet can no longer
make a tier look starved — the tiers differ on numbers that are always present.

Suggested capability lines (all real): Pilot *"Run your first orders end to end"* ·
Growth €149 *"One supplier flow, automated"* · Operations €399 *"Your whole supplier
list, monitored"* · Integration €999 *"Connect it to your ERP and intake channels"* ·
Distributor €1,499 *"High volume, many buyer sources"* · Enterprise *"Your controls,
your terms"*.

**Lead with the differentiator, above the cards:** the **visual output designer that
emits supplier-specific formats** — sample-first, live preview, reusable template. That
is the thing worth selling and it belongs on the page as a product strip (screenshot of
the designer), not as a bullet inside one tier.

## 2 · Does /security still make its case?

Yes, but there is now a hole where the conformance claim sat. Fill it with what the
product **actually does**, stated plainly — these are stronger than the removed claims
because they're demonstrable in the UI:

- **Every delivery is provable.** Timestamp, endpoint, channel, response, payload — kept
  as an append-only record. (The Output Passport is the on-screen proof; screenshot it.)
- **We verify the supplier's server identity** and refuse to deliver if it changes.
  (Ties directly to the new host-key work — a real, checkable control.)
- **Nothing sends until it validates.** Required fields, schema, item codes, totals,
  supplier rules — a blocked order cannot be sent.
- **Credentials are never shown after entry**; secrets are masked and rotatable.

Do **not** replace SSO with a vaguer trust word. Say what exists.

## 3 · Something true we aren't saying

Two, both worth more than the bullets removed:

- **"You can see exactly what we'll send before we send it."** Live preview of the real
  payload, per supplier. Almost nothing in this category shows that.
- **"Set a supplier up once; every later order follows the same rules."** The reusable
  supplier flow is the ROI argument — it belongs on /pricing next to volume limits.

## Honesty note

If SSO is asked for in sales, the accurate line is *"Not available yet"* on an Enterprise
FAQ row — an explicit "not yet" costs less trust than a silent gap.
