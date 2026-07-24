/**
 * Tests for the Postmark inbound-verify Worker.
 *
 * Dependency-free: node:test + the fetch/Request/Response globals that ship
 * with Node 18+. Run from this folder:
 *
 *   node --test
 *
 * These are not wired into the .NET CI (this repo has no JS pipeline) — they
 * exist so the Worker's retry contract can be re-proved before a hand-deploy.
 */

import { test } from "node:test";
import assert from "node:assert/strict";

import worker from "./worker.js";

const WEBHOOK_URL =
  "https://inbound.proculink.eu/api/inbound-email/postmark?token=shared-token";

// Two different allowlisted IPs so the per-IP token bucket in one test cannot
// starve another (module state is shared across tests in a single process).
const POSTMARK_IP_FORWARD = "50.31.156.6";
const POSTMARK_IP_BUCKET = "3.134.147.250";

/** A request shaped like a Postmark inbound webhook POST. */
function postmarkPost(url = WEBHOOK_URL, clientIp = null) {
  const headers = new Headers({ "Content-Type": "application/json" });
  if (clientIp !== null) headers.set("CF-Connecting-IP", clientIp);
  return new Request(url, { method: "POST", headers, body: "{}" });
}

async function reason(response) {
  return (await response.json()).error;
}

/** Swap in a fetch stub for the origin forward, restoring the real one after. */
async function withStubbedOrigin(run) {
  const real = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init });
    return new Response('{"ok":true}', { status: 200 });
  };
  try {
    return await run(calls);
  } finally {
    globalThis.fetch = real;
  }
}

// ── The retry contract ──────────────────────────────────────────────────────
//
// Postmark retries any non-200 inbound webhook response ten times over ~10.5
// hours and then files the message as Failed (manually re-fireable for the
// 45-day retention) — EXCEPT 403, which stops retries immediately and never
// reaches Failed, so the message is unrecoverable by either route.
// https://postmarkapp.com/support/article/understanding-inbound-webhook-retries-in-postmark

test("an unknown source IP is refused with a status Postmark will retry", async () => {
  const res = await worker.fetch(postmarkPost(WEBHOOK_URL, "203.0.113.9"), {});

  assert.notEqual(
    res.status,
    403,
    "403 stops Postmark retrying immediately and never files the message as Failed — " +
      "IP-allowlist drift would silently destroy real purchase orders",
  );
  assert.equal(res.status, 503);
});

test("an unknown source IP is never accepted", async () => {
  const res = await worker.fetch(postmarkPost(WEBHOOK_URL, "203.0.113.9"), {});

  assert.notEqual(res.status, 200, "200 tells Postmark the message is settled");
});

test("a missing CF-Connecting-IP is refused the same retryable way", async () => {
  const res = await worker.fetch(postmarkPost(WEBHOOK_URL, null), {});

  assert.equal(res.status, 503);
});

test("no reject path answers 403", async () => {
  const rejects = [
    await worker.fetch(postmarkPost("https://inbound.proculink.eu/anything-else"), {}),
    await worker.fetch(
      new Request(WEBHOOK_URL, { method: "GET", headers: new Headers() }),
      {},
    ),
    await worker.fetch(postmarkPost(WEBHOOK_URL, "203.0.113.9"), {}),
  ];

  for (const res of rejects) {
    assert.notEqual(res.status, 403, `403 refusal: "${await reason(res.clone())}"`);
    assert.notEqual(res.status, 200, "a refusal must not read as settled");
  }
});

// ── Reason strings (the founder's IP-drift signal) ──────────────────────────

test("the IP refusal keeps its own terse reason string", async () => {
  const res = await worker.fetch(postmarkPost(WEBHOOK_URL, "203.0.113.9"), {});

  assert.equal(await reason(res), "source address not allowed");
});

test("refusals stay distinguishable by both status and reason", async () => {
  const notFound = await worker.fetch(
    postmarkPost("https://inbound.proculink.eu/anything-else"),
    {},
  );
  const badMethod = await worker.fetch(
    new Request(WEBHOOK_URL, { method: "GET", headers: new Headers() }),
    {},
  );
  const badIp = await worker.fetch(postmarkPost(WEBHOOK_URL, "203.0.113.9"), {});

  assert.deepEqual(
    [notFound.status, badMethod.status, badIp.status],
    [404, 405, 503],
  );
  assert.deepEqual(
    [await reason(notFound), await reason(badMethod), await reason(badIp)],
    ["not found", "method not allowed", "source address not allowed"],
  );
});

// ── Everything the change must not disturb ──────────────────────────────────

test("an allowlisted source IP is still forwarded to the origin", async () => {
  await withStubbedOrigin(async (calls) => {
    const res = await worker.fetch(postmarkPost(WEBHOOK_URL, POSTMARK_IP_FORWARD), {
      INBOUND_PROXY_SECRET: "s3cret",
    });

    assert.equal(res.status, 200);
    assert.equal(calls.length, 1);
    assert.equal(
      calls[0].url,
      "https://api.proculink.eu/api/inbound-email/postmark?token=shared-token",
    );
    assert.equal(calls[0].init.headers.get("X-Inbound-Proxy-Secret"), "s3cret");
    assert.equal(
      calls[0].init.headers.get("X-Original-Client-IP"),
      POSTMARK_IP_FORWARD,
    );
  });
});

test("the rate limiter keeps 429 to itself", async () => {
  await withStubbedOrigin(async () => {
    let limited = null;
    // Bucket capacity is 60 per IP per isolate; drain it, then one more.
    for (let i = 0; i < 80 && limited === null; i++) {
      const res = await worker.fetch(postmarkPost(WEBHOOK_URL, POSTMARK_IP_BUCKET), {});
      if (res.status !== 200) limited = res;
    }

    assert.notEqual(limited, null, "expected the bucket to run dry");
    assert.equal(limited.status, 429);
    assert.equal(await reason(limited), "rate limited");
  });
});
