# Handoff — PDF extraction Phases 2/3/4 (2026-06-05)

Self-contained handoff for finishing **Phase 3** and recording the full state of the
PDF-extraction roadmap (`docs/superpowers/plans/2026-06-05-pdf-llm-extraction.md`).
Founder authorized: **merge + push autonomously when safe.** Rule: **offer ⇔ works**
(never claim a capability that isn't shipped/verified). Repos: backend
`C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink`, frontend
`C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink` (use **bun**, never npm).

## Status of the 4 phases

| Phase | What | State |
|---|---|---|
| 1 — text→LLM primary + Azure removed | the primary PDF path | ✅ MERGED to `main` + deployed (earlier session) |
| 4 — canonical enrichment + PO-vs-invoice classifier | totals/supplier/tax/dates + `document_type` | ✅ MERGED `main` `d82b53b` (Railway deploying) |
| 2 — vision fallback for scanned/no-text PDFs | PDFtoImage+SkiaSharp → OpenAI vision | ✅ MERGED `main` `d20313c` (backend) + frontend `bf56fcc` |
| 3 — self-hosted no-egress OCR (RapidOcrNet) | per-org, opt-in, no OpenAI | ⏳ **COMMITTED on `feat/pdf-selfhosted-ocr` (`263b493`), pushed, NOT merged** |

Backend `main` tip after Phase 2: `d20313c`. Frontend `main` tip: `bf56fcc`.
Phase 3 branch `feat/pdf-selfhosted-ocr` = main + 2 commits (`4e09722` feature, `263b493` review fixes).

## Phase 3 — what's built (all verified)

- **`RapidOcrDocumentOcrService`** (`ProcuLink.Infrastructure/Services/Ocr/`): `IDocumentOcrService`
  backed by RapidOcrNet 2.0.0 (PP-OCRv5/ONNX, Apache-2.0, ~12MB models bundled). Opt-in via
  config **`NoEgressOcr:Enabled`** (else `NoOpOcrService` is registered → no models loaded → the
  default deploy is unchanged). Rasterizes via `SkiaPdfRasterizer` → OCR → text. Singleton, lazy
  `InitModels`, async `SemaphoreSlim` serialization (won't starve the WorkerCount=10 pool). Never throws.
- **Per-org `Organisation.SelfHostedOcr`** bool (+ migration `AddSelfHostedOcrFlag`, one additive
  NULLABLE/default-false boolean; **no snapshot drift**; applies clean on Postgres 16).
- **`OrderService.ParsePdfAsync`**: first branch — if the org is no-egress, parse the PDF with the
  DETERMINISTIC parser (its OCR fallback = the self-hosted engine) and RETURN; the OpenAI extractor
  is never reached.
- **No-egress guarantee closed** (review CRITICAL): `BuildLineEntitiesAsync` now gates the AI SKU
  mapping (`SuggestSupplierItemCodesAsync`) on the org flag (single chokepoint → covers all ingress
  paths); `InboundEmailRouter` skips the email-body NLP extractor for no-egress orgs.
- **Dockerfiles** (`Dockerfile`, `Dockerfile.worker`): runtime stage installs `libgomp1`
  (ONNX) + `libfontconfig1` (RapidOcrNet's SkiaSharp). RapidOcrNet added as a DIRECT ref to
  `ProcuLink.Api.csproj` + `ProcuLink.Worker.csproj` so the PP-OCRv5 models land in publish
  (transitive `PackageCopyToOutput` from Infrastructure does NOT flow).

**Verifications already done (don't redo unless changing the relevant code):**
- `dotnet test ProcuLink.slnx` → **785 green** (222 Transform + 334 Infra + 229 Api).
- `dotnet build ProcuLink.slnx` → clean (CA1416 suppressed in Infrastructure.csproj).
- Native deps proven on bare `aspnet:8.0` via Docker probes (see `docs/verification/native-deps.md`):
  PDFtoImage+SkiaSharp (no apt), and a COMBINED PDFtoImage+RapidOcrNet probe (with `libgomp1
  libfontconfig1`) — both ran. Real `Dockerfile.worker` builds with `models/v5/*` + `libonnxruntime.so`
  + `libSkiaSharp.so` + `libpdfium.so` present.
- Migration `AddSelfHostedOcrFlag` + `has-pending-model-changes` = none; applies clean on a throwaway
  Postgres 16 (`self_hosted_ocr` column present).
- Real OCR verified on the Windows runner (`RapidOcrDocumentOcrServiceTests` recognizes printed digits).
- Phase 3 adversarial review (find→2-vote verify): 9 raised, 5 confirmed; the CRITICAL + both MEDIUMs
  fixed in `263b493`; LOWs documented (below).

## REMAINING WORK (the chip's job) — do with Workflow agents; merge when safe

1. **Phase 3 docs/copy reconcile** (offer⇔works). Brief: *self-hosted no-egress OCR is now AVAILABLE,
   opt-in* (global `NoEgressOcr:Enabled` + per-org `SelfHostedOcr`); the **document ingest/parse
   pipeline is no-egress** for such orgs (PDF text/vision, AI SKU mapping, and email-body NLP are all
   gated). Caveat to state honestly: the **AI mapping-inference tool** (`SchemaInferenceController` /
   `OpenAiSchemaInferencer`, an explicit user action on column headers) is the one remaining OpenAI
   touchpoint for a no-egress org — either gate it too (see step 6) or document it as a known
   follow-up; do NOT claim "absolutely nothing touches OpenAI" until it's gated. Files to update
   (grep each for scanned/OCR/no-egress/Phase 3/RapidOcr):
   - Backend internal: `CLAUDE.md` (Group F), `STATUS.md`, the spec Phasing (mark Phase 3 ✅ SHIPPED),
     `docs/standards-matrix.md`, `docs/format-channel-roadmap.md`, `docs/integrations/ORDER_APIS.md`,
     `docs/operator-onboarding-runbook.md` (HOW to enable: set `NoEgressOcr:Enabled=true` on the
     Railway Worker/API + flip an org's `self_hosted_ocr`), `docs/product-selling-points.md`
     (no-egress OCR is now a real differentiator), `README.md`.
   - Frontend (project-proculink, on a branch — see step 4): the `security/page.tsx` page + the
     `formats`/help copy MAY mention an enterprise no-egress/on-prem-OCR option, but it's
     config/enterprise (not self-serve) — keep conservative. Memory: `feedback-offer-equals-works`.
   - Do NOT touch `*.spec.ts` assertions or the `ParseFailureExplain.cs` "scanned or image-only" string.
2. Commit the backend docs on `feat/pdf-selfhosted-ocr`.
3. **Merge backend**: `git checkout main && git merge --no-ff feat/pdf-selfhosted-ocr`; run
   `dotnet test ProcuLink.slnx` (expect green); `git push origin main`. **This auto-deploys to Railway**
   and rebuilds BOTH Docker images (apt layer + ~12MB models) and applies the additive migration — all
   verified safe. After push, `curl https://api.proculink.eu/health` should stay 200.
4. **Frontend** (if any copy changed): create `feat/pdf-selfhosted-copy`, commit, `bun run build`
   (expect ✓), push, then `git checkout main && git merge --no-ff` + push (Vercel).
5. **Cleanup**: delete merged branches (local + remote); confirm both `main`s clean.
6. **Optional but recommended** (closes the last no-egress touchpoint): gate `OpenAiSchemaInferencer`
   / `SchemaInferenceController` for no-egress orgs (check `Organisation.SelfHostedOcr`, short-circuit
   to no suggestions). Then the no-egress claim is whole. Add a test.
7. Update `STATUS.md` to "all 4 PDF phases shipped + merged"; update memory `project-pdf-llm-extraction`.
8. Run a final `/code-review` or adversarial review workflow on the merged diff before declaring done.

## Deferred review findings (documented; NOT blocking)
- **LOW** — ~12MB models + ONNX/Skia natives baked into BOTH Api + Worker images even though OCR runs
  only in the Worker. Kept on the Api for safety (avoid a "works in worker, throws in API" surprise);
  drop the Api-side `RapidOcrNet` ref + apt block later if image size matters.
- **LOW** — first scanned-PDF OCR after each deploy pays a multi-second `InitModels` cold start (inside
  the semaphore). Acceptable; could warm at boot.
- **MED (out of new scope)** — the schema-inference tool touchpoint (step 6).
- Pre-existing/platform (all phases): PDF intake has no byte-size cap on SFTP/S3/IMAP ingress; PdfPig
  parsing isn't time-bounded; the rasterizer dimension cap (2500px box) mitigates the giant-MediaBox
  OOM but the ingress byte cap is still a separate hardening follow-up.

## Key facts / gotchas
- Prod AI config (`Ai:Provider=openai`, `MappingModel=gpt-4o-mini`) comes from
  `appsettings.Production.json`, **NOT** Railway env (`railway variables` shows only
  `Ai__OpenAI__ApiKey`) — don't be fooled into thinking AI is off. gpt-4o-mini is vision-capable
  (Phase 2). Live smokes used it.
- `NoEgressOcr:Enabled` is **unset in prod** → default deploy registers `NoOpOcrService` (no models
  loaded) → Phase 3 ships dormant + safe. The founder enables it per the operator runbook when an
  org needs no-egress.
- Live-gated tests (`PROCULINK_LIVE_AI_TESTS=1` + `Ai__OpenAI__ApiKey`) no-op in CI; pull the key from
  Railway (`railway variables --service ProcuLink --json` → `Ai__OpenAI__ApiKey`) — NEVER print/commit it.
- Spawn agents for the docs reconcile (parallel, disjoint files) + a final review (find→2-vote verify).
