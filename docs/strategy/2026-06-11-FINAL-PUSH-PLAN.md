=== P0 ===
* Public claims truth sweep — security / subprocessors / privacy / DPA-adjacent pages
    why: Four checkably-false claim clusters collapse under the first enterprise security review: fabricated 'SOC 2 Type II in progress, report Q4 2026' + 'annual third-party pen tests'; three mutually contradicting subprocessor lists (Neon — the actual prod DB — and Postmark — which receives customer order 
    scope: project-proculink only: src/app/(marketing)/security/page.tsx, subprocessors/page.tsx, privacy/page.tsx (90-day retention reword), terms cross-check, src/app/page.tsx footer. New src/lib/subprocessors.ts single-source array (pattern: legal-entity.ts) imported by all three pages, then bump version/da
    effort: 0.5–1 day, one agent, one sitting
* Billing copy ⇔ billing engine alignment (pricing FAQ, Terms §5, one-pager)
    why: The pricing FAQ promises 'failed orders are free — an order only counts once successfully delivered' while CountOrdersAsync and the overage biller count every order at CreatedAt with no status filter; the Terms describe hard caps and never mention the auto-billed €0.50/order overage customers will a
    scope: Copy-only direction (the no-new-bugs option): rewrite FAQ to 'an order counts when it enters processing; samples and re-deliveries are free', add the overage sentence to Terms §5, rebuild the one-pager pricing table from src/lib/plans.ts. Files: (marketing)/pricing/page.tsx:94-104, terms/page.tsx:62
    effort: 2–4 h
* ROI calculator honest recommendation + visible overage on /pricing
    why: At the calculator's DEFAULT state it displays €317/mo savings beside a green 'Start with Operations €399/mo' buy-CTA, and the volume-only recommender contradicts the backend's own best-price math (Growth+overage = €174 at 200 orders, cheaper than Operations up to ~650/mo). Meanwhile the €0.50 never-
    scope: src/lib/plans.ts recommendPlanByOrders → cost-optimal (mirror PlanConstants.BestPriceOverageOrders; fixes pricing hero AND calculator in one place since both share the helper); ROICalculator.tsx net-of-plan headline + savings-aware CTA ('start smaller' framing already exists at line 416); pricing/pa
    effort: 0.5 day
* Kill fabricated confidence chips on the order-review heart-piece
    why: Header fields wear hardcoded 95–100% confidence chips (pct:99 PO number, 100 grand total — client-side constants) rendered in the SAME ConfChip UI as real per-line AI confidences, on the screen sold to 30-year veterans on honest provenance. The first wrong PO number wearing '99%' poisons every genui
    scope: SpineReview.tsx buildNodesFromOrder (161-171), ConfChip render (265-275), DocumentAnatomy zone heuristics (979-982): render ConfChip only when a real backend confidence exists; neutral muted 'parsed' chip otherwise; keep pct internal so wire colouring/dash logic in SpineConnectors is byte-for-byte u
    effort: 2–4 h
* A11y blocker pair: restore focus-visible + muted-ink contrast token sweep
    why: Any enterprise procurement axe/Lighthouse scan flags hundreds of violations on every screen: inline outline:'none' on the wire handles and 28+ other controls defeats the otherwise-correct global :focus-visible ring (WCAG 2.4.7 — the well-built keyboard wiring path is invisible in practice), and the 
    scope: Mechanical sweep across ~40 frontend files: delete inline outline:none (globals.css:114 already suppresses mouse-focus rings correctly); add onFocus/onBlur halo state to WireDragLayer/SourceWireDragLayer SVG handles (reuse the existing kbSource halo circle); replace the three hardcoded muted hexes w
    effort: 1 day incl. spot-check

=== P1 ===
* Artifact + delivery provenance columns (revision id, config digest, SHA-256) — THE migration  [1 day]
    The order-level revision pin exists but neither OutboundArtifact nor DeliveryAttempt can prove which revision/config produced them or detect silent artifact corruption — this is th
    scope: ProcuLink.Core/Entities/OutboundArtifact.cs + DeliveryAttempt.cs (+ ConnectionRevisionId, ConfigDigest, ArtifactSha256 nullable), ProcuLinkDbContext mapping, ONE additive EF migrat
* Connection lifecycle minimum: RollbackAsync + evidence-gated publish  [1 day]
    Publish is one click from a zero-evidence draft and rollback does not exist as an operation — the two things an enterprise operator reaches for in the first production incident, an
    scope: ISupplierConnectionService + SupplierConnectionService + ConnectionsController: add RollbackAsync (clone target prior published revision → publish, atomic pointer move on ActiveRev
* Honest revision context at the edit surface + two-way Connections discoverability  [0.5 day]
    Supplier editors (where ALL editing actually happens) write to live loose tables and never say so — operators either silently mutate what 'published' means or never discover versio
    scope: SupplierDockProfile.tsx: 'Connection' tab or header link → /connections/{id}; SupplierDockList per-row link; banner in PO-mapping/delivery/acceptance editors stating exactly what t
* Consume promoted supplier output mappings at transform time (guarded)  [1 day incl. golden tests]
    'Save mappings for this supplier' tells the user 'Future uploads from this supplier reuse it' — and the saved output is dead data. A verified silent no-op on the core product promi
    scope: OrderTransformService: inject IPoMappingService; read supplier PoMappingConfig.Output ONLY when no per-order override exists in CanonicalJson (override stays the highest-priority s
* Heart-piece P1 cluster: auto-verify acceptance into the confirm dialog, 13-inch layout gate, AI reason line, frozen list ordering  [1 day]
    Four verified gaps on the money screen: the 'verify' leg is opt-in (a user can fix lines and Send without ever seeing acceptance-rule results — on the screen whose pitch is 'never 
    scope: SpineReview.tsx only + WireDragLayer focus halos (shared with the a11y P0 agent): auto-run validateOrder at exceptionCount 0 / on dialog open and render pass-fail in ConfirmDialog 
* First-run integrity: wizard skip, checklist mapping step, exception reachability  [0.5 day]
    'Skip the wizard' is a verified no-op (?onboard=skip is never read; the wizard re-pops on every /bridge visit pre-supplier); the aha-path step 3 routes to a nav-hidden, empty libra
    scope: BridgeDashboard.tsx: read onboard=skip + persist dismissal (sessionStorage); OnboardingChecklist.tsx:79-86 href → /inbox/{firstOrderId} (matching the wizard's own behavior); KPI ca
* A11y P1 cluster: 12px floor on verification text, 44px touch ramp, aria-live warnings  [0.5–1 day]
    469 sub-12px text occurrences including INTERACTIVE 8.5px document tables a veteran must read to verify codes (persona = 50+ year-old eyes); resolve actions ship to the mobile acco
    scope: SpineReview.tsx + SourceTokenPanel.tsx font floors (data/warning text ≥10.5–12px; ResizeObserver already re-measures wires on height shifts); copy the proven DSPrimitives min-h-[44
* Demo-path header consistency: PageHeader swap on the five golden-path screens  [0.5 day]
    Five different H1 treatments (24/26/28/30/34px, weights 600/700) across adjacent nav siblings, one click apart within Operations — visible drift on exactly the screens an enterpris
    scope: Header-row-only swaps to the canonical PageHeader (NOT full PageShell migration): InboxView.tsx:797, BridgeDashboard.tsx:638, UploadWorkbench.tsx:553, SupplierDockList.tsx:175, Cro

=== P2 ===
* DB-level immutability for published revisions
* State-machine log-only observer
* Per-line review-reason column
* Delete dead status components + fix the misdirecting comment
* Token sweep: operations/webhooks + connectors + admin modals
* Pricing display polish: Growth above the fold + net-ROI honesty
* Nav taxonomy: promote Connections out of LIBRARY
* Copy/IA residue cluster
* Pilot Pdf/Xml dead feature gates
* Post-launch commitments queue: yearly checkout wiring, 90-day R2 retention sweep, delivered-only meter decision, full revision-authority wiring

=== QUICK WINS ===
- SpineReview HeaderStatusBadge → UnifiedStatusBadge/statusLabel() — kills the 'Ready' vs 'Ready to send'/'Normalized' contradiction on the money screen (minutes; keep crossed/exceptionCount overrides as status-key selecti
- Render sn.aiReason as one muted 10.5-11px line in the violet AI suggestion card (already fetched + mapped, never displayed; pure additive JSX)
- Conditional confirm-dialog copy — kill the permanent 'I've reviewed the 0 exceptions' sentence; '0 →' becomes 'Everything checks out. Send to {supplier}.'
- Add the existing wire-pulse-dot class to SpineConnectors pulse circles → prefers-reduced-motion compliant via the globals.css rule that already exists
- aria-label on the 3 glyph-only ✕ dismiss buttons (SpineReview banner, inbound asns/invoices notices)
- Remove the hardcoded 'All systems operational' green dot from both footers (keep the conditional Status link) — a fabricated live signal
- 'Frankfurt' → 'EU data residency' on the 4 pages (Railway is europe-west4/Netherlands — a checkably wrong city undermines every residency claim)
- Align TLS copy to the DPA's defensible 'TLS 1.2+ (TLS 1.3 where supported)' + 'anonymised' → 'pseudonymous product analytics' on privacy/subprocessors
- Secrets-'vault' sentence → the DPA Annex II AES-256-GCM wording that is already true (and still impressive)
- Inbox genuinely-empty state: drop the 'No orders match this filter.' sentence from emptyStateCopy
- Command palette: relabel 'View order inbox', add a Delivery log action, fix the stale launch-flags.ts header comment
- Dashboard 'Urgent exceptions' KPI card + legend chip → real Links to /operations/exceptions
- Onboarding checklist 'Resolve item mapping' href → /inbox/{firstOrderId} instead of the nav-hidden empty /library/mappings
- Tab change → router.replace('?tab='+id) (reader already exists) + red dot on the response tab when status is rejected_by_supplier
- PromoteMapping honest success copy stopgap — stop claiming 'Future uploads reuse it' until P1 consumption lands ('Saved — applied to this order; supplier-wide reuse is coming')
- MiniStatusPill labels via statusLabel() so future live wiring inherits correct vocabulary; touchAction 'pan-y' on source chips so chip-heavy panels scroll on touch

=== DO NOT DO ===
- Full SpineReview/triptych rewrite — all three lenses that audited it independently concluded every fix is additive JSX/conditional/CSS; a rewrite of the money screen is the maximum new-bug surface for zero demo gain (NO-
- Rewiring the four runtime services (parse/validate/transform/deliver) to read pinned revision config before launch — the verified #1 architecture gap, but it is a cross-cutting behavior change that violates byte-identica
- Changing the billing meter to delivered-only counting — a money-path change with Stripe overage interactions days before launch; fix the FAQ/Terms copy now (the 1-day honest option), revisit the meter deliberately with f
- Hard-enforcing OrderStatusMachine.IsAllowed on every write path — one missing legitimate transition bricks the pipeline; ship the log-only observer, enforce only after a clean observation window
- Switching the default landing to the exception queue (Codex's suggestion) — strands new/empty orgs and forfeits the onboarding hero; IA lens verdict is keep /bridge and make exceptions reachable (clickable KPI + nav badg
- Building SOC 2 / pen-test / SAML / RBAC capabilities to back the marketing claims — retreat the copy to truth in one sitting; build the capabilities post-launch on their own merit
- Wiring yearly checkout end-to-end under launch pressure — hide the toggle (offer⇔works; the smaller honest change), then wire the interval + verify the four Stripe yearly amounts match the displayed 17% as a calm follow-
- Full PageShell migration of all 15 legacy pages in one batch — header-row PageHeader swaps on the 5 demo-path screens only; full shell + container-width unification is post-launch mechanical work
- Implementing the 90-day R2 deletion sweep now — reword the privacy promise to what the system actually does; build the sweep properly post-launch (it is a real GDPR commitment a DPO will audit, not a launch-week hack)
- Replay-from-source-file parse-layer replay — replay was never designed to test parsing and never claimed to; add one sentence of boundary documentation in the ReplayPanel instead of expanding scope pre-launch
- Making Scriban the default output path anywhere in the promoted-mapping consumption work — it stays the power-user escape hatch; the per-order override remains the highest-priority seam

=== SEQUENCE ===
1. Batch 1 — Truth + money copy (frontend, ZERO migration, 3 parallel worktree agents on disjoint files): Agent A = claims truth sweep (P0-1 + trust quick wins: Frankfurt, TLS, vault, pseudonymous, status dot, subprocessors single-source array); Agent B = billing
2. Batch 2 — Heart-piece + a11y (frontend, ZERO migration, 2 agents with explicit file ownership to avoid collisions): Agent A OWNS SpineReview.tsx + wire layers + SourceTokenPanel — fabricated-confidence fix (P0-4), heart-piece quick wins (status badge, aiReason
3. Batch 3 — Backend provenance + lifecycle (THE migration batch — exactly one EF migration): Agent A = provenance columns on OutboundArtifact + DeliveryAttempt with write-path population + tests (P1-1, owns the batch's single migration, merges FIRST); Agent B (n
4. Batch 4 — Versioning honesty + promoted-mapping consumption (ZERO migration, after batch 3 confirms semantics): Agent A = revision-context banners + Connection tab/links on supplier surfaces (P1-3, wording matches verified backend behavior); Agent B = promoted
5. Batch 5 — Polish sweeps (P2, parallel, ZERO migration unless the review-reason column is pulled in — if so it is this batch's single migration): PageHeader demo-path swaps (P1-8) + dead status component deletion; token sweeps webhooks/connectors/admin modals; 
6. Batch 6 — Verification gate (no merge to main without ALL green): full dotnet test suite + bun run build; golden-order byte-identical diff across the existing order set (proves batches 3-4 changed nothing for unconfigured suppliers); live E2E re-run of the pro
