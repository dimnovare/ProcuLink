# Handoff → the frontend design-primitive migration chip (2026-06-07)

You are migrating the `(app)` pages in **`project-proculink`** to the shared design
primitives (`PageShell`/`PageHeader`/`Card`/`MobileListRow` + `UnifiedStatusBadge`,
per `docs/design-system/11-unified-page-rules.md`). This note tells you what changed
on `main` underneath you and how to land your work cleanly.

## What another session shipped to `main` while you were working

Frontend `main` advanced to **`1b9f896`** (then docs-only commits in the backend repo).
That commit touched **4 files — all OUTSIDE your `(app)` migration scope**, from a
live launch audit:

- `src/components/marketing/ROICalculator.tsx` — honest ROI footnote (dropped a false
  "based on pilot customer measurements" claim).
- `src/app/(marketing)/pricing/page.tsx` — pricing secondary tiers now SSR-crawlable
  via `[hidden]` instead of conditional-mount.
- `next.config.ts` — added `async headers()` (X-Content-Type-Options, Referrer-Policy,
  X-Frame-Options, Permissions-Policy).
- `src/app/layout.tsx` — added `metadataBase: new URL("https://proculink.eu")`.

**Action:** `git pull --rebase origin main` before you commit. The only possible overlap
with your work is `src/app/layout.tsx` (1 line added at the top of the `metadata` object)
and `next.config.ts` — if you also edited those, resolve trivially (keep both changes).
Your `(app)` page edits do not overlap the other three files.

## Your WIP at handoff time

At 2026-06-07 the working tree had ~12 uncommitted `(app)` page files mid-migration:
`admin`, `drafts`, `inbound/asns`, `inbound/invoices`, `library/{buyers,standards,templates}`,
`operations/{connectors,exceptions,health,webhooks}`, `settings`. Earlier `operations/health/page.tsx`
had **57 curly/smart-quote chars** (`variant=”wide”`) that failed `bun run build`; by the
re-check those were **fixed (0 curly quotes in all 12)** — good. A snapshot of your WIP was
build-verified in an **isolated worktree** (no contact with your tree); result recorded at the
bottom of this file.

## How to land it safely (hard rules)

1. **Never `git add -A` in this repo.** A concurrent session model means the working tree may
   hold unrelated changes. Stage your migration files by **explicit path** only.
2. **Build-gate before committing:** `bun run build` must be clean. If a full build is blocked
   by unrelated working-tree changes, isolate yours (stash others, or a throwaway worktree).
3. **No smart quotes / non-ASCII in code** — JSX attributes need straight `"`. (A find/replace of
   `“”` → `"` and `‘’` → `'` fixes the class of bug seen here.)
4. **Verify each migrated page renders** (the live site is authed — drive it logged-in, or at
   least `bun run build` + spot-check the route). Don't ship a page that throws.
5. Keep `git add` scoped, commit, `git pull --rebase`, push.

## Remaining launch-audit items also routed to you (lower priority than the migration)

From `docs/audit/2026-06-07-launch-site-audit.md` (backend repo) — confirmed, not yet fixed:

- **P2 SEO metadata** — `/`, `/pricing`, `/how-it-works` share one generic `<title>` +
  description (duplicate across pages). Add per-route `generateMetadata` (distinct title +
  120-160 char description + self `alternates.canonical`; `metadataBase` is already set).
  **`/pricing` is `"use client"`** → add a `layout.tsx` in that route folder to export metadata.
  `/formats` has a unique title/description but its `openGraph`/`twitter` fall back to root —
  set page-specific og/twitter. Sweep `app/(marketing)/*/page.tsx`.
- **P2 app CSP** — add a Content-Security-Policy in `next.config.ts headers()` compatible with
  Clerk (`clerk.proculink.eu`) + Next's inline runtime; at minimum `frame-ancestors 'none'`.
  Test auth still works (this is why it was deferred from the security-header batch).
- **P3** — `og:url` per page; compress `public/og-image.png` (~1.9 MB → <1 MB); align the
  homepage hero capability counts (4+/5+/4) with the `/formats` catalog (9/6/6); `Disallow: /admin`
  in robots.txt (cosmetic).

## Backend repo (ProcuLink) — separate Wave-8/D chip already spawned

`docs/strategy/NEXT_CHIP_HANDOFF.md` covers the backend Wave 8 (clean-env regression gate,
consolidated runbook) + Wave D refactors + the **API security headers** (HSTS + nosniff in
`ProcuLink.Api/Program.cs`). DMARC is already added (`p=none`, verified). Don't duplicate those.

## Isolated build verdict of your WIP snapshot

_(appended below once the isolated `bun run build` completes — see task `bh9jjjm3o`)_
