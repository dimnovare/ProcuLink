# 09 — Trust Rules

These are not principles. They are **non-negotiable product rules**. Every screen is designed against them.

## Rule 1 · Provenance everywhere

Every AI suggestion, auto-mapping, or auto-corrected field shows three things:

1. **Confidence** — as a percentage, in mono, with threshold color.
2. **Source attribution** — what evidence supports the suggestion. *"AI · 84% — used 6× by Heinrich in last 90d"*
3. **One-click jump** — to the matching source-document anatomy zone, with the relevant text highlighted.

**Implementation:**
- Every spine node that came from extraction shows a confidence chip and the source field path (`← header`, `← parties.billTo`).
- Every AI suggestion in the Issues rail has a "Jump to field" action that scrolls + highlights both the spine node and the source anatomy zone.
- Every mapping shows its source: `Manual / AI / Imported / Inherited from supplier rule`.

**Anti-pattern:**
- A green checkmark with no number. (A "looks good" with no provenance is worse than no validation at all — operators stop trusting both.)

## Rule 2 · No silent automation

Auto-process — the mode where a crossing skips human review when validation passes — is:

- **Off by default** for every new supplier dock.
- **Per-supplier**, never global.
- **Explicitly turned on** via a settings page with a confirmation step that lists what the supplier requires.
- **Visible** in the supplier dock header as a status pill: *"Auto-process: ON"* or *"Auto-process: OFF — every crossing reviewed"*.
- **Audited** — every auto-processed crossing has a Crossings Log entry tagged `auto-processed` with the rules that passed.

The Upload Workbench shows the current auto-process state for the chosen supplier in an amber pill if it's enabled, so the operator knows their file may go through without review.

**Anti-pattern:**
- A subtle "auto" toggle hidden in workspace settings that applies to everything. Operators must surface the toggle on the screen where it bites.

## Rule 3 · Failure is loud, recoverable, and explained

The **Failed** view is the most useful screen in the product. Not a tombstone.

Every failed crossing shows:

1. **What the supplier said** — the literal error message, or the rejection from the ERP, or the parse error. Raw, in mono.
2. **What we sent** — the exact output file we delivered (cXML, CSV, etc.), with a "View output" button that opens the side-by-side diff against the canonical model.
3. **What we'd retry** — if the failure is recoverable (e.g. a transient SFTP timeout), a primary "Retry" button. If recoverable with a fix (e.g. "quantity must be > 0"), a primary "Fix and resend" button that opens the spine review pre-scrolled to the failing field.
4. **What this means for similar orders** — if 3+ orders failed with the same rule, surface a banner: "5 crossings have failed with this rule today. Adjust the validation?"

**Anti-pattern:**
- "Something went wrong." with a help link.
- A red banner with no action.
- Hiding the supplier's response text.

## Rule 4 (implicit) · The source is sacred

The user can never get into a state where they can't see what the supplier or buyer originally sent. The Canonical Spine Review always shows the source on the left, even if the operator has heavily edited the canonical center column. The original file is always one click away.

This is implicit in the `<DocumentAnatomy>` component — it's a primary column, not a side panel.

## Rule 5 (implicit) · Edits are auditable

Every operator edit to a canonical field is:
- Saved as a draft within 2 seconds.
- Logged in the Crossings Log with the diff: `payment_terms: "Net 30" → "Net 45" by Maria K. at 14:32`.
- Visible in the Order History tab (right-side drawer) as an inline diff.

The operator never has to ask "did my edit save?" or "what did I change?".

## Trust scorecard

Before shipping any new screen, the designer answers these five questions:

1. Can the operator see where a value came from? (Provenance)
2. Can the operator see what would happen automatically? (No silent auto)
3. Can the operator recover from this screen if something failed? (Loud failure)
4. Can the operator see the original file? (Source is sacred)
5. Can the operator see what they (and anyone else) changed? (Audit)

If any answer is "no," the screen isn't ready.

## Friday 4:30pm test

Read the screen and ask: *"Would a 40-year-old order-processing operator at a Baltic distributor trust this with a €71k cXML order at 4:30pm on a Friday?"*

If they would hesitate, the design hasn't earned the trust yet. The five rules above are how we earn it.
