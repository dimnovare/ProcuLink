# ProcuLink — full design + functionality review (2026-06-18)

Parallel review of the whole app: **14 surface/lens agents** (workshop-v3, classic screen, dashboard,
upload, connections, lists, settings/billing, onboarding, a11y, offer⇔works, design-system, engine,
security, masterplan) → **103 findings** → adversarial verify on every critical/high → **17 confirmed
critical/high**. The top 11 (deduped) are **SHIPPED** (BE `8749141`, FE `00ecf17`); the medium/low
backlog below is durable follow-up.

## Top fixes — SHIPPED ✅

| # | Sev | Area | Fix | Status |
|---|-----|------|-----|--------|
| 1 | High | security | HttpDeliveryDispatcher header CR/LF injection → `HttpHeaderGuard` | ✅ |
| 2 | Critical | security | ErplyConnector + HttpAuthApplier (+FireIntegrationTriggerJob) header injection → guard + tests | ✅ |
| 3 | High | functionality | DeliveryService.RetryDeliveryAsync double-dispatch race → org-scoped atomic claim + per-order Hangfire mutex + Postgres test | ✅ |
| 4 | High | offer⇔works | ConnectionDetail "Make live" enabled when tests failed → disable until pass | ✅ |
| 5 | High | honesty | Annual pricing offered but yearly Stripe IDs empty → gate annual behind `NEXT_PUBLIC_ANNUAL_BILLING_ENABLED` (default OFF) | ✅ |
| 6 | High | offer⇔works | ASN upload always-501 → "coming soon" empty state | ✅ |
| 7 | High | a11y | ConfirmDialog focus-trap (no deps, document-level) → deps + dialog-scoped + fresh-value ref | ✅ |
| 8 | High | a11y | Disabled Send buttons fail contrast (#A9CDAD/#96C69C) → #5A7660 (~5.6:1) + cursor + 44px back-button | ✅ |
| 9 | High | a11y | 11 sub-44px tap targets across settings/topbar/sidebar/designer/catalog/supplier-dock → ≥44×44 via `--tap-min` | ✅ |
| 10 | High | design | Off-palette danger borders (#F0D2D2/#F5C6CB) → `--danger-soft`/`--danger` (SpineReview/SupplierResponsePanel/ConfirmDialog/ConnectionDetail) | ✅ |
| 11 | (refuted) | design | Times-New-Roman in ContextStage is a deliberate document-facsimile, not a code preview — NO change | n/a |

**Refuted by adversarial verify (NOT real):** wire-anchor collision (already fixed), SpineReview Save/Send-label,
dashboard LaneDrawer mock-leak (low), buyers/upload/api-client mock-leak (NODE_ENV-gated),
Erply/Directo/Ftps SSRF (guarded at factory/connect), demo-booking env-gate (intentional).

## Medium / low backlog (durable)

**a11y** — color-only status dots need aria-label (BridgeTopbar:277, health/delivery chips); icon-only buttons
missing aria-label (ops pages); inline select/input without `<label htmlFor>` in OutputStructureDesigner;
single `<h1>` per page; desktop Send: add `!validation.isStale` to match mobile/confirm.

**functionality / honesty** — ReplayPanel: clamp limit 1–50 + distinct 4xx messages; BundleSummary "Not configured"
should read muted/warning; Dashboard exception KPI mixes all-time vs windowed (label/scope it) + CSV 100-row
cap warning on real data; Upload: `humanList` in error copy + clarify "Extracting" + drag-over rejection feedback;
Inbox/Exceptions need pagination controls; Exceptions disabled "Open order" why-tooltip; Invoices download blob
guard; Connectors "coming soon" tiles need a real CTA or removal; Settings email-intake: name the exact unlock.

**engine (BE)** — dedupe `ExtractBuyerName` (4 services) + log malformed CanonicalJson; `OutputFieldValidator`
add `Quantity <= 0`; `MappedTransformService.SafeLineSum` flag-on-overflow not silent-0; log (not swallow)
IncludeWhen/SourceMapReDerive/Scriban eval failures; cXML F2 price scale>2 flag.

**security (BE, medium)** — webhook ingress rate-limit partition is IP-keyed → derive a slug/org key
(WebhookIngressController:26 + Program.cs); verify `OrderIngestionService.CreateStubFromParsedOrderAsync`
sets org_id before SaveChanges (+ round-trip test); `AdminOnlyAttribute` document claim types + log on fail.

**design (low)** — off-palette blue/green soft borders → tokens (BillingSection, mapper panes, ConfidenceChip);
source/format chip colors → tokens; code-frame #1a1a1a/#ECEFF4 → --ink/--border; standardize radius scale;
Buyers page literal constants → tokens; onboarding "delivered"→"sent" copy.

## Masterplan next steps (from the review)

1. **WS-5** — collapse 4→1: built + flag-gated; #114 = founder sign-off → flip `NEXT_PUBLIC_ORDER_WORKSHOP_V2` ON + delete the old two-mode components. **(gated on founder)**
2. **WS-12** — EnvelopeConfig live X12/cXML identity → **SHIPPED** this round (null = byte-identical, characterization tests). Remaining: wire the pinned-revision envelope into the Phase-B caller (OrderTransformService) + verify on prod.
3. **WS-8** — hide versioning: lifecycle demoted; build the History/Advanced drawer (rollback/replay/archive/bundle/run-tests).
4. **WS-9** — vocabulary purge: ~7 canonical terms still in renders; grep-gate + CI grep gate.
5. **WS-13e** — dashboard operational funnel (received/blocked/ready/delivered/failed); demote topology to a "System map" tab.
6. **WS-11** — delete the dead `IParsedOrderTransform` export stack (registered, never invoked).
7. **Designer pixel-QA** — fresh worktree (isolated `.next`) screenshot/drag/responsive pass.
