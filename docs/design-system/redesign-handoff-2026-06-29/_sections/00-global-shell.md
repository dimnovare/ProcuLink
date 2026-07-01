## 00. Global App Shell — `(app) layout / shell`

- **File:** `C:/Users/Dmitri.REDACTED-PARTY/source/repos/project-proculink/src/app/(app)/layout.tsx`
- **Key components:**
  - `src/components/bridge/BridgeSidebar.tsx` — left navy nav (desktop rail + mobile drawer body)
  - `src/components/bridge/BridgeTopbar.tsx` — top bar (mobile menu button, demo badge, breadcrumb, setup chip, search, notifications, help, account) + hosts `NotificationsBell`, `AccountMenu`, `useAutoCrumb`
  - `src/components/bridge/CommandPalette.tsx` — Cmd/Ctrl+K palette
  - `src/components/bridge/HelpSlideover.tsx` — right help drawer
  - `src/components/bridge/SetupProgressChip.tsx` — "Setup n/6" topbar pill
  - `src/components/bridge/BridgeLoader.tsx` — `BridgeLoader` + `BridgePageLoader` (the single loading mark; used by `(app)/bridge/loading.tsx`)
  - `src/components/bridge/ErrorBoundary.tsx` — client render-error catch + `DefaultErrorPanel`
  - `src/components/bridge/breadcrumb.ts` — pure crumb label/href helpers
  - `src/components/bridge/OnboardingWizard.tsx` — first-run modal (rendered by `BridgeDashboard.tsx`, NOT the layout)
  - `src/components/bridge/DSPrimitives.tsx` — `ProcuLinkMark` logo glyph
  - `src/components/ui/toaster.tsx` (`<Toaster />`) + `src/components/ui/sonner.tsx` (`<Sonner />`) — two global toast layers
  - `src/mocks/MSWProvider.tsx` — mock service worker wrapper (mock mode only)
- **Capture URL (mock):** `/bridge` (the dashboard renders inside this shell; the shell itself is the same on every `(app)/*` route, e.g. `/inbox`, `/library/suppliers`)

### What it is & why it exists
The `(app)` layout is the persistent chrome wrapped around every authenticated screen in the parse→normalize→validate→review→transform→deliver→learn workflow. It is the operator's home frame: the left sidebar is the map of the whole product (Workbench / Library / Operations / Inbound / Admin), the top bar tells them where they are (breadcrumb), what needs action (notifications, setup progress), and gives them the two universal accelerators — global search (Cmd+K) and contextual help. A procurement coordinator never "opens" the shell deliberately; it is simply always there, and its job is to make every page feel like one coherent application rather than a pile of routes.

### Who uses it & the primary job
**Primary persona: procurement coordinator** (also integration expert, operator, admin via the gated Admin link). The single most important job the shell does is **orientation + fast traversal**: from anywhere, jump to the order/supplier/buyer you need (Cmd+K or sidebar), see at a glance whether anything is failing or needs review (Notifications bell badge, Inbox count badge), and never get lost (breadcrumb + active nav state). Secondary: surface "you still have setup to do" (Setup chip) without nagging once complete.

### Layout & structure (current)
Full-viewport flex shell, no scroll on the outer wrapper (`flex h-dvh overflow-hidden`, page background `#F6F7FA`). Two columns:

**Left column — sidebar.**
- Desktop (`md+`): `BridgeSidebar collapsible collapseBelowLg`, fixed navy `#0B1A2F`, width **232px expanded / 64px collapsed**, right border `1px #1F3252`. Width animates `transition-[width] 200ms`. In the tablet band (≤1023px / `md`→`lg`) it is forced to the 64px icon rail regardless of preference; at `lg+` it honors the persisted `localStorage["pl-side"]` preference. A screen-requested auto-collapse channel (`SIDEBAR_AUTO_COLLAPSE_EVENT`, e.g. the Review screen's full-document mode) can also force the rail.
- Sidebar internal stack, top→bottom:
  1. **Logo header** (height 56, border-bottom `#1F3252`): `ProcuLinkMark` 24px + "ProcuLink" wordmark (Bricolage Grotesque 16/700) + a collapse toggle chip (`ChevronsLeft`/`ChevronsRight`, 28×28 visible, 44×44 hit area). In the mobile drawer this slot shows an `X` close button instead.
  2. **Workspace badge** (`#14253D` card, border `#1F3252`): 26px square org-initials chip (`#1E66C9`) + org name (12.5/600 white) + plan label (10.5px `#7C8DA6`, e.g. "Operations plan"). **Read-only** — deliberately NO chevron / switcher (comment notes there is no multi-org switcher wired; not implying a menu that doesn't exist).
  3. **Nav** (scrollable, hidden scrollbar): grouped link list — see Data shown.
  4. **Footer** (border-top `#1F3252`): "Talk to a human" (`mailto:sales@proculink.eu`, MessageCircle) + "Back to site" (link to `/`, ExternalLink).
- Mobile (`<md`): the desktop sidebar is hidden; a full-screen drawer (`BridgeSidebar fullWidth showClose`) mounts only when `sidebarOpen`, as a `role="dialog" aria-modal` navy panel over `inset-0 z-50`.

**Right column — topbar + main.**
- `BridgeTopbar`: height **56px**, navy `#0B1A2F`, `position: relative`. Single flex row, `gap-3 sm:gap-4`, `px-3 sm:px-5`, left→right: mobile menu button (`md:hidden`), Demo-data badge (mock only), breadcrumb (`hidden sm:flex`) OR mobile page-title (`sm:hidden`), Setup chip, then `ml-auto` pushes the right cluster: search field (`sm+`, 320px / max 38vw) or search icon (mobile), Notifications bell, Help "?" (`sm+`), Account avatar. A 2px blue→green gradient `link-spine` sits on the bottom edge (animated, keyed by pathname so it re-runs on navigation; frozen under reduced-motion).
- `main`: `flex-1 overflow-auto`, wraps `children` in `<ErrorBoundary context="App">`. This is the only scrolling region.

**Spacing/type/density observations:** Heavy reliance on **inline `style={{}}` with literal hex + literal px** (e.g. `fontSize: 12.5`, `13.5`, `10.5`, `11.5`) rather than tokens/Tailwind scale — the shell predates and partly bypasses the design-token system in globals.css. Type sizes are fractional and non-systematic (10.5/11.5/12.5/13/13.5/14/16/18). Tap targets are correct (44×44 via `var(--tap-min)`) but built as "visible 28/32px chip inside a transparent 44px hit area" repeatedly by hand.

### Data shown
**Sidebar nav (`NAV` → `buildVisibleNav`)** — grouped, direction-aware, flag-gated:
| Group | Items (label → href, icon) |
|---|---|
| (ungrouped) | Dashboard → `/bridge` (Layers) |
| Workbench | Upload → `/upload` (Upload); Inbox → `/inbox` (Inbox, **review badge**); Drafts → `/drafts` (Files) |
| Library | Suppliers → `/library/suppliers` (Truck, relabels to **Customers** in inbound mode); Buyers → `/library/buyers` (Building2); Mappings → `/library/mappings` (GitBranch); Rules → `/library/rules` (ShieldCheck); Rule definitions → `/library/rule-definitions` (ListChecks); Output templates → `/library/templates` (FileCode); Standards → `/library/standards` (BookOpen) |
| Operations | Exceptions → `/operations/exceptions` (AlertTriangle); System health → `/operations/health` (Activity); Delivery log → `/operations/log` (ScrollText); Connectors → `/operations/connectors` (Plug); Webhooks → `/operations/webhooks` (Webhook) |
| Inbound (flag `NEXT_PUBLIC_INBOUND_ENABLED`) | Invoices → `/inbound/invoices` (FileText); ASNs → `/inbound/asns` (Package) |
| (ungrouped) | Admin → `/admin` (ShieldHalf, allowlist-gated); Help → `/help` (HelpCircle, **opens new tab**); Settings → `/settings` (Settings) |

Launch default (`LAUNCH_CORE_ONLY`, i.e. `NEXT_PUBLIC_LAUNCH_FULL_NAV !== "true"`) filters the nav down to `LAUNCH_CORE_HREFS` only: `/bridge, /upload, /inbox, /library/suppliers, /operations/exceptions, /operations/health, /admin, /settings, /help`, dropping now-empty group headers. The full nav above is only shown when that flag is set.

**Live data feeding the shell (all via TanStack Query):**
- Inbox review badge + Setup-chip + Notifications unread: `apiClient.getOrdersSummary()` (mock: `mockGetOrdersSummary`) — `byStatus["pending_review"]` drives the blue Inbox count; `pending_review + failed + delivery_failed + transform_failed + delivery_dead_letter + rejected_by_supplier` drives the bell's amber unread count.
- Workspace badge plan: `getBillingStatus()` → `billing.plan` ("Operations" etc.); org name + initials from Clerk `useOrganization()`.
- Admin-link visibility: `checkAdminAccess()` (query `["admin-access"]`) — UX hint only; page + API re-gate server-side.
- Notifications popover list: `apiClient.getOrders({ pageSize: 100 })` (mock: `mockGetOrders`) → mapped to `failed` / `review` / `delivered` notification kinds, each row showing `o.poNumber` (mono) · `o.supplierName` + relative `timeAgo(createdAt)`.
- Command palette index: `getOrders({pageSize:100})` / `getOrders({search,pageSize:8})`, `getSuppliers()`, `getBuyers()` + a static Actions list + mapper power-commands.
- Setup chip: `useOnboardingStatus()` → `buildChecklistSteps()` → `{totalDone}/{totalSteps}`.
- Breadcrumb dynamic labels read the **existing query cache** (no new fetch): `["order", id].poNumber`, `["suppliers"]` list `.name`, `["connection", id].name`.

`AutoActivateOrg` (invisible) auto-activates the user's first Clerk org so the JWT carries `org_id` (else every API call fails "Organisation not resolved"), then invalidates all queries.

### Interactive elements
| Control | Action | Result/where it goes |
|---|---|---|
| Sidebar nav link (each) | click | `next/link` navigate to its href; mobile drawer also calls `onNavigate`→close |
| Help nav link | click | opens `/help` in a **new tab** (`target=_blank`) |
| Admin nav link | click (only if allowlisted) | `/admin` |
| Sidebar collapse toggle (desktop) | click | toggles 232↔64px; persists `localStorage["pl-side"]`; first clears any auto-collapse |
| Sidebar close `X` (mobile drawer) | click | `onClose` → `closeSidebar()` |
| Workspace badge | none (read-only div) | static org identity; no menu |
| "Talk to a human" | click | `mailto:sales@proculink.eu?subject=ProcuLink%20support` |
| "Back to site" | click | navigate `/` (marketing) |
| Topbar mobile menu button (`md:hidden`) | click | `openSidebar()` → mounts mobile nav drawer |
| Breadcrumb crumb link | click | navigate to that crumb's cumulative path |
| Setup chip ("Setup n/6") | click | navigate `/bridge` (the checklist) |
| Search field / mobile search icon | click | open **Command Palette** (`setPaletteOpen(true)`) |
| ⌘K / Ctrl+K (global) | keydown | toggle Command Palette |
| Notifications bell | click | toggle Notifications popover |
| Notification row | click | close popover + `router.push('/inbox/{id}')` |
| "View all in inbox →" (popover footer) | click | close popover + `/inbox` |
| Help "?" button (`sm+`) | click | mark current route guide "seen" (localStorage), open **Help slideover** |
| Account avatar | click | Clerk `<UserButton>` menu (sign-out, manage account); placeholder "MK" chip if Clerk unconfigured |
| Command palette input | type | filters index (client substring) + debounced server order search (`>=2` chars) |
| Command palette ↑/↓, Enter, Esc | keydown | navigate list / run item / close |
| Command palette result row | click / hover | run (navigate or dispatch action) / set active |
| Help slideover search | type (150ms debounce) | filters help articles (Fuse) |
| Help slideover article / footer links | click | open `/help/*`, `/support` etc. in **new tab** |

### What opens / what closes
| Surface | Type | What opens it | What it contains | What closes it |
|---|---|---|---|---|
| **Mobile nav drawer** | full-screen drawer (`role=dialog aria-modal`, navy `#0B1A2F`, `inset-0 z-50`, `md:hidden`) | Topbar mobile menu button (`openSidebar`) | full `BridgeSidebar` (logo+X, workspace badge, nav, footer) | in-panel `X`; **Esc**; tapping any nav link (`onNavigate`); resizing to `md+` (conditionally unmounts). Has body-scroll lock + focus trap + focus restore to trigger. NO visible backdrop scrim (the panel is opaque full-screen) |
| **Command Palette** | centered modal + backdrop (`top:20vh`, 600px, `z-9999`; backdrop `rgba(11,26,47,.55)` blur, `z-9998`) | search field, mobile search icon, or **⌘K/Ctrl+K** | search input, grouped results (Orders/Suppliers/Buyers/Actions/Mapper cmds), keyboard-hint footer | backdrop click; **Esc** (two listeners); selecting any item (`run`→`onClose`). Body-scroll locked while open. Mounted only when open |
| **Help slideover** | right drawer (`role=dialog aria-modal`, `width min(380px,100%)`, `z-70`, scrim `rgba(11,26,47,.32)`) | Help "?" button | "This screen" guide / popular fallback, Related reading, optional Watch-walkthrough, search results, footer (help center / support / report bug) | header `×`; **Esc**; scrim click; following any link. On close focus returns to "?" trigger |
| **Notifications popover** | popover/dropdown (white card, `sm:absolute right-0 top-8 w-80`; mobile `fixed inset-x-2 top-14`; `z-50`) | Notifications bell | header ("Notifications" + "n need action"), up to 7 ranked rows (failed→review→delivered), "View all in inbox →" footer; empty: "No new activity." | outside mousedown; **Esc**; selecting a row; toggling the bell. NO scrim |
| **Account / user menu** | dropdown (Clerk `<UserButton>` internal popover) | avatar click | Clerk-managed account actions (manage account, sign out, org) | Clerk-managed (outside click / Esc). Not rendered when `NEXT_PUBLIC_CLERK_PUBLISHABLE_KEY` unset (static "MK" chip only) |
| **Onboarding wizard** | centered modal (`role=dialog aria-modal`, `z-50`, max 480px, backdrop `rgba(11,26,47,.45)` blur) | *NOT the layout* — `BridgeDashboard` renders it when `!wizardDismissed && onboardingStatus && !hasSupplier` | Step 0 (order direction radios) → Step 1 (add first supplier form) → done card; step indicator dots; "Dismiss" notice | corner `X` / backdrop click (`handleDismiss`); "Open my setup guide" (`onDismiss`); `?onboard=skip`; dismissal persisted `localStorage["plk-onboarding-wizard-dismissed"]` |
| **Toasts (shadcn `<Toaster/>`)** | toast region | imperative `toast()` from any page | per-page confirmations/errors | auto-dismiss / swipe / close |
| **Toasts (`<Sonner/>`)** | toast region (second system) | `sonner` `toast()` from any page | per-page confirmations/errors | auto-dismiss / close |
| **Tooltips** | tooltip (Radix `TooltipProvider`) | hover/focus on tooltip-wrapped controls | short labels | blur / pointer-leave |

### States
- **Empty:** Shell itself is always present. Notifications popover has a real empty state ("No new activity."). Command palette has `No results for "<q>"`. Sidebar nav: if `getOrdersSummary` returns no `pending_review`, the Inbox badge simply doesn't render (correct). Setup chip renders **nothing** when complete or status unknown (honest — no fabricated 0/6). Workspace badge plan shows "Loading…" until billing resolves.
- **Loading:** Route transitions use `(app)/bridge/loading.tsx` → `BridgePageLoader` (88px animated blue→green wire mark with two pulsing nodes, NOT a bare spinner). Per-route `loading.tsx` files use the same loader. Sidebar/topbar data (badges, plan) loads silently in the background (no skeleton on the chrome itself — badges/counts pop in).
- **Error:** `main` is wrapped in `<ErrorBoundary context="App">` → `DefaultErrorPanel` ("App failed to load", logged to Sentry, collapsible error details, "Try again" / "Reload page"). API-fetch failures for chrome data fail silently (badges just stay absent — `retry:1`, networkMode "always" so they don't latch "paused offline"). Onboarding wizard steps surface inline humanised errors (incl. a CORS/API-unreachable hint).
- **Success/feedback:** Two global toast layers (`<Toaster/>` + `<Sonner/>`). The `link-spine` bottom-edge gradient re-animates on each navigation as a subtle "you moved" cue. Help/wizard fire analytics `capture()` events.

### Responsive behaviour
- **HD 1920 / Desktop 1440:** sidebar expanded at 232px (honors persisted collapse), full breadcrumb, 320px search field, full topbar cluster, Demo badge full pill.
- **Tablet 768 (`md`→`lg`, ≤1023px):** sidebar **forced to the 64px icon rail** (`collapseBelowLg`/`belowLg`) regardless of preference — labels hidden, badges become a corner dot; nav group headers collapse to a divider line. Breadcrumb + search field still shown (`sm+`).
- **Mobile 390 (`<md`):** desktop sidebar hidden entirely → hamburger menu button opens the full-screen navy drawer. Breadcrumb replaced by a single bold page-title (`mobileLabel`). Search field collapses to an icon-only trigger. Help "?" hidden (`sm:flex`); Setup chip hides its "Setup" word (count only); Demo badge shrinks to a dot + "Demo". Notifications popover goes full-width (`inset-x-2 top-14`).
- **Known cliffs:** the `md`→`lg` band forces the icon rail, so labels disappear at exactly 1024px down — a hard cliff with no tween. The Help "?" is unreachable below `sm` (640px), so contextual guidance is desktop/tablet-only on phones. The mobile drawer has **no backdrop scrim** (opaque full-screen panel) so there is no dimmed-context affordance.

### Current UX issues
- **Token bypass / spacing drift (Bar #1, #2).** The entire shell is built with inline `style={{}}` literals (hex `#0B1A2F`, `#14253D`, `#7C8DA6`, `#C8D1E0`; px `12.5/13.5/10.5/11.5`) instead of the CSS variables that exist in globals.css (`--navy`, `--ink-faint`, `--tap-min`). Type sizes are fractional and unsystematic; padding/gaps don't follow a strict 4/8 rhythm (e.g. logo header `"16px 12px 12px 16px"`, badge margin `"12px 14px 6px"`). This makes the chrome hard to keep consistent with the token-driven page bodies.
- **Two toast systems (Bar #4/#6).** Both shadcn `<Toaster/>` and `<Sonner/>` are mounted globally — duplicate, divergent toast styling/behavior depending on which a page calls. Should be one.
- **Inconsistent badge shapes (Bar #4).** The Inbox count (blue pill `#1E66C9`, mono), the Notifications unread (amber `#B36D14` square-ish, 15px), and the Setup chip (navy pill, green dot) are three different shapes/colors/sizes for "count that needs attention." No single status-badge system across the chrome.
- **Hand-rolled hover via JS (Bar #9).** Every nav link, icon button, and breadcrumb sets hover colors through `onMouseEnter/onMouseLeave` inline JS rather than CSS `:hover`/`:focus-visible`. Focus-visible rings are not consistently visible on the navy chrome controls; keyboard users get weak affordance.
- **Workspace badge looks clickable but isn't.** It's a card with initials + name + plan but no menu — fine as honesty, but visually it reads like a switcher; a user will click it expecting org-switching.
- **No scrim on the mobile nav drawer.** Opaque full-screen panel with no dimmed context behind; loses the "overlay over my work" mental model and the tap-outside-to-close affordance other overlays have.
- **Notifications & command-palette mix glyphs.** The palette uses ASCII/symbol icons (`↗ ⊞ ◎ ⚠ ❤ ▤ ≣ ▶`) while the rest of the app uses Lucide — inconsistent icon language, and several read as clip-art.
- **Help unreachable on mobile; two help entry points.** The "?" is `sm+` only, and Help also lives as a sidebar link that opens a new tab AND as the slideover — three overlapping "help" surfaces with different behaviors.
- **Breadcrumb is the only depth cue; no page H1 in chrome.** On detail pages the breadcrumb carries all orientation; on mobile it degrades to a single label.

### Redesign recommendations (for Claude Design)
1. **Tokenize the shell.** Replace every inline hex/px literal in `BridgeSidebar`, `BridgeTopbar`, `CommandPalette`, `HelpSlideover`, `SetupProgressChip` with the existing CSS variables and a strict 4/8 spacing scale. Keep navy `#0B1A2F` + violet/blue `#1E66C9` brand and the blue→green wire identity. This is the single highest-leverage change — it makes the chrome consistent with the token-driven pages for free.
2. **One type scale + weight hierarchy (Bar #2).** Collapse the 10.5/11.5/12.5/13.5 fractional sizes onto a real scale (e.g. 11/12/13/14/16/18) with weight, not color, carrying hierarchy. Ensure nav inactive labels (`#C8D1E0` on navy) and faint text meet 4.5:1.
3. **Unify to ONE toast system (Bar #4/#6).** Drop either `<Toaster/>` or `<Sonner/>`; standardize one toast shape with green/amber/red/neutral + icon semantics, matching the status-badge system.
4. **One status-badge system for the chrome (Bar #4).** Make the Inbox count, Notifications unread, and Setup chip share one pill shape/size/padding and the green/amber/red/neutral palette with an icon-or-word — never color alone. Tabular/mono figures for all counts (already partly done) so they don't jitter.
5. **CSS-native hover + focus-visible (Bar #9).** Replace the `onMouseEnter/onMouseLeave` JS hover handlers with `:hover`/`:focus-visible` so every nav item, icon button, and crumb has a visible, consistent focus ring on navy. Keep ≥44px hit areas (already correct).
6. **Add a scrim + slide-in animation to the mobile nav drawer (Bar overlays).** Give it the same `rgba(11,26,47,.x)` scrim, tap-outside-to-close, and animate-from-trigger that the palette and help slideover already have, so all overlays behave identically.
7. **Resolve the tablet rail cliff (Bar #10).** Either tween the 232↔64 collapse smoothly across the `md`→`lg` band, or keep labels longer; today labels vanish abruptly at 1024px. Stacked-not-shrunk principle: confirm the phone drawer shows full labels (it does) and reaches every group.
8. **Normalize the command-palette + popover icons to Lucide.** Replace the ASCII symbol glyphs with the same Lucide set the sidebar uses; keep the per-group color accents.
9. **Make the workspace badge honestly inert or a real switcher.** Either flatten it so it doesn't read as a button, or wire an actual org switcher — don't leave a card that invites a click with no menu.
10. **Consolidate help entry points.** Pick one primary help affordance (the slideover) and make it reachable on mobile; demote the new-tab `/help` link and de-duplicate the three "help" surfaces.
