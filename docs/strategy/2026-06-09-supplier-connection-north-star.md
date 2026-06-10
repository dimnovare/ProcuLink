# ProcuLink North Star — The Versioned Supplier Connection

**Status: ACTIVE DIRECTION (decided 2026-06-09).** This supersedes the
"Baltic bootstrap / sell-first / freeze-features / win-customer-#1" near-term plan
(see `docs/strategy/2026-05-30-investor-analysis.md` and the "Current direction" box
in `CLAUDE.md`, both now ARCHIVED as prior context, not the active roadmap).

Founder decision: **stop treating "win the first customer" as the gating constraint.
Build the platform.** The engine is strong enough (tenant isolation, idempotency,
delivery retries, format-matrix coverage 8×7 green, catalog-grounded AI, audit
history, multiple delivery adapters, per-field + whole-document Scriban mapping,
all-format override). The next evolution is to make the scattered features —
mappings, rules, templates, delivery config, catalogs, connectors — **one coherent,
versioned product concept.** Pursue this as the ultimate goal over the coming days.

This memo is the plan of record. It synthesizes the 2026-06-09 external (ChatGPT)
architecture review with Claude's codebase-grounded assessment.

---

## The North Star

A first-class, **versioned Supplier Connection** that a user can configure, test,
publish, monitor, and roll back **as one unit**:

```
Supplier Connection (versioned)
├── Input detection & schema (how this supplier's files/feeds are recognised & parsed)
├── Canonical mapping        (source → canonical)
├── Product-code mapping      (buyer SKU → supplier item code; catalog-grounded)
├── Validation requirements   (versioned rule bindings)
├── Output template           (canonical → the supplier's required document; Scriban or field-map)
├── Delivery channel          (HTTP/SFTP/email/ERP/webhook…)
├── Credentials               (encrypted)
├── Test pack                 (sample orders + expected outputs)
└── Published revision        (immutable, monitorable, rollback-able)
```

**The defining invariant:** every order stores the exact `ConnectionRevisionId` it was
processed with. That single change unlocks reproducibility, safe configuration
changes, rollback, replay/simulation, and enterprise-grade auditability.

**Why this is the right unit:** the true value ProcuLink delivers is not an order —
it is an *active supplier connection that removes exceptions repeatably*. Today only
acceptance profiles are versioned; mappings, output templates, and delivery config
are mutable (the passport even acknowledges the missing version info —
`ProcuLink.Api/Services/PassportService.cs:250`). Making the Connection the versioned
aggregate is the architecture that turns ProcuLink from a powerful integration
*workbench* into a polished integration *product*.

---

## Honest risk (recorded so it isn't lost)

The earlier strategy warned: the bottleneck was selling, not features; don't overbuild
before a paying customer. **This pivot deliberately accepts the opposite bet** — that a
versioned-connection platform is worth building now because (a) the foundations are
already overbuilt and disconnected, and (b) the unifying concept is what makes the
overbuilt parts *sellable* as one product. Mitigation: sequence so that each group
ships a usable slice, keep a real order flowing end-to-end at every step, and still put
the product in front of a real supplier as soon as V1+V3 make a connection
publishable. We are not abandoning customers — we are building the thing worth selling.

---

## Sequenced roadmap (groups)

Priority order, refined from the external review. Each group is independently
shippable and leaves a working end-to-end order path.

### V1 — Versioned connection core  ★ start here
Introduce the `SupplierConnection` aggregate with a **draft → test → publish → archive**
lifecycle. A published revision is immutable. Pin `ConnectionRevisionId` to every order
at ingest. Subsume the existing per-supplier mapping / PO-mapping / output config /
delivery config / acceptance profile / catalog assignment as *components of a revision*
rather than free-standing mutable rows.
- New first-class tables (NOT canonical_json — see anti-pattern below).
- Backfill: existing supplier config becomes "revision 1 (published)".
- Definition of done: changing a supplier's mapping creates a new draft revision;
  publishing flips the active revision; orders record which revision they used.

### V2 — Replay & impact testing
Run historical orders through a **draft** connection revision **without delivering**.
Show canonical / validation / output **diffs** vs. the currently-published revision
before the user publishes. This is the "test before you ship a config change" feature
that makes V1 safe and is a major enterprise differentiator.

### V3 — Output templates as a real runtime feature  (engine already built)
The Scriban work (per-field expressions + whole-document templates + all-format
override + the template-preview endpoint, all landing now) is the engine. Finish the
*product*: template publication, supplier/connection assignment, artifact revision
pinning, rollback. Fix the stub — `OutputTemplatesController` currently returns
`Config:null` / `SuppliersCount:0`. Keep Scriban as the **power-user escape hatch**
behind the visual map, never the default experience.

### V4 — Unified validation
Turn the descriptive global rule catalog into **reusable rule definitions**, and make
connections hold **versioned rule bindings**. Do not build a second rules engine —
the executable layer already lives in `SupplierAcceptanceService.cs`. Bind, don't
re-implement.

### V5 — Deepen the canonical PO model (versioned)
`ParsedOrder` is relatively shallow. Add versioned support for legal identifiers,
ship-to / bill-to addresses, references, delivery schedules, Incoterms, taxes,
allowances, charges, attachments, price bases. **Note:** the whole-document Scriban
template + `ShippingAddress`/custom-field model already provides a flexible escape
hatch, so this can be staged — deepen the canonical where standards conformance
(V8) demands it, not speculatively.

### V6 — Exception-first UI
Keep the distinctive Source → Canonical → Output view, but expose it progressively.
The ordinary operator first sees:
> What is wrong? · Why is it wrong? · What should I do? · Will ProcuLink remember
> this? · Was the supplier actually notified, and did they accept it?

Topology/wiring becomes an *overview*, not the primary work queue. **And: never
equate HTTP 200 with business acceptance** — model "delivered" vs "supplier accepted"
distinctly (receipts/ACKs).

### V7 — Internal Connector SDK
Every connector declares a manifest: config schema, secret fields, capabilities,
supported receipts, retry policy, idempotency behaviour, test procedure, health.
Generate the config UI from the manifest.

### V8 — Standards conformance reports
"Supported" must mean *validated against a named profile*. Produce downloadable
conformance reports for cXML / UBL / X12 / EDIFACT / IDoc outputs. The deterministic
8×7 in×out matrix is the seed; build named-profile validation on top.

### V9 — AI decision history & confidence calibration  (in flight)
Persist accepted/rejected AI suggestions with candidates, confidence, model/prompt
version, operator decision (today they're cleared after resolution). Calibrate
confidence over time instead of merely accumulating mappings — this is the Learn-loop
moat made real.

### V10 — Catalog retrieval at scale
Current retrieval loads ≤2,000 products for in-memory matching
(`OrderIngestionService.cs:929`). Baltic IT distributors carry far more, so move to
**indexed Postgres** retrieval: exact code/barcode match → trigram / full-text
ranking. Real near-term for the ICP, not speculative.

---

## Cross-cutting (do alongside the groups)

- **Pricing overage inversion (fixing now):** at €0.50 overage, Growth@500 (€324) <
  Operations (€399), Operations@1,500 (€899) < Integration (€999) — volume
  disincentivises upgrading. Best-price billing / tier-specific overage. (In flight.)
- **Commercial framing:** the value unit is an *active supplier connection*, not an
  order. Revisit pricing toward connections + outcomes once V1 lands (separate
  decision; €5–10M European ceiling is credible for a versioned-connection platform).
- **Engineering hygiene:** enforce `docs/design-system/11-unified-page-rules.md` in CI;
  only 10/22 app pages use `PageShell`; ~243 raw hex + ~662 inline-style blocks remain;
  `SpineReview.tsx` (~3.3k lines) and `api-client.ts` (~3k lines) need decomposition by
  workflow/state ownership; generate a typed TS client from OpenAPI; fix the
  `@next/mdx` v16 vs Next 15 mismatch.

## Anti-patterns (do NOT do)
- Do not add more formats before versioning + conformance testing.
- Do not expose Scriban as the normal mapping experience (escape hatch only).
- Do not build another separate rules engine — bind to the existing one.
- Do not equate HTTP success with supplier business acceptance.
- **Do not let `CanonicalJson` become the permanent home for every new concept.** The
  per-order override/template/custom-fields live there today to dodge migrations; V1+
  must give the connection concepts **first-class tables/columns**.

---

## Immediate next steps
1. Land the in-flight work (Scriban template editor FE + template-preview /
   pricing-overage / AI-history / output-templates backend) — these are V3/V9 pieces.
2. Produce the **Group V1 implementation plan** (the `SupplierConnection` aggregate,
   lifecycle, `ConnectionRevisionId` pinning, migration + backfill strategy, how it
   subsumes existing config) — then execute V1, then V2 (replay).
3. Keep a real order flowing end-to-end at every step; demo a *publishable connection*
   to a real supplier as soon as V1+V3 allow.
