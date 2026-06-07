# Public-site + public-API launch audit — 2026-06-07

Read-only, adversarially-verified audit of proculink.eu + api.proculink.eu (22 agents,
2-vote verification). **15 confirmed findings: 0 P0, 4 P1, 5 P2, 6 P3.** Three dimensions
fully clean: **page availability** (all 14 pages + 24 sitemap URLs 200), **broken links**
(none), **API error/CORS hygiene** (health 200, 401 on protected, 404 ProblemDetails, exact-origin
CORS, no wildcard, no stack-trace leaks).

## Fixed + verified live this session (frontend `1b9f896`)

| # | Sev | Finding | Fix |
|---|---|---|---|
| 1 | P1 | Homepage ROI footnote claimed "70% … based on pilot customer measurements" (pre-launch, no customers) — offer⇔works violation | Reworded to "an illustrative default based on our analysis of typical manual reformatting effort, not a measured customer outcome." `ROICalculator.tsx`. **Verified live.** |
| 2 | P1 | Pricing "See all tiers" disclosure: panel `#plk-all-tiers` + Growth/Integration/Distributor absent from SSR (not crawlable; dangling `aria-controls` when collapsed). *NB: the control DOES work for JS users — verified clicking reveals all 6 tiers; the defect was SSR/a11y only, not a dead control.* | Secondary tiers now stay in the DOM collapsed via `[hidden]` (+ `.plk-pricing-grid[hidden]{display:none}`) instead of conditional-mount. SSR now carries all 6 tiers; default view still shows 3; disclosure still toggles. **Verified live** (collapsed=3 cards/display:none, expanded=6 cards/display:grid). |
| 3 | P1/P2/P3 | App origin missing X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy (HSTS already present) | Added all four via `next.config.ts` `headers()`. **Verified live** on /, /pricing, /formats. Full CSP intentionally deferred (needs Clerk-compatible testing). |
| — | — | metadataBase prerequisite for canonical/OG | Added `metadataBase: new URL("https://proculink.eu")` to root layout. |

## Routed to the next chip (NEXT_CHIP_HANDOFF.md / Wave 8+D)

- **P2 SEO — per-page title + meta description + canonical.** `/`, `/pricing`, `/how-it-works` inherit the root layout's generic title/description (duplicate across 3 pages). `/pricing` is a client component → needs a `layout.tsx` wrapper to export metadata. Add self-referencing `alternates.canonical` per page (metadataBase already set). `/formats` already has unique title/description but its og/twitter fall back to root defaults — set page-specific openGraph/twitter. Sweep `app/(marketing)/*/page.tsx`.
- **P2 API security headers.** api.proculink.eu has no HSTS or X-Content-Type-Options. Add `app.UseHsts()` (prod) + a nosniff response-header middleware in `ProcuLink.Api/Program.cs`. Test-gate with the full backend suite. (Defense-in-depth; API serves JSON via fetch so real risk is low.)
- **P2 App CSP.** Add a Content-Security-Policy compatible with Clerk (clerk.proculink.eu) + Next inline runtime; at minimum `frame-ancestors 'none'`. Needs careful testing to avoid breaking auth.
- **P3** og:url per page; compress `og-image.png` (currently ~1.9 MB → <1 MB so FB/LinkedIn/Slack fetch it reliably); clamp the pricing volume-recommender so every recommendation maps to a visible card (or it's moot once SEO renders all tiers); align homepage hero capability counts (4+/5+/4) with the /formats catalog (9/6/6) — currently *under*-stated, not an overclaim; add `Disallow: /admin` to robots.txt (cosmetic; already auth-gated + not in sitemap).
- **Observability hygiene** (from the dashboard sweep, not the site audit): bulk-resolve the 22 stale Sentry issues so a post-launch error stands out; downgrade the Postmark inbound-webhook log level from Error→Warning for unauthorized bot-probe calls.

## ⚠️ Broken uncommitted WIP in the frontend working tree (FLAG)

At audit time the `project-proculink` working tree had **~12 uncommitted `(app)` page files** mid-migration to the design primitives (PageShell/PageHeader/Card) — `admin`, `drafts`, `inbound/asns`, `inbound/invoices`, `library/{buyers,standards,templates}`, `operations/{connectors,exceptions,health,webhooks}`, `settings`. At least `operations/health/page.tsx` is **broken** (57 curly/smart-quote chars, e.g. `variant=”wide”`) and **fails `bun run build`**. It is **uncommitted, so prod (`0ceb156`→`1b9f896`) is unaffected**, but it must NOT be committed as-is. This is the design-primitive page migration (Wave D's "0/22 pages use primitives" item) started by a concurrent chip/agent. Complete it properly (straight quotes, build-gated) before committing — do NOT `git add -A`.
