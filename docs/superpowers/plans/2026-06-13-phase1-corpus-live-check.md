# Phase 1 — 12-PO corpus live-extraction check (operator checklist)

**Status:** human checklist, NOT a CI test.
**Pairs with:** `ProcuLink.Infrastructure.Tests/Ai/CorpusExtractionShapeTests.cs`
(the deterministic, CI-safe half of Task 9).
**Date:** 2026-06-13 · branch `feat/flexible-mapping-phase1`.

---

## Why this is a checklist, not a CI test

The deterministic mapping contract — "if the model returns party VAT / EDI id /
manufacturer P/N / incoterms / per-line recipient / a raw_fields bag, those fields
survive losslessly onto the canonical `ExtractedOrder`" — is fully covered by
`CorpusExtractionShapeTests` with hand-built DTOs and zero network calls. That part
runs in CI on every commit.

What this document covers is the **other half**: proving the *live* `gpt-4o-mini`
extraction actually *captures* those fields from the real PDFs, where before Phase 1
they were silently dropped at the extraction schema. That cannot live in CI because:

1. **LLM non-determinism.** The model output varies run-to-run (especially on
   multi-row line tables), so any exact assertion against a live call would flake.
2. **It needs a real key.** Extraction is a no-op without `Ai:OpenAI:ApiKey`
   (provider must be `openai`); the extractor returns `Success=false` and the
   pipeline falls back to the deterministic regex parser. CI has no key.
3. **Per-org AI cap.** Each org has a monthly token cap (`IAiUsageTracker`). A live
   12-PDF sweep consumes real tokens against a real org's budget — not something to
   run unattended on every push. (If "all PDFs suddenly fail", check
   `GET /api/billing/ai-usage` first — a latched cap looks like a code bug.)

So: the **shape** is pinned in CI; the **capture** is proven by hand, once, with a
real key, and re-run by an operator whenever the extraction model or schema changes.

---

## The 12 corpus files

These are the founder's real anonymisable corpus, in `~/Downloads` (Windows:
`%USERPROFILE%\Downloads`). Each is one vendor's real PO PDF; the DocParser-confirmed
"expected widened fields" are in the table below.

| # | File (in `~/Downloads`) | Vendor |
|---|---|---|
| 1 | `Bestellung 4730154181.PDF` | REDACTED-PARTY |
| 2 | `purchaseOrder_10123140_1781251382358.pdf` | EXEMPLAR SEAFOOD |
| 3 | `PO2680200079.pdf` | LähiTapiola |
| 4 | `E032180_20260612194803867794229.pdf` | REDACTED-PARTY |
| 5 | `PO-26-10-00874.pdf` (a `purchaseOrder…` export) | Gjensidige |
| 6 | `PO9709842760.PDF` | Siemens |
| 7 | `nuovo ordine acquisto nr. ATT@4500898187.PDF` | Chiesi |
| 8 | `P-202635261.pdf` | REDACTED-PARTY |
| 9 | `Rheinbahn Bestellung 11421247 12.06.2026.pdf` | Rheinbahn |
| 10 | `redacted-fixture` | DNV |
| 11 | `Danfoss Purchase Order 4509404105.pdf` | Danfoss |
| 12 | `PurchPurchaseOrder_32782484_20260612_161520.PDF` | REDACTED-PARTY |

---

## Expected widened fields per vendor (DocParser-confirmed)

These are the fields Phase 1 should now capture where the old fixed-canonical header
**dropped** them. "—" = not present on that document (don't expect it). Empty cells in
a column mean that channel wasn't the salient one for that vendor; capture it if printed.

| # | Vendor | Ship-to / Bill-to | VAT / EDI id | Contact | Manufacturer P/N | Incoterms | Per-line recipient |
|---|---|---|---|---|---|---|---|
| 1 | REDACTED-PARTY | shipTo (Linz) | VAT `REDACTED-TAXID` | `redacted@example.invalid` | `SCPMX94EGK` | `DDP` | — |
| 2 | EXEMPLAR SEAFOOD | shipTo (farm site) | — | — | — | — | `redacted@example.invalid` |
| 3 | LähiTapiola | billTo | EDI id `REDACTED-DOCNO` | — | — | — | — |
| 4 | REDACTED-PARTY | shipTo (plant) | — | — | `X2791HS-B1` | `FCA` (or `DAP`) | — |
| 5 | Gjensidige | billTo | org/VAT no. | requisitioner | — | — | per-line cost-centre |
| 6 | Siemens | shipTo + billTo | VAT (DE) | buyer contact | vendor mat. no. | `DAP`/`FCA` | — |
| 7 | Chiesi | shipTo (Parma) | VAT (IT) | buyer email | — | incoterm if printed | — |
| 8 | REDACTED-PARTY | shipTo (site) | VAT (AT/DE) | site contact | — | — | per-line delivery addr |
| 9 | Rheinbahn | shipTo (depot) | VAT (DE) | buyer contact | vendor mat. no. | — | — |
| 10 | DNV | shipTo | — | requisitioner | — | — | `redacted@example.invalid` + cost centre (raw) |
| 11 | Danfoss | billTo (Nordborg, DK) | VAT `REDACTED-TAXID` | buyer contact | vendor part no. | — | — |
| 12 | REDACTED-PARTY | shipTo (port) | VAT (DK) | buyer contact | — | — | per-line vessel/voyage (raw) |

Anything labelled on the document but with no canonical slot (supplier number, EDI
id, contract no., cost centre, cost object, requisition no., vessel/voyage) must land
in the order's **raw_fields** bag verbatim — that is the lossless escape hatch.

---

## Live-check steps (per file)

Pre-reqs:
- A running app (API + Worker) with a real `Ai:OpenAI:ApiKey` and `Ai:Provider=openai`.
- A test org whose monthly AI cap has headroom (check `GET /api/billing/ai-usage`;
  raise via the admin per-org override if needed).
- A supplier the upload can resolve to (or the manual-review path).

For **each** of the 12 files:

1. **Upload** the PDF through the running app (browser upload, or the inbound REST /
   email path). Let the Worker run the parse job to completion.
2. **GET the order** — `GET /api/orders/{id}`. Confirm:
   - PO number, currency, lines, and totals look right.
   - The header-level widened fields are populated: `parties[]` (with role + VAT
     where the table says so), `contactEmail`, `incoterms`, `buyerOrderRef`.
   - The per-line widened fields are populated where the table marks them:
     `manufacturerPartNumber`, `recipient`.
3. **GET the `source_captures` row** for that order and confirm the **raw bag** is
   populated — every labelled-but-unslotted value (EDI id, cost centre, contract no.,
   vessel/voyage…) is present verbatim. This is the proof that nothing the model saw
   was silently discarded at the schema boundary.
4. **Cross-check against the table above.** Every "expected" cell that is non-`—`
   should be captured. A miss is a real regression to file (extraction prompt or
   schema), not a flaky test.

Record pass/miss per file. A full green sweep is the live proof that Phase 1 lossless
capture works on the real corpus, not just on hand-built DTOs.

---

## Honest caveats

- **Scanned / XFA layouts may still need human review.** A PDF with no text layer
  goes through the vision fallback (or self-hosted OCR for no-egress orgs), and
  **every** line from a scanned PDF is review-flagged by design — there is no text
  layer to verify the numbers against. XFA/dynamic forms can also surface partial
  text; treat their extraction as assisted, not certified.
- **`gpt-4o-mini` is non-deterministic on multi-row line tables.** It can transpose,
  merge, or drop rows on dense multi-line POs run-to-run. For documents with many
  line rows, a **stronger extraction model** (set `Ai:OpenAI:ExtractionModel`) is
  recommended; the mini model is fine for header capture + small line counts.
- **This proves capture, not correctness of every number.** The anti-hallucination
  net flags numbers that don't appear in the source, but a hallucinated value that
  coincides with another printed number can still pass — human review on flagged
  lines remains the backstop.
