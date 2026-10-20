"use client";

import { AlertTriangle, CheckCircle2, Info, X, XCircle } from "lucide-react";
import { useTranslations } from "next-intl";
import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from "react";
import { cn } from "@/lib/utils";
import { toneClasses } from "./badge";

type ToastTone = "info" | "success" | "warning" | "danger";

export interface ToastInput {
  tone?: ToastTone;
  title: ReactNode;
  description?: ReactNode;
  action?: ReactNode;
  /** ms; 0 keeps it until dismissed */
  duration?: number;
}

interface ToastItem extends ToastInput {
  id: number;
}

const ToastContext = createContext<((t: ToastInput) => void) | null>(null);

const icons: Record<ToastTone, ReactNode> = {
  info: <Info className="size-5" />,
  success: <CheckCircle2 className="size-5" />,
  warning: <AlertTriangle className="size-5" />,
  danger: <XCircle className="size-5" />,
};

/** Toast notifications in a polite live region (errors are announced assertively). */
export function ToastProvider({ children }: { children: ReactNode }) {
  const t = useTranslations("common");
  const [items, setItems] = useState<ToastItem[]>([]);
  const nextId = useRef(1);

  const dismiss = useCallback((id: number) => setItems((list) => list.filter((x) => x.id !== id)), []);

  const push = useCallback(
    (input: ToastInput) => {
      const id = nextId.current++;
      setItems((list) => [...list.slice(-3), { ...input, id }]);
      const duration = input.duration ?? (input.tone === "danger" ? 8000 : 5000);
      if (duration > 0) setTimeout(() => dismiss(id), duration);
    },
    [dismiss],
  );

  const value = useMemo(() => push, [push]);

  return (
    <ToastContext.Provider value={value}>
      {children}
      <div
        className="pointer-events-none fixed inset-x-4 bottom-4 z-50 flex flex-col items-stretch gap-2 sm:inset-x-auto sm:end-4 sm:w-96 print:hidden"
        aria-live="polite"
        aria-relevant="additions"
      >
        {items.map((item) => {
          const tone = item.tone ?? "info";
          return (
            <div
              key={item.id}
              role={tone === "danger" ? "alert" : "status"}
              className={cn(
                "pointer-events-auto flex gap-3 rounded-card border border-border px-4 py-3 text-sm shadow-floating",
                toneClasses(tone),
              )}
            >
              <span className="mt-0.5 shrink-0" aria-hidden="true">
                {icons[tone]}
              </span>
              <div className="min-w-0 flex-1">
                <p className="font-semibold">{item.title}</p>
                {item.description ? <p className="mt-0.5">{item.description}</p> : null}
                {item.action ? <div className="mt-2">{item.action}</div> : null}
              </div>
              <button
                type="button"
                onClick={() => dismiss(item.id)}
                className="-me-1 -mt-1 flex size-7 shrink-0 items-center justify-center rounded-control hover:bg-black/5"
                aria-label={t("dismiss")}
              >
                <X className="size-4" aria-hidden="true" />
              </button>
            </div>
          );
        })}
      </div>
    </ToastContext.Provider>
  );
}

export function useToast() {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error("useToast must be used inside <ToastProvider>");
  return ctx;
}
