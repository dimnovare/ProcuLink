/**
 * Tests for the Postmark webhook-IP drift checker.
 *
 * Dependency-free: node:test + Node 18+ globals. Run from this folder:
 *
 *   node --test
 *
 * The checker's whole job is to notice when Postmark's published webhook
 * source IPs stop matching POSTMARK_WEBHOOK_SOURCES in worker.js. The tests
 * that matter most are therefore the ones proving it FAILS LOUDLY when it
 * cannot tell — a drift monitor that quietly passes on a changed page is
 * worse than no monitor, because it launders "I did not look" as "all clear".
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import {
  extractWebhookIps,
  parseWorkerAllowlist,
  diffAllowlist,
} from "./check-postmark-ips.mjs";

const WORKER_PATH = fileURLToPath(new URL("./worker.js", import.meta.url));

/**
 * The real shape of https://postmarkapp.com/support/article/800-ips-for-firewalls
 * as served on 2026-07-24 (captured with curl — the IPs are in the static
 * markup, nothing is client-rendered). Trimmed to two sections: the checker
 * must read only the second one.
 *
 * This matters: the live page lists ~48 IPs across SMTP / MX / DKIM / inbound
 * sections, and only these four are webhook sources. A monitor that grepped
 * every IP on the page would report 44 phantom differences and be switched off
 * within a week.
 */
const REAL_SHAPE_HTML = `
<p>Below is a list of IPs you can add to your firewall.</p>
<h2 id="smtp-servers">SMTP Servers</h2>
<p>These are the IPs we send email from.</p>
<ul>
<li>3.34.213.247</li>
<li>107.22.71.110</li>
<li>52.2.5.255</li>
<li>72.44.42.226</li>
</ul>
<h2 id="webhooks">Webhooks</h2>
<p>These IPs apply for every webhook sent by Postmark (inbound, bounce, open, etc).</p>
<ul>
<li>3.134.147.250</li>
<li>50.31.156.6</li>
<li>50.31.156.77</li>
<li>18.217.206.57</li>
</ul>
      </div>
      <footer class="entry_footer">
        Last updated March 17th, 2025
      </footer>
`;

const PUBLISHED_IPS = [
  "3.134.147.250",
  "50.31.156.6",
  "50.31.156.77",
  "18.217.206.57",
];

// ── Reading Postmark's published list ───────────────────────────────────────

test("the published webhook IPs are read from the Webhooks section", () => {
  assert.deepEqual(extractWebhookIps(REAL_SHAPE_HTML), PUBLISHED_IPS);
});

test("IPs from the article's other sections are not mistaken for webhook IPs", () => {
  const found = extractWebhookIps(REAL_SHAPE_HTML);

  // 3.34.213.247 is an SMTP sending IP one section earlier. Letting it through
  // would mean permanent false drift against a correct allowlist.
  assert.ok(!found.includes("3.34.213.247"), `leaked an SMTP IP: ${found}`);
  assert.equal(found.length, 4);
});

test("a missing Webhooks heading fails loudly instead of reporting no drift", () => {
  const renamed = REAL_SHAPE_HTML.replace(
    '<h2 id="webhooks">Webhooks</h2>',
    '<h2 id="webhook-ips">Webhook IPs</h2>',
  );

  assert.throws(() => extractWebhookIps(renamed), /webhooks/i);
});

test("an empty Webhooks list fails loudly instead of reporting every IP missing", () => {
  const emptied = REAL_SHAPE_HTML.replace(
    /<h2 id="webhooks">Webhooks<\/h2>\s*<p>[^<]*<\/p>\s*<ul>[\s\S]*?<\/ul>/,
    '<h2 id="webhooks">Webhooks</h2><p>See the API.</p><ul></ul>',
  );

  assert.throws(() => extractWebhookIps(emptied), /no ip/i);
});

test("a non-IP entry in the list fails loudly rather than being silently dropped", () => {
  // e.g. Postmark adds "IPv6: see this page" or a CIDR-with-note bullet. The
  // checker must not quietly ignore it — a human has to look at the change.
  const annotated = REAL_SHAPE_HTML.replace(
    "<li>18.217.206.57</li>",
    "<li>18.217.206.57</li>\n<li>IPv6 addresses coming soon</li>",
  );

  assert.throws(() => extractWebhookIps(annotated), /not an ip/i);
});

// ── Reading the Worker's configured list ────────────────────────────────────

test("the configured allowlist is read from the real worker.js", () => {
  // Deliberately against the real file, not a fixture: if someone reformats
  // POSTMARK_WEBHOOK_SOURCES in a way the parser cannot read, that is a
  // blinded monitor and this test is the thing that says so.
  const configured = parseWorkerAllowlist(readFileSync(WORKER_PATH, "utf8"));

  assert.deepEqual(configured, PUBLISHED_IPS);
});

test("a renamed or missing constant fails loudly", () => {
  assert.throws(
    () => parseWorkerAllowlist("const SOMETHING_ELSE = [];\n"),
    /POSTMARK_WEBHOOK_SOURCES/,
  );
});

test("an empty configured allowlist fails loudly", () => {
  assert.throws(
    () => parseWorkerAllowlist("const POSTMARK_WEBHOOK_SOURCES = [];\n"),
    /no entries/i,
  );
});

// ── The diff ────────────────────────────────────────────────────────────────

test("an IP Postmark added but the Worker lacks is reported as missing", () => {
  const diff = diffAllowlist(["1.1.1.1", "2.2.2.2"], ["1.1.1.1"]);

  assert.deepEqual(diff.missing, ["2.2.2.2"]);
  assert.deepEqual(diff.extra, []);
  assert.equal(diff.inSync, false);
});

test("an IP the Worker still allows but Postmark dropped is reported as extra", () => {
  const diff = diffAllowlist(["1.1.1.1"], ["1.1.1.1", "9.9.9.9"]);

  assert.deepEqual(diff.missing, []);
  assert.deepEqual(diff.extra, ["9.9.9.9"]);
  assert.equal(diff.inSync, false);
});

test("order and duplicates do not count as drift", () => {
  const diff = diffAllowlist(
    ["2.2.2.2", "1.1.1.1", "1.1.1.1"],
    ["1.1.1.1", "2.2.2.2"],
  );

  assert.equal(diff.inSync, true);
});

test("the live worker.js is in sync with the list published when it was pinned", () => {
  const configured = parseWorkerAllowlist(readFileSync(WORKER_PATH, "utf8"));

  assert.equal(diffAllowlist(PUBLISHED_IPS, configured).inSync, true);
});
