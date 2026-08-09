# New setup-guide walkthrough-video scripts (2026-07-05)

Shot lists + narration beats for **six** high-value NEW help articles that don't yet
carry a `videoUrl`. These match the existing per-tool video pipeline
(`project-proculink/scripts/demo-video/tools/`) in structure, length, voice and
honesty rules. **Scripts only — nothing is recorded here.** Produce them with the
existing pipeline (see the last section) after founder sign-off, then wire the
`videoUrl`/`videoPosterUrl` onto the one mapped article in
`src/lib/help-articles.ts`.

## Why these six

All ten shipped tool videos (`upload`, `review`, `dashboard`, `inbox`, `suppliers`,
`po-mapping`, `delivery`, `connections`, `exceptions`, `settings-integrations`) cover
the *core* flow. The newer, deeper setup guides added since — the credential-heavy and
schema-heavy ones — have no video. These six are the ones where **seeing the exact
screen and which field goes where** removes the most friction:

| # | Video id | Article slug (carries `videoUrl`) | Why it's video-worthy |
|---|----------|-----------------------------------|------------------------|
| 1 | `cxml-setup` | `cxml-setup` | Six credential fields (From/To/Sender domain+identity + write-only secret) that people mis-fill constantly. |
| 2 | `oauth2-delivery-setup` | `oauth2-delivery-setup` | Eight-field token exchange; `authStyle`/`requestStyle`/`tokenPath` are opaque in text, obvious on screen. |
| 3 | `sftp-ftps-delivery-keys` | `sftp-ftps-delivery-keys` | Key-format conversion (.ppk → OpenSSH), leave-blank-to-keep, cert toggle — all visual "which field" moments. |
| 4 | `sftp-polling-setup` | `sftp-polling-setup` | The silent-empty-folder failure is much clearer when you watch a wrong path return nothing. |
| 5 | `api-order-schema-reference` | `api-order-schema-reference` | Copy the endpoint + key from the app, then the JSON body shape — a two-surface flow that reads better in motion. |
| 6 | `csv-xlsx-field-guide` | `csv-xlsx-field-guide` | Header auto-detect + decimal-separator behaviour is a "watch it happen" story. |

Target length for each: **~60–90 s** (matches the shipped library: `delivery` 1:41,
`po-mapping` 1:34, `dashboard` 1:24). VO first, then capture; each beat holds exactly
as long as its narration (+~1.1 s pad).

**Format of each spec below** mirrors `tools/<id>.json`: an ordered `beats` array where
each beat has a `shot` (what's on camera / the action) and a `vo` (narration). Author
the real `tools/<id>.json` from these, plus a `capture-<id>.spec.ts`. Say acronyms
letter-spaced in the VO copy the way the shipped specs do (e.g. "H T T P", "S F T P",
"O Auth 2", "A P I") so ElevenLabs reads them as letters.

---

## Honesty guardrails that apply to ALL six (founder rules)

- **Real UI only.** The founder rejected card-based / fabricated cuts — every claim must
  be on the real screen when it's spoken (offer ⇔ works). No synthetic drift, real motion.
- **Test-fire caveat, verbatim intent, every time it appears:** a green test proves *the
  endpoint answered / the file was written / the token was minted* — **not** that the
  supplier accepted the order. ProcuLink never conflates HTTP 200 (or a delivered file)
  with business acceptance. Keep this line in cXML, OAuth2, and SFTP/FTPS delivery scripts.
- **Write-only secrets** (shared secret, client secret, SFTP key/password): say they are
  encrypted at rest and never shown back; "leave blank to keep" on edit.
- **Plain language** — no internal jargon (revisions/spine/crossings) in the narration.
- **Mock-capture reality:** the delivery tab's `getDeliveryConfig`/`test-fire` have no mock
  twins (they error / no-op in mock). In the recording the fields are typed and rested on;
  the **Send a test now** button is rested on, **not clicked** (it would error on camera).
  The VO narrates the test *action and its meaning* without claiming an on-screen green
  result. Same discipline the shipped `delivery.mp4` used. See produce notes.

---

## 1 — `cxml-setup.mp4` → article `cxml-setup`

**Route:** `/library/suppliers/{id}?tab=delivery` (a supplier profile, Delivery tab).
**Target:** ~80 s.

| Beat | Shot |
|------|------|
| b1-open | Land on the supplier's Delivery tab. Cursor walks the protocol/output rail; the Output format select is opened and **cXML** is picked. |
| b2-parties | The three credential blocks appear (From / To / Sender). Cursor rests on **From**, then **To**, then **Sender**, tracing the domain + identity pair on each. |
| b3-from-to | Cursor fills **From** — domain (e.g. `DUNS`) then identity — and **To** — domain (e.g. `NetworkID`) then identity. Sample values only. |
| b4-secret | Cursor fills **Sender** (domain + identity), then the **Sender shared secret** field; a "write-only · encrypted · leave blank to keep" hint sits under it. |
| b5-test | Cursor moves to **Send a test now** and rests on it (not clicked in capture). |
| b6-outcome | Rest on the result/status area; the honest "endpoint answered ≠ accepted" framing. |

**Narration beats:**
- **b1** cXML is the format supplier networks like Coupa, Ariba and Jaggaer speak. On this supplier's Delivery tab, pick cXML as the output format, and ProcuLink will convert every order for them into cXML.
- **b2** cXML proves who you are with three party identities in the message header — From, To, and Sender. Each is a domain, which names the identifier scheme, plus an identity, the value inside it.
- **b3** From is usually you, the buyer. To is the supplier or network. The supplier gives you these exact values — a Network I D with its matching domain. A mismatched domain is the single most common reason an endpoint rejects the credential, so match both exactly.
- **b4** Sender is the authenticated party, often the same as From, and it carries the shared secret the network issued you. That secret is write-only. Once saved it's encrypted at rest and never shown again — leave it blank when you edit to keep the stored one.
- **b5** Before any real order rides this route, send a test. ProcuLink posts a real cXML payload to the endpoint and shows you the verbatim result.
- **b6** And be precise about what green means. The endpoint answered. That is not the same as the supplier accepting the order — a cXML network returns its own acceptance status, and an order can still be rejected downstream. ProcuLink never pretends otherwise.

---

## 2 — `oauth2-delivery-setup.mp4` → article `oauth2-delivery-setup`

**Route:** `/library/suppliers/{id}?tab=delivery`, protocol **HTTP**, auth **OAuth2 fetch-token**.
**Target:** ~85 s.

| Beat | Shot |
|------|------|
| b1-open | Delivery tab with **HTTP** selected. Cursor opens the Auth type select and picks **OAuth2 fetch-token**; the token fields reveal. |
| b2-token | Cursor fills **tokenUrl**, then **clientId**, then **clientSecret** (hint: encrypted at rest). |
| b3-scope-grant | Cursor fills **scope** (`orders.write`) and confirms **grantType** = `client_credentials`. |
| b4-styles | Cursor toggles **authStyle** (`basic-header` vs `body`) and **requestStyle** (`form` vs `json`), then rests on **tokenPath** (`access_token`). |
| b5-flow | Rest on the webhook URL field above; brief note that the token is fetched fresh per send and attached as a bearer to the order POST. |
| b6-test | Cursor rests on **Send a test now** (not clicked); the two-step green (token minted, then webhook answered) explained, with the acceptance caveat. |

**Narration beats:**
- **b1** Some supplier A P Is won't take a static key — they want you to trade a client id and secret for a short-lived access token first. On the H T T P delivery channel, pick the O Auth 2 fetch-token method and these fields appear.
- **b2** Token U R L is where the supplier mints the token. Client id and client secret are the credentials they issued you. The secret is encrypted at rest and never shown back.
- **b3** Scope is the space-separated permissions the token should carry — optional if they don't require one. Grant type for machine-to-machine delivery is almost always client credentials.
- **b4** Two fields decide the wire format. Auth style is how your id and secret reach the token endpoint — a basic header, or fields in the body. Request style is how that request is encoded — form or J S O N. Their docs tell you which. Token path is which property in the response holds the token; the standard is access underscore token.
- **b5** Before every delivery, ProcuLink calls the token U R L, reads the token out, and attaches it as a bearer on the actual order POST. It's fetched fresh each send, so short expiry windows are never a problem.
- **b6** Test it, and read the result carefully. Green means two things happened — the token endpoint returned a token, and the supplier's webhook answered. It still does not mean the supplier accepted the order. An H T T P 200 proves the connection; the contents can be rejected later, which is why ProcuLink tracks that as a separate stage.

---

## 3 — `sftp-ftps-delivery-keys.mp4` → article `sftp-ftps-delivery-keys`

**Route:** `/library/suppliers/{id}?tab=delivery`, protocol **SFTP** (then a glance at **FTPS**).
**Target:** ~85 s.

| Beat | Shot |
|------|------|
| b1-open | Delivery tab, **SFTP** selected. Cursor rests on host / port / username. |
| b2-authmode | Cursor toggles the auth mode between **Password** and **Private key**; password path shown first (encrypted-at-rest hint). |
| b3-key | Switch to **Private key**; cursor rests on the key field. A lower-third or in-frame note shows an OpenSSH `-----BEGIN OPENSSH PRIVATE KEY-----` header vs a rejected `PuTTY-User-Key-File-` header. |
| b4-convert | Brief callout of the PuTTYgen path (Load .ppk → Conversions → Export OpenSSH key → paste). Static in-frame note card, no external app on camera. |
| b5-dir-cert | Cursor rests on the remote **directory** field (auto-create vs must-pre-exist), then switches protocol to **FTPS** and rests on the **certificate validation** toggle (leave on). |
| b6-blank-test | Re-open shows the **"leave blank to keep"** secret hint; cursor rests on **Send a test now** (not clicked) with the delivered-file ≠ accepted caveat. |

**Narration beats:**
- **b1** When a supplier takes orders as files instead of over an A P I, ProcuLink drops the transformed order over S F T P or F T P S. Give it the host, port, and username.
- **b2** Then choose how to authenticate. Password is the simplest — it's encrypted at rest. Private key is more secure and common for S F T P: the supplier registers your public key, and you paste the matching private key here.
- **b3** ProcuLink expects an OpenSSH-format key — one that begins BEGIN OPENSSH PRIVATE KEY. A key that starts PuTTY-User-Key-File is the un-converted dot p p k format, and it will not authenticate.
- **b4** If that's what you have, convert it once with PuTTYgen — load the dot p p k, export an OpenSSH key, and paste the full contents, header lines and all.
- **b5** Set the remote directory the file lands in — depending on the server's permissions it's either created for you or has to already exist, so when in doubt ask the supplier to pre-create the exact path. On F T P S, leave certificate validation on; only turn it off for a confirmed self-signed host.
- **b6** Re-open a saved config and the secret shows "leave blank to keep" — secrets are write-only, never shown back, and saving blank keeps the old one. Then test. A green test proves the login, path, and transport — but a delivered file is not a supplier-accepted order. Their system can still reject the contents downstream.

---

## 4 — `sftp-polling-setup.mp4` → article `sftp-polling-setup`

**Route:** `/settings?tab=sftp` (SFTP pull).
**Target:** ~70 s.

| Beat | Shot |
|------|------|
| b1-open | Land on **Settings → SFTP pull**. Cursor settles on the form. |
| b2-conn | Cursor fills **Host** (`sftp.supplier.example`), **Port** (`22`), then the **Path / folder** (`/exports/orders`) — a beat of emphasis on the path field. |
| b3-creds | Cursor picks username+password or username+private key (OpenSSH); encrypted-at-rest hint. |
| b4-schedule | Cursor sets the **polling schedule** (slowest cadence that meets turnaround) and picks the **default supplier** imported files are attributed to. |
| b5-processed | Rest on / brief note: ProcuLink records what it imported and never deletes or moves your files — the source folder is left as-is. |
| b6-silent | The honest failure: a wrong path connects fine and returns an **empty list** — no error, no orders. Cursor rests on the path field again. |

**Narration beats:**
- **b1** When a system exports purchase orders to an S F T P server on a schedule, ProcuLink can log in, pick up new files, and parse them. You set this up yourself under Settings, S F T P pull — it's on every paid plan.
- **b2** Give it the host, the port — 22 unless it was moved — and the absolute path to the folder. That path is the single most common thing to get wrong.
- **b3** Then credentials — a username and password, or a username and a private key in OpenSSH format. Either way they're encrypted at rest.
- **b4** Choose a polling schedule — the slowest cadence that still meets your turnaround, since there's no benefit to checking faster than files arrive. And pick a default supplier, because a raw file drop carries no routing of its own.
- **b5** Each poll imports files it hasn't seen and records them, so nothing is re-imported. ProcuLink never deletes or moves your files — the source folder is left exactly as the sender left it.
- **b6** One thing to watch: if the path is wrong, ProcuLink connects successfully and simply finds an empty list. No error, no orders. If a schedule's been running and nothing appears, re-check the exact path against where the source actually writes. A successful connection means it reached the server — it doesn't guarantee any file parses.

---

## 5 — `api-order-schema-reference.mp4` → article `api-order-schema-reference`

**Route:** `/settings?tab=api` (API keys + ingress endpoint block), then a code overlay for the body.
**Target:** ~80 s.

| Beat | Shot |
|------|------|
| b1-keys | Land on **Settings → API keys**. Cursor creates / reveals a `plk_` key (one-time reveal), then rests on the read-only **ingress endpoint** row (`POST …/api/ingress/{slug}/orders`). Copy affordance shown. |
| b2-header | Overlay/callout: the `X-ProcuLink-Key: plk_…` header + `Content-Type: application/json`. Note the key is a bearer credential — keep it secret. |
| b3-ping | Brief note: confirm key + slug with a `GET …/ping` before sending a real order. |
| b4-body | Code overlay of the JSON body — `orderNumber`, `orderDate`, `currency`, `supplierId` (GUID or exact name), and a `lines` array. Highlight `supplierId` + at least one line = required; numbers are numbers, not strings. |
| b5-response | Code overlay of the success response (`id`, `status: needs_review`, `linesCount`) + a note that a 2xx means stored, not supplier-accepted. |
| b6-dedupe | Brief `Idempotency-Key` note (Zapier/Make deliver at-least-once; a repeat within 24 h returns the original). |

**Narration beats:**
- **b1** When another system already holds an order as structured data, push it straight into ProcuLink over H T T P S. Create a P L K key here, and copy your ingress endpoint — it's scoped to your workspace slug.
- **b2** Send a POST to that U R L with your key in the X-ProcuLink-Key header and a J S O N body. The key is a bearer credential, so keep it secret.
- **b3** Before the first real order, confirm the key and slug work with a GET to slash ping — it returns a small OK payload.
- **b4** The body is order header fields plus a lines array. Supplier id — a supplier's GUID or its exact name — and at least one line are required; the rest are optional but recommended. Quantity and unit price are numbers, not strings, and the date is a plain I S O date.
- **b5** On success you get back the created order id, its status — needs review — and the line count. A 2xx means ProcuLink stored the order, not that a supplier accepted it. It still flows through review and delivery like any other.
- **b6** One more field worth setting: an idempotency key. Zapier and Make deliver at least once, so send a stable key per order and a repeat within a day returns the original instead of a duplicate.

*(This one uses code overlays for the body/response — the same technique the shipped
walkthrough used for the triptych/XML shots. The endpoint + key beats are real UI.)*

---

## 6 — `csv-xlsx-field-guide.mp4` → article `csv-xlsx-field-guide`

**Route:** `/upload` (real drop), continuing into the parsed review; the field rules ride
on code overlays of a sample CSV.
**Target:** ~80 s.

| Beat | Shot |
|------|------|
| b1-open | Land on `/upload`. Cursor traces the accepted-format line; a real CSV is dropped → "Detected: CSV". |
| b2-required | Code overlay of a sample CSV header row; highlight that only a line with a **quantity** is truly required, and header fields (PO number, date, buyer, currency) only need a value on row 1. |
| b3-headers | Overlay showing header aliases resolving — `Buyer Item Code`, `buyerItemCode`, `BUYER_ITEM_CODE` all map to one field; unmatched columns are ignored (headers matched by name, not position). |
| b4-encoding | Note UTF-8 decode, BOM stripped, CRLF/LF normalised; a legacy CP1252 file garbles accents/€ — save as "CSV UTF-8". |
| b5-decimals | The number rule: last separator wins (`1,234.56` and `1.234,56` both read the same); a **semicolon-delimited** CSV is the reliable European signal (`73,22` = seventy-three point two two). Ambiguous numbers go to review, never a silent wrong value. |
| b6-review | Cut to the parsed order opening for review — lines + inferred line numbers; XLSX is input-only (read from the first worksheet, delivered back out as CSV/XML/cXML/UBL/X12/JSON). |

**Narration beats:**
- **b1** C S V and X L S X are the two most common ways a buyer hands over an order, and both parse the same way — row one is the header, every row after is a line, and headers are matched by name, not by position.
- **b2** Only one thing is truly required: at least one line with a quantity. Everything else has a sensible default or is left blank for review. Header fields like P O number, date, buyer and currency only need a value on the first row.
- **b3** You don't have to rename your columns. ProcuLink lowercases each header and strips spaces and punctuation, so Buyer Item Code, buyerItemCode and the underscored version all resolve to the same field. Any column it doesn't recognise is simply ignored.
- **b4** It decodes as UTF-8 and quietly strips a byte-order mark and normalises line endings. A file saved in a legacy Windows codepage can garble accents and the euro sign — so choose "C S V UTF-8" when you save.
- **b5** Numbers are read locale-aware. When both a dot and a comma appear, the last one is the decimal separator, so one-two-three-four point five-six reads the same either way. A semicolon-delimited C S V is the reliable European signal. And if a number is ambiguous, ProcuLink refuses to guess — it flags that line for review instead of shipping a silently wrong value.
- **b6** Then the parsed order opens for review, line by line. X L S X follows the same shape from the first worksheet — it's an input format only; ProcuLink delivers back out in whatever format your supplier requires.

---

## How to produce these (pipeline summary)

Full detail: `project-proculink/scripts/demo-video/tools/PRODUCTION.md` and
`HELP-INTEGRATION.md`. Run from the **frontend** repo root
(`%USERPROFILE%\source\repos\project-proculink`) with `ffmpeg`, `ffprobe` and
ImageMagick (`magick`) on PATH and Playwright browsers installed.

**Founder constraint (non-negotiable):** real-UI screen capture only. The founder
**rejected the card-based / fabricated cut** as worse than real footage — no synthetic
drift, no invented cards standing in for screens. Every spoken claim must be visible on
the real screen when it's said (offer ⇔ works).

Per video:

1. **Author the spec.** Write `scripts/demo-video/tools/<id>.json` from the shot list +
   narration above (one file = beats + VO, the source of truth), plus a
   `capture-<id>.spec.ts` that drives the route and beats. Model both on the shipped
   `delivery.json` / `capture-delivery.spec.ts`.
2. **Voiceover first.** `bun run demo:tools:vo <id>` → ElevenLabs TTS (voice **Daniel**,
   `onwK4e9ZLuTAKqWW03F9`, `eleven_multilingual_v2`) → `out/<id>/vo/*.mp3` + `manifest.json`.
   Key is read from `~/.proculink-secrets/elevenlabs.key` — never commit or print it. Use the
   calm settings the recent cuts used (`ELEVENLABS_STABILITY≈0.6`, `SPEED≈1.04`).
3. **Capture.** `bun run demo:tools:capture capture-<id>` — Playwright drives the **real
   frontend in MOCK mode on :8090** (`NEXT_PUBLIC_USE_MOCK=true`,
   `PROCULINK_QA_BYPASS_AUTH=true`, **placeholder Clerk keys** `pk_test_ci_placeholder_not_real`;
   without a key @clerk/nextjs paints keyless popups over the UI — fatal). 1080p, visible
   demo cursor, one page load per take (mock state resets on reload — all nav after the first
   `goto` is client-side). Each beat holds exactly its VO duration (+~1.1 s pad).
4. **Assemble.** `bun run demo:tools:assemble <id>` → brand intro card (names the tool) →
   footage with VO synced to beat markers + low music bed (`assets/music.mp3` at ~0.14) →
   brand outro card → `out/<id>.mp4` + `<id>-poster.jpg` + `<id>.srt`.
5. **Verify (this environment can't play video).**
   `node scripts/demo-video/tools/verify-tool.mjs <id>` — ffprobe stream/format
   (1080p30 h264+aac), decode-error count (target 0), `volumedetect` (mean ≈ −22 dB), and one
   still per beat under `out/<id>/check/` for a frame-by-frame eyeball. Confirm no "Loading…"
   spinner lands on any cut.
6. **Host.** Upload to the **public** R2 bucket only —
   `wrangler r2 object put proculink-public/marketing/tools/<id>.mp4 --file … --content-type video/mp4 --remote`
   (+ `<id>-poster.jpg`). HEAD-check each URL 200 `video/mp4` at
   `https://assets.proculink.eu/marketing/tools/<id>.mp4`. **Never** the private `proculink`
   order-data bucket.
7. **Wire into Help.** Set `videoUrl`/`videoPosterUrl` on the ONE mapped article in
   `src/lib/help-articles.ts` (use the existing `${TOOL_VIDEO_BASE}/<id>.mp4` pattern) —
   **only after** the mp4 is uploaded + verified (registry field is the single switch;
   nothing dead renders). `HelpArticleShell` renders the `<video>` automatically; no MDX edit.
   If the screen's `SECTION_GUIDES` entry doesn't already reference the slug, add it.

### Mock-capture gotchas that shape these six specifically

- **Delivery-tab scripts (cXML, OAuth2, SFTP/FTPS):** `getDeliveryConfig` / `test-fire` have
  no mock twins — the initial GET errors and test-fire would error on camera. Hide the
  "Failed to fetch" banner via the capture-scoped `hideTexts` hider, **type into fields and
  rest on** the **Send a test now** button (do **not** click it), and let the VO narrate the
  test *action + meaning* without claiming an on-screen green. This is exactly how the shipped
  `delivery.mp4` handled it.
- **Native `<select>` popups don't render in headless capture** — use `selectOption` for
  output-format / auth-type / auth-mode pickers (the value + any dependent hint change on
  camera) instead of an open dropdown.
- **Settings-tab scripts (SFTP polling, API keys):** deep-link the tab (`/settings?tab=sftp`,
  `/settings?tab=api`); the "Loading members…" spinner on other tabs isn't in frame here.
  Mock ingress/push URLs may show the dev host — cosmetically prod-ify to
  `https://api.proculink.eu` (path + capability are real), as prior cuts did.
- **Code-overlay beats** (API body/response, sample CSV) are the honest way to show schema
  the mock UI can't — same technique the shipped walkthrough used for the XML triptych. Keep
  sample identities obviously fake.
- After regenerating any VO, **re-capture** — ElevenLabs durations vary and the capture reads
  the manifest at run time so holds match.
