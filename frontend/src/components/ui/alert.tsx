import { AlertTriangle, CheckCircle2, Info, XCircle } from "lucide-react";
import type { ReactNode } from "react";
import { cn } from "@/lib/utils";
import { toneClasses } from "./badge";

type AlertTone = "info" | "success" | "warning" | "danger";

const icons: Record<AlertTone, ReactNode> = {
  info: <Info className="size-5" />,
  success: <CheckCircle2 className="size-5" />,
  warning: <AlertTriangle className="size-5" />,
  danger: <XCircle className="size-5" />,
};

/** Inline message. Errors use role="alert" (announced immediately); others are polite status messages. */
export function Alert({
  tone = "info",
  title,
  children,
  action,
  className,
}: {
  tone?: AlertTone;
  title?: ReactNode;
  children?: ReactNode;
  action?: ReactNode;
  className?: string;
}) {
  return (
    <div
      role={tone === "danger" ? "alert" : "status"}
      className={cn("flex gap-3 rounded-card px-4 py-3 text-sm", toneClasses(tone), className)}
    >
      <span className="mt-0.5 shrink-0" aria-hidden="true">
        {icons[tone]}
      </span>
      <div className="min-w-0 flex-1">
        {title ? <p className="font-semibold">{title}</p> : null}
        {children ? <div className={cn(title && "mt-0.5")}>{children}</div> : null}
        {action ? <div className="mt-2 flex flex-wrap gap-2">{action}</div> : null}
      </div>
    </div>
  );
}
