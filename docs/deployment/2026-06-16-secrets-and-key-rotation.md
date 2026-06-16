# Secrets & encryption-key runbook (2026-06-16)

Grounded from the trust/reliability audit. Records the current (good) posture and the ONE
operation that needs a plan + sign-off: AES key rotation.

## Current posture — GOOD

- **No secret is committed.** `Delivery:EncryptionKey` is an **empty string** in
  `appsettings.json` / `appsettings.Development.json`; the real 32-byte AES-256 key is injected at
  runtime via the `DELIVERY__ENCRYPTIONKEY` env var (Railway). `DeliveryEncryptionService` **throws on
  startup** if the key is absent or not a valid 32-byte base64 — so a misconfigured deploy fails loud,
  it never runs with a weak/blank key. (The historical all-zero-key-in-git issue was fixed earlier; it
  is NOT present today.)
- **Rate-limit middleware order is correct** — `UseRateLimiter()` runs after `UseAuthentication()` so
  the limiter partitions by the authenticated `sub` / `apikey:{id}` claim, falling back to IP for
  unauthenticated ingress. No change needed (the SEC-1 concern was a false alarm on close read).
- **Delivery reliability is robust** — `RetryDeliveryJob` (max attempts 3, exponential 30→60→120 min
  backoff, scheduled not Hangfire-auto so counts don't double), 4xx stops retry (supplier rejection),
  120-min SLA window, `delivery_dead_letter` terminal state, `StuckDeliveryDetectionJob` recovery. No gaps.

## What encryption protects (the rotation blast radius)

The AES-256-GCM key (`DeliveryEncryptionService`, ciphertext = `base64(version[1]+nonce[12]+tag[16]+ct)`)
encrypts, with **no per-row key version**:

- `SupplierDeliveryConfig.EncryptedCredentials` (HTTP/SFTP/FTPS passwords, API keys, bearer tokens)
- `SupplierDeliveryConfig.EncryptedCxmlSharedSecret` (cXML signing secret)
- `S3IngressConfig.EncryptedSecretKey`, `SftpIngressConfig.EncryptedPassword` (catalog/order pull creds)
- `IntegrationSubscription.EncryptedSecret` (outbound webhook HMAC secret)

## ⚠️ AES key rotation — REQUIRES a re-encryption job + sign-off (do NOT just swap the env var)

Because the ciphertext carries no key version, **rotating the key makes every existing encrypted value
permanently undecryptable** → delivery, S3/SFTP pull, and webhook signing all break. A bare env-var swap
is a data-loss incident, not a rotation.

**Safe rotation procedure (when needed):**
1. Add a key-version byte / dual-key window: `DeliveryEncryptionService` accepts BOTH the old and new
   key (decrypt-tries-both), encrypts with the new.
2. Run a one-shot re-encryption job: for each encrypted column above, decrypt with old → re-encrypt with
   new → persist, inside a transaction, idempotent + resumable.
3. Verify a sample decrypts with the new key only; then remove the old key.
4. Schedule in a maintenance window; have a rollback (keep the old key until verified).

Until that job exists, treat the key as **non-rotatable**. If it must be rotated urgently (suspected
compromise), the dual-key + re-encryption job is the prerequisite — not optional.

## Deferred (product decisions, not safe to auto-build)

- **RBAC** — today every org member is fully privileged within their org (cross-tenant isolation is
  enforced; cross-org admin is allowlist-gated via `AdminOnlyAttribute`). Per-member roles/permissions
  slot into `TenantResolutionMiddleware` (resolve `MemberRole`/`Permissions` after org) + `[RequireRole]`
  attributes — but this needs the multi-user-org product shape decided first.
- **API-key scoping** — keys are org-level (full access); fine-grained scopes are a future feature.

## Operator visibility (shipped alongside this)

`GET /api/ops/job-failures` exposes recent Hangfire job failures (id, job, exception, failed-at) via
`IMonitoringApi`, so an operator can diagnose a stuck/failing worker without SSHing into the Hangfire
Postgres. Complements the existing `/api/ops/health`, dead-letter, heartbeat, and `WorkerHealthAlertJob`.
