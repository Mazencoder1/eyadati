"use client";

import { Plus, Trash2 } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";
import { useMemo, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { useToast } from "@/components/ui/toast";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError, type ApiError } from "@/lib/api/client";
import type { DayOfWeek, Doctor, ScheduleWindow } from "@/lib/api/types";
import { assignableBranches, isBranchRestricted } from "@/lib/auth/branch-scope";
import { useApi } from "@/lib/auth/session";
import { trimSeconds } from "@/lib/datetime";
import { ConflictAlert } from "./doctor-form";

/** Saudi working week starts on Sunday. */
export const DAYS: DayOfWeek[] = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

interface Row {
  key: number;
  branchId: string;
  dayOfWeek: DayOfWeek;
  start: string; // HH:mm
  end: string;
}

export function weekdayNames(locale: string): Record<DayOfWeek, string> {
  // 2026-01-04 is a Sunday; weekday names don't depend on the calendar system.
  const fmt = new Intl.DateTimeFormat(`${locale}-u-ca-gregory`, { weekday: "long", timeZone: "UTC" });
  return Object.fromEntries(DAYS.map((d, i) => [d, fmt.format(new Date(Date.UTC(2026, 0, 4 + i, 12)))])) as Record<DayOfWeek, string>;
}

/** Same-day overlap check (mirrors DoctorService.ValidateWindowsAsync; across branches too). */
export function findScheduleProblems(rows: { dayOfWeek: DayOfWeek; start: string; end: string }[]): {
  invalid: Set<number>;
  overlapDays: Set<DayOfWeek>;
} {
  const invalid = new Set<number>();
  const overlapDays = new Set<DayOfWeek>();
  rows.forEach((r, i) => {
    if (!r.start || !r.end || r.end <= r.start) invalid.add(i);
  });
  for (const day of DAYS) {
    const sorted = rows.filter((r) => r.dayOfWeek === day && r.start && r.end).sort((a, b) => a.start.localeCompare(b.start));
    for (let i = 1; i < sorted.length; i++) if (sorted[i].start < sorted[i - 1].end) overlapDays.add(day);
  }
  return { invalid, overlapDays };
}

/**
 * Weekly working hours: several periods per day (e.g. 09:00–13:00 and 16:00–21:00), each tied to a branch.
 * Times are clinic-local wall-clock times.
 */
export function ScheduleEditor({ doctor, onSaved, onReload }: { doctor: Doctor; onSaved: (d: Doctor) => void; onReload: () => void }) {
  const t = useTranslations("doctors.schedule");
  const tc = useTranslations("common");
  const locale = useLocale();
  const api = useApi();
  const toast = useToast();
  const { branches, branchScope } = useClinic();
  // Branch-restricted members only see (and may only send) windows in their own branches; the server keeps the
  // windows of other branches untouched (T-011), so they are never sent back from here.
  const activeBranches = assignableBranches(branches, branchScope);
  const restricted = isBranchRestricted(branchScope);
  const names = useMemo(() => weekdayNames(locale), [locale]);
  const [nextKey, setNextKey] = useState(doctor.schedule.length + 1);
  const [rows, setRows] = useState<Row[]>(() =>
    doctor.schedule.map((w, i) => ({
      key: i + 1,
      branchId: w.branchId,
      dayOfWeek: w.dayOfWeek,
      start: trimSeconds(w.startTime),
      end: trimSeconds(w.endTime),
    })),
  );
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [showProblems, setShowProblems] = useState(false);
  const problems = findScheduleProblems(rows);
  const hasProblems = problems.invalid.size > 0 || problems.overlapDays.size > 0;

  const update = (key: number, patch: Partial<Row>) => setRows((list) => list.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const add = (day: DayOfWeek) => {
    const last = rows.filter((r) => r.dayOfWeek === day).at(-1);
    setRows((list) => [
      ...list,
      {
        key: nextKey,
        branchId: last?.branchId ?? activeBranches[0]?.id ?? "",
        dayOfWeek: day,
        start: last ? last.end : "09:00",
        end: last ? "21:00" : "13:00",
      },
    ]);
    setNextKey((k) => k + 1);
  };

  const save = async () => {
    setShowProblems(true);
    if (hasProblems) return;
    setSaving(true);
    setError(null);
    try {
      const windows = toScheduleWindows(rows);
      const saved = await api.doctors.updateSchedule(doctor.id, windows, doctor.rowVersion);
      toast({ tone: "success", title: t("saved") });
      onSaved(saved);
    } catch (e) {
      if (!isApiError(e)) throw e;
      setError(e);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="flex flex-col gap-4">
      <p className="text-sm text-fg-muted">{t("description")}</p>
      {restricted ? <Alert tone="info">{t("otherBranchesKept")}</Alert> : null}
      {error?.isConcurrencyConflict ? <ConflictAlert onReload={onReload} /> : error ? <Alert tone="danger">{error.message}</Alert> : null}
      {showProblems && hasProblems ? <Alert tone="danger">{t("invalid")}</Alert> : null}

      <ul className="flex flex-col divide-y divide-border rounded-card border border-border">
        {DAYS.map((day) => {
          const dayRows = rows.filter((r) => r.dayOfWeek === day);
          const overlap = showProblems && problems.overlapDays.has(day);
          return (
            <li key={day} className="flex flex-col gap-3 p-4 md:flex-row md:items-start">
              <div className="flex items-center justify-between md:w-36 md:shrink-0 md:pt-2">
                <span className="font-semibold">{names[day]}</span>
                {dayRows.length === 0 ? <span className="text-xs text-fg-subtle md:hidden">{t("dayOff")}</span> : null}
              </div>
              <div className="flex min-w-0 flex-1 flex-col gap-2">
                {dayRows.length === 0 ? <span className="hidden pt-2 text-sm text-fg-subtle md:block">{t("dayOff")}</span> : null}
                {dayRows.map((r) => {
                  const invalid = showProblems && problems.invalid.has(rows.indexOf(r));
                  return (
                    <div key={r.key} className="flex flex-wrap items-end gap-2">
                      <TimeField label={t("from")} value={r.start} invalid={invalid || overlap} onChange={(v) => update(r.key, { start: v })} />
                      <TimeField label={t("to")} value={r.end} invalid={invalid || overlap} onChange={(v) => update(r.key, { end: v })} />
                      {activeBranches.length > 1 ? (
                        <label className="flex min-w-40 flex-1 flex-col gap-1 text-xs text-fg-muted">
                          {t("branch")}
                          <select
                            value={r.branchId}
                            onChange={(e) => update(r.key, { branchId: e.target.value })}
                            className="h-10 rounded-control border border-border-input bg-surface px-2 text-sm text-fg"
                          >
                            {branchChoices(activeBranches, branches, r.branchId).map((b) => (
                              <option key={b.id} value={b.id}>
                                {b.name}
                              </option>
                            ))}
                          </select>
                        </label>
                      ) : null}
                      <Button
                        variant="ghost"
                        size="icon"
                        onClick={() => setRows((list) => list.filter((x) => x.key !== r.key))}
                        aria-label={t("remove", { day: names[day], from: r.start, to: r.end })}
                      >
                        <Trash2 className="size-4" aria-hidden="true" />
                      </Button>
                    </div>
                  );
                })}
                {overlap ? <p className="text-xs font-medium text-danger-strong">{t("overlap")}</p> : null}
                <div>
                  <Button variant="ghost" size="sm" icon={<Plus className="size-4" aria-hidden="true" />} onClick={() => add(day)}>
                    {t("addPeriod")}
                  </Button>
                </div>
              </div>
            </li>
          );
        })}
      </ul>
      <div className="flex justify-end gap-2">
        <Button variant="outline" onClick={onReload} disabled={saving}>
          {tc("cancel")}
        </Button>
        <Button onClick={() => void save()} loading={saving} disabled={activeBranches.length === 0}>
          {t("save")}
        </Button>
      </div>
    </div>
  );
}

function TimeField({
  label,
  value,
  invalid,
  onChange,
}: {
  label: string;
  value: string;
  invalid?: boolean;
  onChange: (v: string) => void;
}) {
  return (
    <label className="flex flex-col gap-1 text-xs text-fg-muted">
      {label}
      <input
        type="time"
        step={300}
        value={value}
        aria-invalid={invalid || undefined}
        onChange={(e) => onChange(e.target.value)}
        className="ltr-nums h-10 w-32 rounded-control border border-border-input bg-surface px-2 text-sm text-fg aria-[invalid=true]:border-danger"
      />
    </label>
  );
}

/** The PUT /doctors/{id}/schedule payload: exactly the rows in the editor (which came from the server's scoped view). */
export function toScheduleWindows(rows: { branchId: string; dayOfWeek: DayOfWeek; start: string; end: string }[]): ScheduleWindow[] {
  return rows.map((r) => ({ branchId: r.branchId, dayOfWeek: r.dayOfWeek, startTime: `${r.start}:00`, endTime: `${r.end}:00` }));
}

/** Branch options for one period: the assignable branches, plus the period's current branch if it is not one of them. */
export function branchChoices<B extends { id: string }>(assignable: B[], all: B[], current: string): B[] {
  if (!current || assignable.some((b) => b.id === current)) return assignable;
  const own = all.find((b) => b.id === current);
  return own ? [own, ...assignable] : assignable;
}
