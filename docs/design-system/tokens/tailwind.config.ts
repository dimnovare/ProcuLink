import type { Config } from "tailwindcss";

/**
 * ProcuLink Tailwind theme · v1.0 · "The Bridge Layer"
 *
 * Wire this into your Next.js + Tailwind app:
 *
 *   // tailwind.config.ts at the project root
 *   import procu from "./design-system/tokens/tailwind.config";
 *   export default {
 *     ...procu,
 *     content: ["./app/**\/*.{ts,tsx}", "./components/**\/*.{ts,tsx}"]
 *   } satisfies Config;
 *
 * Or merge with your existing config — just spread procu.theme.extend
 * into your theme.extend.
 */

const config: Pick<Config, "theme" | "plugins"> = {
  theme: {
    extend: {
      colors: {
        brand: {
          blue:       "#1E66C9",
          "blue-deep":"#0F4FA8",
          "blue-soft":"#E3EDFB",
          green:      "#2E8E3A",
          "green-deep":"#1E6D29",
          "green-soft":"#E2F1E2",
        },
        navy: {
          DEFAULT: "#0B1A2F",
          surface: "#10243E",
          border:  "#1C2F49",
          text:    "#C5D2E4",
          muted:   "#7C8DA6",
        },
        bg:       "#F6F7FA",
        "bg-warm":"#F8F6F1",
        surface: {
          DEFAULT: "#FFFFFF",
          "2":     "#EFF2F7",
        },
        border: {
          DEFAULT: "#E2E6EE",
          strong:  "#C6CDDA",
        },
        ink: {
          DEFAULT: "#0B1A2F",
          muted:   "#56627A",
          faint:   "#8A93A5",
        },
        amber: {
          DEFAULT: "#C97A14",
          soft:    "#FAEFD6",
        },
        danger: {
          DEFAULT: "#C53A3A",
          soft:    "#FBE3E3",
        },
        ai: {
          DEFAULT: "#6F4FCE",
          soft:    "#EEE7FB",
        },
      },

      fontFamily: {
        sans:    ['"Inter"', "system-ui", "-apple-system", "sans-serif"],
        display: ['"Bricolage Grotesque"', '"Inter"', "system-ui", "sans-serif"],
        mono:    ['"JetBrains Mono"', "ui-monospace", "monospace"],
      },

      fontSize: {
        xs:         ["10px",   { lineHeight: "1.3" }],
        sm:         ["11.5px", { lineHeight: "1.4" }],
        "body-s":   ["12.5px", { lineHeight: "1.45" }],
        body:       ["13px",   { lineHeight: "1.5" }],
        "body-l":   ["14px",   { lineHeight: "1.55" }],
        h4:         ["16px",   { lineHeight: "1.35", letterSpacing: "-0.01em" }],
        h3:         ["18px",   { lineHeight: "1.3",  letterSpacing: "-0.015em" }],
        h2:         ["24px",   { lineHeight: "1.2",  letterSpacing: "-0.02em" }],
        h1:         ["32px",   { lineHeight: "1.15", letterSpacing: "-0.025em" }],
        "display-s":["36px",   { lineHeight: "1.05", letterSpacing: "-0.025em" }],
        display:    ["48px",   { lineHeight: "1.0",  letterSpacing: "-0.03em" }],
        "display-l":["78px",   { lineHeight: "0.98", letterSpacing: "-0.035em" }],
      },

      borderRadius: {
        sm:     "4px",
        DEFAULT:"6px",
        md:     "8px",
        lg:     "10px",
        xl:     "12px",
        full:   "9999px",
      },

      boxShadow: {
        card: "0 1px 2px rgba(11,26,47,0.04)",
        pop:  "0 8px 24px rgba(11,26,47,0.10)",
        hero: "0 50px 120px rgba(11,26,47,0.10), 0 8px 24px rgba(11,26,47,0.06)",
      },

      backgroundImage: {
        "link-spine":    "linear-gradient(90deg, #1E66C9 0%, #1E66C9 35%, #2E8E3A 65%, #2E8E3A 100%)",
        "bridge-deck":   "linear-gradient(90deg, #1E66C9, #2E8E3A)",
        "rail-buyer":    "linear-gradient(180deg, rgba(30,102,201,0.2), #1E66C9 50%, rgba(30,102,201,0.2))",
        "rail-supplier": "linear-gradient(180deg, rgba(46,142,58,0.2), #2E8E3A 50%, rgba(46,142,58,0.2))",
      },

      spacing: {
        // Tailwind already covers 1..96 via the default 0.25rem scale.
        // Add named tokens where useful:
        "rail":      "4px",   // edge-rail thickness
        "card-edge": "3px",   // XCard strip
        "spine":     "3px",   // canonical-spine line
        "topbar":    "52px",  // topbar height
        "sidebar":   "220px", // sidebar width
      },

      zIndex: {
        rails:   "1",
        sticky:  "10",
        drawer:  "20",
        topbar:  "30",
        popover: "40",
        modal:   "50",
        toast:   "60",
      },

      transitionTimingFunction: {
        "ease-out-soft":  "cubic-bezier(0.16, 1, 0.3, 1)",
        "ease-in-out-soft": "cubic-bezier(0.65, 0, 0.35, 1)",
      },

      keyframes: {
        "link-spine-fill": {
          "0%":   { backgroundPosition: "-100% 0" },
          "100%": { backgroundPosition: "100% 0" },
        },
        "wire-pulse": {
          "0%":      { offsetDistance: "0%",   opacity: "0" },
          "10%,90%": { opacity: "1" },
          "100%":    { offsetDistance: "100%", opacity: "0" },
        },
        "node-pulse": {
          "0%":   { boxShadow: "0 0 0 0    rgba(30, 102, 201, 0.5)" },
          "70%":  { boxShadow: "0 0 0 14px rgba(30, 102, 201, 0)" },
          "100%": { boxShadow: "0 0 0 0    rgba(30, 102, 201, 0)" },
        },
        "connector-draw": {
          from: { strokeDashoffset: "200" },
          to:   { strokeDashoffset: "0" },
        },
      },
      animation: {
        "link-spine":    "link-spine-fill 1200ms cubic-bezier(0.16,1,0.3,1) forwards",
        "wire-pulse":    "wire-pulse 6s linear infinite",
        "node-pulse":    "node-pulse 2s ease-out",
        "connector-draw":"connector-draw 800ms cubic-bezier(0.16,1,0.3,1) forwards",
      },
    },
  },

  plugins: [],
};

export default config;
