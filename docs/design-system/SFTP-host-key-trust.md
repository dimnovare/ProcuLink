# SFTP host-key trust — supplier → Delivery tab

Inline section, not a modal. Audience: procurement operator who has never heard of a
host key. Plain language, no bridge metaphors, no jargon. Every state offers a next step.

Section title: **Server identity** · sub: *"We check the supplier's server is the same
one every time, so orders and passwords never go somewhere else."*

Card = standard hairline card, 3px left edge coloured by state.

---

## 1 · Not yet connected (edge `borderStrong`, neutral)

- Icon tile neutral + **"Not checked yet"**
- Body: *"The first time we connect, we'll record this server's identity and check it
  matches on every delivery after that."*
- Optional, collapsed by default: **"My supplier gave me a fingerprint"** → mono input,
  placeholder `SHA256:…`, helper *"Paste it to verify even the first connection."*
- Actions: **Save fingerprint** (secondary) · **Test connection** (primary)

## 2 · Trusted (edge `green`)

- `Icon.CheckCircle` green + **"Verified"** + *"Recorded 12 Jun 2026, matched on every
  delivery since."*
- Fingerprint in **mono, selectable, full OpenSSH form**, with a Copy button:
  `SHA256:8f3aK2…c1d0` — operators compare this against `ssh-keygen -lf` output.
- Helper: *"This should match what your supplier's IT team sees."*
- Tertiary: **Forget this server** (text button, `inkMuted` → danger on hover)

## 3 · Refused (edge `danger`, `dangerSoft` tint) — the important one

- `Icon.Warn` danger + **"We stopped the delivery"**
- Plain statement, no accusation, no reassurance:
  *"This server's identity changed since we last connected. That happens when a supplier
  rebuilds their server — but it can also mean the connection is being redirected.
  We didn't send the order, and we didn't send the password."*
- **Two fingerprints stacked, labelled, mono, visually different weights:**
  - `Expected (recorded 12 Jun)` — `SHA256:8f3a…c1d0`
  - `Received today` — `SHA256:b71c…9e42` on `dangerSoft`
- **Next steps, always present:**
  1. **Ask your supplier to confirm the new fingerprint** (primary — this is the real
     first move; opens a prefilled email to the supplier contact)
  2. **Trust the new identity** — *deliberate*: secondary, disabled until a checkbox
     *"My supplier confirmed this change"* is ticked; confirm step names the supplier and
     is logged to the audit trail.
  3. **Retry delivery** — reappears only after re-trust.
- Queued orders shown inline: *"3 orders waiting for this supplier."* → link to Inbox.

## 4 · Pinned (edge `blue`)

- `Icon.Lock` blue + **"Identity set by you"** + *"We check every connection against the
  fingerprint you entered — including the first one."*
- Fingerprint mono + **Edit** / **Remove pin**.
- If a pinned value has matched a live connection, add a green line: *"Matched on last
  delivery, 14:02 today."*

---

## Copy rules applied

- Never "host key", "MITM", "TOFU", "SSH". Say *server identity*, *fingerprint*
  (unavoidable — it's what the operator will be shown by their supplier), *we stopped it*.
- The refusal never guesses which cause it is. It states what we did and what to ask.
- Re-trust is two deliberate acts (checkbox + confirm) and is auditable — never a
  one-click "Trust anyway".
