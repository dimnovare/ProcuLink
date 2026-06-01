# Boringly Reliable PO Loop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make upload -> parse -> review -> transform -> deliver reliable enough that a buyer/procurement team can trust it with real purchase orders.

**Architecture:** Treat every intake channel as a front door into the same order state machine. Fix parser routing and response consistency first, then add regression tests around each transition, then harden the UX around the exact failure point instead of hiding failures behind generic screens.

**Tech Stack:** ASP.NET Core 8, EF Core/Npgsql, Hangfire, ProcuLink.Transform parsers, Next.js 15 App Router frontend, Playwright visual QA.

---

## File map

- `ProcuLink.Api/Services/OrderService.cs` — core upload, stored-file parse, transform, and returned entity state.
- `ProcuLink.Transform/Parsing/OrderParserFactory.cs` — content-aware parser selection for ambiguous formats.
- `ProcuLink.Api.Tests/Integration/EndToEndPipelineTests.cs` — real Postgres E2E tests for the PO loop.
- `docs/integrations/ORDER_APIS.md` — current API/intake documentation.
- `docs/integrations/SUBMISSION.md` — Zapier/Make checklist; links to API docs.
- `STATUS.md`, `CLAUDE.md`, `AGENTS.md` — handoff memory and current priority.
- Frontend follow-up files for later passes:
  - `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/upload/page.tsx`
  - `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/inbox/[orderId]/page.tsx`
  - `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/UploadWorkbench.tsx`
  - `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/orders/SpineReview.tsx`

---

### Task 1: Lock XML Parser Routing

**Files:**
- Modify: `ProcuLink.Api/Services/OrderService.cs`
- Test: `ProcuLink.Api.Tests/Integration/EndToEndPipelineTests.cs`

- [x] **Step 1: Write the failing regression**

Add a Docker-backed E2E test that uploads a UBL XML file named `buyer-order.xml`.
The test must assert that `ParseStoredFileAsync` succeeds, `PoNumber` is
`PO-UBL-001`, `Currency` is `EUR`, exactly one line is returned, and detected
format is `ubl`.

- [x] **Step 2: Verify the failure**

Run:

```powershell
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ParseStoredFileAsync_UblXml"
```

Expected before the fix: the parse path can select the wrong `.xml` parser or
return inconsistent line state.

- [x] **Step 3: Use content-aware parser selection**

In `OrderService.CreateStubAsync` and `OrderService.ParseStoredFileAsync`, call:

```csharp
buffer.Position = 0;
var parser = _parserFactory.GetParser(extension, buffer);
buffer.Position = 0;
```

Use the returned parser for parsing, rather than choosing by extension only.

- [x] **Step 4: Fix returned parse entity line duplication**

After `PurchaseOrderLines.AddRange(lineEntities)` and `SaveChangesAsync`, set:

```csharp
entity.Lines = lineEntities;
```

Do not call `entity.Lines.AddRange(lineEntities)` because EF relationship fixup
can already attach the same tracked lines to the loaded order.

- [x] **Step 5: Verify the targeted test passes**

Run:

```powershell
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ParseStoredFileAsync_UblXml"
```

Expected: 1 passed, 0 failed.

---

### Task 2: Re-run The Existing Happy Path

**Files:**
- Test: `ProcuLink.Api.Tests/Integration/EndToEndPipelineTests.cs`

- [x] **Step 1: Run the full PO-loop integration test class**

Run:

```powershell
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~EndToEndPipelineTests"
```

Expected: both Docker-backed E2E tests pass when Docker is available. If Docker
is unavailable, tests should be skipped by `DockerRequiredFactAttribute`.

- [x] **Step 2: Run backend build**

Run:

```powershell
dotnet build ProcuLink.slnx --no-restore
```

Expected: build succeeds with 0 errors.

---

### Task 3: Lock The Manual Review Path

**Files:**
- Test: `ProcuLink.Api.Tests/Integration/EndToEndPipelineTests.cs`

- [x] **Step 1: Add a review-path E2E regression**

Add a Docker-backed test where a CSV order parses with one unresolved line. The
test must assert:

- parse status is `pending_review`;
- `TransformAsync` fails before resolution;
- `ResolveAsync` with `saveMappings: true` marks the order `ready`;
- the new item mapping is persisted;
- transform + HTTP delivery succeeds;
- final order status is `delivered`.

- [x] **Step 2: Run the targeted test**

Run:

```powershell
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~ReviewResolveTransformDeliver"
```

Expected: 1 passed, 0 failed.

- [x] **Step 3: Re-run E2E class**

Run:

```powershell
dotnet test ProcuLink.Api.Tests\ProcuLink.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~EndToEndPipelineTests"
```

Expected: 3 passed, 0 failed.

---

### Task 4: Document Intake Channels Honestly

**Files:**
- Create: `docs/integrations/ORDER_APIS.md`
- Modify: `docs/integrations/SUBMISSION.md`

- [x] **Step 1: Document browser upload, IMAP, hosted inbound email, REST API, and SFTP/S3**

The docs must distinguish:

- self-service now: browser upload, IMAP UI where plan allows it;
- backend exists but assisted setup: Postmark hosted inbound email, REST API;
- backend exists but internal/assisted only: SFTP/S3 polling until supplier routing is hardened.

- [x] **Step 2: Add REST API examples**

Include:

```http
GET /api/ingress/{slug}/ping
POST /api/ingress/{slug}/orders
X-ProcuLink-Key: plk_...
```

Include the exact JSON body with `supplierId`, `orderNumber`, `orderDate`,
`currency`, and `lines`.

- [x] **Step 3: Add OCR explanation**

Document that scanned PDFs should use OCR provider extraction first, then AI for
mapping/interpretation. Do not tell agents to use an LLM as the OCR engine.

---

### Task 5: Harden Assisted Pull Intake Before Self-Service

**Files:**
- Modify later: `ProcuLink.Core/Entities/SftpIngressConfig.cs`
- Modify later: `ProcuLink.Core/Entities/S3IngressConfig.cs`
- Modify later: `ProcuLink.Infrastructure/Services/Ingress/SftpIngressService.cs`
- Modify later: `ProcuLink.Infrastructure/Services/Ingress/S3IngressService.cs`
- Test later: `ProcuLink.Infrastructure.Tests/Services/Ingress/*`

- [ ] **Step 1: Add supplier resolution to SFTP/S3 configs**

Add one of these explicit routing rules before enabling customer self-service:

```text
defaultSupplierId
pathPattern -> supplierId
fileNamePattern -> supplierId
```

- [ ] **Step 2: Block unsafe imports**

If no supplier can be resolved, the poller must not call `CreateStubAsync` with
`Guid.Empty`. It must log the skipped object and keep it retryable.

- [ ] **Step 3: Add tests**

Add tests for:

- default supplier imports;
- no supplier configured skips safely;
- already-imported file is idempotently ignored;
- unsupported extension is skipped.

---

### Task 6: Live UI Happy/Error QA

**Files:**
- Modify later in frontend repo as needed.

- [ ] **Step 1: Run a real upload from browser to API**

Use a CSV sample with one mapped line and one unmapped line. Confirm:

- upload creates order;
- parse completes;
- review shows the unresolved line clearly;
- resolved mapping can be saved;
- transform creates artifact;
- manual delivery or auto-delivery produces an auditable attempt.

- [ ] **Step 2: Run failure cases**

Confirm the UI gives specific next actions for:

- unsupported format;
- scanned PDF with OCR disabled;
- no supplier selected;
- supplier delivery config missing;
- supplier rejection response.

---

### Self-review

- Spec coverage: this plan covers the first reliability spine, API documentation,
  intake-channel clarity, OCR positioning, and the known SFTP/S3 supplier-routing
  risk.
- Placeholder scan: no task uses "TBD" or asks an agent to invent unspecified
  validation.
- Type consistency: endpoint names, service names, and config keys match the
  current codebase.
