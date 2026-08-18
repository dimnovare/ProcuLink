# Runbook — GDPR erasure request (Art. 17, "right to be forgotten")

**Date:** 2026-08-18
**Audience:** a platform admin on the `Admin:UserIds` / `Admin:Emails` allowlist. Nobody else can run this.
**Time:** ~15 minutes for a single order; longer if you must agree the scope with the customer first.
**Risk:** **HIGH and irreversible.** There is no undo, no soft-delete, and no restore-from-backup path
offered to customers. Read §3 before you run anything.

**Erasure is admin-only by decision, not by omission.** There is no self-serve control and none is
planned. The customer-facing half of this decision is published on `/privacy` under
"Deleting your data" (`project-proculink/src/app/(marketing)/privacy/page.tsx`); this runbook is the
operator half. If you change what the endpoints do, change that copy in the same change.

---

## 1. What an erasure actually removes — read this before you promise anything

Source of truth: `ProcuLink.Infrastructure/Services/DataErasureService.cs`. This table is what the
code does, not what we would like it to do.

### Deleted outright

| What | Where |
|---|---|
| Stored source document, transformed output files, and any order-confirmation source, **in Cloudflare R2** | `DataErasureService.cs` step 1 — `_storage.DeleteAsync(key, ct)` per key |
| `purchase_orders` (the order row) | `_db.PurchaseOrders.Remove(order)` |
| `purchase_order_lines` | `RemoveRange` |
| `outbound_artifacts` | `RemoveRange` |
| `delivery_attempts` | `RemoveRange` |
| `order_exceptions` | `RemoveRange` |
| `order_validation_results` | `RemoveRange` |
| `po_passport_events` | `RemoveRange` |
| `audit_events` **for this order only** (`entity_id = <order id>`) | `RemoveRange` |
| `ai_suggestion_decisions` | `RemoveRange` |
| `idempotency_keys` | `RemoveRange` |
| `email_import_records` (IMAP attachment name + Message-Id) | `RemoveRange` |
| `order_confirmations` + `order_confirmation_lines` | `RemoveRange`, committed first (RESTRICT FK) |
| `order_supplier_suggestions` | `RemoveRange` |
| `order_parties` — **contact name, email, phone, address, VAT** | **`ON DELETE CASCADE` only.** The service never names this table. |
| `source_captures` — **`raw_text`, the full extracted document** | **`ON DELETE CASCADE` only.** Same. |

The last two rows are the ones to watch. They are the highest-PII rows attached to an order and they
disappear because of a foreign key, not because of code. `OrderErasureCoverageTests` in
`ProcuLink.Api.Tests/Architecture/` fails the build if that cascade is ever dropped, or if a new
order-tied table is added and nothing erases it.

### Kept on purpose

| What | Why |
|---|---|
| `imported_sftp_files` / `imported_s3_objects` — **tombstoned, not deleted** (`order_id` set to the terminal sentinel, row retained) | The source file still sits on the **customer's own** SFTP server or S3 bucket. ProcuLink never deletes it, and the poller lists it every cycle. Delete the ledger row and the erased order re-imports as a brand-new order. |
| The erasure receipt: one `audit_events` row, `action = admin.order.erased` (or `admin.orders.bulk_erased`), carrying the acting admin's `sub` + email and the per-table counts | GDPR Art. 5(2) accountability. It is written **after** the erase, so it is not caught by the delete, and a repeated erase cannot remove it. Without it you cannot later prove the erasure happened. |

### NOT touched at all — say this to the customer, do not let them assume otherwise

- **Everything at workspace level.** Suppliers, buyers, item mappings, validation rules, output
  templates, supplier connections and revisions, delivery configuration and its encrypted
  credentials, IMAP settings, API keys, the inbound-email address token, plan and billing history.
- **The organisation and its users.** No code path anywhere deletes an `Organisation`. There is no
  account-closure endpoint.
- **Clerk.** The backend has no Clerk SDK and no Clerk webhook. ProcuLink never deletes a Clerk user
  or organisation, and is never told when one is deleted. Sign-in identity must be removed
  separately, in the Clerk dashboard.
- **`audit_events` not keyed to the erased order.** Supplier edits, mapping changes,
  delivery-config changes and billing events remain, and they can still name the same customer.
- **Invoices and advance shipping notices.** `invoices`, `invoice_lines`,
  `advance_shipping_notices` and their R2 `source_file_key` blobs have **no erasure path at all**.
  If the customer's request covers these, this runbook does not close it — escalate.
- **Backups.** Neon's point-in-time backups still contain the rows until they age out. Nothing here
  rewrites a backup.

---

## 2. Verify the requester before you touch anything

An erasure request is unauthenticated email. Treat it as a claim, not an instruction.

1. **The request must arrive at `privacy@proculink.eu`** (published on `/privacy`). A request that
   arrives anywhere else gets redirected there first — that inbox is the record.
2. **Confirm the sender is a member of the workspace they are naming.** Do not take the org name
   from the email body. Look the sender's address up:

   ```sql
   SELECT u.id, u.email, m.org_id, o.name, o.slug
   FROM   users u
   JOIN   memberships m ON m.user_id = u.id
   JOIN   organisations o ON o.id = m.org_id
   WHERE  lower(u.email) = lower('<requester email>');
   ```

   No row means the sender is not a member of any workspace. **Stop.** Do not erase on the strength
   of a name match — reply asking them to write from the address on the account.
3. **Confirm they can authorise it.** A right-to-erasure request from an individual is not the same
   as a controller instructing deletion of workspace data. If the requester is not the workspace
   owner or an admin, get written confirmation from someone who is before proceeding.
4. **Agree the scope in writing, in the reply, before running anything.** Specifically:
   - which orders — a list of PO numbers, a PO-number prefix, or a date cut-off;
   - that workspace-level data (suppliers, mappings, credentials) is **not** included unless they
     say so, and that there is no automated path for it;
   - that Clerk sign-in identity is separate;
   - that it cannot be undone.
5. **Keep the thread.** It is your evidence of the instruction you acted on.

---

## 3. Before you run it

- [ ] Scope agreed in writing (§2.4) and pasted into the ticket.
- [ ] You are on the admin allowlist. `AdminOnlyAttribute` fails closed: an empty or unset allowlist
      authorises nobody, so a 403 usually means misconfiguration, not a bad token. The 403 is logged
      with the presented `sub`/email under `ProcuLink.Api.Auth.AdminOnlyAttribute`.
- [ ] You have the **organisation id** and the **order ids**, both as UUIDs, both verified against
      the org from §2.2.
- [ ] You have run the "before" query in §5 and saved the output. You cannot prove the erasure
      worked without a before.
- [ ] **Check the order is not mid-delivery.** Erasing an order that is `delivering` removes the row
      under a running job. Wait for it to reach a terminal state.

```bash
export API_BASE=https://api.proculink.eu
export ORG_ID=<organisation uuid>
export TOKEN=<your Clerk session JWT>
```

---

## 4. Run it

### Single order — the default. Use this unless the scope is genuinely bulk.

```bash
curl -i -X DELETE \
  "$API_BASE/api/admin/organisations/$ORG_ID/orders/$ORDER_ID" \
  -H "Authorization: Bearer $TOKEN"
```

- `200` with a JSON body of per-table counts — erased. Save the body into the ticket; it is the
  human-readable receipt.
- `404` — no such order **in that organisation**, or it was already erased. The operation is
  idempotent: re-running it is safe and does nothing. Do not "fix" a 404 by widening the scope.
- `401` — not authenticated. `403` — not on the admin allowlist.

### Bulk — only with a written, specific scope

```bash
curl -i -X POST \
  "$API_BASE/api/admin/organisations/$ORG_ID/orders/bulk-erase" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"ids":["<uuid>","<uuid>"]}'
```

Filter fields are `poNumberPrefix`, `status`, `ids`, `olderThan`; they combine with **AND**. Prefer
`ids` — it is the only filter that cannot match more than you listed.

**An empty filter `{}` is refused with `400`, deliberately, so it can never wipe the org.** A blank
string and an empty `ids` array both count as "not supplied" and are refused the same way. If you get
that 400, your filter was empty — do not add a criterion just to make the call succeed.

---

## 5. Verify it worked

Run **before and after**, and keep both. `:order_id` is the erased order; `:org_id` its organisation.

```sql
-- 1. Every order-tied table. After a successful erase every count must be 0.
SELECT 'purchase_orders'        AS t, count(*) FROM purchase_orders           WHERE id       = :order_id
UNION ALL SELECT 'lines',             count(*) FROM purchase_order_lines      WHERE order_id = :order_id
UNION ALL SELECT 'parties',           count(*) FROM order_parties             WHERE order_id = :order_id
UNION ALL SELECT 'source_captures',   count(*) FROM source_captures           WHERE order_id = :order_id
UNION ALL SELECT 'artifacts',         count(*) FROM outbound_artifacts        WHERE order_id = :order_id
UNION ALL SELECT 'delivery_attempts', count(*) FROM delivery_attempts         WHERE order_id = :order_id
UNION ALL SELECT 'exceptions',        count(*) FROM order_exceptions          WHERE order_id = :order_id
UNION ALL SELECT 'validations',       count(*) FROM order_validation_results  WHERE order_id = :order_id
UNION ALL SELECT 'passport',          count(*) FROM po_passport_events        WHERE order_id = :order_id
UNION ALL SELECT 'ai_decisions',      count(*) FROM ai_suggestion_decisions   WHERE order_id = :order_id
UNION ALL SELECT 'suggestions',       count(*) FROM order_supplier_suggestions WHERE order_id = :order_id
UNION ALL SELECT 'idempotency',       count(*) FROM idempotency_keys          WHERE order_id = :order_id
UNION ALL SELECT 'email_imports',     count(*) FROM email_import_records      WHERE order_id = :order_id
UNION ALL SELECT 'confirmations',     count(*) FROM order_confirmations       WHERE purchase_order_id = :order_id;
```

```sql
-- 2. The order's own audit rows are gone, but the RECEIPT is present.
--    Expect: erased_rows = 0, receipts >= 1. If receipts = 0 the erase ran but the
--    accountability write failed (logged at Error) — record that in the ticket.
SELECT
  count(*) FILTER (WHERE action <> 'admin.order.erased') AS erased_rows,
  count(*) FILTER (WHERE action  = 'admin.order.erased') AS receipts
FROM audit_events
WHERE entity_id = :order_id;
```

```sql
-- 3. The receipt itself: who ran it, when, and what it removed. Paste into the ticket.
SELECT created_at, action, payload
FROM   audit_events
WHERE  entity_id = :order_id AND action IN ('admin.order.erased','admin.orders.bulk_erased')
ORDER  BY created_at DESC;
```

```sql
-- 4. Pull-ingress ledgers are TOMBSTONED, not deleted. Expect 0 rows still pointing at
--    the real order id. A row here means the erased order can be resurrected by the poller.
SELECT 'sftp' AS ledger, count(*) FROM imported_sftp_files WHERE order_id = :order_id
UNION ALL
SELECT 's3',             count(*) FROM imported_s3_objects WHERE order_id = :order_id;
```

**Object storage.** R2 deletes are best-effort by design: a single failed key is logged at `Error`
and does not abort the erase, because the DB rows still have to go. So a `200` does **not** prove
every blob went. Compare the `r2ObjectsDeleted` count in the response against the number of keys the
order had (source + each artifact + each confirmation source). If it is short, search the API logs
for `failed to delete R2 key` and remove the named keys by hand.

---

## 6. What to tell the customer

Reply on the original thread. Say plainly:

- **What was erased** — name the orders (PO numbers), and say that the stored document files, the
  extracted order content, the output files sent to suppliers, and the processing and delivery
  history for those orders are gone.
- **What was kept, and why** — one record that the erasure took place (date, who ran it, what it
  covered), retained as the accountability record required of us; and workspace-level configuration
  that is not order content: suppliers, mappings, delivery settings, plan and billing records.
- **What was not included** — their sign-in account with our identity provider is separate; say so
  and offer to remove it if that is what they want. Ditto anything under §1 "NOT touched at all"
  that falls inside their request.
- **That it cannot be undone.**
- **When.** GDPR gives one month from the request, extendable by two further months for complex
  requests, with notice inside the first month. Do not promise faster in writing than you have
  already met.

Attach nothing containing another customer's data. The response body from §4 is safe — it is counts.

---

## 7. Known gaps this runbook does not close

Written down so the next operator does not rediscover them under time pressure.

1. **No account closure.** No code path deletes an `Organisation`. A customer who leaves keeps a full
   org row and everything hanging off it, indefinitely.
2. **Clerk is never notified, in either direction.** No SDK, no webhook. Deleting a Clerk user erases
   nothing here; erasing here removes nothing from Clerk.
3. **Invoices and ASNs have no erasure path**, and each carries its own R2 blob.
4. **Workspace-level erasure is manual.** Delivery credentials, IMAP passwords, API keys and the
   inbound-email token can only be removed through the product UI, one at a time.
5. **Backups are not rewritten.** Erased rows persist in Neon point-in-time backups until they age
   out.
6. **The org-wide retention sweep is off.** `DataRetentionOptions.Enabled` defaults to `false` and no
   checked-in config sets it, so `audit_events` and `po_passport_events` accumulate. Note it deletes
   **cross-tenant by age** with no dry-run mode — do not switch it on to service a single request.
