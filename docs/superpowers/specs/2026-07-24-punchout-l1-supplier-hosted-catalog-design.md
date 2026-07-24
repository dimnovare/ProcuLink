# Design — PunchOut L1: supplier-hosted catalog browsing with cXML cart return

**Date:** 2026-07-24
**Status:** proposed — founder decision pending. SPEC ONLY, deliberately not implemented
(handover 2026-07-23 item 8: "do NOT implement").
**Origin:** founder idea 2026-07-24. Today's catalog is the "L2-catalogue-like" model
(a local product list per supplier); the question is whether ProcuLink should also
support the L1 model, where the supplier hosts the catalog and the cart comes back.
**Priority:** unscheduled. No customer has asked for it yet; the trigger would be a
supplier (distributor-style: Ingram/Also/Logicom class) that offers PunchOut but no
catalog file/feed.

## What L1 PunchOut is, against what we have

Today a supplier's catalog is **local data**: `SupplierProduct` rows per (org, supplier)
(`ProcuLink.Core/Entities/SupplierProduct.cs:14`), filled by manual CSV/XLSX import or a
scheduled pull (`SupplierCatalogSource.cs:16` — sftp/ftp/ftps/http/https/logicom). The AI
mapping suggestion path is deliberately **catalog-grounded**: a suggested supplier code
that is not a real catalog row is discarded, never surfaced (allow-list guard, pinned by
`OpenAiMappingServiceTests.cs:236,252,272`).

L1 PunchOut inverts the catalog's location. ProcuLink acts as the *procurement
application* in the cXML PunchOut protocol:

1. Operator clicks "browse the supplier's catalog" → ProcuLink POSTs a cXML
   `PunchOutSetupRequest` (`operation="create"`) to the supplier's PunchOut setup URL,
   authenticated by cXML `From`/`To`/`Sender` credentials + `SharedSecret`.
2. Supplier replies `PunchOutSetupResponse` containing a one-time `StartPage` URL.
3. The operator's browser opens the StartPage and shops **on the supplier's site**
   (supplier-hosted; ProcuLink never sees the browsing).
4. Checkout: the supplier's site form-POSTs a `PunchOutOrderMessage` (the cart, with
   the supplier's own part IDs, prices, UoMs) back to the return URL ProcuLink supplied
   in the setup request (`BrowserFormPost`).
5. ProcuLink ingests that cart as a draft order → normal Validate → Transform → Deliver,
   typically emitting a cXML `OrderRequest` that echoes the cart's
   `SupplierPartID`/`SupplierPartAuxiliaryID` back to the same supplier.

L1 = store-level entry (one "browse catalog" door per supplier). L2 (item-level search
index) is explicitly **out of scope** here — our current local-catalog import already
covers the L2-shaped need where a file/feed exists.

## What is already in-house (verified 2026-07-24)

| Piece | Where | State |
|---|---|---|
| cXML 1.2.024 emitter (OrderRequest/PurchaseOrder, DTD envelope) | `ProcuLink.Transform/Output/CxmlTransformService.cs:50,70` | live |
| cXML network identities (From/To/Sender domain+identity), cleartext JSON | `SupplierDeliveryConfig.CxmlConfigJson` (`SupplierDeliveryConfig.cs`) | live |
| cXML Sender `SharedSecret`, AES-256-GCM encrypted | `SupplierDeliveryConfig.EncryptedCxmlSharedSecret` + `CxmlCredentialResolver.cs:34` | live |
| Credential encryption envelope (v1 + nonce + tag + ciphertext) | `DeliveryEncryptionService.cs:12` | live |
| Versioned Supplier Connection bundle (draft/test/published/archived, test evidence, `ActiveRevisionId`) | `SupplierConnectionRevision.cs:21`, `SupplierConnectionService.cs:254` | live |
| Orders pin the revision they were ingested under | `PurchaseOrderEntity.ConnectionRevisionId` (`:151`), assigned at `OrderIngestionService.cs:205` | live |
| Anonymous, rate-limited, tenant-slugged inbound HTTP (HMAC: `X-ProcuLink-Timestamp`/`Nonce`/`Signature`, ±300 s skew, nonce replay cache) | `WebhookIngressController.cs:32`, `HmacWebhookVerifier.cs:14` | live |
| PunchOut in the product surface | FE only, as **vocabulary**: standards page copy ("cXML 1.2 — punchout & marketplace orders"), connectors page "SAP Ariba · cXML PunchOut" tile marked `coming_soon` (mock), help keywords | copy, no code |

No `PunchOutSetupRequest` / `PunchOutOrderMessage` handling exists anywhere (FE or BE) —
grep-verified. The cXML investment is real but strictly **outbound OrderRequest**.

## Fit into the versioned Supplier Connection model

PunchOut config is a **new bundle member on the revision**, following the existing
split of non-secret config vs encrypted secret:

- `PunchOutConfigJson` (non-secret, jsonb): setup URL, From/To/Sender identities,
  browser-session TTL. Sibling of `DeliveryConfigJson` (`SupplierConnectionRevision.cs:80`).
- Shared secret inside `CredentialsRef` (`:85`) — the revision's existing
  authenticated-encrypted credential payload — or a dedicated encrypted column mirroring
  `EncryptedCxmlSharedSecret`. Decision point D4.
- Lifecycle falls out for free: **test** = fire a real `PunchOutSetupRequest create` and
  assert a 200 + well-formed `PunchOutSetupResponse` with a resolvable StartPage URL
  (records into the existing `TestResultJson`/`TestPassed` evidence gate); **publish**
  moves `ActiveRevisionId`; **rollback** restores the prior endpoint/creds.
- A cart-originated order pins `ConnectionRevisionId` exactly as uploaded orders do at
  `OrderIngestionService.cs:205`, so "which endpoint/credentials produced this order"
  stays reproducible.
- `CatalogMode` (`:93`, `'live'` in V1) gains a value — `'punchout'` — declaring "this
  connection has no local product list; codes originate from the supplier's cart".

## Catalog / AI-suggestion implications (the honest part)

The current promise is "AI suggests **only real** supplier codes", enforced by the
catalog allow-list guard. A PunchOut supplier has **no local code list**, so:

1. **Cart-originated lines don't need the guard.** Codes arrive supplier-authored
   (`SupplierPartID`) — real by construction, better provenance than any AI suggestion.
   Mapping/AI-suggestion should be **skipped** for these lines, and their provenance
   labeled "from supplier cart", not "AI".
2. **`SupplierPartAuxiliaryID` must round-trip opaque.** It is the supplier's cart-line
   token; the outgoing `OrderRequest` must echo it byte-identical. Editing a cart line's
   code/qty in review may invalidate it at the supplier — see D2.
3. **Uploaded POs for the same supplier lose AI grounding.** If a buyer also uploads a
   PO naming that supplier, there are no `SupplierProduct` rows to ground suggestions.
   The guard must NOT be loosened — ungrounded suggestions are exactly the hallucination
   class it exists to kill. Honest options:
   - (a) suggestions simply unavailable for punchout-only suppliers (typeahead empty,
     no AI card) — truthful, cheapest;
   - (b) **shadow catalog**: upsert `SupplierProduct` rows from every returned cart, so
     coverage grows with use. Partial and staleness-prone (prices/availability drift; a
     never-carted item never appears), so rows need a provenance marker
     (`source: punchout_cart`) and the "unknown code" flag must stay honest about
     partial coverage;
   - (c) require a file/pull catalog **in addition** when the buyer wants AI mapping.
   Recommendation: (a) at launch + (b) as the follow-up; never silently (c).
4. **Validation semantics shift.** Price/UoM checks currently treat the catalog as
   ground truth; for cart orders the cart *is* the supplier's own quote — validating its
   prices against a (missing/stale) catalog is meaningless. Cart-origin orders should
   validate structure/totals only.

## Transport / auth requirements

**Outbound (setup request):** HTTPS POST of cXML over the existing emitter/DTD
infrastructure; identities + shared secret reuse the `CxmlConfigJson` /
`EncryptedCxmlSharedSecret` shapes — but on the **connection revision**, not
`SupplierDeliveryConfig`: the PunchOut setup URL is not the delivery URL, and a supplier
may have both. Operator-entered setup URLs need the same SSRF gates the catalog-source
editor already applies (`host_not_allowed`: private/loopback/link-local blocked,
credentials-in-URL rejected).

**Inbound (cart return):** a new public endpoint in the `WebhookIngressController`
family — anonymous, rate-limited, tenant-slugged — but it **cannot use HMAC**: the
`PunchOutOrderMessage` arrives as a *browser* form POST (`cxml-urlencoded` /
`cxml-base64` body field), not a signed server call. Authentication is therefore
correlation, not signature:

- Per-session **`BuyerCookie`**: unguessable (≥128-bit random), org- and
  supplier-scoped, TTL-bound (browser session length, e.g. 2 h), **single-use**. The
  return POST is only accepted if its `BuyerCookie` matches a live pending session; the
  nonce-cache pattern from `HmacWebhookVerifier` (`:26`) covers replay.
- Cart ingestion re-checks org scoping from the session row — never from the payload.
- StartPage opens in a **new tab**, not an iframe (supplier sites routinely send
  `X-Frame-Options: DENY`; Ariba-era punchout assumes top-level navigation).
- The return endpoint must be reachable from the public internet on prod
  (`api.proculink.eu`) — already true for webhook ingress.

## Effort estimate (implementation, if approved)

| Slice | Contents | Size |
|---|---|---|
| 1. Protocol core | `PunchOutSetupRequest` emit + `PunchOutSetupResponse` parse; session entity (BuyerCookie, TTL, org/supplier/revision); return endpoint + cxml-urlencoded/base64 decode; `PunchOutOrderMessage` → draft order (pins revision) | ~1.5–2 wk incl. real-Postgres tests |
| 2. Connection-model fit | `PunchOutConfigJson` + secret slot on revision (migration), test-pack leg (live setup ping), publish/rollback already free | ~0.5 wk |
| 3. FE | "Browse supplier catalog" entry, session hand-off UX, cart-arrived inbox routing, punchout config editor on the connection screen, honest AI-unavailable states (§implications 3a) | ~1–1.5 wk |
| 4. Hardening | SSRF gates, rate limits, single-use/replay tests, `SupplierPartAuxiliaryID` round-trip pin test, billing/order-limit gate on cart ingestion | ~0.5 wk |

Total ≈ **3.5–4.5 weeks** for L1 end-to-end against one real supplier sandbox.
Biggest external risk: getting a real PunchOut sandbox to test against (Ariba/Coupa
sandboxes assume you are the *network*, not a standalone procurement app; a
distributor's direct PunchOut endpoint is the realistic first target).

## Founder decision points

- **D1 — Build at all / when.** No demand signal yet. Recommendation: keep as spec
  until a concrete prospect names a PunchOut-only supplier; the connectors page already
  honestly shows the capability as `coming_soon`.
- **D2 — Cart edit policy.** Review-only (recommended: cart lines locked, qty edits
  force a re-browse) vs editable lines with the risk that the supplier rejects an order
  whose `SupplierPartAuxiliaryID` no longer matches a cart.
- **D3 — Shadow catalog** (§implications 3b): launch without (honest "no AI for this
  supplier") and add later, or in scope from day one?
- **D4 — Secret placement**: reuse the revision's `CredentialsRef` blob (one credential
  surface per revision) vs a dedicated encrypted punchout-secret column (mirrors the
  delivery-config precedent). Engineering leans `CredentialsRef`; needs a call because
  it defines the credential-rotation UX.
- **D5 — Who initiates**: v1 is operator-initiated browsing inside ProcuLink. Some
  buyers will ask for the reverse (ProcuLink as the *supplier-side* PunchOut host for
  their ERP). That is a different, much larger product; explicitly not this spec.

## Out of scope

L2 item-level index; PunchOut `edit`/`inspect` operations (re-open an existing cart);
order-confirmation round trips; acting as the supplier-side PunchOut host (D5);
any change to the existing catalog import/pull path.
