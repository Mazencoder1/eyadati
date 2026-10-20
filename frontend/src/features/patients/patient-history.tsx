"use client";

import { CalendarClock, FileText, Stethoscope } from "lucide-react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { useMemo } from "react";
import { Card, CardBody, CardHeader } from "@/components/ui/card";
import { LoadingBlock } from "@/components/ui/spinner";
import { AppointmentStatusBadge } from "@/features/appointments/status";
import { useClinic } from "@/features/clinic/clinic-context";
import { VisitStatusBadge } from "@/features/visits/visit-page";
import type { Appointment } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { formatGregorianDate, formatTime, todayInZone } from "@/lib/datetime";
import { useQuery } from "@/lib/hooks/use-query";
import { buildHistory, nextFollowUp, type HistoryEntry } from "./patient-history-model";

const PAGE = 100; // API maximum

/**
 * The patient's history, newest first. Each source is loaded only with the permission the API requires for it, so a
 * receptionist sees appointments only, a nurse also sees visits, and diagnoses/prescriptions need MedicalRecords.View.
 * Doctors without ViewAllDoctors get only their own visits and prescriptions from the API.
 */
export function PatientHistory({
  patientId,
  appointments,
  appointmentsTruncated,
}: {
  patientId: string;
  appointments: Appointment[] | undefined;
  appointmentsTruncated: boolean;
}) {
  const t = useTranslations("history");
  const locale = useLocale();
  const { api, can } = useAuth();
  const { timeZone } = useClinic();
  const canVisits = can(P.Visits.View);
  const canRx = can(P.Prescriptions.View) && can(P.MedicalRecords.View);

  const visits = useQuery(canVisits ? `patient-visits-all:${patientId}` : null, (signal) =>
    api.visits.listForPatient(patientId, { pageSize: PAGE }, signal),
  );
  const prescriptions = useQuery(canRx ? `patient-rx:${patientId}` : null, (signal) =>
    api.prescriptions.listForPatient(patientId, { pageSize: PAGE }, signal),
  );

  const days = useMemo(
    () => buildHistory(appointments, visits.data?.items, prescriptions.data?.items, timeZone),
    [appointments, visits.data, prescriptions.data, timeZone],
  );
  const loading = (appointments === undefined && can(P.Appointments.View)) || visits.initialLoading || prescriptions.initialLoading;
  const truncated =
    appointmentsTruncated || (visits.data?.meta.totalPages ?? 1) > 1 || (prescriptions.data?.meta.totalPages ?? 1) > 1;
  const followUp = nextFollowUp(visits.data?.items, todayInZone(timeZone));

  return (
    <Card>
      <CardHeader title={t("title")} description={!canVisits ? t("appointmentsOnly") : !can(P.MedicalRecords.View) ? t("noClinical") : undefined} />
      <CardBody>
        {followUp ? (
          <p className="mb-4 flex items-center gap-2 text-sm text-fg">
            <CalendarClock className="size-4 shrink-0 text-primary" aria-hidden="true" />
            {t("nextFollowUp", { date: formatGregorianDate(followUp, locale) })}
          </p>
        ) : null}
        {loading ? (
          <LoadingBlock label={t("loading")} className="py-6" />
        ) : days.length === 0 ? (
          <p className="text-sm text-fg-muted">{t("empty")}</p>
        ) : (
          <ol className="flex flex-col gap-5">
            {days.map((day) => (
              <li key={day.date}>
                <DayHeading date={day.date} />
                <ul className="mt-2 flex flex-col divide-y divide-border border-s-2 border-border ps-4">
                  {day.entries.map((e) => (
                    <Entry key={`${e.kind}:${entryId(e)}`} entry={e} />
                  ))}
                </ul>
              </li>
            ))}
          </ol>
        )}
        {truncated ? <p className="mt-4 text-xs text-fg-subtle">{t("truncated")}</p> : null}
      </CardBody>
    </Card>
  );
}

function entryId(e: HistoryEntry) {
  return e.kind === "visit" ? e.visit.id : e.kind === "prescription" ? e.prescription.id : e.appointment.id;
}

function DayHeading({ date }: { date: string }) {
  const locale = useLocale();
  return <h3 className="text-sm font-semibold text-fg">{formatGregorianDate(date, locale)}</h3>;
}

function Entry({ entry }: { entry: HistoryEntry }) {
  const t = useTranslations("history");
  const locale = useLocale();
  const { timeZone } = useClinic();
  const time = <span className="ltr-nums text-xs text-fg-subtle">{formatTime(entry.at, timeZone, locale)}</span>;

  if (entry.kind === "visit") {
    const v = entry.visit;
    return (
      <li className="flex flex-col gap-1 py-2.5">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <Stethoscope className="size-4 shrink-0 text-fg-subtle" aria-hidden="true" />
          <Link href={`/visits/${v.id}`} className="text-sm font-semibold text-primary-strong hover:underline">
            {t("visit", { doctor: v.doctorName })}
          </Link>
          {time}
          <VisitStatusBadge status={v.status} />
        </div>
        {v.diagnosis ? (
          <p className="text-sm text-fg">
            <span className="text-fg-muted">{t("diagnosis")}:</span> {v.diagnosis}
          </p>
        ) : null}
        {v.followUpDate ? (
          <p className="text-sm text-fg">
            <span className="text-fg-muted">{t("followUp")}:</span> {formatGregorianDate(v.followUpDate, locale)}
          </p>
        ) : null}
      </li>
    );
  }

  if (entry.kind === "prescription") {
    const p = entry.prescription;
    return (
      <li className="flex flex-wrap items-center gap-x-2 gap-y-1 py-2.5">
        <FileText className="size-4 shrink-0 text-fg-subtle" aria-hidden="true" />
        <Link href={`/prescriptions/${p.id}`} className="text-sm font-semibold text-primary-strong hover:underline">
          {t("prescription", { number: p.number })}
        </Link>
        <span className="text-sm text-fg-muted">{t("medications", { count: p.itemCount })}</span>
        {time}
      </li>
    );
  }

  const a = entry.appointment;
  return (
    <li className="flex flex-wrap items-center gap-x-2 gap-y-1 py-2.5">
      <span className="text-sm text-fg">{t("appointment", { doctor: a.doctorName })}</span>
      {time}
      <AppointmentStatusBadge status={a.status} />
    </li>
  );
}
