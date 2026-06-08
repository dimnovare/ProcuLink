#!/usr/bin/env node
/**
 * Edge-case order generator for ProcuLink.
 *
 * Emits adversarial purchase-order files across the formats the ingest pipeline
 * actually accepts (CSV, cXML 1.2, UBL 2.1 Order) into ./out/, plus a manifest
 * describing the intended edge case and the expected outcome (parse / review /
 * reject) so a human or the live runner can score behaviour.
 *
 * Zero dependencies — Node >= 18. Run:  node generate.mjs
 *
 * Column aliases / element shapes here are matched to the real parsers in
 * ProcuLink.Transform:
 *   - CsvOrderParser      (alias-driven header matching, ',' or ';' delimiter)
 *   - CxmlOrderParser     (<cXML>/Request/OrderRequest/OrderRequestHeader/ItemOut)
 *   - UblOrderParser      (UBL 2.1 Order, cbc/cac local-name matching)
 *
 * Each case carries an `expect` hint:
 *   parse   — should parse to a clean canonical order
 *   review  — should parse but flag line(s) / order for human review (exceptions)
 *   reject  — should fail parse / be rejected (unsupported / malformed / missing required)
 */

import { writeFileSync, mkdirSync, rmSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const OUT = join(__dirname, "out");

// Reset out/ for a deterministic run
if (existsSync(OUT)) rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

const manifest = [];
function emit(filename, content, meta) {
  writeFileSync(join(OUT, filename), content, "utf8");
  manifest.push({ file: filename, bytes: Buffer.byteLength(content, "utf8"), ...meta });
}

// ── tiny builders ──────────────────────────────────────────────────────────
const csv = (rows) => rows.map((r) => r.join(",")).join("\r\n") + "\r\n";
const csvSemi = (rows) => rows.map((r) => r.join(";")).join("\r\n") + "\r\n";
const xmlEsc = (s) =>
  String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

function cxml({ orderID, currency = "EUR", deployment = "production", total, lines, omitTotal = false }) {
  const items = lines
    .map(
      (l) => `      <ItemOut quantity="${l.qty}" lineNumber="${l.line}">
        <ItemID>${l.sku === undefined ? "" : `<SupplierPartID>${xmlEsc(l.sku)}</SupplierPartID>`}</ItemID>
        <ItemDetail>
          ${l.price === undefined ? "" : `<UnitPrice><Money currency="${currency}">${l.price}</Money></UnitPrice>`}
          <Description xml:lang="en">${xmlEsc(l.desc ?? "")}</Description>
          <UnitOfMeasure>${xmlEsc(l.uom ?? "EA")}</UnitOfMeasure>
        </ItemDetail>
      </ItemOut>`
    )
    .join("\n");
  return `<?xml version="1.0" encoding="UTF-8"?>
<cXML payloadID="edge-${orderID}@proculink.test" timestamp="2026-06-08T10:00:00+00:00" version="1.2.044">
  <Header>
    <From><Credential domain="NetworkId"><Identity>EDGE_BUYER</Identity></Credential></From>
    <To><Credential domain="NetworkId"><Identity>REDACTED-NETWORK-ID</Identity></Credential></To>
    <Sender><Credential domain="NetworkId"><Identity>EDGE_BUYER</Identity><SharedSecret>x</SharedSecret></Credential><UserAgent>EdgeGen</UserAgent></Sender>
  </Header>
  <Request deploymentMode="${deployment}">
    <OrderRequest>
      <OrderRequestHeader orderID="${xmlEsc(orderID)}" orderDate="2026-06-08T10:00:00+00:00" type="new">
        ${omitTotal ? "" : `<Total><Money currency="${currency}">${total}</Money></Total>`}
      </OrderRequestHeader>
${items}
    </OrderRequest>
  </Request>
</cXML>
`;
}

function ubl({ id, currency = "EUR", omitId = false, buyer = "Edge Buyer Ltd", seller = "Edge Supplier OY", lines, peppol = false }) {
  const olines = lines
    .map(
      (l) => `  <cac:OrderLine>
    <cac:LineItem>
      <cbc:ID>${l.line}</cbc:ID>
      ${l.qty === undefined ? "" : `<cbc:Quantity unitCode="${xmlEsc(l.uom ?? "EA")}">${l.qty}</cbc:Quantity>`}
      <cac:Price><cbc:PriceAmount currencyID="${currency}">${l.price ?? ""}</cbc:PriceAmount></cac:Price>
      <cac:Item>
        <cbc:Name>${xmlEsc(l.desc ?? "")}</cbc:Name>
        ${l.sku === undefined ? "" : `<cac:SellersItemIdentification><cbc:ID>${xmlEsc(l.sku)}</cbc:ID></cac:SellersItemIdentification>`}
      </cac:Item>
    </cac:LineItem>
  </cac:OrderLine>`
    )
    .join("\n");
  return `<?xml version="1.0" encoding="UTF-8"?>
<Order xmlns="urn:oasis:names:specification:ubl:schema:xsd:Order-2"
       xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"
       xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2">
  ${peppol ? "<cbc:CustomizationID>urn:fdc:peppol.eu:poacc:trns:order:3</cbc:CustomizationID>" : ""}
  ${omitId ? "" : `<cbc:ID>${xmlEsc(id)}</cbc:ID>`}
  <cbc:IssueDate>2026-06-08</cbc:IssueDate>
  <cbc:DocumentCurrencyCode>${currency}</cbc:DocumentCurrencyCode>
  <cac:BuyerCustomerParty><cac:Party><cac:PartyName><cbc:Name>${xmlEsc(buyer)}</cbc:Name></cac:PartyName></cac:Party></cac:BuyerCustomerParty>
  <cac:SellerSupplierParty><cac:Party><cac:PartyName><cbc:Name>${xmlEsc(seller)}</cbc:Name></cac:PartyName></cac:Party></cac:SellerSupplierParty>
${olines}
</Order>
`;
}

// Canonical CSV header used by most cases (matches CsvOrderParser aliases)
const H = ["ponumber", "orderdate", "currency", "buyername", "linenumber", "buyeritemcode", "description", "quantity", "unit", "unitprice"];

// ════════════════════════════════════════════════════════════════════════════
// CSV edge cases
// ════════════════════════════════════════════════════════════════════════════

// 1. Clean baseline — should parse cleanly
emit("csv-01-clean.csv",
  csv([H,
    ["PO-1001", "2026-06-08", "EUR", "Acme Buyer", "1", "BUY-001", "Widget A", "10", "EA", "12.50"],
    ["PO-1001", "2026-06-08", "EUR", "Acme Buyer", "2", "BUY-002", "Widget B", "5", "EA", "8.00"],
  ]),
  { format: "csv", case: "clean baseline", expect: "parse" });

// 2. Missing required PO number (header empty)
emit("csv-02-missing-po.csv",
  csv([H,
    ["", "2026-06-08", "EUR", "Acme Buyer", "1", "BUY-001", "Widget A", "10", "EA", "12.50"],
  ]),
  { format: "csv", case: "missing PO number header", expect: "review" });

// 3. Zero and negative quantity
emit("csv-03-zero-negative-qty.csv",
  csv([H,
    ["PO-1003", "2026-06-08", "EUR", "Acme Buyer", "1", "BUY-001", "Zero qty", "0", "EA", "12.50"],
    ["PO-1003", "2026-06-08", "EUR", "Acme Buyer", "2", "BUY-002", "Negative qty", "-5", "EA", "8.00"],
  ]),
  { format: "csv", case: "zero + negative quantity", expect: "review" });

// 4. 250 lines (large order)
{
  const rows = [H];
  for (let i = 1; i <= 250; i++) {
    rows.push(["PO-BIG", "2026-06-08", "EUR", "Bulk Buyer", String(i), `BUY-${String(i).padStart(4, "0")}`, `Item ${i}`, String((i % 9) + 1), "EA", (10 + (i % 50) + 0.99).toFixed(2)]);
  }
  emit("csv-04-250-lines.csv", csv(rows), { format: "csv", case: "250 lines (bulk)", expect: "parse", lines: 250 });
}

// 5. Multi-currency within one file (header currency vs per-row mismatch) — parser takes first non-empty currency
emit("csv-05-multi-currency.csv",
  csv([H,
    ["PO-1005", "2026-06-08", "EUR", "Acme Buyer", "1", "BUY-001", "Priced in EUR", "1", "EA", "100.00"],
    ["PO-1005", "2026-06-08", "USD", "Acme Buyer", "2", "BUY-002", "Priced in USD", "1", "EA", "120.00"],
    ["PO-1005", "2026-06-08", "GBP", "Acme Buyer", "3", "BUY-003", "Priced in GBP", "1", "EA", "90.00"],
  ]),
  { format: "csv", case: "multi-currency rows (header keeps first)", expect: "review" });

// 6. Unicode / accented / multi-script descriptions + buyer name
emit("csv-06-unicode.csv",
  csv([H,
    ["PO-Ünïcödé", "2026-06-08", "EUR", "Société Générale Łódź", "1", "BUY-ÉÀ1", "Câble réseau — caté­gorie 6", "3", "EA", "4.91"],
    ["PO-Ünïcödé", "2026-06-08", "EUR", "Société Générale Łódź", "2", "BUY-日本", "ネットワークケーブル", "2", "EA", "9.00"],
    ["PO-Ünïcödé", "2026-06-08", "EUR", "Société Générale Łódź", "3", "BUY-emoji", "Mouse 🖱️ ergonomic", "1", "EA", "49.31"],
  ]),
  { format: "csv", case: "unicode/accented/CJK/emoji text", expect: "parse" });

// 7. European thousands separator "1.234,56" (semicolon-delimited, comma decimal)
//    Parser uses InvariantCulture decimal.TryParse(NumberStyles.Any) → "1.234,56" will NOT parse as 1234.56.
//    This case proves how the engine handles EU number formatting (likely → 0 / review).
emit("csv-07-eu-thousands.csv",
  csvSemi([H,
    ["PO-1007", "2026-06-08", "EUR", "Käufer GmbH", "1", "BUY-001", "EU-formatted price", "1", "EA", "1.234,56"],
    ["PO-1007", "2026-06-08", "EUR", "Käufer GmbH", "2", "BUY-002", "EU-formatted qty", "1.000", "EA", "73,22"],
  ]),
  { format: "csv", case: "EU thousands/decimal 1.234,56 (semicolon)", expect: "review" });

// 8. US thousands separator "1,234.56" (comma-delimited → comma in number breaks columns)
//    Quoted to survive CSV; proves grouping-comma handling under InvariantCulture.
emit("csv-08-us-thousands.csv",
  csv([H,
    ["PO-1008", "2026-06-08", "USD", "US Buyer Inc", "1", "BUY-001", "US-formatted price", "1", "EA", '"1,234.56"'],
  ]),
  { format: "csv", case: "US thousands 1,234.56 (quoted)", expect: "review" });

// 9. Duplicate PO numbers across two orders in one file (single canonical order — first PO wins)
emit("csv-09-duplicate-po.csv",
  csv([H,
    ["PO-DUP", "2026-06-08", "EUR", "Buyer A", "1", "BUY-001", "From order A", "1", "EA", "10.00"],
    ["PO-DUP", "2026-06-09", "EUR", "Buyer B", "1", "BUY-002", "From order B (dup PO)", "2", "EA", "20.00"],
  ]),
  { format: "csv", case: "duplicate PO number rows", expect: "review" });

// 10. Empty line descriptions + empty buyer code
emit("csv-10-empty-fields.csv",
  csv([H,
    ["PO-1010", "2026-06-08", "EUR", "Acme Buyer", "1", "", "", "1", "EA", "10.00"],
    ["PO-1010", "2026-06-08", "EUR", "Acme Buyer", "2", "BUY-002", "", "2", "", "20.00"],
  ]),
  { format: "csv", case: "empty description + empty buyer code", expect: "review" });

// 11. Malformed header (unknown column names — nothing maps)
emit("csv-11-malformed-header.csv",
  csv([["foo", "bar", "baz", "qux"],
    ["PO-1011", "stuff", "more", "1"],
  ]),
  { format: "csv", case: "malformed/unknown header columns", expect: "reject" });

// 12. Missing unit price (price column empty → null UnitPrice)
emit("csv-12-missing-price.csv",
  csv([H,
    ["PO-1012", "2026-06-08", "EUR", "Acme Buyer", "1", "BUY-001", "No price", "3", "EA", ""],
  ]),
  { format: "csv", case: "missing unit price", expect: "review" });

// 13. Non-numeric quantity ("five")
emit("csv-13-nonnumeric-qty.csv",
  csv([H,
    ["PO-1013", "2026-06-08", "EUR", "Acme Buyer", "1", "BUY-001", "Bad qty", "five", "EA", "12.50"],
  ]),
  { format: "csv", case: "non-numeric quantity → 0", expect: "review" });

// 14. Alias headers only (qty/price/po/sku/line) — proves alias resolution
emit("csv-14-alias-headers.csv",
  csv([["po", "line", "sku", "description", "qty", "unit", "price"],
    ["PO-1014", "1", "BUY-001", "Alias-mapped", "4", "EA", "5.50"],
  ]),
  { format: "csv", case: "alias-only headers (po/line/sku/qty/price)", expect: "parse" });

// 15. Header only, no data rows (empty order)
emit("csv-15-header-only.csv", csv([H]),
  { format: "csv", case: "header only, zero lines", expect: "reject" });

// 16. BOM + CRLF + trailing whitespace in headers
emit("csv-16-bom-whitespace.csv",
  "﻿" + csv([[" PO Number ", " Quantity ", " Unit Price ", " Buyer Item Code ", " Description "],
    ["PO-1016", "2", "9.99", "BUY-001", "Whitespace + BOM header"],
  ]),
  { format: "csv", case: "BOM + spaced/cased headers", expect: "parse" });

// ════════════════════════════════════════════════════════════════════════════
// cXML edge cases
// ════════════════════════════════════════════════════════════════════════════

// 17. Clean cXML
emit("cxml-17-clean.xml",
  cxml({ orderID: "CX-2001", total: "250.00", lines: [
    { line: 1, qty: "10", sku: "SUP-A", price: "12.50", desc: "Widget A", uom: "EA" },
    { line: 2, qty: "5", sku: "SUP-B", price: "25.00", desc: "Widget B", uom: "EA" },
  ] }),
  { format: "cxml", case: "clean cXML 1.2", expect: "parse" });

// 18. cXML missing orderID (required)
emit("cxml-18-missing-orderid.xml",
  cxml({ orderID: "", total: "10.00", lines: [{ line: 1, qty: "1", sku: "SUP-A", price: "10.00", desc: "x" }] }),
  { format: "cxml", case: "missing orderID", expect: "reject" });

// 19. cXML line missing SupplierPartID
emit("cxml-19-missing-sku.xml",
  cxml({ orderID: "CX-2003", total: "10.00", lines: [{ line: 1, qty: "1", sku: undefined, price: "10.00", desc: "no sku" }] }),
  { format: "cxml", case: "line missing SupplierPartID", expect: "reject" });

// 20. cXML line missing UnitPrice/Money
emit("cxml-20-missing-price.xml",
  cxml({ orderID: "CX-2004", total: "0.00", lines: [{ line: 1, qty: "1", sku: "SUP-A", price: undefined, desc: "no price" }] }),
  { format: "cxml", case: "line missing UnitPrice", expect: "reject" });

// 21. cXML zero + negative quantity
emit("cxml-21-zero-neg-qty.xml",
  cxml({ orderID: "CX-2005", total: "10.00", lines: [
    { line: 1, qty: "0", sku: "SUP-A", price: "10.00", desc: "zero qty" },
    { line: 2, qty: "-3", sku: "SUP-B", price: "5.00", desc: "neg qty" },
  ] }),
  { format: "cxml", case: "zero + negative quantity", expect: "review" });

// 22. cXML 200 lines
{
  const lines = [];
  for (let i = 1; i <= 200; i++) lines.push({ line: i, qty: String((i % 7) + 1), sku: `SUP-${i}`, price: (1 + (i % 30)).toFixed(2), desc: `Item ${i}`, uom: "EA" });
  emit("cxml-22-200-lines.xml", cxml({ orderID: "CX-BIG", total: "9999.00", lines }),
    { format: "cxml", case: "200 lines", expect: "parse", lines: 200 });
}

// 23. cXML non-EUR currency (PLN, like real Nestle POs)
emit("cxml-23-pln-currency.xml",
  cxml({ orderID: "CX-PLN", currency: "PLN", total: "295.02", lines: [
    { line: 10, qty: "1.0", sku: "29048107", price: "295.02", desc: "Gigaset 550 HX — Dodatkowa słuch", uom: "EA" },
  ] }),
  { format: "cxml", case: "PLN currency + Polish accents", expect: "parse" });

// 24. cXML unicode + ampersand in description (escaped)
emit("cxml-24-unicode-amp.xml",
  cxml({ orderID: "CX-Ünï", total: "20.00", lines: [
    { line: 1, qty: "1", sku: "SUP-A", price: "20.00", desc: "Café & Thé — Łódź ☕", uom: "EA" },
  ] }),
  { format: "cxml", case: "unicode + escaped ampersand", expect: "parse" });

// 25. cXML missing Total (header total omitted)
emit("cxml-25-missing-total.xml",
  cxml({ orderID: "CX-NOTOTAL", omitTotal: true, lines: [{ line: 1, qty: "1", sku: "SUP-A", price: "10.00", desc: "no total" }] }),
  { format: "cxml", case: "missing header Total", expect: "review" });

// 26. cXML malformed (truncated / not well-formed XML)
emit("cxml-26-malformed.xml",
  `<?xml version="1.0"?>\n<cXML><Request><OrderRequest><OrderRequestHeader orderID="CX-BAD"`,
  { format: "cxml", case: "truncated / not well-formed XML", expect: "reject" });

// 27. Wrong root element (looks like xml, not cXML or UBL)
emit("cxml-27-wrong-root.xml",
  `<?xml version="1.0"?>\n<NotAnOrder><foo>bar</foo></NotAnOrder>\n`,
  { format: "cxml", case: "wrong XML root element", expect: "reject" });

// ════════════════════════════════════════════════════════════════════════════
// UBL edge cases
// ════════════════════════════════════════════════════════════════════════════

// 28. Clean UBL 2.1
emit("ubl-28-clean.xml",
  ubl({ id: "UBL-3001", lines: [
    { line: 1, qty: "10", uom: "EA", price: "12.50", desc: "Widget A", sku: "SUP-A" },
    { line: 2, qty: "5", uom: "EA", price: "8.00", desc: "Widget B", sku: "SUP-B" },
  ] }),
  { format: "ubl", case: "clean UBL 2.1 Order", expect: "parse" });

// 29. Peppol BIS 3 variant
emit("ubl-29-peppol.xml",
  ubl({ id: "UBL-PEPPOL", peppol: true, lines: [
    { line: 1, qty: "3", uom: "EA", price: "4.91", desc: "Peppol line", sku: "SUP-P" },
  ] }),
  { format: "ubl", case: "Peppol BIS Order 3 profile", expect: "parse" });

// 30. UBL missing header cbc:ID (required)
emit("ubl-30-missing-id.xml",
  ubl({ id: "", omitId: true, lines: [{ line: 1, qty: "1", uom: "EA", price: "10.00", desc: "no id", sku: "SUP-A" }] }),
  { format: "ubl", case: "missing header cbc:ID", expect: "reject" });

// 31. UBL line missing Quantity
emit("ubl-31-missing-qty.xml",
  ubl({ id: "UBL-3004", lines: [{ line: 1, qty: undefined, uom: "EA", price: "10.00", desc: "no qty", sku: "SUP-A" }] }),
  { format: "ubl", case: "line missing Quantity", expect: "reject" });

// 32. UBL missing buyer party name (no BuyerCustomerParty name)
emit("ubl-32-missing-buyer.xml",
  ubl({ id: "UBL-3005", buyer: "", lines: [{ line: 1, qty: "1", uom: "EA", price: "10.00", desc: "no buyer", sku: "SUP-A" }] }),
  { format: "ubl", case: "missing buyer name", expect: "review" });

// 33. UBL unicode + multi-currency-ish (GBP)
emit("ubl-33-gbp-unicode.xml",
  ubl({ id: "UBL-£GBP", currency: "GBP", buyer: "Æthelred & Sons Ltd", lines: [
    { line: 1, qty: "2", uom: "EA", price: "19.33", desc: "SanDisk Ultra Flair — clé USB", sku: "11097719" },
  ] }),
  { format: "ubl", case: "GBP + accented buyer/desc", expect: "parse" });

// 34. UBL 200 lines
{
  const lines = [];
  for (let i = 1; i <= 200; i++) lines.push({ line: i, qty: String((i % 5) + 1), uom: "EA", price: (2 + (i % 40)).toFixed(2), desc: `UBL Item ${i}`, sku: `SUP-${i}` });
  emit("ubl-34-200-lines.xml", ubl({ id: "UBL-BIG", lines }),
    { format: "ubl", case: "200 lines", expect: "parse", lines: 200 });
}

// ── write manifest ───────────────────────────────────────────────────────────
const byFormat = manifest.reduce((acc, m) => ((acc[m.format] = (acc[m.format] || 0) + 1), acc), {});
const byExpect = manifest.reduce((acc, m) => ((acc[m.expect] = (acc[m.expect] || 0) + 1), acc), {});
const summary = { generatedAt: new Date().toISOString(), total: manifest.length, byFormat, byExpect, files: manifest };
writeFileSync(join(OUT, "manifest.json"), JSON.stringify(summary, null, 2), "utf8");

console.log(`Generated ${manifest.length} edge-case files into ${OUT}`);
console.log("By format:", JSON.stringify(byFormat));
console.log("By expected outcome:", JSON.stringify(byExpect));
console.log("Manifest: out/manifest.json");
