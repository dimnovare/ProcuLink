# Supplier Delivery Config Design

**Date:** 2026-05-25  
**Group:** Phase 4 Group D2  
**Status:** Approved for implementation

---

## Goal

Add per-supplier delivery configuration so that transformed PO artifacts can be dispatched automatically or on-demand via HTTP webhook, SFTP, or FTP — replacing the stub `DestinationType`/`DestinationConfig` fields that have lived unused on `SupplierProfileEntity`.

---

## Architecture

A new `SupplierDeliveryConfig` entity stores non-secret endpoint config as JSONB and sensitive credentials as AES-256-CBC encrypted JSON. A `IDeliveryDispatcher` abstraction has three implementations (HTTP, SFTP, FTP). A `DeliveryService` orchestrates dispatch, writes `DeliveryAttempt` audit rows, and updates order status. `OrderService.TransformAsync` calls `DeliveryService.DispatchIfAutoAsync` immediately after artifact upload.

**Tech stack additions:**
- `SSH.NET` NuGet package — SFTP client
- `FluentFTP` NuGet package — FTP/FTPS client
- `System.Security.Cryptography.Aes` (BCL) — AES-256-CBC encryption, no extra package

---

## Data Model

### `supplier_delivery_configs` table

| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | |
| `org_id` | uuid FK | → organisations |
| `supplier_id` | uuid FK | → suppliers |
| `protocol` | text | `'http'` \| `'sftp'` \| `'ftp'` |
| `auto_deliver` | bool | Default false. If true, dispatch fires automatically after transform. |
| `config_json` | jsonb | Non-secret endpoint/path/options (see shapes below) |
| `encrypted_credentials` | text | AES-256-CBC, base64-encoded. Decrypted only server-side at dispatch time. |
| `created_at` | timestamp | |
| `updated_at` | timestamp | |

**Unique index** on `(org_id, supplier_id)` — one delivery config per supplier per org.

The existing `SupplierProfileEntity.DestinationType` and `DestinationConfig` columns are left in place (no breaking migration). They are not read or written by Group D2.

### `config_json` shapes

**HTTP:**
```jsonc
{
  "url": "https://api.supplier.com/orders",
  "method": "POST",                          // default POST
  "headers": { "X-Format": "csv" },         // optional extra headers
  "timeoutSeconds": 30                       // default 30
}
```

**SFTP:**
```jsonc
{
  "host": "sftp.supplier.com",
  "port": 22,
  "remotePath": "/in/orders/",
  "fileNamePattern": "{poNumber}.{ext}"      // tokens: {poNumber}, {ext}, {date}
}
```

**FTP:**
```jsonc
{
  "host": "ftp.supplier.com",
  "port": 21,
  "remotePath": "/orders/",
  "useTls": true,
  "fileNamePattern": "{poNumber}.{ext}"
}
```

### `encrypted_credentials` — decrypted shapes

**HTTP — one of:**
```jsonc
{ "type": "apikey",  "header": "X-Api-Key", "value": "sk-…" }
{ "type": "bearer",  "token": "eyJ…" }
{ "type": "basic",   "username": "u", "password": "p" }
{ "type": "none" }
```

**SFTP — one of:**
```jsonc
{ "username": "svc", "password": "p" }
{ "username": "svc", "privateKey": "-----BEGIN OPENSSH PRIVATE KEY-----…" }
```

**FTP:**
```jsonc
{ "username": "u", "password": "p" }
```

### Encryption

Key: `IConfiguration["Delivery:EncryptionKey"]` — 32-byte base64 string set via environment variable / secrets.  
Algorithm: AES-256-CBC with a random 16-byte IV prepended to the ciphertext before base64 encoding.  
`DeliveryEncryptionService.Encrypt(plaintext) → base64(iv + ciphertext)`  
`DeliveryEncryptionService.Decrypt(base64) → plaintext | null` (null on any error, never throws)

---

## Backend Components

### `ProcuLink.Core`

**`Entities/SupplierDeliveryConfig.cs`**  
EF entity mapping to `supplier_delivery_configs`. Properties: `Id`, `OrgId`, `SupplierId`, `Protocol`, `AutoDeliver`, `ConfigJson`, `EncryptedCredentials`, `CreatedAt`, `UpdatedAt`. Navigation: `Supplier`, `Organisation`.

**`Services/Delivery/IDeliveryConfigService.cs`**
```csharp
Task<SupplierDeliveryConfig?> GetAsync(Guid orgId, Guid supplierId, CancellationToken ct);
Task<SupplierDeliveryConfig> UpsertAsync(Guid orgId, Guid supplierId, UpsertDeliveryConfigRequest request, CancellationToken ct);
Task DeleteAsync(Guid orgId, Guid supplierId, CancellationToken ct);
```

**`Services/Delivery/IDeliveryDispatcher.cs`**
```csharp
string Protocol { get; }   // "http" | "sftp" | "ftp"
Task<DeliveryResult> DispatchAsync(
    byte[] content, string fileName, string contentType,
    SupplierDeliveryConfig config, string decryptedCredentials,
    CancellationToken ct);
```

**`Services/Delivery/DeliveryResult.cs`**
```csharp
record DeliveryResult(bool Success, string? ErrorMessage);
```

**`Services/Delivery/IDeliveryService.cs`**
```csharp
/// <summary>Called by OrderService after artifact upload. No-ops if no config or auto_deliver=false.</summary>
Task DispatchIfAutoAsync(Guid orgId, Guid supplierId, OutboundArtifact artifact, CancellationToken ct);

/// <summary>Called by test-fire endpoint. Sends hardcoded sample payload. orderId=null in audit row.</summary>
Task<DeliveryResult> TestFireAsync(Guid orgId, Guid supplierId, CancellationToken ct);
```

### `ProcuLink.Infrastructure`

**`Services/DeliveryEncryptionService.cs`**  
Wraps `Aes.Create()`. Key loaded from config. `Encrypt` generates random IV per call. `Decrypt` returns null (not throws) on any error.

**`Services/DeliveryConfigService.cs`**  
Implements `IDeliveryConfigService`. `GetAsync` returns entity with `EncryptedCredentials` intact (never decrypted for API responses). `UpsertAsync` encrypts credentials before saving. `DeleteAsync` is a no-op if not found.

**`Services/DeliveryService.cs`**  
Implements `IDeliveryService`. On dispatch: load config → resolve dispatcher by `Protocol` → decrypt credentials → call `DispatchAsync` → write `DeliveryAttempt` → update order status.

**`Services/Dispatchers/HttpDeliveryDispatcher.cs`**  
Uses `IHttpClientFactory`. Posts artifact bytes as `multipart/form-data` or raw body (per `config_json.method`). Applies auth headers from decrypted credentials. 4xx/5xx → `DeliveryResult(false, "HTTP 422: …")`.

**`Services/Dispatchers/SftpDeliveryDispatcher.cs`**  
Uses `Renci.SshNet.SftpClient`. Connects with password or private key from credentials. Uploads artifact to `remotePath/fileName`. Disconnects in `finally`.

**`Services/Dispatchers/FtpDeliveryDispatcher.cs`**  
Uses `FluentFTP.AsyncFtpClient`. Connects with `useTls` flag. Uploads to `remotePath/fileName`. Disconnects in `finally`.

**`ProcuLinkDbContext.cs`**  
Adds `DbSet<SupplierDeliveryConfig> SupplierDeliveryConfigs`. EF config: `ToTable("supplier_delivery_configs")`, snake_case columns, `HasColumnType("jsonb")` for `ConfigJson`, unique index on `(OrgId, SupplierId)`.

### `ProcuLink.Api`

**`Controllers/SuppliersController.cs`** — 4 new endpoints:

| Method | Route | Description |
|---|---|---|
| `GET` | `{id}/delivery-config` | Returns config with credentials redacted (`"••••"`) |
| `PUT` | `{id}/delivery-config` | Upsert; encrypts credentials server-side |
| `DELETE` | `{id}/delivery-config` | Remove config |
| `POST` | `{id}/delivery-config/test-fire` | Send sample payload; return `{ success, errorMessage }` |

All 4 endpoints verify `supplier.OrgId == orgId && supplier.DeletedAt == null` before acting.  
`GET` never returns raw `EncryptedCredentials` — credentials are replaced with `"••••"` in the response DTO.

**`Services/OrderService.cs`**  
In `TransformAsync`, after `_db.SaveChangesAsync(ct)` (artifact + status saved), add:
```csharp
await _deliveryService.DispatchIfAutoAsync(organisationId, entity.SupplierId, artifact, ct);
```
Dispatch failure does not roll back the transform — the artifact is always preserved. Order status is updated to `"dispatch_failed"` by `DeliveryService` if dispatch fails.

### `DeliveryAttempt` entity

Already exists in `ProcuLink.Core/Entities/DeliveryAttempt.cs`. The entity requires one migration change before use: `OrderId` must become nullable (`Guid?`) to support test-fire rows that are not linked to a real order. The `Order` navigation property becomes optional too.

Group D2 populates it with:
- `OrgId`
- `OrderId` — `null` for test-fire, set for real dispatches
- `Channel` — `"http"` / `"sftp"` / `"ftp"` (maps to our protocol concept)
- `Destination` — URL or host string from config
- `Status` — `"success"` or `"failed"`
- `ResponseCode` — HTTP status code for HTTP dispatches; null for SFTP/FTP
- `ErrorMessage` — null on success
- `AttemptedAt` — `DateTime.UtcNow`

`SupplierId` is not on the existing entity — supplier context is recovered via `Order.SupplierId` for real dispatches, or stored in `Destination` for test-fire rows.

---

## Frontend

### New component: `DeliveryConfigEditor`

`src/components/bridge/DeliveryConfigEditor.tsx`

**Layout:** Protocol pill tabs at top (HTTP / SFTP / FTP). Fields below swap based on selected protocol. Auto-deliver toggle. Test-fire button with inline result. Save and Delete buttons in footer.

**Protocol: HTTP fields:**
- Endpoint URL (text input)
- Auth type (dropdown: None / API Key / Bearer / Basic)
  - API Key: header name + value inputs
  - Bearer: token input
  - Basic: username + password inputs
- Extra headers (optional key/value pairs, add/remove rows)
- Timeout seconds (number input, default 30)

**Protocol: SFTP fields:**
- Host, Port (default 22)
- Remote path
- File name pattern (hint: `{poNumber}`, `{ext}`, `{date}`)
- Auth type (dropdown: Password / Private Key)
  - Password: username + password
  - Private Key: username + textarea for PEM key

**Protocol: FTP fields:**
- Host, Port (default 21)
- Remote path
- Use TLS toggle (default on)
- File name pattern
- Username + password

**Credential masking:** Existing saved credentials are displayed as `"••••••••"` (never returned from API). Editing the credential field replaces it. A "clear credential" icon lets users intentionally blank it.

**Test-fire result:** A status strip appears below the form after test-fire: green `✓ Success` or red `✗ <errorMessage>`. Strip disappears when the form is edited.

**Auto-deliver toggle:** Toggle switch. When on, a dim note: _"Artifact will be sent automatically after every successful transform."_

### Integration

`src/components/bridge/SupplierDockProfile.tsx`:
- Add `"delivery"` to `Tab` union
- Add `{ id: "delivery", label: "Delivery" }` to TABS array (after "PO Mapping")
- Add `DeliveryConfigEditor` panel for `activeTab === "delivery"`

`src/lib/api/delivery.ts`:
- `getDeliveryConfig(supplierId)` → `DeliveryConfig | null`
- `upsertDeliveryConfig(supplierId, config)` → `DeliveryConfig`
- `deleteDeliveryConfig(supplierId)` → `void`
- `testFireDelivery(supplierId)` → `{ success: boolean, errorMessage?: string }`

`src/lib/api/types.ts`:
- `DeliveryConfig`, `HttpDeliveryConfig`, `SftpDeliveryConfig`, `FtpDeliveryConfig`, `DeliveryCredentials` interfaces

---

## Error Handling

| Scenario | Behaviour |
|---|---|
| Dispatch network failure | `DeliveryAttempt(success=false, errorMessage=ex.Message)`. Order status → `"dispatch_failed"`. Transform artifact preserved. |
| HTTP 4xx/5xx | Same as above, `errorMessage` = `"HTTP {status}: {body}"` |
| SFTP/FTP auth failure | Same, `errorMessage` from SSH.NET / FluentFTP exception |
| Credential decrypt failure | Return 500 with generic message. Log full error. Never expose key material. |
| No delivery config + auto_deliver check | `DispatchIfAutoAsync` no-ops silently. Transform always succeeds. |
| Test-fire with no config saved | `404 Not Found` from the endpoint |

---

## Testing

| Component | Approach |
|---|---|
| `DeliveryEncryptionService` | Unit: encrypt→decrypt round-trip; wrong-key returns null |
| `HttpDeliveryDispatcher` | Unit: mock `HttpMessageHandler`; 200 = success, 422 = failure with message |
| `SftpDeliveryDispatcher` | Manual QA via test-fire only (requires live SFTP server) |
| `FtpDeliveryDispatcher` | Manual QA via test-fire only |
| `DeliveryService` | Unit: auto_deliver=true triggers dispatcher; auto_deliver=false skips; failure writes `DeliveryAttempt` |
| `DeliveryConfigService` | Unit: upsert encrypts credentials; get returns entity; delete is no-op if missing |
| API endpoints | Integration: org-scope guard rejects wrong org; GET redacts credentials |

---

## Order of Implementation

1. `SupplierDeliveryConfig` entity + EF migration
2. `DeliveryEncryptionService` + unit tests
3. `IDeliveryDispatcher` interface + `DeliveryResult` + `HttpDeliveryDispatcher` + unit tests
4. `SftpDeliveryDispatcher` + `FtpDeliveryDispatcher`
5. `IDeliveryConfigService` + `DeliveryConfigService` + unit tests
6. `IDeliveryService` + `DeliveryService` + unit tests
7. DI registration + 4 API endpoints on `SuppliersController`
8. `OrderService.TransformAsync` integration
9. Frontend: TypeScript types + `delivery.ts` API client
10. `DeliveryConfigEditor` component + supplier dock tab
11. Push both repos + update STATUS.md
