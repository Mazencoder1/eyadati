"use client";

import { UserRound, X } from "lucide-react";
import { useTranslations } from "next-intl";
import { useId, useState } from "react";
import { SearchBox } from "@/components/ui/data-table";
import { Spinner } from "@/components/ui/spinner";
import type { PatientListItem } from "@/lib/api/types";
import { useApi } from "@/lib/auth/session";
import { useQuery } from "@/lib/hooks/use-query";
import { toLocalMobile } from "@/lib/validation/saudi";

export interface PickedPatient {
  id: string;
  fullName: string;
  medicalRecordNumber: string;
  mobileNumber: string;
}

/**
 * Patient search for booking: debounced server search (name prefix, mobile, MRN or ID number) with results as a
 * radio group — keyboard and screen-reader friendly without a custom combobox.
 */
export function PatientPicker({
  value,
  onChange,
  error,
}: {
  value: PickedPatient | null;
  onChange: (patient: PickedPatient | null) => void;
  error?: string;
}) {
  const t = useTranslations("patients.picker");
  const api = useApi();
  const groupId = useId();
  const [search, setSearch] = useState("");
  const results = useQuery<PatientListItem[]>(search.length >= 2 ? `patient-search:${search}` : null, async (signal) =>
    (await api.patients.list({ search, pageSize: 8 }, signal)).items,
  );

  if (value) {
    return (
      <div className="flex flex-col gap-1.5">
        <span className="text-sm font-medium">{t("label")}</span>
        <div className="flex items-center gap-3 rounded-control border border-border-input bg-primary-soft/40 px-3 py-2">
          <UserRound className="size-5 shrink-0 text-primary" aria-hidden="true" />
          <div className="min-w-0 flex-1">
            <p className="truncate text-sm font-semibold">{value.fullName}</p>
            <p className="text-xs text-fg-muted">
              <span className="ltr-nums">{value.medicalRecordNumber}</span>
              {value.mobileNumber ? (
                <>
                  {" "}·{" "}
                  <span className="ltr-nums">{toLocalMobile(value.mobileNumber)}</span>
                </>
              ) : null}
            </p>
          </div>
          <button
            type="button"
            onClick={() => onChange(null)}
            className="flex size-8 items-center justify-center rounded-control text-fg-muted hover:bg-surface-muted"
            aria-label={t("change")}
          >
            <X className="size-4" aria-hidden="true" />
          </button>
        </div>
      </div>
    );
  }

  return (
    <fieldset className="flex min-w-0 flex-col gap-2" aria-describedby={error ? `${groupId}-error` : undefined}>
      <legend className="mb-1.5 text-sm font-medium">
        {t("label")} <span className="text-danger" aria-hidden="true">*</span>
      </legend>
      <SearchBox initial="" onChange={setSearch} placeholder={t("searchPlaceholder")} className="sm:max-w-none" />
      {error ? (
        <p id={`${groupId}-error`} className="text-xs font-medium text-danger-strong">
          {error}
        </p>
      ) : null}
      <div aria-live="polite" className="min-h-6">
        {search.length < 2 ? (
          <p className="text-xs text-fg-subtle">{t("hint")}</p>
        ) : results.loading ? (
          <p className="flex items-center gap-2 text-sm text-fg-muted">
            <Spinner className="size-4" /> {t("searching")}
          </p>
        ) : results.data && results.data.length === 0 ? (
          <p className="text-sm text-fg-muted">{t("noResults")}</p>
        ) : null}
      </div>
      {results.data && results.data.length > 0 && search.length >= 2 ? (
        <ul className="flex max-h-60 flex-col gap-1 overflow-y-auto">
          {results.data.map((p) => (
            <li key={p.id}>
              <label className="flex cursor-pointer items-center gap-3 rounded-control border border-border px-3 py-2 hover:bg-surface-muted has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-focus">
                <input
                  type="radio"
                  name={groupId}
                  className="sr-only"
                  onChange={() =>
                    onChange({ id: p.id, fullName: p.fullName, medicalRecordNumber: p.medicalRecordNumber, mobileNumber: p.mobileNumber })
                  }
                />
                <UserRound className="size-4 shrink-0 text-fg-subtle" aria-hidden="true" />
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-sm font-medium">{p.fullName}</span>
                  <span className="block text-xs text-fg-muted">
                    <span className="ltr-nums">{p.medicalRecordNumber}</span> ·{" "}
                    <span className="ltr-nums">{toLocalMobile(p.mobileNumber)}</span>
                  </span>
                </span>
              </label>
            </li>
          ))}
        </ul>
      ) : null}
    </fieldset>
  );
}
