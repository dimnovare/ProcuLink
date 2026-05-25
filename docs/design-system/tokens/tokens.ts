/**
 * ProcuLink design tokens — typed TS exports.
 *
 * Import in any TS file:
 *   import { color, font, size } from "@/design-system/tokens/tokens";
 *
 * Source of truth: tokens.json. Keep this file in sync if you edit JSON.
 */

export const color = {
  brand: {
    blue:      "#1E66C9",
    blueDeep:  "#0F4FA8",
    blueSoft:  "#E3EDFB",
    green:     "#2E8E3A",
    greenDeep: "#1E6D29",
    greenSoft: "#E2F1E2",
  },
  navy: {
    DEFAULT: "#0B1A2F",
    surface: "#10243E",
    border:  "#1C2F49",
    text:    "#C5D2E4",
    muted:   "#7C8DA6",
  },
  bg:       "#F6F7FA",
  bgWarm:   "#F8F6F1",
  surface:  "#FFFFFF",
  surface2: "#EFF2F7",
  border:   "#E2E6EE",
  borderStrong: "#C6CDDA",
  ink:      "#0B1A2F",
  inkMuted: "#56627A",
  inkFaint: "#8A93A5",
  amber:    "#C97A14",
  amberSoft:"#FAEFD6",
  danger:   "#C53A3A",
  dangerSoft:"#FBE3E3",
  ai:       "#6F4FCE",
  aiSoft:   "#EEE7FB",
} as const;

export const gradient = {
  linkSpine:    "linear-gradient(90deg, #1E66C9 0%, #1E66C9 35%, #2E8E3A 65%, #2E8E3A 100%)",
  bridgeDeck:   "linear-gradient(90deg, #1E66C9, #2E8E3A)",
  railBuyer:    "linear-gradient(180deg, rgba(30,102,201,0.2), #1E66C9 50%, rgba(30,102,201,0.2))",
  railSupplier: "linear-gradient(180deg, rgba(46,142,58,0.2), #2E8E3A 50%, rgba(46,142,58,0.2))",
} as const;

export const font = {
  sans:    '"Inter", system-ui, -apple-system, sans-serif',
  display: '"Bricolage Grotesque", "Inter", system-ui, sans-serif',
  mono:    '"JetBrains Mono", ui-monospace, monospace',
} as const;

export const size = {
  xs: 10, sm: 11.5,
  bodyS: 12.5, body: 13, bodyL: 14,
  h4: 16, h3: 18, h2: 24, h1: 32,
  displayS: 36, display: 48, displayL: 78,
} as const;

export const space = {
  1: 4, 2: 8, 3: 12, 4: 16, 5: 20, 6: 24,
  8: 32, 10: 40, 12: 48, 16: 64, 20: 80, 24: 96,
} as const;

export const radius = {
  sm: 4, DEFAULT: 6, md: 8, lg: 10, xl: 12, full: 9999,
} as const;

export const shadow = {
  card: "0 1px 2px rgba(11,26,47,0.04)",
  pop:  "0 8px 24px rgba(11,26,47,0.10)",
  hero: "0 50px 120px rgba(11,26,47,0.10), 0 8px 24px rgba(11,26,47,0.06)",
} as const;

export const motion = {
  easeOut:    "cubic-bezier(0.16, 1, 0.3, 1)",
  easeInOut:  "cubic-bezier(0.65, 0, 0.35, 1)",
  durationFast:    150,
  duration:        250,
  durationSlow:    400,
  durationSpine:  1200,
  durationWireLoop: 6000,
} as const;

export const z = {
  base: 0, rails: 1, sticky: 10, drawer: 20,
  topbar: 30, popover: 40, modal: 50, toast: 60,
} as const;

/**
 * Confidence threshold helper — used by ConfidenceChip, anatomy zones,
 * spine-node field backgrounds, etc.
 */
export function confidenceTier(pct: number): "ok" | "warn" | "danger" {
  if (pct >= 90) return "ok";
  if (pct >= 75) return "warn";
  return "danger";
}

export function confidenceColors(pct: number) {
  const tier = confidenceTier(pct);
  if (tier === "ok")   return { fg: color.brand.greenDeep, bg: color.brand.greenSoft };
  if (tier === "warn") return { fg: color.amber, bg: color.amberSoft };
  return { fg: color.danger, bg: color.dangerSoft };
}
