"use client";

import { ArrowRight, Printer } from "lucide-react";
import { useParams } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { RequirePermission } from "@/components/app/page";
import { Alert } from "@/components/ui/alert";
import { Button, ButtonLink } from "@/components/ui/button";
import { LoadingBlock } from "@/components/ui/spinner";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { Prescription } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { ageInYears, formatGregorianDate, formatHijriDate, formatTime, isoDateInZone, todayInZone } from "@/lib/datetime";
import { useQuery } from "@/lib/hooks/use-query";
import { toLocalMobile } from "@/lib/validation/saudi";

/** Reading a prescription needs both permissions (the API enforces the same pair). */
export function PrescriptionDocumentPage() {
  return (
    <RequirePermission anyOf={[P.Prescriptions.View]}>
      <RequirePermission anyOf={[P.MedicalRecords.View]}>
        <PrescriptionDocument />
      </RequirePermission>
    </RequirePermission>
  );
}

/**
 * The printable prescription. Everything shown comes from existing data: the prescription itself, the clinic
 * settings, the visit's branch, the doctor's profile (specialty, SCFHS licence) and the patient. Pieces the user may
 * not read (e.g. a doctor hidden by branch scope) are simply left out. On screen it shows a toolbar; on paper only the
 * document (the app shell hides itself in print).
 */
function PrescriptionDocument() {
  const t = useTranslations("prescriptions");
  const tc = useTranslations("common");
  const { id } = useParams<{ id: string }>();
  const { api } = useAuth();
  const rx = useQuery(`rx:${id}`, (signal) => api.prescriptions.get(id, signal));

  if (rx.initialLoading) return <LoadingBlock label={t("loading")} />;
  if (!rx.data) {
    return rx.error ? (
      <Alert
        tone="danger"
        title={t("loadFailed")}
        action={
          <Button size="sm" variant="outline" onClick={rx.reload}>
            {tc("retry")}
          </Button>
        }
      >
        {isApiError(rx.error) ? rx.error.message : null}
      </Alert>
    ) : null;
  }
  return <Document rx={rx.data} />;
}

function Document({ rx }: { rx: Prescription }) {
  const t = useTranslations("prescriptions");
  const tp = useTranslations("patients");
  const locale = useLocale();
  const { api, can } = useAuth();
  const { settings, timeZone, branches } = useClinic();

  const quiet = <T,>(p: Promise<T>) => p.catch(() => undefined);
  const visit = useQuery(rx.visitId && can(P.Visits.View) ? `visit:${rx.visitId}` : null, (signal) => quiet(api.visits.get(rx.visitId!, signal)));
  const doctor = useQuery(can(P.Doctors.View) ? `doctor:${rx.doctorId}` : null, (signal) => quiet(api.doctors.get(rx.doctorId, signal)));
  const patient = useQuery(can(P.Patients.View) ? `patient:${rx.patientId}` : null, (signal) => quiet(api.patients.get(rx.patientId, signal)));

  const branch = branches.find((b) => b.id === visit.data?.branchId) ?? branches.find((b) => b.isMain);
  const issuedDate = isoDateInZone(new Date(rx.issuedAtUtc), timeZone);
  const today = todayInZone(timeZone);
  const p = patient.data;
  const d = doctor.data;
  const backHref = rx.visitId && can(P.Visits.View) ? `/visits/${rx.visitId}` : `/patients/${rx.patientId}`;
  const phone = branch?.phone ?? settings?.phone;

  return (
    <>
      <div className="mb-4 flex flex-wrap items-center justify-between gap-2 print:hidden">
        <ButtonLink href={backHref} variant="ghost" size="sm" icon={<ArrowRight className="size-4 rtl:rotate-0 ltr:rotate-180" aria-hidden="true" />}>
          {t("back")}
        </ButtonLink>
        <Button icon={<Printer className="size-4" aria-hidden="true" />} onClick={() => window.print()}>
          {t("print")}
        </Button>
      </div>

      <article
        aria-label={t("documentTitle", { number: rx.number })}
        className="mx-auto max-w-3xl border border-border bg-surface px-6 py-8 text-fg sm:px-10 print:max-w-none print:border-0 print:p-0"
      >
        <header className="flex flex-col gap-4 border-b-2 border-fg pb-4 sm:flex-row sm:items-start sm:justify-between print:flex-row print:items-start print:justify-between">
          <div className="min-w-0">
            <p className="text-xl font-bold">{settings?.name}</p>
            {branch && branches.length > 1 ? <p className="text-sm">{branch.name}</p> : null}
            <p className="text-sm text-fg-muted">
              {[branch?.address, branch?.city ?? settings?.city].filter(Boolean).join("، ")}
            </p>
            {phone ? (
              <p className="text-sm text-fg-muted">
                {t("doc.phone")}: <span className="ltr-nums">{toLocalMobile(phone)}</span>
              </p>
            ) : null}
            {settings?.commercialRegistration ? (
              <p className="text-sm text-fg-muted">
                {t("doc.cr")}: <span className="ltr-nums">{settings.commercialRegistration}</span>
              </p>
            ) : null}
          </div>
          <div className="shrink-0 sm:text-end print:text-end">
            <h1 className="text-lg font-bold">{t("doc.title")}</h1>
            <p className="ltr-nums text-sm font-semibold">{rx.number}</p>
            <p className="text-sm">
              {formatGregorianDate(issuedDate, locale)} — <span className="ltr-nums">{formatTime(rx.issuedAtUtc, timeZone, locale)}</span>
            </p>
            <p className="text-xs text-fg-muted">{formatHijriDate(issuedDate, locale)}</p>
          </div>
        </header>

        <section aria-label={t("doc.patient")} className="grid grid-cols-2 gap-x-6 gap-y-2 border-b border-border py-4 text-sm sm:grid-cols-4 print:grid-cols-4">
          <Field label={t("doc.patient")} value={rx.patientName} wide />
          <Field label={tp("fields.mrn")} value={<span className="ltr-nums">{rx.patientMrn}</span>} />
          <Field label={tp("fields.age")} value={p?.dateOfBirth ? tp("ageYears", { count: ageInYears(p.dateOfBirth, today) }) : null} />
          {p ? <Field label={tp("fields.gender")} value={tp(`gender.${p.gender}`)} /> : null}
        </section>

        <section aria-label={t("doc.medications")} className="py-5">
          <p className="mb-3 text-2xl font-bold" aria-hidden="true">
            ℞
          </p>
          <ol className="flex flex-col divide-y divide-border">
            {rx.items.map((item) => (
              <li key={item.lineNumber} className="flex gap-3 py-3 break-inside-avoid">
                <span className="ltr-nums w-6 shrink-0 font-semibold">{item.lineNumber}.</span>
                <div className="min-w-0 flex-1">
                  {/* <bdi> keeps Latin drug names readable inside RTL text without changing the line's alignment. */}
                  <p className="font-semibold">
                    <bdi>{item.medicationName}</bdi>
                    {item.strength ? (
                      <span className="font-normal">
                        {" — "}
                        <bdi>{item.strength}</bdi>
                      </span>
                    ) : null}
                  </p>
                  <p className="mt-0.5 text-sm">
                    {[
                      item.dosage,
                      item.frequency,
                      item.route,
                      item.durationDays ? t("doc.forDays", { count: item.durationDays }) : null,
                    ]
                      .filter(Boolean)
                      .join(" · ")}
                  </p>
                  {item.instructions ? <p className="mt-0.5 text-sm text-fg-muted">{item.instructions}</p> : null}
                </div>
              </li>
            ))}
          </ol>
          {rx.notes ? (
            <p className="mt-4 border-t border-border pt-3 text-sm">
              <span className="font-semibold">{t("fields.notes")}:</span> {rx.notes}
            </p>
          ) : null}
        </section>

        <footer className="mt-6 flex flex-col gap-6 border-t border-border pt-4 text-sm sm:flex-row sm:items-end sm:justify-between print:flex-row print:items-end print:justify-between">
          <div>
            <p className="font-semibold">{rx.doctorName}</p>
            {d?.specialty ? <p>{d.specialty}</p> : null}
            {d?.licenseNumber ? (
              <p className="text-fg-muted">
                {t("doc.license")}: <span className="ltr-nums">{d.licenseNumber}</span>
              </p>
            ) : null}
          </div>
          <div className="w-48">
            <div className="h-12 border-b border-fg" />
            <p className="mt-1 text-xs text-fg-muted">{t("doc.signature")}</p>
          </div>
        </footer>
      </article>
    </>
  );
}

function Field({ label, value, wide }: { label: string; value: React.ReactNode; wide?: boolean }) {
  return (
    <div className={wide ? "col-span-2 sm:col-span-1 print:col-span-1" : undefined}>
      <p className="text-xs text-fg-subtle">{label}</p>
      <p className="font-medium">{value || "—"}</p>
    </div>
  );
}
