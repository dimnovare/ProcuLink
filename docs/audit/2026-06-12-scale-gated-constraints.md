# Scale-gated constraints (documented, not yet fixed)

**Date:** 2026-06-12
**Source:** the audit top-10 plan's "EXPLICITLY DEFER" table
(`docs/superpowers/plans/2026-06-12-audit-top10.md`).

These are **known, intentional** constraints that are correct at the current ICP
scale (one API replica; per-org order/invoice history in the low thousands) and
that would only need work once a specific threshold is crossed. None is an active
bug. Each is documented here and carries a matching `SCALE-GATED CONSTRAINT`
code comment at the site so the threshold-to-revisit is visible where the code
lives.

No behaviour changed in this pass — comments + this doc only.

---

## 1. Inbox search — leading-wildcard ILIKE (full-table scan)

**Site:** `ProcuLink.Api/Services/Orders/OrderQueryService.cs` — the `search`
predicate (`EF.Functions.ILike(o.PoNumber, "%term%")` over PO number, supplier
name, buyer name).

A leading-wildcard `ILIKE ('%term%')` cannot use a B-tree index, so the search is
a sequential scan over the org's orders. It is already **org-scoped and
paginated**, so the scan is bounded to one tenant's partition and only runs when a
search term is supplied. Fine at ICP scale.

**Revisit past ~50k orders/org:** add a `pg_trgm` GIN index on
`(po_number, buyer_name)` (and supplier name), or drop the leading wildcard in
favour of a prefix index if product allows prefix-only search.

---

## 2. Invoices list — no pagination, no `(org, created_at)` index

**Site:** `ProcuLink.Infrastructure/Services/InvoiceService.ListAsync` (exposed by
`GET /api/invoices` in `InvoiceController`).

Returns the org's **entire** invoice list with `OrderByDescending(CreatedAt)` and
no `LIMIT`. There is no composite `(organisation_id, created_at)` index, so the
sort falls back to sorting the org partition. Invoice ingestion is a low-volume,
Integration+ **secondary** surface (the PO path is primary), so today this is
cheap.

**Revisit past ~1k invoices/org:** add a `(organisation_id, created_at DESC)`
index and paginate the endpoint the way `GET /api/orders` already does
(`limit`/`offset` + `totalCount`).

---

## 3. Ops health dead-letter list — in-memory `GroupBy → First` reduction

**Site:** `ProcuLink.Infrastructure/Services/OpsHealthService.ListDeadLetterAsync`
— "latest delivery attempt per order" computed by an in-app
`GroupBy(OrderId).First()` over the page's attempts.

The reduction is in-memory because the EF InMemory provider does not translate
`GroupBy → First` reliably, and the input set is intentionally **bounded**:
dead-letter is a rare terminal state an operator drains, so the page covers a
handful of orders and their attempts.

**Revisit if this is ever widened to a busy/unbounded set:** push "latest attempt
per order" into SQL — a window function or `DISTINCT ON (order_id) … ORDER BY
attempted_at DESC` — instead of reducing in the app.

---

## 4. Rate limiter — process-local fixed windows (per-replica)

**Site:** `ProcuLink.Api/Program.cs` — `AddRateLimiter` (the `upload` / `transform`
/ `ai` / `signed-url` / `webhook` / `support` policies and the global backstop are
all `FixedWindowRateLimiter`s).

These counters live in **process memory**, so each API replica enforces its own
window independently. With **one** API replica (today's deploy) the published
limits are exact. With N replicas the effective per-partition ceiling becomes
~N× the configured value, because one partition can hit each replica's window.
The limits are tuned conservatively, so this is headroom rather than a
correctness hole.

**Revisit before running multiple API replicas:** back the limiters with a shared
distributed store (e.g. Redis) so the window is global across replicas.

---

## 5. Delivery config — `ConfigJson` stored in cleartext (non-secret only)

**Site:** `ProcuLink.Core/Entities/SupplierDeliveryConfig.ConfigJson`.

`ConfigJson` is stored **in cleartext**, and that is **by design — not a
secret-at-rest finding.** Every secret (passwords, API keys, bearer tokens,
basic-auth, OAuth2 client secrets, SFTP/FTP credentials) is kept out of
`ConfigJson` and stored **AES-GCM encrypted** in
`SupplierDeliveryConfig.EncryptedCredentials`. `ConfigJson` holds only non-secret
connection metadata (endpoint URL, host, remote path, non-secret headers,
timeout).

**Invariant to preserve (not a future fix, a standing rule):** never write a
credential/secret into `ConfigJson`. If a new delivery option needs a secret, add
it to the encrypted credential payload. There is no scale threshold here — this
entry exists to record that the cleartext column was reviewed and is intentionally
non-secret.
