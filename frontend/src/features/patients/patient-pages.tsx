"use client";

import { HeartPulse, Pencil, ShieldAlert, Stethoscope, Trash2 } from "lucide-react";
import { useParams, useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { PageHeader, RequirePermission } from "@/components/app/page";
import { Alert } from "@/components/ui/alert";
import { Button, ButtonLink } from "@/components/ui/button";
import { Card, CardBody, CardHeader, DescriptionList } from "@/components/ui/card";
import { ConfirmDialog, Dialog } from "@/components/ui/dialog";
import { Select, Textarea } from "@/components/ui/field";
import { LoadingBlock } from "@/components/ui/spinner";
import { useToast } from "@/components/ui/toast";
import { useClinic } from "@/features/clinic/clinic-context";
import { useOpenVisit } from "@/features/visits/use-open-visit";
import { isApiError } from "@/lib/api/client";
import type { Appointment, BloodGroup, PatientDetails } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { ageInYears, formatDateTime, formatHijriDate, formatShortDate, todayInZone } from "@/lib/datetime";
import { useForm } from "@/lib/hooks/use-form";
import { useQuery } from "@/lib/hooks/use-query";
import { emptyToNull } from "@/lib/utils";
import { check, rules, toLocalMobile } from "@/lib/validation/saudi";
import { PatientForm } from "./patient-form";
import { PatientHistory } from "./patient-history";
import { latestAppointments } from "./patient-history-model";

const BLOOD_GROUPS: BloodGroup[] = ["APositive", "ANegative", "BPositive", "BNegative", "ABPositive", "ABNegative", "OPositive", "ONegative"];

function usePatient(id: string) {
  const { api } = useAuth();
  return useQuery(`patient:${id}`, (signal) => api.patients.get(id, signal));
}

function LoadError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const t = useTranslations("patients");
  const tc = useTranslations("common");
  return (
    <Alert
      tone="danger"
      title={t("loadFailed")}
      action={
        <Button size="sm" variant="outline" onClick={onRetry}>
          {tc("retry")}
        </Button>
      }
    >
      {isApiError(error) ? error.message : null}
    </Alert>
  );
}

// ------------------------------------------------------------------ new

export function NewPatientPage() {
  const t = useTranslations("patients");
  const router = useRouter();
  const toast = useToast();
  return (
    <RequirePermission anyOf={[P.Patients.Create]}>
      <PageHeader
        title={t("new")}
        breadcrumbs={[{ label: t("title"), href: "/patients" }, { label: t("new") }]}
      />
      <PatientForm
        onSaved={(p) => {
          toast({ tone: "success", title: t("created", { mrn: p.medicalRecordNumber }) });
          router.push(`/patients/${p.id}`);
        }}
      />
    </RequirePermission>
  );
}

// ------------------------------------------------------------------ edit

export function EditPatientPage() {
  const t = useTranslations("patients");
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const toast = useToast();
  const patient = usePatient(id);

  return (
    <RequirePermission anyOf={[P.Patients.Edit]}>
      <PageHeader
        title={t("edit")}
        breadcrumbs={[
          { label: t("title"), href: "/patients" },
          { label: patient.data?.fullName ?? "…", href: `/patients/${id}` },
          { label: t("edit") },
        ]}
      />
      {patient.initialLoading ? (
        <LoadingBlock label={t("loading")} />
      ) : patient.error && !patient.data ? (
        <LoadError error={patient.error} onRetry={patient.reload} />
      ) : patient.data ? (
        <PatientForm
          // Re-mount with fresh values after a reload (e.g. following a concurrency conflict).
          key={patient.data.rowVersion}
          patient={patient.data}
          onReload={patient.reload}
          onSaved={() => {
            toast({ tone: "success", title: t("updated") });
            router.push(`/patients/${id}`);
          }}
        />
      ) : null}
    </RequirePermission>
  );
}

// ------------------------------------------------------------------ details

export function PatientDetailsPage() {
  return (
    <RequirePermission anyOf={[P.Patients.View]}>
      <Details />
    </RequirePermission>
  );
}

function Details() {
  const t = useTranslations("patients");
  const locale = useLocale();
  const router = useRouter();
  const toast = useToast();
  const { id } = useParams<{ id: string }>();
  const { api, can } = useAuth();
  const { timeZone } = useClinic();
  const patient = usePatient(id);
  const appointments = useQuery(can(P.Appointments.View) ? `patient-appts:${id}` : null, (signal) =>
    latestAppointments((page, pageSize) => api.appointments.list({ patientId: id, page, pageSize }, signal)),
  );
  const [editMedical, setEditMedical] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const today = todayInZone(timeZone);

  if (patient.initialLoading) return <LoadingBlock label={t("loading")} />;
  if (!patient.data) return patient.error ? <LoadError error={patient.error} onRetry={patient.reload} /> : null;
  const p = patient.data;

  return (
    <>
      <PageHeader
        title={p.fullName}
        description={
          <>
            <span className="ltr-nums">{p.medicalRecordNumber}</span>
            {p.dateOfBirth ? ` · ${t("ageYears", { count: ageInYears(p.dateOfBirth, today) })}` : null}
          </>
        }
        breadcrumbs={[{ label: t("title"), href: "/patients" }, { label: p.fullName }]}
        actions={
          <>
            <StartVisitButton patientId={p.id} appointments={appointments.data?.items} />
            {can(P.Patients.Delete) ? (
              <Button variant="outline" icon={<Trash2 className="size-4" aria-hidden="true" />} onClick={() => setConfirmDelete(true)}>
                {t("delete")}
              </Button>
            ) : null}
            {can(P.Patients.Edit) ? (
              <ButtonLink href={`/patients/${p.id}/edit`} icon={<Pencil className="size-4" aria-hidden="true" />}>
                {t("edit")}
              </ButtonLink>
            ) : null}
          </>
        }
      />

      <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
        <div className="flex min-w-0 flex-col gap-6 xl:col-span-2">
          <Card>
            <CardHeader title={t("sections.demographics")} />
            <CardBody>
              <DescriptionList
                items={[
                  { label: t("fields.fullNameEn"), value: p.fullNameEn ? <span dir="ltr">{p.fullNameEn}</span> : null },
                  { label: t("fields.mobile"), value: <span className="ltr-nums">{toLocalMobile(p.mobileNumber)}</span> },
                  { label: t("fields.email"), value: p.email ? <span dir="ltr">{p.email}</span> : null },
                  { label: t("fields.gender"), value: t(`gender.${p.gender}`) },
                  {
                    label: t("fields.dateOfBirth"),
                    value: p.dateOfBirth ? (
                      <>
                        <span className="ltr-nums">{formatShortDate(p.dateOfBirth, locale)}</span>
                        <span className="block text-xs text-fg-subtle">{formatHijriDate(p.dateOfBirth, locale)}</span>
                      </>
                    ) : null,
                  },
                  { label: t("fields.identityType"), value: t(`identityTypes.${p.identityType}`) },
                  { label: t("fields.identityNumber"), value: p.identityNumber ? <span className="ltr-nums">{p.identityNumber}</span> : null },
                  { label: t("fields.nationality"), value: p.nationality ? regionName(p.nationality, locale) : null },
                  { label: t("fields.registeredAt"), value: <span className="tabular-nums">{formatDateTime(p.createdAtUtc, timeZone, locale)}</span> },
                ]}
              />
              {p.notes ? (
                <div className="mt-5 rounded-control bg-surface-muted px-4 py-3 text-sm">
                  <p className="text-xs font-medium text-fg-subtle">{t("fields.notes")}</p>
                  <p className="mt-1 whitespace-pre-line">{p.notes}</p>
                </div>
              ) : null}
            </CardBody>
          </Card>

          {/* Clinical data: the API returns `medical` only to holders of MedicalRecords.View. */}
          {p.medical ? (
            <Card>
              <CardHeader
                title={
                  <span className="flex items-center gap-2">
                    <HeartPulse className="size-5 text-danger" aria-hidden="true" />
                    {t("sections.medical")}
                  </span>
                }
                description={t("medicalConfidential")}
                actions={
                  can(P.MedicalRecords.Edit) ? (
                    <Button variant="outline" size="sm" icon={<Pencil className="size-4" aria-hidden="true" />} onClick={() => setEditMedical(true)}>
                      {t("editMedical")}
                    </Button>
                  ) : null
                }
              />
              <CardBody>
                <DescriptionList
                  items={[
                    { label: t("fields.bloodGroup"), value: p.medical.bloodGroup ? t(`bloodGroups.${p.medical.bloodGroup}`) : null },
                    { label: t("fields.allergies"), value: p.medical.allergies },
                    { label: t("fields.chronicConditions"), value: p.medical.chronicConditions },
                    { label: t("fields.medicalNotes"), value: p.medical.medicalNotes ? <span className="whitespace-pre-line">{p.medical.medicalNotes}</span> : null },
                  ]}
                />
              </CardBody>
            </Card>
          ) : (
            <Card>
              <CardBody className="flex items-center gap-3 text-sm text-fg-muted">
                <ShieldAlert className="size-5 shrink-0" aria-hidden="true" />
                {t("medicalHidden")}
              </CardBody>
            </Card>
          )}
        </div>

        <div className="min-w-0">
          <PatientHistory
            patientId={p.id}
            appointments={appointments.data?.items}
            appointmentsTruncated={appointments.data?.truncated ?? false}
          />
        </div>
      </div>

      {editMedical ? (
        <MedicalDialog
          patient={p}
          onClose={() => setEditMedical(false)}
          onSaved={(saved) => {
            patient.setData(() => saved);
            setEditMedical(false);
            toast({ tone: "success", title: t("updated") });
          }}
          onConflict={() => {
            setEditMedical(false);
            patient.reload();
          }}
        />
      ) : null}

      <ConfirmDialog
        open={confirmDelete}
        onClose={() => setConfirmDelete(false)}
        title={t("deleteTitle")}
        description={t("deleteDescription", { name: p.fullName })}
        confirmLabel={t("delete")}
        tone="danger"
        loading={deleting}
        onConfirm={async () => {
          setDeleting(true);
          try {
            await api.patients.remove(p.id);
            toast({ tone: "success", title: t("deleted") });
            router.push("/patients");
          } catch (e) {
            toast({ tone: "danger", title: isApiError(e) ? e.message : t("loadFailed") });
          } finally {
            setDeleting(false);
            setConfirmDelete(false);
          }
        }}
      />
    </>
  );
}

/**
 * "Start visit" for the signed-in doctor. If the patient is checked in for this doctor, the visit belongs to that
 * appointment; otherwise a visit without an appointment is recorded (the API picks the member's only branch, or asks
 * for one when there are several). The doctor is the caller's own profile from /auth/me — never chosen here.
 */
function StartVisitButton({ patientId, appointments }: { patientId: string; appointments: Appointment[] | undefined }) {
  const t = useTranslations("visits");
  const tc = useTranslations("common");
  const router = useRouter();
  const toast = useToast();
  const { api, can } = useAuth();
  const { ownDoctorId, allowedBranches } = useClinic();
  const openVisit = useOpenVisit();
  const [choosing, setChoosing] = useState(false);
  const [branchId, setBranchId] = useState("");
  const [busy, setBusy] = useState(false);

  if (!ownDoctorId || !can(P.Visits.Create) || !can(P.MedicalRecords.Edit)) return null;
  const checkedIn = appointments?.find((a) => a.status === "CheckedIn" && a.doctorId === ownDoctorId);
  const activeBranches = allowedBranches.filter((b) => b.isActive);

  const create = async (branch: string | null) => {
    setBusy(true);
    try {
      const visit = await api.visits.create({ patientId, doctorId: ownDoctorId, branchId: branch });
      router.push(`/visits/${visit.id}`);
    } catch (e) {
      toast({ tone: "danger", title: isApiError(e) ? e.message : String(e) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Button
        variant="secondary"
        icon={<Stethoscope className="size-4" aria-hidden="true" />}
        loading={busy || (checkedIn ? openVisit.busyId === checkedIn.id : false)}
        onClick={() => {
          if (checkedIn) void openVisit.open(checkedIn);
          else if (activeBranches.length > 1) setChoosing(true);
          else void create(activeBranches[0]?.id ?? null);
        }}
      >
        {checkedIn ? t("openForAppointment") : t("start")}
      </Button>
      {choosing ? (
        <Dialog
          open
          size="sm"
          onClose={() => setChoosing(false)}
          title={t("start")}
          description={t("chooseBranch")}
          dismissible={!busy}
          footer={
            <>
              <Button variant="outline" onClick={() => setChoosing(false)} disabled={busy}>
                {tc("cancel")}
              </Button>
              <Button onClick={() => void create(branchId)} loading={busy} disabled={!branchId}>
                {t("start")}
              </Button>
            </>
          }
        >
          <Select
            label={t("branch")}
            placeholder={t("chooseBranchPlaceholder")}
            value={branchId}
            onChange={(e) => setBranchId(e.target.value)}
            options={activeBranches.map((b) => ({ value: b.id, label: b.name }))}
          />
        </Dialog>
      ) : null}
    </>
  );
}

function regionName(code: string, locale: string) {
  try {
    return new Intl.DisplayNames([locale], { type: "region" }).of(code) ?? code;
  } catch {
    return code;
  }
}

function MedicalDialog({
  patient,
  onClose,
  onSaved,
  onConflict,
}: {
  patient: PatientDetails;
  onClose: () => void;
  onSaved: (p: PatientDetails) => void;
  onConflict: () => void;
}) {
  const t = useTranslations("patients");
  const tc = useTranslations("common");
  const toast = useToast();
  const { api } = useAuth();
  const form = useForm({
    bloodGroup: patient.medical?.bloodGroup ?? "",
    allergies: patient.medical?.allergies ?? "",
    chronicConditions: patient.medical?.chronicConditions ?? "",
    medicalNotes: patient.medical?.medicalNotes ?? "",
  });

  const save = () =>
    form
      .submit(
        (v) => ({
          allergies: check(v.allergies, rules.maxLength(1000)),
          chronicConditions: check(v.chronicConditions, rules.maxLength(1000)),
          medicalNotes: check(v.medicalNotes, rules.maxLength(2000)),
        }),
        async (v) => {
          try {
            onSaved(
              await api.patients.updateMedical(patient.id, {
                bloodGroup: (emptyToNull(v.bloodGroup) as BloodGroup | null) ?? null,
                allergies: emptyToNull(v.allergies),
                chronicConditions: emptyToNull(v.chronicConditions),
                medicalNotes: emptyToNull(v.medicalNotes),
                rowVersion: patient.rowVersion,
              }),
            );
          } catch (e) {
            if (isApiError(e) && e.isConcurrencyConflict) {
              toast({ tone: "warning", title: tc("conflictTitle"), description: tc("conflictBody") });
              onConflict();
              return;
            }
            throw e;
          }
        },
      )
      .then(() => undefined);

  return (
    <Dialog
      open
      onClose={onClose}
      title={t("editMedical")}
      description={t("medicalConfidential")}
      dismissible={!form.submitting}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={form.submitting}>
            {tc("cancel")}
          </Button>
          <Button onClick={() => void save()} loading={form.submitting}>
            {tc("save")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {form.formError ? <Alert tone="danger">{form.formError.message}</Alert> : null}
        <Select
          label={t("fields.bloodGroup")}
          placeholder={t("unspecified")}
          options={BLOOD_GROUPS.map((b) => ({ value: b, label: t(`bloodGroups.${b}`) }))}
          {...form.bind("bloodGroup")}
        />
        <Textarea label={t("fields.allergies")} maxLength={1000} {...form.bind("allergies")} />
        <Textarea label={t("fields.chronicConditions")} maxLength={1000} {...form.bind("chronicConditions")} />
        <Textarea label={t("fields.medicalNotes")} maxLength={2000} rows={4} {...form.bind("medicalNotes")} />
      </div>
    </Dialog>
  );
}
