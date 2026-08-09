# ProcuLink — Routing & Triage Redesign Brief for Claude Design

*Paste this whole document into Claude Design. It is a companion to the main workbench redesign brief (2026-06-26-MASTER-redesign-prompt-for-claude-design.md) and covers ONE hard problem: when many orders arrive on shared channels, how does the user see, trust, and control **which supplier each order is routed to** — without ever sending to the wrong one. Written by someone who knows exactly what the engine does today and what is intended to be built, so design something buildable and honest.*

---

## 0. The problem in one sentence

A procurement team works with many suppliers. Orders pour in from **SFTP, S3, a hosted email address, and a REST API** — often on **one shared channel** (a single SFTP folder, one inbox). ProcuLink must decide **which supplier each order belongs to**, show the user *why* it decided that, let the user fix it in seconds when it's unsure, and **remember the decision** so the next identical order routes itself. The cost of a silent wrong guess is high: an order sent to the wrong supplier. So the design's prime directive is: **be confident when it's safe, ask when it's not, and never auto-send a low-confidence or ambiguous route.**

---

## 1. Ground truth — what exists today vs what this redesign introduces (read first; respect offer ⇔ works)

Design the **target** experience below, but know which parts are real so nothing in the UI lies about a capability:

**Exists today (deterministic, channel-bound):**
- An SFTP folder / S3 prefix is bound to **one** supplier (`DefaultSupplierId`). Every file in it → that supplier.
- A hosted-email address `orders@{slug}.proculink.eu` resolves the **organisation**; the supplier is the org's configured default, and **only** that. With no default configured — or one that no longer resolves — the message parks `unrouted` for a human to route. (Until 2026-07-26 it fell back to the org's oldest active supplier; that guess was deleted, since an emailed order carries no supplier identity of its own.)
- The REST API **requires** the caller to name the supplier in the payload.
- Browser upload: the user picks the supplier.
- The order's parsed content **captures** supplier identity — name, **tax-id/VAT**, **EDI/network code**, address — as structured data (`OrderParty`), and a **schema fingerprint** (column-layout hash) records **which supplier(s) have used that exact layout**, with **collision detection** when a layout is shared by more than one supplier.

**Does NOT exist yet — this redesign is the UI for building it:**
- **Content-based routing.** Nothing today reads the captured VAT / EDI code / name / layout and *auto-matches* the order to a supplier. That logic must be built; this brief designs how the user experiences it.
- **A triage queue for unrouted orders.** Today an order without a resolvable supplier is *rejected at the door* (email 422, API 400, SFTP poll aborts) — it never becomes a reviewable item. The redesign introduces a **holding state**: ingest the order, mark it **Unrouted**, and surface it for a human, instead of silently dropping it.
- **Splitting one shared channel across many suppliers.** Today one folder = one supplier. The redesign lets a shared channel fan out to many suppliers by content.

**Hard honesty rules (non-negotiable):**
- Never present a guessed route as a fact. A medium-confidence match is **"Likely Northwind — confirm,"** not **"Northwind."**
- Never auto-deliver an order whose supplier was guessed below the high-confidence bar or whose layout collides between suppliers. Those go to the human, always.
- Always show the **evidence** behind a match ("matched on VAT NO000000000 + this layout seen 7× for Northwind"). A route the user can't audit is a route they won't trust.

---

## 2. The routing model to design around

### 2a. The signals (what ProcuLink matches on — show these as the "why")
Design the UI so each routing decision can expose, in plain language, which of these fired:
1. **Channel binding** — the file came from a folder/prefix/inbox/API key already tied to a supplier. (Strongest, deterministic.)
2. **Supplier identity on the document** — the supplier's **VAT/tax-id**, **GLN/DUNS**, **EDI/network id**, or **name** printed in the order.
3. **Layout fingerprint** — this column/field shape has been used by supplier X N times before.
4. **Item-code / SKU overlap** — the line codes match supplier X's catalog.
5. **Buyer's own reference / account number** for that supplier.

### 2b. The three confidence tiers (the spine of the whole queue)
Every incoming order lands in exactly one tier. **Color + icon + word**, never color alone:
- **Matched** (high confidence — e.g. deterministic channel binding, or a strong identity + repeated-layout match): **green**. "Matched to {Supplier}." Flows on automatically; the user only spot-checks.
- **Needs confirmation** (medium — one good signal, or signals that don't fully agree): **amber**. "Likely {Supplier} — confirm." One click to accept, or reassign.
- **Unrouted / Ambiguous** (no confident match, or **collision** — two+ suppliers fit): **red / attention**. "Which supplier?" Blocks until a human decides.

### 2c. Learn-for-next-time (the payoff)
When a user confirms or reassigns a route, offer to **remember** it as a rule in plain words:
*"Remember: orders with VAT NO000000000 → Northwind"* or *"Files in /inbound/acme/ → Acme."*
Under the hood this binds the supplier to the fingerprint / channel. Surface it as **one toggle**, never as "fingerprint binding." The felt result: **the second identical order routes itself and leaves the queue without a click.**

---

## 3. Screens to design

### Screen 1 — The Routing Inbox (the home of this feature)
A single, calm, dense operational queue — **not** a dashboard of metrics. One row per incoming order. This is a "control room": the user scans it top-to-bottom and only the unsure rows need them.

- **Top: a tight summary strip**, each a filter, not a vanity chart: **Unrouted (N)** · **Needs confirmation (N)** · **Matched, ready (N)** · **Sent today (N)** · **Failed (N)**. Default sort: **blocking first** (Unrouted → Needs confirmation → Matched).
- **Each row shows:** order identifier (PO #), **where it came from** (a small channel chip: SFTP / S3 / Email / API, with the folder or sender on hover), **buyer**, **the routed supplier with its confidence tier** (green/amber/red chip), a one-line **"why"** (the evidence), arrival time, and the line/value summary.
- **Inline actions per row:** **Confirm** (amber→matched), **Reassign** (open supplier picker), **Open** (full order). No row should require leaving the queue to resolve a simple confirm.
- **Bulk:** select many amber/green rows → **Confirm all** (with an explicit count: "Confirm routing for 12 orders"). Never bulk-confirm reds — they're ambiguous by definition.
- **Density:** tabular figures for counts/amounts, row hover highlight, sortable columns (`aria-sort`), sticky header, horizontal-scroll wrapper for narrow widths. Must stay usable at 1024px — no capability hidden below a breakpoint cliff.

### Screen 2 — The routing decision card (per order, expanded)
When a row is opened or expanded, show the **decision** before the order content:
- **The verdict, loud:** the supplier + tier ("Likely Northwind — 1 of 2 possible") and the **primary action** (Confirm / Choose supplier).
- **The evidence list:** each signal that fired, as a plain bullet with the actual value — *"VAT on document: NO000000000 → Northwind," "This layout seen 7× for Northwind," "Item code BRACKET-S in Northwind's catalog."* Conflicting signals shown honestly (*"but the email came from Acme's folder"*).
- **Then** the received-order summary, so the user can sanity-check identity against content.
- **Remember-this toggle** sits with the confirm action.

### Screen 3 — Disambiguation (the collision case — design this best)
The hardest, most valuable moment: **two or more suppliers fit** (shared layout, or VAT missing). Do NOT make the user guess blind.
- Present the **candidate suppliers side by side** (2–4), each with **its own evidence** and a confidence bar: *"Acme — matches layout (5×), but no VAT match"* vs *"Northwind — matches VAT + layout (7×)."*
- One click selects; immediately offer **"Always route this layout/VAT to {chosen} for next time"** (with a quiet note that a shared layout alone isn't enough to auto-route, which is why it asked).
- If genuinely undecidable, let the user **open the document** inline to read the supplier name, then pick.

### Screen 4 — Channel → supplier setup
Where the user wires a channel once. Keep the simple case one line:
- **Simple:** "Files from this SFTP folder → {supplier}." A picker. Done. (This is today's `DefaultSupplierId`, made legible.)
- **Sub-folder fan-out:** "Each sub-folder is a supplier" → map `/acme/ → Acme`, `/northwind/ → Northwind`.
- **Content routing (the new mode):** "**Let ProcuLink route by what's in the order**" → orders land in the Routing Inbox at their confidence tier instead of being force-assigned. Explain the trade plainly: *"We'll match each order to a supplier and ask you when we're not sure."*
- Show the channel's **health** quietly: last poll, last file, last successful route, and any rejected/held items.

### Screen 5 — The "held / rejected" recovery (replaces today's silent drop)
Because today email-with-no-supplier is rejected and SFTP-with-no-default is skipped, the redesign needs a visible home for **"arrived but couldn't be routed."** A short list: what arrived, from where, why it couldn't route, and **assign supplier / set channel binding / discard**. This is the safety net that turns silent data loss into a one-click fix.

### Screen 6 — Mobile
Mobile is for **triage on the go**, not setup. Show: the inbox summary counts, the Unrouted/Needs-confirmation rows, the decision card with evidence, **Confirm / Reassign**, and the supplier picker. Channel setup and disambiguation-with-document-reading can be **desktop-only** — say so gracefully ("Open on a laptop to set up channel routing").

---

## 4. The supplier picker (used everywhere a route is set)
A searchable command-style picker, because a team may have dozens of suppliers:
- Search by **name, code, or VAT/EDI id**; show each supplier's identifying line so two similarly-named suppliers are distinguishable.
- Surface **best guesses first** (the candidates the engine scored), labelled with their evidence, then the full list.
- "**+ New supplier**" inline for a first-time supplier, without leaving the flow.
- On select: optional **"Remember this routing."**

---

## 5. States (design all of them)
- **Empty (good):** no orders waiting — "Nothing to route. New orders will appear here as they arrive." Not a blank panel.
- **Empty (setup):** no channels connected yet — point to channel setup + "or upload a file."
- **Loading:** orders parsing/being matched — skeleton rows + a calm "Reading and matching new orders…", never a frozen or half-empty screen.
- **All matched:** a quiet "All caught up — N orders routed and ready" with the green tier dominant.
- **Collision present:** the Unrouted count carries the attention color; the disambiguation entry is obvious.
- **Channel error:** SFTP unreachable / email auth failed — a real reason + a fix action, surfaced on the channel and as a held-items note, not a silent skip.
- **Reassigned:** after a manual route, a brief "✓ Routed to {Supplier}" confirmation; if a rule was created, "We'll route matching orders automatically next time."

---

## 6. Color, type, motion
- **Confidence semantics:** Matched = **green**, Needs confirmation = **amber**, Unrouted/Ambiguous = **red/attention**. Channel/source chips = **blue** (buyer/source side). Supplier = **green** side. Always pair color with an icon and a word (color-blind safe).
- **Style:** dense operational table aesthetic ("data-dense dashboard") — calm, light, readable, generous in information but not cramped; minimal chrome, strong row rhythm (4/8px scale), low-contrast gridlines, **tabular figures** for counts and amounts. Serious operational type system (procurement/finance/logistics), not expressive/marketing type. Not card-soup, not playful.
- **Motion = state only:** a new order arriving, a row resolving from amber→green, an order leaving the queue once routed. Stagger row entrance subtly (30–50ms). No ambient animation. Respect reduced-motion.

---

## 7. Exact UI copy (use these)
- Tiers: **"Matched to {Supplier}"** · **"Likely {Supplier} — confirm"** · **"Which supplier?"**
- Evidence lead-ins: **"Matched on VAT {value}"** · **"This layout was used by {Supplier} {n} times"** · **"Item code {code} is in {Supplier}'s catalog"** · **"From {Supplier}'s folder"**
- Actions: **"Confirm routing"** · **"Choose supplier"** · **"Reassign"** · **"Remember this routing"** · **"Confirm routing for {n} orders"**
- Collision: **"Two suppliers could match this order. Pick the right one — we'll remember it."**
- Held: **"Arrived but we couldn't tell which supplier this is for."** + **"Assign supplier"**
- Setup: **"Let ProcuLink route by what's in the order"** + helper **"We'll match each order to a supplier and ask you when we're not sure."**

**Ban in normal UI:** fingerprint, hash, default_supplier_id, tenant, ingress, payload, canonical, 422/400, "schema," "binding" (use "routing rule"), "Guid."

---

## 8. The signature interaction
**The confidence-tiered routing inbox where ambiguous orders rise to the top and resolving one row teaches the next.** The user opens it, sees green rows already handled, confirms a column of amber in one motion, and spends their attention only on the few reds — each of which, once resolved with "remember this," never comes back. It turns "which of my 30 suppliers is this for?" from a per-order chore into a queue that shrinks itself.

---

## 9. Deliverables
1. The Routing Inbox (desktop) in full.
2. The routing decision card with evidence.
3. The disambiguation / collision screen.
4. Channel → supplier setup (simple, sub-folder, content-routing modes).
5. The held/rejected recovery list.
6. The supplier picker.
7. Mobile triage screens.
8. Empty / loading / all-matched / collision / error / reassigned states.
9. Exact copy for every action and tier.
10. How a confirmed route becomes an automatic rule (the learn loop), shown without jargon.

## 10. Quality bar
A coordinator with 30 suppliers and a shared SFTP folder should clear a morning's intake in minutes: glance, confirm the obvious, decide the few ambiguous ones with the evidence in front of them, and trust that nothing was silently sent to the wrong supplier. Powerful routing, obvious controls, zero blind guesses.
