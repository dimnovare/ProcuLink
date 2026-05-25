"use client";
import * as React from "react";

/**
 * <LinkSpine>
 *
 * The 2px blue→green gradient line that runs across every topbar and
 * serves as a section divider. Set `animated` for a one-shot fill on a
 * state change (e.g. order advances a stage).
 */

type LinkSpineProps = {
  height?: 1 | 2 | 3;
  /** Fade at the ends */
  soft?: boolean;
  /** Animate left-to-right fill */
  animated?: boolean;
  /** Animation duration in ms (default 1200) */
  duration?: number;
  className?: string;
};

export function LinkSpine({
  height = 2,
  soft = false,
  animated = false,
  duration = 1200,
  className,
}: LinkSpineProps) {
  const base = soft
    ? "bg-gradient-to-r from-transparent via-brand-blue to-brand-green opacity-60"
    : "bg-link-spine";

  return (
    <div
      role="presentation"
      className={[base, className].filter(Boolean).join(" ")}
      style={{
        height,
        ...(animated
          ? {
              backgroundSize: "200% 100%",
              animation: `link-spine-fill ${duration}ms cubic-bezier(0.16, 1, 0.3, 1) forwards`,
            }
          : {}),
      }}
    />
  );
}
