# ProcuLink Three-Video Remake Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a new flagship walkthrough, a B2B marketing film, and a short launch film from current ProcuLink UI, with professional voice and sound, no subtitles, strict QA, and a reversible staged release.

**Architecture:** Add an isolated `scripts/demo-video/films` production pipeline to the Next.js repository so the existing published per-tool videos are not disturbed. Film specifications drive ElevenLabs narration, strict Playwright capture, branded cards, ffmpeg assembly, poster creation, and automated QA; Kling is limited to two abstract bridge/document clips for the marketing and launch cuts. All three drafts are reviewed locally before dated R2 staging, and the live walkthrough object is replaced only after explicit founder approval.

**Tech Stack:** Next.js 15 mock mode, TypeScript, Playwright 1.60, Node.js 20-22, bun, ElevenLabs TTS, Kling video generation, ffmpeg/ffprobe, Cloudflare R2/Wrangler.

## Global Constraints

- No subtitles, no captions, no SRT output.
- Walkthrough product claims use real current product UI.
- Generated video is allowed only for abstract document/bridge motion, never readable UI.
- Use only safe fictional demo data; never show real orders, credentials, tokens, email addresses, or customer names.
- Keep the current approved three-column order review screen unchanged.
- Story is buyer/procurement outbound PO processing: import, parse, map/fix, validate, preview exact output, deliver, audit.
- “Delivered” means endpoint/transmission success, not supplier business acceptance.
- AI appears as an evidence-backed helper, not as magic or the whole product.
- Visual direction is Direction 4 “Bridge Layer”, supported by Direction 3 “System Identity”.
- Buyer-side visual language is blue; supplier-side success is green; chrome is navy; work areas are light.
- Cursor appears only around a real click or typing action and is hidden at rest.
- Required capture actions fail the build when selectors or expected states are missing.
- Outputs are 1920x1080, 30 fps, H.264 video, AAC stereo audio, `yuv420p`, and `+faststart`.
- Walkthrough target duration is 90-110 seconds.
- Marketing target duration is 35-45 seconds.
- Launch target duration is 18-25 seconds.
- Never overwrite `proculink-public/marketing/walkthrough.mp4` without explicit founder approval after local draft review.
- Frontend repository commands use bun, never npm or yarn.

---

## File Structure

Create the following isolated production area in
`%USERPROFILE%\source\repos\project-proculink`:

```text
scripts/demo-video/films/
├── README.md
├── film-spec.ts
├── film-spec.test.ts
├── capture-helpers.ts
├── render-film-cards.mjs
├── generate-film-vo.mjs
├── assemble-film.mjs
├── verify-film.mjs
├── walkthrough-2026-07.json
├── capture-walkthrough-2026-07.spec.ts
├── marketing-2026-07.json
├── capture-marketing-2026-07.spec.ts
├── launch-2026-07.json
├── capture-launch-2026-07.spec.ts
├── prompts/
│   ├── marketing-bridge.md
│   └── launch-documents.md
└── out/                         # generated; gitignored
```

Create `playwright.films.config.ts` at the frontend repository root. Modify
`package.json` only to add the film commands, and modify `.gitignore` only to
ignore generated film output.

The design/production documentation remains in the backend repository:

```text
docs/superpowers/specs/2026-07-28-three-video-remake-design.md
docs/superpowers/plans/2026-07-28-three-video-remake.md
```

---

### Task 1: Add the strict film-spec and capture foundation

**Files:**
- Create: `project-proculink/scripts/demo-video/films/film-spec.ts`
- Create: `project-proculink/scripts/demo-video/films/film-spec.test.ts`
- Create: `project-proculink/scripts/demo-video/films/capture-helpers.ts`
- Create: `project-proculink/playwright.films.config.ts`
- Modify: `project-proculink/package.json`
- Modify: `project-proculink/.gitignore`

**Interfaces:**
- Consumes: existing Next.js mock mode on port `8090`.
- Produces: `FilmSpec`, `FilmBeat`, `loadFilmSpec(id)`, `validateFilmSpec(spec)`,
  `FilmClock`, `prepareFilmPage(page)`, `FilmCursor`, `required(locator, label)`,
  and a Playwright config matching `capture-*.spec.ts` under `films/`.

- [ ] **Step 1: Write the failing film-spec tests**

Create `scripts/demo-video/films/film-spec.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { validateFilmSpec } from "./film-spec";

const valid = {
  id: "walkthrough-2026-07",
  title: "ProcuLink walkthrough",
  targetSeconds: { min: 90, max: 110 },
  intro: {
    kicker: "How ProcuLink works",
    headline: "From buyer PO to supplier-ready order",
  },
  outro: {
    headline: "Send every purchase order in the format each supplier needs.",
    cta: "proculink.eu",
  },
  beats: [
    {
      id: "open",
      kind: "ui",
      route: "/upload",
      vo: "ProcuLink turns buyer purchase orders into supplier-ready orders.",
      shot: "Current upload screen.",
    },
  ],
};

describe("validateFilmSpec", () => {
  it("accepts a valid specification", () => {
    expect(validateFilmSpec(valid)).toEqual(valid);
  });

  it("rejects subtitle or caption output", () => {
    expect(() =>
      validateFilmSpec({ ...valid, captions: true }),
    ).toThrow(/captions and subtitles are forbidden/i);
  });

  it("rejects generated footage for a UI beat", () => {
    expect(() =>
      validateFilmSpec({
        ...valid,
        beats: [{ ...valid.beats[0], source: "generated" }],
      }),
    ).toThrow(/ui beats must use real capture/i);
  });

  it("rejects duplicate beat ids", () => {
    expect(() =>
      validateFilmSpec({
        ...valid,
        beats: [valid.beats[0], valid.beats[0]],
      }),
    ).toThrow(/duplicate beat id/i);
  });
});
```

- [ ] **Step 2: Run the test and confirm it fails**

Run:

```powershell
bun run test -- scripts/demo-video/films/film-spec.test.ts
```

Expected: FAIL because `./film-spec` does not exist.

- [ ] **Step 3: Implement typed specification validation**

Create `scripts/demo-video/films/film-spec.ts` with these exported contracts:

```ts
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { z } from "zod";

const here = dirname(fileURLToPath(import.meta.url));

const beatSchema = z.object({
  id: z.string().min(1),
  kind: z.enum(["ui", "brand", "abstract"]),
  source: z.enum(["capture", "generated", "card"]).optional(),
  route: z.string().startsWith("/").optional(),
  vo: z.string().min(1),
  shot: z.string().min(1),
  actionLeadMs: z.number().int().nonnegative().optional(),
  extraMs: z.number().int().nonnegative().optional(),
  overIntro: z.boolean().optional(),
  overOutro: z.boolean().optional(),
});

const filmSchema = z.object({
  id: z.string().min(1),
  title: z.string().min(1),
  targetSeconds: z.object({
    min: z.number().positive(),
    max: z.number().positive(),
  }),
  captions: z.boolean().optional(),
  intro: z.object({
    kicker: z.string().min(1),
    headline: z.string().min(1),
  }),
  outro: z.object({
    headline: z.string().min(1),
    cta: z.string().min(1),
  }),
  beats: z.array(beatSchema).min(1),
});

export type FilmBeat = z.infer<typeof beatSchema>;
export type FilmSpec = z.infer<typeof filmSchema>;

export function validateFilmSpec(input: unknown): FilmSpec {
  const spec = filmSchema.parse(input);
  if (spec.captions === true) {
    throw new Error("Captions and subtitles are forbidden for these films.");
  }
  if (spec.targetSeconds.min >= spec.targetSeconds.max) {
    throw new Error("Film duration minimum must be less than maximum.");
  }
  const ids = spec.beats.map((beat) => beat.id);
  if (new Set(ids).size !== ids.length) {
    throw new Error("Duplicate beat id in film specification.");
  }
  for (const beat of spec.beats) {
    if (beat.kind === "ui" && beat.source === "generated") {
      throw new Error("UI beats must use real capture, never generated footage.");
    }
  }
  return spec;
}

export function loadFilmSpec(id: string): FilmSpec {
  return validateFilmSpec(
    JSON.parse(readFileSync(resolve(here, `${id}.json`), "utf8")),
  );
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run:

```powershell
bun run test -- scripts/demo-video/films/film-spec.test.ts
```

Expected: 4 tests PASS.

- [ ] **Step 5: Implement strict recording helpers**

Create `scripts/demo-video/films/capture-helpers.ts` by reusing the useful cursor
and timing mechanics from `tools/demo-helpers.ts`, with these mandatory changes:

```ts
import { expect, type Locator, type Page } from "@playwright/test";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import type { FilmSpec } from "./film-spec";

const here = dirname(fileURLToPath(import.meta.url));

export async function required(locator: Locator, label: string): Promise<Locator> {
  await expect(locator, `Required film element missing: ${label}`).toBeVisible({
    timeout: 12_000,
  });
  return locator;
}

export class FilmClock {
  private readonly start = Date.now();
  readonly markers: Record<string, number> = {};

  mark(id: string) {
    this.markers[id] = (Date.now() - this.start) / 1000;
  }

  since(id: string) {
    return Date.now() - (this.start + this.markers[id] * 1000);
  }

  save(spec: FilmSpec) {
    const dir = resolve(here, "out", spec.id);
    mkdirSync(dir, { recursive: true });
    writeFileSync(
      resolve(dir, "markers.json"),
      JSON.stringify(this.markers, null, 2),
      "utf8",
    );
  }
}

export function narrationBudgets(spec: FilmSpec, padMs = 300) {
  const manifestPath = resolve(here, "out", spec.id, "vo", "manifest.json");
  const measured = JSON.parse(readFileSync(manifestPath, "utf8")) as Array<{
    id: string;
    durationSec: number;
  }>;
  const durationById = Object.fromEntries(
    measured.map((item) => [item.id, item.durationSec * 1000]),
  );
  return Object.fromEntries(
    spec.beats.map((beat) => [
      beat.id,
      Math.round(
        durationById[beat.id] + padMs + (beat.extraMs ?? 0),
      ),
    ]),
  );
}

export async function prepareFilmPage(page: Page) {
  await page.addInitScript(() => {
    window.localStorage.setItem(
      "proculink_cookie_consent_v1",
      "functional-only",
    );
    const style = document.createElement("style");
    style.textContent =
      'span[title^="You are viewing mock data"],nextjs-portal,' +
      "[data-next-badge-root],[data-nextjs-toast]," +
      "[data-nextjs-dev-tools-button]{display:none!important;}" +
      "*{caret-color:transparent}";
    document.documentElement.appendChild(style);
  });
}

export async function saveFilmVideo(page: Page, filmId: string) {
  const outputDirectory = resolve(here, "out", filmId);
  mkdirSync(outputDirectory, { recursive: true });
  const video = page.video();
  if (!video) {
    throw new Error(`Playwright did not create a video for ${filmId}.`);
  }
  const destination = resolve(outputDirectory, "capture.webm");
  await page.close();
  await video.saveAs(destination);
  return destination;
}
```

Implement `FilmCursor` with cursor visibility tied only to real interaction:

```ts
export class FilmCursor {
  private x = 960;
  private y = 620;

  constructor(private readonly page: Page) {}

  private async setVisible(visible: boolean) {
    await this.page.evaluate((show) => {
      document.body.dataset.plkCursor = show ? "on" : "off";
    }, visible);
  }

  private async glideTo(locator: Locator, durationMs = 520) {
    await required(locator, "cursor target");
    await locator.scrollIntoViewIfNeeded();
    const box = await locator.boundingBox();
    if (!box) throw new Error("Required cursor target has no bounding box.");
    const targetX = box.x + box.width / 2;
    const targetY = box.y + box.height / 2;
    const frames = Math.max(8, Math.round(durationMs / 16));
    const startX = this.x;
    const startY = this.y;
    for (let index = 1; index <= frames; index += 1) {
      const raw = index / frames;
      const eased =
        raw < 0.5
          ? 2 * raw * raw
          : 1 - Math.pow(-2 * raw + 2, 2) / 2;
      await this.page.mouse.move(
        startX + (targetX - startX) * eased,
        startY + (targetY - startY) * eased,
      );
      await this.page.waitForTimeout(14);
    }
    this.x = targetX;
    this.y = targetY;
  }

  async click(locator: Locator) {
    await this.setVisible(true);
    await this.glideTo(locator);
    await this.page.waitForTimeout(160);
    await this.page.mouse.down();
    await this.page.waitForTimeout(90);
    await this.page.mouse.up();
    await this.page.waitForTimeout(220);
    await this.hide();
  }

  async type(locator: Locator, value: string) {
    await required(locator, "typing target");
    await this.setVisible(true);
    await this.glideTo(locator);
    await locator.click();
    await this.page.keyboard.press("Control+a");
    await this.page.keyboard.press("Delete");
    await this.page.keyboard.type(value, { delay: 52 });
    await this.hide();
  }

  async hide() {
    await this.setVisible(false);
  }
}
```

`prepareFilmPage()` must also inject the existing 26 px green/navy demo cursor
and a mutation observer that shows it only while
`document.body.dataset.plkCursor === "on"`. Do not include a fail-soft wrapper
for required shots.

- [ ] **Step 6: Add the isolated Playwright film config**

Create `playwright.films.config.ts`:

```ts
import { defineConfig, devices } from "@playwright/test";

const port = process.env.DEMO_PORT ?? "8090";

export default defineConfig({
  testDir: "./scripts/demo-video/films",
  testMatch: /capture-.*\.spec\.ts/,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: "list",
  timeout: 900_000,
  outputDir: "./scripts/demo-video/films/out/.playwright",
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? `http://127.0.0.1:${port}`,
    viewport: { width: 1920, height: 1080 },
    video: { mode: "on", size: { width: 1920, height: 1080 } },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    actionTimeout: 12_000,
    navigationTimeout: 45_000,
    launchOptions: {
      args: ["--force-color-profile=srgb", "--hide-scrollbars"],
    },
  },
  projects: [
    {
      name: "chromium",
      use: {
        ...devices["Desktop Chrome"],
        viewport: { width: 1920, height: 1080 },
      },
    },
  ],
  webServer: {
    command: "bun run dev:demo",
    url: `http://127.0.0.1:${port}`,
    timeout: 120_000,
    reuseExistingServer: true,
    env: {
      PROCULINK_QA_BYPASS_AUTH: "true",
      NEXT_PUBLIC_QA_BYPASS_AUTH: "true",
      NEXT_PUBLIC_USE_MOCK: "true",
      NEXT_PUBLIC_API_BASE_URL:
        process.env.PLAYWRIGHT_API_URL ?? "http://localhost:5223",
      NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY:
        "pk_test_ci_placeholder_not_real",
      CLERK_SECRET_KEY: "sk_test_ci_placeholder_not_real",
    },
  },
});
```

- [ ] **Step 7: Add commands and generated-output ignore**

Add these scripts to `package.json`:

```json
{
  "film:vo": "node scripts/demo-video/films/generate-film-vo.mjs",
  "film:capture": "playwright test --config=playwright.films.config.ts",
  "film:assemble": "node scripts/demo-video/films/assemble-film.mjs",
  "film:verify": "node scripts/demo-video/films/verify-film.mjs"
}
```

Add to `.gitignore`:

```gitignore
scripts/demo-video/films/out/
```

- [ ] **Step 8: Verify the foundation**

Run:

```powershell
bun run test -- scripts/demo-video/films/film-spec.test.ts
bunx playwright test --config=playwright.films.config.ts --list
```

Expected: spec tests pass; Playwright loads the config and lists no capture
tests until Task 3.

- [ ] **Step 9: Commit**

```powershell
git add .gitignore package.json playwright.films.config.ts scripts/demo-video/films
git commit -m "feat: add strict film production pipeline"
```

---

### Task 2: Add no-caption voice, cards, assembly, and QA

**Files:**
- Create: `project-proculink/scripts/demo-video/films/generate-film-vo.mjs`
- Create: `project-proculink/scripts/demo-video/films/render-film-cards.mjs`
- Create: `project-proculink/scripts/demo-video/films/assemble-film.mjs`
- Create: `project-proculink/scripts/demo-video/films/verify-film.mjs`
- Create: `project-proculink/scripts/demo-video/films/README.md`

**Interfaces:**
- Consumes: `<film-id>.json`, `out/<film-id>/capture.webm`,
  `out/<film-id>/markers.json`, optional `out/<film-id>/abstract/*.mp4`.
- Produces: `out/<film-id>.mp4`, `out/<film-id>-poster.jpg`,
  `out/<film-id>/qa/report.json`, `out/<film-id>/qa/contact-sheet.jpg`.
- Explicitly does not produce `.srt`, caption streams, subtitle streams, or
  burned-in narration text.

- [ ] **Step 1: Implement ElevenLabs generation**

Adapt the proven key-loading and silence-trimming behavior from
`tools/generate-tool-vo.mjs`. The new script must:

```js
const VOICE_ID =
  process.env.ELEVENLABS_VOICE_ID ?? "onwK4e9ZLuTAKqWW03F9";
const MODEL =
  process.env.ELEVENLABS_MODEL ?? "eleven_multilingual_v2";
const STABILITY = Number(process.env.ELEVENLABS_STABILITY ?? "0.62");
const SIMILARITY = Number(process.env.ELEVENLABS_SIMILARITY ?? "0.8");
const STYLE = Number(process.env.ELEVENLABS_STYLE ?? "0");
const SPEED = Number(process.env.ELEVENLABS_SPEED ?? "1.06");
```

For each beat, write:

```text
out/<film-id>/vo/<beat-id>.mp3
out/<film-id>/vo/manifest.json
out/<film-id>/voiceover-script.txt
```

`--dry-run` writes only the review script and performs no API call. The key is
read from `ELEVENLABS_API_KEY` or
`%USERPROFILE%\.proculink-secrets\elevenlabs.key` and is never logged.

- [ ] **Step 2: Implement branded card rendering**

`render-film-cards.mjs` exports:

```js
export function renderFilmCards(spec, outputDirectory) {
  return {
    intro: resolve(outputDirectory, "cards", "intro.png"),
    outro: resolve(outputDirectory, "cards", "outro.png"),
  };
}
```

Render at 1920x1080 with Playwright or ImageMagick using only:

- ProcuLink link mark and wordmark from the committed design-system assets.
- Navy background `#0B1A2F`.
- Buyer blue `#2773D2`.
- Supplier green `#299447`.
- White primary type and restrained mono operational labels.
- Intro kicker/headline from the film spec.
- Outro headline and `proculink.eu`.

Do not place narration sentences on the cards.

- [ ] **Step 3: Implement the no-caption assembler**

`assemble-film.mjs <film-id>` must:

1. Validate the film spec.
2. Fail if required capture, markers, VO manifest, or music are missing.
3. Trim footage to the first marker.
4. Place each VO clip at its marker.
5. Place a beat with `overIntro: true` over the intro card and a beat with
   `overOutro: true` over the outro card; those beats require no capture marker.
6. Concatenate intro, captured/generated beats in spec order, and outro.
7. Mix VO above a restrained music bed.
8. Output H.264/AAC 1080p30 with `yuv420p` and `+faststart`.
9. Write a clean poster from a real UI frame.
10. Never create `.srt` or add subtitle streams.

Use these default audio controls:

```js
const MUSIC_VOLUME = Number(process.env.FILM_MUSIC_VOLUME ?? "0.10");
const OUTPUT_GAIN = Number(process.env.FILM_OUTPUT_GAIN ?? "1.45");
const INTRO_SECONDS = Number(process.env.FILM_INTRO_SECONDS ?? "2.4");
const OUTRO_SECONDS = Number(process.env.FILM_OUTRO_SECONDS ?? "3.0");
```

The final ffmpeg encode must include:

```text
-c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p
-r 30 -c:a aac -b:a 192k -ar 48000 -ac 2
-movflags +faststart
```

- [ ] **Step 4: Implement automated QA**

`verify-film.mjs <film-id>` must fail non-zero unless all are true:

```js
const expected = {
  videoCodec: "h264",
  audioCodec: "aac",
  width: 1920,
  height: 1080,
  frameRate: 30,
  audioChannels: 2,
  subtitleStreams: 0,
  decodeErrors: 0,
};
```

It must also:

- enforce the film spec’s duration range;
- reject files with no audio stream;
- report mean and peak volume;
- reject peak audio above `-0.5 dB`;
- create one JPEG per beat;
- create a 4-column contact sheet;
- scan the film output directory and fail if an `.srt`, `.vtt`, or `.ass` file
  exists;
- write `out/<film-id>/qa/report.json`.

The report structure is:

```json
{
  "filmId": "walkthrough-2026-07",
  "passed": true,
  "durationSeconds": 101.2,
  "video": {
    "codec": "h264",
    "width": 1920,
    "height": 1080,
    "fps": 30
  },
  "audio": {
    "codec": "aac",
    "channels": 2,
    "meanDb": -20.4,
    "peakDb": -2.1
  },
  "subtitleStreams": 0,
  "decodeErrors": 0
}
```

- [ ] **Step 5: Document exact local commands**

Create `scripts/demo-video/films/README.md` with:

```powershell
# Review narration without API calls
bun run film:vo -- walkthrough-2026-07 --dry-run

# Generate narration
bun run film:vo -- walkthrough-2026-07

# Capture current UI
bun run film:capture -- capture-walkthrough-2026-07

# Assemble and verify
bun run film:assemble -- walkthrough-2026-07
bun run film:verify -- walkthrough-2026-07
```

Document that only files in `films/out/` are generated, and that R2 publication
is a separate approval-gated task.

- [ ] **Step 6: Syntax-check the production scripts**

Run:

```powershell
node --check scripts/demo-video/films/generate-film-vo.mjs
node --check scripts/demo-video/films/render-film-cards.mjs
node --check scripts/demo-video/films/assemble-film.mjs
node --check scripts/demo-video/films/verify-film.mjs
```

Expected: all commands exit 0.

- [ ] **Step 7: Commit**

```powershell
git add scripts/demo-video/films
git commit -m "feat: add no-caption film assembly and QA"
```

---

### Task 3: Produce the flagship walkthrough

**Files:**
- Create: `project-proculink/scripts/demo-video/films/walkthrough-2026-07.json`
- Create: `project-proculink/scripts/demo-video/films/capture-walkthrough-2026-07.spec.ts`

**Interfaces:**
- Consumes: Task 1 strict capture helpers and Task 2 voice/assembly pipeline.
- Produces: a 90-110 second real-UI walkthrough draft and poster.

- [ ] **Step 1: Add the approved walkthrough script**

Create `walkthrough-2026-07.json` with `captions: false` and these exact beats:

```json
{
  "id": "walkthrough-2026-07",
  "title": "ProcuLink walkthrough",
  "captions": false,
  "targetSeconds": { "min": 90, "max": 110 },
  "intro": {
    "kicker": "How ProcuLink works",
    "headline": "From buyer PO to supplier-ready order"
  },
  "outro": {
    "headline": "Send every purchase order in the format each supplier needs.",
    "cta": "proculink.eu"
  },
  "beats": [
    {
      "id": "open",
      "kind": "brand",
      "source": "card",
      "vo": "ProcuLink turns purchase orders from the formats buyers use into the exact orders each supplier expects.",
      "shot": "Short brand opener followed by the current upload workbench.",
      "overIntro": true
    },
    {
      "id": "import",
      "kind": "ui",
      "source": "capture",
      "route": "/upload",
      "vo": "Start with the file you already have. Upload a spreadsheet, CSV, PDF, XML, cXML, or EDI order. Email, API, and managed file intake can feed the same workflow.",
      "shot": "Upload a fictional CSV and show the real intake affordances.",
      "extraMs": 500
    },
    {
      "id": "detect",
      "kind": "ui",
      "source": "capture",
      "route": "/upload",
      "vo": "ProcuLink detects the format, reads the order, keeps the source visible, and routes it to the selected supplier flow.",
      "shot": "Detected format, PO number, line count, and selected supplier are visible."
    },
    {
      "id": "review",
      "kind": "ui",
      "source": "capture",
      "route": "/inbox/ord-002",
      "vo": "The order opens in review. Source, normalized data, and supplier output stay connected, while the work queue focuses attention on the exceptions that can block delivery.",
      "shot": "Current approved three-column order workshop; do not redesign it."
    },
    {
      "id": "suggest",
      "kind": "ui",
      "source": "capture",
      "route": "/inbox/ord-002",
      "vo": "For an unresolved item, ProcuLink suggests the supplier code with confidence and evidence. Accept it when it is right.",
      "shot": "Accept one real mock AI suggestion and show the issue count decrease.",
      "extraMs": 250
    },
    {
      "id": "manual-fix",
      "kind": "ui",
      "source": "capture",
      "route": "/inbox/ord-002",
      "vo": "When you know better, enter the value yourself. Saving the mapping makes the same buyer-to-supplier match reusable on the next order.",
      "shot": "Enter a supplier item code, save, and show the line resolved.",
      "extraMs": 500
    },
    {
      "id": "validate",
      "kind": "ui",
      "source": "capture",
      "route": "/inbox/ord-002",
      "vo": "Before sending, supplier rules check required fields, totals, item mappings, and output readiness. Blocking issues stay visible until they are fixed.",
      "shot": "Readiness and validation area with a truthful ready state."
    },
    {
      "id": "preview-output",
      "kind": "ui",
      "source": "capture",
      "route": "/inbox/ord-002",
      "vo": "Preview exactly what the supplier will receive. The configured template controls the structure, field mappings, format, and delivery channel.",
      "shot": "Switch to full document or supplier output preview with readable output."
    },
    {
      "id": "deliver",
      "kind": "ui",
      "source": "capture",
      "route": "/inbox/ord-002",
      "vo": "Send when the order is ready. ProcuLink generates the artifact, transmits it on the configured channel, and reports delivery success or a recoverable failure.",
      "shot": "Confirm send and show generating, sending, then delivered transmission state.",
      "extraMs": 1000
    },
    {
      "id": "audit",
      "kind": "ui",
      "source": "capture",
      "route": "/delivery-log",
      "vo": "The audit trail records what changed, who changed it, what was generated, where it was sent, and the result. Your source remains intact throughout.",
      "shot": "Current delivery log and order audit proof, without implying business acceptance."
    },
    {
      "id": "close",
      "kind": "brand",
      "source": "card",
      "vo": "Review the exceptions. Control the output. Send every supplier the order they require. ProcuLink.",
      "shot": "Branded closing card.",
      "overOutro": true
    }
  ]
}
```

- [ ] **Step 2: Dry-run and review the narration duration**

Run:

```powershell
bun run film:vo -- walkthrough-2026-07 --dry-run
```

Expected: the script contains 180-215 words, no unsupported claims, and no
caption/SRT file.

- [ ] **Step 3: Implement the strict walkthrough capture**

Create `capture-walkthrough-2026-07.spec.ts` using the current v13 test only as a
selector reference. The required sequence is:

```ts
const SAMPLE_CSV =
  "po_number,buyer_name,line_no,item_code,description,quantity,unit_price,currency\n" +
  "PO-2026-004417,Nordic Electronics,1,TB-CAP-100,Capacitor 100uF,200,0.35,EUR\n" +
  "PO-2026-004417,Nordic Electronics,2,TB-RES-220,Resistor 220R,500,0.02,EUR\n" +
  "PO-2026-004417,Nordic Electronics,3,TB-WIRE-22,Wire 22AWG Black 100m,5,12.50,EUR\n";

test("film: walkthrough 2026-07", async ({ page }) => {
  const spec = loadFilmSpec("walkthrough-2026-07");
  const budgets = narrationBudgets(spec);
  const clock = new FilmClock();
  const cursor = new FilmCursor(page);

  await prepareFilmPage(page);

  await page.goto("/upload", { waitUntil: "networkidle" });
  await required(
    page.getByRole("heading", { name: /upload an order/i }),
    "upload heading",
  );
  clock.mark("import");

  const chooserPromise = page.waitForEvent("filechooser");
  await cursor.click(page.getByRole("button", { name: /browse files/i }).first());
  const chooser = await chooserPromise;
  await chooser.setFiles({
    name: "PO-2026-004417.csv",
    mimeType: "text/csv",
    buffer: Buffer.from(SAMPLE_CSV),
  });
  await required(page.getByText(/detected:/i).first(), "detected format");
  clock.mark("detect");

  await page.goto("/inbox/ord-002", { waitUntil: "networkidle" });
  await required(page.getByText(/PO-2024-005678/i).first(), "review PO");
  clock.mark("review");

  await cursor.click(
    page.getByRole("button", {
      name: /accept ai suggestion for line 2/i,
    }),
  );
  clock.mark("suggest");

  await cursor.click(
    page.getByRole("button", {
      name: /enter a supplier code manually for line 4/i,
    }),
  );
  await cursor.type(
    page.getByLabel(/supplier code for line 4/i),
    "ES-WIRE-22BK-100",
  );
  await cursor.click(page.getByRole("button", { name: /^save$/i }).first());
  clock.mark("manual-fix");

  await required(
    page.getByText(/ready to send|all required fields/i).first(),
    "supplier readiness",
  );
  clock.mark("validate");

  await cursor.click(page.getByRole("button", { name: /full document/i }));
  await required(
    page.getByText(/supplier output|what we send/i).first(),
    "supplier output preview",
  );
  clock.mark("preview-output");

  await cursor.click(page.getByRole("button", { name: /triage/i }).first());
  await cursor.click(
    page.getByRole("button", { name: /^send to supplier$/i }).first(),
  );
  await cursor.click(page.locator("#confirm-check"));
  await cursor.click(
    page.getByRole("button", { name: /send to supplier/i }).last(),
  );
  await required(page.getByText(/delivered/i).first(), "delivered state");
  clock.mark("deliver");

  await page.goto("/delivery-log", { waitUntil: "networkidle" });
  await required(
    page.getByRole("heading", { name: /delivery log/i }),
    "delivery log",
  );
  clock.mark("audit");
  clock.mark("close");

  clock.save(spec);
  await saveFilmVideo(page, spec.id);
});
```

The implementation must add waits based on `budgets[beatId]` after each action,
warm routes before the first marker, and use the existing fictional sample data.
Any renamed current selector must be discovered from the current UI and asserted,
not bypassed with hidden DOM or a fail-soft catch.

- [ ] **Step 4: Generate voiceover**

Run:

```powershell
$env:ELEVENLABS_STABILITY='0.62'
$env:ELEVENLABS_SPEED='1.06'
bun run film:vo -- walkthrough-2026-07
```

Expected: one MP3 per beat plus `manifest.json`; no `.srt`.

- [ ] **Step 5: Capture, assemble, and verify**

Run:

```powershell
bun run film:capture -- capture-walkthrough-2026-07
bun run film:assemble -- walkthrough-2026-07
bun run film:verify -- walkthrough-2026-07
```

Expected: QA passes; duration is 90-110 seconds; no subtitle stream or sidecar.

- [ ] **Step 6: Review the contact sheet and full video**

Inspect:

```text
scripts/demo-video/films/out/walkthrough-2026-07/qa/contact-sheet.jpg
scripts/demo-video/films/out/walkthrough-2026-07.mp4
```

Reject and recapture if any frame shows:

- stale navigation or old UI;
- loading or failed-fetch banners;
- contradictory buyer/supplier data;
- wandering cursor;
- clipped menus or tooltips;
- delivery described as supplier acceptance;
- illegible supplier output;
- private information.

- [ ] **Step 7: Copy the approved local draft**

Create the review directory if absent, then copy:

```powershell
New-Item -ItemType Directory -Force -Path '%USERPROFILE%\Videos\ProcuLink'
Copy-Item -LiteralPath 'scripts\demo-video\films\out\walkthrough-2026-07.mp4' -Destination '%USERPROFILE%\Videos\ProcuLink\walkthrough-2026-07-DRAFT.mp4'
```

- [ ] **Step 8: Commit**

```powershell
git add scripts/demo-video/films/walkthrough-2026-07.json scripts/demo-video/films/capture-walkthrough-2026-07.spec.ts
git commit -m "feat: produce current product walkthrough"
```

Do not commit generated audio, video, posters, frames, or QA output.

---

### Task 4: Produce the B2B marketing film

**Files:**
- Create: `project-proculink/scripts/demo-video/films/marketing-2026-07.json`
- Create: `project-proculink/scripts/demo-video/films/capture-marketing-2026-07.spec.ts`
- Create: `project-proculink/scripts/demo-video/films/prompts/marketing-bridge.md`

**Interfaces:**
- Consumes: strict film pipeline and current UI.
- Produces: a 35-45 second marketing draft with one approved abstract bridge
  clip and real UI proof.

- [ ] **Step 1: Add the marketing film specification**

The narration must use this concise spine:

```text
Buyers send purchase orders however they can. Suppliers expect them exactly
their way. ProcuLink sits between them: reading the order, keeping the source
visible, resolving risky fields, applying supplier rules, and generating the
right output for the right channel. Your team reviews exceptions, not every
line. Every change and every transmission is recorded. Each supplier flow
becomes reusable.
```

Create six beats:

```text
problem (abstract, 6s)
bridge (abstract, 6s)
control (real /inbox/ord-002 UI, 8s)
exact-output (real output preview UI, 8s)
proof (real delivery log UI, 7s)
close (brand card, 5s)
```

Set `captions: false`, `targetSeconds.min: 35`, and
`targetSeconds.max: 45`.

- [ ] **Step 2: Add the controlled Kling prompt**

Create `prompts/marketing-bridge.md`:

```text
Create a restrained premium B2B motion-design shot, 16:9, five seconds.
Background is deep navy with a precise faint technical grid. On the left,
several clean abstract document sheets and spreadsheet-like blocks enter in
buyer blue. They contain no letters, numbers, logos, or readable text. A single
asymmetric ProcuLink bridge curve guides them through the center. On the right,
they leave as aligned structured document blocks in supplier green. Motion is
calm, mechanical, exact, and physically coherent. Fixed camera, no humans, no
office stock footage, no glow bloom, no particles, no fake software interface,
no readable typography, no subtitles, no watermark.
```

- [ ] **Step 3: Generate one charged Kling candidate**

Before generation, confirm the active model list:

```powershell
kling account
kling who_am_i
```

Use `kling-video-v3_0` text-to-video if it remains available with 1080p, 16:9,
5-second support; otherwise use the currently available 1080p text-to-video
model with the closest declared parameters. Generate one candidate only:

```powershell
kling text_to_video --model kling-video-v3_0 --duration 5 --aspect_ratio 16:9 --resolution 1080p --enable_audio false --poll 180 "<prompt from marketing-bridge.md>"
```

Record the returned generation id and final URL in the task notes. Download to:

```text
scripts/demo-video/films/out/marketing-2026-07/abstract/bridge.mp4
```

Reject the clip if it contains any text, pseudo-UI, logos, people, random
particles, disconnected travelers, or color drift outside navy/blue/green.
Do not silently generate another paid candidate; document the rejection first.

- [ ] **Step 4: Capture the three real UI proof shots**

Create `capture-marketing-2026-07.spec.ts`. Warm and strictly capture:

1. `/inbox/ord-002` with the exception queue and one evidence-backed suggestion;
2. the same order’s supplier output preview;
3. `/delivery-log` with a successful transmission/audit event.

Write each clip as:

```text
out/marketing-2026-07/clips/control.webm
out/marketing-2026-07/clips/exact-output.webm
out/marketing-2026-07/clips/proof.webm
```

The capture must not mutate the order between clips; it should use stable mock
states selected before recording.

- [ ] **Step 5: Generate voice, assemble, and verify**

Run:

```powershell
bun run film:vo -- marketing-2026-07 --dry-run
bun run film:vo -- marketing-2026-07
bun run film:capture -- capture-marketing-2026-07
bun run film:assemble -- marketing-2026-07
bun run film:verify -- marketing-2026-07
```

Expected: 35-45 seconds, no captions/subtitle stream, abstract clip is limited
to problem/bridge, and every product claim is shown with current UI.

- [ ] **Step 6: Copy the local review draft**

```powershell
Copy-Item -LiteralPath 'scripts\demo-video\films\out\marketing-2026-07.mp4' -Destination '%USERPROFILE%\Videos\ProcuLink\marketing-2026-07-DRAFT.mp4'
```

- [ ] **Step 7: Commit**

```powershell
git add scripts/demo-video/films/marketing-2026-07.json scripts/demo-video/films/capture-marketing-2026-07.spec.ts scripts/demo-video/films/prompts/marketing-bridge.md
git commit -m "feat: produce ProcuLink marketing film"
```

---

### Task 5: Produce the short launch film

**Files:**
- Create: `project-proculink/scripts/demo-video/films/launch-2026-07.json`
- Create: `project-proculink/scripts/demo-video/films/capture-launch-2026-07.spec.ts`
- Create: `project-proculink/scripts/demo-video/films/prompts/launch-documents.md`

**Interfaces:**
- Consumes: strict film pipeline, the accepted marketing abstract clip when it
  crops cleanly, and current UI.
- Produces: an 18-25 second 16:9 launch draft.

- [ ] **Step 1: Add the launch specification**

Use these five beats and no subtitle track:

```json
[
  {
    "id": "launch",
    "kind": "brand",
    "source": "card",
    "vo": "ProcuLink is live.",
    "shot": "ProcuLink lockup and restrained bridge activation."
  },
  {
    "id": "problem",
    "kind": "abstract",
    "source": "generated",
    "vo": "Buyer orders arrive as spreadsheets, PDFs, XML, EDI, email, and API payloads.",
    "shot": "Abstract document formats converge; no generated text."
  },
  {
    "id": "product",
    "kind": "ui",
    "source": "capture",
    "vo": "ProcuLink reads them, resolves the risky parts, and builds the supplier-ready output.",
    "shot": "Fast current UI sequence: upload, exception review, output preview."
  },
  {
    "id": "promise",
    "kind": "ui",
    "source": "capture",
    "vo": "Send every purchase order to each supplier in the format and channel they require.",
    "shot": "Delivered transmission state and audit proof."
  },
  {
    "id": "cta",
    "kind": "brand",
    "source": "card",
    "vo": "ProcuLink. Connecting procurement.",
    "shot": "Brand close with proculink.eu.",
    "overOutro": true
  }
]
```

Set `targetSeconds.min: 18`, `targetSeconds.max: 25`, and `captions: false`.

- [ ] **Step 2: Add or reuse the abstract document clip**

Create `prompts/launch-documents.md`:

```text
Five-second 16:9 premium B2B motion design. Deep navy background, faint exact
technical grid. A small set of abstract paper, spreadsheet, XML-tree, and API
payload shapes enters from the left in buyer blue. The shapes have no readable
text, letters, numbers, logos, or interface controls. They align into one clean
structured document that exits right in supplier green. Fixed camera, calm
precise motion, no people, no particles, no lens flare, no gradients beyond the
single blue-to-green route, no watermark, no subtitles.
```

If the accepted marketing bridge clip cleanly covers this beat, reuse a
different 3-4 second section and do not spend another Kling generation. Generate
one new clip only if reuse is visibly repetitive or semantically wrong.

- [ ] **Step 3: Capture the real UI launch montage**

Create `capture-launch-2026-07.spec.ts` with strict 2-3 second clips:

```text
upload detected state
exception resolved state
supplier output preview
delivered transmission + audit state
```

Use hard cuts or 4-6 frame dissolves. Do not use rapid zooms, fake cursor
movement, or more than one on-screen action per clip.

- [ ] **Step 4: Generate voice, assemble, and verify**

Run:

```powershell
bun run film:vo -- launch-2026-07 --dry-run
bun run film:vo -- launch-2026-07
bun run film:capture -- capture-launch-2026-07
bun run film:assemble -- launch-2026-07
bun run film:verify -- launch-2026-07
```

Expected: 18-25 seconds, comprehensible from visual sequence and minimal title
cards, no captions/SRT, no stale UI.

- [ ] **Step 5: Copy the local review draft**

```powershell
Copy-Item -LiteralPath 'scripts\demo-video\films\out\launch-2026-07.mp4' -Destination '%USERPROFILE%\Videos\ProcuLink\launch-2026-07-DRAFT.mp4'
```

- [ ] **Step 6: Commit**

```powershell
git add scripts/demo-video/films/launch-2026-07.json scripts/demo-video/films/capture-launch-2026-07.spec.ts scripts/demo-video/films/prompts/launch-documents.md
git commit -m "feat: produce ProcuLink launch film"
```

---

### Task 6: Run cross-film editorial and technical QA

**Files:**
- Create: `project-proculink/scripts/demo-video/films/out/review-manifest.json`
- Modify: `project-proculink/scripts/demo-video/films/README.md`

**Interfaces:**
- Consumes: all three final local draft MP4s and posters.
- Produces: a review manifest and founder review package; generated output stays
  uncommitted.

- [ ] **Step 1: Verify all three outputs in one command**

Run:

```powershell
bun run film:verify -- walkthrough-2026-07 marketing-2026-07 launch-2026-07
```

Expected: all three pass technical, duration, no-caption, and decode checks.

- [ ] **Step 2: Check narration and music**

Listen on headphones and laptop speakers. Accept only when:

- every spoken word is intelligible;
- no sentence is clipped at a cut;
- no unexplained pause exceeds 700 ms;
- music remains subordinate to voice;
- there is no harsh jump in loudness between films;
- pronunciation of “ProcuLink”, “cXML”, “EDI”, “SFTP”, and supplier names is
  correct.

If pronunciation fails, fix only the affected beat’s text/voice clip and
reassemble; do not regenerate every beat.

- [ ] **Step 3: Perform claim-to-screen review**

For every walkthrough and marketing narration sentence, record:

```json
{
  "claim": "Preview exactly what the supplier will receive.",
  "filmId": "walkthrough-2026-07",
  "beatId": "preview-output",
  "evidence": "/inbox/ord-002 supplier output preview",
  "result": "pass"
}
```

Reject claims that only exist in narration or generated visuals. The delivery
claim must explicitly remain a transmission result, not supplier acceptance.

- [ ] **Step 4: Perform sensitive-data review**

Inspect every beat frame and video at full resolution. Confirm none show:

- live API keys or credential values;
- real email addresses;
- real customer or supplier order data;
- localhost URLs;
- Clerk, Railway, Vercel, Cloudflare, Stripe, or other admin consoles;
- personal browser chrome, bookmarks, notifications, or profile data.

- [ ] **Step 5: Create the review manifest**

Write `out/review-manifest.json`:

```json
{
  "createdAt": "2026-07-28",
  "status": "awaiting-founder-review",
  "films": [
    {
      "id": "walkthrough-2026-07",
      "localPath": "%USERPROFILE%\\Videos\\ProcuLink\\walkthrough-2026-07-DRAFT.mp4",
      "stagedR2Key": "marketing/walkthrough-2026-07.mp4",
      "liveReplacementKey": "marketing/walkthrough.mp4"
    },
    {
      "id": "marketing-2026-07",
      "localPath": "%USERPROFILE%\\Videos\\ProcuLink\\marketing-2026-07-DRAFT.mp4",
      "stagedR2Key": "marketing/proculink-marketing-2026-07.mp4"
    },
    {
      "id": "launch-2026-07",
      "localPath": "%USERPROFILE%\\Videos\\ProcuLink\\launch-2026-07-DRAFT.mp4",
      "stagedR2Key": "marketing/proculink-launch-2026-07.mp4"
    }
  ]
}
```

- [ ] **Step 6: Stop for founder review**

Present the three local files and contact sheets. Do not upload or overwrite any
R2 object in this task.

---

### Task 7: Stage approved dated assets in public R2

**Files:**
- No source files changed.
- External state: public R2 bucket `proculink-public`, dated keys only.

**Interfaces:**
- Consumes: explicit founder approval for the three local drafts.
- Produces: dated public R2 objects that do not replace the live walkthrough.

- [ ] **Step 1: Confirm identity and bucket**

Run:

```powershell
wrangler whoami
wrangler r2 bucket list
```

Expected: authenticated Cloudflare account and `proculink-public` present.

- [ ] **Step 2: Upload dated MP4 and poster objects**

Run:

```powershell
wrangler r2 object put proculink-public/marketing/walkthrough-2026-07.mp4 --file '%USERPROFILE%\Videos\ProcuLink\walkthrough-2026-07-DRAFT.mp4' --content-type video/mp4 --remote
wrangler r2 object put proculink-public/marketing/walkthrough-2026-07-poster.jpg --file 'scripts\demo-video\films\out\walkthrough-2026-07-poster.jpg' --content-type image/jpeg --remote
wrangler r2 object put proculink-public/marketing/proculink-marketing-2026-07.mp4 --file '%USERPROFILE%\Videos\ProcuLink\marketing-2026-07-DRAFT.mp4' --content-type video/mp4 --remote
wrangler r2 object put proculink-public/marketing/proculink-marketing-2026-07-poster.jpg --file 'scripts\demo-video\films\out\marketing-2026-07-poster.jpg' --content-type image/jpeg --remote
wrangler r2 object put proculink-public/marketing/proculink-launch-2026-07.mp4 --file '%USERPROFILE%\Videos\ProcuLink\launch-2026-07-DRAFT.mp4' --content-type video/mp4 --remote
wrangler r2 object put proculink-public/marketing/proculink-launch-2026-07-poster.jpg --file 'scripts\demo-video\films\out\launch-2026-07-poster.jpg' --content-type image/jpeg --remote
```

- [ ] **Step 3: Verify public delivery**

Run:

```powershell
curl.exe -I https://assets.proculink.eu/marketing/walkthrough-2026-07.mp4
curl.exe -I https://assets.proculink.eu/marketing/proculink-marketing-2026-07.mp4
curl.exe -I https://assets.proculink.eu/marketing/proculink-launch-2026-07.mp4
```

Expected for each: HTTP 200, `Content-Type: video/mp4`, non-zero
`Content-Length`, byte-range support, and cache headers suitable for immutable
dated media.

- [ ] **Step 4: Browser-check staged walkthrough playback**

Open the dated walkthrough URL in Chromium and confirm:

- first frame appears promptly;
- play starts;
- seeking works;
- audio plays;
- duration matches local QA;
- no CORS or media decode error occurs.

- [ ] **Step 5: Stop before live replacement**

Report dated URLs and ask for the final explicit approval to replace
`marketing/walkthrough.mp4`.

---

### Task 8: Replace the live walkthrough with rollback protection

**Files:**
- Modify only if required: `project-proculink/.env` or Vercel environment values.
- External state: `proculink-public/marketing/walkthrough.mp4` and poster.

**Interfaces:**
- Consumes: explicit approval after the staged URL has been watched.
- Produces: updated live `/watch` media with a recoverable dated prior version.

- [ ] **Step 1: Preserve the current live object**

Download the currently live MP4 and poster before replacement:

```powershell
curl.exe -L https://assets.proculink.eu/marketing/walkthrough.mp4 -o C:\tmp\proculink-walkthrough-before-2026-07.mp4
curl.exe -L https://assets.proculink.eu/marketing/walkthrough-poster.jpg -o C:\tmp\proculink-walkthrough-poster-before-2026-07.jpg
```

Verify both are non-empty with `ffprobe`/image inspection.

- [ ] **Step 2: Upload rollback copies**

```powershell
wrangler r2 object put proculink-public/marketing/archive/walkthrough-before-2026-07.mp4 --file C:\tmp\proculink-walkthrough-before-2026-07.mp4 --content-type video/mp4 --remote
wrangler r2 object put proculink-public/marketing/archive/walkthrough-poster-before-2026-07.jpg --file C:\tmp\proculink-walkthrough-poster-before-2026-07.jpg --content-type image/jpeg --remote
```

- [ ] **Step 3: Replace the live MP4 and poster**

```powershell
wrangler r2 object put proculink-public/marketing/walkthrough.mp4 --file '%USERPROFILE%\Videos\ProcuLink\walkthrough-2026-07-DRAFT.mp4' --content-type video/mp4 --remote
wrangler r2 object put proculink-public/marketing/walkthrough-poster.jpg --file 'scripts\demo-video\films\out\walkthrough-2026-07-poster.jpg' --content-type image/jpeg --remote
```

- [ ] **Step 4: Purge only the replaced media URLs if cache remains stale**

Purge:

```text
https://assets.proculink.eu/marketing/walkthrough.mp4
https://assets.proculink.eu/marketing/walkthrough-poster.jpg
```

Do not purge the entire zone.

- [ ] **Step 5: Verify the live site**

Check:

```text
https://proculink.eu/watch
https://proculink.eu/how-it-works
```

Confirm the new poster, duration, playback, seeking, audio, and CTA. Confirm the
old video is still available at the archive key.

- [ ] **Step 6: Run the focused frontend test**

Run:

```powershell
bun run test -- src/lib/walkthrough.test.ts
bun run test:e2e -- tests/e2e/sample-order-happy-path.spec.ts
```

Expected: walkthrough availability logic and watch flow pass.

- [ ] **Step 7: Update production notes and commit**

Update `scripts/demo-video/PRODUCTION.md` with:

- new production date;
- new duration and file size;
- dated R2 source key;
- archive rollback key;
- confirmation that no subtitle/SRT artifact was published.

Commit:

```powershell
git add scripts/demo-video/PRODUCTION.md
git commit -m "docs: record July walkthrough release"
```

---

## Final Verification

Before declaring the project complete, run:

```powershell
bun run test -- scripts/demo-video/films/film-spec.test.ts src/lib/walkthrough.test.ts
bun run film:verify -- walkthrough-2026-07 marketing-2026-07 launch-2026-07
bun run build
```

Then verify the public URLs with `curl.exe -I` and browser playback. Completion
requires:

- three local review MP4s;
- three passing QA reports and contact sheets;
- zero subtitle streams and zero `.srt`/`.vtt`/`.ass` artifacts;
- dated R2 assets only after draft approval;
- live walkthrough replacement only after separate explicit approval;
- rollback copy of the previous walkthrough;
- current `/watch` and `/how-it-works` playback verified;
- no uncommitted source changes from the production work.
