# Binding encrypted credentials to their tenant and their purpose

Date: 2026-08-06
Status: design approved, not yet implemented

## Problem

`DeliveryEncryptionService` encrypts with AES-256-GCM using the overloads that take no
associated data:

- `ProcuLink.Infrastructure/Services/DeliveryEncryptionService.cs:44` — `aes.Encrypt(nonce, plaintextBytes, ciphertext, tag)`
- `ProcuLink.Infrastructure/Services/DeliveryEncryptionService.cs:70` — `aes.Decrypt(nonce, ciphertext, tag, plaintextBytes)`

A ciphertext blob is therefore bound to the deployment key and to nothing else — not to an
organisation, not to a supplier, not to the kind of credential it is. Any blob that decrypts,
decrypts for every tenant. Nothing cryptographic stops one tenant's credentials from being used
under another tenant's supplier if a blob ever escapes.

PR #157 closed the one route by which a caller could supply such a blob (`credentialsRef` was
accepted straight off the connection-revision request body and is now refused), and read-back
masking means no client can obtain its own ciphertext through the API today. This is defence in
depth, not a live breach. The binding is the actual fix, and it was deliberately left out of #157
because it is a format change rather than a bolt-on.

## What the investigation found

Three findings changed the design away from the shape originally assumed.

### 1. The service is not delivery-only, and most call sites have no supplier

19 production call sites across 8 credential kinds. Organisation id is available at every one.
Supplier id is available at fewer than half.

| Call site | Owning row | org | supplier |
|---|---|:--:|:--:|
| `DeliveryConfigService.cs:100` Enc, `:133` Enc | SupplierDeliveryConfig | yes | yes |
| `DeliveryService.cs:171` Dec, `:756` Dec | SupplierDeliveryConfig | yes | yes |
| `CxmlCredentialResolver.cs:54` Dec | SupplierDeliveryConfig | yes | yes |
| `CatalogSourceSettingsService.cs:103,124,163` Enc | SupplierCatalogSource | yes | yes |
| `CatalogPullService.cs:312,496,556` Dec | SupplierCatalogSource | yes | yes |
| `IntegrationController.cs:67` Enc | IntegrationSubscription | yes | no |
| `FireIntegrationTriggerJob.cs:133` Dec | IntegrationSubscription | yes | no |
| `EmailSettingsService.cs:47` Enc | Organisation.EmailConfigJson | yes | no |
| `EmailPollOrgJob.cs:145` Dec | Organisation.EmailConfigJson | yes | no |
| `PullIngressSettingsService.cs:73,127` Enc | Sftp/S3IngressConfig | yes | no |
| `SftpIngressService.cs:103` Dec, `S3IngressService.cs:109` Dec | Sftp/S3IngressConfig | yes | no |

The ingress and email configurations do carry a `DefaultSupplierId`, but it is mutable routing
configuration rather than ownership. Binding associated data to it would invalidate every stored
blob the moment an operator changed the default supplier. It must not be used.

### 2. `CredentialsRef` is a verbatim byte-copy, and it is immutable

`SupplierConnectionRevision.CredentialsRef` (`ProcuLink.Core/Entities/SupplierConnectionRevision.cs:102`)
holds a byte-identical copy of `SupplierDeliveryConfig.EncryptedCredentials`:

- copied at `ConnectionBackfillService.cs:167` and `SupplierConnectionService.cs:483`
- decrypted through the same `DeliveryService.cs:171` call, via the detached snapshot view built
  in `ResolveEffectiveDeliveryConfigAsync`
- compared to the live blob by ordinal byte equality in `DeliverySnapshotMatches`
  (`SupplierConnectionService.cs:554`) to decide whether the live config has drifted from the
  active revision
- guarded in Postgres by `proculink_block_published_revision_content_update`
  (`ProcuLink.Infrastructure/Migrations/20260611182132_AddBlobRetentionSweep.cs:104`), which raises
  `P0001` when `NEW.credentials_ref IS DISTINCT FROM OLD.credentials_ref` on a published revision.
  Only `output_mapping_json` has a fill-only exemption; `credentials_ref` has none.

AES-GCM uses a random nonce, so re-encrypting the same plaintext always yields different bytes.
Re-encrypting the live blob alone breaks the drift comparison, which then reports "drifted"
permanently and spawns a redundant revision on every save. Re-encrypting the revision copy trips
the trigger. Published revisions are retained forever because orders pin to them.

Delivery credentials therefore cannot be re-encrypted, by backfill or lazily.

Two escape hatches were considered and rejected. Adding a `credentials_ref` exemption to the
trigger would let a genuine tamper through a guard that was built deliberately and is covered by
`PublishedRevisionImmutabilityPostgresTests`. Writing to a new column that the trigger does not
enumerate would be a loophole against an intentional invariant.

### 3. Two call sites already fail open

Eight of the ten decrypt call sites fail closed today, and their behaviour must be preserved
exactly (the `CatalogPullService` row below covers two sites):

| Site | Current behaviour on `null` |
|---|---|
| `DeliveryService.cs:171` | `FailBeforeDispatchAsync(… "Delivery credentials could not be decrypted.")` |
| `DeliveryService.cs:756` | `DeliveryTestResult(false, "Delivery credentials could not be decrypted.", null)` |
| `CatalogPullService.cs:312` | `throw new CatalogSyncException(ErrCredentialsUnreadable)` |
| `CatalogPullService.cs:496,556` | `throw new CatalogSyncException(ErrAuthConfigUnreadable)` |
| `SftpIngressService.cs:103` | log warning, `return 0` |
| `S3IngressService.cs:109` | log warning, `return 0` |
| `EmailPollOrgJob.cs:145` | log warning, `return` |

Two fail open:

- `FireIntegrationTriggerJob.cs:133` — the `if (secret is not null)` guard lets a null fall
  through, `sigHeader` stays null, and the webhook is POSTed **unsigned**, with no log and no
  failure recorded.
- `CxmlCredentialResolver.cs:54` — a null shared secret combined with present identity fields
  still returns a `CxmlCredentialConfig` carrying `sharedSecret: null`, and the transform proceeds.

Both predate this work. Associated data makes them materially worse: today a null means a wrong key
or a corrupt blob, but after this change it also means a mis-bound blob — exactly the attack case —
silently degrading to an unauthenticated send.

## Design

### Associated data

```
AAD = "proculink.cred.v2" ␠ orgId (32 hex) ␠ purpose (UTF-8) ␠ scopeId (32 hex)
```

`purpose` is a compile-time constant naming the credential kind. `scopeId` identifies the owning
record, or `Guid.Empty` for organisation-level singletons. The space separators keep the
concatenation unambiguous — no field can contain a space, since the purposes are dotted lowercase
and "N"-format Guids are hex only — so no two distinct tuples can produce the same byte string.
This binds
strictly more than tenant and supplier: a supplier's delivery credentials cannot be substituted for
that same supplier's cXML shared secret.

| Purpose | scopeId | Sites |
|---|---|---|
| `supplier.delivery.credentials` | supplierId | `DeliveryConfigService.cs:100`, `DeliveryService.cs:171,756` |
| `supplier.delivery.cxml_secret` | supplierId | `DeliveryConfigService.cs:133`, `CxmlCredentialResolver.cs:54` |
| `supplier.catalog.password` | `source.Id` | `CatalogSourceSettingsService.cs:163`, `CatalogPullService.cs:312` |
| `supplier.catalog.auth_config` | `source.Id` | `CatalogSourceSettingsService.cs:103,124`, `CatalogPullService.cs:496,556` |
| `org.integration.webhook_secret` | `subscription.Id` | `IntegrationController.cs:67`, `FireIntegrationTriggerJob.cs:133` |
| `org.email.imap_password` | `Guid.Empty` | `EmailSettingsService.cs:47`, `EmailPollOrgJob.cs:145` |
| `org.ingress.sftp_password` | `Guid.Empty` | `PullIngressSettingsService.cs:73`, `SftpIngressService.cs:103` |
| `org.ingress.s3_secret_key` | `Guid.Empty` | `PullIngressSettingsService.cs:127`, `S3IngressService.cs:109` |

`supplier.delivery.credentials` scopes on **supplierId, not the config row id**. This is what lets
the verbatim `CredentialsRef` copy keep decrypting: the live config and every revision snapshot
share the same `(orgId, supplierId)`, while their row ids differ. Scoping on the row id would break
delivery for every pinned order.

`IntegrationController.cs:67` encrypts before assigning `Id = Guid.NewGuid()`. The assignment moves
above the encrypt call.

### Signature

`Encrypt` and `Decrypt` take a `CredentialScope` value carrying org id, purpose, and scope id. The
overloads that take no scope are **deleted**, not deprecated, so an unbound call fails to compile.
That is a stronger guarantee than any test could give, and it costs nothing.

### Envelope and dual read

The stored format keeps its shape: `base64(version[1] + nonce[12] + tag[16] + ciphertext)`.

- `Encrypt` always writes version 2, with associated data.
- `Decrypt` reads version 1 (no associated data, for blobs written before this change) and
  version 2 (associated data required and verified). Any other version byte is rejected, as today.

### Backfill

An idempotent, EF-only service run at API boot, following the `ConnectionBackfillService` precedent
(`ProcuLink.Api/Program.cs:1059`). It re-encrypts version-1 blobs to version 2 for the seven columns
that nothing snapshots:

- `IntegrationSubscription.EncryptedSecret`
- `SftpIngressConfig.EncryptedPassword`
- `S3IngressConfig.EncryptedSecretKey`
- `Organisation.EmailConfigJson` → the nested `PasswordCiphertext` field, rewritten in place
- `SupplierCatalogSource.EncryptedPassword`
- `SupplierCatalogSource.AuthConfigEncrypted`
- `SupplierDeliveryConfig.EncryptedCxmlSharedSecret` — never snapshotted onto a revision, so it is
  safe to rewrite

Idempotency comes from the version byte: a blob already at version 2 is skipped. Dual read means a
concurrent reader is correct whether it sees the old or the new value, so there is no window to
coordinate.

**Deliberately excluded:** `SupplierDeliveryConfig.EncryptedCredentials` and every
`SupplierConnectionRevision.CredentialsRef`, for the reasons in finding 2.

### Residual: delivery credentials stay on version 1 until rotated

Existing delivery credentials migrate only when an operator next saves them. That path writes
version 2 to the live config *and* mints a new revision carrying the same version-2 bytes, so byte
identity holds, the drift comparison stays correct, and the immutability trigger is never involved.

Until then those blobs retain today's weaker property: bound to the deployment key and nothing
else. The version-1 read path for `supplier.delivery.credentials` is therefore permanent in
practice, not transitional, because published revisions pinned by orders are retained forever.

This is accepted knowingly. The other seven credential kinds gain the full property immediately,
and every newly-saved delivery credential is bound from the moment this ships. Prompting operators
to rotate delivery credentials is the remediation that would close the residual; it is out of scope
here and belongs with the frontend.

### Failure handling

`Decrypt` throws `CredentialUnbindableException` instead of returning null. The exception carries a
reason distinguishing a corrupt envelope, an unknown version, and a failed authentication tag
(wrong key or mis-bound associated data), so logs can tell an operational fault from a security
signal.

The distinction between "no credential stored" and "credential unreadable" is preserved: every site
already guards with `string.IsNullOrWhiteSpace` before calling `Decrypt`, and that stays.

The eight fail-closed sites gain a catch that reproduces today's behaviour and message strings
verbatim. The two fail-open sites change:

- `FireIntegrationTriggerJob.cs:133` — catch, log, `RecordFailureAsync`, then throw, mirroring the
  SSRF-guard block twelve lines below it. A webhook is never sent unsigned because its secret could
  not be read.
- `CxmlCredentialResolver.cs:54` — propagate rather than return a config with a null shared secret.
  The caller's error path must be checked during implementation; the existing "malformed config
  must never break the transform" tolerance at line 49 applies to unparseable JSON, not to an
  unreadable secret.

## Testing

Every test below is mutation-checked: the guard is removed, the test is confirmed red, and the
guard is restored **by editing the file**. `git checkout <file>` is never used — it has destroyed
uncommitted work in this repository before.

Both directions, as required:

- a blob encrypted for org A + supplier X decrypts for org A + supplier X
- the same blob does not decrypt for org B
- the same blob does not decrypt for supplier Y
- the same blob does not decrypt under a different purpose — a `supplier.delivery.credentials` blob
  presented as `supplier.delivery.cxml_secret` for the same supplier is refused

Version handling:

- a checked-in version-1 base64 fixture, generated under a fixed test key, decrypts through the
  dual-read path regardless of the scope presented. The fixture is a literal so the path is proven
  rather than assumed; it must not be produced by calling `Encrypt`.
- `Encrypt` always emits version byte 2
- an unknown version byte is refused

Backfill:

- converts version-1 to version-2 for each of the seven covered columns, including the nested
  `EmailConfigJson.PasswordCiphertext`
- is idempotent: a second run changes nothing
- leaves `EncryptedCredentials` and `CredentialsRef` untouched
- raises no `P0001`, and `DeliverySnapshotMatches` still reports a match for a live/revision pair
  after it runs — the regression guard for finding 2

Call sites:

- the two fail-open sites fail closed: an unbindable webhook secret records a failure and sends
  nothing; an unbindable cXML secret does not yield a config with a null secret
- each of the eight fail-closed sites keeps its exact current message

## Out of scope

- Renaming `DeliveryEncryptionService`, which now misdescribes what it holds
- Key rotation, which is a separate concern from binding
- An operator-facing surface listing suppliers whose delivery credentials are still on version 1
- Removing the version-1 read path
