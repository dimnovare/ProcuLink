# Golden Path — Live Verification Checklist

Run this checklist against `https://proculink.eu` after deploying the Railway Worker.
The system is already live. This is a human-run smoke test, not an automated suite.
Go step by step. Do not skip steps.

---

## Sample CSV to upload

Save this as `sample-order.csv` or use the fixture already in the repo at
`ProcuLink.Api/Fixtures/sample-order.csv`:

```csv
po_number,buyer_name,line_no,item_code,description,quantity,unit_price,currency
DEMO-2026-001,Northwind Trading OÜ,1,ACME-WIDGET-A,Widget A 10mm,12,4.50,EUR
DEMO-2026-001,Northwind Trading OÜ,2,ACME-WIDGET-B,Widget B 20mm,6,8.25,EUR
DEMO-2026-001,Northwind Trading OÜ,3,ACME-BRACKET-S,Bracket short,24,1.95,EUR
```

---

## Checklist

### Step 1 — Sign in

Open `https://proculink.eu` in a browser. Sign in with a real account via Clerk.

**What to check:** You land on the Dashboard. No redirect loop. No blank screen.

> Note: On first sign-in, `TenantResolutionMiddleware` auto-provisions an org/tenant
> row in the DB. If this fails you will hit a 500 on every API call — check Railway
> API logs for `organisation` insert errors.

---

### Step 2 — Dashboard loads cleanly

Open browser DevTools → Console. Confirm:

- No uncaught JavaScript errors.
- No failed network requests (401, 500, CORS) in the Network tab.
- The Dashboard renders with real data regions (even if empty).

---

### Step 3 — Add a test supplier

Navigate to **Suppliers → Add supplier**. Fill in a name (e.g. `Test Supplier QA`).
Save. The supplier detail page opens.

**What to record:** the supplier ID shown in the URL or page — you may need it later
when configuring delivery.

---

### Step 4 — Upload a sample CSV order

Navigate to **Upload**. Upload the CSV from the "Sample CSV" section above.

**What to check:**
- The upload request returns an `orderId`.
- You are redirected to `/upload/preview/<orderId>` or `/inbox/<orderId>`.
- Note the `orderId` — you will use it in steps 6–10.

---

### Step 5 — Verify the order leaves `parsing` and reaches `pending_review`

After upload, the order status should be `parsing`. Wait up to 30 seconds.

**Expected:** status changes to `pending_review`.

**If it stays `parsing` after 2 minutes:**
- The Hangfire Worker is not consuming jobs.
- Go to Railway → `ProcuLink.Worker` service → Logs.
- Look for `ParseOrderJob` or connection errors.
- Also check `https://api.proculink.eu/hangfire` (admin-gated) for queued/failed jobs.

---

### Step 6 — Review unresolved lines

Open the review page at:

```
https://proculink.eu/upload/preview/<orderId>
```

**What to check:**
- Unresolved lines are listed. The sample CSV has 3 lines with buyer item codes
  (`ACME-WIDGET-A`, `ACME-WIDGET-B`, `ACME-BRACKET-S`) that will be unresolved
  unless a mapping already exists.
- For each unresolved line, manually enter a supplier item code (any value, e.g.
  `SUP-001`, `SUP-002`, `SUP-003`).

---

### Step 7 — Save mappings and confirm `ready` status

Click **Save mappings** (or equivalent). Confirm:

- The save request succeeds (no error toast).
- Order status updates to `ready`.

---

### Step 8 — Send to supplier and check delivery outcome

Click **Send to supplier** (or **Transform & Deliver**).

Two valid outcomes:

**A — `delivered`:** supplier delivery config exists and the HTTP call succeeded.
This is the happy path. Confirm `delivered` status on the order.

**B — `delivery_failed` with a specific error message:** this is expected if no
delivery config is set for the supplier. The error must be honest and specific —
e.g. `"No delivery configuration found for supplier. Configure it in Suppliers → Delivery."`.
A generic `"Something went wrong"` or unhandled 500 is a bug.

Both outcomes are acceptable for a soft launch. A crash or hang is not.

---

### Step 9 — Verify the audit trail

Fetch the audit events for the order. You need a valid Bearer token (copy from
the browser Network tab — any authenticated API request will have it in the
`Authorization` header).

```
GET https://api.proculink.eu/api/orders/<orderId>/audit
Authorization: Bearer <token>
```

**What to confirm:**
- Response is 200 with a JSON array.
- Array contains events for at minimum:
  - `ParseCompleted`
  - `TransformCompleted`
  - `DeliveryAttempted` or `DeliveryFailed`
- Each event has a timestamp and a description.

If the array is empty or the endpoint returns 404, the audit pipeline has a gap —
check `ParseOrderJob` and `DeliveryService` logs on Railway.

---

### Step 10 — Verify delivery attempts

```
GET https://api.proculink.eu/api/orders/<orderId>/delivery-attempts
Authorization: Bearer <token>
```

**What to confirm:**
- Response is 200 with a JSON array.
- At least one attempt row exists containing:
  - `channel` (e.g. `http`)
  - `attemptedAt` (ISO timestamp)
  - `succeeded` (bool)
  - An honest `errorMessage` if the attempt failed — not null, not empty.

A missing attempt row means the delivery dispatcher did not run. Check
`DeliveryService` and `TransformOrderJob` logs.

---

## If something fails

### Worker not consuming jobs

**Symptom:** order stays `parsing` for more than 2 minutes.

**Triage:**
1. Railway → `ProcuLink.Worker` service → Logs — look for startup errors or
   `ParseOrderJob` exceptions.
2. Check that `ConnectionStrings__DefaultConnection` is set on the Worker service
   in Railway (it must point to the same Postgres instance as the API).
3. Check that Hangfire tables exist in the DB (`HangFire.Job`, etc.) — if the
   migration did not run against the production DB, Hangfire has nowhere to write.

### R2 upload failure

**Symptom:** upload returns a 500 or the file never appears in R2.

**Triage:**
1. Railway → API service → Logs — look for `R2` or `S3` errors.
2. Confirm `Storage__R2AccessKeyId`, `Storage__R2SecretAccessKey`,
   `Storage__R2BucketName`, and `Storage__R2Endpoint` are set on the Railway API
   service environment.
3. If keys are absent, `LocalFileStorageService` is used instead — the upload may
   succeed locally but the Worker cannot find the file across the network. R2
   credentials are required in production.

### Parse never completes (stays `parsing` indefinitely)

**Symptom:** Worker is running and consuming, but status never leaves `parsing`.

**Triage:**
1. Check Railway Worker logs for `ParseOrderJob` exceptions.
2. Confirm all EF migrations ran: `dotnet ef database update` from `ProcuLink.Api`
   against the production connection string, or verify via Railway DB console that
   the `__EFMigrationsHistory` table includes the latest migration name.
3. If the DB is missing tables (e.g. `purchase_orders`), the migration did not
   apply — run it before retrying.

### Clerk auth 401 on API calls

**Symptom:** all API calls return 401; frontend shows auth errors.

**Triage:**
1. Confirm `Clerk__Authority` on the Railway API service is set to the production
   Clerk authority (not the dev `golden-alpaca-43` instance) — or that you are
   using the matching Clerk instance.
2. Confirm `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY` on Vercel matches the same Clerk
   instance as the API's `Clerk__Authority`.
3. A mismatch between dev and prod Clerk instances is the most common cause.

---

## Definition of done

The soft launch is **green** when all of the following are true:

- [ ] Sign-in works and the Dashboard renders without JS errors.
- [ ] A supplier can be added and saved successfully.
- [ ] A CSV upload succeeds and returns an `orderId`.
- [ ] The parse job completes within 30 seconds (Worker is alive and consuming).
- [ ] The review page shows unresolved lines honestly — no crash, no blank state.
- [ ] Saving mappings transitions the order to `ready`.
- [ ] Transform runs and creates an output artifact.
- [ ] Delivery either succeeds (`delivered`) OR fails with a specific, honest error
  message (`delivery_failed`) — no generic crash or hang.
- [ ] Audit trail (`/api/orders/<orderId>/audit`) returns at least 3 events.
- [ ] Delivery attempts (`/api/orders/<orderId>/delivery-attempts`) returns at
  least 1 row with an honest outcome.

If all 10 boxes are checked, the primary PO path is verified live. The system is
ready for a real buyer PO from a real supplier.
