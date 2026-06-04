# Supplier-setup Trust Bundle — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make supplier setup credible for the first paying client — real SFTP/FTPS/email delivery + OAuth2 token-fetch HTTP, understandable validation rules, supplier deletion, less cruft, and honest marketing — under one rule: every import/delivery channel we **offer** must **work**.

**Architecture:** Almost entirely frontend wiring over an already-working backend (dispatchers, delete endpoint, acceptance API all exist + tested). The single backend change is a new OAuth2 client-credentials auth mode in `HttpDeliveryDispatcher`. No DB migrations.

**Tech Stack:** Backend ASP.NET Core 8 / xUnit / Moq / FluentAssertions. Frontend Next.js 15 / TypeScript / TanStack Query / `bun`. Spec: `docs/superpowers/specs/2026-06-04-supplier-setup-trust-bundle-design.md`.

**Repos:** Backend `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink` (branch `feat/supplier-setup-trust-bundle`). Frontend `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink` (create branch `feat/supplier-setup-trust-bundle`). **Frontend writes must be done by the main session — backend-scoped subagents cannot write to the frontend repo.**

---

## File structure

**Backend (this repo):**
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs` — OAuth2 auth mode.
- Modify: `ProcuLink.Infrastructure.Tests/Services/Dispatchers/HttpDeliveryDispatcherTests.cs` — 2 OAuth2 tests + routing handler.

**Frontend:**
- Modify: `src/lib/api/types.ts:46` — `DeliveryProtocol` union.
- Modify: `src/components/bridge/DeliveryConfigEditor.tsx` — ② multi-channel + ②b OAuth2 (bulk of work).
- Modify: `src/components/bridge/SupplierDockProfile.tsx` — ③ validation clarity, ④ button removal, ⑤ delete.
- Modify: `src/components/orders/FileUploadZone.tsx` — ⑦ upload copy.
- Modify: `src/app/(marketing)/how-it-works/page.tsx` — ⑦ claims.
- Modify: `src/app/(marketing)/help/page.tsx` — ⑦ claims.

---

## Phase A — Backend: OAuth2 fetch-token HTTP auth (②b)

**Files:**
- Modify: `ProcuLink.Infrastructure.Tests/Services/Dispatchers/HttpDeliveryDispatcherTests.cs`
- Modify: `ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs`

### Task A1: Failing tests for OAuth2 token-fetch

- [ ] **Step 1: Add a routing test handler + two tests** to `HttpDeliveryDispatcherTests.cs`.

Add this file-scoped helper next to the existing handlers (bottom of file):

```csharp
file sealed class RoutingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(route(request));
}
```

Add these two tests inside the `HttpDeliveryDispatcherTests` class:

```csharp
[Fact]
public async Task Dispatch_OAuth2_FetchesTokenThenSendsBearer()
{
    string? deliveryAuth = null;
    var handler = new RoutingHttpMessageHandler(req =>
    {
        if (req.RequestUri!.AbsoluteUri.Contains("/oauth/token"))
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"access_token\":\"tok-123\",\"expires_in\":3600}") };
        deliveryAuth = req.Headers.Authorization?.ToString();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
    });
    var factory = new Moq.Mock<IHttpClientFactory>();
    factory.Setup(f => f.CreateClient("delivery")).Returns(new HttpClient(handler));

    var dispatcher = new HttpDeliveryDispatcher(factory.Object, MakePermissiveGuard(), NullLogger<HttpDeliveryDispatcher>.Instance);
    var config = MakeConfig("https://supplier.example/orders");
    var creds = JsonSerializer.Serialize(new
    {
        type = "oauth2_client_credentials",
        tokenUrl = "https://supplier.example/oauth/token",
        clientId = "cid", clientSecret = "secret", scope = "orders.write",
    });

    var result = await dispatcher.DispatchAsync(
        Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, creds, default);

    result.Success.Should().BeTrue();
    deliveryAuth.Should().Be("Bearer tok-123");
}

[Fact]
public async Task Dispatch_OAuth2_TokenEndpoint401_FailsWithoutDelivering()
{
    var deliveryCalled = false;
    var handler = new RoutingHttpMessageHandler(req =>
    {
        if (req.RequestUri!.AbsoluteUri.Contains("/oauth/token"))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("nope") };
        deliveryCalled = true;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
    });
    var factory = new Moq.Mock<IHttpClientFactory>();
    factory.Setup(f => f.CreateClient("delivery")).Returns(new HttpClient(handler));

    var dispatcher = new HttpDeliveryDispatcher(factory.Object, MakePermissiveGuard(), NullLogger<HttpDeliveryDispatcher>.Instance);
    var config = MakeConfig("https://supplier.example/orders");
    var creds = JsonSerializer.Serialize(new
    {
        type = "oauth2_client_credentials",
        tokenUrl = "https://supplier.example/oauth/token",
        clientId = "cid", clientSecret = "secret",
    });

    var result = await dispatcher.DispatchAsync(
        Encoding.UTF8.GetBytes("data"), "order.csv", "text/csv", config, creds, default);

    result.Success.Should().BeFalse();
    result.ErrorMessage.Should().Contain("OAuth token request failed: HTTP 401");
    deliveryCalled.Should().BeFalse();
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~HttpDeliveryDispatcherTests"`
Expected: the two new `OAuth2` tests FAIL (no oauth2 case yet → no Bearer header set; on 401 delivery still called and returns 200 → success true).

### Task A2: Implement the OAuth2 auth mode

- [ ] **Step 3: Make auth async + add the OAuth2 branch** in `HttpDeliveryDispatcher.cs`.

In `DispatchAsync`, move the timeout/`requestCt` block above the auth call and replace the `ApplyAuth(request, creds);` line. The middle of `DispatchAsync` becomes:

```csharp
var client  = _httpClientFactory.CreateClient("delivery");
var request = new HttpRequestMessage(
    new HttpMethod(string.IsNullOrWhiteSpace(httpCfg.Method) ? "POST" : httpCfg.Method),
    endpoint);

using var timeoutCts = httpCfg.TimeoutSeconds is > 0
    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
    : null;
timeoutCts?.CancelAfter(TimeSpan.FromSeconds(httpCfg.TimeoutSeconds!.Value));
var requestCt = timeoutCts?.Token ?? ct;

// Apply auth (oauth2 mode fetches a fresh token first)
var authError = await ApplyAuthAsync(request, creds, client, requestCt);
if (authError is not null)
    return new DeliveryResult(false, authError);

// Apply extra headers
if (httpCfg.Headers is not null)
    foreach (var (k, v) in httpCfg.Headers)
        request.Headers.TryAddWithoutValidation(k, v);

request.Content = new ByteArrayContent(content);
request.Content.Headers.ContentType =
    MediaTypeHeaderValue.TryParse(contentType, out var mt) ? mt : new MediaTypeHeaderValue("application/octet-stream");

var response = await client.SendAsync(request, requestCt);
```

(Delete the old standalone `timeoutCts`/`requestCt` block that previously sat just before `SendAsync`, and the old `ApplyAuth(request, creds);` call.)

- [ ] **Step 4: Replace `ApplyAuth` with `ApplyAuthAsync` + add `FetchOAuthTokenAsync`.** Replace the existing `private static void ApplyAuth(...)` method with:

```csharp
private async Task<string?> ApplyAuthAsync(HttpRequestMessage request, JsonElement creds, HttpClient client, CancellationToken ct)
{
    if (creds.ValueKind == JsonValueKind.Undefined) return null;

    var type = creds.TryGetProperty("type", out var t) ? t.GetString() : "none";
    switch (type)
    {
        case "apikey":
            if (creds.TryGetProperty("header", out var h) &&
                creds.TryGetProperty("value", out var v) &&
                !string.IsNullOrWhiteSpace(h.GetString()))
                request.Headers.TryAddWithoutValidation(h.GetString()!, v.GetString());
            break;

        case "bearer":
            if (creds.TryGetProperty("token", out var token) &&
                !string.IsNullOrWhiteSpace(token.GetString()))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.GetString());
            break;

        case "basic":
            if (creds.TryGetProperty("username", out var username) &&
                creds.TryGetProperty("password", out var password))
            {
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username.GetString()}:{password.GetString()}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
            }
            break;

        case "oauth2_client_credentials":
            var (oauthToken, oauthError) = await FetchOAuthTokenAsync(creds, client, ct);
            if (oauthError is not null) return oauthError;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oauthToken);
            break;
    }

    return null;
}

private async Task<(string? token, string? error)> FetchOAuthTokenAsync(JsonElement creds, HttpClient client, CancellationToken ct)
{
    var tokenUrl = creds.TryGetProperty("tokenUrl", out var u) ? u.GetString() : null;
    if (string.IsNullOrWhiteSpace(tokenUrl))
        return (null, "OAuth token URL is missing from delivery credentials.");

    // SSRF guard the token URL — same protection as the delivery URL.
    var guard = await _guard.ValidateAsync(tokenUrl, ct);
    if (!guard.Allowed)
        return (null, $"OAuth token request blocked: {guard.Reason}");

    string Get(string name) => creds.TryGetProperty(name, out var e) ? e.GetString() ?? "" : "";
    var clientId     = Get("clientId");
    var clientSecret = Get("clientSecret");
    var scope        = Get("scope");
    var grantType    = string.IsNullOrWhiteSpace(Get("grantType")) ? "client_credentials" : Get("grantType");
    var authStyle    = string.IsNullOrWhiteSpace(Get("authStyle")) ? "body" : Get("authStyle");
    var requestStyle = string.IsNullOrWhiteSpace(Get("requestStyle")) ? "form" : Get("requestStyle");
    var tokenPath    = string.IsNullOrWhiteSpace(Get("tokenResponsePath")) ? "access_token" : Get("tokenResponsePath");
    var useBasic     = string.Equals(authStyle, "basic", StringComparison.OrdinalIgnoreCase);

    var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
    if (useBasic)
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

    if (string.Equals(requestStyle, "json", StringComparison.OrdinalIgnoreCase))
    {
        var payload = new Dictionary<string, string> { ["grant_type"] = grantType };
        if (!string.IsNullOrWhiteSpace(scope)) payload["scope"] = scope;
        if (!useBasic) { payload["client_id"] = clientId; payload["client_secret"] = clientSecret; }
        tokenRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    }
    else
    {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", grantType) };
        if (!string.IsNullOrWhiteSpace(scope)) form.Add(new("scope", scope));
        if (!useBasic) { form.Add(new("client_id", clientId)); form.Add(new("client_secret", clientSecret)); }
        tokenRequest.Content = new FormUrlEncodedContent(form);
    }

    HttpResponseMessage resp;
    try { resp = await client.SendAsync(tokenRequest, ct); }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "OAuth token request failed before a response.");
        return (null, "OAuth token request failed before a response was received.");
    }

    var bodyStr = await resp.Content.ReadAsStringAsync(ct);
    if (!resp.IsSuccessStatusCode)
        return (null, $"OAuth token request failed: HTTP {(int)resp.StatusCode}.");

    try
    {
        using var doc = JsonDocument.Parse(bodyStr);
        var el = doc.RootElement;
        foreach (var seg in tokenPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(seg, out el))
                return (null, $"OAuth token response did not contain a token at '{tokenPath}'.");

        var resolved = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        return string.IsNullOrWhiteSpace(resolved)
            ? (null, $"OAuth token response did not contain a token at '{tokenPath}'.")
            : (resolved, null);
    }
    catch (JsonException)
    {
        return (null, "OAuth token response was not valid JSON.");
    }
}
```

- [ ] **Step 5: Run the dispatcher tests — verify pass**

Run: `dotnet test ProcuLink.Infrastructure.Tests/ProcuLink.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~HttpDeliveryDispatcherTests"`
Expected: all HttpDeliveryDispatcher tests PASS (existing 6 + 2 new).

- [ ] **Step 6: Run the full backend suite — no regressions**

Run: `dotnet test ProcuLink.slnx --no-restore`
Expected: all green (≥ prior 715 + 2).

- [ ] **Step 7: Commit**

```bash
git add ProcuLink.Infrastructure/Services/Dispatchers/HttpDeliveryDispatcher.cs ProcuLink.Infrastructure.Tests/Services/Dispatchers/HttpDeliveryDispatcherTests.cs
git commit -m "feat(delivery): OAuth2 client-credentials fetch-token HTTP auth"
```

---

## Phase B — Frontend: real SFTP / FTPS / Email delivery + OAuth2 UI (② + ②b)

First: `cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink && git checkout -b feat/supplier-setup-trust-bundle`.

**File:** `src/lib/api/types.ts`, `src/components/bridge/DeliveryConfigEditor.tsx`

### Task B1: Extend the `DeliveryProtocol` union

- [ ] **Step 1:** In `src/lib/api/types.ts:46` change:
```ts
export type DeliveryProtocol = "http" | "sftp" | "ftps" | "smtp" | "erp_erply" | "erp_directo";
```
(Drop the dead `"ftp"`; add `"ftps"` and `"smtp"`.)

- [ ] **Step 2: Type-check**: `bun run build` — expect a compile error in `DeliveryConfigEditor.tsx` at the `PROTOCOLS` entry using `id: "ftp"` (proves the dead id was in use). That error is fixed in B2.

### Task B2: Protocol list + protocol-aware state in `DeliveryConfigEditor.tsx`

- [ ] **Step 3:** Replace the `PROTOCOLS` constant (lines ~20-26) with:
```ts
const PROTOCOLS: Array<{ id: DeliveryProtocol; label: string; enabled: boolean }> = [
  { id: "http", label: "HTTP", enabled: true },
  { id: "sftp", label: "SFTP", enabled: true },
  { id: "ftps", label: "FTPS", enabled: true },
  { id: "smtp", label: "Email (SMTP)", enabled: true },
  { id: "erp_erply", label: "Erply ERP", enabled: true },
  { id: "erp_directo", label: "Directo ERP", enabled: true },
];
```

- [ ] **Step 4:** Add state for the new fields (next to existing `useState`s):
```ts
const [host, setHost] = useState("");
const [port, setPort] = useState<number | "">("");
const [remotePath, setRemotePath] = useState("");
const [makeDirectories, setMakeDirectories] = useState(true);
const [sftpAuthMode, setSftpAuthMode] = useState<"password" | "key">("password");
const [privateKey, setPrivateKey] = useState("");
const [privateKeyPassphrase, setPrivateKeyPassphrase] = useState("");
const [allowInvalidCertificate, setAllowInvalidCertificate] = useState(false);
const [useSsl, setUseSsl] = useState(false);
const [fromAddress, setFromAddress] = useState("");
const [toAddresses, setToAddresses] = useState("");
```
Defaults for port on protocol change: set 22 (sftp) / 21 (ftps) / 587 (smtp) when switching protocol and port is empty.

- [ ] **Step 5: `bun run build`** — expect clean compile (no behavioural change yet for new protocols beyond enabling them).

### Task B3: Protocol-aware field rendering

- [ ] **Step 6:** In the form body (the grid currently rendering Endpoint URL / Method / Erply / Directo / Timeout), render fields by protocol:
  - `http`/`erp_erply`/`erp_directo`: **unchanged** (URL [+ method/clientCode/database], timeout).
  - `sftp`: Host, Port, Remote path, Make directories (checkbox), Timeout. Auth block: a Password ⇄ Private key toggle → password shows Username+Password; key shows Username + Private key (textarea) + Passphrase (optional).
  - `ftps`: Host, Port, Remote path, Make directories, Timeout, Allow invalid certificate (checkbox + warning "self-signed/expired only — leave off for public CAs"), Username, Password.
  - `smtp`: Host, Port, Use SSL (checkbox), From address, Recipients (comma-separated), Timeout. Advanced `<details>` disclosure: Subject template, Body template, Attachment file name (note `{poNumber}` / `{fileName}` tokens).
  Reuse the existing `<Field label>` component and existing input styling. Gate the existing Authentication card to `protocol === "http"` (sftp/ftps/smtp carry their own credential inputs; erp_directo keeps its current behaviour).

- [ ] **Step 7: `bun run build`** — clean.

### Task B4: Build/validate/hydrate per protocol

- [ ] **Step 8:** Replace `buildConfigObject()` so it branches by protocol (camelCase keys must match the dispatcher POCOs exactly):
```ts
function buildConfigObject() {
  if (protocol === "erp_erply") return { url, clientCode: erplyClientCode, timeoutSeconds };
  if (protocol === "erp_directo") return { url, database: directoDatabase, timeoutSeconds };
  if (protocol === "sftp") return { host, port: Number(port) || 22, remotePath, makeDirectories, timeoutSeconds };
  if (protocol === "ftps") return { host, port: Number(port) || 21, remotePath, makeDirectories, timeoutSeconds, allowInvalidCertificate };
  if (protocol === "smtp") return {
    host, port: Number(port) || 587, useSsl, fromAddress, toAddresses, timeoutSeconds,
    ...(subjectTemplate ? { subjectTemplate } : {}),
    ...(bodyTemplate ? { bodyTemplate } : {}),
    ...(attachmentFileName ? { attachmentFileName } : {}),
  };
  return { url, method, timeoutSeconds }; // http
}
```
(Add `subjectTemplate`/`bodyTemplate`/`attachmentFileName` state if the advanced disclosure is built; otherwise omit them.)

- [ ] **Step 9:** Extend `buildCredentialsJson()` with branches BEFORE the http auth-type logic:
```ts
if (protocol === "sftp") {
  if (sftpAuthMode === "key") {
    if (!privateKey && hasSavedCredentials) return null;
    return JSON.stringify({ username: basicUsername, privateKey, privateKeyPassphrase });
  }
  if (!basicPassword && hasSavedCredentials) return null;
  return JSON.stringify({ username: basicUsername, password: basicPassword });
}
if (protocol === "ftps" || protocol === "smtp") {
  if (!basicPassword && hasSavedCredentials) return null;
  return JSON.stringify({ username: basicUsername, password: basicPassword });
}
```
Keep the existing erp_directo / http (none/apikey/bearer/basic/oauth2 — see B5) logic after these.

- [ ] **Step 10:** Replace `canSave`:
```ts
const canSave =
  protocol === "sftp" || protocol === "ftps" ? Boolean(host) :
  protocol === "smtp" ? Boolean(host) && Boolean(fromAddress) && Boolean(toAddresses.trim()) :
  Boolean(url) && (protocol !== "erp_directo" || Boolean(directoDatabase));
```

- [ ] **Step 11:** Extend `hydrateConfig()` to also read host/port/remotePath/makeDirectories/useSsl/fromAddress/toAddresses/allowInvalidCertificate/subject/body/attachment from the parsed config for the matching protocol, so an existing config round-trips into the form.

- [ ] **Step 12: `bun run build`** — clean. The on-screen JSON preview (`configPreview`) now shows the correct shape per protocol.

### Task B5: OAuth2 "fetch token first" auth mode (HTTP)

- [ ] **Step 13:** Add `"oauth2"` to the `AuthType` union and the HTTP auth-type `<select>` (label "OAuth2 — fetch token first"). Add state: `tokenUrl, oauthClientId, oauthClientSecret, oauthScope` (primary) and `oauthGrantType="client_credentials", oauthRequestStyle="form", oauthAuthStyle="body", oauthTokenPath="access_token"` (advanced `<details>`).

- [ ] **Step 14:** In `buildCredentialsJson()` http branch, add:
```ts
if (authType === "oauth2") {
  if (!oauthClientSecret && hasSavedCredentials) return null;
  return JSON.stringify({
    type: "oauth2_client_credentials",
    tokenUrl, clientId: oauthClientId, clientSecret: oauthClientSecret,
    scope: oauthScope, grantType: oauthGrantType,
    authStyle: oauthAuthStyle, requestStyle: oauthRequestStyle,
    tokenResponsePath: oauthTokenPath,
  });
}
```
Render the OAuth2 fields when `protocol === "http" && authType === "oauth2"`: Token URL, Client ID, Client secret, Scope, + advanced disclosure for the four optional knobs.

- [ ] **Step 15: `bun run build`** — clean.

### Task B6: Verify ② end-to-end

- [ ] **Step 16:** Start/confirm dev stack (frontend `:8082`, API `:5223`, `PROCULINK_QA_BYPASS_AUTH=true`, `Delivery__EncryptionKey` set). On a supplier's Delivery tab: select SFTP → fill host/path/username/password → Save → reload → confirm it round-trips (config persisted, credential masked). Repeat for FTPS and Email. For HTTP OAuth2: select the mode, fill token URL + client id/secret, Save, reload, confirm round-trip. Verify via DOM/HTTP (not screenshots).
- [ ] **Step 17:** (If a throwaway SFTP/mailbox/token endpoint is available) Test-fire each and confirm a real result row. Otherwise note "needs real endpoint (founder)".
- [ ] **Step 18: Commit** (frontend repo):
```bash
git add src/lib/api/types.ts src/components/bridge/DeliveryConfigEditor.tsx
git commit -m "feat(delivery): real SFTP/FTPS/email channels + OAuth2 fetch-token in the editor"
```

---

## Phase C — Frontend: validation rules clarity (③)

**File:** `src/components/bridge/SupplierDockProfile.tsx` (the `AcceptanceTab`).

### Task C1: Plain-language explainer

- [ ] **Step 1:** Add an always-visible explainer at the top of the tab body:
> **How validation works.** Before an order is sent to this supplier, ProcuLink checks it against these rules. **Error** rules block delivery until they're fixed; **Warning** rules only flag and never block. Validation never changes the order — it's a gate.

And replace the empty-state copy's example line with: *e.g. Currency must be EUR (error) · Every line needs a supplier code (error).*

- [ ] **Step 2: `bun run build`** — clean.

### Task C2: Constrain Field to resolvable paths

- [ ] **Step 3:** Replace the free-text Field input with a per-scope `<select>`:
  - scope `order` → options `currency`, `buyerName`.
  - scope `line` → options `supplierItemCode`, `buyerItemCode`, `description`, `quantity`, `unitPrice`.
  When scope changes, reset fieldPath to the first valid option for that scope. Add a code comment: *"These must match EvaluateOrderField / EvaluateLineField in SupplierAcceptanceService.cs — adding a field requires updating both."*

- [ ] **Step 4: `bun run build`** — clean.

### Task C3: Align operators with the backend

- [ ] **Step 5:** Set the operator options to exactly (value → label): `required`→"is present", `equals`→"equals", `not_equals`→"does not equal", `in`→"is one of (comma list)", `contains`→"contains", `greater_than`→"greater than", `less_than`→"less than", `min`→"at least (≥)", `max`→"at most (≤)", `max_length`→"max length". (Adds the missing `in`/`min`/`max`.)

- [ ] **Step 6: `bun run build`** — clean.

### Task C4: "+ Add common rule" quick-pick

- [ ] **Step 7:** Add a quick-pick (button → small menu) that appends a prefilled rule. Templates (all use resolvable paths):
```ts
const QUICK_RULES = [
  { label: "Currency must be EUR",            scope: "order", fieldPath: "currency",         operator: "equals",       expectedValue: "EUR", severity: "error",   blockOnFail: true },
  { label: "Every line has a supplier code",  scope: "line",  fieldPath: "supplierItemCode", operator: "required",     expectedValue: "",    severity: "error",   blockOnFail: true },
  { label: "Quantity greater than 0",         scope: "line",  fieldPath: "quantity",         operator: "greater_than", expectedValue: "0",   severity: "error",   blockOnFail: true },
  { label: "Unit price is required",          scope: "line",  fieldPath: "unitPrice",        operator: "required",     expectedValue: "",    severity: "error",   blockOnFail: true },
  { label: "Every line has a description",    scope: "line",  fieldPath: "description",      operator: "required",     expectedValue: "",    severity: "warning", blockOnFail: false },
];
```
Each click pushes a new draft rule into the editor's rules array (entering edit mode if not already).

- [ ] **Step 8: `bun run build`** — clean; dev-stack check: open a supplier's Validation rules tab, click each quick rule, confirm it adds a well-formed row; Save draft; Activate.
- [ ] **Step 9: Commit**:
```bash
git add src/components/bridge/SupplierDockProfile.tsx
git commit -m "feat(acceptance): plain-language validation help, scoped field picker, aligned operators, quick-add rules"
```

---

## Phase D — Frontend: ④ remove redundant button + ⑤ delete supplier

**File:** `src/components/bridge/SupplierDockProfile.tsx`

### Task D1: Remove the "Configure delivery" button

- [ ] **Step 1:** Delete the `<button>…Configure delivery…</button>` (lines ~651-663). Leave the Delivery tab in the tab bar as the single entry point.
- [ ] **Step 2: `bun run build`** — clean.

### Task D2: Delete supplier action + confirm dialog

- [ ] **Step 3:** Ensure imports: `useRouter` from `next/navigation`; `apiClient`; `useQueryClient` (already used as `qc`). Add state `const [confirmDelete, setConfirmDelete] = useState(false); const [deleting, setDeleting] = useState(false); const [deleteError, setDeleteError] = useState<string | null>(null);` and `const router = useRouter();`.

- [ ] **Step 4:** Add a low-prominence "Delete supplier" button in the profile header (right side, danger styling). On click → `setConfirmDelete(true)`.

- [ ] **Step 5:** Add a confirm dialog (reuse the app's existing modal/confirm pattern if present; otherwise a fixed-overlay panel) with copy:
> **Delete {supplierName}?** This removes it from your supplier list. Past orders are kept for audit. This can't be undone here.

Confirm handler:
```ts
async function doDelete() {
  setDeleting(true); setDeleteError(null);
  try {
    await apiClient.deleteSupplier(id);
    await qc.invalidateQueries({ queryKey: ["suppliers"] });
    router.push("/library/suppliers");
  } catch (e) {
    setDeleteError(e instanceof Error ? e.message : "Could not delete supplier.");
    setDeleting(false);
  }
}
```

- [ ] **Step 6: `bun run build`** — clean; dev-stack check: create a throwaway supplier, delete it, confirm it leaves the list (`/library/suppliers`) and a previously-created order still loads.
- [ ] **Step 7: Commit**:
```bash
git add src/components/bridge/SupplierDockProfile.tsx
git commit -m "feat(suppliers): delete supplier with confirm; remove redundant configure-delivery button"
```

---

## Phase E — Frontend: claims reconciliation copy (⑦)

Honesty rule: promise exactly what works; the `/library/standards` catalog stays the SoT.

### Task E1: Upload copy (honestly expand)

- [ ] **Step 1:** In `src/components/orders/FileUploadZone.tsx` (the human hint ~line 137), change "Supports PDF, CSV, XLS, XLSX" to reflect what we actually parse, e.g.: **"Supports CSV, XLSX, PDF — and structured formats: cXML, UBL, EDIFACT, X12."** (The `accept` attr already allows these; parsers are real per the audit.)
- [ ] **Step 2: `bun run build`** — clean.

### Task E2: how-it-works claims

- [ ] **Step 3:** In `src/app/(marketing)/how-it-works/page.tsx`: the delivery step badges/copy may now honestly include **webhook, SFTP, FTPS, email, ERP** (all real + offered). For OUTPUT-format badges, align to the catalog: keep cXML / UBL / CSV / JSON; **remove or mark "planned"** EDIFACT and X12 outbound (no production transformer). Keep the import "any format" claim only if paired with the real list; otherwise soften to "PDF, XLSX, CSV, cXML, UBL, EDIFACT, X12."
- [ ] **Step 4: `bun run build`** — clean.

### Task E3: help page claims

- [ ] **Step 5:** In `src/app/(marketing)/help/page.tsx` (~line 21) the "HTTP webhook, email, and ERP connectors" line is now TRUE (email shipped in Phase B) — keep it, and optionally add SFTP/FTPS. Ensure nothing else claims an unbuilt capability.
- [ ] **Step 6: `bun run build`** — clean; dev-stack check the three pages render.
- [ ] **Step 7: Commit**:
```bash
git add src/components/orders/FileUploadZone.tsx "src/app/(marketing)/how-it-works/page.tsx" "src/app/(marketing)/help/page.tsx"
git commit -m "docs(marketing): reconcile import/delivery claims with real capability (offer == works)"
```

---

## Phase F — Verification bar (offer ⇔ works)

### Task F1: Upload-format parser coverage

- [ ] **Step 1:** Confirm `ProcuLink.Transform.Tests` has a passing parse test for each accepted upload format (csv, xlsx, pdf-text, cxml, ubl, edifact, x12). Run: `dotnet test ProcuLink.Transform.Tests/ProcuLink.Transform.Tests.csproj --no-restore`. If any accepted format lacks a representative test, add a minimal one (a tiny fixture → assert ≥1 parsed line). List the channel→test mapping.

### Task F2: Delivery-protocol coverage

- [ ] **Step 2:** Confirm each offered delivery protocol has dispatcher tests green: http (incl. the new oauth2), sftp, ftps, smtp. Run the four dispatcher test classes. For erp_erply/erp_directo, confirm the dispatcher→connector path is exercised (existing tests) and note the live ERP call is founder-side.

### Task F3: Final reconciliation checklist + STATUS

- [ ] **Step 3:** Write a short "channel truth matrix — verified" table (offered channel → passing test / honest "needs real endpoint" Test-fire). Confirm NOTHING offered in the UI lacks a backing verification. Update `STATUS.md` with the bundle summary + the matrix result.
- [ ] **Step 4: Commit** (backend repo): `git add STATUS.md && git commit -m "docs(status): supplier-setup trust bundle + verified channel matrix"`.

---

## Self-review notes
- **Spec coverage:** ②→Phase B; ②b→Phase A (backend) + B5 (UI); ③→Phase C; ④→D1; ⑤→D2; ⑦→Phase E; verification bar→Phase F. All covered.
- **Type consistency:** credential JSON keys (`username`/`password`/`privateKey`/`privateKeyPassphrase`, smtp `fromAddress`/`toAddresses`, oauth `tokenUrl`/`clientId`/`clientSecret`/`tokenResponsePath`) match the dispatcher POCOs read in the spec. `DeliveryProtocol` adds `ftps`+`smtp`. Suppliers query key `["suppliers"]`; supplier list route `/library/suppliers`.
- **No DB migrations.** Backend change is dispatcher-only.
