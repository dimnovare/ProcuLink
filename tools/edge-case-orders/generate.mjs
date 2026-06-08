#!/usr/bin/env node
/**
 * Edge-case order generator for ProcuLink.
 *
 * Emits adversarial purchase-order files across the deterministic text formats the
 * ingest pipeline accepts (CSV, cXML 1.2, UBL 2.1 Order, EDIFACT ORDERS D96A,
 * ANSI X12 850, SAP IDoc ORDERS05) into ./out/, plus a manifest describing the
 * intended edge case and the expected outcome (parse / review / reject) so a human
 * or the live runner can score behaviour.
 *
 * XLSX is binary and is NOT emitted here (this is a zero-dependency script); the
 * deterministic XLSX edge cases — including the EU-locale numeric-cell corruption
 * regression — live in the in-process FormatMatrix xUnit suite
 * (ProcuLink.Transform.Tests/FormatMatrix). PDF needs the live OpenAI path and is
 * likewise covered there + by run-live.mjs, not here.
 *
 * Zero dependencies — Node >= 18. Run:  node generate.mjs
 *
 * Column aliases / element shapes here are matched to the real parsers in
 * ProcuLink.Transform:
 *   - CsvOrderParser      (alias-driven header matching, ',' or ';' delimiter)
 *   - CxmlOrderParser     (<cXML>/Request/OrderRequest/OrderRequestHeader/ItemOut)
 *   - UblOrderParser      (UBL 2.1 Order, cbc/cac local-name matching)
 *   - EdifactOrderParser  (UN/EDIFACT ORDERS D96A: UNB/UNH/BGM/DTM/CUX/NAD/LIN/QTY/PRI)
 *   - X12OrderParser      (ANSI X12 850: ISA/GS/ST/BEG/CUR/N1/PO1/PID/CTT/SE/GE/IEA)
 *   - IDocOrders05Parser  (SAP IDoc ORDERS05: E1EDK01/E1EDKA1/E1EDK02/E1EDP01/E1EDP19/E1EDS01)
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

// ── EDIFACT ORDERS D96A builder (default delimiters : + ? ') ─────────────────
function ediEsc(s) {
  let out = "";
  for (const c of String(s)) {
    if (c === "?" || c === ":" || c === "+" || c === "'") out += "?";
    out += c;
  }
  return out;
}
function edifact({ poNumber, currency = "EUR", buyer = "Edge Buyer Ltd", orderDate = "20260608", lines, omitUnh = false }) {
  const seg = [];
  seg.push("UNB+UNOC:3+SENDER:14+RECEIVER:14+260608:0900+1'");
  if (!omitUnh) seg.push("UNH+1+ORDERS:D:96A:UN'");
  seg.push(`BGM+220+${ediEsc(poNumber)}+9'`);
  seg.push(`DTM+137:${orderDate}:102'`);
  seg.push(`CUX+2:${currency}:9'`);
  seg.push(`NAD+BY+5412345678901::9++${ediEsc(buyer)}'`);
  for (const l of lines) {
    seg.push(`LIN+${l.line}++${ediEsc(l.sku ?? "")}:IN'`);
    if (l.desc !== undefined && l.desc !== "") seg.push(`IMD+F+ANM+:::${ediEsc(l.desc)}'`);
    seg.push(`QTY+21:${l.qty}:${ediEsc(l.uom ?? "EA")}'`);
    if (l.price !== undefined) seg.push(`PRI+AAA:${l.price}'`);
  }
  seg.push("UNS+S'");
  seg.push("UNT+99+1'"); // count not validated by the parser
  seg.push("UNZ+1+1'");
  return seg.join("\r\n") + "\r\n";
}

// ── X12 850 builder (delimiters * > ~; fixed-width 105-char ISA) ──────────────
function x12San(s) {
  return String(s).replace(/[*>~]/g, " ").trim();
}
function x12Isa() {
  return (
    "ISA*00*" + " ".repeat(10) + "*00*" + " ".repeat(10) +
    "*ZZ*" + "SENDER".padEnd(15) + "*ZZ*" + "RECEIVER".padEnd(15) +
    "*260608*0830*U*00401*000000001*0*P*>~"
  );
}
function x12({ poNumber, currency = "USD", buyer = "Edge Buying Corp", orderDate = "20260608", lines, omitIsa = false }) {
  const seg = [];
  if (!omitIsa) seg.push(x12Isa());
  seg.push("GS*PO*SENDER*RECEIVER*20260608*0830*1*X*004010~");
  seg.push("ST*850*0001~");
  seg.push(`BEG*00*NE*${x12San(poNumber)}**${orderDate}~`);
  seg.push(`CUR*BY*${currency}~`);
  seg.push(`N1*BY*${x12San(buyer)}~`);
  for (const l of lines) {
    seg.push(`PO1*${l.line}*${l.qty}*${x12San(l.uom ?? "EA")}*${l.price ?? "0"}*PE*BP*${x12San(l.sku ?? "")}~`);
    if (l.desc !== undefined && l.desc !== "") seg.push(`PID*F****${x12San(l.desc)}~`);
  }
  seg.push(`CTT*${lines.length}~`);
  seg.push("SE*99*0001~");
  seg.push("GE*1*1~");
  seg.push("IEA*1*000000001~");
  return seg.join("\r\n") + "\r\n";
}

// ── SAP IDoc ORDERS05 builder ────────────────────────────────────────────────
function idoc({ poNumber, buyer = "Edge Buyer", orderDate = "20260608", curcy = "704", sunit = "EUR", grandTotal = "186.01", lines, omitBelnr = false }) {
  const lineXml = lines
    .map(
      (l) => `    <E1EDP01>
      <POSEX>${String(l.line).padStart(5, "0")}</POSEX><MENGE>${l.qty}</MENGE><MENEE>${xmlEsc(l.uom ?? "EA")}</MENEE><VPREI>${l.price ?? "0"}</VPREI><NETWR>${l.netwr ?? ""}</NETWR>
      <E1EDP19><QUALF>002</QUALF><IDTNR>${xmlEsc(l.sku ?? "")}</IDTNR><KTEXT>${xmlEsc(l.desc ?? "")}</KTEXT></E1EDP19>
      ${l.desc ? `<E1EDPT1><E1EDPT2><TDLINE>${xmlEsc(l.desc)}</TDLINE></E1EDPT2></E1EDPT1>` : ""}
    </E1EDP01>`
    )
    .join("\n");
  return `<?xml version="1.0" encoding="UTF-8"?>
<ORDERS05>
  <IDOC BEGIN="1">
    <E1EDK01><CURCY>${xmlEsc(curcy)}</CURCY>${omitBelnr ? "" : `<BELNR>${xmlEsc(poNumber)}</BELNR>`}</E1EDK01>
    <E1EDKA1><PARVW>AG</PARVW><NAME1>${xmlEsc(buyer)}</NAME1></E1EDKA1>
    <E1EDKA1><PARVW>LF</PARVW><NAME1>Edge Supplier OU</NAME1></E1EDKA1>
    ${omitBelnr ? "" : `<E1EDK02><QUALF>001</QUALF><BELNR>${xmlEsc(poNumber)}</BELNR><DATUM>${orderDate}</DATUM></E1EDK02>`}
${lineXml}
    <E1EDS01><SUMME>${grandTotal}</SUMME><SUNIT>${xmlEsc(sunit)}</SUNIT></E1EDS01>
  </IDOC>
</ORDERS05>
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

// 35. CSV with a trailing charge/delivery line (freight) — proves a non-product
//     line is captured as a line (parser is line-shape agnostic; review decides).
emit("csv-35-charge-line.csv",
  csv([H,
    ["PO-CHG", "2026-06-08", "EUR", "Acme Buyer", "1", "BUY-001", "Widget A", "10", "EA", "12.50"],
    ["PO-CHG", "2026-06-08", "EUR", "Acme Buyer", "2", "FREIGHT", "Delivery charge", "1", "EA", "15.00"],
  ]),
  { format: "csv", case: "trailing charge/delivery line", expect: "review" });

// ════════════════════════════════════════════════════════════════════════════
// EDIFACT ORDERS D96A edge cases
// ════════════════════════════════════════════════════════════════════════════

// 36. Clean EDIFACT
emit("edifact-36-clean.edi",
  edifact({ poNumber: "EDI-4001", lines: [
    { line: 1, qty: "10", sku: "BUY-001", price: "12.50", desc: "Widget A", uom: "EA" },
    { line: 2, qty: "5", sku: "BUY-002", price: "8.00", desc: "Widget B", uom: "EA" },
  ] }),
  { format: "edifact", case: "clean ORDERS D96A", expect: "parse" });

// 37. EDIFACT release-escaped delimiters in free text (?: ?+ )
emit("edifact-37-escaped-delims.edi",
  edifact({ poNumber: "EDI-ESC", buyer: "Buyer:Co+Ltd", lines: [
    { line: 1, qty: "1", sku: "ITEM-A", price: "5.00", desc: "Cable A:B+C grade", uom: "EA" },
  ] }),
  { format: "edifact", case: "release-escaped delimiters in text", expect: "parse" });

// 38. EDIFACT 200 lines
{
  const lines = [];
  for (let i = 1; i <= 200; i++) lines.push({ line: i, qty: String((i % 6) + 1), sku: `ITEM-${i}`, price: (3 + (i % 20)).toFixed(2), desc: `Item ${i}`, uom: "EA" });
  emit("edifact-38-200-lines.edi", edifact({ poNumber: "EDI-BIG", lines }),
    { format: "edifact", case: "200 lines", expect: "parse", lines: 200 });
}

// 39. EDIFACT zero/negative quantity
emit("edifact-39-zero-neg-qty.edi",
  edifact({ poNumber: "EDI-QTY", lines: [
    { line: 1, qty: "0", sku: "ITEM-Z", price: "5.00", desc: "zero", uom: "EA" },
    { line: 2, qty: "-3", sku: "ITEM-N", price: "5.00", desc: "neg", uom: "EA" },
  ] }),
  { format: "edifact", case: "zero + negative quantity", expect: "review" });

// 40. EDIFACT missing UNH (not an ORDERS message → reject)
emit("edifact-40-missing-unh.edi",
  edifact({ poNumber: "EDI-BAD", omitUnh: true, lines: [{ line: 1, qty: "1", sku: "X", price: "1.00", desc: "x" }] }),
  { format: "edifact", case: "missing UNH header", expect: "reject" });

// ════════════════════════════════════════════════════════════════════════════
// X12 850 edge cases
// ════════════════════════════════════════════════════════════════════════════

// 41. Clean X12 850
emit("x12-41-clean.x12",
  x12({ poNumber: "X12-5001", lines: [
    { line: 1, qty: "10", sku: "BUY-001", price: "12.50", desc: "Widget A", uom: "EA" },
    { line: 2, qty: "5", sku: "BUY-002", price: "8.00", desc: "Widget B", uom: "EA" },
  ] }),
  { format: "x12", case: "clean 850", expect: "parse" });

// 42. X12 delimiter chars in free text (sanitised, must not inject segments)
emit("x12-42-delims-in-text.x12",
  x12({ poNumber: "X12-DELIM", lines: [
    { line: 1, qty: "1", sku: "B*1>x", price: "1.00", desc: "Has*star>and~tilde", uom: "EA" },
  ] }),
  { format: "x12", case: "delimiter chars in free text", expect: "parse" });

// 43. X12 200 lines
{
  const lines = [];
  for (let i = 1; i <= 200; i++) lines.push({ line: i, qty: String((i % 7) + 1), sku: `ITEM-${i}`, price: (1 + (i % 30)).toFixed(2), desc: `Item ${i}`, uom: "EA" });
  emit("x12-43-200-lines.x12", x12({ poNumber: "X12-BIG", lines }),
    { format: "x12", case: "200 lines", expect: "parse", lines: 200 });
}

// 44. X12 missing ISA (reject)
emit("x12-44-missing-isa.x12",
  x12({ poNumber: "X12-NOISA", omitIsa: true, lines: [{ line: 1, qty: "1", sku: "X", price: "1.00", desc: "x" }] }),
  { format: "x12", case: "missing ISA envelope", expect: "reject" });

// ════════════════════════════════════════════════════════════════════════════
// SAP IDoc ORDERS05 edge cases
// ════════════════════════════════════════════════════════════════════════════

// 45. Clean IDoc — numeric CURCY=704 must be overridden by alphabetic SUNIT=EUR
emit("idoc-45-clean-curcy704.xml",
  idoc({ poNumber: "4501450099", curcy: "704", sunit: "EUR", lines: [
    { line: 1, qty: "50.000", sku: "15728463", price: "1.05", netwr: "52.5", desc: "Short text A", uom: "EA" },
    { line: 2, qty: "10.000", sku: "15728464", price: "13.351", netwr: "133.51", desc: "Short text B", uom: "EA" },
  ] }),
  { format: "idoc", case: "numeric CURCY 704 overridden by SUNIT EUR", expect: "parse" });

// 46. IDoc numeric CURCY with NO SUNIT — currency must NOT leak as "704"
emit("idoc-46-numeric-curcy-no-sunit.xml",
  idoc({ poNumber: "PO-IDOC-NC", curcy: "704", sunit: "", lines: [
    { line: 1, qty: "1.000", sku: "ITEM-A", price: "10.00", netwr: "10.00", desc: "no sunit", uom: "EA" },
  ] }),
  { format: "idoc", case: "numeric CURCY no SUNIT (currency stays null)", expect: "review" });

// 47. IDoc 200 lines
{
  const lines = [];
  for (let i = 1; i <= 200; i++) lines.push({ line: i, qty: String((i % 5) + 1) + ".000", sku: `ITEM-${i}`, price: (2 + (i % 40)).toFixed(2), netwr: "0", desc: `IDoc Item ${i}`, uom: "EA" });
  emit("idoc-47-200-lines.xml", idoc({ poNumber: "IDOC-BIG", lines }),
    { format: "idoc", case: "200 lines", expect: "parse", lines: 200 });
}

// 48. IDoc missing PO number (no BELNR anywhere → reject)
emit("idoc-48-missing-belnr.xml",
  idoc({ poNumber: "X", omitBelnr: true, lines: [{ line: 1, qty: "1.000", sku: "ITEM-A", price: "1.00", netwr: "1.00", desc: "no po" }] }),
  { format: "idoc", case: "missing BELNR / PO number", expect: "reject" });

// ── write manifest ───────────────────────────────────────────────────────────
const byFormat = manifest.reduce((acc, m) => ((acc[m.format] = (acc[m.format] || 0) + 1), acc), {});
const byExpect = manifest.reduce((acc, m) => ((acc[m.expect] = (acc[m.expect] || 0) + 1), acc), {});
const summary = { generatedAt: new Date().toISOString(), total: manifest.length, byFormat, byExpect, files: manifest };
writeFileSync(join(OUT, "manifest.json"), JSON.stringify(summary, null, 2), "utf8");

console.log(`Generated ${manifest.length} edge-case files into ${OUT}`);
console.log("By format:", JSON.stringify(byFormat));
console.log("By expected outcome:", JSON.stringify(byExpect));
console.log("Manifest: out/manifest.json");
