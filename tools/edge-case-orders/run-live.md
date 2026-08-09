# Edge-case order harness — generate + run live

Two-step tool for stress-testing the ProcuLink ingest pipeline (parse → normalize →
validate → exceptions) against adversarial orders, then against the founder's real POs.

- `generate.mjs` — emits adversarial CSV / cXML / UBL files into `out/` (gitignored) + `out/manifest.json`.
- `run-live.mjs` — uploads a folder of files to a running API, polls each order, scores
  actual outcome vs the manifest's `expect` hint, writes `live-results.json`.

No dependencies. Node ≥ 18 (v22 verified). **The runner contains no token** — you supply one.

---

## 1. Generate the adversarial corpus

```bash
cd tools/edge-case-orders
node generate.mjs
```

Writes ~34 files + `manifest.json` to `out/`. Re-running resets `out/`.

Each manifest entry has an `expect` field:
- `parse` — should produce a clean canonical order
- `review` — should parse but flag line(s)/order for human review (shows in `/operations/exceptions`)
- `reject` — should fail parse / be rejected (unsupported, malformed, missing required field)

> `expect` is a *hint* for scoring, not a contract. A `review`-vs-`parse` disagreement is
> useful signal about how strict the engine is (e.g. whether zero-qty or EU number
> formatting is silently accepted vs. flagged), not necessarily a bug.

---

## 2. Run it live (the proven recipe)

### 2a. Get a Clerk bearer token (prod or any Clerk-auth deploy)

Per the `project-live-format-testing` memory, the proven way to call the API as a real
user is to grab a fresh Clerk session JWT **in the browser** while signed in to the app:

1. Sign in to the ProcuLink frontend (e.g. https://app.proculink.eu) as a real user.
2. Open DevTools console and run:
   ```js
   await window.Clerk.session.getToken()
   ```
3. Copy the printed JWT. It is short-lived (~60 s by default) — grab it right before running,
   or re-grab if the run 401s partway. For a longer-lived token use a Clerk JWT template
   and `getToken({ template: "..." })`.

The endpoint is **`POST /api/orders/upload`**, `multipart/form-data`, fields:
- `file` — the order file (≤ 10 MB; allowed: `.csv .xlsx .pdf .xml .cxml .edi .x12 .txt`)
- `supplierId` — an existing supplier GUID in your org (Pilot orgs cap at 1 supplier — reuse it)

On success the response includes `orderId`; parsing runs **async on the Worker** (the API
hosts no Hangfire — the Worker process is mandatory for parse to complete).

### 2b. Run the bulk uploader

```bash
# Prod (with a browser-grabbed token)
node run-live.mjs \
  --base https://api.proculink.eu \
  --supplier <existing-supplier-guid> \
  --token "eyJhbGci..." \
  --dir ./out

# Local QA-bypass (no token needed)
#   API must run with: ASPNETCORE_ENVIRONMENT=Development PROCULINK_QA_BYPASS_AUTH=true
#   plus a 32-byte base64 Delivery__EncryptionKey, local Postgres :5435, AND the Worker running.
node run-live.mjs --base http://localhost:5223 --supplier <guid> --dir ./out
```

Env-var equivalents: `PROCULINK_API_URL`, `PROCULINK_SUPPLIER_ID`, `PROCULINK_TOKEN`.

The runner uploads each file, polls `GET /api/orders/{id}` until a terminal-ish state
(`parsed` / `needs_review` / `ready_to_deliver` / `delivered` / `delivery_failed` /
`parse_failed` / `rejected`), and prints `expect` vs `actual` with MATCH/MISMATCH.
Full report → `live-results.json` (gitignored).

### 2c. Run the founder's REAL POs

Same runner, point `--dir` at the real folder:

```bash
node run-live.mjs --base https://api.proculink.eu --supplier <guid> --token "..." \
  --dir "%USERPROFILE%\Downloads\POs"
```

The 22 PDFs exercise the live **text→LLM extraction** path (needs the prod OpenAI key set
via `appsettings.Production.json`, not Railway env — see the PDF-extraction memory). The 9
XMLs are cXML / SAP-IDoc ORDERS05 — **see the inventory below for what each is and what
the current engine does or does NOT support.**

---

## 3. Caveats / gotchas

- **Token expiry**: Clerk session tokens are ~60 s. If a long run starts 401ing, re-grab.
- **Worker mandatory**: the API does not run Hangfire. Without the Worker, orders sit in
  `parsing` forever and the poller times out (reported as `pending/unknown`).
- **Pilot supplier cap = 1**: reuse the single existing supplier GUID; don't expect to
  create one per file.
- **Rate limiting**: `/api/orders/upload` is rate-limited (`EnableRateLimiting("upload")`).
  A 34-file burst may hit 429; the runner records it as the upload status. Add a delay or
  chunk if needed.
- **xlsx on prod**: openpyxl-generated .xlsx has failed on prod .NET before — use ClosedXML
  if you add an xlsx case (this generator emits no xlsx for that reason).
- **No data exfil**: `live-results.json` and `out/` are gitignored — they can contain real
  order content.

---

## 4. Real-PO inventory (Downloads/POs) — XML classification

> Full per-file table is in the agent report. Key facts the runner needs:

**9 XML files fall into TWO families:**

| Family | Files | Schema | Engine support |
|---|---|---|---|
| **cXML 1.2** | `redacted-fixture`, `nestle 2.xml`, `new 7.xml`, `new 8.xml` | `<cXML>` OrderRequest | **Supported** by `CxmlOrderParser` |
| **SAP IDoc ORDERS05** | `new 9.xml`, `new 710.xml`, `new 11.xml`, `new 12.xml`, `new 13.xml` | `<ORDERS05>`/`<IDOC>` E1EDK*/E1EDP* | **NOT supported** — no IDoc parser; root is not `<cXML>` nor UBL `<Order>`, so the XML branch will reject these |

So uploading the 5 IDoc files today should produce a **reject / unsupported** outcome — that
is expected and is itself a finding (SAP IDoc ORDERS05 is a real inbound format Markit
receives but the engine cannot yet parse).

**22 PDFs** — real multi-language POs (DE/FR/PL/EN). One file, **`Facture redacted-fixture`,
is a French INVOICE ("Facture"), not a purchase order** — expect the Phase-4 PO-vs-invoice
classifier to flag it or the extractor to mis-handle it; it should NOT be treated as a
deliverable PO. **`redacted-fixture` and `orders-4300267706 (1).pdf` are byte-identical
duplicates** (both 61003 bytes) — useful for an idempotency / dedupe check.
