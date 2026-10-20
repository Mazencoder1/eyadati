"use client";

import { CalendarClock, CalendarPlus } from "lucide-react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { useState } from "react";
import { PageHeader, RequirePermission } from "@/components/app/page";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/ui/data-table";
import { EmptyState } from "@/components/ui/empty-state";
import { Input, Select } from "@/components/ui/field";
import { useToast } from "@/components/ui/toast";
import { BookingDialog } from "@/features/appointments/booking-dialog";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { FollowUpItem } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { addDays, formatDateTime, formatShortDate, isIsoDate, todayInZone } from "@/lib/datetime";
import { useQuery } from "@/lib/hooks/use-query";

/**
 * Follow-ups the doctors asked for, so the front desk can book them. Operational data only (patient, date, doctor,
 * booked or not) — the API never returns diagnosis, notes or other clinical content here.
 */
export function FollowUpsPage() {
  return (
    <RequirePermission anyOf={[P.Appointments.View]}>
      <FollowUps />
    </RequirePermission>
  );
}

type BookedFilter = "" | "false" | "true";

function FollowUps() {
  const t = useTranslations("followUps");
  const locale = useLocale();
  const toast = useToast();
  const { api, can } = useAuth();
  const { timeZone } = useClinic();
  const today = todayInZone(timeZone);
  const [page, setPage] = useState(1);
  const [from, setFrom] = useState(today);
  const [to, setTo] = useState(addDays(today, 7));
  const [doctorId, setDoctorId] = useState("");
  const [booked, setBooked] = useState<BookedFilter>("false");
  const [booking, setBooking] = useState<FollowUpItem | null>(null);
  const canBook = can(P.Appointments.Create);

  const rangeInvalid = isIsoDate(from) && isIsoDate(to) && from > to;
  const ready = isIsoDate(from) && isIsoDate(to) && !rangeInvalid;
  const list = useQuery(ready ? `follow-ups:${page}:${from}:${to}:${doctorId}:${booked}` : null, (signal) =>
    api.followUps.list(
      { page, pageSize: 25, from, to, doctorId: doctorId || undefined, booked: booked === "" ? undefined : booked === "true" },
      signal,
    ),
  );
  const doctors = useQuery("doctors:active", async (signal) => (await api.doctors.list({ activeOnly: true, pageSize: 100 }, signal)).items);
  const services = useQuery(canBook ? "services:active" : null, () => api.services.list(true));
  const resetPage = <V,>(set: (v: V) => void) => (v: V) => {
    set(v);
    setPage(1);
  };

  const columns: Column<FollowUpItem>[] = [
    {
      id: "date",
      header: t("columns.date"),
      cell: (f) => (
        <span className="flex flex-wrap items-center gap-2">
          <span className="ltr-nums whitespace-nowrap">{formatShortDate(f.followUpDate, locale)}</span>
          {!f.booked && f.followUpDate < today ? <Badge tone="warning">{t("overdue")}</Badge> : null}
        </span>
      ),
    },
    {
      id: "patient",
      header: t("columns.patient"),
      cell: (f) => (
        <span className="flex flex-col">
          {can(P.Patients.View) ? (
            <Link href={`/patients/${f.patientId}`} className="font-medium text-primary-strong hover:underline">
              {f.patientName}
            </Link>
          ) : (
            <span className="font-medium">{f.patientName}</span>
          )}
          <span className="ltr-nums text-xs text-fg-subtle">{f.patientMrn}</span>
        </span>
      ),
    },
    { id: "doctor", header: t("columns.doctor"), cell: (f) => f.doctorName },
    {
      id: "status",
      header: t("columns.status"),
      cell: (f) =>
        f.booked ? (
          <span className="text-fg-muted">
            {t("bookedFor")} <span className="ltr-nums">{f.nextAppointmentUtc ? formatDateTime(f.nextAppointmentUtc, timeZone, locale) : ""}</span>
          </span>
        ) : (
          <span className="text-fg">{t("notBooked")}</span>
        ),
    },
    ...(canBook
      ? [
          {
            id: "actions",
            header: t("columns.actions"),
            cell: (f: FollowUpItem) =>
              f.booked ? null : (
                <Button
                  size="sm"
                  variant="secondary"
                  icon={<CalendarPlus className="size-4" aria-hidden="true" />}
                  onClick={() => setBooking(f)}
                  aria-label={t("bookFor", { patient: f.patientName })}
                >
                  {t("book")}
                </Button>
              ),
          },
        ]
      : []),
  ];

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} />
      <Card>
        <DataTable
          caption={t("title")}
          columns={columns}
          rows={ready ? list.data?.items : []}
          rowKey={(f) => `${f.patientId}:${f.followUpDate}:${f.doctorId}`}
          loading={list.loading}
          error={list.error ? (isApiError(list.error) ? list.error.message : t("loadFailed")) : null}
          onRetry={list.reload}
          meta={list.data?.meta}
          onPageChange={setPage}
          toolbar={
            <div className="flex flex-wrap items-end gap-3">
              <Input label={t("filters.from")} type="date" value={from} max={to || undefined} onChange={(e) => resetPage(setFrom)(e.target.value)} className="w-40" />
              <Input
                label={t("filters.to")}
                type="date"
                value={to}
                min={from || undefined}
                onChange={(e) => resetPage(setTo)(e.target.value)}
                error={rangeInvalid ? t("filters.rangeInvalid") : undefined}
                className="w-40"
              />
              <Select
                label={t("filters.doctor")}
                value={doctorId}
                onChange={(e) => resetPage(setDoctorId)(e.target.value)}
                placeholder={t("filters.allDoctors")}
                options={(doctors.data ?? []).map((d) => ({ value: d.id, label: d.displayNameAr }))}
                className="w-48"
              />
              <Select
                label={t("filters.status")}
                value={booked}
                onChange={(e) => resetPage(setBooked)(e.target.value as BookedFilter)}
                options={[
                  { value: "false", label: t("filters.notBooked") },
                  { value: "true", label: t("filters.booked") },
                  { value: "", label: t("filters.all") },
                ]}
                className="w-40"
              />
            </div>
          }
          empty={<EmptyState icon={<CalendarClock className="size-6" />} title={t("emptyTitle")} description={t("emptyDescription")} />}
        />
      </Card>
      <p className="mt-3 text-xs text-fg-subtle">{t("privacyNote")}</p>

      {booking ? (
        <BookingDialog
          open
          mode="booking"
          initialDate={booking.followUpDate >= today ? booking.followUpDate : today}
          initialPatient={{ id: booking.patientId, fullName: booking.patientName, medicalRecordNumber: booking.patientMrn, mobileNumber: "" }}
          initialDoctorId={booking.doctorId}
          doctors={doctors.data ?? []}
          services={services.data ?? []}
          onClose={() => setBooking(null)}
          onBooked={() => {
            setBooking(null);
            toast({ tone: "success", title: t("booked") });
            list.reload();
          }}
        />
      ) : null}
    </>
  );
}
