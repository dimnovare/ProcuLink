#!/usr/bin/env node
/**
 * The out-of-band half of the live delivery test.
 *
 * ── WHY A SEPARATE VERIFIER ──────────────────────────────────────────────────
 * `dotnet test --filter "Category=LiveEndpoint"` exits 0 in two very different
 * situations: every live test passed, and every live test SKIPPED. The tests are
 * `[EnvironmentGatedFact]`s — if the opt-in variable or the base URL is missing
 * they are skipped with a reason, which is correct behaviour for a developer's
 * laptop and completely wrong as a scheduled production check. A run that
 * verified nothing must not be able to report the same green as a run that
 * verified everything.
 *
 * So the pass condition is not the test runner's exit code alone. It is what the
 * disposable sink actually received: a token was requested with the right client
 * credentials, an authenticated purchase order arrived on /po carrying the
 * bearer that token issued, and an unauthenticated one arrived on /po-plain. All
 * three are facts about the wire, recorded by a process the tests do not control.
 *
 * That also makes this the first check that can see a class of regression the
 * assertions inside the tests cannot: `result.Success == true` says the supplier
 * answered 200, and says nothing at all about what was sent.
 *
 * Usage:
 *   node tools/live-delivery-sink/verify-receipts.mjs --log ./sink-receipts.jsonl
 */

import { readFileSync, existsSync } from "node:fs";

function arg(name, fallback) {
  const i = process.argv.indexOf(`--${name}`);
  return i === -1 ? fallback : process.argv[i + 1];
}

const LOG = arg("log", process.env.SINK_LOG ?? "sink-receipts.jsonl");

if (!existsSync(LOG)) {
  fail(
    `No receipt file at ${LOG}. The sink never started, or it wrote somewhere else — ` +
      `either way nothing was verified and this run proves nothing.`,
  );
}

const receipts = readFileSync(LOG, "utf8")
  .split("\n")
  .filter((line) => line.trim().length > 0)
  .map((line) => JSON.parse(line));

const problems = [];

function require_(condition, message) {
  if (!condition) problems.push(message);
}

const tokenRequests = receipts.filter((r) => r.kind === "token");
const authorizedDeliveries = receipts.filter((r) => r.kind === "delivery" && r.route === "po");
const plainDeliveries = receipts.filter((r) => r.kind === "delivery" && r.route === "po-plain");
const unexpected = receipts.filter((r) => r.kind === "unexpected");

// ── Anti-vacuity: something has to have happened ─────────────────────────────
require_(
  receipts.length > 0,
  `The sink received NOTHING. Either the live tests skipped (check that ` +
    `PROCULINK_LIVE_ENDPOINT_TESTS=1 and PROCULINK_LIVE_HTTP_BASE were both set for the ` +
    `dotnet test step) or the dispatcher never reached the endpoint. A green test run on an ` +
    `empty sink means the delivery path was not exercised at all.`,
);

// ── The OAuth2 client-credentials round trip really happened ─────────────────
require_(
  tokenRequests.length > 0,
  `No request reached POST /token. The OAuth2 client-credentials test did not run, or the ` +
    `dispatcher stopped fetching a token before delivering.`,
);
// Guarded on there being a token request at all, so a run where nothing arrived
// reports "nothing arrived" once instead of also accusing HttpAuthApplier of a
// change it did not make.
require_(
  tokenRequests.length === 0 || tokenRequests.some((r) => r.accepted === true),
  `A token was requested but the credentials were wrong, so the sink refused every one. ` +
    `HttpAuthApplier is no longer sending client_id/client_secret/grant_type in the form body ` +
    `it used to — that is a real change to how ProcuLink authenticates to suppliers.`,
);

// ── The token was APPLIED, not merely fetched ────────────────────────────────
require_(
  authorizedDeliveries.length > 0,
  `No purchase order reached POST /po. The authenticated delivery test did not run, or the ` +
    `delivery failed before it reached the endpoint.`,
);
require_(
  authorizedDeliveries.every((r) => r.authorized === true),
  `A delivery reached /po WITHOUT the bearer token the sink had just issued ` +
    `(${authorizedDeliveries.filter((r) => !r.authorized).length} of ${authorizedDeliveries.length}). ` +
    `Fetching a token and then not sending it is exactly the failure a supplier would see as a 401, ` +
    `and it is invisible to any test that only checks the token endpoint.`,
);

// ── The unauthenticated path still works ─────────────────────────────────────
require_(
  plainDeliveries.length > 0,
  `No purchase order reached POST /po-plain. The unauthenticated delivery test did not run, ` +
    `or plain HTTP delivery is broken.`,
);

// ── Something real was actually on the wire ──────────────────────────────────
//
// NOT asserted here, deliberately, and said out loud so nobody reads the sink's
// header capture as coverage it is not: these deliveries carry NO
// Idempotency-Key and no X-Message-Id. HttpDeliveryDispatcher adds those two
// headers only when it is given an idempotency key, and
// LiveEndpointDeliveryTests calls DispatchAsync without one — a measured fact,
// confirmed against the recorded receipts, not an assumption. Requiring them
// here would fail every run; claiming this checks them would be false. The sink
// records all headers so that a future test which DOES pass a key can assert on
// them without changing the sink.
for (const delivery of [...authorizedDeliveries, ...plainDeliveries]) {
  const headers = delivery.headers ?? {};
  require_(
    typeof headers["content-type"] === "string" && headers["content-type"].length > 0,
    `A delivery to ${delivery.route} arrived with no Content-Type header.`,
  );
  require_(
    delivery.bodyLength > 0,
    `A delivery to ${delivery.route} arrived with an EMPTY body. The dispatcher reported success ` +
      `for a purchase order that contained nothing.`,
  );
}

// ── Nothing surprising ───────────────────────────────────────────────────────
require_(
  unexpected.length === 0,
  `The sink received ${unexpected.length} request(s) to routes it does not serve: ` +
    `${[...new Set(unexpected.map((r) => `${r.method} ${r.path}`))].join(", ")}. ` +
    `The dispatcher is sending somewhere the test did not intend.`,
);

console.log(
  `Sink receipts: ${receipts.length} total — ${tokenRequests.length} token, ` +
    `${authorizedDeliveries.length} authenticated delivery, ${plainDeliveries.length} plain delivery, ` +
    `${unexpected.length} unexpected.`,
);

if (problems.length > 0) {
  for (const p of problems) {
    console.error(`::error title=Live delivery not verified::${p}`);
  }
  process.exit(1);
}

console.log("Live delivery verified end to end against the disposable sink.");

function fail(message) {
  console.error(`::error title=Live delivery not verified::${message}`);
  process.exit(1);
}
