"use client";

import { CheckCheck, FileText, Lock, Plus } from "lucide-react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { useState, type ReactNode } from "react";
import { PageHeader, RequirePermission } from "@/components/app/page";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, ButtonLink } from "@/components/ui/button";
import { Card, CardBody, CardFooter, CardHeader } from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/dialog";
import { Input, Textarea } from "@/components/ui/field";
import { LoadingBlock } from "@/components/ui/spinner";
import { useToast } from "@/components/ui/toast";
import { useClinic } from "@/features/clinic/clinic-context";
import { PrescriptionDialog } from "@/features/prescriptions/prescription-dialog";
import { isApiError } from "@/lib/api/client";
import type { PatientDetails, Visit, VisitListItem } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { ageInYears, formatDateTime, formatGregorianDate, formatShortDate, isoDateInZone, todayInZone } from "@/lib/datetime";
import { useForm } from "@/lib/hooks/use-form";
import { useQuery } from "@/lib/hooks/use-query";
import { cn } from "@/lib/utils";
import type { ValidationCode } from "@/lib/validation/saudi";
import {
  isDirty,
  mapVisitServerErrors,
  TEXT_LIMITS,
  toFormValues,
  toUpdateRequest,
  validateVisit,
  VITAL_KEYS,
  VITAL_RANGES,
  type VisitFormValues,
} from "./visit-model";

/**
 * Free text follows its own script (Arabic notes in the English UI and vice versa), but an empty field keeps the
 * page direction — `dir="auto"` alone would make an empty field left-to-right in the Arabic UI.
 */
const textDir = (value: string) => (value.trim() ? "auto" : undefined);

/** GD-13: visit sections are separated by a thin rule and a title, not a full header band. */
const SECTION_HEADER = "border-b-0 pb-0";

/**
 * Read-only view of a visit (completed, or opened by someone other than its doctor — GD-01/GD-13): the recorded
 * values as text, never empty inputs that look editable. A section with nothing recorded says so once.
 */
function ClinicalSummary({ values }: { values: VisitFormValues }) {
  const t = useTranslations("visits");
  const locale = useLocale();
  const vitals = VITAL_KEYS.filter((key) => values[key].trim());
  const entry = (key: string, label: string, value: ReactNode, className?: string) => (
    <div key={key} className={className}>
      <dt className="text-sm text-fg-muted">{label}</dt>
      <dd className="mt-0.5 whitespace-pre-wrap break-words text-fg">{value}</dd>
    </div>
  );
  const text = (key: "symptoms" | "clinicalNotes" | "diagnosis" | "treatmentPlan" | "followUpNotes", wide = false) =>
    values[key].trim() ? entry(key, t(`fields.${key}`), <span dir="auto">{values[key]}</span>, wide ? "sm:col-span-2" : undefined) : null;
  const section = (title: string, entries: ReactNode[], first = false) => {
    const filled = entries.filter(Boolean);
    return (
      <>
        <CardHeader title={title} className={cn(SECTION_HEADER, !first && "border-t")} />
        <CardBody>
          {filled.length > 0 ? (
            <dl className={cn("grid gap-4", first ? "grid-cols-2 sm:grid-cols-4" : "grid-cols-1 sm:grid-cols-2")}>{filled}</dl>
          ) : (
            <p className="text-sm text-fg-subtle">{t("notRecorded")}</p>
          )}
        </CardBody>
      </>
    );
  };

  return (
    <Card>
      {section(
        t("sections.vitals"),
        vitals.map((key) =>
          entry(
            key,
            t(`vitals.${key}`),
            <>
              <span className="ltr-nums font-semibold" dir="ltr">
                {values[key]}
              </span>{" "}
              <span className="text-sm text-fg-muted">{t(`units.${key}`)}</span>
            </>,
          ),
        ),
        true,
      )}
      {section(t("sections.findings"), [text("symptoms", true), text("clinicalNotes", true)])}
      {section(t("sections.diagnosis"), [
        text("diagnosis"),
        values.diagnosisCode.trim()
          ? entry("diagnosisCode", t("fields.diagnosisCode"), <span className="font-mono" dir="ltr">{values.diagnosisCode}</span>)
          : null,
        text("treatmentPlan", true),
      ])}
      {section(t("sections.followUp"), [
        values.followUpDate ? entry("followUpDate", t("fields.followUpDate"), formatGregorianDate(values.followUpDate, locale)) : null,
        text("followUpNotes"),
      ])}
    </Card>
  );
}

export function VisitPage() {
  return (
    <RequirePermission anyOf={[P.Visits.View]}>
      <VisitScreen />
    </RequirePermission>
  );
}

export function VisitStatusBadge({ status }: { status: Visit["status"] }) {
  const t = useTranslations("visits.status");
  return status === "Completed" ? (
    <Badge tone="success" icon={<Lock className="size-3.5" />}>
      {t("Completed")}
    </Badge>
  ) : (
    <Badge tone="warning">{t("Open")}</Badge>
  );
}

function VisitScreen() {
  const t = useTranslations("visits");
  const tc = useTranslations("common");
  const { id } = useParams<{ id: string }>();
  const { api, can } = useAuth();
  const visit = useQuery(`visit:${id}`, (signal) => api.visits.get(id, signal));
  const patientId = visit.data?.patientId;
  const patient = useQuery(patientId && can(P.Patients.View) ? `patient:${patientId}` : null, (signal) =>
    api.patients.get(patientId!, signal),
  );

  if (visit.initialLoading) return <LoadingBlock label={t("loading")} />;
  if (!visit.data) {
    return visit.error ? (
      <Alert
        tone="danger"
        title={t("loadFailed")}
        action={
          <Button size="sm" variant="outline" onClick={visit.reload}>
            {tc("retry")}
          </Button>
        }
      >
        {isApiError(visit.error) ? visit.error.message : null}
      </Alert>
    ) : null;
  }

  return (
    <VisitWorkspace
      // A reload after a conflict re-mounts the form with the server's values.
      key={visit.data.rowVersion}
      visit={visit.data}
      patient={patient.data}
      onChanged={(updated) => visit.setData(() => updated)}
      onReload={visit.reload}
    />
  );
}

function VisitWorkspace({
  visit,
  patient,
  onChanged,
  onReload,
}: {
  visit: Visit;
  patient: PatientDetails | undefined;
  onChanged: (v: Visit) => void;
  onReload: () => void;
}) {
  const t = useTranslations("visits");
  const tc = useTranslations("common");
  const tp = useTranslations("patients");
  const tv = useTranslations("validation");
  const locale = useLocale();
  const toast = useToast();
  const { api, can } = useAuth();
  const { timeZone, branchName, ownDoctorId } = useClinic();
  const today = todayInZone(timeZone);
  const form = useForm<VisitFormValues>(toFormValues(visit));
  const [saving, setSaving] = useState(false);
  const [confirmComplete, setConfirmComplete] = useState(false);
  const [completing, setCompleting] = useState(false);

  const open = visit.status === "Open";
  // The API returns `clinical` only with MedicalRecords.View; without it nothing clinical is shown or editable.
  const canSeeClinical = can(P.MedicalRecords.View);
  const isTreatingDoctor = !!ownDoctorId && ownDoctorId === visit.doctorId;
  // GD-01: only the visit's own doctor documents and completes it (the API enforces the same rule); everyone else
  // with MedicalRecords.View reads it.
  const canEdit = open && isTreatingDoctor && canSeeClinical && can(P.Visits.Edit) && can(P.MedicalRecords.Edit);
  // Prescriptions are written while the visit is open; a completed visit is locked (the API refuses with VISIT_COMPLETED).
  const canPrescribe = open && isTreatingDoctor && can(P.Prescriptions.Create);
  const canReadPrescriptions = can(P.Prescriptions.View) && can(P.MedicalRecords.View);
  const dirty = canEdit && isDirty(form.values, visit);

  /** Saves the form; returns the saved visit, or null when it did not save (errors are shown on the form). */
  const save = async (): Promise<Visit | null> => {
    const found: Record<string, string> = {};
    for (const [key, code] of Object.entries(validateVisit(form.values, today))) {
      if (code) found[key] = tv(code as ValidationCode);
    }
    form.setErrors(found);
    form.setFormError(null);
    if (Object.keys(found).length > 0) {
      requestAnimationFrame(() => document.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus());
      return null;
    }
    setSaving(true);
    try {
      const saved = await api.visits.update(visit.id, toUpdateRequest(form.values, visit.rowVersion));
      onChanged(saved);
      return saved;
    } catch (e) {
      if (!isApiError(e)) throw e;
      if (e.isConcurrencyConflict) {
        toast({ tone: "warning", title: tc("conflictTitle"), description: tc("conflictBody") });
        onReload();
        return null;
      }
      if (e.errors) form.setErrors(mapVisitServerErrors(e.errors));
      form.setFormError(e);
      return null;
    } finally {
      setSaving(false);
    }
  };

  const complete = async () => {
    setCompleting(true);
    try {
      // Unsaved edits are saved first, so what is locked is what the doctor sees.
      const current = dirty ? await save() : visit;
      if (!current) return;
      const done = await api.visits.complete(visit.id, current.rowVersion);
      onChanged(done);
      toast({ tone: "success", title: t("completed") });
    } catch (e) {
      if (!isApiError(e)) throw e;
      toast({ tone: "danger", title: e.message });
      if (e.isConcurrencyConflict) onReload();
    } finally {
      setCompleting(false);
      setConfirmComplete(false);
    }
  };

  const age = patient?.dateOfBirth ? tp("ageYears", { count: ageInYears(patient.dateOfBirth, today) }) : null;
  const gender = patient ? tp(`gender.${patient.gender}`) : null;

  return (
    <>
      <PageHeader
        breadcrumbs={[
          { label: tp("title"), href: "/patients" },
          { label: visit.patientName, href: `/patients/${visit.patientId}` },
          { label: t("title") },
        ]}
        title={visit.patientName}
        description={
          <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
            <span className="ltr-nums">{visit.patientMrn}</span>
            {age ? <span>· {age}</span> : null}
            {gender ? <span>· {gender}</span> : null}
            <span>· {visit.doctorName}</span>
            <span>
              · <span className="ltr-nums">{formatDateTime(visit.visitedAtUtc, timeZone, locale)}</span>
            </span>
            {branchName(visit.branchId) ? <span>· {branchName(visit.branchId)}</span> : null}
            <VisitStatusBadge status={visit.status} />
          </span>
        }
        actions={
          canEdit ? (
            <Button icon={<CheckCheck className="size-4" aria-hidden="true" />} onClick={() => setConfirmComplete(true)} disabled={saving}>
              {t("complete")}
            </Button>
          ) : null
        }
      />

      {patient?.medical && (patient.medical.allergies || patient.medical.chronicConditions) ? (
        <Alert tone="warning" className="mb-6" title={t("alertsTitle")}>
          {patient.medical.allergies ? (
            <p>
              <span className="font-semibold">{tp("fields.allergies")}:</span> {patient.medical.allergies}
            </p>
          ) : null}
          {patient.medical.chronicConditions ? (
            <p>
              <span className="font-semibold">{tp("fields.chronicConditions")}:</span> {patient.medical.chronicConditions}
            </p>
          ) : null}
        </Alert>
      ) : null}

      {!open ? (
        <Alert tone="info" className="mb-6" title={t("lockedTitle")}>
          {t("lockedBody", { time: formatDateTime(visit.completedAtUtc, timeZone, locale) })}
        </Alert>
      ) : canSeeClinical && !isTreatingDoctor ? (
        <Alert tone="info" className="mb-6">
          {t("readOnlyNotTreating", { doctor: visit.doctorName })}
        </Alert>
      ) : null}

      <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
        <div className="flex min-w-0 flex-col gap-6 xl:col-span-2">
          {!canSeeClinical ? (
            <Card>
              <CardBody className="text-sm text-fg-muted">{t("clinicalHidden")}</CardBody>
            </Card>
          ) : !canEdit ? (
            <ClinicalSummary values={form.values} />
          ) : (
            <form
              noValidate
              onSubmit={(e) => {
                e.preventDefault();
                void save().then((saved) => saved && toast({ tone: "success", title: tc("saved") }));
              }}
            >
              <Card>
                <CardHeader title={t("sections.vitals")} className={SECTION_HEADER} />
                <CardBody>
                  <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                    {VITAL_KEYS.map((key) => (
                      <Input
                        key={key}
                        label={t(`vitals.${key}`)}
                        hint={t(`units.${key}`)}
                        inputMode={VITAL_RANGES[key].decimal ? "decimal" : "numeric"}
                        dir="ltr"
                        {...form.bind(key)}
                      />
                    ))}
                  </div>
                </CardBody>

                <CardHeader title={t("sections.findings")} className={cn(SECTION_HEADER, "border-t")} />
                <CardBody className="flex flex-col gap-4">
                  <Textarea label={t("fields.symptoms")} rows={2} maxLength={TEXT_LIMITS.symptoms} dir={textDir(form.values.symptoms)} {...form.bind("symptoms")} />
                  <Textarea
                    label={t("fields.clinicalNotes")}
                    hint={t("hints.clinicalNotes")}
                    rows={4}
                    maxLength={TEXT_LIMITS.clinicalNotes}
                    dir={textDir(form.values.clinicalNotes)} {...form.bind("clinicalNotes")}
                  />
                </CardBody>

                <CardHeader title={t("sections.diagnosis")} className={cn(SECTION_HEADER, "border-t")} />
                <CardBody className="flex flex-col gap-4">
                  <div className="grid grid-cols-1 gap-4 sm:grid-cols-4">
                    <Textarea
                      className="sm:col-span-3"
                      label={t("fields.diagnosis")}
                      rows={2}
                      maxLength={TEXT_LIMITS.diagnosis}
                      dir={textDir(form.values.diagnosis)} {...form.bind("diagnosis")}
                    />
                    <Input
                      label={t("fields.diagnosisCode")}
                      hint={t("hints.diagnosisCode")}
                      dir="ltr"
                      maxLength={TEXT_LIMITS.diagnosisCode}
                      {...form.bind("diagnosisCode")}
                    />
                  </div>
                  <Textarea
                    label={t("fields.treatmentPlan")}
                    rows={3}
                    maxLength={TEXT_LIMITS.treatmentPlan}
                    dir={textDir(form.values.treatmentPlan)} {...form.bind("treatmentPlan")}
                  />
                </CardBody>

                <CardHeader title={t("sections.followUp")} className={cn(SECTION_HEADER, "border-t")} />
                <CardBody>
                  <div className="grid grid-cols-1 gap-4 sm:grid-cols-4">
                    <Input label={t("fields.followUpDate")} type="date" min={today} {...form.bind("followUpDate")} />
                    <Input
                      className="sm:col-span-3"
                      label={t("fields.followUpNotes")}
                      maxLength={TEXT_LIMITS.followUpNotes}
                      dir={textDir(form.values.followUpNotes)} {...form.bind("followUpNotes")}
                    />
                  </div>
                </CardBody>

                <CardFooter className="sticky bottom-0 bg-surface">
                  {form.formError && !form.formError.errors ? (
                    <p role="alert" className="me-auto text-sm text-danger-strong">
                      {form.formError.message}
                    </p>
                  ) : dirty ? (
                    <p className="me-auto text-sm text-fg-muted" aria-live="polite">
                      {t("unsaved")}
                    </p>
                  ) : null}
                  <Button type="submit" loading={saving} disabled={!dirty}>
                    {tc("save")}
                  </Button>
                </CardFooter>
              </Card>
            </form>
          )}
        </div>

        <div className="flex min-w-0 flex-col gap-6">
          {canReadPrescriptions || canPrescribe ? (
            <VisitPrescriptions visit={visit} patient={patient} canPrescribe={canPrescribe} canRead={canReadPrescriptions} />
          ) : null}
          {can(P.Visits.View) ? <PreviousVisits visit={visit} /> : null}
        </div>
      </div>

      <ConfirmDialog
        open={confirmComplete}
        onClose={() => setConfirmComplete(false)}
        title={t("completeTitle")}
        description={dirty ? t("completeDescriptionUnsaved") : t("completeDescription")}
        confirmLabel={t("complete")}
        loading={completing}
        onConfirm={() => void complete()}
      />
    </>
  );
}

// ------------------------------------------------------------------ side panels

function SidePanel({ title, actions, children }: { title: ReactNode; actions?: ReactNode; children: ReactNode }) {
  return (
    <Card>
      <CardHeader title={title} actions={actions} as="h2" />
      <CardBody>{children}</CardBody>
    </Card>
  );
}

function VisitPrescriptions({
  visit,
  patient,
  canPrescribe,
  canRead,
}: {
  visit: Visit;
  patient: PatientDetails | undefined;
  canPrescribe: boolean;
  canRead: boolean;
}) {
  const t = useTranslations("prescriptions");
  const locale = useLocale();
  const toast = useToast();
  const { api } = useAuth();
  const { timeZone } = useClinic();
  const [issuing, setIssuing] = useState(false);
  const list = useQuery(canRead ? `visit-rx:${visit.id}` : null, async (signal) =>
    (await api.prescriptions.listForPatient(visit.patientId, { pageSize: 100 }, signal)).items.filter((p) => p.visitId === visit.id),
  );

  return (
    <SidePanel
      title={t("forVisit")}
      actions={
        canPrescribe ? (
          <Button size="sm" variant="secondary" icon={<Plus className="size-4" aria-hidden="true" />} onClick={() => setIssuing(true)}>
            {t("new")}
          </Button>
        ) : null
      }
    >
      {!canRead ? (
        <p className="text-sm text-fg-muted">{t("listHidden")}</p>
      ) : list.initialLoading ? (
        <LoadingBlock label={t("loading")} className="py-4" />
      ) : (list.data ?? []).length === 0 ? (
        <p className="text-sm text-fg-muted">{canPrescribe ? t("noneForVisit") : t("noneForVisitReadOnly")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border">
          {list.data!.map((p) => (
            <li key={p.id} className="flex items-center justify-between gap-3 py-2.5">
              <div className="min-w-0">
                <p className="ltr-nums text-sm font-semibold text-fg">{p.number}</p>
                <p className="text-xs text-fg-muted">
                  <span className="ltr-nums">{formatDateTime(p.issuedAtUtc, timeZone, locale)}</span> · {t("itemCount", { count: p.itemCount })}
                </p>
              </div>
              <ButtonLink href={`/prescriptions/${p.id}`} size="sm" variant="outline" icon={<FileText className="size-4" aria-hidden="true" />}>
                {t("view")}
              </ButtonLink>
            </li>
          ))}
        </ul>
      )}
      {issuing ? (
        <PrescriptionDialog
          visit={visit}
          patient={patient}
          onClose={() => setIssuing(false)}
          onIssued={(rx) => {
            setIssuing(false);
            toast({ tone: "success", title: t("issued", { number: rx.number }) });
            list.reload();
          }}
        />
      ) : null}
    </SidePanel>
  );
}

function PreviousVisits({ visit }: { visit: Visit }) {
  const t = useTranslations("visits");
  const locale = useLocale();
  const { api } = useAuth();
  const { timeZone } = useClinic();
  const list = useQuery(`patient-visits:${visit.patientId}`, async (signal) =>
    (await api.visits.listForPatient(visit.patientId, { pageSize: 6 }, signal)).items,
  );
  const others: VisitListItem[] = (list.data ?? []).filter((v) => v.id !== visit.id).slice(0, 5);

  return (
    <SidePanel title={t("previous")}>
      {list.initialLoading ? (
        <LoadingBlock label={t("loading")} className="py-4" />
      ) : others.length === 0 ? (
        <p className="text-sm text-fg-muted">{t("noPrevious")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border">
          {others.map((v) => (
            <li key={v.id} className="py-2.5">
              <Link href={`/visits/${v.id}`} className="text-sm font-medium text-primary-strong hover:underline">
                <span className="ltr-nums">{formatShortDate(isoDateInZone(new Date(v.visitedAtUtc), timeZone), locale)}</span> — {v.doctorName}
              </Link>
              {v.diagnosis ? <p className="mt-0.5 line-clamp-2 text-sm text-fg-muted">{v.diagnosis}</p> : null}
            </li>
          ))}
        </ul>
      )}
    </SidePanel>
  );
}
