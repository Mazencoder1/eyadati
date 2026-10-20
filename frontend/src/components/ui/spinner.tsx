import { cn } from "@/lib/utils";

/** Decorative spinner; pair it with visible text or aria-busy on the owning element. */
export function Spinner({ className }: { className?: string }) {
  return (
    <svg className={cn("size-5 animate-spin", className)} viewBox="0 0 24 24" fill="none" aria-hidden="true">
      <circle cx="12" cy="12" r="9" stroke="currentColor" strokeOpacity="0.25" strokeWidth="3" />
      <path d="M21 12a9 9 0 0 0-9-9" stroke="currentColor" strokeWidth="3" strokeLinecap="round" />
    </svg>
  );
}

export function Skeleton({ className }: { className?: string }) {
  return <div className={cn("animate-pulse rounded-control bg-surface-muted", className)} aria-hidden="true" />;
}

/** Full-area loading indicator with an accessible label. */
export function LoadingBlock({ label, className }: { label: string; className?: string }) {
  return (
    <div role="status" className={cn("flex items-center justify-center gap-3 py-16 text-fg-muted", className)}>
      <Spinner />
      <span>{label}</span>
    </div>
  );
}
