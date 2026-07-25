/**
 * ProcuLink — Postmark inbound-webhook hardening Worker
 * =====================================================
 *
 * Sits in front of the ProcuLink API's public inbound-email webhook
 * (POST /api/inbound-email/postmark). Postmark inbound webhooks are NOT
 * HMAC-signed, so the accepted mitigation is this Cloudflare Worker, which:
 *
 *   1. Allows only requests originating from Postmark's published webhook
 *      source IPs (503 for everything else — see "Responses" below for why the
 *      refusal is deliberately retryable and not a 403).
 *   2. Forwards valid requests to the origin API with an added
 *      `X-Inbound-Proxy-Secret` header, which the API can be configured to
 *      require in addition to the existing `?token=` shared token.
 *   3. Applies a best-effort per-IP rate limit (in-memory token bucket —
 *      per-isolate, see caveat below; add a Cloudflare WAF rate-limiting
 *      rule for a real distributed limit, see README).
 *
 * Dependency-free. ES-module Worker syntax. Deployable via wrangler or
 * dashboard paste — see README.md next to this file.
 *
 * Configuration (Worker settings):
 *   INBOUND_PROXY_SECRET  (secret)  — value stamped into X-Inbound-Proxy-Secret
 *                                     on every forwarded request. Must match
 *                                     Inbound__Postmark__ProxySecret on the
 *                                     Railway API service.
 *   ORIGIN_HOST           (plain var, optional) — origin hostname to forward
 *                                     to. Defaults to "api.proculink.eu".
 *
 * IMPORTANT: this Worker only *moves the front door*. The origin stays
 * publicly reachable (api.proculink.eu is DNS-only to Railway, and the
 * *.up.railway.app hostname always exists). The IP allowlist only becomes a
 * real barrier once the API also requires X-Inbound-Proxy-Secret — see
 * README.md, section "API-side change".
 */

// ── Postmark webhook source IPs ──────────────────────────────────────────────
//
// REFRESH-ME: Postmark's published webhook source IPs. Source of truth:
//   https://postmarkapp.com/support/article/800-ips-for-firewalls
//   ("These IPs apply for every webhook sent by Postmark (inbound, bounce,
//    open, etc).")
// Verified current: 2026-07-24. If legitimate inbound emails start failing in
// Postmark's Activity log with 503 "source address not allowed", re-check that
// page — Postmark has changed/added IPs before and announces it there. Until
// the list is refreshed and the Worker redeployed, those messages keep retrying
// and end up as Failed, which is re-fireable by hand — nothing is lost.
//
// You should not be the one who notices, though: this array is WATCHED. The
// weekly `.github/workflows/postmark-ip-drift.yml` job re-reads that article
// and fails loudly the moment it stops matching this list — see
// `check-postmark-ips.mjs` next to this file. Keep the constant's name and its
// plain array-of-string-literals shape: the checker parses this file as text
// (deliberately — so monitoring never forces an export into hand-deployed
// code), and a test pins that it can still read it.
//
// Entries may be single IPv4 addresses or CIDR blocks ("a.b.c.d/nn").
const POSTMARK_WEBHOOK_SOURCES = [
  "3.134.147.250",
  "50.31.156.6",
  "50.31.156.77",
  "18.217.206.57",
];

// The only path this Worker will proxy. Exact match — the Postmark webhook
// URL must use exactly this path (it already does).
const ALLOWED_PATH = "/api/inbound-email/postmark";

// ── Best-effort rate limit (token bucket) ───────────────────────────────────
//
// CAVEAT: this bucket lives in Worker isolate memory. Cloudflare runs many
// isolates across many datacenters, each with its own bucket, and isolates
// are evicted/restarted at will — so the effective global limit is
// (BUCKET_CAPACITY × number-of-isolates) and resets unpredictably. That is
// fine for its purpose here: a cheap backstop against a runaway retry loop or
// a replay flood *from an allowed Postmark IP*. Random-internet traffic never
// reaches this point (blocked by the IP allowlist above).
//
// For a real, distributed limit, add a Cloudflare WAF rate-limiting rule on
// the hostname (1 rule is included on the Free plan) — see README.md.
const BUCKET_CAPACITY = 60;   // max burst per source IP per isolate
const REFILL_PER_SECOND = 1;  // sustained ~60 requests/minute per source IP

const buckets = new Map(); // ip -> { tokens: number, last: epoch-ms }

function takeToken(ip) {
  const now = Date.now();
  let b = buckets.get(ip);
  if (!b) {
    b = { tokens: BUCKET_CAPACITY, last: now };
    buckets.set(ip, b);
  }
  const elapsedSec = (now - b.last) / 1000;
  b.tokens = Math.min(BUCKET_CAPACITY, b.tokens + elapsedSec * REFILL_PER_SECOND);
  b.last = now;
  if (b.tokens < 1) return false;
  b.tokens -= 1;
  return true;
}

// ── IPv4 allowlist matching (single IPs + CIDR) ─────────────────────────────

/** Parse dotted-quad IPv4 to a 32-bit unsigned int; null if not valid IPv4. */
function ipv4ToInt(ip) {
  const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(ip);
  if (!m) return null;
  let out = 0;
  for (let i = 1; i <= 4; i++) {
    const octet = Number(m[i]);
    if (octet > 255) return null;
    out = (out << 8) | octet;
  }
  return out >>> 0;
}

/** True if `ip` (dotted-quad string) matches any allowlist entry. */
function ipAllowed(ip) {
  const ipInt = ipv4ToInt(ip);
  // Postmark webhooks originate from IPv4 (AWS). An IPv6 CF-Connecting-IP —
  // or anything unparsable — is not on the list, so it is denied. If Postmark
  // ever moves webhooks to IPv6 they will announce new addresses on the
  // support article cited above; add them (and IPv6 support) then.
  if (ipInt === null) return false;

  for (const entry of POSTMARK_WEBHOOK_SOURCES) {
    const slash = entry.indexOf("/");
    if (slash === -1) {
      const allowed = ipv4ToInt(entry);
      if (allowed !== null && allowed === ipInt) return true;
    } else {
      const base = ipv4ToInt(entry.slice(0, slash));
      const bits = Number(entry.slice(slash + 1));
      if (base === null || !Number.isInteger(bits) || bits < 0 || bits > 32) continue;
      const mask = bits === 0 ? 0 : (0xffffffff << (32 - bits)) >>> 0;
      if ((ipInt & mask) === (base & mask)) return true;
    }
  }
  return false;
}

// ── Responses ────────────────────────────────────────────────────────────────
//
// Reason strings are intentionally terse but distinct — they cost an attacker
// nothing (path/method are public knowledge; an IP rejection is self-evident)
// and they tell the founder at a glance whether a legitimate Postmark POST was
// blocked because the IP list drifted (=> refresh POSTMARK_WEBHOOK_SOURCES).
//
// The STATUS is a retry instruction, not a verdict — same model the API uses
// (ProcuLink.Api/Controllers/InboundEmailController.cs). Postmark retries any
// non-200 inbound webhook response ten times over ~10.5 hours and then files
// the message as Failed, which stays manually re-fireable for the 45-day
// retention. Two statuses break that: 200 settles the message as Processed
// (unrecoverable), and 403 stops the retries on the first attempt AND never
// reaches Failed, so the message is unrecoverable by either route.
//   https://postmarkapp.com/support/article/understanding-inbound-webhook-retries-in-postmark
//
// So this Worker spends neither. Every refusal here is a condition an operator
// fixes — a stale IP allowlist, a mis-set webhook URL, a burst — and none of
// them is a judgement about the mail, which the Worker never even reads. The
// IP gate in particular is the most likely thing in this file to go stale
// (Postmark has changed its published IPs before), and 403 would turn that
// drift into silent loss of real purchase orders on the first attempt. 503
// ("cannot accept this right now") keeps the full retry window and the Failed
// landing; 429 is not reused for it, because the token bucket below already
// means that, and one meaning per status is what makes the activity log
// readable at a glance.

function deny(status, reason) {
  return new Response(JSON.stringify({ error: reason }), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

// ── Entry point ──────────────────────────────────────────────────────────────

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    // 1. Only the webhook path exists on this front door.
    if (url.pathname !== ALLOWED_PATH) {
      return deny(404, "not found");
    }

    // 2. Postmark inbound webhooks are always POST.
    if (request.method !== "POST") {
      return deny(405, "method not allowed");
    }

    // 3. Source-IP allowlist. CF-Connecting-IP is set by Cloudflare and
    //    cannot be spoofed by the client on proxied/custom-domain traffic.
    //    Retryable on purpose (see "Responses"): a legitimate Postmark POST
    //    lands here whenever the hardcoded allowlist has gone stale.
    const clientIp = request.headers.get("CF-Connecting-IP") || "";
    if (!ipAllowed(clientIp)) {
      return deny(503, "source address not allowed");
    }

    // 4. Best-effort rate limit (per-isolate — see caveat above).
    if (!takeToken(clientIp)) {
      return deny(429, "rate limited");
    }

    // 5. Forward to the origin API, preserving path + query string (the
    //    existing ?token= shared token rides along — layered auth), streaming
    //    the body through without buffering (attachments can be megabytes).
    const originUrl = new URL(url);
    originUrl.protocol = "https:";
    originUrl.hostname = env.ORIGIN_HOST || "api.proculink.eu";
    originUrl.port = "";

    const headers = new Headers(request.headers);
    if (env.INBOUND_PROXY_SECRET) {
      headers.set("X-Inbound-Proxy-Secret", env.INBOUND_PROXY_SECRET);
    }
    // For origin-side logging/audit — who actually POSTed to the Worker.
    headers.set("X-Original-Client-IP", clientIp);

    return fetch(originUrl.toString(), {
      method: "POST",
      headers,
      body: request.body,
    });
  },
};
