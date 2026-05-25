"use client";
import * as React from "react";

/**
 * <MonumentNumber>
 *
 * The display-font KPI block. Use on the Bridge dashboard, marketing hero,
 * and order summary surfaces. Reserve for numbers that anchor a section.
 */

type Accent = "success" | "warning" | "danger" | "muted" | "brand";

type MonumentNumberProps = {
  value: string;          // pre-formatted, e.g. "1,284" or "1m 42s"
  label: string;          // small uppercase label above
  sub?: string;           // delta / trend below
  accent?: Accent;        // sub color
  size?: 28 | 32 | 36 | 44 | 48 | 56;
  /** Use on dark surfaces */
  dark?: boolean;
  className?: string;
};

const ACCENT_COLOR: Record<Accent, string> = {
  success: "text-brand-green-deep",
  warning: "text-amber",
  danger:  "text-danger",
  muted:   "text-ink-muted",
  brand:   "text-brand-blue-deep",
};

export function MonumentNumber({
  value,
  label,
  sub,
  accent = "success",
  size = 36,
  dark,
  className,
}: MonumentNumberProps) {
  return (
    <div className={className}>
      <div
        className={[
          "text-[11px] font-semibold uppercase tracking-[0.06em]",
          dark ? "text-navy-muted" : "text-ink-muted",
        ].join(" ")}
      >
        {label}
      </div>
      <div
        className={[
          "font-display font-semibold leading-none mt-1.5",
          dark ? "text-white" : "text-ink",
        ].join(" ")}
        style={{
          fontSize: size,
          letterSpacing: "-0.035em",
        }}
      >
        {value}
      </div>
      {sub && (
        <div className={["text-[12px] font-medium mt-1", ACCENT_COLOR[accent]].join(" ")}>
          {sub}
        </div>
      )}
    </div>
  );
}
