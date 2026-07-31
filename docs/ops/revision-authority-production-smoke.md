# Runbook — production smoke: revision authority actually holds

**Status: NOT EXECUTED.** This is a founder action. WP-21 built the automated proof and the
readable surfaces; it deliberately did **not** touch production data, because rule R8 forbids
mutating production without a written pre-list — and this document *is* that pre-list, not its
execution.

**Audience:** the founder, or anyone with Railway + production Clerk access.
**Time:** ~15 minutes.
**Risk:** low, and fully undoable — every step is reverted by step 7.

---

## 1. What this proves, and why a test could not

The automated proof
(`ProcuLink.Api.Tests/Integration/PinnedOrderDoesNotRerouteAfterConfigEditPostgresTests.cs`) runs
the real `DeliveryService` against a real Postgres and shows that a pinned order keeps its
revision's endpoint after a live delivery edit + republish, while an unpinned order takes the edit.
That is the behaviour.

What it cannot show is that **the deployed processes are configured to run that behaviour**. Two
things could still be wrong in production and no test would notice:

1. a Railway service silently loses `Connections__RevisionAuthority` (someone edits variables, a
   service is recreated, a new host is added without it);
2. the API and the Worker disagree — one honours the pin, the other reads live tables — so the same
   order is previewed under one config and delivered under another.

Steps 2 and 3 below answer both, without touching any data at all. Steps 4–6 are the end-to-end
observation and are the only ones that write anything.

---

## 2. Read the flag from the running processes (no data touched)

Do this first. If it fails, stop — nothing below is meaningful.

```bash
# API — expect: revisionAuthority: true
curl -s https://api.proculink.eu/health/ready | jq '{status, workerHealthy, revisionAuthority}'
```

**PASS:** `"revisionAuthority": true`.
**FAIL:** `false`, or the field is missing.

- `false` → the Railway `ProcuLink` service has lost the variable. Fix per §8 before anything else;
  every pinned order the API previews or manually sends is currently reading live tables.
- field missing → the deployed build predates WP-21. Redeploy `main` and re-read.

The Worker serves no HTTP, so it is read from its log instead:

```bash
# Worker — expect a line containing: revision authority enabled=True
railway logs --service aware-amazement | grep -i "revision authority"
```

**PASS:** `ProcuLink.Worker startup: revision authority enabled=True …`
**FAIL:** `enabled=False`, or no such line (build predates WP-21 → redeploy).

> The Worker is the host that runs parse, transform and delivery. If only one host can be checked,
> check this one.

### Optional — read the variables directly

⚠️ **Never run `railway variables` unfiltered.** One unfiltered call on 2026-07-27 leaked the live
OpenAI key, PostHog key and Neon password into a transcript. Always filter:

```bash
railway variables --service ProcuLink       | grep -i revisionauthority
railway variables --service aware-amazement | grep -i revisionauthority
```

Expect `Connections__RevisionAuthority=true` from each. Do not widen the grep.

---

## 3. Confirm the roster is complete

`ProcuLink.Infrastructure/Services/RevisionAuthorityHosts.cs` lists every host that resolves an
effective config. `RevisionAuthorityHostCoverageTests` fails the build if a host registers the
resolver without being listed, so the list cannot silently fall behind the code.

What it cannot know is whether a **listed** host is deployed as a service you forgot to configure.
So: for each entry in `RevisionAuthorityHosts.All`, run the matching check from §2. Today that is
exactly two — `ProcuLink` and `aware-amazement`. If the list has grown, the extra host is new since
this runbook was written and needs the variable set (§8).

---

## 4. Pre-list — exactly what steps 5–6 will touch

Fill this in **before** doing anything, and keep it. Nothing outside this list may be touched.

| Field | Value | Notes |
|---|---|---|
| Org slug | `______________` | Use a **throwaway** org, never a paying customer |
| Supplier id | `______________` | A supplier that exists only in that org |
| Order id (pinned) | `______________` | Created in step 5; note the id the upload returns |
| Delivery config key edited | `url` (in `SupplierDeliveryConfig.ConfigJson`) | The only field changed |
| Endpoint BEFORE | `https://webhook.site/<token-A>` | A disposable request bin |
| Endpoint AFTER | `https://webhook.site/<token-B>` | A second bin, so the two are distinguishable |

Constraints, non-negotiable:

- **No real supplier endpoint.** Both endpoints are disposable request bins you own. A real
  supplier receiving a smoke-test PO is an incident, not a test.
- **No existing order.** The pinned order is created by this runbook and deleted by step 7.
- **A throwaway org.** Not a customer's, not the founder's main workspace.

---

## 5. Create the pinned order

1. Sign in to the throwaway org.
2. Add the supplier, and configure HTTP delivery pointing at **endpoint BEFORE**. Save.
   Publishing a connection revision snapshots that endpoint — this is the contract the order will
   be pinned to.
3. Upload one small CSV PO. Let it parse, map and transform.
4. Stop before sending. Record the order id in the pre-list.

**Checkpoint.** The order is now pinned to a published revision whose delivery snapshot holds
endpoint BEFORE.

---

## 6. Edit the config, then send the pinned order

1. Change the supplier's delivery URL to **endpoint AFTER**. Save.
   (The save republishes: the old revision is archived, a new published revision snapshots
   endpoint AFTER, and the active pointer moves.)
2. Send the order created in step 5.

**Observe both bins:**

| Observation | Verdict |
|---|---|
| The PO arrives at **endpoint BEFORE**, and **endpoint AFTER** gets nothing | ✅ **PASS** — revision authority holds. The pinned order was delivered under the contract it was ingested with. |
| The PO arrives at **endpoint AFTER** | ❌ **FAIL** — the pin was ignored. Re-run §2: the Worker most likely has `enabled=False`. If §2 says `True`, this is a real regression — file it against `EffectiveConnectionConfigResolver` and attach both bin logs. |
| Nothing arrives anywhere | ⚠️ **INCONCLUSIVE** — a delivery failure, not a routing answer. Read the order's delivery history and retry. |

**Cross-check the audit trail.** Open the order's delivery history. The recorded destination must
be **endpoint BEFORE**. If the request bin says BEFORE but the audit trail says AFTER, the routing
is right and the *logging* is wrong — still a bug worth filing, and a different one.

**Optional second half — prove the live path still moves.** Upload a second PO to the same supplier
now and send it. It must arrive at **endpoint AFTER**. Together the two orders are the same
difference the automated proof asserts, observed in production.

---

## 7. Undo

1. Delete the order(s) created in step 5/6.
2. Delete the supplier.
3. Delete the throwaway org.
4. Discard both request bins.

Nothing else was modified. No production variable was changed by this runbook.

---

## 8. If a host is missing the flag

```bash
railway variables --service <service> --set "Connections__RevisionAuthority=true"
```

Then redeploy that service and re-run §2 for it. Do **not** print the surrounding variables.

New services: add the entry to `RevisionAuthorityHosts.All` in the same PR that registers
`IEffectiveConnectionConfigResolver` — `RevisionAuthorityHostCoverageTests` fails the build until
you do, and it also fails until this runbook names the new service.

---

## Appendix — the history this exists to prevent

On 2026-07-27 a full-codebase audit filed a **P0**: "revision authority is off in production; the
entire versioning/reproducibility story is inert where it matters." It read
`ProcuLink.Api/appsettings.Development.json:46`, saw the flag set only there, and inferred the
deployed value.

It never read the deployed environment. `Connections__RevisionAuthority = true` on both Railway
services, and had been. The finding was refuted by one filtered CLI call.

The inference was reasonable; the missing step was not looking. The reason it *could* go unlooked
is that the effective value was served nowhere — not on an endpoint, not in a log, not in a doc.
WP-21 closed that: `GET /health/ready` carries `revisionAuthority`, every host announces the parsed
value at startup, and `RevisionAuthorityHosts` is enforced by a test. The fact is now readable by
anyone in under a minute, which is the actual fix.
