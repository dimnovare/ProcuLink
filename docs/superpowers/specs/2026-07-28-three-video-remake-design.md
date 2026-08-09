# ProcuLink three-video remake design

Date: 2026-07-28  
Status: approved direction, ready for implementation planning  
Owner: video production pipeline

## Goal

Remake three ProcuLink videos from a fresh start:

1. **Walkthrough** - the most important video. It appears on the site in the
   How it works / Watch flow and must prove the actual product journey.
2. **Marketing video** - a premium B2B explainer for the homepage, sales follow-up,
   LinkedIn, and paid/organic distribution.
3. **Launch video** - a short announcement cut for social and launch posts.

The videos must feel professional, current, credible, and specific to B2B
purchase-order processing. They should not feel like generic AI SaaS ads.

## Non-negotiables

- **No subtitles, no captions, no SRT output.** The founder explicitly approved
  the direction with this change.
- The walkthrough uses **real product UI** for every product claim.
- Generated video may be used for mood, document motion, and bridge transitions,
  but **never for readable UI**, because generated UI will invent text and controls.
- Use only safe demo data. Do not expose real buyers, suppliers, orders, secrets,
  API keys, or credentials.
- The 3-column order review/workshop screen may be shown if it reflects the
  current approved product, but do not invent a redesigned version in video.
- The story is outbound PO processing for buyer/procurement teams:
  import order, detect/parse, map, validate, preview exact supplier output,
  deliver, and prove what happened.
- Do not imply supplier business acceptance when the product only proves
  transmission/delivery. Keep delivered vs accepted honest.
- Do not over-brand AI. AI is a helper for suggestions and extraction, not the
  whole product.

## Current-state evidence

Existing local assets:

- A local `proculink-launch-v2.mp4` is 40.4 seconds, 1920x1080, H.264 + AAC. It
  has useful brand direction but is now old-product-adjacent and too broad.
- A local `proculink-launch-short.mp4` is 24.3 seconds, 1920x1080, silent.
- The public walkthrough at
  `https://assets.proculink.eu/marketing/walkthrough.mp4` is 126.3 seconds,
  1920x1080, H.264 + AAC, about 8 MB, last modified 2026-06-13.

Existing production tooling:

- The frontend repo already has a repeatable video pipeline under
  `scripts/demo-video`.
- It can generate ElevenLabs voiceover, capture real UI with Playwright in mock
  mode, assemble with ffmpeg, produce posters, and upload to Cloudflare R2.
- The current R2 public bucket is `proculink-public`, served through
  `https://assets.proculink.eu`.

Available creative tools:

- The voiceover provider's API key is held outside the repository, in the
  operator's local secret store. Never commit it, never print it, and do not
  record its path here.
- The image/motion CLI is authenticated and has available credits.
- Use Kling sparingly for abstract document-flow / bridge-motion shots, not for
  product screens.

## Global creative direction

The visual system follows `docs/design-system/00-agent-quick-brief.md`:

- Direction 4, The Bridge Layer, is the main metaphor.
- Direction 3, System Identity, supplies the link mark and glyph language.
- Buyer-side motion is blue; supplier-side success is green.
- Navy chrome, light work area, exact operational UI, calm density.
- Motion communicates state: parse, normalize, validate, transform, deliver.

The emotional tone is:

- Calm, exact, operational.
- Premium but not flashy.
- Useful to a procurement/integration person, not a generic founder video.
- "This makes the messy part controlled" rather than "AI does magic."

## Deliverable 1: Walkthrough

### Purpose

The walkthrough must make a first-time visitor understand how ProcuLink works
and believe the product can handle real PO work. It is the video users watch
when they click "Watch the walkthrough" from `/how-it-works`.

### Target

- Length: **90-110 seconds**
- Format: 1920x1080, 30 fps, H.264 + AAC
- Audio: ElevenLabs voiceover + restrained music bed
- Captions/subtitles: **none**
- Source material: real UI capture only, with limited branded cards for intro
  and close

### Storyboard

| Beat | Target | What appears |
|---|---:|---|
| 1. Open | 5s | Brand card or live `/how-it-works` product pipeline. One sentence: ProcuLink turns buyer POs into supplier-ready orders. |
| 2. Import | 10s | `/upload`: drop or select sample PO. Show accepted sources: upload, email, API, SFTP/S3 as surrounding affordances only if real. |
| 3. Detect | 8s | Format detected, supplier route selected/detected, PO number and line count visible. |
| 4. Review | 14s | Order review/workshop: source on left, canonical data, supplier output. Show only exceptions needing attention. |
| 5. Map/fix | 16s | Accept one suggested supplier item-code match with confidence/provenance. Manually edit one value and save. |
| 6. Validate | 10s | Supplier readiness checks: unresolved fields, rule results, and blocking state if something is wrong. |
| 7. Output preview | 13s | Preview exact supplier output: CSV/XML/cXML/JSON/EDI-like output as configured. Show "what supplier receives" clearly. |
| 8. Deliver | 12s | Send to supplier. Show delivery states and a recoverable failed/unknown distinction only if already in UI. |
| 9. Audit/proof | 8s | Delivery log / audit trail: who changed what, what was sent, when, and result. |
| 10. Close | 5s | ProcuLink lockup: "Send every purchase order in the format each supplier needs." CTA: proculink.eu |

### Draft voiceover direction

Voiceover should be around 170-210 words. It should sound like a confident
operator explaining the product, not a hype reel.

Core message:

> ProcuLink receives purchase orders from the places your buyers already use,
> reads the order, keeps the source visible, resolves only the risky parts,
> validates against supplier rules, generates the exact supplier-ready output,
> delivers it, and records proof for every step.

### Walkthrough acceptance criteria

- Every visible product claim is on a real current UI screen.
- No stale routes, old navigation names, mock-only bugs, loading flashes, or
  "Failed to fetch" banners appear.
- No cursor wandering. Cursor appears only for real clicks/typing, then rests.
- No captions/subtitles are generated or shipped.
- Product states are honest: delivered means endpoint/transmission success, not
  supplier business acceptance.
- Final file is staged for review before overwriting the live R2 object.

## Deliverable 2: Marketing video

### Purpose

A stronger brand/product explainer for the homepage, sales messages, LinkedIn,
and product introduction. It should make the value obvious without requiring a
full feature walkthrough.

### Target

- Length: **35-45 seconds**
- Format: 1920x1080, 30 fps, H.264 + AAC
- Audio: ElevenLabs voiceover + premium restrained music bed
- Captions/subtitles: **none**
- Source material: mix of real UI, generated abstract bridge/document motion,
  and branded motion cards

### Storyboard

| Beat | Target | What appears |
|---|---:|---|
| 1. Problem | 6s | Messy buyer-side formats: PDF, XLSX, CSV, cXML, EDI, email, API, SFTP. Visualized as document cards moving toward the bridge. |
| 2. Bridge | 7s | ProcuLink bridge topology: buyers on one side, suppliers on the other, wires becoming clean routes. Generated/animated brand motion is acceptable here. |
| 3. Control | 8s | Real UI: upload/review screen, confidence/provenance, exceptions only. |
| 4. Exact output | 8s | Real UI: output preview or supplier delivery config. Emphasize exact supplier format/channel. |
| 5. Proof | 7s | Real UI: delivery/audit/log proof. |
| 6. Close | 5s | Brand CTA: "From messy order to supplier-ready output. proculink.eu" |

### Draft copy direction

Possible spine:

> Buyers send orders however they can. Suppliers expect them exactly their way.
> ProcuLink sits between them: reading messy files, resolving risky fields,
> applying supplier rules, and sending the right output over the right channel.
> Your team reviews exceptions, not every line. Every delivery is logged. Every
> supplier flow becomes reusable.

### Marketing acceptance criteria

- Feels premium B2B, not startup stock footage.
- Shows real product UI for the core proof.
- Uses generated footage only for abstract document/bridge motion.
- Ends with clear value and URL.
- No subtitles/captions.

## Deliverable 3: Launch video

### Purpose

A short social announcement that ProcuLink is live and clearly states what it
does. It should be punchier than the marketing video.

### Target

- Length: **18-25 seconds**
- Primary format: 1920x1080, 30 fps, H.264 + AAC
- Optional follow-up crops after approval: 1:1 and 9:16
- Audio: voiceover or music-only with minimal on-screen copy
- Captions/subtitles: **none**

### Storyboard

| Beat | Target | What appears |
|---|---:|---|
| 1. Launch | 3s | ProcuLink lockup. "ProcuLink is live." |
| 2. Problem | 5s | Buyer orders arrive as PDF/XLSX/cXML/EDI/API/email. |
| 3. Product | 7s | Fast real-UI sequence: upload, review exception, output preview, delivered. |
| 4. Promise | 5s | "Send every purchase order to any supplier, in the format they require." |
| 5. CTA | 3s | proculink.eu |

### Launch acceptance criteria

- Understandable with sound off from the visuals and short on-screen copy, but
  no subtitle track.
- Does not over-explain.
- No feature list dumping.
- No old UI.

## Tooling plan

Use the existing frontend demo-video pipeline where possible:

- Add new specs under `project-proculink/scripts/demo-video/tools/`.
- Capture real UI with Playwright in mock mode first.
- Use live/prod capture only if mock mode cannot show a current, truthful state.
- Generate ElevenLabs voiceover per beat.
- Generate a fresh restrained music bed or reuse the best current bed if the new
  one distracts from the voiceover.
- Generate Kling bridge/document clips only after still references and prompts
  are fixed; inspect every output for fake text or UI artifacts.
- Assemble with ffmpeg.
- Produce review copies locally first.

No captions/SRT generation should be added to these three outputs. If the
existing assembler emits SRT by default, either disable that for these builds or
ignore/remove the SRT from the staged artifacts.

## Proposed filenames and staging

Local review copies:

- `walkthrough-2026-07-DRAFT.mp4`
- `marketing-2026-07-DRAFT.mp4`
- `launch-2026-07-DRAFT.mp4`

(kept in the operator's local video folder, outside the repository)

Staged R2 keys after founder approval:

- `marketing/walkthrough-2026-07.mp4`
- `marketing/proculink-marketing-2026-07.mp4`
- `marketing/proculink-launch-2026-07.mp4`

Live replacement only after explicit approval:

- Replace `marketing/walkthrough.mp4` with the approved walkthrough.
- Keep dated files for rollback.

Optional final public URLs:

- `https://assets.proculink.eu/marketing/walkthrough.mp4`
- `https://assets.proculink.eu/marketing/proculink-marketing-2026-07.mp4`
- `https://assets.proculink.eu/marketing/proculink-launch-2026-07.mp4`

## QA checklist

Before review:

- `ffprobe` confirms 1920x1080, 30 fps, H.264 video, AAC audio.
- `ffmpeg` decode check reports zero decode errors.
- Audio volume is comfortable: voice clear, music subordinate.
- Manual frame check across every beat.
- No subtitles, captions, or SRT delivered.
- No secret values visible.
- No stale UI, wrong plan copy, old route names, or broken banners visible.
- No generated text artifacts in Kling shots.
- Walkthrough claim-to-screen check passes.
- File sizes are reasonable for web playback.
- Poster frames are clean and not mid-transition.

## Risks and mitigations

- **Risk: current UI changes while filming.** Mitigation: capture close to final
  approval and prefer one stable mock data set.
- **Risk: Kling creates fake UI/text.** Mitigation: use Kling only for abstract
  document/bridge shots; reject any generated readable text.
- **Risk: walkthrough becomes too long again.** Mitigation: cap to 110 seconds
  and remove secondary features from the main story.
- **Risk: real UI is hard to follow at full-screen density.** Mitigation: crop
  and zoom to the active area per beat, without fabricating UI.
- **Risk: product claims outrun implemented behavior.** Mitigation: each claim
  must map to current code/UI/help docs or be cut.

## Implementation planning notes

The implementation plan should likely split into:

1. Finalize scripts and shot lists.
2. Update or create capture specs for current UI.
3. Generate voiceover and music.
4. Generate and review any Kling bridge/document clips.
5. Assemble review drafts.
6. QA the drafts.
7. Upload staged R2 assets.
8. After explicit founder approval, replace the live walkthrough object and wire
   any marketing/launch references.

Do not upload over the current public walkthrough until the new walkthrough draft
is reviewed and explicitly approved.
