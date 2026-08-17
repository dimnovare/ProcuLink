#!/usr/bin/env node
/**
 * A disposable supplier endpoint, created at runtime, that exists for one CI run.
 *
 * ── WHY THIS EXISTS ──────────────────────────────────────────────────────────
 * LiveEndpointDeliveryTests fires the REAL HttpDeliveryDispatcher — real
 * HttpClient, real OAuth2 client-credentials token fetch, real OutboundUrlPolicy
 * and OutboundRequestGuard — at whatever `PROCULINK_LIVE_HTTP_BASE` names. Its
 * own header says the caller "verifies receipt out-of-band", and until now there
 * was no caller: the tests are gated off in CI and were run by hand, against a
 * Cloudflare Worker somebody had to keep alive, with the receipt checked by a
 * human looking at a dashboard.
 *
 * This is that endpoint, and that verification, as code. It records every
 * request it receives to a JSONL file so a later step can assert what actually
 * arrived — which is the only thing that distinguishes "delivery works" from
 * "two gated tests skipped and the job went green".
 *
 * ── WHY LOOPBACK, AND WHAT THAT COSTS ────────────────────────────────────────
 * It binds 127.0.0.1 and nothing else. A sink that cannot be routed to from
 * outside the runner cannot, even if misconfigured, hold a real supplier's
 * hostname or leak a purchase order to a third party. `OutboundUrlPolicy` permits
 * plain `http` for loopback specifically (SecureSchemes = https,
 * LoopbackOnlySchemes = http, `IsLoopback`), so this is a supported target rather
 * than a hole being exploited.
 *
 * State the limitation plainly, because a check is worth what it actually covers:
 * a loopback sink does NOT exercise TLS negotiation, certificate validation, or
 * real-world latency. What it does exercise is everything the dispatcher itself
 * does — the token round trip, the bearer application, the headers, the body,
 * the guard, and the status handling. If TLS-level coverage is wanted later that
 * is a different endpoint and a separate decision, not a flag on this one.
 *
 * ── ROUTES ───────────────────────────────────────────────────────────────────
 *   POST /token      OAuth2 client-credentials. Form-encoded, per HttpAuthApplier's
 *                    default request style. Returns {access_token, token_type,
 *                    expires_in}. REJECTS a wrong client_id/secret with 401, so a
 *                    dispatcher that stopped sending credentials fails the run.
 *   POST /po         Requires `Authorization: Bearer <the token just issued>`.
 *                    401 otherwise — this is what proves the fetched token was
 *                    actually applied to the delivery, not merely fetched.
 *   POST /po-plain   Unauthenticated. 200.
 *   GET  /health     Readiness, so the workflow can wait for the port.
 *
 * Anything else is 404 and is still recorded, so an unexpected request is
 * evidence rather than silence.
 *
 * Usage:
 *   node tools/live-delivery-sink/sink.mjs --port 8899 --log ./sink-receipts.jsonl
 */

import { createServer } from "node:http";
import { appendFileSync, writeFileSync } from "node:fs";
import { randomUUID } from "node:crypto";

function arg(name, fallback) {
  const i = process.argv.indexOf(`--${name}`);
  return i === -1 ? fallback : process.argv[i + 1];
}

const PORT = Number(arg("port", process.env.SINK_PORT ?? "8899"));
const LOG = arg("log", process.env.SINK_LOG ?? "sink-receipts.jsonl");

/**
 * The credentials LiveEndpointDeliveryTests sends. They are literals in a test
 * file that is already in the repository, they authenticate nothing but this
 * throwaway process, and they live for the duration of one job — but they are
 * still checked, because a token endpoint that accepts anything cannot tell you
 * whether the client sent credentials at all.
 */
const EXPECTED_CLIENT_ID = "proculink-test-client";
const EXPECTED_CLIENT_SECRET = "test-secret-9f3a2b";

/** Issued per process, so a stale token from an earlier run cannot pass. */
const ISSUED_TOKEN = `sink-token-${randomUUID()}`;

// Truncate on start: a receipt file left by a previous run would otherwise be
// read as this run's evidence, which is the exact failure mode this whole file
// is built to prevent.
writeFileSync(LOG, "");

function record(entry) {
  appendFileSync(LOG, `${JSON.stringify(entry)}\n`);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => resolve(Buffer.concat(chunks)));
    req.on("error", reject);
  });
}

function json(res, status, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(status, { "content-type": "application/json", "content-length": Buffer.byteLength(body) });
  res.end(body);
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url ?? "/", `http://127.0.0.1:${PORT}`);
  const path = url.pathname;

  if (req.method === "GET" && path === "/health") {
    json(res, 200, { ok: true });
    return;
  }

  const body = await readBody(req);

  // Headers are recorded because they are half of what is being verified:
  // Idempotency-Key and X-Message-Id are the dispatcher's contract with a
  // supplier, and a delivery that stopped sending them is a real regression that
  // a status code alone would never show.
  const entry = {
    at: new Date().toISOString(),
    method: req.method,
    path,
    headers: req.headers,
    bodyLength: body.length,
    bodyPreview: body.subarray(0, 512).toString("utf8"),
  };

  if (req.method === "POST" && path === "/token") {
    const form = new URLSearchParams(body.toString("utf8"));
    const okCreds =
      form.get("client_id") === EXPECTED_CLIENT_ID &&
      form.get("client_secret") === EXPECTED_CLIENT_SECRET &&
      form.get("grant_type") === "client_credentials";

    record({ ...entry, kind: "token", accepted: okCreds, scope: form.get("scope") });

    if (!okCreds) {
      json(res, 401, { error: "invalid_client" });
      return;
    }
    json(res, 200, { access_token: ISSUED_TOKEN, token_type: "Bearer", expires_in: 3600 });
    return;
  }

  if (req.method === "POST" && path === "/po") {
    const authorized = req.headers.authorization === `Bearer ${ISSUED_TOKEN}`;
    record({ ...entry, kind: "delivery", route: "po", authorized });

    if (!authorized) {
      json(res, 401, { error: "missing_or_wrong_bearer" });
      return;
    }
    json(res, 200, { received: true });
    return;
  }

  if (req.method === "POST" && path === "/po-plain") {
    record({ ...entry, kind: "delivery", route: "po-plain", authorized: true });
    json(res, 200, { received: true });
    return;
  }

  record({ ...entry, kind: "unexpected" });
  json(res, 404, { error: "no_such_route" });
});

// 127.0.0.1 explicitly, never 0.0.0.0. Binding all interfaces on a CI runner
// would make a test sink reachable from the runner's network for as long as the
// job lasts, for no benefit whatsoever.
server.listen(PORT, "127.0.0.1", () => {
  console.log(`Disposable delivery sink listening on http://127.0.0.1:${PORT} (receipts → ${LOG})`);
});

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
