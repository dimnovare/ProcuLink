# 06 — Motion

Motion is for **state**, not decoration. Six patterns, each with a single job. All respect `prefers-reduced-motion: reduce`.

## Patterns

### M1 · Link-spine activation

**When:** An order advances a stage (validate → transform, transform → deliver).
**What:** The 2px topbar spine fills left→right.
**Duration:** 1200ms · `--ease-out`.

```css
@keyframes link-spine-fill {
  0%   { background-position: -100% 0; }
  100% { background-position: 100% 0; }
}
.link-spine[data-animated="true"] {
  background-size: 200% 100%;
  animation: link-spine-fill var(--duration-spine) var(--ease-out) forwards;
}
```

### M2 · Wire-topology travellers

**When:** Always, on the Bridge dashboard.
**What:** Tiny white-dot pulses move along each active wire.
**Duration:** 6s loop, staggered −0.6s per wire.

```css
@keyframes wire-pulse {
  0%       { offset-distance: 0%;  opacity: 0; }
  10%, 90% { opacity: 1; }
  100%     { offset-distance: 100%; opacity: 0; }
}
.wire-traveller {
  offset-rotate: 0deg;
  animation: wire-pulse var(--duration-wire-loop) linear infinite;
}
```

Per wire, set inline:
```tsx
style={{
  offsetPath: `path('${pathD}')`,
  animationDelay: `-${wireIndex * 0.6}s`
}}
```

### M3 · Status node pulse

**When:** The active stage in `<StatusJourney>` activates.
**What:** The current node pulses once with a brand-color ring.
**Duration:** 2s, no loop on initial mount, no further pulses unless stage changes.

```css
@keyframes node-pulse {
  0%   { box-shadow: 0 0 0 0     rgba(30,102,201,0.5); }
  70%  { box-shadow: 0 0 0 14px  rgba(30,102,201,0); }
  100% { box-shadow: 0 0 0 0     rgba(30,102,201,0); }
}
.status-node[data-active="true"] { animation: node-pulse 2s ease-out; }
```

### M4 · Connector draw

**When:** A SKU mapping is accepted or saved.
**What:** The buyer-SKU ↔ supplier-SKU line draws via `stroke-dashoffset`, blue→green.
**Duration:** 800ms · `--ease-out`.

```css
@keyframes connector-draw {
  from { stroke-dashoffset: 200; }
  to   { stroke-dashoffset: 0; }
}
.connector-line[data-just-saved="true"] {
  stroke-dasharray: 200;
  animation: connector-draw 800ms var(--ease-out) forwards;
}
```

### M5 · Validate-to-deliver flush

**When:** "Cross the bridge" is clicked.
**What:** Each stage in `<StatusJourney>` advances in 40ms stagger; the output panel briefly highlights.
**Duration:** ~200ms cascade + 250ms highlight.

Implementation: setTimeout cascade in component state, plus a single 250ms background-color transition on the output panel.

### M6 · Empty-state link-close

**When:** Hovering an empty card or placeholder.
**What:** The System Identity mark inside the empty state completes its loop.
**Duration:** 600ms · `--ease-in-out`.

Implementation: the mark has two SVG paths drawn with `stroke-dasharray`. On hover, animate `stroke-dashoffset` to 0.

## Budget

- Loop max: **6 seconds**. Anything longer is annoying.
- Concurrent animations on screen: max 2 + the wire-topology background.
- Pause all motion while the user is typing in an input. (Listen on input focus.)
- Pause all motion above the action bar during keyboard navigation (`j` / `k` in the inbox, arrow keys in tables).

## Reduced motion

```css
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 1ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 1ms !important;
  }
}
```

For the wire topology specifically, swap the travellers for a single static dot at the midpoint of each wire under reduced motion.

## Don'ts

- ✕ No bouncing, spring overshoot, or elastic curves. We are operational software.
- ✕ No floating elements that drift on hover.
- ✕ No skeleton shimmer (it's distracting in dense tables). Use a subtle pulse on the row background instead.
- ✕ No page transitions on route change. Routes load instantly.
- ✕ No micro-animations on hover (e.g. icon spin, color sweep). Hover changes state, period.
