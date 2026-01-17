import { cn } from "@/lib/utils";
import type { AutomationStatus } from "@/types/procurement";
import { CheckCircle2, AlertTriangle } from "lucide-react";

interface StatusBadgeProps {
  status: AutomationStatus;
  showIcon?: boolean;
  size?: "sm" | "md" | "lg";
  className?: string;
}

export function StatusBadge({ status, showIcon = true, size = "md", className }: StatusBadgeProps) {
  const isAutomatable = status === "Automatable";
  
  const sizeClasses = {
    sm: "text-xs px-2 py-0.5",
    md: "text-sm px-2.5 py-1",
    lg: "text-sm px-3 py-1.5",
  };
  
  const iconSizes = {
    sm: "h-3 w-3",
    md: "h-3.5 w-3.5",
    lg: "h-4 w-4",
  };

  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full font-medium border",
        sizeClasses[size],
        isAutomatable 
          ? "bg-success-muted text-success border-success/20" 
          : "bg-warning-muted text-warning border-warning/30",
        className
      )}
    >
      {showIcon && (
        isAutomatable 
          ? <CheckCircle2 className={iconSizes[size]} />
          : <AlertTriangle className={iconSizes[size]} />
      )}
      {isAutomatable ? "Automatable" : "Needs Clarification"}
    </span>
  );
}
