# Supplier Delivery Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build reliable buyer-side outbound supplier delivery configuration: transformed purchase orders become `ready_to_deliver`, then are dispatched through supplier delivery settings with audit, retry, test-fire, and clear delivery states.

**Architecture:** Keep the existing D2 scaffold, but correct the unsafe pieces before adding more connector breadth. `OrderService` owns parse/transform and artifact persistence; a new delivery workflow owns supplier dispatch and the only transition to `delivered`. HTTP/webhook is hardened first; SFTP/FTP are only added after the workflow and audit path are reliable.

**Tech Stack:** ASP.NET Core 8, EF Core 8, PostgreSQL JSONB, Hangfire, `AesGcm` credential encryption, `IHttpClientFactory`, Next.js 15 App Router frontend, TanStack Query, Bridge Layer design system.

---

## Current Checkpoint

Claude Code reached Task 3, then hit usage limits during code-quality review. Codex completed that review and committed:

`70f20bd fix: harden HTTP delivery dispatcher`

Committed D2 backend scaffold now includes:

- `SupplierDeliveryConfig` entity and EF migration.
- nullable `DeliveryAttempt.OrderId` for test-fire rows.
- `DeliveryEncryptionService`, currently AES-CBC and must be replaced.
- `IDeliveryDispatcher`, `DeliveryResult`, and hardened `HttpDeliveryDispatcher`.
- `ProcuLink.Infrastructure.Tests` with encryption and HTTP dispatcher tests.

Do not continue the older connector-first plan. This plan replaces it.

---

## Files And Responsibilities

Backend:

- `ProcuLink.Core/Constants/OrderStatusConstants.cs` - one source of truth for order workflow states.
- `ProcuLink.Core/Entities/SupplierDeliveryConfig.cs` - delivery config entity; update credential comment after GCM migration.
- `ProcuLink.Core/Entities/PurchaseOrderEntity.cs` - status documentation.
- `ProcuLink.Core/Services/Delivery/*` - delivery contracts, request/response records, service interfaces.
- `ProcuLink.Infrastructure/Services/DeliveryEncryptionService.cs` - replace AES-CBC with authenticated `AesGcm`.
- `ProcuLink.Infrastructure/Services/DeliveryConfigService.cs` - org-scoped CRUD and credential encryption.
- `ProcuLink.Infrastructure/Services/DeliveryService.cs` - dispatch workflow, audit rows, idempotency, test-fire.
- `ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs` - existing HTTP connector; keep hardened behavior.
- `ProcuLink.Infrastructure.Tests/Services/*` - focused tests for encryption, config service, delivery service, HTTP dispatcher.
- `ProcuLink.Api/Controllers/SuppliersController.cs` - delivery config endpoints.
- `ProcuLink.Api/Jobs/DeliverOrderJob.cs` - delegate to `IDeliveryService`, no old SupplierProfile webhook logic.
- `ProcuLink.Api/Jobs/TransformOrderJob.cs` - enqueue delivery after successful transform.
- `ProcuLink.Api/Services/OrderService.cs` - transform sets `ready_to_deliver`, not `delivered`.
- `ProcuLink.Api/Program.cs` - DI registrations.

Frontend:

- `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/lib/api/types.ts` - delivery types.
- `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/lib/api/delivery.ts` - API client.
- `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/DeliveryConfigEditor.tsx` - Delivery tab UI.
- `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/SupplierDockProfile.tsx` - add Delivery tab.

---

### Task 1: Replace AES-CBC With Authenticated Encryption

**Files:**

- Modify: `ProcuLink.Infrastructure/Services/DeliveryEncryptionService.cs`
- Modify: `ProcuLink.Infrastructure.Tests/Services/DeliveryEncryptionServiceTests.cs`
- Modify: `ProcuLink.Core/Entities/SupplierDeliveryConfig.cs`

- [ ] **Step 1: Update encryption tests first**

Add/adjust tests so they require authenticated encryption behavior:

```csharp
[Fact]
public void Decrypt_TamperedPayload_ReturnsNull()
{
    var svc = CreateService();
    var encrypted = svc.Encrypt("{\"type\":\"apikey\",\"value\":\"secret\"}");
    var bytes = Convert.FromBase64String(encrypted);
    bytes[^1] ^= 0x01;

    svc.Decrypt(Convert.ToBase64String(bytes)).Should().BeNull();
}
```

Keep the existing round-trip, different-output, wrong-key, garbage, and missing-key tests.

- [ ] **Step 2: Run encryption tests and verify the tamper test fails**

Run:

```bash
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter DeliveryEncryptionServiceTests
```

Expected before implementation: the tamper test fails or the service still references AES-CBC.

- [ ] **Step 3: Implement `AesGcm`**

Use `Delivery:EncryptionKey` as a base64 32-byte key. Payload format should be one base64 blob:

```text
version[1] + nonce[12] + tag[16] + ciphertext[n]
```

Use version byte `1`. `Decrypt` returns null on malformed payload, wrong key, or authentication failure.

- [ ] **Step 4: Update entity comment**

Change `SupplierDeliveryConfig.EncryptedCredentials` summary from AES-CBC to authenticated encrypted payload.

- [ ] **Step 5: Run tests**

Run:

```bash
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter DeliveryEncryptionServiceTests
```

Expected: all encryption tests pass.

- [ ] **Step 6: Commit**

```bash
git add ProcuLink.Infrastructure/Services/DeliveryEncryptionService.cs ProcuLink.Infrastructure.Tests/Services/DeliveryEncryptionServiceTests.cs ProcuLink.Core/Entities/SupplierDeliveryConfig.cs
git commit -m "fix: use authenticated delivery credential encryption"
```

---

### Task 2: Add Explicit Order Status Constants And Fix Transform Semantics

**Files:**

- Create: `ProcuLink.Core/Constants/OrderStatusConstants.cs`
- Modify: `ProcuLink.Core/Entities/PurchaseOrderEntity.cs`
- Modify: `ProcuLink.Core/Services/IOrderService.cs`
- Modify: `ProcuLink.Api/Services/OrderService.cs`
- Test: existing API/service tests if present; otherwise verify by build and focused manual code inspection.

- [ ] **Step 1: Add constants**

Create:

```csharp
namespace ProcuLink.Core.Constants;

public static class OrderStatusConstants
{
    public const string PendingParse = "pending_parse";
    public const string Parsing = "parsing";
    public const string PendingReview = "pending_review";
    public const string Ready = "ready";
    public const string Transforming = "transforming";
    public const string ReadyToDeliver = "ready_to_deliver";
    public const string Delivering = "delivering";
    public const string Delivered = "delivered";
    public const string DeliveryFailed = "delivery_failed";
    public const string Failed = "failed";
}
```

- [ ] **Step 2: Replace transform success status**

In `OrderService.TransformAsync`, after creating `OutboundArtifact`, set:

```csharp
entity.Status = OrderStatusConstants.ReadyToDeliver;
```

Do not set `delivered` during transform.

- [ ] **Step 3: Update comments/contracts**

Update `PurchaseOrderEntity.Status` XML comment and `IOrderService.TransformAsync` comment so they say transform persists an artifact and advances to `ready_to_deliver`.

- [ ] **Step 4: Build**

Run:

```bash
dotnet build ProcuLink.slnx --no-restore
```

Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Core/Constants/OrderStatusConstants.cs ProcuLink.Core/Entities/PurchaseOrderEntity.cs ProcuLink.Core/Services/IOrderService.cs ProcuLink.Api/Services/OrderService.cs
git commit -m "fix: separate transform and delivery order statuses"
```

---

### Task 3: Add Delivery Config Contracts And Service Interface

**Files:**

- Create: `ProcuLink.Core/Services/Delivery/DeliveryConfigContracts.cs`
- Create: `ProcuLink.Core/Services/Delivery/IDeliveryConfigService.cs`

- [ ] **Step 1: Add request/response records**

Use these records:

```csharp
namespace ProcuLink.Core.Services.Delivery;

public sealed record UpsertDeliveryConfigRequest(
    string Protocol,
    bool AutoDeliver,
    string ConfigJson,
    string? CredentialsJson);

public sealed record DeliveryConfigResponse(
    Guid SupplierId,
    string Protocol,
    bool AutoDeliver,
    string ConfigJson,
    bool HasCredentials,
    string? CredentialsDisplay,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record DeliveryTestResult(
    bool Success,
    string? ErrorMessage,
    int? ResponseCode);
```

- [ ] **Step 2: Add service interface**

```csharp
using ProcuLink.Core.Entities;

namespace ProcuLink.Core.Services.Delivery;

public interface IDeliveryConfigService
{
    Task<SupplierDeliveryConfig?> GetEntityAsync(Guid orgId, Guid supplierId, CancellationToken ct);
    Task<DeliveryConfigResponse?> GetAsync(Guid orgId, Guid supplierId, CancellationToken ct);
    Task<DeliveryConfigResponse> UpsertAsync(Guid orgId, Guid supplierId, UpsertDeliveryConfigRequest request, CancellationToken ct);
    Task DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}
```

- [ ] **Step 3: Build**

Run:

```bash
dotnet build ProcuLink.slnx --no-restore
```

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Core/Services/Delivery/DeliveryConfigContracts.cs ProcuLink.Core/Services/Delivery/IDeliveryConfigService.cs
git commit -m "feat: add delivery config contracts"
```

---

### Task 4: Implement DeliveryConfigService With Redaction

**Files:**

- Create: `ProcuLink.Infrastructure/Services/DeliveryConfigService.cs`
- Modify: `ProcuLink.Api/Program.cs`
- Test: `ProcuLink.Infrastructure.Tests/Services/DeliveryConfigServiceTests.cs`

- [ ] **Step 1: Write service tests**

Tests must cover:

- upsert creates one row scoped by `OrgId` and `SupplierId`
- upsert encrypts credentials when `CredentialsJson` is non-empty
- upsert preserves existing encrypted credentials when `CredentialsJson` is null
- `GetAsync` returns `HasCredentials=true` and `CredentialsDisplay="********"` but never plaintext or encrypted payload
- delete is no-op if not found

- [ ] **Step 2: Implement service**

Implementation rules:

- Verify protocol is exactly `http`, `sftp`, or `ftp`; otherwise throw `ArgumentException`.
- Persist only non-secret config in `ConfigJson`.
- Encrypt `CredentialsJson` through `DeliveryEncryptionService`.
- Never decrypt credentials in `GetAsync`.

- [ ] **Step 3: Register DI**

In `Program.cs`:

```csharp
builder.Services.AddSingleton<DeliveryEncryptionService>();
builder.Services.AddScoped<IDeliveryConfigService, DeliveryConfigService>();
builder.Services.AddScoped<IDeliveryDispatcher, HttpDeliveryDispatcher>();
```

- [ ] **Step 4: Run tests**

```bash
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter DeliveryConfigServiceTests
```

- [ ] **Step 5: Commit**

```bash
git add ProcuLink.Infrastructure/Services/DeliveryConfigService.cs ProcuLink.Infrastructure.Tests/Services/DeliveryConfigServiceTests.cs ProcuLink.Api/Program.cs
git commit -m "feat: add delivery config service"
```

---

### Task 5: Add Supplier Delivery Config API Endpoints

**Files:**

- Modify: `ProcuLink.Api/Controllers/SuppliersController.cs`

- [ ] **Step 1: Inject `IDeliveryConfigService`**

Add a constructor dependency and private field.

- [ ] **Step 2: Add four endpoints**

Add:

```text
GET    /api/suppliers/{id}/delivery-config
PUT    /api/suppliers/{id}/delivery-config
DELETE /api/suppliers/{id}/delivery-config
POST   /api/suppliers/{id}/delivery-config/test-fire
```

Each endpoint first verifies:

```csharp
var supplier = await _db.Suppliers
    .FirstOrDefaultAsync(s => s.Id == id && s.OrgId == orgId && s.DeletedAt == null, ct);
if (supplier is null) return NotFound();
```

GET returns `NoContent()` when no config exists. PUT returns saved redacted response. DELETE returns `NoContent()`.

The test-fire endpoint is wired in Task 7 after `IDeliveryService` exists; in this task it may return `501` only if implementing endpoints before Task 7, but prefer doing Task 7 before exposing test-fire.

- [ ] **Step 3: Build**

```bash
dotnet build ProcuLink.slnx --no-restore
```

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Api/Controllers/SuppliersController.cs
git commit -m "feat: add supplier delivery config endpoints"
```

---

### Task 6: Add DeliveryService And Delivery Job Workflow

**Files:**

- Create: `ProcuLink.Core/Services/Delivery/IDeliveryService.cs`
- Create: `ProcuLink.Infrastructure/Services/DeliveryService.cs`
- Modify: `ProcuLink.Api/Jobs/DeliverOrderJob.cs`
- Modify: `ProcuLink.Api/Program.cs`
- Test: `ProcuLink.Infrastructure.Tests/Services/DeliveryServiceTests.cs`

- [ ] **Step 1: Add interface**

```csharp
namespace ProcuLink.Core.Services.Delivery;

public interface IDeliveryService
{
    Task<DeliveryResult> DispatchArtifactAsync(Guid orgId, Guid orderId, Guid artifactId, bool requireAutoDeliver, CancellationToken ct);
    Task<DeliveryTestResult> TestFireAsync(Guid orgId, Guid supplierId, CancellationToken ct);
}
```

- [ ] **Step 2: Write service tests**

Tests must cover:

- no config and `requireAutoDeliver=true` no-ops and leaves order `ready_to_deliver`
- auto-deliver false and `requireAutoDeliver=true` no-ops
- success writes `DeliveryAttempt(Status="success")` and order `delivered`
- failure writes `DeliveryAttempt(Status="failed")` and order `delivery_failed`
- test-fire uses `OrderId=null`
- wrong org cannot dispatch another org's order/artifact/config

- [ ] **Step 3: Implement service**

Implementation rules:

- Load `OutboundArtifact` by `artifactId`, `orderId`, and `orgId`.
- Load order by `orderId` and `orgId`.
- Load config by `orgId` and `order.SupplierId`.
- If no config, return `DeliveryResult(true, null)` and do not mark delivered.
- If `requireAutoDeliver` and `AutoDeliver=false`, return `DeliveryResult(true, null)` and do not mark delivered.
- Before dispatch, set order status `delivering`.
- Decrypt credentials; on null, write failed attempt and set `delivery_failed`.
- Resolve dispatcher by `config.Protocol`.
- Persist `DeliveryAttempt` for every real dispatch.
- Only dispatcher success sets order `delivered`.

- [ ] **Step 4: Rewrite `DeliverOrderJob`**

Replace old `SupplierProfile.DestinationType == "webhook"` logic with:

```csharp
await _deliveryService.DispatchArtifactAsync(organisationId, orderId, artifactId, requireAutoDeliver: true, ct);
```

Let Hangfire retry only transient exceptions. DeliveryService should return failure results for expected remote rejections.

- [ ] **Step 5: Register DI**

```csharp
builder.Services.AddScoped<IDeliveryService, DeliveryService>();
```

- [ ] **Step 6: Run tests**

```bash
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter DeliveryServiceTests
```

- [ ] **Step 7: Commit**

```bash
git add ProcuLink.Core/Services/Delivery/IDeliveryService.cs ProcuLink.Infrastructure/Services/DeliveryService.cs ProcuLink.Infrastructure.Tests/Services/DeliveryServiceTests.cs ProcuLink.Api/Jobs/DeliverOrderJob.cs ProcuLink.Api/Program.cs
git commit -m "feat: add delivery workflow service"
```

---

### Task 7: Enqueue Auto Delivery After Transform

**Files:**

- Modify: `ProcuLink.Api/Jobs/TransformOrderJob.cs`
- Modify: `ProcuLink.Api/Controllers/OrdersController.cs` only if response/status copy needs adjusting.

- [ ] **Step 1: Inject `IBackgroundJobClient` into `TransformOrderJob`**

After successful transform, call:

```csharp
DeliverOrderJob.Enqueue(_jobs, orderId, organisationId, result.Value!.ArtifactId);
```

This is safe because `DeliverOrderJob` no-ops when no config exists or `auto_deliver=false`.

- [ ] **Step 2: Confirm transform response semantics**

Transform endpoint still returns `202 Accepted` for job enqueue. Order status after transform should become `ready_to_deliver`, then `delivering`/`delivered`/`delivery_failed` only if the delivery job runs.

- [ ] **Step 3: Build**

```bash
dotnet build ProcuLink.slnx --no-restore
```

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Api/Jobs/TransformOrderJob.cs ProcuLink.Api/Controllers/OrdersController.cs
git commit -m "feat: enqueue delivery after transform"
```

---

### Task 8: Wire Test-Fire Endpoint

**Files:**

- Modify: `ProcuLink.Api/Controllers/SuppliersController.cs`
- Test manually via Scalar after backend build.

- [ ] **Step 1: Inject `IDeliveryService`**

Add service to constructor.

- [ ] **Step 2: Implement endpoint body**

After supplier org-scope guard:

```csharp
var result = await _deliveryService.TestFireAsync(orgId, id, ct);
return Ok(result);
```

If no config exists, return `NotFound(new { error = "Delivery config not found." })`.

- [ ] **Step 3: Build**

```bash
dotnet build ProcuLink.slnx --no-restore
```

- [ ] **Step 4: Commit**

```bash
git add ProcuLink.Api/Controllers/SuppliersController.cs
git commit -m "feat: add delivery test-fire endpoint"
```

---

### Task 9: Add Frontend Delivery API Client And Types

**Files:**

- Modify: `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/lib/api/types.ts`
- Create: `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/lib/api/delivery.ts`

- [ ] **Step 1: Add TypeScript types**

Add `DeliveryConfig`, `DeliveryProtocol`, `UpsertDeliveryConfigRequest`, and `DeliveryTestResult`.

- [ ] **Step 2: Add API client functions**

Implement:

```ts
getDeliveryConfig(supplierId)
upsertDeliveryConfig(supplierId, payload)
deleteDeliveryConfig(supplierId)
testFireDelivery(supplierId)
```

Use the existing API client helper style in `src/lib/api/mapping.ts`.

- [ ] **Step 3: Run type check/build**

```bash
bun run build
```

- [ ] **Step 4: Commit**

```bash
git add src/lib/api/types.ts src/lib/api/delivery.ts
git commit -m "feat: add delivery config API client"
```

---

### Task 10: Add Bridge Layer Delivery Tab

**Files:**

- Create: `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/DeliveryConfigEditor.tsx`
- Modify: `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/components/bridge/SupplierDockProfile.tsx`

- [ ] **Step 1: Read design quick brief**

Read:

```text
C:/Users/Dmitri.REDACTED-PARTY/source/repos/ProcuLink/docs/design-system/00-agent-quick-brief.md
```

Use Bridge Layer density and avoid Lovable/Vite patterns.

- [ ] **Step 2: Build `DeliveryConfigEditor`**

UI requirements:

- Protocol segmented control: HTTP enabled first. SFTP/FTP can be visible but disabled with "coming after HTTP workflow is proven" if backend dispatch is not implemented yet.
- Auto-deliver toggle.
- HTTP fields: endpoint URL, method, auth type, API key/bearer/basic fields, headers, timeout seconds.
- Saved credentials display as masked; editing replaces.
- Test-fire button with green/red inline result strip.
- Save/Delete footer.

- [ ] **Step 3: Add tab to SupplierDockProfile**

Add `"delivery"` to tab union and add the panel after PO Mapping.

- [ ] **Step 4: Run frontend build**

```bash
bun run build
```

- [ ] **Step 5: Commit**

```bash
git add src/components/bridge/DeliveryConfigEditor.tsx src/components/bridge/SupplierDockProfile.tsx
git commit -m "feat: add supplier delivery config UI"
```

---

### Task 11: Final Verification And Status Update

**Files:**

- Modify: `STATUS.md`

- [ ] **Step 1: Backend verification**

Run:

```bash
dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore
dotnet build ProcuLink.slnx --no-restore
```

- [ ] **Step 2: Frontend verification**

In `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink`:

```bash
bun run build
```

- [ ] **Step 3: Manual checks**

Use Scalar or frontend UI to verify:

- GET delivery config returns `204` when absent.
- PUT HTTP config saves and returns masked credentials only.
- GET returns masked credentials only.
- Test-fire creates a `DeliveryAttempt` with `order_id = null`.
- Transforming an order leaves it `ready_to_deliver` before real delivery acceptance.
- Delivery success is the only path to `delivered`.
- Delivery failure preserves the artifact and marks only delivery as failed.

- [ ] **Step 4: Update status**

Update `STATUS.md` with exactly what shipped and any deferred connector work.

- [ ] **Step 5: Code review**

Run `/code-review` or equivalent review before marking D2 complete.

---

## Deferred From D2

Do not add PEPPOL, ERP connectors, invoices, or broad document types in this group.

SFTP and FTP dispatchers are intentionally deferred unless HTTP delivery workflow, status semantics, credential encryption, audit rows, and UI are already green. The UI may show SFTP/FTP as disabled or saved-only depending on final execution choice, but real dispatch should not be half-built.

---

## Self-Review

Spec coverage:

- Buyer-side outbound delivery: covered by tasks 2, 6, 7, 10.
- Authenticated credential encryption: covered by task 1.
- Config CRUD and redaction: covered by tasks 3, 4, 5, 9, 10.
- Delivery workflow states: covered by tasks 2, 6, 7.
- HTTP first: covered by current Task 3 checkpoint and tasks 6-8.
- Test-fire audit rows: covered by tasks 6 and 8.
- Frontend Bridge Layer UI: covered by task 10.

Known execution concern:

- C2 billing reconciliation is still a separate required queue item in `STATUS.md`. Do not change billing behavior inside D2 unless the user explicitly moves C2 into the same implementation session.
