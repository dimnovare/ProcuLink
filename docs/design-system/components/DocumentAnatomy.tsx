"use client";
import * as React from "react";
import { confidenceTier } from "../tokens/tokens";

/**
 * <DocumentAnatomy>
 *
 * Wraps any rendered source document and overlays per-zone confidence
 * annotations. Zone labels live INSIDE the document page at the top-right
 * of each zone — never in a margin (so they never overflow the column).
 *
 * Pass the rendered document (PDF page, spreadsheet, syntax-highlighted XML)
 * as the `document` slot. The component positions zones absolutely on top.
 */

export type AnatomyZone = {
  id: string;
  label: string;            // "Header zone"
  fields: string;           // "PO # · Date"
  confidence: number;       // 0..100
  top: number;              // px from top of document
  height: number;
};

type DocumentAnatomyProps = {
  document: React.ReactNode;
  zones: AnatomyZone[];
  activeZoneId?: string;
  onZoneHover?: (id: string | null) => void;
  className?: string;
};

export function DocumentAnatomy({
  document,
  zones,
  activeZoneId,
  onZoneHover,
  className,
}: DocumentAnatomyProps) {
  return (
    <div className={["relative bg-bg rounded-md p-3 border border-border", className].filter(Boolean).join(" ")}>
      <div className="relative">
        {document}

        {/* Zone overlays */}
        {zones.map(z => {
          const tier = confidenceTier(z.confidence);
          const ok = tier === "ok";
          const isActive = z.id === activeZoneId;
          return (
            <React.Fragment key={z.id}>
              <div
                role="region"
                aria-label={`${z.label} — ${z.confidence}% confidence`}
                className={[
                  "absolute -left-0.5 -right-0.5 rounded border-[1.5px] transition-colors",
                  ok ? "border-brand-green bg-brand-green/[0.04]" : "border-amber bg-amber/[0.04]",
                  isActive ? "ring-2 ring-brand-blue ring-offset-1" : "",
                ].join(" ")}
                style={{ top: z.top, height: z.height }}
                onMouseEnter={() => onZoneHover?.(z.id)}
                onMouseLeave={() => onZoneHover?.(null)}
              />
              {/* Inline zone label at top-right of the zone */}
              <div
                className={[
                  "absolute z-base inline-flex items-center gap-1.5 px-1.5 py-0.5 rounded-sm bg-white shadow-card",
                  "border",
                  ok ? "border-brand-green" : "border-amber",
                ].join(" ")}
                style={{ right: 4, top: z.top - 9 }}
              >
                <span className="text-[9px] font-bold uppercase tracking-[0.04em] text-ink">{z.label}</span>
                <span className="text-[8.5px] text-ink-faint">· {z.fields}</span>
                <span className={["text-[9px] font-bold font-mono", ok ? "text-brand-green-deep" : "text-amber"].join(" ")}>
                  {z.confidence}%
                </span>
              </div>
            </React.Fragment>
          );
        })}
      </div>
    </div>
  );
}
