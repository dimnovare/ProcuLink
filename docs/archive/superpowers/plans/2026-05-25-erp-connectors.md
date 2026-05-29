# ERP Connectors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add first-pass Erply and Directo ERP delivery connectors to the existing supplier delivery pipeline.

**Architecture:** Treat ERP connectors as delivery protocols because `SupplierDeliveryConfig.Protocol` already drives dispatcher selection and delivery attempts. Add a small ERP connector abstraction for shared semantics, then expose `erp_erply` and `erp_directo` through `IDeliveryDispatcher` adapters so generated artifacts can be delivered by the existing `DeliveryService`.

**Tech Stack:** .NET 8, `IHttpClientFactory`, `System.Text.Json`, XML form posts for Directo, xUnit/FluentAssertions/Moq, Next.js/Tailwind delivery config editor.

---

## File Structure

- `ProcuLink.Core/Services/Erp/IErpConnector.cs` — provider-neutral ERP connector contract.
- `ProcuLink.Infrastructure/Services/Erp/ErplyConnector.cs` — REST-style Erply delivery implementation.
- `ProcuLink.Infrastructure/Services/Erp/DirectoConnector.cs` — Directo XML/API delivery implementation.
- `ProcuLink.Infrastructure/Services/Dispatchers/ErpDeliveryDispatchers.cs` — two protocol adapters: `erp_erply`, `erp_directo`.
- `ProcuLink.Infrastructure/Services/DeliveryConfigService.cs` — allow the two ERP protocols.
- `ProcuLink.Api/Program.cs` — register connector services and dispatchers.
- `ProcuLink.Infrastructure.Tests/Services/DeliveryConfigServiceTests.cs` — protocol validation tests.
- `ProcuLink.Infrastructure.Tests/Services/Erp/ErpConnectorTests.cs` — connector request/response behavior tests.
- `project-proculink/src/lib/api/types.ts` — add ERP protocol literal types.
- `project-proculink/src/components/bridge/DeliveryConfigEditor.tsx` — add Erply/Directo configuration modes.
- `STATUS.md`, `CLAUDE.md`, `AGENTS.md` — mark Group G complete and Group H next.

---

### Task 1: Backend Protocol Validation Tests

**Files:**
- Modify: `ProcuLink.Infrastructure.Tests/Services/DeliveryConfigServiceTests.cs`

- [x] Add a test proving `erp_erply` and `erp_directo` are accepted and normalized.
- [x] Update the unknown-protocol test expectation to list `http`, `sftp`, `ftp`, `erp_erply`, and `erp_directo`.
- [x] Run:
  `dotnet test ProcuLink.Infrastructure.Tests\ProcuLink.Infrastructure.Tests.csproj --no-restore --filter DeliveryConfigServiceTests`
  Expected before implementation: protocol acceptance test fails.

### Task 2: Backend Protocol Constants and Validation

**Files:**
- Create: `ProcuLink.Core/Constants/DeliveryProtocolConstants.cs`
- Modify: `ProcuLink.Core/Entities/SupplierDeliveryConfig.cs`
- Modify: `ProcuLink.Core/Services/Delivery/IDeliveryDispatcher.cs`
- Modify: `ProcuLink.Infrastructure/Services/DeliveryConfigService.cs`

- [x] Add constants:
  - `http`
  - `sftp`
  - `ftp`
  - `erp_erply`
  - `erp_directo`
- [x] Replace hard-coded validation strings in `DeliveryConfigService`.
- [x] Update XML comments so the new destination/protocol values are documented.
- [x] Run the DeliveryConfigService tests again. Expected: pass.

### Task 3: ERP Connector Contract and Tests

**Files:**
- Create: `ProcuLink.Core/Services/Erp/IErpConnector.cs`
- Create: `ProcuLink.Infrastructure.Tests/Services/Erp/ErpConnectorTests.cs`

- [x] Define:
  - `IErpConnector`
  - `ErpDeliveryRequest`
  - `ErpDeliveryResult`
- [x] Add tests proving:
  - Erply posts artifact bytes to the configured endpoint with bearer/API-key auth.
  - Directo posts XML payload as form data to the configured endpoint.
  - Invalid/missing config returns a failed `ErpDeliveryResult`, not an exception.
- [x] Run:
  `dotnet test ProcuLink.Infrastructure.Tests\ProcuLink.Infrastructure.Tests.csproj --no-restore --filter ErpConnectorTests`
  Expected before implementation: connector types missing.

### Task 4: ERP Connector Implementations and Dispatchers

**Files:**
- Create: `ProcuLink.Infrastructure/Services/Erp/ErplyConnector.cs`
- Create: `ProcuLink.Infrastructure/Services/Erp/DirectoConnector.cs`
- Create: `ProcuLink.Infrastructure/Services/Dispatchers/ErpDeliveryDispatchers.cs`
- Modify: `ProcuLink.Api/Program.cs`

- [x] Implement Erply connector as an HTTP POST adapter with safe config validation and masked failure messages.
- [x] Implement Directo connector as an HTTP POST adapter sending XML/API parameters as form data.
- [x] Implement dispatcher adapters:
  - `ErplyDeliveryDispatcher.Protocol = "erp_erply"`
  - `DirectoDeliveryDispatcher.Protocol = "erp_directo"`
- [x] Register connectors and dispatchers in DI.
- [x] Run infrastructure tests and backend build.

### Task 5: Frontend Delivery Editor ERP Modes

**Files:**
- Modify: `project-proculink/src/lib/api/types.ts`
- Modify: `project-proculink/src/components/bridge/DeliveryConfigEditor.tsx`

- [x] Extend `DeliveryProtocol` with `erp_erply` and `erp_directo`.
- [x] Add enabled protocol buttons for Erply and Directo.
- [x] Render ERP-specific fields:
  - Erply: endpoint URL, auth mode/API token, client code, timeout.
  - Directo: endpoint URL, database, user, key/password, timeout.
- [x] Save protocol-specific `configJson` and `credentialsJson` without leaking secrets back into the UI.
- [x] Run `bun run build`.

### Task 6: Handoff and Commit

**Files:**
- Modify: `STATUS.md`
- Modify: `CLAUDE.md`
- Modify: `AGENTS.md`

- [x] Mark Group G implemented and Group H next.
- [x] Note that these are connector adapters for already-generated artifacts; full ERP-native order modeling remains future hardening.
- [x] Run:
  - `dotnet build ProcuLink.slnx --no-restore`
  - `dotnet test ProcuLink.slnx --no-restore`
  - `bun run build`
- [x] Commit backend and frontend separately.

---

## Self-Review

- Spec coverage: covers `IErpConnector`, `ErplyConnector`, `DirectoConnector`, and the new `erp_erply` / `erp_directo` protocol values.
- Scope control: does not add a new DB table or ERP-native order model; uses the existing delivery config and attempt audit trail.
- Main limitation to document: connectors deliver the generated artifact payload to ERP endpoints; supplier/ERP-specific native payload transforms are a later hardening task.
