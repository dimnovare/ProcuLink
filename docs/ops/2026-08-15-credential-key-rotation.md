# Credential encryption key rotation — what is possible, and what is not

Written 2026-08-15 alongside the downgrade guard
(`fix/v1-credential-ciphertext-is-scope-inert`). Read this before touching
`Delivery__EncryptionKey` on Railway.

## The short version

Rotation is now **possible but not completable in code**. The read path accepts a
retiring key, and the boot-time backfill drains every column it covers onto the new
key. Two columns can never be drained by any migration, so the retiring key must
stay configured until a human has re-saved every supplier's delivery config.

Do not remove the old key on the theory that "the backfill has run".

## Why a re-encrypt migration does not exist for delivery credentials

`SupplierDeliveryConfig.EncryptedCredentials` is byte-copied verbatim into
`SupplierConnectionRevision.CredentialsRef`. The two are compared by **ordinal byte
equality** (`SupplierConnectionService.DeliverySnapshotMatches`), and published
revisions are frozen by the
`proculink_block_published_revision_content_update` trigger.

AES-GCM uses a random nonce, so re-encrypting the same plaintext produces different
bytes. Therefore:

- re-encrypt the live row only → the pinned revision now differs → permanent
  "config drifted" reporting on every pinned order;
- re-encrypt the revision copy only → the trigger raises `P0001`;
- re-encrypt both → the revision is no longer the bytes that were published, which
  is the one thing revision pinning exists to guarantee.

The plaintext is not recoverable outside a decrypt, so there is no third option
where the migration writes the same ciphertext under a new key.

**The only way those two columns move is an operator opening the supplier's
Delivery tab and saving, which writes both sides together in one transaction.**

## Configuration

| Key | Required | Meaning |
|---|---|---|
| `Delivery__EncryptionKey` | yes | 32-byte base64. The **primary** key. Every write uses it. |
| `Delivery__PreviousEncryptionKey` | no | 32-byte base64. Read-only fallback for blobs written under the key being retired. |
| `Delivery__AllowUnboundLegacyCredentials` | no, default `false` | Emergency only. Restores the pre-guard behaviour in which a version-1 (unbound) envelope is accepted for **every** purpose. Re-opens the portable-ciphertext hole. |

A `Delivery__PreviousEncryptionKey` that is present but not 32 base64 bytes, or that
is identical to the primary, **fails the boot on purpose**. Silently ignoring it
looks exactly like a rotation that worked, right up until the first pre-rotation
credential fails in production.

## Runbook

Both API and Worker read these variables. Set them on **both** Railway services, and
never in a commit.

1. **Check the residual first.** Every boot logs one of:
   - `Credential binding: no delivery credentials remain in the unbound envelope.`
   - `Credential binding: N live delivery config(s) and M pinned revision copy(ies)
     are still in the unbound (version 1) envelope.`

   The second line is the count of credentials that are still tenant-portable. It is
   also the count of workspaces that will need a manual re-save during a rotation.

2. **Install the new key as primary, the old one as previous.** Set
   `Delivery__PreviousEncryptionKey` = the current key, then
   `Delivery__EncryptionKey` = the new key, in that order, then redeploy. Reversing
   the order leaves a window where nothing decrypts.

3. **Let the backfill drain the covered columns.** It runs on API boot and rewrites
   every blob that verified under the retiring key: webhook signing secrets, SFTP and
   S3 ingress secrets, IMAP passwords, catalog passwords and auth config, and cXML
   shared secrets. It is idempotent; a blob already under the primary key is left
   byte-identical.

4. **Drive the residual to zero by hand.** For every supplier counted in step 1, an
   operator saves the Delivery tab. Nothing else moves those two columns.

5. **Only when the boot log reports zero, and step 4 is complete for every
   workspace, remove `Delivery__PreviousEncryptionKey`.** Removing it earlier breaks
   delivery for whoever was not re-saved, and the plaintext is gone at that point.

## What is deliberately NOT built

- **No key id in the envelope.** The fallback is a trial decrypt: try the primary,
  then the previous. That costs one extra AES-GCM verification on a miss and keeps
  the on-disk format byte-identical, so an older running instance can still read
  everything a newer one writes. Adding a key-id byte would make the deploy one-way
  for no benefit at two keys.
- **No automatic retirement of the old key.** Nothing in the system can prove every
  delivery config has been re-saved, so nothing should act as if it can.
- **No re-encrypt of `EncryptedCredentials` / `CredentialsRef`.** See above. If this
  is ever wanted, it needs a schema change that lets a revision reference a
  credential by identity rather than by ciphertext bytes — at which point the byte
  comparison and the immutability trigger both stop being the obstacle. That is a
  founder-scoped decision, not a hardening fix.
