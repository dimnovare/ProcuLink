# Practice order — designing the ending

The practice order reaches "send" and 404s, because the seeded delivery target is dead.
Every new user who completes onboarding hits it.

## Recommendation: **(a) deliver it for real, to an endpoint we control** — with the
simulation stated on the face of the screen.

**Reasoning.** The purpose of the practice order is to make someone believe the product
works. Belief comes from the receipt, not the file — "we sent it, here's the response,
here's the audit entry" is the one moment that proves the whole chain (parse → map → fix
→ generate → **deliver** → prove). Option (b) stops one step short of the only step that
is hard to believe on faith, and the download it ends on is the least novel artefact in
the product.

The honesty objection to (a) is real but it is a **copy problem, not a structural one**.
The risk is implying a real supplier received it. We remove that by naming the recipient
truthfully everywhere it appears: the supplier is **"ProcuLink Practice Endpoint"**, not a
plausible company name. Nothing then misleads — the delivery *is* real, and the recipient
*is* labelled as ours.

Option (b) also has a failure mode that's easy to miss: a user who never sees a delivery
never learns that delivery proof exists, so the product's strongest trust feature is
invisible at exactly the moment we're earning trust.

## The final screen

Reuse the **Output Passport** — the real delivery-proof artefact, not a bespoke success
page. Same navy header, buyer dock → transform → supplier dock, seal, checkpoints,
evidence trail. That the practice order ends in the same artefact as a production order
is the point.

Three deltas from a production Passport:

1. **Practice banner, above the passport, not inside it.** Blue `#1E66C9` (informational,
   not amber/alarm):
   > **This was a practice delivery.** We sent your file to ProcuLink's practice endpoint
   > and it confirmed receipt. No supplier received this order.
2. **Supplier dock reads `ProcuLink Practice Endpoint`** with a small `PRACTICE` chip. The
   fingerprint, response (`200 OK · acknowledged`), latency and timestamp are all genuine.
3. **Seal reads `DELIVERED · PRACTICE`** — same green, honest qualifier.

**What they're told (the one line that matters):**
> Everything you just did — parsing, the mapping fix, the generated file, this receipt —
> works the same way with a real supplier.

**The single next action:** **Connect your first supplier** (primary, green `send`
variant). It's the only forward move; the practice order has no further state.

Secondary, quiet: **Download the file** (they may want to show a colleague) and
**View in delivery log** (the practice entry is in the real log, tagged `Practice`, so
the log is never empty on day one — a small, real win).

**Not on this screen:** confetti, "Congratulations", a checklist of what they learned, or
a second CTA competing with supplier setup.

## Implementation notes

- Echo endpoint returns a genuine `200` with a payload ID; the audit entry is written like
  any other, with `practice: true`.
- The practice entry is **filterable out** of the delivery log (`Exclude practice`), so it
  never pollutes real operational counts, but is visible by default while the org has zero
  real deliveries.
- Practice deliveries never count toward plan volume limits.
