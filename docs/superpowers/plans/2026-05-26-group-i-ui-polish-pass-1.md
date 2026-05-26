# Group I UI Polish Pass 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the first visible Bridge Layer UI defect and make the Bridge dashboard behave credibly across narrower viewports.

**Architecture:** Keep Direction 4 - The Bridge Layer as the visual source of truth. Replace detached standalone topology pulse dots with animated wire segments that share the exact rendered SVG path, then tighten the dashboard layout so cards, controls, and operational tables wrap instead of overflowing.

**Tech Stack:** Next.js 15 App Router, React, Tailwind CSS, local ProcuLink design system, bun.

---

## File Structure

- Modify `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\src\components\bridge\WireTopology.tsx`
  - Remove standalone SVG pulse circles.
  - Add attached travelling wire segments on the same `pathD` as each visible wire.
  - Keep Direction 4 buyer-left/supplier-right topology.
- Modify `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\src\components\bridge\BridgeDashboard.tsx`
  - Make dashboard header controls wrap.
  - Make KPI and lower dashboard sections responsive.
  - Prevent queue and supplier-health text overflow.
- Modify `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\src\app\globals.css`
  - Disable topology travellers for `prefers-reduced-motion`.
- Modify `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\STATUS.md`
  - Record that Group I has started and pass 1 is complete after verification.
- Modify `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\docs\superpowers\plans\2026-05-26-production-hardening-roadmap.md`
  - Mark Group I as in progress and document pass 1 scope.

---

### Task 1: Replace Detached Topology Dots With Attached Wire Travellers

**Files:**
- Modify: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\src\components\bridge\WireTopology.tsx`
- Modify: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\src\app\globals.css`

- [x] **Step 1: Inspect the current topology implementation**

Run:

```powershell
Get-Content -LiteralPath C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\src\components\bridge\WireTopology.tsx
```

Expected: visible wire paths and pulse circles both use `pathD`, but the visual traveller is a freestanding circle animated along the path.

- [x] **Step 2: Replace animated circles with same-path animated dash segments**

In each wire group, render duplicate `<path>` elements with the same `d={pathD}`, `pathLength={1}`, and animated `stroke-dashoffset`. Use a wider translucent gradient dash behind a narrower white dash so the traveller remains visibly attached to the wire.

- [x] **Step 3: Hide travellers for reduced motion**

In `globals.css`, add `.wire-traveller` to the reduced-motion rules and hide it with `display: none !important`.

- [x] **Step 4: Verify no standalone dot remains**

Run:

```powershell
Select-String -Path C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\src\components\bridge\WireTopology.tsx -Pattern "animateMotion|<circle r=\{4\}|wire-traveller"
```

Expected: no `animateMotion`, no old pulse circle, and `wire-traveller` exists.

---

### Task 2: Make Dashboard Layout Responsive

**Files:**
- Modify: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\src\components\bridge\BridgeDashboard.tsx`
- Modify: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink\src\components\bridge\WireTopology.tsx`

- [x] **Step 1: Make header controls wrap**

Change the dashboard header to a column layout on small screens and a row on larger screens. Put period buttons and export into a wrapping control row.

- [x] **Step 2: Make KPI cards responsive**

Change the KPI grid from fixed five columns to `grid-cols-1 sm:grid-cols-2 xl:grid-cols-5`.

- [x] **Step 3: Make lower panels responsive**

Change the lower dashboard panels from fixed two columns to `grid-cols-1 xl:grid-cols-2`, and make row content wrap/truncate safely.

- [x] **Step 4: Prevent topology compression on mobile**

Keep the topology's internal geometry readable by allowing horizontal scroll below tablet widths instead of compressing the buyer/supplier rail until labels overlap.

---

### Task 3: Verify And Update Handoff Docs

**Files:**
- Modify: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\STATUS.md`
- Modify: `C:\Users\Dmitri.REDACTED-PARTY\source\repos\ProcuLink\docs\superpowers\plans\2026-05-26-production-hardening-roadmap.md`

- [x] **Step 1: Run frontend build**

Run:

```powershell
cd C:\Users\Dmitri.REDACTED-PARTY\source\repos\project-proculink
bun run build
```

Expected: successful Next.js production build.

- [x] **Step 2: Update status docs**

Record Group I pass 1 as complete if build passes:

```text
Group I started. Pass 1 fixed the Wire Topology detached traveller defect and tightened Bridge dashboard responsiveness. Full route-by-route visual QA remains.
```

- [x] **Step 3: Commit and push safe changes**

Commit frontend UI changes in `project-proculink`, then commit docs/status changes in `ProcuLink`.
