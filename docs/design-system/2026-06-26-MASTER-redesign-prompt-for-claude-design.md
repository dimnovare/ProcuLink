# ProcuLink — MASTER Redesign Prompt for Claude Design

*This is the single, definitive brief. Paste the whole thing. It supersedes the earlier full-product briefs. Design a completely new product from scratch. Written by someone who knows exactly what the engine does, what it cannot do, and where today's UI fails — so design a real product, not a plausible-looking one.*

---

## SCOPE — read first

**In scope:** the full product redesign — every screen a user touches to take one purchase order from "messy file in" to "sent to supplier," plus the reusable supplier setup and the work queue.

**Out of scope for this redesign (do NOT design it): supplier routing.** "Which supplier does this incoming order belong to?" — the matching of orders arriving on shared SFTP/email/API channels to the right supplier — is a **separate track** being built independently. For this redesign, **assume each order already has its supplier** (either the channel is bound to one supplier, or the user picks the supplier on import). Do not design a routing inbox, a triage queue, confidence-tier matching, or channel-fan-out. The Import and Work Queue screens here are about *one known order*, not about deciding its supplier.

---

## 1. Start from scratch

Ignore the current ProcuLink UI. Do not polish existing screens. Do not reuse the current dashboard / persistent admin sidebar / card-heavy / topology-diagram layout. Do not build a generic B2B SaaS UI. **Design the product model first, then pages.** Pick one strong concept and commit. It should feel like the best tool in its category, not an admin panel.

---

## 2. The one rule that overrides everything: offer ⇔ works

ProcuLink is a real, shipped engine. **Every control you draw must map to something the engine actually does, and the UI must never imply a capability the engine doesn't have.** If a screen offers "design your cXML structure" but cXML is a fixed standard the engine assembles automatically, that screen is a lie that breaks trust the first time it's used. Read §3 before designing a single screen. When unsure, *show* what's happening (read-only, "filled automatically") rather than *offer* control that doesn't exist.

Three invariants the whole design must protect:
- **Preview == what is sent.** The output preview renders the *actual bytes* that will be delivered — never a mockup. This is the most reassuring fact in the product. Make it loud.
- **One check → one gate.** The same plain-language validation that lists "what's wrong" is the *only* thing between the user and Send. There is never a hidden reason it won't send. "Fix these 3 things" *is* the gate.
- **HTTP 200 ≠ acceptance.** A supplier can return 200 and still reject the order. Delivery UI must distinguish "delivered" from "supplier accepted" and surface real rejections.

---

## 3. Ground truth — what ProcuLink actually is and does

**One line:** ProcuLink turns a messy purchase order (any format) into the *exact* file a specific supplier accepts, validates it in plain language, previews the real output, sends it, and **learns the setup** so the next order from that buyer→supplier pair flows through with near-zero clicks.

**Who it replaces (north-star scenario).** A real customer receives a PDF purchase order from a buyer (e.g. REDACTED-PARTY, Gjensidige). Today they run **three tools**: DocParser (PDF→flat XML) → Altova MapForce (a hand-built mapping → cXML) → an admin screen (credentials, send). ProcuLink collapses all three into **one flow**: drop the PDF → it reads it → confirm a couple of item codes → see the exact cXML → send. **The design's job is to make that three-tool, EDI-expert chain feel like dropping a file and pressing Send.**

### 3a. The pipeline (every order)
`Read → Understand (the received order) → Map to the supplier's shape → Validate → Fix exceptions → Preview the real output → Send → Learn.`

### 3b. Intake (the formats/channels — but NOT routing, see Scope)
File upload (**CSV, Excel/XLSX, PDF, XML, cXML, UBL, EDIFACT, X12/EDI**), forwarded/hosted **email** with an attachment, **REST API**, **SFTP/S3**, **IMAP**. Default UI = "drop a file"; everything else is "more ways to receive orders," progressively disclosed. PDFs are read by AI; scanned/image PDFs are OCR'd or vision-read and **every line is flagged for human review** — assisted, never silently auto-sent.

### 3c. "Understand" — the received order (the join key)
ProcuLink parses any input into one internal **received order**: header (PO number, order date, currency, buyer, supplier, totals, tax, payment terms, **buyer tax-id**), parties (**ship-to / bill-to** name + full address, **contact** name/email/phone), and lines (line no, buyer item code, **supplier item code**, description, quantity, unit, unit price, line amount, **tax rate/amount**, delivery date). The user thinks in these plain fields. Internally each maps to standards (UBL `cbc:ID`, X12 `BEG03`, cXML `OrderRequestHeader@orderID`) — **surface that only on demand** (an info icon / command-palette "show standards mapping"), never by default. Procurement veterans trust the product *because* they can see it; novices never need to.

### 3d. "Map to the supplier's shape" — the critical distinction you MUST get right
There are **two kinds of output**, needing different UI. Do not conflate them:

- **Structured standards — cXML, UBL, X12, EDIFACT.** Fixed, non-negotiable document shape defined by the standard. The user does **not** build or rearrange the structure. The engine assembles it from the received order + a little **per-supplier config** (network credentials/identity, envelope, optional DTD). Addresses, contact, VAT/tax, totals, buyer identity all **emit automatically** — the user must **see** them ("filled automatically from the order," read-only) but does **not** hand-map them. The usual only per-line decision is confirming the **supplier item code**. → For these formats: **show, don't offer.** No structure editor. A read-only "this is what the supplier receives" + an "Advanced" drawer for the genuinely configurable bits (credentials, identity, DTD).

- **Flat / templated formats — CSV, Excel, JSON, XML(generic).** The supplier's shape is arbitrary, so the user **does** design it. This is where the sample-first **Output Designer** lives (screen 7): paste the supplier's example → infer structure → bind fields → live preview → save as the supplier's template. **The only place a "design the output" experience belongs.**

The **honest message** when a user on a structured format reaches for "add a field": *"This supplier uses a structured format (cXML). Fields like contact and addresses are filled in automatically from the order — editing fields here won't change what's sent. To change the structure, edit the supplier's setup."*

### 3e. The flexible part that IS the magic: bind any received field
For any output field the user can bind **any** field that was received — not just the ~14 canonical names, but *any* column/cell/leaf/segment from the original file (an unrecognized CSV column, an XML attribute, an EDI segment). For lines, "this column, per line." This is what makes ProcuLink lossless vs a rigid template. In the UI: **pick where this value comes from** — a searchable list of received fields, each showing its real value — with "remember for next time." Never expose it as "source tokens" or paths.

### 3f. "Validate + Fix" — plain-language exceptions
The engine checks the order against the supplier's requirements and flags issues in plain language (not "rule max 50000 failed"): *"Line 2 has no supplier item code," "the delivery date format isn't one this supplier accepts," "line total doesn't match quantity × price."* Each carries a **suggested fix** with **confidence + source** (existing mappings, catalog, AI). The user **accepts / edits / skips**; some issues **block sending**, some are warnings. AI suggestions are shown as suggestions (subtle, with confidence), **never auto-applied**. **Bulk-accept** the confident ones.

### 3g. "Preview" — the Output Mirror
The exact supplier-ready bytes, framed **"This is exactly what {Supplier} receives."** Same content that gets delivered. A format read-out (the supplier's real format), copy, download. Raw text on demand; readable by default. Flat formats update live as the user designs; structured formats show the assembled standard document.

### 3h. "Send" — delivery you can trust
Channels: **HTTP/API, SFTP, FTPS, email/SMTP, Erply ERP, Directo ERP.** States: ready → sending → **delivered** / **supplier rejected** / **failed** / **retrying**. On failure: explain what happened, show the supplier's response if any, offer retry, keep a quiet audit trail. (Remember invariant: 200 ≠ accepted.)

### 3i. "Learn" — the Supplier Flow (the moat + the reusable setup)
A **Supplier Flow** is the reusable setup for one buyer→supplier relationship: accepted input examples, the field mapping, the validation requirements, the output format, the delivery method, the last test result, the last successful send. It is **versioned under the hood** (set up → test → make live; every order pins the version that produced it, so results are reproducible and rollback is possible) — **but hide all that vocabulary.** The user sees: *Edit mapping · Test · Make live · Restore a previous version.* The payoff to make *felt*: **set it up once; the second order from that supplier needs almost no clicks** — ProcuLink recognizes the shape and re-applies the saved flow. That "it just remembered" moment is the whole pitch vs MapForce, where every document type means hand-maintaining a mapping file forever.

### 3j. What's already true — keep the *truth*, replace the surface
Real received values shown (not placeholders). Per-field honest status (not "everything red"). One validator → one Send gate. Preview == delivered bytes. Honest disabled-with-reason controls. These are correct; the redesign keeps the truth while replacing the layout.

### 3k. The current UI's real failures (what you're fixing)
- A **1280px responsive cliff**: below it the field-mapper *disappears* — a 13"/14" laptop loses the core capability. Must work down to a normal laptop.
- **Three stacked "here are your issues" surfaces** + a dense header → the actual work sits below the fold. Collapse to one calm path, one primary action.
- Output rows that **lead with the machine path** (`cbc:ID`, `BEG03`) instead of the human field name.
- A field-by-field mapper shown for **every** order even when the supplier is already learned (should default to "review & send," mapping behind a step).
- Residual jargon and a structure-editor that misleads for fixed standards (§3d).

---

## 4. The product model

Three plain concepts the product revolves on: **Received order · Required fixes · Supplier output.**

Three surfaces:
- **The Order Studio** — one focused workspace for one order. Answers, in order: *What came in? What did ProcuLink understand? What does this supplier need? What's missing or risky? What will be sent? Can I send now?* One order moving toward "ready" — not a row of disconnected tabs.
- **The Supplier Flow** — the reusable, self-learning setup for one buyer→supplier pair (§3i). Shows setup completeness and what's missing.
- **The Output Mirror** — the live, exact "this is what the supplier will receive." Readable by default; advanced/raw on demand; **structure-editing only for flat formats** (§3d).

**Design principle:** at every moment the user knows *where they are, what still blocks sending, what ProcuLink suggests, and what happens when they press the main button.*

---

## 5. The main flow (one coherent system, not isolated pages)
1. Start with a purchase order (drop a file; or it arrived by a channel, with its supplier already known — see Scope).
2. ProcuLink reads + detects fields → the **received order**.
3. The user confirms only the **uncertain** fields (a learned supplier ⇒ often nothing).
4. ProcuLink shows **blocking issues** in plain language.
5. The user accepts / edits fixes (bulk-accept the confident ones).
6. The user previews the **exact supplier output**.
7. The user **sends** (delivery states + honest failures).
8. ProcuLink **saves/learns** the flow for next time.

Show the *movement* — one order travelling from "messy in" to "sent."

---

## 6. Screens to design from scratch

1. **First launch (no orders, no flows).** Make the next action obvious in 30s: *Drop a purchase order · Try a sample order · Set up a supplier.* One honest sentence: "Turn any purchase order into the exact file your supplier accepts — and we'll remember the setup."

2. **Work Queue (replaces the dashboard — no vanity metrics).** Operational, by order state: *needs fixes · ready to send · sent today · failed deliveries · suppliers needing setup.* Counts that map to a click, not charts. (No routing/triage here — see Scope.)

3. **Import.** Default = drop a file. Behind "More ways to receive": forward email, REST API, SFTP/S3, IMAP. Show accepted formats honestly. Don't surface tokens/credentials up front. Where a supplier must be chosen for an uploaded file, a simple picker — not a routing system.

4. **Order Studio (the heart — most of your effort).** Received document/data · detected header · detected lines · the supplier's requirements · the issue list · AI suggestions (subtle) · mapping confirmation (only what's uncertain) · the Output Mirror · the **one** primary Send. A *learned* supplier opens on "review & send," not a full mapper; an *unfamiliar* one guides setup. Lead every output row with the **human field name**; the standard path is a quiet secondary.

5. **Fixing issues (best-in-class).** Per issue: plain problem · affected field/line · why it matters · suggested fix · confidence + source · accept / edit / skip · does-it-block. Real examples to design against: *supplier item code missing · delivery date wrong format · quantity missing · currency not accepted · line total ≠ qty × price · supplier requires a buyer reference · scanned-PDF line needs review.* Inline resolution — never bounce the user elsewhere. One ordered list, blocking first.

6. **Mapping (simple by default).** Default per row: *example received value · what ProcuLink thinks it means · the supplier field it fills · confidence · "remember for next time."* Must work for a non-technical buyer. Advanced (disclosed): bind any received field (§3e), per-line binding, conditions ("only when…"), transforms (trim, reformat date, find/replace, default-when-empty, multiply/divide), fixed values, formatting. **No technical wire-graph as the default.**

7. **Output Designer (flat formats only — §3d).** Sample-first: *paste/upload the supplier's example output → detect structure → bind fields → live preview → validation shows missing required fields → rename/reorder, add fixed values, add conditions, format dates/numbers/currency → save as the supplier's template.* The feeling is **"make the supplier output look like this,"** not "configure a schema pipeline." For **structured standards** there is no structure editor — a read-only "this is what's sent" + an Advanced drawer for credentials/identity/DTD. The format choice must make this obvious.

8. **Supplier Flow Builder.** Guided setup for one supplier: details · accepted input examples · mapping · validation rules · output format · delivery method · **test send** · go live. A visible completeness meter ("3 of 5 set up; missing: delivery method") and a real **Test flow** before go-live. Hide the versioning vocabulary; keep the safety (test before live, restore previous).

9. **Delivery result (trust).** ready · sending · **delivered** · **supplier rejected** · failed · retrying. On failure: what happened, the supplier's response if available, a clear fix/retry, a quiet audit trail. Make "delivered" feel earned, "rejected" actionable; never imply 200 = accepted.

10. **Mobile (designed from scratch).** For: checking the work queue, reviewing an order's issues, accepting suggestions, previewing the output summary, sending/retrying, seeing delivery status. The full Output Designer is desktop-only — say so gracefully ("Open on a laptop to edit the output layout"). Never silently hide the mapper on a small screen the way today's product does.

---

## 7. States (every screen)
Empty (no orders / no suppliers / no issues = green "ready to send"), loading (**"We're reading your order…"** while a PDF parses, auto-advancing — not a half-empty screen), error (parse / transform / delivery failed, each with a real reason + retry), dirty vs saved (a calm "✓ Saved"), blocked vs ready, AI-suggestion-present, scanned-PDF-needs-review, plan/limit reached.

---

## 8. Color, type, motion
- **Color = meaning:** buyer/source = **blue** · supplier/output = **green** · blocking = **red** · warning/uncertain = **amber** · AI suggestion = subtle **violet/blue** (never sparkly) · success = **green** · calm readable background. Never color-only — pair with icon + word.
- **Typography:** serious, operational, dense-but-readable — belongs in procurement / finance / logistics, not marketing. Tables and forms read cleanly; **tabular figures** for prices/quantities/totals so columns don't jitter.
- **Motion = state, never decoration:** file being read, field detected, issue resolved, output updated, order sent. No ambient floating dots. Respect reduced-motion.

---

## 9. Naming
Use: **Order Studio · Supplier Flow · Received order · Required fixes · Supplier output · Output preview · Ready to send · Remember this · Send to supplier · Test flow · Filled automatically.**
Never show in normal UI: *canonical · parser · transformer · AST · schema · token · source token · webhook config · immutable · conformance · revision · draft/publish · raw JSON/XML/EDI editor as the default · Scriban · payload.* (They exist in the engine; they stay invisible.)

---

## 10. Remove / hide / automate
**Remove:** the metrics dashboard, the persistent admin sidebar, the abstract topology/bridge diagram (unless it provably helps finish an order), the "design your structure" editor for cXML/UBL/X12/EDIFACT, any tab row that fragments one order, duplicate "here are your issues" surfaces.
**Hide as Advanced (one disclosure, discoverable via a command palette):** standards mappings, bind-any-field / per-line binding, conditions/transforms/fixed-values, XML namespaces, cXML credentials/identity/DTD, raw output text, the connection version history.
**Automate (the user never touches these):** detecting the input format, parsing to the received order, emitting addresses / contact / VAT / totals / buyer identity into structured output, recognizing a learned supplier and re-applying its saved flow.

---

## 11. The signature interaction
Make the **Before/After Mirror** the signature: the **received order on one side, the exact supplier output on the other,** always visible, with the issue list and the single Send button as the spine between them. As the user resolves an issue or confirms a field, the right side updates to the real bytes in real time. It teaches the whole product in one glance — *messy in, exact out, I can see the difference* — and it's the honest embodiment of "preview == what's sent." (If you find a stronger single concept — a "translation studio" translating the buyer order into the supplier's language — commit to it fully, but preserve the three invariants in §2.)

---

## 12. Deliverables
1. New product concept name + rationale.
2. New information architecture (how the three surfaces + the work queue connect).
3. Main **desktop** screens.
4. Main **mobile** screens.
5. **Order Studio** in full detail (the heart).
6. **Output Designer** in full detail — explicitly how it differs for **structured standards** (show/config) vs **flat formats** (sample-first design).
7. **Supplier Flow Builder** in full detail (completeness + test-before-live).
8. Empty / error / loading / success states.
9. **Exact UI copy** for the important actions and the example errors in §6.5.
10. What to remove from the current product.
11. What to hide as advanced.
12. What to automate.
13. The signature interaction and why it's useful.

---

## 13. Quality bar
A non-technical procurement coordinator should process an order **without training**. An integration expert should still control output details (via disclosure, not by default). Powerful but **obvious** — new not because it's flashy, but because it finally makes a painful three-tool B2B workflow *understandable*. Make a nervous coordinator feel **certain**: one calm path, plain words, real received values, the real output beside them, one safe Send.

Be bold. If the sidebar/dashboard model is wrong, replace it. If the mapping/output mental model is wrong, invent a better one. But never offer a capability the engine doesn't have (§2), never break preview-equals-delivery or one-check-equals-the-gate, and **do not design supplier routing — that's a separate track (see Scope).**
