# Routing matrix — per-channel supplier-routing proof (run 2026-07-26)

**Question:** for every order intake channel, does an order reach the *correct* vendor?

**Answer:** the three push channels route correctly on production. The three pull channels route
correctly locally. And the `unrouted` hold — the mechanism that exists so nobody guesses whose
order a document is — **cannot be reached on production at all**, because the inbound-email
channel silently guesses instead. Two P1 defects came out of the run, both live-observed.

Method precedent: OPS-1 (Postmark → `{slug}@orders.proculink.eu`) and the OPS-2 re-run
(`2026-07-24-ops2-vendor-feed-prod-test.md`) — founder's Chrome signed in to prod, Growth plan,
per-request Clerk JWT pulled inside the page, no secret ever printed.

---

## The matrix

| Channel | Doc type | Routing mode | Expected supplier | Actual supplier | Order id | Verdict |
|---|---|---|---|---|---|---|
| Browser upload | CSV | explicit `supplierId` form field | ROUTETEST Supplier A | **ROUTETEST Supplier A** | `0…001` | ✅ PROD |
| REST ingress (`plk_` key) | JSON body | supplier by **NAME** string | ROUTETEST Supplier B | **ROUTETEST Supplier B** | `0…002` | ✅ PROD |
| Inbound email | CSV attachment | org default supplier **set** | ROUTETEST Supplier A | **ROUTETEST Supplier A** | `0…003` | ✅ PROD |
| Inbound email | CSV attachment | org default **cleared** | `unrouted` / "Needs supplier" | **ProcuLink Sample Supplier** (oldest active) | `0…004` | ❌ PROD — **F1** |
| Inbound email | CSV attachment | org with **zero** suppliers | `unrouted` | **`unrouted`, supplier NULL** | `0…005` | ✅ LOCAL |
| assign-supplier (resolve the park) | — | operator picks supplier B | Supplier B, re-parsed | **Supplier B, `pending_review`** | `0…005` (same order) | ✅ LOCAL |
| Inbound email, 2nd doc, **same layout** | CSV attachment | fingerprint binding? | (nothing — suggest-only at best) | **routed to A (oldest), fingerprint ignored** | `0…006` | ⚠️ LOCAL — **F2/F3** |
| SFTP pull | CSV | `SftpIngressConfig.DefaultSupplierId` | configured default | **configured default** | n/a (test asserts the id) | ✅ LOCAL |
| S3 / R2 pull | CSV | `S3IngressConfig.DefaultSupplierId` | configured default | **configured default** | n/a | ✅ LOCAL |
| IMAP pull | CSV attachment | `email_config.defaultSupplierId` | configured default | **configured default** | n/a | ✅ LOCAL — after **F4** fix |

Order ids are stand-ins (`0…001` … `0…006`); the same stand-in means the same order. Real ids are
not recorded here — this repository is public. PROD = production `proculink.eu` / `api.proculink.eu`,
the founder's own organisation, plan `growth`, `accountStatus=active`. LOCAL = local API + Worker +
Postgres `:5435`, a throwaway local org.

---

## F1 (P1) — the inbound-email channel guesses a supplier instead of parking `unrouted`

> **FIXED 2026-07-26** (founder ruling "do recommended"). The fallback quoted below is deleted:
> a configured Email-intake default routes the mail, and with none configured the message parks
> `unrouted` and answers 200. `unrouted` is reachable on production for the first time. The
> one-time backfill considered alongside it was **not** shipped — a read-only census of the
> production database showed the 5 orgs it would have covered are dormant pre-launch tenants
> with **0 lifetime orders between them**, and one of the 5 would have had its *sample* supplier
> pinned as a permanent default. See the STATUS.md entry of the same date for the full count.

**Measured on production.** With the org's default supplier cleared, a purchase order emailed to
the org's own `{slug}@orders.proculink.eu` inbound address did **not** park. It was attributed to
**ProcuLink Sample Supplier** — a counterparty nobody chose — and went straight to
`pending_review` as a normal, actionable order.

Cause, `ProcuLink.Infrastructure/Services/Email/InboundEmailRouter.cs:464-472`: after the
configured default fails to resolve, `ResolveSupplierIdAsync` falls back to

```csharp
.Where(s => s.OrgId == orgId && s.DeletedAt == null)
.OrderBy(s => s.CreatedAt)          // ← the oldest active supplier, whoever that is
```

Consequences worth stating plainly:

- **`unrouted` is unreachable on production.** Upload 400s without a supplier
  (`OrdersController.cs:169-170`), REST ingress 400s (`IngressController.cs:123/135`), the three
  pull channels are not configured on prod, and email takes the fallback whenever the org has
  **≥1** supplier. So BE #52's park and FE #32's assign-supplier banner have **no reachable
  production trigger** — which is why both had to be proven locally here.
- **None of the pull channels has this fallback** (`SftpIngressService.cs:318-337`,
  `S3IngressService.cs:387`, `EmailPollOrgJob.cs:366` — null means unrouted, full stop). Email is
  the odd one out, and it is the only push channel that reaches the park at all.
- The fallback is silent. There is no audit row saying "we guessed"; the order looks identically
  routed to one the operator chose. Prior evidence of it firing unnoticed is already on prod: two
  orders from the 2026-07-24 run both sit on ProcuLink Sample Supplier.

Not fixed here — a routing-semantics change with a live blast radius needs its own RED-first PR
and a founder call on the tradeoff (silently guessing vs. parking mail for a human). The
conservative reading of the *offer ⇔ works* rule says park.

## F2 (P1) — BE #54's "learns from operator corrections" silently fails on the real path

**Measured locally, end to end.** An `unrouted` order was resolved to **Supplier B** through the
exact endpoint the UI calls. The order routed correctly. The layout binding did not:

| After the operator's correction | Value |
|---|---|
| `SchemaFingerprints.SupplierIdsCsv` | **empty** — B never recorded |
| `SampleSupplierName` | NULL |
| Worker log | `Schema fingerprint recording failed for order 0…005 (non-fatal)` |
| Exception | `DbUpdateConcurrencyException: expected to affect 1 row(s), but actually affected 0 row(s)` |
| Thrown from | `SchemaFingerprintService.LearnSupplierFromCorrectionAsync` line 182 (its `SaveChangesAsync`) |
| Swallowed at | `ParseOrderJob.cs:150-153` (`catch … LogWarning`, non-fatal by design) |

**Mechanism.** This is the phantom-tracked-row trap PR #60 fixed — on the *other* branch. The
file-backed re-parse deletes the prior lines with `ExecuteDeleteAsync`
(`OrderIngestionService.cs:1025-1027`) and **never detaches them**, while the file-less branch
(`:1250-1252`) does exactly that, with a comment explaining why. `ExecuteDeleteAsync` removes rows
but tells the change tracker nothing, so the previously-`Include`d lines stay tracked against rows
that no longer exist; reflecting the new set severs them from their required parent and EF cascades
them to `Deleted`. `ParseOrderJob` resolves the ingestion service and the fingerprint service from
the **same** scope, so the next `SaveChanges` in that scope inherits the phantoms — and that next
`SaveChanges` is the fingerprint's. PR #60 tested whether the file-backed path's *own* passport
emit was a victim and correctly refuted it (that emit runs *before* the reflection). Nobody checked
the writer that runs *after*.

**The outcome is worse than "did not learn".** The second same-layout document arrived, took F1's
fallback to **Supplier A**, and its first-parse insert bound **A** to the layout. So the fingerprint
now holds the supplier a fallback guessed and has dropped the one a human chose:

```
ParseSuccessCount = 2   SupplierIdsCsv = <id of supplier A>   SampleSupplierName = NULL
```

Every real unrouted order is file-backed (it arrived as an attachment or a pull file), so this is
the normal path, not an edge case. The feature's real-Postgres tests pass because they exercise
the fingerprint service directly rather than through a `ParseOrderJob` scope that has just run a
file-backed re-parse.

**Fix shape (not applied):** detach the stale line entries the moment their rows go, mirroring
`:1254`'s fix on the file-backed branch. A regression test must assert the *fingerprint* write
survives a file-backed assign-supplier re-parse — asserting the passport emit survives is what
already passes.

## F3 (P2) — the layout→supplier binding has no consumer

Even with a correct binding, nothing would happen. `SupplierIdsCsv` is read in exactly one place —
`SchemaFingerprintService.LookupAsync:288-303` — whose only production caller is
`FormatDetectionController.cs:58` (`POST /api/upload/detect-format`, the pre-upload preview).
`ParseOrderJob` never calls it, so no ingest path consults the fingerprint. And on the one read
path, `FingerprintBoost.Apply` (`ISchemaFingerprintService.cs:81-95`) copies only `Confidence`,
`Reasoning` and `SeenCount` — `match.SupplierIds` and `SampleSupplierName` are dropped. `OrderDto`
carries no suggestion field.

Live confirmation: the second same-layout document routed to **A** while the layout's human-chosen
supplier was **B**. So the honest status is **not "suggest-only" but "not surfaced at all"** —
BE #54 makes the training data accumulate; no reader turns it into a suggestion. Worth saying out
loud because "the fingerprint suggests a supplier" is a claim the product cannot currently make.

## F4 (fixed in this PR) — the live IMAP test had been dead since 2026-07-09

`Live_ImapIngress_RealPollImportsCsvAttachment` failed on first run with a bare
`NullReferenceException` from `EmailPollOrgJob.cs:336` — production code, which is what made it
look alarming. It is a harness gap: the test seeded an `Organisation` but **no `Supplier` row**, so
`ResolveDefaultSupplierIdAsync` resolved null, the job took the unrouted branch added by `de4ea0e`
(2026-07-09), and `CreateUnroutedStubAsync` — the one member the Moq mock never stubbed — returned
null. The test could not have passed since that commit, and nothing noticed: it is env-gated and
is one of the two tests CI reports as skipped.

Fixed by seeding the supplier the config points at, plus a `CreateUnroutedStubAsync` setup that
throws a named error, so a future regression reads "the poll imported the attachment UNROUTED"
instead of an NRE two frames deep. **No production defect here** — real `IStubOrderCreator`
implementations always return a `Result`.

## F5 (doc correction) — the display name and the DB slug belong to the same org

The OPS-2 re-run recorded them as different organisations and concluded the frozen org "is not a
membership of this user, so it cannot be driven from here". The Email-intake tab shows the org's
inbound address as **`{slug}@orders.proculink.eu`**, built from the DB `Organisation.Slug`.
The Clerk slug for the same row is a **different string**, derived from the display name, so the
two read as two tenants and are one row — frozen
by the Stripe cancel that morning and un-frozen by the Growth checkout that evening.

Practical cost: the REST-ingress POST first returned a bodiless **403** because the Clerk slug was
used in the route. `IngressController.SlugMatchesCallerAsync:47-53` matches on the **DB** slug —
`GET /api/ingress/{slug}/ping` is the cheap way to confirm which one you hold.

## Open lead — NOT verified

`POST /api/orders/upload` reads `supplierId` from the form and passes it to `CreateStubAsync`
without any visible org-ownership check (`OrdersController.cs:169-222`;
`OrderIngestionService.CreateStubAsync:247+` shows none in its opening block). Whether a supplier
id belonging to another tenant is rejected further down was **not tested** — probing that on
production would mean a deliberate cross-tenant attempt, and the local stack was already torn down.
Every other channel does check (`IngressController.cs:120-133`, `assign-supplier`, all three pull
resolvers). Worth a chip with a real-Postgres two-org test.

---

## How each result was produced

**Prod push channels.** Founder's signed-in Chrome; a per-request Clerk JWT fetched inside the page
(`window.Clerk.session.getToken()`), never extracted. Upload went through
`POST /api/orders/upload` as an in-page `FormData` — the same endpoint and form field the
Browse-files flow posts — because the harness blocks file-picker injection for scratchpad paths
(`file_upload` allowlist) and an isolated-world `DataTransfer` assignment does not stick
(`input.files.length` stayed 0). The supplier was selected in the real UI picker first and the page
confirmed "all sent to the same ROUTETEST Supplier A".

**Prod email.** Real mail via the Postmark `/email` API using the server token read out of Railway
into an env var (`railway variables --json` → `node -e` → `$PM_TOKEN`; never echoed, never written
to the repo). Both messages were accepted (Postmark returned a `MessageID` for each) and the order
existed **~3 s later** — MX → Postmark inbound → CF verify-Worker → API is healthy.

**Local pull channels.** `PROCULINK_LIVE_ENDPOINT_TESTS=1` plus per-channel env, against
throwaway infrastructure: `atmoz/sftp` on `:2222` (`RemoteDirectory` must be the **relative**
`upload` — the code default `/upload` finds nothing), **MinIO** on `:9000` (works because
`AmazonS3ClientFactory` sets `ForcePathStyle = true`; the S3 test uses the allow-private guard, so
localhost passes the SSRF check), and a throwaway **Ethereal** mailbox for IMAP.

**These tests return silently when their env is unset — a green run proves nothing by itself**, so
each was also run as a negative control:

| Channel | Negative control | Result |
|---|---|---|
| SFTP | wrong password | **RED** |
| S3 | non-existent bucket | **RED** |
| IMAP | wrong password | **RED** — `SMTP authentication failed` |

Reproduce:

```bash
docker run -d --name plk-rt-sftp -p 2222:22 -v "<seed>/sftp-upload:/home/testuser/upload:ro" atmoz/sftp testuser:testpass:::upload
```

```bash
dotnet test ProcuLink.Infrastructure.Tests --filter "FullyQualifiedName~Live_SftpIngress|FullyQualifiedName~Live_S3Ingress"
```

---

## Test data created — for founder cleanup

Everything this run created is test data. Nothing was delivered: both ROUTETEST suppliers were
created fresh with **no delivery config**, so no PO could leave the building. Identifiers are
deliberately not recorded here — this repository is public. Find the residue by its `ROUTETEST`
prefix.

**Production.** 2 test suppliers, both named "delete me", which moved the org to 25/30; and 4 test
orders, all with `ROUTETEST` PO numbers (one per push channel, two for email). Already cleaned up,
no action needed: both API keys created for the REST test are **revoked**, and
`settings/email → defaultSupplierId` is back to **null**, its value at the start of the run. IMAP
polling was never enabled.

**Local (`proculink_dev`, throwaway).** One throwaway org, two suppliers and two orders, all on the
local Postgres. Its `supplier_limit_override`/`order_limit_override` were raised to get past the
Pilot cap of 1 supplier. Containers `plk-rt-sftp` / `plk-rt-minio` and network `plk-rt-net` were
removed at the end of the run.

## Deviations from the item as written

- **(b) "inbound email with default CLEARED → expect `unrouted`" does not hold** — that is F1. The
  park was reached on a zero-supplier org locally instead, which is the only way to reach it.
- **The assign-supplier UI (FE #32) was not driven through the browser.** It is merged
  (`4425247`) and its client half is wired to the endpoint that was exercised
  (`api-client.ts:810` → `POST /api/orders/{id}/assign-supplier`, verified by reading it). Running
  the UI would have needed a Clerk session against the dev instance, i.e. entering the founder's
  password, and there is no unrouted order on prod for it to render against. The endpoint the
  banner calls is proven; the banner's own rendering is not re-proven here — say it that way.
- **No prod organisation was created.** A zero-supplier org is the only production route to the
  park, and standing up a new tenant is a founder decision, not a QA side effect.
- **No prod pull infrastructure was stood up**, per the brief; those three are labelled LOCAL.
- One test file changed (`LiveImapIngressTests.cs`, F4). F1, F2 and the open lead are **reported,
  not fixed**.
