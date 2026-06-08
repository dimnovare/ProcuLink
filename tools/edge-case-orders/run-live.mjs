#!/usr/bin/env node
/**
 * Live bulk-upload runner for ProcuLink edge-case orders.
 *
 * Uploads every file in a folder (default ./out) to a running ProcuLink API,
 * polls each created order to terminal/parsed state, and writes a results
 * report scoring actual outcome vs the manifest's `expect` hint.
 *
 * THIS SCRIPT DOES NOT EMBED A TOKEN. You must supply:
 *   --base      API base URL              (e.g. https://api.proculink.eu  or  http://localhost:5223)
 *   --supplier  supplierId GUID           (an existing supplier in the target org)
 *   --token     Clerk bearer JWT          (see run-live.md for how to grab one from the browser)
 *               (omit when the API runs with PROCULINK_QA_BYPASS_AUTH=true in Development)
 *   --dir       folder of files to upload (default ./out)
 *
 * Node >= 18 (uses global fetch / FormData / Blob). No dependencies.
 *
 * Example (prod):
 *   node run-live.mjs --base https://api.proculink.eu \
 *     --supplier 11111111-2222-3333-4444-555555555555 \
 *     --token "eyJhbGci..." --dir ./out
 *
 * Example (local QA bypass):
 *   node run-live.mjs --base http://localhost:5223 --supplier <guid> --dir ./out
 *
 * See run-live.md for the full proven recipe and the browser token snippet.
 */

import { readdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, basename, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));

// ── args ─────────────────────────────────────────────────────────────────────
const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, arr) => {
    if (a.startsWith("--")) acc.push([a.slice(2), arr[i + 1] && !arr[i + 1].startsWith("--") ? arr[i + 1] : "true"]);
    return acc;
  }, [])
);

const BASE = (args.base || process.env.PROCULINK_API_URL || "").replace(/\/$/, "");
const SUPPLIER = args.supplier || process.env.PROCULINK_SUPPLIER_ID || "";
const TOKEN = args.token || process.env.PROCULINK_TOKEN || "";
const DIR = args.dir ? (args.dir.startsWith("/") || /^[A-Za-z]:/.test(args.dir) ? args.dir : join(__dirname, args.dir)) : join(__dirname, "out");
const POLL_MS = Number(args.pollMs || 2000);
const POLL_MAX = Number(args.pollMax || 30); // ~60s per order

if (!BASE) { console.error("ERROR: --base is required (e.g. http://localhost:5223)"); process.exit(2); }
if (!SUPPLIER) { console.error("ERROR: --supplier <guid> is required"); process.exit(2); }
if (!TOKEN) console.warn("WARN: no --token supplied — only works if the API runs with PROCULINK_QA_BYPASS_AUTH=true");

const authHeaders = TOKEN ? { Authorization: `Bearer ${TOKEN}` } : {};

// Manifest (optional) for expected-outcome scoring
let manifest = {};
const manifestPath = join(DIR, "manifest.json");
if (existsSync(manifestPath)) {
  try {
    const m = JSON.parse(readFileSync(manifestPath, "utf8"));
    for (const f of m.files || []) manifest[f.file] = f;
  } catch { /* ignore */ }
}

const UPLOAD_EXT = new Set([".csv", ".xlsx", ".pdf", ".xml", ".cxml", ".edi", ".x12", ".txt"]);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function contentTypeFor(name) {
  if (name.endsWith(".csv")) return "text/csv";
  if (name.endsWith(".xml") || name.endsWith(".cxml")) return "application/xml";
  if (name.endsWith(".pdf")) return "application/pdf";
  if (name.endsWith(".xlsx")) return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
  return "application/octet-stream";
}

async function uploadOne(file) {
  const path = join(DIR, file);
  const bytes = readFileSync(path);
  const form = new FormData();
  form.append("file", new Blob([bytes], { type: contentTypeFor(file) }), file);
  form.append("supplierId", SUPPLIER);

  const res = await fetch(`${BASE}/api/orders/upload`, { method: "POST", headers: { ...authHeaders }, body: form });
  const text = await res.text();
  let body; try { body = JSON.parse(text); } catch { body = text; }
  return { status: res.status, body };
}

async function pollOrder(orderId) {
  for (let i = 0; i < POLL_MAX; i++) {
    const res = await fetch(`${BASE}/api/orders/${orderId}`, { headers: { ...authHeaders } });
    if (res.ok) {
      const o = await res.json();
      const st = (o.status || o.Status || "").toLowerCase();
      // terminal-ish states for parse scoring
      if (["parsed", "needs_review", "ready_to_deliver", "delivered", "delivery_failed", "parse_failed", "rejected", "failed"].includes(st)) {
        return o;
      }
    }
    await sleep(POLL_MS);
  }
  return null;
}

function score(file, uploadStatus, order) {
  const exp = manifest[file]?.expect;
  let actual;
  if (uploadStatus >= 400) actual = "reject";
  else if (!order) actual = "pending/unknown";
  else {
    const st = (order.status || order.Status || "").toLowerCase();
    if (["parse_failed", "rejected", "failed"].includes(st)) actual = "reject";
    else if (["needs_review"].includes(st)) actual = "review";
    else actual = "parse";
  }
  const match = exp ? (exp === actual ? "MATCH" : "MISMATCH") : "n/a";
  return { expect: exp ?? "n/a", actual, match };
}

(async () => {
  const files = readdirSync(DIR).filter((f) => UPLOAD_EXT.has("." + f.split(".").pop().toLowerCase()) && f !== "manifest.json");
  if (files.length === 0) { console.error(`No uploadable files in ${DIR}. Run: node generate.mjs`); process.exit(1); }

  console.log(`Uploading ${files.length} files to ${BASE} (supplier ${SUPPLIER})\n`);
  const results = [];
  for (const file of files) {
    process.stdout.write(`→ ${file.padEnd(34)} `);
    try {
      const up = await uploadOne(file);
      const orderId = up.body?.orderId || up.body?.id || up.body?.Id;
      let order = null;
      if (up.status < 400 && orderId) order = await pollOrder(orderId);
      const sc = score(file, up.status, order);
      const finalStatus = order ? (order.status || order.Status) : `HTTP ${up.status}`;
      results.push({ file, uploadStatus: up.status, orderId: orderId ?? null, finalStatus, ...sc });
      console.log(`HTTP ${up.status}  state=${finalStatus}  expect=${sc.expect} actual=${sc.actual} [${sc.match}]`);
    } catch (e) {
      results.push({ file, error: String(e) });
      console.log(`ERROR ${e}`);
    }
  }

  const out = { ranAt: new Date().toISOString(), base: BASE, supplier: SUPPLIER, total: results.length, results };
  const outPath = join(__dirname, "live-results.json");
  writeFileSync(outPath, JSON.stringify(out, null, 2), "utf8");
  const mism = results.filter((r) => r.match === "MISMATCH").length;
  const matched = results.filter((r) => r.match === "MATCH").length;
  console.log(`\nDone. ${matched} matched, ${mism} mismatched. Full report → ${outPath}`);
})();
