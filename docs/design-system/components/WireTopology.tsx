"use client";
import * as React from "react";

/**
 * <WireTopology>
 *
 * The Bridge dashboard centerpiece — a network diagram with buyer ports
 * on the left and supplier ports on the right, wires arcing between them.
 *
 * Volume → stroke width (1..6). Health → stroke gradient (ok | warn).
 * Active wires get a travelling pulse via CSS offset-path.
 */

export type Port = {
  id: string;
  name: string;
  code: string;
  y: number;
  /** For supplier ports — flag a warning state (amber border) */
  warn?: boolean;
  /** For buyer ports — weekly volume */
  volume?: number;
};

export type Wire = {
  b: string;        // buyer port id
  s: string;        // supplier port id
  weight: 1 | 2 | 3 | 4 | 5 | 6;
  health: "ok" | "warn";
  /** Exceptions on this lane in the window — shows a mid-wire badge */
  alert?: number;
};

type WireTopologyProps = {
  buyers: Port[];      // place at left
  suppliers: Port[];   // place at right
  wires: Wire[];
  width?: number;      // viewBox width (default 1280)
  height?: number;     // viewBox height (default 600)
  title?: string;
  subtitle?: string;
  onWireClick?: (lane: { b: string; s: string }) => void;
};

// Token-aware colors so the SVG works inside a Tailwind app without theme().
const COLORS = {
  blue:        "#1E66C9",
  green:       "#2E8E3A",
  amber:       "#C97A14",
  surface:     "#FFFFFF",
  ink:         "#0B1A2F",
  inkMuted:    "#56627A",
  inkFaint:    "#8A93A5",
  borderStrong:"#C6CDDA",
};

export function WireTopology({
  buyers,
  suppliers,
  wires,
  width = 1280,
  height = 600,
  // "Order topology" was purged from user-facing copy; the shipped heading is "Orders".
  title = "Orders",
  subtitle,
  onWireClick,
}: WireTopologyProps) {

  // CSS for the travelling pulse (injected once)
  React.useEffect(() => {
    if (document.getElementById("procu-wire-css")) return;
    const s = document.createElement("style");
    s.id = "procu-wire-css";
    s.textContent = `
      @keyframes procu-wire-pulse {
        0%       { offset-distance: 0%;   opacity: 0; }
        10%, 90% { opacity: 1; }
        100%     { offset-distance: 100%; opacity: 0; }
      }
      .procu-wire-traveller {
        offset-rotate: 0deg;
        animation: procu-wire-pulse 6s linear infinite;
      }
      @media (prefers-reduced-motion: reduce) {
        .procu-wire-traveller { animation: none !important; offset-distance: 50% !important; opacity: 1 !important; }
      }
    `;
    document.head.appendChild(s);
  }, []);

  return (
    <div className="relative w-full h-full">
      <svg viewBox={`0 0 ${width} ${height}`} width="100%" height="100%" preserveAspectRatio="xMidYMid meet">
        <defs>
          <linearGradient id="procu-wire-grad" x1="0" x2="1">
            <stop offset="0%" stopColor={COLORS.blue}/>
            <stop offset="100%" stopColor={COLORS.green}/>
          </linearGradient>
          <linearGradient id="procu-wire-warn" x1="0" x2="1">
            <stop offset="0%" stopColor={COLORS.blue}/>
            <stop offset="50%" stopColor={COLORS.amber}/>
            <stop offset="100%" stopColor={COLORS.amber}/>
          </linearGradient>
        </defs>

        {/* Title block — above ports */}
        <text x="48" y="40" style={{ fill: COLORS.ink, fontSize: 20, fontWeight: 600, letterSpacing: "-0.015em", fontFamily: "Bricolage Grotesque, Inter, sans-serif" }}>
          {title}
        </text>
        {subtitle && (
          <text x="48" y="64" style={{ fill: COLORS.inkMuted, fontSize: 12, fontFamily: "Inter, sans-serif" }}>
            {subtitle}
          </text>
        )}

        {/* Legend — top right */}
        <g transform="translate(1100, 32)">
          <text x="170" y="0" textAnchor="end" style={{ fill: COLORS.inkFaint, fontSize: 10, fontWeight: 600, letterSpacing: "0.06em", fontFamily: "Inter, sans-serif" }}>
            VOLUME
          </text>
          <line x1="0"   y1="14" x2="50"  y2="14" stroke={COLORS.ink} strokeWidth="1"/>
          <line x1="60"  y1="14" x2="110" y2="14" stroke={COLORS.ink} strokeWidth="2.5"/>
          <line x1="120" y1="14" x2="170" y2="14" stroke={COLORS.ink} strokeWidth="5"/>
          <text x="0"   y="32" style={{ fill: COLORS.inkFaint, fontSize: 9, fontFamily: "Inter, sans-serif" }}>low</text>
          <text x="170" y="32" textAnchor="end" style={{ fill: COLORS.inkFaint, fontSize: 9, fontFamily: "Inter, sans-serif" }}>high</text>
        </g>

        {/* Wires */}
        {wires.map((w, i) => {
          const b = buyers.find(x => x.id === w.b);
          const s = suppliers.find(x => x.id === w.s);
          if (!b || !s) return null;
          const x1 = 280, y1 = b.y, x2 = 1000, y2 = s.y;
          const cx1 = 540, cx2 = 740;
          const path = `M ${x1} ${y1} C ${cx1} ${y1}, ${cx2} ${y2}, ${x2} ${y2}`;
          const stroke = w.health === "warn" ? "url(#procu-wire-warn)" : "url(#procu-wire-grad)";

          return (
            <g
              key={`${w.b}-${w.s}`}
              onClick={() => onWireClick?.({ b: w.b, s: w.s })}
              style={{ cursor: onWireClick ? "pointer" : "default" }}
            >
              <path d={path} stroke={stroke} strokeWidth={w.weight} fill="none" opacity={0.72}/>
              <circle
                r={3}
                fill={COLORS.surface}
                stroke={COLORS.green}
                strokeWidth={1.5}
                className="procu-wire-traveller"
                style={{ offsetPath: `path('${path}')`, animationDelay: `-${i * 0.6}s` }}
              />
              {w.alert && (
                <g transform={`translate(${(x1 + x2) / 2}, ${(y1 + y2) / 2 - 12})`}>
                  <circle r={9} fill={COLORS.surface} stroke={COLORS.amber} strokeWidth={1.5}/>
                  <text x="0" y="3" textAnchor="middle" style={{ fill: COLORS.amber, fontSize: 9, fontWeight: 700, fontFamily: "Inter, sans-serif" }}>
                    {w.alert}
                  </text>
                </g>
              )}
            </g>
          );
        })}

        {/* Buyer ports (left) */}
        {buyers.map(b => (
          <g key={b.id}>
            <rect x="80" y={b.y - 22} width="200" height="44" rx="6" fill={COLORS.surface} stroke={COLORS.blue} strokeWidth="1.5"/>
            <rect x="80" y={b.y - 22} width="3" height="44" fill={COLORS.blue}/>
            <text x="98" y={b.y - 4} style={{ fill: COLORS.ink, fontSize: 12.5, fontWeight: 600, fontFamily: "Inter, sans-serif" }}>{b.name}</text>
            <text x="98" y={b.y + 12} style={{ fill: COLORS.inkFaint, fontSize: 10, fontFamily: "JetBrains Mono, monospace" }}>
              {b.code}{b.volume ? ` · ${b.volume}/wk` : ""}
            </text>
            <circle cx="280" cy={b.y} r="5" fill={COLORS.blue}/>
          </g>
        ))}

        {/* Supplier ports (right) */}
        {suppliers.map(s => (
          <g key={s.id}>
            <rect x="1000" y={s.y - 22} width="200" height="44" rx="6" fill={COLORS.surface} stroke={s.warn ? COLORS.amber : COLORS.green} strokeWidth="1.5"/>
            <rect x="1197" y={s.y - 22} width="3" height="44" fill={s.warn ? COLORS.amber : COLORS.green}/>
            <text x="1018" y={s.y - 4} style={{ fill: COLORS.ink, fontSize: 12.5, fontWeight: 600, fontFamily: "Inter, sans-serif" }}>{s.name}</text>
            <text x="1018" y={s.y + 12} style={{ fill: COLORS.inkFaint, fontSize: 10, fontFamily: "JetBrains Mono, monospace" }}>{s.code}</text>
            <circle cx="1000" cy={s.y} r="5" fill={COLORS.surface} stroke={s.warn ? COLORS.amber : COLORS.green} strokeWidth="2"/>
          </g>
        ))}

        {/* Bottom caption */}
        <text
          x={width / 2}
          y={height - 14}
          textAnchor="middle"
          style={{ fill: COLORS.inkFaint, fontSize: 10, fontWeight: 700, letterSpacing: "0.12em", fontFamily: "Inter, sans-serif", textTransform: "uppercase" }}
        >
          ↑ The Bridge — parse · normalize · validate · transform · deliver ↑
        </text>
      </svg>
    </div>
  );
}
