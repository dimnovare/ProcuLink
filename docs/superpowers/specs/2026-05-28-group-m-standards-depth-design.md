# Group M — Standards Depth Design

_Date: 2026-05-28. Approved by founder. Implementation via writing-plans → executing-plans._

_Parent roadmap: `docs/superpowers/plans/2026-05-28-phase-6-international-standard-roadmap.md` (Horizon 2, "Standards Backbone + Channel Breadth")._

---

## Summary

Group M is the "Cinderella shoe — fits any standard" half of ProcuLink's
international-standard thesis. It completes the round-trip story for the two
input parsers already shipped in Wave 1 (UBL 2.1 Order, EDIFACT ORDERS D96A),
adds a new format family (X12 850 in both directions), and turns the standards
matrix into a surfaced, in-app teaching tool that builds trust with both
novices (who learn) and veterans (who verify).

Six independently-mergeable phases, total estimated effort ~17 dev-days. Zero
commercial library spend.

---

## 1. Scope

### 1.1 In scope (7 deliverables across 6 phases)

1. **UBL 2.1 Order outbound transformer** — pairs to shipped `UblOrderParser`;
   emits Peppol BIS Order 3.0-compatible XML.
2. **EDIFACT ORDERS D96A outbound transformer** — pairs to shipped
   `EdifactOrderParser`; emits valid D96A ORDERS messages.
3. **X12 850 parser + transformer** — both directions; new format. North American
   counterpart to EDIFACT ORDERS.
4. **ISO 20022 reference doc** — `docs/standards/iso-20022-po-mapping.md`.
   Documentation only; no parser.
5. **`docs/standards-matrix.md` refresh** — UBL Order and EDIFACT ORDERS
   reclassified from "planned" to "supported"; X12 850 entries added.
6. **In-app standards comparison screen** — `/library/standards` route on the
   frontend; static build-time pipeline reads `docs/standards-matrix.md` into
   `src/lib/standards/catalog.json`.
7. **Conformance fixtures pass** — golden + tampered files per standard, under
   `ProcuLink.Transform.Tests/Fixtures/standards/`.

### 1.2 Out of scope (explicit)

- **Peppol Access Point transport** — Group N (channels).
- **UBL Invoice transmit** — Group S (P2P loop, Horizon 3).
- **EDIFACT ORDRSP / DESADV / INVOIC** — future hardening; trigger to revisit
  hand-rolled vs library decision documented at `EdifactOrderParser.cs:6-30`.
- **OCR / scanned PDF** — already deferred in `docs/standards-matrix.md`.
- **Refactoring shipped parsers onto a shared abstraction** — premature per
  CLAUDE.md ("three similar lines is better than a premature abstraction").
  Revisit when the third EDI format ships in Group S.

### 1.3 Already shipped — do NOT redo

The following were verified on `main` during brainstorming and are out of scope
for Group M:

- `UblOrderParser` (Wave 1, `c395b6c`) — full UBL 2.1 + Peppol BIS Order 3.0
  with `IsUblOrderDocument` peek helper. Registered at `Program.cs:239`.
- `EdifactOrderParser` (Wave 1, `2bd4ecd`) — hand-rolled segment tokenizer
  covering UNA/UNB/UNH/BGM/DTM/NAD/CUX/LIN/IMD/QTY/PRI. Registered at
  `Program.cs:240`.
- `JsonTransformService` — generic canonical JSON output. `OutputFormat.Json`
  is live. Registered at `Program.cs:249`. _Removes deliverable #7 from the
  original Group M prompt; nothing further to build._
- `OrderParserFactory` stream-aware `GetParser(string, Stream)` overload —
  UBL-vs-cXML root disambiguation and EDIFACT `.txt` promotion via `UNA`/`UNB`
  sniff. Extend (not rewrite) for X12 in M.3.

---

## 2. Cross-cutting decisions

### 2.1 No commercial EDI licence

All new EDI work continues the hand-rolled segment-tokenizer pattern shipped
in Wave 1. EdiFabric (~€1,500/yr) is explicitly out per founder budget
constraint (2026-05-28). `indice.Edi` (MIT) was also rejected: attribute/POCO-
driven; needs ~600 LOC of POCOs per EDIFACT version and per X12 message type;
mixes two patterns in the codebase. Decision rationale already encoded in
`EdifactOrderParser.cs:6-30` and reused here.

### 2.2 Peppol BIS Order 3.0 conformance claim

Marketing language must be: **"Peppol BIS Order 3.0 profile-compatible against
the documented test set."** Not "Peppol certified". Peppol certification
requires Access Point accreditation, which is Group N (channels) work and is
explicitly out of scope. The in-app standards screen (M.5) and any sales copy
must repeat this qualifier inline.

### 2.3 Performance budget

Stream-oriented for all four standards:

- UBL transformer → `XmlWriter` (forward-only).
- EDIFACT/X12 → existing line-tokenizer pattern from `EdifactOrderParser`.

Target budget: ≤10MB file in <2s, <100MB process memory. CI test with a 5MB
synthetic file per format added in M.6.

### 2.4 Plan gates

| Format | Plan gate | Backing constant |
|---|---|---|
| cXML | Integration | Existing `BillingFeature.Cxml` |
| UBL | Integration | New `BillingFeature.Ubl` |
| EDIFACT | Integration | New `BillingFeature.Edifact` |
| X12 | Integration | New `BillingFeature.X12` |
| JSON output | Growth | Existing — already gates this way |

New `BillingFeature` enum values added in the earliest phase that needs them
(M.1 for UBL, M.2 for EDIFACT, M.3 for X12). No new tier or pricing change.

### 2.5 Fixture sourcing strategy

| Standard | Source |
|---|---|
| UBL 2.1 / Peppol BIS | OpenPeppol public test set (docs.peppol.eu/poacc/billing/3.0/files/) |
| EDIFACT ORDERS D96A | GS1 published EDIFACT cookbooks (gs1.org/standards/edi) |
| X12 850 | X12.org public sample catalogue + 2 reputable EDI cookbooks (cross-verified) |
| cXML 1.2 | Already shipped — `Tests/Fixtures/sample-order.cxml` (relocate to new layout in M.6) |
| JSON | In-tree synthetic — derive from any UBL/EDIFACT golden via existing JSON transform |

Tampered variants generated by us in-tree (single-character mutations or
section deletions that should reproducibly trip parser validation).

### 2.6 Fixture layout

```
ProcuLink.Transform.Tests/Fixtures/standards/
├── ubl/
│   ├── golden/
│   │   ├── peppol-bis-3-minimal.xml
│   │   └── peppol-bis-3-with-tax.xml
│   └── tampered/
│       ├── missing-id.xml
│       └── empty-orderline.xml
├── edifact/
│   ├── golden/
│   │   ├── d96a-orders-minimal.edi
│   │   └── d96a-orders-multi-line.edi
│   └── tampered/
│       ├── wrong-message-type.edi
│       └── missing-bgm.edi
├── x12/
│   ├── golden/
│   │   ├── 850-minimal.x12
│   │   └── 850-multi-line.x12
│   └── tampered/
│       ├── missing-st.x12
│       └── unterminated-segment.x12
├── cxml/
│   ├── golden/
│   │   └── sample-order.cxml     # relocated from Tests/Fixtures/
│   └── tampered/
│       └── missing-orderid.cxml
└── json/
    └── golden/
        └── canonical-po.json
```

### 2.7 DI registration convention

Each new parser/transformer added to `Program.cs` next to the existing block:

- Parsers at the `IPurchaseOrderParser` registration cluster (currently lines
  235–240).
- Transformers at the `ITransformService` registration cluster (currently
  lines 244–249).

No keyed services, no module extension methods, no DI reorganisation. CLAUDE.md
"three similar lines" rule — keep flat until the third format makes the case.

### 2.8 Phasing rule

Each phase ships its own feature branch (`feat/group-m-w<N>-<slug>`), goes
through `/superpowers:brainstorm` → `/superpowers:write-plan` →
`/superpowers:execute-plan` → `/code-review`, merges to `main`, then deletes
the branch local + remote before starting the next phase. No long-lived shared
branch.

---

## 3. Phases

### M.1 — UBL Order outbound (3 days)

**Goal.** Emit Peppol BIS Order 3.0-compatible XML from a fully-resolved
`PurchaseOrderEntity`. Round-trip parity with the shipped `UblOrderParser`.

**New files.**

- `ProcuLink.Transform/Output/UblOrderTransformService.cs`
- `ProcuLink.Transform.Tests/Output/UblOrderTransformServiceTests.cs`
- `ProcuLink.Transform.Tests/Fixtures/standards/ubl/golden/peppol-bis-3-minimal.xml`
- `ProcuLink.Transform.Tests/Fixtures/standards/ubl/golden/peppol-bis-3-with-tax.xml`

**Modified files.**

- `ProcuLink.Core/Services/ITransformService.cs` — add `OutputFormat.Ubl`.
- `ProcuLink.Core/Constants/BillingFeature.cs` — add `Ubl`.
- `ProcuLink.Api/Program.cs` — register `UblOrderTransformService`.
- `docs/standards-matrix.md` — flip UBL Output from "planned" to "supported".

**Type sketch.**

```csharp
public sealed class UblOrderTransformService : ITransformService
{
    public bool CanTransform(OutputFormat format) => format == OutputFormat.Ubl;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order, OutputFormat format, CancellationToken ct)
    {
        ValidateOrder(order);  // shared pattern from CxmlTransformService

        // XmlWriter forward-only. Emit:
        //   <Order xmlns="...:Order-2">
        //     <cbc:CustomizationID>urn:fdc:peppol.eu:poacc:trns:order:3</cbc:CustomizationID>
        //     <cbc:ID>{order.PoNumber}</cbc:ID>
        //     <cbc:IssueDate>{order.OrderDate:yyyy-MM-dd}</cbc:IssueDate>
        //     <cbc:DocumentCurrencyCode>{order.Currency}</cbc:DocumentCurrencyCode>
        //     <cac:BuyerCustomerParty><cac:Party><cac:PartyName><cbc:Name>...
        //     <cac:SellerSupplierParty><cac:Party><cac:PartyName><cbc:Name>...
        //     <cac:OrderLine><cac:LineItem>... per order.Lines
        //   </Order>

        return Task.FromResult(new TransformResult(
            Content: stream, ContentType: "application/xml", FileExtension: ".xml"));
    }
}
```

**Tests (≥3).** Happy path (round-trip parses back to identical `ParsedOrder`
via `UblOrderParser`), tampered input rejection (`UnresolvedLinesException` for
NeedsReview lines), edge case (empty `BuyerName` is allowed, empty `Lines` is
rejected at validation).

### M.2 — EDIFACT ORDERS outbound (3 days)

**Goal.** Emit valid D96A ORDERS messages from a fully-resolved
`PurchaseOrderEntity`. Round-trip parity with the shipped `EdifactOrderParser`.

**New files.**

- `ProcuLink.Transform/Output/EdifactOrderTransformService.cs`
- `ProcuLink.Transform.Tests/Output/EdifactOrderTransformServiceTests.cs`
- `ProcuLink.Transform.Tests/Fixtures/standards/edifact/golden/d96a-orders-minimal.edi`
- `ProcuLink.Transform.Tests/Fixtures/standards/edifact/golden/d96a-orders-multi-line.edi`

**Modified files.**

- `ProcuLink.Core/Services/ITransformService.cs` — add `OutputFormat.Edifact`.
- `ProcuLink.Core/Constants/BillingFeature.cs` — add `Edifact`.
- `ProcuLink.Api/Program.cs` — register `EdifactOrderTransformService`.
- `docs/standards-matrix.md` — flip EDIFACT Output from "planned" to "supported".

**Type sketch.**

```csharp
public sealed class EdifactOrderTransformService : ITransformService
{
    // Default delimiters per ISO 9735 — no UNA needed unless customer requests.
    private const char Component = ':';
    private const char Element   = '+';
    private const char Segment   = '\'';

    public bool CanTransform(OutputFormat format) => format == OutputFormat.Edifact;

    public Task<TransformResult> TransformAsync(
        PurchaseOrderEntity order, OutputFormat format, CancellationToken ct)
    {
        ValidateOrder(order);

        var sb = new StringBuilder();
        // UNB+UNOC:3+SENDER+RECIPIENT+250528:1200+1'
        // UNH+1+ORDERS:D:96A:UN'
        // BGM+220+{PoNumber}+9'
        // DTM+137:{yyyyMMdd}:102'
        // NAD+BY+++{BuyerName}'  (when BuyerName present)
        // CUX+2:{Currency}:9'
        // For each line:
        //   LIN+{LineNumber}++{SupplierItemCode}:IN'
        //   IMD+F+ANM+:::{Description}'  (when Description present)
        //   QTY+21:{Quantity}:{Unit ?? "EA"}'
        //   PRI+AAA:{UnitPrice}'
        // UNS+S'
        // UNT+{segmentCount}+1'
        // UNZ+1+1'

        return Task.FromResult(new TransformResult(
            Content: stream, ContentType: "application/edifact", FileExtension: ".edi"));
    }
}
```

**Tests (≥3).** Happy path (round-trip parses back to identical `ParsedOrder`),
tampered input rejection (NeedsReview lines), edge case (release-character `?`
in description is escaped correctly).

### M.3 — X12 850 parser + transformer (5–6 days)

**Goal.** Add X12 850 (Purchase Order) as a first-class input and output
format. Same canonical PO target as the other parsers/transformers.

**New files.**

- `ProcuLink.Transform/Parsing/X12OrderParser.cs`
- `ProcuLink.Transform/Parsing/X12ParseException.cs`
- `ProcuLink.Transform/Output/X12OrderTransformService.cs`
- `ProcuLink.Transform.Tests/Parsing/X12OrderParserTests.cs`
- `ProcuLink.Transform.Tests/Output/X12OrderTransformServiceTests.cs`
- `ProcuLink.Transform.Tests/Fixtures/standards/x12/golden/850-minimal.x12`
- `ProcuLink.Transform.Tests/Fixtures/standards/x12/golden/850-multi-line.x12`
- `ProcuLink.Transform.Tests/Fixtures/standards/x12/tampered/missing-st.x12`
- `ProcuLink.Transform.Tests/Fixtures/standards/x12/tampered/unterminated-segment.x12`

**Modified files.**

- `ProcuLink.Core/Services/ITransformService.cs` — add `OutputFormat.X12_850`.
- `ProcuLink.Core/Constants/BillingFeature.cs` — add `X12`.
- `ProcuLink.Api/Program.cs` — register both parser and transformer.
- `ProcuLink.Transform/Parsing/OrderParserFactory.cs` — extend `.x12` and `.edi`
  content-sniff branch with `X12OrderParser.LooksLikeX12` peek helper
  (recognises `ISA*` envelope).
- Upload validation whitelist — accept `.x12` alongside `.edi`.
- `docs/standards-matrix.md` — add X12 850 Input + Output rows as "supported".

**Type sketch.**

```csharp
public sealed class X12OrderParser : IPurchaseOrderParser
{
    // X12 default delimiters from ISA segment positions 104-106.
    // No UNA equivalent — the ISA segment IS the envelope and carries delimiters.
    public bool CanParse(string fileExtension) =>
        string.Equals(fileExtension, ".x12", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikeX12(string head) =>
        head.TrimStart().StartsWith("ISA", StringComparison.Ordinal);

    public async Task<ParsedOrder> ParseAsync(Stream fileStream, CancellationToken ct)
    {
        // 1. Read ISA segment (fixed-width 106 chars + segment terminator).
        // 2. Extract element delimiter (position 104), component delimiter (104),
        //    segment terminator (105 or 106 depending on convention).
        // 3. Validate GS/ST envelope for transaction set 850.
        // 4. Walk:
        //    BEG  → PO number (BEG03), date (BEG05)
        //    REF  → buyer/supplier references
        //    N1   → name (BY = buyer)
        //    CUR  → currency (CUR02)
        //    PO1  → line item (PO101=line, PO102=qty, PO103=uom, PO104=price,
        //                      PO106=catalog/buyer item, PO107=ID qualifier)
        //    PID  → description
        // 5. Validate SE/GE/IEA trailers (segment counts).
    }
}

public sealed class X12OrderTransformService : ITransformService
{
    public bool CanTransform(OutputFormat format) => format == OutputFormat.X12_850;
    // ISA/GS/ST/BEG/REF/N1/CUR/PO1.../CTT/SE/GE/IEA envelope per X12 standard.
}
```

**Tests (≥6).** Parser: happy path, tampered (missing ST), tampered
(unterminated segment), edge case (single-line PO). Transformer: happy path
round-trip, tampered input rejection.

### M.4 — ISO 20022 reference doc + matrix refresh (1 day)

**Goal.** Document where ISO 20022 (FINANCE messages) maps to UBL/Peppol for
procurement. No parser. Increasingly asked about in EU financial integrations
where procurement and finance overlap (e.g. Purchase-to-Pay flows touching
payment initiation).

**New files.**

- `docs/standards/iso-20022-po-mapping.md` — content outline:
  1. Why ISO 20022 is asked about for procurement (FINANCE space, but P2P loop).
  2. What ISO 20022 covers (`pain.*`, `camt.*`, `pacs.*` payment messages).
  3. What it does NOT cover (no purchase-order message in ISO 20022 — the
     financial industry uses UBL/Peppol for trade documents).
  4. Mapping table: ISO 20022 payment fields ↔ UBL Invoice fields ↔ canonical
     ProcuLink invoice model.
  5. "When you should care" — only when a customer integrates payments
     downstream from invoice approval (Group S, Horizon 3).

**Modified files.**

- `docs/standards-matrix.md` — refresh all entries that the brainstorm
  flagged stale:
  - UBL 2.1 Order Input: planned → supported (Wave 1, `UblOrderParser`).
  - UBL 2.1 Order Output: planned → supported (M.1).
  - EDIFACT ORDERS D96A Input: planned/deferred → supported (Wave 1).
  - EDIFACT ORDERS D96A Output: planned/deferred → supported (M.2).
  - JSON Output: planned → supported (`JsonTransformService`, already shipped).
  - X12 850 Input + Output: planned/deferred → supported (M.3).
  - Add ISO 20022 row pointing at the new reference doc.

### M.5 — In-app standards comparison screen (3–4 days)

**Goal.** Surface the standards matrix as an in-product feature so
30-year-veteran procurement leads see "ProcuLink understands my standards" and
first-time users learn how the field they care about maps across formats.

**Frontend repo: `project-proculink`.**

**New files.**

- `content/standards-matrix.md` — vendored copy of the backend
  `docs/standards-matrix.md`. Refreshed via a 1-line `cp` step in the release
  checklist whenever the backend matrix changes.
- `scripts/build-standards-catalog.ts` — Node/bun script. Parses
  `content/standards-matrix.md` into `src/lib/standards/catalog.json`.
- `src/lib/standards/catalog.json` — generated, committed to repo. Single
  source for the screen at request time. CI gate fails the build when the
  parsed catalog has <10 rows.
- `src/lib/standards/types.ts` — TypeScript types for the catalog.
- `src/app/(app)/library/standards/page.tsx` — Server Component renders the
  comparison table.
- `src/components/standards/StandardsTable.tsx` — Client Component for
  filtering by format/direction and expanding a row to see canonical mapping.

**Modified files.**

- `package.json` — add `build-standards` script run before `next build`.
- `src/components/shell/SidebarNav.tsx` — add "Standards" link under Library.

**Content shape.** Reuses the existing standards-matrix structure:

```typescript
type StandardEntry = {
  format: string;             // "cXML 1.2", "UBL 2.1 / Peppol BIS Order 3"
  direction: "input" | "output";
  supportLevel: "supported" | "planned" | "deferred";
  parserClass: string | null;
  validationDepth: string;
  fixtureFile: string | null;
  planGate: "growth" | "operations" | "integration" | "enterprise";
  notes: string;
};

type CanonicalFieldMapping = {
  canonicalField: string;
  perStandard: Record<string, string>;  // "cXML" → "ItemOut/SupplierPartID"
};
```

**Vendored matrix refresh discipline.** The standards-matrix.md lives in the
backend repo (`ProcuLink/docs/standards-matrix.md`); the frontend cannot read
across repos at build time. The frontend keeps a vendored copy at
`project-proculink/content/standards-matrix.md`. Refresh discipline: any PR
that modifies the backend matrix must also bump the frontend copy in the same
landing. Release checklist line: "If backend `docs/standards-matrix.md`
changed this cycle, run `cp ../ProcuLink/docs/standards-matrix.md
content/standards-matrix.md && bun run build-standards`." No CI cross-repo
check (introduces a coupling we don't otherwise have); rely on the release
checklist + the build-time row-count gate to catch drift.

### M.6 — Conformance fixtures pass (2 days)

**Goal.** Raise the test floor from "happy + 1 tampered + 1 edge" per format
to "≥2 golden + ≥2 tampered per format from authoritative sources". Sets up
for a future "test your input" customer-facing feature without rework.

**New files.** All fixtures under `ProcuLink.Transform.Tests/Fixtures/standards/`
per the layout in §2.6. Replace inline test strings in:

- `CxmlOrderParserTests.cs`
- `UblOrderParserTests.cs`
- `EdifactOrderParserTests.cs`
- `JsonTransformServiceTests.cs`
- All four transformer test files added in M.1 / M.2 / M.3.

**Modified files.**

- Relocate `Tests/Fixtures/sample-order.cxml` → `Tests/Fixtures/standards/cxml/golden/`.
- Update any `[InlineData]` test discovery to read from new fixture paths.
- Add `FixtureLoader` test helper in `Tests/Support/` (only if the third test
  file needs it — otherwise inline `File.ReadAllText` per CLAUDE.md).
- Add 5MB synthetic-file performance test per format with `<2s, <100MB` assert.

**Tests added.** ≥1 test per fixture pair (golden parses, tampered throws the
expected exception type). Net new test count: ~12–16 across all four standards.

---

## 4. Decision log

| Decision | Choice | Rationale |
|---|---|---|
| EDIFACT/X12 library | Hand-rolled (no EdiFabric, no `indice.Edi`) | Founder budget; CLAUDE.md "no premature abstraction"; Wave 1 already proved the pattern works |
| Phasing | EU-first sequential (M.1–M.6) | Matches CLAUDE.md "first ICP wedge: buyer/procurement teams sending POs out" and Phase 6 Horizon 2 ordering |
| Standards UI source | Static build-time MD-to-JSON pipeline | Single source of truth in `docs/standards-matrix.md`; zero DB migration; matches existing docs-as-source convention |
| Peppol claim | "BIS 3.0 profile-compatible against documented test set" | Honest; Access Point cert is Group N work |
| Fixture sourcing | OpenPeppol + GS1 + X12.org + cookbooks | Authoritative public sources; cross-verified for X12 |
| Fixture layout | `Tests/Fixtures/standards/{format}/{golden,tampered}/` | Mirrors future "test your input" customer feature without rework |
| Plan gates | All new EDI formats = Integration | Matches existing `BillingFeature.Cxml` precedent |

---

## 5. Risks + mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| X12 850 fixture sourcing thin compared to UN/EDIFACT | Medium | Medium | Source from ≥2 reputable EDI cookbooks (Sterling Commerce, EDI Academy) + X12.org public catalogue; hand-verify against the X12.6 specification |
| Standards-matrix MD-to-JSON parser brittle to markdown table format drift | Low | Low | Lock column structure in the build script with a clear error message; CI check on ≥10 parsed rows |
| Peppol "conformant" language drifts into marketing copy | Medium | High (legal/reputation) | Spec calls out explicit language; standards screen displays qualifier inline; PR review checklist for marketing copy |
| EDIFACT release character (`?`) escaping bug in transformer round-trip | Medium | Medium | Tampered fixture covers a description containing `?`; round-trip test asserts parser sees the literal character |
| Bundle size of `src/lib/standards/catalog.json` if matrix grows | Low | Low | Limit catalog to ~50 rows; if it grows beyond, switch to dynamic import |
| Vendored frontend matrix copy drifts from backend source of truth | Medium | Low | Release checklist line + build-time row-count gate; covered in §M.5 |

---

## 6. Definition of done

A phase is done when:

1. New code compiles clean (`dotnet build ProcuLink.slnx --no-restore`).
2. All new tests pass + full suite stays green (`dotnet test ProcuLink.slnx --no-restore`).
3. Frontend builds clean where applicable (`bun run build` in `project-proculink`).
4. `/code-review` passes with no unresolved high-confidence findings.
5. Feature branch merged to `main`; branch deleted local + remote.
6. `STATUS.md` updated with the phase's commit hash + test count.
7. `docs/standards-matrix.md` reflects the new support level (M.1, M.2, M.3, M.4).

Group M as a whole is done when all six phases above merge and the in-app
standards screen renders the updated matrix on the deployed Vercel URL.
