"use client";
import * as React from "react";
import { confidenceTier } from "../tokens/tokens";

/* =====================================================================
   Primitives — Button, ConfidenceChip, SrcChip, StatusPill,
   AiSuggestion, ProcuLinkMark.
   ===================================================================== */

/* -------- Button -------- */
type ButtonVariant = "primary" | "secondary" | "ghost" | "danger" | "ai";
type ButtonSize = "sm" | "md" | "lg";

type ButtonProps = React.ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
  size?: ButtonSize;
  loading?: boolean;
};

const BUTTON_BASE =
  "inline-flex items-center justify-center gap-2 rounded font-medium border whitespace-nowrap transition-colors disabled:opacity-50 disabled:cursor-not-allowed";

const BUTTON_VARIANT: Record<ButtonVariant, string> = {
  primary:   "bg-navy text-white border-transparent hover:bg-navy-surface",
  secondary: "bg-surface text-ink border-border hover:bg-surface-2 hover:border-border-strong",
  ghost:     "bg-transparent text-ink-muted border-transparent hover:bg-surface-2 hover:text-ink",
  danger:    "bg-danger text-white border-transparent hover:opacity-90",
  ai:        "bg-ai text-white border-transparent hover:opacity-90",
};

const BUTTON_SIZE: Record<ButtonSize, string> = {
  sm: "h-[26px] px-2.5 text-[12px]",
  md: "h-[30px] px-3   text-[12.5px]",
  lg: "h-[36px] px-4   text-[14px]",
};

export function Button({
  variant = "secondary",
  size = "md",
  loading,
  className,
  children,
  ...rest
}: ButtonProps) {
  return (
    <button
      {...rest}
      className={[BUTTON_BASE, BUTTON_VARIANT[variant], BUTTON_SIZE[size], className].filter(Boolean).join(" ")}
      disabled={rest.disabled || loading}
    >
      {loading && <span className="w-3 h-3 rounded-full border-2 border-current border-t-transparent animate-spin"/>}
      {children}
    </button>
  );
}

/* -------- ConfidenceChip -------- */
export function ConfidenceChip({ value }: { value: number }) {
  const tier = confidenceTier(value);
  const cls =
    tier === "ok"   ? "bg-brand-green-soft text-brand-green-deep" :
    tier === "warn" ? "bg-amber-soft text-amber" :
                      "bg-danger-soft text-danger";
  return (
    <span className={["font-mono text-[10px] font-bold px-1 py-px rounded-sm", cls].join(" ")}>
      {value}%
    </span>
  );
}

/* -------- SrcChip — file-type chip -------- */
type SrcType = "PDF" | "XLSX" | "CSV" | "XML" | "cXML" | "EDI" | "EMAIL" | "API" | "JSON";

const SRC_PALETTE: Record<SrcType, { bg: string; fg: string }> = {
  PDF:   { bg: "#FBEEEE", fg: "#B53F3F" },
  XLSX:  { bg: "#E2F1E2", fg: "#1E6D29" },
  CSV:   { bg: "#EEF3F8", fg: "#345470" },
  XML:   { bg: "#EEE7FB", fg: "#5E3DB0" },
  cXML:  { bg: "#EEE7FB", fg: "#5E3DB0" },
  EDI:   { bg: "#FAEFD6", fg: "#C97A14" },
  EMAIL: { bg: "#E9EDF3", fg: "#4A5568" },
  API:   { bg: "#E3F0E3", fg: "#1E6D29" },
  JSON:  { bg: "#FFF4D6", fg: "#846100" },
};

export function SrcChip({ type }: { type: SrcType }) {
  const p = SRC_PALETTE[type];
  return (
    <span
      className="font-mono text-[10px] font-bold px-1.5 py-px rounded-sm"
      style={{ background: p.bg, color: p.fg, letterSpacing: "0.02em" }}
    >
      {type}
    </span>
  );
}

/* -------- StatusPill -------- */
type Status = "new" | "extracting" | "review" | "ready" | "sent" | "failed";

const STATUS_MAP: Record<Status, { bg: string; fg: string; dot: string; label: string }> = {
  new:        { bg: "bg-brand-blue-soft", fg: "text-brand-blue-deep", dot: "bg-brand-blue",  label: "New" },
  extracting: { bg: "bg-ai-soft",         fg: "text-ai",              dot: "bg-ai",          label: "Extracting" },
  review:     { bg: "bg-amber-soft",      fg: "text-amber",           dot: "bg-amber",       label: "Needs review" },
  ready:      { bg: "bg-brand-green-soft",fg: "text-brand-green-deep",dot: "bg-brand-green", label: "Ready" },
  sent:       { bg: "bg-surface-2",       fg: "text-ink-muted",       dot: "bg-ink-faint",   label: "Delivered" },
  failed:     { bg: "bg-danger-soft",     fg: "text-danger",          dot: "bg-danger",      label: "Failed" },
};

export function StatusPill({ status }: { status: Status }) {
  const s = STATUS_MAP[status];
  return (
    <span className={["inline-flex items-center gap-1.5 h-5 px-1.5 rounded-sm text-[11px] font-medium", s.bg, s.fg].join(" ")}>
      <span className={["w-[5px] h-[5px] rounded-full", s.dot].join(" ")}/>
      {s.label}
    </span>
  );
}

/* -------- AiSuggestion -------- */
type AiSuggestionProps = {
  confidence: number;
  title: string;
  description: string;
  children?: React.ReactNode;   // action slot
};

export function AiSuggestion({ confidence, title, description, children }: AiSuggestionProps) {
  return (
    <div className="border border-border border-l-[3px] border-l-ai rounded-md p-2.5 bg-surface">
      <div className="flex items-center gap-2">
        <span className="text-[13px] font-semibold flex-1">{title}</span>
        <span className="text-[9.5px] font-bold uppercase tracking-[0.04em] px-1.5 py-px rounded-sm bg-ai-soft text-ai">
          AI · {confidence}%
        </span>
      </div>
      <div className="text-[11.5px] text-ink-muted mt-1 leading-relaxed">{description}</div>
      {children && <div className="flex gap-1.5 mt-2">{children}</div>}
    </div>
  );
}

/* -------- ProcuLinkMark — System Identity (Direction 3) -------- *
 *
 * Asymmetric link-node: two endpoint dots connected by a single gradient curve.
 * The mark is ONE expression of a shape system that also lives in edge rails,
 * the canonical spine, stage glyphs, and the loading state.
 *
 * Same construction at every size. Use `mono` on dark/navy surfaces.
 */
type MarkProps = {
  size?: number;
  /** Use currentColor instead of brand colors (for sidebar / dark surfaces) */
  mono?: boolean;
  /** When several marks render on one page, pass a unique id (defaults to React.useId). */
  gradientId?: string;
};

export function ProcuLinkMark({ size = 24, mono = false, gradientId }: MarkProps) {
  const autoId = React.useId();
  const id = gradientId ?? `procu-mark-${autoId.replace(/:/g, "")}`;
  const stroke = mono ? "currentColor" : `url(#${id})`;
  const dotA   = mono ? "currentColor" : "#1E66C9";
  const dotB   = mono ? "currentColor" : "#2E8E3A";

  return (
    <svg width={size} height={size} viewBox="0 0 40 40" fill="none" aria-label="ProcuLink" role="img">
      <title>ProcuLink</title>
      {!mono && (
        <defs>
          <linearGradient id={id} x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%"   stopColor="#1E66C9"/>
            <stop offset="100%" stopColor="#2E8E3A"/>
          </linearGradient>
        </defs>
      )}
      {/* The link curve: buyer endpoint → arcs around → supplier endpoint */}
      <path
        d="M8 10h14a10 10 0 0 1 10 10v0a10 10 0 0 1-10 10H8"
        stroke={stroke} strokeWidth="3.6" strokeLinecap="round" fill="none"
      />
      {/* Buyer endpoint */}
      <circle cx="8" cy="10" r="2.5" fill={dotA}/>
      {/* Supplier endpoint */}
      <circle cx="8" cy="30" r="2.5" fill={dotB}/>
    </svg>
  );
}
