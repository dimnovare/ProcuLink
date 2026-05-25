"use client";
import * as React from "react";

/**
 * <StatusJourney>
 *
 * The 5-node mini-track that replaces a static status pill.
 * Stages: Parse · Normalize · Validate · Transform · Deliver.
 *
 * `stage` (0..4) is the CURRENT stage; nodes before it are filled green
 * (completed), the current node is filled blue (or red if `failed`), and
 * future nodes are unfilled.
 */

const STAGES = ["Parse", "Normalize", "Validate", "Transform", "Deliver"] as const;

type StatusJourneyProps = {
  stage: 0 | 1 | 2 | 3 | 4;
  /** Tiny variant for inbox rows */
  compact?: boolean;
  /** Mark current stage as failed (danger color, X mark) */
  failed?: boolean;
  className?: string;
};

export function StatusJourney({
  stage,
  compact,
  failed,
  className,
}: StatusJourneyProps) {
  const sz = compact ? 14 : 18;
  const gap = compact ? 14 : 22;

  return (
    <div className={["inline-flex items-center", className].filter(Boolean).join(" ")} style={{ gap: 0 }}>
      {STAGES.map((label, i) => {
        const done = i < stage;
        const active = i === stage && !failed;
        const isFailed = i === stage && failed;
        const future = i > stage;

        const bg =
          done    ? "bg-brand-green" :
          active  ? "bg-brand-blue" :
          isFailed? "bg-danger" :
                    "bg-surface";

        const border =
          done    ? "border-brand-green" :
          active  ? "border-brand-blue" :
          isFailed? "border-danger" :
                    "border-border-strong";

        const ring = active
          ? `0 0 0 ${compact ? 3 : 4}px rgba(30,102,201,0.18)`
          : "none";

        return (
          <React.Fragment key={label}>
            <div
              title={label}
              data-active={active}
              data-failed={isFailed}
              className={["rounded-full border-[1.5px] transition-all", bg, border].join(" ")}
              style={{
                width: sz,
                height: sz,
                boxShadow: ring,
                animation: active ? "node-pulse 2s ease-out" : "none",
              }}
            />
            {i < STAGES.length - 1 && (
              <div
                aria-hidden
                className={done ? "bg-brand-green" : "bg-border"}
                style={{
                  flex: `0 0 ${gap - sz}px`,
                  height: 1.5,
                  marginLeft: 0,
                  marginRight: 0,
                  alignSelf: "center",
                }}
              />
            )}
          </React.Fragment>
        );
      })}
    </div>
  );
}
