"use client";
import * as React from "react";

/**
 * <EdgeRails>
 *
 * The 4px blue + green vertical rails that frame any order-handling work area.
 * Place at the screen body level (NOT around individual cards).
 *
 * Tailwind classes assume the design-system theme is wired up. See
 * design-system/tokens/tailwind.config.ts.
 */

type EdgeRailsProps = {
  children: React.ReactNode;
  /** 0..1 — for subtler variants; default 1. */
  intensity?: number;
  /** Show port markers and vertical labels at the top of each rail. Default true. */
  showLabels?: boolean;
  className?: string;
};

export function EdgeRails({
  children,
  intensity = 1,
  showLabels = true,
  className,
}: EdgeRailsProps) {
  return (
    <div className={["relative w-full h-full", className].filter(Boolean).join(" ")}>
      {/* Buyer rail (left) */}
      <div
        aria-hidden
        className="absolute left-0 top-0 bottom-0 w-rail z-rails bg-rail-buyer"
        style={{ opacity: intensity }}
      />
      {showLabels && (
        <>
          {/* Port marker — left */}
          <div
            aria-hidden
            className="absolute left-0 top-8 h-9 w-[14px] bg-brand-blue rounded-r-sm z-rails"
          />
          <div
            aria-hidden
            className="absolute left-[18px] top-9 text-[9px] font-bold text-brand-blue uppercase tracking-[0.12em] z-rails"
            style={{ writingMode: "vertical-rl" }}
          >
            Buyer · In
          </div>
        </>
      )}

      {/* Supplier rail (right) */}
      <div
        aria-hidden
        className="absolute right-0 top-0 bottom-0 w-rail z-rails bg-rail-supplier"
        style={{ opacity: intensity }}
      />
      {showLabels && (
        <>
          <div
            aria-hidden
            className="absolute right-0 top-8 h-9 w-[14px] bg-brand-green rounded-l-sm z-rails"
          />
          <div
            aria-hidden
            className="absolute right-[18px] top-9 text-[9px] font-bold text-brand-green uppercase tracking-[0.12em] z-rails"
            style={{ writingMode: "vertical-rl", transform: "rotate(180deg)" }}
          >
            Supplier · Out
          </div>
        </>
      )}

      {children}
    </div>
  );
}
