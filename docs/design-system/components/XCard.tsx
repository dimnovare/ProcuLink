"use client";
import * as React from "react";

/**
 * <XCard>
 *
 * The card with a 3px cross-section edge strip — the "wire seen end-on."
 * Replaces decorative borders / notched corners as the primary card device.
 *
 * - color="buyer"    → blue strip
 * - color="supplier" → green strip
 * - color="bridge"   → full link-spine gradient
 * - color="amber" / "danger" / "ai" → semantic strips
 * - color="none"     → no strip (plain card)
 */

type XEdge = "left" | "right" | "top" | "bottom";
type XColor = "buyer" | "supplier" | "bridge" | "amber" | "danger" | "ai" | "none";

type XCardProps = {
  children: React.ReactNode;
  edge?: XEdge;
  color?: XColor;
  /** Use tighter padding for dense surfaces */
  dense?: boolean;
  className?: string;
};

const COLOR_BG: Record<XColor, string> = {
  buyer:    "bg-brand-blue",
  supplier: "bg-brand-green",
  bridge:   "bg-link-spine",
  amber:    "bg-amber",
  danger:   "bg-danger",
  ai:       "bg-ai",
  none:     "bg-transparent",
};

export function XCard({
  children,
  edge = "left",
  color = "none",
  dense,
  className,
}: XCardProps) {
  const stripPos =
    edge === "left"   ? "left-0 top-0 bottom-0 w-card-edge" :
    edge === "right"  ? "right-0 top-0 bottom-0 w-card-edge" :
    edge === "top"    ? "left-0 right-0 top-0 h-card-edge" :
                        "left-0 right-0 bottom-0 h-card-edge";

  return (
    <div
      className={[
        "relative bg-surface border border-border rounded-md overflow-hidden",
        dense ? "" : "",
        className,
      ].filter(Boolean).join(" ")}
    >
      {color !== "none" && (
        <div aria-hidden className={["absolute", stripPos, COLOR_BG[color]].join(" ")} />
      )}
      {children}
    </div>
  );
}
