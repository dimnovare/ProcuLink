# Catalog source API — quirks and the add-a-vendor recipe

**Status:** durable. Lifted 2026-08-09 out of a live vendor-feed QA transcript that was deleted
because it carried live operational data (vendor hostnames, resolved addresses, service banners,
an account-keyed feed URL, production supplier record ids and confidential dealer pricing). This
repository is public; nothing below names a counterparty or an endpoint.

Everything here was found by hitting the endpoint, not by reading the controller. The first item
costs a confusing round-trip if you build the payload from the controller source alone.

## API quirks worth knowing before you build a payload

1. **`Host` and `RemotePath` are required even for protocols that do not use them.**
   `UpsertCatalogSourceRequest` is a positional record with non-nullable `string Host` and
   `string RemotePath` (`ICatalogSourceSettingsService.cs:36-55`), so ASP.NET's implicit
   required-ness for non-nullable reference types rejects `null` — *before* the controller's
   protocol-aware branch ever runs. The XML doc comment says host/port/remote-path are "unused"
   for http/https, which is true of the *logic* and misleading about the *payload*.
   **Send `"host":"", "remotePath":"", "port":0`** — empty strings pass, because implicit-required
   rejects null only. A working https source stores exactly `host:""`, `remotePath:""`, `port:0`.

2. **Validation order** (`SuppliersController.cs:865-985`): protocol → file format →
   column-mapping targets → URL-or-host branch → password → **billing gate** → SSRF guard → save.
   The password check precedes the SSRF check, so an SFTP host cannot be SSRF-validated without
   also supplying a password.

3. **Password requirements differ by protocol.** `sftp`/`ftps` require **both** username and
   password at save; plain `ftp` requires neither; the vendor-specific pull protocol requires
   **all four** of its vendor fields, either provided now or already stored. This is why a plain
   FTP source can be pre-staged credential-free and the others cannot.

4. **Secret semantics on PUT**: `null` = keep stored, `""` = clear, a value = re-encrypt
   (AES-256-GCM). Omitting a secret field on a later edit is safe.

5. **Credentials in the URL are rejected** — `Uri.UserInfo` → `400 credentials_in_url_not_allowed`.
   Basic auth goes in `authConfig`, never in `https://user:pass@host/...`.

6. **Column-mapping targets are whitelisted**: `code, name, unit, price, currency, barcode,
   external_id`, plus the `__noheader__` / `__encoding__` directive keys (whose values are
   free-form). Any other target → `400`.

7. **Enabling needs Growth or above** — `BillingFeature.SftpIngestion`. An admin limits override
   does **not** help: it writes order/supplier/trial caps only, never `Plan`.

8. **Catalog list params are `?q=` and `?take=`** — *not* `search`, `page`, or `pageSize`. Unknown
   params are silently ignored, so a wrong name looks like "pagination is broken". `total` is the
   **unfiltered** count by design.

## How to add the next vendor — recipe

Steps 3–4 are the founder's; everything else is agent-safe.

1. **Create or locate the supplier** — `POST /api/suppliers`, or the Suppliers page. Watch the
   supplier cap (`suppliersUsed` / `supplierLimit` in `GET /api/billing/status`).

2. **Pre-stage the credential-free config** (agent-safe) — `PUT /api/suppliers/{id}/catalog/source`
   with `isEnabled:false` and no secret fields. Include host/path/URL, `fileFormat:"auto"`,
   `syncIntervalHours`, and whatever column mapping is already known. A `200` here also proves the
   host clears the SSRF guard. Skip this step for the protocols in quirk 3 whose save paths reject
   a secret-less body.

3. **Founder pastes the secrets** — Supplier → **Catalog** tab → import source editor. Leaving a
   secret field blank keeps the stored one, so re-editing later never needs a re-paste.
   Never paste vendor credentials into an agent transcript or a chat window.

4. **Test-fetch — read-only, and do this BEFORE enabling** —
   `POST /api/suppliers/{id}/catalog/source/test-fetch`. It runs the real production pull path
   (SSRF guard, timeouts, bounded read, shared parser) and **writes nothing**. Require all of:
   - `ok:true`
   - `rowsWithCode > 0`
   - `mappedFields` contains **`code`, `name`, and `price`** — not just `code` and `price`.
     *This is the step that catches the silent code-only import.* If `name` is missing, read
     `headerColumns` from the same response, add the mapping, and re-run.
   - `unmappedColumns` reviewed — anything useful still unmapped gets mapped now.
   - Eyeball the ≤5 `sampleRows` against the vendor portal. Comma decimals (`130,41` → `130.41`)
     and umlauts are the two things that most often go wrong.

5. **Headerless / encoded feeds**: use positional keys (`{"3":"code"}`) plus `__noheader__`, and
   `__encoding__` for anything that is not UTF-8 (cp1252 is common). ZIP archives are unpacked by
   the fetcher; map against the *inner* file's columns.

6. **Enable** — `PUT …/catalog/source` with `isEnabled:true`. A `false→true` flip returns
   `syncEnqueued:true` and runs the first sync immediately; after that the hourly `catalog-sync`
   dispatcher re-runs it every `syncIntervalHours`.

7. **Verify rows landed** — `GET /api/suppliers/{id}/catalog?take=1` → `total`, and check
   `lastSyncStatus=ok` / `lastSyncError=null` on the source.

8. **Spot-check names AND prices at scale** — query 2–3 brands with `?q=<brand>&take=200` and
   count rows carrying **both** a name and a price. A code-only import looks successful everywhere
   else; this is the only cheap check that catches it.

9. **Re-check after the second scheduled run.** The first pull proves the parse; the *second*
   proves idempotent upsert. Expect `created≈0, updated≈total` and an unchanged catalog total —
   a rising total means the dedupe key is wrong.
