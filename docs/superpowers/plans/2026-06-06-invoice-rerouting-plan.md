# Plan (NOT YET IMPLEMENTED) — Phase 4 invoice-pipeline rerouting

**Status: deferred — needs founder review before building.** Written 2026-06-06 during the
overnight hardening batch. An adversarial investigation judged this **unsafe to ship unattended**
(`safeToAutoMerge: "no"`, `needsMigration: true`, large blast radius, semantic mapping gaps), so it
was deliberately left out of that batch. The other deferred items (image slimming, OCR warm-boot,
ingress byte caps + PdfPig timeout, Phase 4 UI surfacing) shipped; this one did not.

## What exists today (the gap)

When a PO upload is classified `document_type = "invoice"` (the LLM PDF/email classifier), ProcuLink:
- forces the order to `pending_review`, and
- writes a `ClassifiedAsInvoice` audit event,

so an invoice that arrives on the PO path is **never silently transformed and delivered as a PO**.
But it does **not** create an `InvoiceEntity` — the invoice domain (`InvoiceEntity`/`InvoiceLineEntity`,
`InvoiceController`, `IInvoiceService`, `UblInvoiceParser`, `ParseInvoiceJob`) is untouched. So the
document is held for a human but is not yet a first-class invoice record.

Evidence:
- `ProcuLink.Api/Services/OrderService.cs` — `CreateStubFromParsedOrderAsync` (email/REST) and
  `ParseStoredFileAsync` (PDF/file) compute `isInvoice` and write only the audit event.
- `ProcuLink.Core/Entities/InvoiceEntity.cs` + `InvoiceLineEntity.cs` — target shapes.
- `ProcuLink.Infrastructure/Services/InvoiceService.cs` — `PersistParsedAsync` requires an existing
  stub; `CreateStubAsync` requires a `Stream`. Neither fits an already-parsed in-memory order, so a
  new `CreateFromParsedAsync` overload would be needed.
- `ProcuLink.Infrastructure/ProcuLinkDbContext.cs` — `Invoices`/`InvoiceLines` DbSets already exist
  (no migration needed *to write rows* — but see the link-column point below).

## Why it was deferred (the risks)

1. **Two asymmetric persistence paths.** `ParseStoredFileAsync` uses `ExecuteUpdateAsync`, which the
   InMemory test provider cannot translate — so the higher-traffic PDF/file path would ship
   effectively unverified by the existing InMemory harness unless a **relational (Npgsql) integration
   test** is added.
2. **Semantic mapping gaps.** Invoice entities have non-nullable `SubTotal/TaxTotal/GrandTotal` and
   per-line `LineTotal`, but the parsed model is all-nullable; `DueDate`/`BuyerRef`/`SupplierRef` have
   no source; `InvoiceNumber` would be a **PO number**, not a real invoice number. Without careful
   fallbacks, invoices come out with 0/garbage values that operators would see as legitimate received
   invoices.
3. **No PO↔invoice link/dedup column.** True idempotency on the Hangfire retry path relies solely on
   the `entity.Status == "parsing"` guard, and operators can't trace which invoice came from which
   order. A proper `InvoiceEntity.SourcePurchaseOrderId` column is the honest fix — **and that needs a
   migration**, defeating the "no-migration" appeal.
4. **Dual-write atomicity.** Order save and invoice save must be atomic; a partial failure leaves an
   order flagged-as-invoice with no invoice (or an orphan invoice). The order path also fires
   `order.created` integration triggers — invoice trigger semantics are undecided.
5. **Worker DI.** The order parse path runs in the **Worker**; `IInvoiceService` registration in
   `ProcuLink.Worker/Program.cs` is unconfirmed and would DI-fail in production only.

## Recommended approach (when greenlit)

Build on a branch behind a config flag (default OFF), **additive** — keep the existing
`isInvoice → pending_review + ClassifiedAsInvoice` behaviour and ADD invoice creation alongside it:

1. Add `InvoiceEntity.SourcePurchaseOrderId` (nullable) + migration — gives idempotency + traceability.
2. Add `IInvoiceService.CreateFromParsedAsync(orgId, supplierId?, ParsedInvoiceData, sourceFileKey?, ct)`
   that creates the invoice + lines from already-parsed data (no stub/Stream required), and is
   idempotent on `SourcePurchaseOrderId`.
3. A `MapToInvoiceData` helper in `OrderService` with explicit fallbacks:
   `IssueDate = OrderDate ?? UtcNow.Date`; `InvoiceNumber = PoNumber` (flagged provisional);
   `Currency ?? "EUR"`; per-line `LineTotal = LineAmount ?? Quantity * (UnitPrice ?? 0)`;
   `UnitCode = Unit ?? "EA"`; `TaxRate ?? 0`; `DueDate/BuyerRef/SupplierRef = null`.
4. Call it from both ingress paths inside the existing status-guarded block (retry-safe).
5. Register `IInvoiceService` in `ProcuLink.Worker/Program.cs`.
6. Tests: extend `OrderServiceBatchResolveTests` (InMemory) for the email/REST path **and** add a
   relational/Npgsql integration test for the `ParseStoredFileAsync` (`ExecuteUpdateAsync`) path.

## Decision needed from founder

- Is a first-class invoice record wanted now, or is "held for review on the PO path" sufficient until
  there is a paying customer who needs the invoice domain? (The current freeze prioritises selling.)
- If yes: accept the `SourcePurchaseOrderId` migration as part of the work (recommended over relying on
  the status guard alone).
