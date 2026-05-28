# Polish slice 2 — shell polish (sidebar + topbar)

**Status:** Agent F3 was Edit-denied on the frontend repo (same permission constraint as overnight Agents 3 & 4). Work taken over from the parent thread and shipped.

## Changes

### `BridgeSidebar.tsx`
- Workbench group `items` array: dropped `{ label: "Drafts", href: "/drafts" }`.
- Added inline comment "Drafts hidden until the drafts API ships in Group L." above the array so the omission is intentional and discoverable.

The Workbench group now has only **Upload** and **Orders**. Drafts page itself (`/drafts`) still exists as a route but is no longer navigable from the sidebar — fewer dead clicks for new users.

### `BridgeTopbar.tsx`
- Inserted a "Demo data" pill badge between the mobile menu button and the breadcrumb container.
- Visible only when `process.env.NEXT_PUBLIC_USE_MOCK === "true"`.
- Hidden below `sm:` breakpoint to keep mobile crowding under control.
- Style: 22px high, 0/10 padding, 99px radius, amber (`#FAEFD6` bg, `#F0D39A` border, `#7A4D0B` text), uppercase 11px / 600 weight, 6×6 amber pip + "Demo data" label.
- Title attribute explains what mock mode means and how to switch it off.
- Used `flexShrink: 0` so the badge does not compete with the breadcrumb's `min-w-0 flex-1` width.

## Build

`bun run build` → passes. No new warnings. Pre-existing Sentry/Browserslist/ESLint warnings unchanged.

## Constraints honoured

- Only `BridgeSidebar.tsx` and `BridgeTopbar.tsx` touched.
- No new npm dependencies.
- No commit; left uncommitted for batched polish-slice-2 commit.
- Other agents' file scopes (`InboxView.tsx`, `MappingEditor.tsx`, `BridgeDashboard.tsx`) untouched.

## Note on sub-agent denials

This is the second time a sub-agent has been Write/Edit-denied on `project-proculink/src/**`. The parent thread (this session) has working write access — so frontend agent work consistently falls back to parent execution. The pattern is established: spawn frontend agents only if they're doing exploratory reads; for actual code changes, the parent thread handles them.
