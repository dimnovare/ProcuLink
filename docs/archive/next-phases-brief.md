# ProcuLink — Next Phases Brief

_Generated: 2026-05-28. Based on STATUS.md, production-hardening-roadmap.md, standards-matrix.md, canonical-po-model.md._

---

## 1. Immediate — This Week (Make It Shippable To Customer #1)

**Goal:** One buyer/procurement team can sign up, upload a real PO, map it to one supplier, and receive a confirmed delivery. That cycle must work end-to-end against live services, not mocks.

### Five things to do right now

**1. Set Railway + Vercel env vars and verify `/health`.**
The entire deployment is blocked on 17 environment variables listed in `STATUS.md` (Group J section). Until `ASPNETCORE_ENVIRONMENT=Production`, `ConnectionStrings__DefaultConnection`, `Clerk__Authority`, `Stripe__SecretKey + WebhookSecret + PriceIds`, `Storage__R2*`, `Delivery__EncryptionKey`, and the Vercel counterparts are set, nothing works in production. Start here — no other Group J item is possible without this.

**2. Merge `feat/group-k-standards` and run 78 tests clean.**
The branch adds `CxmlOrderParser`, `CxmlTransformService`, `OutputFormat.CXml`, and 18 new tests. It is already written. Leaving it unmerged creates a drift risk and means `docs/standards-matrix.md` and `docs/canonical-po-model.md` exist in the repo but their backing code is on a dangling branch. Merge it before any live QA.

**3. Complete the 11-item Group J live QA checklist.**
In order: Clerk login → upload → parse → `pending_review` → mapping resolve → transform → artifact download → HTTP delivery test-fire to webhook.site. Each checkpoint maps to a real route (`/upload`, `/inbox`, `/inbox/[orderId]`, `/library/suppliers/[id]` Delivery tab). Do not move to billing QA until the core PO flow is confirmed live.

**4. Wire and verify Stripe Checkout for the Growth plan.**
`BillingController` already handles Checkout, Portal, and webhook; the Stripe price IDs just need to be set. Run a full test-card cycle: Checkout → webhook → org plan updated to `active/growth` → order-limit enforcement on upload. An expired Pilot that never upgrades is the most likely failure path for customer #1.

**5. Complete the Group L onboarding checklist.**
`/settings` has billing, email, and connector tabs — but there is no guided first-run path. Add an in-app onboarding checklist component (5 steps: add supplier → upload PO → map items → transform → deliver) that surfaces on `/bridge` when a new org has zero completed orders. This is the single highest-leverage Group L item before first customer contact. Pages that need copy: the landing page hero (`/`) and pricing page (`/pricing`) — replace the abstract "bridge" metaphor with concrete metrics: "reduce manual re-keying by 80%", "first order delivered in under 5 minutes".

---

## 2. Short-Term — Weeks 2–4 (Groups M–N: Revenue And Retention Drivers)

### Group M — Supplier-side reliability and feedback loop

The buyer's procurement team uses ProcuLink because their suppliers keep rejecting orders. The product must close that loop.

- **Delivery failure replay**: When `delivery_failed` lands in `purchase_orders`, the operations log (`/operations/log`) shows it, but there is no one-click retry. Add `POST /api/orders/{id}/redeliver` → re-enqueues `DeliverOrderJob`. Visible at `OrderStatusConstants.delivery_failed` in `DeliveryService`. Estimated backend effort: 2 hours.
- **Supplier rejection notes**: Allow a delivery config to capture the supplier's rejection reason and attach it to the failed `DeliveryAttempt`. Surface this in the supplier dock's delivery tab. Without this, buyers cannot learn which field caused the rejection.
- **SFTP dispatcher**: Currently deferred from D2. Three of the most common supplier integrations in Baltic/Nordic procurement use SFTP drops. Implementing `SftpDeliveryDispatcher` behind the existing `IDeliveryDispatcher` interface is ~4 hours of backend work. Add it as the second delivery protocol in `DeliveryConfigEditor`.
- **Auto-learn mapping**: When a buyer manually resolves an AI suggestion (`AiSuggestedSupplierItemCode → SupplierItemCode`), the resolution should be persisted back to `item_mappings` automatically via `ItemMappingService`. This is architecturally expected (`STATUS.md` Group E: "stores suggestions… Manual line resolution clears suggestion metadata and persists confirmed mappings when requested") but the "when requested" path needs a confirmed end-to-end test.

### Group N — JSON/API output + webhook delivery

`standards-matrix.md` flags this as priority #1: "low effort; unblocks webhook delivery of canonical JSON to suppliers". Add `JsonTransformService` and `OutputFormat.Json`, then wire it to `DeliveryService` so HTTP delivery endpoints can push JSON payloads directly. This unlocks the entire class of supplier integrations that prefer REST callbacks over file transfer — the fastest-growing segment. Estimated effort: 1 day backend + half-day frontend (`DeliveryConfigEditor` output-format selector).

---

## 3. Medium-Term — Month 2 (Groups O–P: Defensibility)

### Group O — UBL 2.1 / Peppol BIS Order 3

This is the moat. Peppol is mandatory for public-sector procurement in Estonia, Finland, and most EU markets. No competitor in the Baltic SMB space has clean Peppol input + output behind a self-serve SaaS interface. `standards-matrix.md` already scopes it: `urn:oasis:names:specification:ubl:schema:xsd:Order-2`, evaluate `UBL.NET` NuGet. Gate it under `BillingFeature.Peppol` at the Integration tier (€999/mo). One live Peppol customer justifies the entire tier.

### Group P — Multi-org and workspace separation

The current model is single-org-per-Clerk-user. The ICP (procurement teams) typically have: one org admin, 2-5 operators, and potentially multiple buyer entities. Add:
- Clerk organization roles surfaced in the app (`admin` vs `operator`).
- Operator-scoped access: can upload and map but cannot change delivery credentials or billing.
- This is a retention multiplier — once a second user is added, churn requires buy-in from multiple stakeholders.

---

## 4. Key Risks

**Technical debt that could break traction:**
- The IMAP polling job (`EmailPollingJob`, Hangfire every 5 minutes) has never run against a real mailbox. A misconfigured IMAP host silently fails. Before selling the email-ingestion feature (Integration tier), run it against a Proton/Gmail app-password mailbox and confirm `MarkAsSeen` dedupe works across restarts.
- `PdfOrderParser` uses regex-based line extraction (`PdfPig`). Real-world buyer PDFs are diverse. A PDF that parses to zero lines will create an order stuck at `pending_review` with no actionable error message for the user. Add a `"no lines detected"` user-facing error on `OrderDetailPage`.
- The Hangfire Worker runs as a separate Railway service. If it crashes silently, orders get stuck at `parsing` or `transforming` indefinitely. Add a dead-letter/stuck-order alert: query for orders in terminal-processing states older than 10 minutes and surface them on `/operations/log`.

**Missing feature UX gaps:**
- No "why did my order fail to parse?" screen. `OrderDetailPage` shows status but not the parse error body. Buyers will email support instead of self-serving.
- The bridge dashboard (`/bridge`) has no "complete order count this month" KPI. Buyers need to see their usage vs plan limit before they hit the 429 wall on upload.
- Mapping is per-item-code. If a buyer uses 200 unique item codes, the first order requires 200 manual resolutions. The AI suggestion flow helps, but there is no bulk-accept button. This is the #1 onboarding friction point.

---

## 5. Revenue Milestones By Pricing Tier

| Tier | What the product must do to justify it |
|---|---|
| **Pilot (€0, 14 days)** | One complete upload → map → transform → deliver cycle works live. Buyer sees output file or confirmed webhook delivery. Nothing else matters. |
| **Growth (€149/mo)** | 150 orders/month is credible only if the mapping layer is fast: AI suggestions cover ≥70% of new item codes on first upload, and confirmed mappings auto-learn so repeat orders require zero manual resolution. Also: PDF ingestion must work reliably (not just for demo PDFs). |
| **Operations (€399/mo)** | At 500 orders/month the buyer has multiple suppliers. The product must support ≥5 supplier configs simultaneously with different output formats (CSV, XML, JSON) and delivery channels (HTTP, SFTP). Retry-on-failure and delivery audit trail are expected. |
| **Integration (€999/mo)** | The buyer sends orders to a supplier that requires cXML or Peppol BIS Order 3. Email polling (IMAP) replaces a manual upload step. IMAP dedupe must be solid. This tier is sold to a procurement manager, not a developer — the UI must be self-sufficient. |
| **Enterprise (from €2,500/mo)** | Multiple buyer entities, custom ERP connector configuration, SLA-backed support, and likely an onboarding call. Requires multi-org roles (Group P) and at least one live ERP customer reference (Erply or Directo). |

---

_Under 800 words (content); references: STATUS.md Group J env-var table, standards-matrix.md Next Implementation Priorities, canonical-po-model.md entity layer, production-hardening-roadmap.md Group L tasks._
