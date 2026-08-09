# 07 — Content & Copy

> ⚠️ **This file predates the vocabulary purge and is partly wrong.** The founder removed
> `dock` / `crossing` / `lane` / `spine` / "Cross the bridge" from all user-facing copy; they
> survive only as code identifiers, tokens, component names and routes. The vocabulary table
> below, and many of the sample strings after it, still show the purged wording as prescribed
> UI copy. **`00-agent-quick-brief.md` carries the authoritative list and wins over this file.**
> Do not reintroduce the purged wording into user-visible text on this file's authority.

## Vocabulary (use these consistently)

| Term | Meaning | Avoid |
|---|---|---|
| **Bridge** | The product / the dashboard | Platform, hub, app |
| **Crossing** | A single order transit | Run, job, task |
| **Dock** | Buyer or supplier endpoint | Account, organization |
| **Lane** | Buyer↔supplier pairing | Relationship, route |
| **Spine** | The canonical PO model | Schema, document model |
| **Anatomy** | Source-document zone overlay | Annotation, OCR |
| **Wire** | A buyer↔supplier connection on the dashboard | Link, edge, flow |
| **Map / mapping** | Buyer-SKU ↔ supplier-SKU pairing | Translation, mapping rule |
| **Output** | The supplier-ready file we produce | Result, export |

## Page title pairs

| Screen | Title (Display) | Subtitle (Inter) |
|---|---|---|
| Bridge dashboard | "Order flow" | "Today · Mon 12 Jan 2026 · 18 lanes · 6 suppliers" |
| Inbox | "Inbox" | "{n} crossings · last sync {t}" |
| Order review | The PO number (mono) | "{buyer} → {supplier} · {n} lines · {issues} open" |
| Upload | "Cross a new order" | "Drop a buyer order. ProcuLink detects the format." |
| Mappings | "Item mappings" | "{buyer} ↔ {supplier} · {n} mappings" |
| Crossings log | "Crossings log" | "Last 30 days · {n} crossings · {pct}% delivered first-try" |

## Buttons

| Action | Label | Variant |
|---|---|---|
| Submit order to supplier | **"Cross the bridge →"** | primary |
| Save in-progress edits | "Save draft" | secondary |
| Drop into upload zone | "or browse from disk" | ghost (link-style) |
| Approve AI suggestion | "Accept" | ai |
| Reject AI suggestion | "Reject" | ghost |
| Try a failed delivery again | "Fix and resend" | primary |
| Open detail | "Open →" or row click | ghost |
| Destructive | "Delete mapping" | danger |

**Never:**
- "Submit", "Process" → the order-detail primary action is **"Send to supplier"**
- "Apply magic", "Run AI", "Smart fix" → AI is shown, not advertised
- "Click here" → buttons say what they do
- "OK / Cancel" pairs in confirms → name the action ("I've reviewed exceptions. Send to Acme." / "Back")

## Empty states

| Screen | Headline | Sub | Action |
|---|---|---|---|
| Empty Inbox | "No crossings yet." | "Drop a file or connect a buyer dock." | "↑ Upload" + "+ Connect dock" |
| No mappings | "No mappings yet." | "Import a CSV, or let AI suggest from the next order." | "↑ Import CSV" |
| No connectors | "No buyer docks connected." | "Set up an email inbox, SFTP, API or cXML PunchOut." | "+ Add dock" |
| Empty audit log | "No crossings logged in this window." | "Adjust the date range or send a test order." | "Reset filter" |

## Confirms

The confirm before sending must show: **what** (recipient), **how much** (total), **what was reviewed** (exceptions). Operator clicks the action; never "OK".

```
Cross the bridge to Acme Components Ltd.

  Recipient   ops@acme.example.com (SFTP)
  Total       € 4,436.73
  Lines       14 (3 with AI-mapped SKUs)
  Exceptions  3 reviewed and resolved

  [ ] I've reviewed the exceptions.

  [ Back ]  [ Send to Acme ]
```

## Toasts

| Event | Copy |
|---|---|
| Delivered | "Crossed to Acme · accepted · 1m 42s" |
| Saved draft | "Draft saved" |
| Mapping accepted | "Mapped HEI-PLT-09 → ACM-PLT-200×200×4" |
| Auto-process turned on | "Auto-process enabled for Acme. Crossings will skip review when validation passes." |
| Delivery failed | "Crossing to Acme failed — see what Acme said →" |

Toasts auto-dismiss in 5s. Failure toasts persist until dismissed.

## Microcopy in the workflow

| Place | Copy |
|---|---|
| Confidence chip on a field | "84%" (in mono, no "confidence" label) |
| AI source attribution | "AI · 84% — used 6× by Heinrich in last 90d" |
| Validation rule failure | "Acme rejects negative quantities." (the rule's own message) |
| Unmapped SKU | "— unmapped —" |
| UoM translated | "UoM 'CN' → 'EA' for Acme" |
| Duplicate warning | "Possible duplicate of line 1 — same SKU on same PO." |
| Address format mismatch | "Postfach format differs from prior 12 orders." |
| Saved indicator | "Saved 3s ago" (low-contrast, top of canonical column) |

## Marketing copy

### Hero
```
H1   Buyers on one side.
     Suppliers on the other.
     We are the bridge.

Sub  ProcuLink turns messy purchase orders into supplier-ready outputs.
     Upload Excel, PDF, cXML or EDI orders, review only exceptions, deliver clean.

CTA  [ Cross the bridge free for 30 days ]   [ Watch a 90-second walkthrough → ]
```

### Stat block (below hero)
```
84% automation     1m 42s avg crossing     €4.20 per order     9 connectors
```

### Section heads
- *"What you'll stop doing manually"* → 4-up of manual order-processing pains
- *"How a crossing works"* → animated diagram (parse → normalize → validate → transform → deliver)
- *"Built for the messy 80%"* → distributors / wholesalers / resellers
- *"Trust through transparency"* → provenance · no silent automation · failure is loud

## Tone calibration

Read every label out loud. If it sounds like a sales deck, rewrite it. If it sounds like a thoughtful colleague telling you what happened, ship it.

Good:
- *"Acme rejected line 4 — qty was −3. Set qty to 1 and resend?"*
- *"AI matched HEI-PLT-09 to ACM-PLT-200×200×4. Used by Heinrich 6 times in last 90 days."*

Bad:
- *"Oops, something went wrong! Try again 😅"*
- *"Smart AI matching detected a possible mapping ✨"*

## Don'ts

- ✕ Don't use emoji in product UI. (One exception: file-type chips don't have emoji either — they're just text.)
- ✕ Don't use "we" and "you" in the same sentence. Pick a perspective per screen.
- ✕ Don't say "click" — use the verb of the action. *"Open the order"* not *"Click to open."*
- ✕ Don't apologize for the AI being uncertain. Show the confidence and let the operator decide.
- ✕ Don't use questions as confirms. *"Are you sure?"* → *"Delete this mapping?"* with action button.
