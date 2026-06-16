# App-shell audit (2026-06-16) — reconciliation: is everything done?

Source: the 6-cluster, adversarially-verified whole-app audit (run `wilbglh93`) over all 24 in-app
screens → **58 confirmed findings**. This ledger accounts for every one.

## Tally

| Disposition | Count | Meaning |
|---|---:|---|
| ✅ **Fixed** | 47 | Shipped + live (commits `aed4780`, `5277687`, `5b13e36`, + Phase-4 `20a1fad`/`501a29c`) |
| 🟢 **Already honest** | 5 | The finding overstated it — the screen already handles it correctly |
| ⚪ **Correct-skip** | 4 | Deliberate product name / real plan name — changing would be wrong |
| ❌ **False positive** | 2 | Verified wrong on close read (verify-before-fix) |

= 58. **Nothing actionable is left open.**

## ✅ Fixed (47)

Jargon → plain language (the dominant theme): "envelope"→"format", "canonical field"→"order field",
"artifact"→plain, "SKU mapping"→"item code mapping", "Reusable definitions"→"Built-in rule
definitions", "Map codes — step 1 of 2"→"Confirm item codes", "Document totals"→"Order totals",
"Reconstructed from parsed fields"→"Built from your file", pipeline labels Parse/Normalize/Validate→
Reading file/Checking format/Preparing review, exception "stage:"→"Step:" + "pipeline pass"→"next time
the order is reprocessed", connection leftovers ("Live revision"→"Live version", ReplayPanel "revision"→
"version", page metadata, "Every"→"Each version"), "upgrading from Pilot"→plan-agnostic (Settings email
+ SFTP/S3 pull), webhook "ingested"→"received", standards/templates copy.

a11y: aria-labels on inbound ASN/invoice file inputs; removed redundant title on the notifications
bell; title hints on disabled Test-fire / Save / Test-connection buttons.

Broken-state / clarity: Settings email-error + Admin "Stripe MRR unavailable" / "pending read-only"
reworded; dashboard contradictory KPI → "Needs attention / Open now"; ValidationRules "Global"/"All
suppliers" unified; supplier LiveEditNotice + auto-process + "Automatic import" reworded.

Mock placeholders (mock-mode only): real-looking company names in Drafts/Buyers/Templates demo data →
generic "Example …".

**T10** (this pass): dashboard `pending_review` label "Validate"→"Needs review" so the same order reads
the same on the dashboard and the inbox (UnifiedStatusBadge is the single source of truth).

Plus the founder's bug #2 (Phase 4): acceptance failure messages now plain-language (`AcceptanceMessages`).

## 🟢 Already honest — no change needed (5)

- **Connectors: SAP Ariba / Coupa / Microsoft Dynamics 365 "coming_soon" (4 findings).** Already carry a
  clear amber **"Coming soon"** pill (`ConnectorStatusPill`), a **"Not available yet"** footer, and a
  "— not yet available" description. Triply honest; not presented as connectable. The "dead-control"
  finding overstated it.
- **Webhooks "Edit hidden" (offer-works).** Correct by design: there is no PUT endpoint, so Edit is
  hidden (not a broken no-op), and `handleSave` carries an honest "delete and re-add to change it"
  notice. This IS the offer⇔works principle.

## ⚪ Correct-skip — deliberate / real names (4)

- **"Validation rules" supplier tab** — the founder deliberately named this (not "Acceptance").
- **PLAN_LABELS "Operations plan" / "Integration plan" / "Growth plan" (2 findings)** — these are the
  real, public plan names (pricing), not internal jargon.
- **Invoices download "↓ CSV"** — the button is compact; the `title="Download as CSV"` tooltip already
  expands it. Not actually inconsistent.

## ❌ False positives — verified wrong (2)

- **operations/health inline `style={{display:flex}}` "defeats md:hidden".** The flagged element is a
  CHILD of the `md:hidden` parent; a `display:none` parent hides its whole subtree, so the child's
  inline display can't un-hide it. No conflict. (The code comment even documents why it's correct.)
- **ValidationRules "Validation rules tab" link.** The link text already matches the real tab label;
  the proposed "rename to Acceptance" contradicts the deliberate name.

## Verdict

The app-shell audit is **fully resolved**: every user-visible jargon/dead-control/broken-state/a11y/
mock-leak finding is fixed or confirmed already-correct. The only declined items are real product names
and two verified false positives. No open work remains from this audit.
