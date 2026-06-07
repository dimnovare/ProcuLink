# ProcuLink — Launch Push Handoff (for the next chip)

**Updated:** 2026-06-07 · **Launch target:** 2026-06-09 · **Read order:** this file → `LAUNCH_EXECUTION_PLAN.md` → `PROD_LAUNCH_AUDIT.md`.
**Golden rule (founder):** do EVERYTHING in the audit, with as many agents as possible, **but NO new bugs** (build+tests green before every push; deploy-verify every wave). If unsure, ask. Use bun (never npm).

---

## TL;DR state

All **six launch-blocker waves (0–6) are DONE, pushed, deployed, and verified live on prod.** Backend tests **988 green** (was 887). Both repos clean + in sync with `origin/main`. Zero regressions reached prod (the adversarial review loop caught 4 real issues pre-deploy: a Wave-2 integration regression, an unguarded ERP SSRF, dead rate-limit policies, a missing Worker DI registration — all fixed before shipping).

**Remaining:** Wave 7 (full production test — founder just logged back in), Wave 8 (final regression + doc reconcile + runbook), Wave D (the deep refactors the founder green-lit + design-primitive page migrations).

### Commit anchors (HEAD = last shipped wave)
- Backend `ProcuLink` (https://github.com/dimnovare/ProcuLink) main HEAD: **`4af5dd5`** (Wave 6 AI model pin). Wave commits: `025dfe3` x12, `0da39cf` W2, `3c789b6` W3+3b, `5013fd8`+`961d5af` W4, `bbe1dd5` W5, `4af5dd5` W6. `901a3ba` design rules doc.
- Frontend `project-proculink` (https://github.com/dimnovare/project-proculink) main HEAD: **`0ceb156`** (Wave 6 UX). `432c4ea` W1, `1288691`/`3e98820`/`6d8e986` design+x12.
- Local repos: backend `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink`, frontend `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink`.

### Live prod
- Frontend: https://proculink.eu (Vercel, project `project-proculink`, account `dimnovare-9994`). API: https://api.proculink.eu (Railway project `lucid-generosity`, API service `ProcuLink`, Worker `aware-amazement`). DB: Neon Postgres. Clerk PROD instance (`iss=clerk.proculink.eu`, tokens carry `azp`). Stripe **TEST mode** (`sk_test`, live-swap is the founder's June-9 gate — see `docs/deployment/stripe-go-live-runbook.md`).

---

## What each wave delivered (done + verified live)

- **W0 Reconcile** — 6 read-only agents verified every audit claim vs current code; ~7 items were already-fixed (no redo).
- **W1 Honesty** — Inbound (Invoice/ASN) nav gated behind `NEXT_PUBLIC_INBOUND_ENABLED` (default off); invoice download (blob, was JSON) + accept-list fixed; ASN "EDI DESADV" claim dropped; `/formats` delivery (SFTP/FTPS/SMTP/Erply/Directo) → "Configurable" (HTTP+X12 stay Supported); `LimitBanner` from `plans.ts`; "supplier flows"→"Suppliers"; `ready_to_deliver` copy; Document-Anatomy tints now real (removed invented `ZONE_CONF`).
- **W2 Correctness** — Npgsql pool ceiling (API 30/Worker 20, read LAZILY + skip when `Pooling=false`); `IngressController` idempotency; AI mapping 50-line chunking; `StuckOrderDetectionService` requeue (additive `requeue_count` col, migration `20260607090000_AddOrderRequeueCount` — **verified applied on prod Neon**); removed dead `OrderService.ListAsync`.
- **W3+3b Security** — SSRF connect-time IP re-validation on HTTP/webhook (`SocketsHttpHandler.ConnectCallback`) + revalidate-before-connect on SMTP/SFTP/FTPS + guarded `"delivery"` HttpClient (fixes ERP SSRF) + CGNAT/benchmark ranges; global exception handler (ProblemDetails); `azp` required; CORS wildcard-subdomain removed; auto-provision throttle (bounded dict); tenant resolution unified (API-key path → `HttpContext.Items` like JWT); billing methods on `IBillingService` (no downcast); rate-limit policies APPLIED (transform/ai/signed-url; Stripe webhook deliberately spared); ApiKey `LastUsedAt` no longer races DbContext; path-traversal Ordinal containment. **Verified live:** boot 200, unauth→401, CORS preflight 204 exact-origin.
- **W4 Reliability** — Worker Sentry (`Sentry.Extensions.Logging`); `WorkerHealthAlertJob` (heartbeat/dead-letter → Sentry, anti-spam); `DataRetentionSweepJob` (DISABLED by default, config `DataRetention`); `/health` = fast liveness, `/health/ready` = DB+storage+migration checks; migrate fail-loud (flips readiness + Sentry, process stays up). **Verified live:** `/health` 200, `/health/ready` Healthy, Worker heartbeat 29s (1 server, no zombie).
- **W5 Billing** — Stripe `AppInfo` + documented that Stripe.net 51.1.0 auto-pins the API version (no risky hand-pin); **test-mode QA green** (4 prices active, Distributor checkout session created); `docs/deployment/stripe-go-live-runbook.md`.
- **W6 UX** — pricing 6→3 primary tiers + POs/month ROI recommender + "see all tiers" disclosure (all tiers reachable); in-app Distributor upsell (`Integration.next="distributor"` + Pilot row); wizard a11y (real `aria-checked` + filled dot) + restored buyer-blue active step (`T.blue` `#2E8E3A`→`#1E66C9`); `next` pinned `15.5.18` + `engines`; explicit `Ai:OpenAI:ExtractionModel=gpt-4o-mini`. **Verified live:** Vercel built clean, `/pricing` renders 3-tier + slider + disclosure.

---

## How to continue (orchestration model — replicate this)

1. **One `Workflow` per wave**, fan-out agents on **DISJOINT files** (assign file ownership so no two agents touch the same file; e.g. one agent owns `Program.cs` for all its changes). No worktree isolation needed when files are disjoint + agents don't build.
2. **Agent rules (paste into every prompt):** NO build/dotnet/bun/dev-server (gated centrally), NO commit/push, only edit YOUR files, VERIFY the issue is still present (cite file:line) before fixing (avoid redo), add/UPDATE xUnit tests, match existing style, behavior-preserving.
3. **Adversarial review** for security/correctness: a 2nd read-only agent tries to REFUTE each fix (pipeline: fix→verify). It caught the ERP-SSRF + dead-rate-limits this run — keep doing it.
4. **Integration gate (you, serial):** after merging a wave, run `dotnet test ProcuLink.slnx` (must be 988+ green) AND `cd project-proculink && bun run build` (must compile). Watch for: hand-written EF migrations missing the `HasColumnName` mapping in `ProcuLinkDbContext`; eager-vs-lazy config reads breaking `WebApplicationFactory` tests; runtime DI registrations agents can't add (they flag "ACTION REQUIRED" — you add them).
5. **Commit per repo** (founder authorized merge-when-green), push → Railway (API+Worker, auto-migrates additive migrations on boot) + Vercel. Exclude `.live-fixtures/` from commits.
6. **Deploy-verify every wave** (see commands below). Never push red.

---

## Verify / test commands (reusable)

```bash
# Full backend gate (Docker must be up for EndToEndPipelineTests testcontainers):
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink && dotnet test ProcuLink.slnx --nologo -v q
# Frontend build:
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink && bun run build
# Prod health:
curl -s -o /dev/null -w "%{http_code}" https://api.proculink.eu/health        # 200 liveness
curl -s https://api.proculink.eu/health/ready                                  # Healthy (DB+storage+migrate)
# Prod DB / Worker checks (secret piped, never printed) — scripts in .live-fixtures/:
railway variables --service ProcuLink --json | python .live-fixtures/dbcheck.py      # column/migration/row checks
railway variables --service ProcuLink --json | python .live-fixtures/workercheck.py  # hangfire heartbeat
# Railway var NAMES + set/blank (no values):
railway variables --service ProcuLink --json | python -c "import sys,json;d=json.load(sys.stdin);[print(('SET ' if str(v).strip() else 'BLANK')+' '+k) for k,v in sorted(d.items()) if not k.startswith('RAILWAY_')]"
```

## Live production test recipe (Wave 7) — fixtures + browser-JS upload

- Test fixtures already generated in `.live-fixtures/`: `live-csv.csv`, `live-xlsx2.xlsx` (ClosedXML — openpyxl xlsx FAILS on prod .NET "unsupported compression"!), `live-cxml.cxml`, `live-ubl.xml`, `live-edifact.edi`, `live-x12.edi`, `live-po.pdf` (real Markit text PDF). Distinct PO numbers `LIVE-<FMT>-2026`.
- The Chrome `file_upload` tool REJECTS repo/temp paths. Upload via **browser `javascript_tool`** in a tab on `https://proculink.eu`: `await window.Clerk.session.getToken()` → build a `File` from base64 (`atob`→Uint8Array) → `POST https://api.proculink.eu/api/orders/upload` (multipart `file`+`supplierId`; supplierId from `GET /api/suppliers`, Pilot cap=1 so reuse existing). Inject big base64 (PDF ~37KB+) in ≤50KB chunks via `window.__p += "..."`. Poll `GET /api/orders/{id}`.
- `javascript_tool` REPL: wrap as `(async()=>{...})()` (top-level await throws). **Chrome renders at fixed 1920 viewport — can't screenshot true mobile; QA mobile via code.** The founder's Clerk session expires — if `window.Clerk` is null the tab bounced to /sign-in; ask the founder to re-login (cannot log in for them).
- Delivery-channel test mimics: HTTP/webhook → webhook.site capture URL; S3 → throwaway bucket; SFTP/FTPS/SMTP → a public test server (founder may supply) else mark "Configurable"; ERP → mock REST (founder chose mock). Sentry testing token + PostHog token were provided in chat (HELD IN MEMORY, never committed — rotate post-launch).

---

## Founder decisions (locked)
1. **Deep refactors: DO NOW** (Wave D) — but sequence AFTER launch-blockers (done), facade-preserving + adversarial-reviewed + full test-gate; "no new bugs" is the tiebreaker (drop any that can't land green).
2. **Stripe: test-mode now; founder does the live swap** (runbook ready).
3. **ERP: mock REST endpoint** (mark Erply/Directo "Configurable" on /formats until a real sandbox).
4. **Email deliverability: Cloudflare full-write token PROVIDED** (in chat, hold in memory) — add SPF/DKIM/DMARC to `proculink.eu` for the supplier email-delivery channel (do in Wave 7/8).

---

## Wave D — deep refactors (remaining, do with maximum care)
From the audit's "can-wait/do-NOT-refactor-before-launch" list, founder said do them now:
- **OrderService God-object split** (1.7k LOC) into ingest/query/resolve services behind the `IOrderService` facade so controllers don't churn. Highest risk — keep facade, behavior-preserving, full test-gate, adversarial review.
- **Order status enum + transition table** (kills the silent-filter-break class).
- **Postgres RLS** as defense-in-depth (additive; app-level scoping stays).
- **Typed DTO/codegen contract layer** (the inline-anonymous-object pattern caused the invoice contract bugs).
- **Split `api-client.ts`** (2.7k LOC) into per-domain modules (DX, not correctness).
- **Design-primitive page migrations** — migrate the ~10 worst-offender pages (settings, operations/{health,connectors,webhooks}, admin, inbound/*, library/templates) onto `src/components/bridge/layout/{PageShell,PageHeader,Card,MobileListRow}` + `UnifiedStatusBadge` (primitives + `docs/design-system/11-unified-page-rules.md` already exist; 0/22 pages adopt them yet). NOTE: pages already render the Bridge visual language consistently (UI audit rated visual hierarchy STRONG) — this is maintainability, do per-page with build+deploy verify.
- **Also queued:** EmailPolling indexed boolean flag (replace `EmailConfigJson != "{}"` scan), R2/DB GDPR per-org delete path, Redis for HMAC nonce (only at >1 API replica), consolidate dual delivery-retry schedulers.

## Wave 8 — final gate (before declaring launch-ready)
- Full `dotnet test ProcuLink.slnx` + `bun run build` green; live golden-path smoke.
- Reconcile stale docs: CLAUDE.md says "211 tests" (reality 988); STATUS.md; remove dead mock-residue comments.
- SPF/DKIM/DMARC on proculink.eu (Cloudflare token provided).
- Consolidated ops runbook (Worker restart, stuck-order requeue, R2 secret rotation, Stripe live-swap, incident alerts).

## Gotchas (cost real time this run — avoid)
- Hand-written EF migration MUST also add `b.Property(x=>x.Foo).HasColumnName("foo")` in `ProcuLinkDbContext` (no global snake convention) — else runtime "column does not exist".
- `WebApplicationFactory` tests need LAZY config reads in `AddDbContext` (eager read at builder time misses the test's connection-string override).
- Agents that own file A can't add DI registrations in file B owned by a sibling — they flag "ACTION REQUIRED"; you add them.
- `Worker/Program.cs:239` already registers `IParseJobEnqueuer` (don't re-add).
- LF→CRLF git warnings are benign (Windows).
