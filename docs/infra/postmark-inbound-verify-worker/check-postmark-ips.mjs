#!/usr/bin/env node
/**
 * ProcuLink — Postmark webhook-IP drift checker
 * =============================================
 *
 * Compares Postmark's *published* webhook source IPs against the hardcoded
 * POSTMARK_WEBHOOK_SOURCES allowlist in `worker.js`, and exits non-zero when
 * they differ. Run on a schedule by
 * `.github/workflows/postmark-ip-drift.yml`.
 *
 *   node docs/infra/postmark-inbound-verify-worker/check-postmark-ips.mjs
 *
 * WHY THIS EXISTS
 * ---------------
 * The Worker's IP allowlist is hardcoded and Postmark has changed its
 * published webhook IPs before. Since the source-IP refusal answers 503, drift
 * no longer *destroys* mail (Postmark retries for ~10.5 h, then parks the
 * message in Failed, re-fireable for 45 days) — but nobody finds out until a
 * customer's purchase orders are already queueing. This closes that gap: the
 * check runs weekly against the published list, so drift is caught before any
 * message is refused, not after.
 *
 * WHY IT SCRAPES HTML
 * -------------------
 * Postmark publishes no machine-readable source for these IPs — no JSON, no
 * API endpoint, no .txt (verified 2026-07-24). The support article is the only
 * source of truth there is, so the choice is between scraping it and not
 * monitoring at all. Scraping is made honest by the failure design below
 * rather than by pretending the page is an API.
 *
 * FAILING LOUDLY IS THE WHOLE DESIGN
 * ----------------------------------
 * Every uncertain outcome is a failure, never a pass:
 *
 *   - the fetch fails, times out, or is non-200          → exit 1
 *   - the "Webhooks" heading is gone or renamed          → exit 1
 *   - the section holds no IPs                           → exit 1
 *   - a list entry is not an IP/CIDR                     → exit 1
 *   - the published set differs from worker.js           → exit 1
 *
 * A monitor that shrugs when the page changes shape would report "no drift"
 * forever after Postmark's next redesign, which is the exact failure it is
 * supposed to prevent. The article is small, static, and rarely edited
 * ("Last updated March 17th, 2025" at the time of writing), so the noise cost
 * of that strictness is a rare, genuinely-worth-reading failure.
 */

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

export const POSTMARK_IPS_ARTICLE =
  "https://postmarkapp.com/support/article/800-ips-for-firewalls";

/** Single IPv4 ("a.b.c.d") or CIDR block ("a.b.c.d/nn"). */
const IPV4_OR_CIDR = /^(?:\d{1,3}\.){3}\d{1,3}(?:\/\d{1,2})?$/;

/**
 * Pull the webhook source IPs out of the support article's HTML.
 *
 * Scoped to the `<h2 id="webhooks">` section deliberately: the article lists
 * ~48 addresses across SMTP / MX / DKIM / inbound sections and only this
 * handful are webhook sources. Matching every IP on the page would produce
 * permanent false drift.
 *
 * @param {string} html raw article HTML
 * @returns {string[]} published webhook IPs, in page order
 * @throws {Error} if the section, the list, or an entry cannot be read
 */
export function extractWebhookIps(html) {
  const heading = /<h2[^>]*\bid=["']webhooks["'][^>]*>/i.exec(html);
  if (!heading) {
    throw new Error(
      'could not find the <h2 id="webhooks"> section in the Postmark article — ' +
        "the page structure changed; re-read it by hand and update this checker",
    );
  }

  // The section runs from its heading to the next <h2> (or the end of the doc).
  const afterHeading = html.slice(heading.index + heading[0].length);
  const nextHeading = /<h2[\s>]/i.exec(afterHeading);
  const section = nextHeading
    ? afterHeading.slice(0, nextHeading.index)
    : afterHeading;

  const list = /<ul[^>]*>([\s\S]*?)<\/ul>/i.exec(section);
  if (!list) {
    throw new Error(
      "the Webhooks section no longer contains a list of IPs — " +
        "the page structure changed; re-read it by hand",
    );
  }

  const entries = [...list[1].matchAll(/<li[^>]*>([\s\S]*?)<\/li>/gi)].map((m) =>
    // Strip any inline markup (e.g. <code>) and collapse whitespace.
    m[1].replace(/<[^>]*>/g, "").trim(),
  );

  if (entries.length === 0) {
    throw new Error(
      "found the Webhooks section but no IP entries in it — the page structure changed",
    );
  }

  for (const entry of entries) {
    if (!IPV4_OR_CIDR.test(entry)) {
      throw new Error(
        `the Webhooks list contains an entry that is not an IP or CIDR: "${entry}" — ` +
          "read the article by hand and decide what it means for the allowlist",
      );
    }
  }

  return entries;
}

/**
 * Read POSTMARK_WEBHOOK_SOURCES out of the Worker source.
 *
 * Parsed from the text rather than imported so that `worker.js` needs no
 * export added for monitoring — that file is deployed to Cloudflare BY HAND,
 * and a monitor must never be the reason production and this repo diverge.
 *
 * @param {string} source contents of worker.js
 * @returns {string[]} configured allowlist entries
 * @throws {Error} if the constant is missing, renamed, or empty
 */
export function parseWorkerAllowlist(source) {
  const match = /const\s+POSTMARK_WEBHOOK_SOURCES\s*=\s*\[([\s\S]*?)\]/.exec(
    source,
  );
  if (!match) {
    throw new Error(
      "could not find POSTMARK_WEBHOOK_SOURCES in worker.js — it was renamed or " +
        "reformatted; this monitor is blind until the parser is updated",
    );
  }

  const entries = [...match[1].matchAll(/["']([^"']+)["']/g)].map((m) => m[1]);
  if (entries.length === 0) {
    throw new Error(
      "POSTMARK_WEBHOOK_SOURCES has no entries — the Worker would refuse every " +
        "inbound webhook",
    );
  }

  return entries;
}

/**
 * Set-compare the published list against the configured one.
 *
 * Both directions are reported and both fail the check:
 *   - `missing` — Postmark publishes it, the Worker does not allow it. This is
 *     the one that queues real mail behind a 503.
 *   - `extra` — the Worker allows an address Postmark no longer publishes. Not
 *     an outage, but it is a stale hole in an allowlist, and the entry may
 *     have been reassigned to someone else inside AWS.
 *
 * Comparison is literal string equality on entries. It does NOT do subnet
 * containment: if Postmark ever replaces the four addresses with a CIDR that
 * covers them, this reports drift in both directions and a human decides. That
 * is the intended outcome — a silent "still covered" verdict is exactly the
 * kind of cleverness that hides a real change.
 *
 * @param {string[]} published
 * @param {string[]} configured
 */
export function diffAllowlist(published, configured) {
  const publishedSet = new Set(published);
  const configuredSet = new Set(configured);

  const missing = [...publishedSet].filter((ip) => !configuredSet.has(ip));
  const extra = [...configuredSet].filter((ip) => !publishedSet.has(ip));

  return { missing, extra, inSync: missing.length === 0 && extra.length === 0 };
}

// ── CLI ─────────────────────────────────────────────────────────────────────

/** GitHub Actions annotation (plain text when run outside Actions). */
function annotateError(title, message) {
  const oneLine = message.replace(/\n/g, " ");
  if (process.env.GITHUB_ACTIONS === "true") {
    console.log(`::error title=${title}::${oneLine}`);
  }
  console.error(`ERROR (${title}): ${message}`);
}

async function fetchArticle(url) {
  const response = await fetch(url, {
    headers: {
      // Identify the monitor honestly; a bare fetch UA invites bot blocking.
      "User-Agent":
        "ProcuLink-inbound-verify-worker-ip-monitor/1.0 (+https://proculink.eu)",
      Accept: "text/html",
    },
    signal: AbortSignal.timeout(25_000),
    redirect: "follow",
  });

  if (!response.ok) {
    throw new Error(`GET ${url} returned HTTP ${response.status}`);
  }

  return response.text();
}

async function main() {
  const workerPath = fileURLToPath(new URL("./worker.js", import.meta.url));

  let configured;
  try {
    configured = parseWorkerAllowlist(readFileSync(workerPath, "utf8"));
  } catch (error) {
    annotateError("Worker allowlist unreadable", error.message);
    return 1;
  }

  let published;
  try {
    published = extractWebhookIps(await fetchArticle(POSTMARK_IPS_ARTICLE));
  } catch (error) {
    annotateError(
      "Postmark IP list unreadable",
      `${error.message}\nArticle: ${POSTMARK_IPS_ARTICLE}\n` +
        "Until this is fixed the allowlist is UNMONITORED — check the article by hand.",
    );
    return 1;
  }

  const { missing, extra, inSync } = diffAllowlist(published, configured);

  console.log(`Published by Postmark : ${published.join(", ")}`);
  console.log(`Configured in worker.js: ${configured.join(", ")}`);

  if (inSync) {
    console.log(
      `\nIn sync — ${published.length} webhook source IPs match. Nothing to do.`,
    );
    return 0;
  }

  const lines = [];
  if (missing.length > 0) {
    lines.push(
      `Postmark now publishes ${missing.join(", ")} but worker.js does NOT allow ` +
        "it: inbound mail from that address is being refused with 503 and is " +
        "queueing (~10.5 h of retries, then Failed for 45 days).",
    );
  }
  if (extra.length > 0) {
    lines.push(
      `worker.js still allows ${extra.join(", ")} but Postmark no longer ` +
        "publishes it: a stale allowlist entry.",
    );
  }
  lines.push(
    "FIX: update POSTMARK_WEBHOOK_SOURCES in " +
      "docs/infra/postmark-inbound-verify-worker/worker.js, then HAND-DEPLOY the " +
      "Worker (`bunx wrangler deploy`, or dashboard → postmark-inbound-verify → " +
      "Edit code → Deploy) — merging alone changes nothing in production. " +
      "Runbook: docs/infra/postmark-inbound-verify-worker/README.md " +
      '("Redeploying after a change to worker.js"). Then re-fire any inbound ' +
      "messages sitting in Postmark → Activity → Inbound with status Failed " +
      "(the default view hides them).",
  );

  annotateError("Postmark webhook IP drift", lines.join("\n"));
  return 1;
}

// Only run the CLI when executed directly, so the tests can import the pure
// functions without hitting the network.
if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1].replace(/\\/g, "/")}`).href) {
  process.exitCode = await main();
}
