# ProcuLink — Walkthrough Video Brief (for Lovable + ElevenLabs)

> **Purpose:** produce a ~110-second product walkthrough video. Final MP4 lives in
> Cloudflare R2 and is embedded on the marketing `/watch` page.
> **Note:** Lovable is used here ONLY as a video/voiceover production tool. Per
> `CLAUDE.md`, no Lovable-generated code goes into the ProcuLink app.

---

## 1. Product context (so Lovable understands what it's narrating)

**What ProcuLink is:** a B2B outbound procurement bridge. Buyer/procurement teams
import a purchase order in whatever format they have (email, PDF, CSV, Excel, XML,
cXML), and ProcuLink validates it, maps supplier item codes, transforms it into the
exact format each supplier requires, and delivers it — with a full audit trail.

**Core loop:** Parse → Normalize → Validate → Review exceptions → Transform → Deliver → Learn.

**Who it's for:** buyer/procurement operations teams, distributors, and industrial
wholesalers (50–400 employees) sending 100–500 POs/month to 3–20 suppliers — today
handled by hand in Excel.

**The one-line value:** *"Stop reformatting purchase orders. Start delivering them."*

**Tone:** calm, confident, operational. NOT hypey, NOT "AI robot." This is a tool a
30-year procurement veteran trusts. The vibe is **boringly reliable**.

---

## 2. Video specs

| Spec | Value |
|---|---|
| Length | 100–115 seconds |
| Aspect ratio | 16:9, 1920×1080 (also export a 1:1 1080×1080 cut for LinkedIn if easy) |
| Format | MP4, H.264, ~8–12 Mbps, AAC audio |
| Captions | Burned-in optional; also export an `.srt` for the embed |
| Music | Soft, minimal, low bed — must sit under the VO, never compete |
| Final home | Cloudflare R2 bucket `proculink`, embedded on `/watch` |

---

## 3. Brand / visual system

Use the live ProcuLink "Bridge Layer" design language. Confirm exact tokens against
the running app, but the palette is:

| Token | Hex |
|---|---|
| Navy base | `#0B1A2F` |
| Primary blue | `#1E66C9` |
| Success green | `#2E8E3A` |
| AI / accent violet | `#6F4FCE` |
| Soft blue surface | `#E3EDFB` |
| Soft green surface | `#E2F1E2` |

Visual motifs: dark navy base, faint grid, left-to-right flow, crisp chips with subtle
borders, reduced-motion (calm, not bouncy). Screen recordings should be of the **real
app at `proculink.eu`**, not mockups.

---

## 4. Narrative arc (8 scenes, ~110s)

| # | Time | On-screen action | Voiceover (ElevenLabs) |
|---|---|---|---|
| 1 | 0–12s | Montage of messy inbound: an email with a PDF attached, a CSV opening in Excel, a raw XML file | "Every supplier wants your purchase orders a different way. A PDF here. A CSV there. cXML, EDI, a custom XML. So your team reformats orders by hand — and the mistakes get expensive." |
| 2 | 12–22s | ProcuLink dashboard / Bridge visual fades in, clean and calm | "ProcuLink is the bridge. One place to take any incoming order and send it out in exactly the format and channel each supplier needs." |
| 3 | 22–37s | `/upload` — drag the sample order file in; the format-detection confidence pill appears | "Drop in an order — CSV, Excel, PDF, XML, or cXML. ProcuLink detects the format automatically and gets to work." |
| 4 | 37–52s | Order detail (`/inbox/[orderId]`) — three lines parsed, buyer "Northwind Trading OÜ", status journey lighting up | "It parses every line, and surfaces only the lines that actually need your attention — not the whole order." |
| 5 | 52–68s | Magic mapping preview — an AI-suggested supplier code with a confidence score and a short reason; click Accept | "When a supplier uses different item codes, the AI suggests the mapping — with a confidence score and the reason behind it — and it remembers your corrections for next time." |
| 6 | 68–82s | Validation panel — order checked against the supplier's acceptance rules; green passes | "Before anything goes out, the order is checked against that supplier's own acceptance rules — so it isn't rejected at the other end." |
| 7 | 82–98s | Transform to cXML, then Deliver; status journey shows Parse ✓ Normalize ✓ Validate ✓ Transform ✓ Deliver ● | "Then ProcuLink transforms the order into the supplier's required format, and delivers it — over HTTP, SFTP, email, or straight into their ERP." |
| 8 | 98–112s | PO Passport / audit trail + delivery attempts; end card with logo + URL | "Every step is logged — a complete audit trail and proof of delivery for each order. Boringly reliable, by design. ProcuLink — try it with a sample order today." |

---

## 5. ElevenLabs voiceover — clean script

> Voice direction: professional, neutral-European English. Mid pace (~150 wpm).
> Calm and assured, slight warmth. No upspeak, no hard-sell energy. Short pauses
> between scenes.

```
Every supplier wants your purchase orders a different way. A PDF here. A CSV there.
cXML, EDI, a custom XML. So your team reformats orders by hand — and the mistakes
get expensive.

ProcuLink is the bridge. One place to take any incoming order and send it out in
exactly the format and channel each supplier needs.

Drop in an order — CSV, Excel, PDF, XML, or cXML. ProcuLink detects the format
automatically and gets to work.

It parses every line, and surfaces only the lines that actually need your attention —
not the whole order.

When a supplier uses different item codes, the AI suggests the mapping — with a
confidence score and the reason behind it — and it remembers your corrections for
next time.

Before anything goes out, the order is checked against that supplier's own acceptance
rules — so it isn't rejected at the other end.

Then ProcuLink transforms the order into the supplier's required format, and delivers
it — over HTTP, SFTP, email, or straight into their ERP.

Every step is logged — a complete audit trail and proof of delivery for each order.
Boringly reliable, by design. ProcuLink — try it with a sample order today.
```

---

## 6. Recording checklist (screen capture from the real app)

- [ ] Use a clean demo organisation on `proculink.eu` (real org name shown — no "Nordic Distribution").
- [ ] Use the built-in **"Try with sample order"** flow → order `DEMO-2026-001`, buyer "Northwind Trading OÜ", 3 lines, EUR 150.30, supplier codes SUP-001/002/003.
- [ ] Record at 1920×1080, hide bookmarks bar / OS clutter, use a neutral browser theme.
- [ ] Capture each scene's screen with a steady, slow cursor — no fast jitter (matches reduced-motion brand).
- [ ] Scene 7: have a supplier delivery config pointed at a controlled endpoint so the status journey reaches **Deliver ✓** (a real `delivered` / code 200), not a failure.
- [ ] Scene 8: open the PO Passport / audit view showing the logged steps + delivery attempt.

---

## 7. Wiring the finished video into `/watch`

The video sits in R2 as an MP4 (not Loom). Today `/watch` expects a Loom iframe via
`NEXT_PUBLIC_WALKTHROUGH_LOOM_URL`. Small frontend change needed:

1. Upload final MP4 to R2 bucket `proculink` (e.g. `marketing/walkthrough-v1.mp4`),
   expose it via a public/CDN URL.
2. Add env var `NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL` (the R2/CDN MP4 URL).
3. Update `src/app/(marketing)/watch/page.tsx`: if `NEXT_PUBLIC_WALKTHROUGH_VIDEO_URL`
   is set, render an HTML5 `<video controls poster=...>` player; else fall back to the
   existing Loom iframe; else (neither set) the page link stays hidden.
4. Until the video is live, `/watch` is **hidden from nav + CTAs** (no "being recorded"
   placeholder shown to visitors).

---

## 8. Deliverables back from Lovable

- [ ] Final 16:9 MP4 (1080p) — the walkthrough.
- [ ] Optional 1:1 1080×1080 social cut.
- [ ] `.srt` caption file.
- [ ] A poster/thumbnail still (1920×1080) for the `<video poster>`.
