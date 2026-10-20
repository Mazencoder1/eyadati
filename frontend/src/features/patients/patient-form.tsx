"use client";

import { RefreshCw } from "lucide-react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { useMemo, useState } from "react";
import { Alert } from "@/components/ui/alert";
import { Button, ButtonLink } from "@/components/ui/button";
import { Card, CardBody, CardFooter } from "@/components/ui/card";
import { Input, Select, Textarea } from "@/components/ui/field";
import { useClinic } from "@/features/clinic/clinic-context";
import type { DuplicateCandidate, Gender, IdentityDocumentType, PatientDetails, SavePatientRequest } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { todayInZone } from "@/lib/datetime";
import { useForm } from "@/lib/hooks/use-form";
import { emptyToNull } from "@/lib/utils";
import { check, normalizeSaudiMobile, rules, toLatinDigits, toLocalMobile } from "@/lib/validation/saudi";

const IDENTITY_TYPES: IdentityDocumentType[] = ["None", "NationalId", "Iqama", "Passport", "GccId"];
/** Common nationalities first; names come from Intl.DisplayNames (localized, nothing hard-coded). */
const NATIONALITIES = ["SA", "EG", "YE", "SY", "JO", "SD", "PK", "IN", "BD", "PH", "ID", "AE", "KW", "BH", "QA", "OM", "IQ", "LB", "PS", "ET", "NP", "LK", "US", "GB"];

export type PatientFormValues = {
  fullName: string;
  fullNameEn: string;
  mobileNumber: string;
  email: string;
  gender: string;
  dateOfBirth: string;
  identityType: string;
  identityNumber: string;
  nationality: string;
  notes: string;
  registeredBranchId: string;
};

export function toFormValues(p?: PatientDetails): PatientFormValues {
  return {
    fullName: p?.fullName ?? "",
    fullNameEn: p?.fullNameEn ?? "",
    mobileNumber: toLocalMobile(p?.mobileNumber),
    email: p?.email ?? "",
    gender: p?.gender ?? "",
    dateOfBirth: p?.dateOfBirth ?? "",
    identityType: p?.identityType ?? "None",
    identityNumber: p?.identityNumber ?? "",
    nationality: p?.nationality ?? "SA",
    notes: p?.notes ?? "",
    registeredBranchId: "",
  };
}

function toRequest(v: PatientFormValues): SavePatientRequest {
  const identityType = v.identityType as IdentityDocumentType;
  return {
    fullName: v.fullName.trim(),
    fullNameEn: emptyToNull(v.fullNameEn),
    mobileNumber: v.mobileNumber.trim(),
    email: emptyToNull(v.email),
    gender: v.gender as Gender,
    dateOfBirth: emptyToNull(v.dateOfBirth),
    identityType,
    identityNumber: identityType === "None" ? null : emptyToNull(toLatinDigits(v.identityNumber)),
    nationality: emptyToNull(v.nationality),
    notes: emptyToNull(v.notes),
    registeredBranchId: emptyToNull(v.registeredBranchId),
  };
}

/**
 * Create/edit form for patient demographics (clinical fields are a separate, permissioned form).
 * On create it warns about possible duplicates (same mobile or ID) without blocking — families often share a mobile.
 * On edit it sends the rowVersion it loaded; a 409 CONCURRENCY_CONFLICT offers to reload the latest version.
 */
export function PatientForm({
  patient,
  onSaved,
  onReload,
}: {
  patient?: PatientDetails;
  onSaved: (saved: PatientDetails) => void;
  onReload?: () => void;
}) {
  const t = useTranslations("patients");
  const tc = useTranslations("common");
  const locale = useLocale();
  const { api, can } = useAuth();
  const { timeZone, allowedBranches } = useClinic();
  const form = useForm<PatientFormValues>(toFormValues(patient));
  const [duplicates, setDuplicates] = useState<DuplicateCandidate[]>([]);
  const isEdit = !!patient;
  const today = todayInZone(timeZone);

  const regionNames = useMemo(() => {
    try {
      return new Intl.DisplayNames([locale], { type: "region" });
    } catch {
      return null;
    }
  }, [locale]);

  const checkDuplicates = async () => {
    if (isEdit || !can(P.Patients.Create)) return;
    const mobile = normalizeSaudiMobile(form.values.mobileNumber) ?? undefined;
    const id = form.values.identityType !== "None" ? toLatinDigits(form.values.identityNumber.trim()) || undefined : undefined;
    if (!mobile && !id) return;
    try {
      setDuplicates(await api.patients.duplicates(mobile, id));
    } catch {
      setDuplicates([]);
    }
  };

  const onSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    void form.submit(
      (v) => ({
        fullName: check(v.fullName, rules.required(), rules.minLength(2), rules.maxLength(200)),
        fullNameEn: check(v.fullNameEn, rules.maxLength(200)),
        mobileNumber: check(v.mobileNumber, rules.required(), rules.saudiMobile()),
        email: check(v.email, rules.email()),
        gender: check(v.gender, rules.required()),
        dateOfBirth: v.dateOfBirth && (v.dateOfBirth > today || v.dateOfBirth < "1900-01-01") ? "invalid_date" : null,
        identityNumber:
          v.identityType === "None"
            ? null
            : v.identityType === "NationalId" || v.identityType === "Iqama"
              ? check(v.identityNumber, rules.required(), rules.nationalId())
              : check(v.identityNumber, rules.required(), rules.maxLength(20)),
        notes: check(v.notes, rules.maxLength(1000)),
      }),
      async (v) => {
        const body = toRequest(v);
        const saved = isEdit
          ? await api.patients.update(patient.id, { ...body, rowVersion: patient.rowVersion })
          : await api.patients.create(body);
        onSaved(saved);
      },
    );
  };

  const conflict = form.formError?.isConcurrencyConflict;

  return (
    <form onSubmit={onSubmit} noValidate>
      <Card>
        <CardBody className="flex flex-col gap-5">
          {conflict ? (
            <Alert
              tone="warning"
              title={tc("conflictTitle")}
              action={
                onReload ? (
                  <Button size="sm" variant="outline" icon={<RefreshCw className="size-4" aria-hidden="true" />} onClick={onReload}>
                    {tc("reload")}
                  </Button>
                ) : undefined
              }
            >
              {tc("conflictBody")}
            </Alert>
          ) : form.formError && !form.formError.isUpgradeRequired ? (
            <Alert tone="danger">{form.formError.message}</Alert>
          ) : null}

          {duplicates.length > 0 ? (
            <Alert tone="warning" title={t("duplicates.title")}>
              <p>{t("duplicates.body")}</p>
              <ul className="mt-2 flex flex-col gap-1">
                {duplicates.map((d) => (
                  <li key={d.id}>
                    <Link href={`/patients/${d.id}`} className="font-medium underline">
                      {d.fullName}
                    </Link>{" "}
                    — <span className="ltr-nums">{d.medicalRecordNumber}</span> ·{" "}
                    <span className="ltr-nums">{toLocalMobile(d.mobileNumber)}</span>
                  </li>
                ))}
              </ul>
            </Alert>
          ) : null}

          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
            <Input label={t("fields.fullName")} required autoComplete="off" {...form.bind("fullName")} />
            <Input label={t("fields.fullNameEn")} dir="ltr" autoComplete="off" {...form.bind("fullNameEn")} />
            <Input
              label={t("fields.mobile")}
              required
              type="tel"
              inputMode="tel"
              dir="ltr"
              placeholder="05XXXXXXXX"
              {...form.bind("mobileNumber")}
              onBlur={() => void checkDuplicates()}
            />
            <Select
              label={t("fields.gender")}
              required
              placeholder={t("chooseGender")}
              options={(["Male", "Female"] as const).map((g) => ({ value: g, label: t(`gender.${g}`) }))}
              {...form.bind("gender")}
            />
            <Input label={t("fields.dateOfBirth")} type="date" max={today} {...form.bind("dateOfBirth")} />
            <Input label={t("fields.email")} type="email" dir="ltr" inputMode="email" {...form.bind("email")} />
            <Select
              label={t("fields.identityType")}
              options={IDENTITY_TYPES.map((it) => ({ value: it, label: t(`identityTypes.${it}`) }))}
              {...form.bind("identityType")}
            />
            {form.values.identityType !== "None" ? (
              <Input
                label={t("fields.identityNumber")}
                required
                dir="ltr"
                inputMode={form.values.identityType === "Passport" ? "text" : "numeric"}
                maxLength={20}
                hint={form.values.identityType === "NationalId" || form.values.identityType === "Iqama" ? t("identityHint") : undefined}
                {...form.bind("identityNumber")}
                onBlur={() => void checkDuplicates()}
              />
            ) : null}
            <Select
              label={t("fields.nationality")}
              placeholder={t("unspecified")}
              options={NATIONALITIES.map((code) => ({ value: code, label: regionNames?.of(code) ?? code }))}
              {...form.bind("nationality")}
            />
            {!isEdit && allowedBranches.length > 1 ? (
              <Select
                label={t("fields.branch")}
                placeholder={t("unspecified")}
                options={allowedBranches.map((b) => ({ value: b.id, label: b.name }))}
                {...form.bind("registeredBranchId")}
              />
            ) : null}
          </div>
          <Textarea label={t("fields.notes")} hint={t("notesHint")} maxLength={1000} {...form.bind("notes")} />
        </CardBody>
        <CardFooter>
          <ButtonLink href={isEdit ? `/patients/${patient.id}` : "/patients"} variant="outline">
            {tc("cancel")}
          </ButtonLink>
          <Button type="submit" loading={form.submitting} disabled={conflict}>
            {isEdit ? tc("saveChanges") : t("create")}
          </Button>
        </CardFooter>
      </Card>
    </form>
  );
}
