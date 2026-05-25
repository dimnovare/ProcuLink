"use client";
import * as React from "react";

/**
 * <CanonicalSpine> + <SpineNode>
 *
 * The vertical schema spine that anchors the order review.
 * Children must be <SpineNode>. The container draws the 3px link-spine line
 * down the column; each node renders a circle on the line + a card to the right.
 */

type SpineProps = {
  children: React.ReactNode;
  className?: string;
};

export function CanonicalSpine({ children, className }: SpineProps) {
  return (
    <div className={["relative py-2", className].filter(Boolean).join(" ")}>
      {/* The spine itself */}
      <div
        aria-hidden
        className="absolute top-0 bottom-0 w-spine rounded-sm bg-link-spine"
        style={{ left: 22 }}
      />
      {children}
    </div>
  );
}

/* ----- SpineNode ----- */

type SubNode = {
  sku: string;
  qty: number;
  /** Mark as AI-mapped */
  ai?: boolean;
  /** AI confidence */
  pct?: number;
  /** Mark as error */
  err?: boolean;
  /** Optional inline note */
  hint?: string;
};

type SpineNodeProps = {
  /** Index — drives the node color (first ~4 are blue, last ~ are green) */
  index: number;
  /** Total nodes — to color from buyer-side to supplier-side */
  total?: number;
  label: string;
  value: React.ReactNode;
  confidence: number;       // 0..100
  mono?: boolean;
  big?: boolean;
  tone?: "buyer" | "supplier";
  hint?: string;
  srcRef: string;
  outRef: string;
  subnodes?: SubNode[];
  onJump?: () => void;
};

export function SpineNode(props: SpineNodeProps) {
  const {
    index, total = 9,
    label, value, confidence, mono, big, tone, hint, srcRef, outRef, subnodes, onJump,
  } = props;
  const isWarn = confidence < 90;
  const isErr  = confidence < 75;

  const conf =
    confidence >= 90 ? { bg: "bg-brand-green-soft", fg: "text-brand-green-deep" } :
    confidence >= 75 ? { bg: "bg-amber-soft",       fg: "text-amber" } :
                       { bg: "bg-danger-soft",      fg: "text-danger" };

  const bg =
    isErr  ? "bg-danger-soft" :
    isWarn ? "bg-amber-soft" : "bg-surface";

  const border =
    isErr  ? "border-[#F0D2D2]" :
    isWarn ? "border-[#F0E0BD]" : "border-border";

  // Color the dot blue for first ~half, green for second half
  const dotBorder = index < total / 2 ? "border-brand-blue" : "border-brand-green";

  return (
    <div className="relative mb-2.5 pl-9">
      {/* Node dot on the spine */}
      <div
        aria-hidden
        className={["absolute top-3.5 w-[13px] h-[13px] rounded-full bg-white border-[2.5px] z-base", dotBorder].join(" ")}
        style={{ left: 17 }}
      />

      {/* Connector stubs */}
      <svg width="14" height="34" viewBox="0 0 14 34" aria-hidden className="absolute" style={{ left: -14, top: 8 }}>
        <path d="M 0 17 Q 7 17 14 17" stroke="#1E66C9" strokeWidth="1" strokeDasharray="2 2" fill="none"/>
      </svg>
      <svg width="14" height="34" viewBox="0 0 14 34" aria-hidden className="absolute" style={{ right: -14, top: 8 }}>
        <path d="M 0 17 Q 7 17 14 17" stroke="#2E8E3A" strokeWidth="1" strokeDasharray="2 2" fill="none"/>
      </svg>

      <div
        className={["border rounded-md p-2.5", bg, border].join(" ")}
        onClick={onJump}
        role={onJump ? "button" : undefined}
        tabIndex={onJump ? 0 : undefined}
      >
        {/* Header row */}
        <div className="flex items-center gap-1.5 text-[10px] uppercase tracking-[0.05em] font-semibold text-ink-faint mb-1">
          {tone === "buyer"    && <span className="w-[5px] h-[5px] rounded-sm bg-brand-blue"/>}
          {tone === "supplier" && <span className="w-[5px] h-[5px] rounded-sm bg-brand-green"/>}
          <span>{label}</span>
          <span className={["ml-auto font-mono text-[9.5px] font-bold px-1 py-px rounded-sm", conf.bg, conf.fg].join(" ")}>
            {confidence}%
          </span>
        </div>

        {/* Value */}
        <div
          className={[
            big ? "text-[16px] font-semibold" : "text-[12.5px] font-medium",
            mono ? "font-mono" : "",
          ].filter(Boolean).join(" ")}
          style={big ? { letterSpacing: "-0.01em" } : {}}
        >
          {value}
        </div>

        {hint && <div className="text-[10.5px] text-amber mt-1">⚠ {hint}</div>}

        {/* Subnodes */}
        {subnodes && subnodes.length > 0 && (
          <div className="mt-1.5 pt-1.5 border-t border-dashed border-border flex flex-col gap-1">
            {subnodes.map((sn, i) => (
              <div
                key={i}
                className={[
                  "flex items-center gap-2 text-[10.5px] px-1 py-0.5 rounded-sm",
                  sn.err ? "bg-danger-soft" :
                  sn.ai  ? "bg-ai-soft"     : "bg-transparent",
                ].join(" ")}
              >
                <span
                  className={[
                    "font-mono font-semibold flex-1",
                    sn.err ? "text-danger" :
                    sn.ai  ? "text-ai"     : "text-brand-green-deep",
                  ].join(" ")}
                >
                  {sn.sku}
                </span>
                <span
                  className={[
                    "font-mono",
                    sn.err ? "text-danger font-bold" : "text-ink-muted",
                  ].join(" ")}
                >
                  {sn.qty}
                </span>
                {sn.pct && <span className="text-[9px] font-bold text-ai">{sn.pct}%</span>}
              </div>
            ))}
          </div>
        )}

        {/* Footer: source → output mapping refs (inside the card) */}
        <div className="mt-2 pt-1.5 border-t border-dashed border-border flex items-center gap-1.5 text-[9.5px] font-mono font-semibold leading-tight">
          <span title={srcRef} className="text-brand-blue overflow-hidden text-ellipsis whitespace-nowrap max-w-[45%]">
            ← {srcRef}
          </span>
          <span className="text-ink-faint shrink-0">→</span>
          <span title={outRef} className="text-brand-green-deep overflow-hidden text-ellipsis whitespace-nowrap flex-1 text-right">
            {outRef}
          </span>
        </div>
      </div>
    </div>
  );
}
