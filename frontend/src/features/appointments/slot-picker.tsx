"use client";

import { CalendarX2 } from "lucide-react";
import { useTranslations } from "next-intl";
import { useId, type ReactNode } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/spinner";
import { isApiError } from "@/lib/api/client";
import type { AvailableSlot } from "@/lib/api/types";
import { cn } from "@/lib/utils";

/**
 * Radio group of free slots exactly as GET /doctors/{id}/availability returned them (computed by the server in
 * clinic time). Shared by booking and rescheduling; it never computes availability itself.
 */
export function SlotPicker({
  slots,
  selected,
  onSelect,
  error,
  placeholder,
  branchLabel,
}: {
  slots: { data?: AvailableSlot[]; loading: boolean; error?: unknown; reload: () => void };
  selected: AvailableSlot | null;
  onSelect: (slot: AvailableSlot) => void;
  error?: string | null;
  /** Shown instead of the slots while their prerequisites are missing (e.g. no doctor chosen yet). */
  placeholder?: ReactNode;
  /** Optional second line per slot (the branch, when slots of several branches are listed). */
  branchLabel?: (slot: AvailableSlot) => string | null;
}) {
  const t = useTranslations("appointments.booking");
  const groupId = useId();
  const list = slots.data ?? [];

  return (
    <fieldset aria-describedby={error ? `${groupId}-error` : undefined}>
      <legend className="mb-2 text-sm font-medium">
        {t("slots")} <span className="text-danger" aria-hidden="true">*</span>
      </legend>
      {error ? (
        <p id={`${groupId}-error`} className="mb-2 text-xs font-medium text-danger-strong">
          {error}
        </p>
      ) : null}
      {placeholder ? (
        <p className="text-sm text-fg-subtle">{placeholder}</p>
      ) : slots.loading ? (
        <div className="grid grid-cols-3 gap-2 sm:grid-cols-6" aria-busy="true">
          {Array.from({ length: 6 }, (_, i) => (
            <Skeleton key={i} className="h-10" />
          ))}
        </div>
      ) : slots.error ? (
        <Alert tone="danger" action={<Button size="sm" variant="outline" onClick={slots.reload}>{t("retry")}</Button>}>
          {isApiError(slots.error) ? slots.error.message : t("slotsFailed")}
        </Alert>
      ) : list.length === 0 ? (
        <p className="flex items-center gap-2 rounded-control bg-surface-muted px-3 py-3 text-sm text-fg-muted">
          <CalendarX2 className="size-4 shrink-0" aria-hidden="true" />
          {t("noSlots")}
        </p>
      ) : (
        <div className="grid grid-cols-3 gap-2 sm:grid-cols-5 lg:grid-cols-6">
          {list.map((s) => {
            const isSelected = selected?.startUtc === s.startUtc && selected.branchId === s.branchId;
            const branch = branchLabel?.(s);
            return (
              <label
                key={`${s.startUtc}-${s.branchId}`}
                className={cn(
                  "flex cursor-pointer flex-col items-center justify-center rounded-control border px-2 py-2 text-sm transition-colors",
                  "has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-focus",
                  isSelected
                    ? "border-primary bg-primary text-primary-fg"
                    : "border-border-strong bg-surface hover:border-primary hover:bg-primary-soft",
                )}
              >
                <input
                  type="radio"
                  name={groupId}
                  className="sr-only"
                  checked={isSelected}
                  onChange={() => onSelect(s)}
                />
                <span className="ltr-nums font-semibold">{s.localTime}</span>
                {branch ? (
                  <span className={cn("truncate text-[11px]", isSelected ? "text-primary-fg" : "text-fg-subtle")}>{branch}</span>
                ) : null}
              </label>
            );
          })}
        </div>
      )}
    </fieldset>
  );
}
