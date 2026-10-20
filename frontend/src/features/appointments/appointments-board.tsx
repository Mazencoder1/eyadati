"use client";

import { CalendarPlus, ChevronLeft, ChevronRight, DoorOpen, RefreshCw } from "lucide-react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { useMemo, useState } from "react";
import { PageHeader, RequirePermission } from "@/components/app/page";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/ui/data-table";
import { Select } from "@/components/ui/field";
import { useToast } from "@/components/ui/toast";
import { useClinic } from "@/features/clinic/clinic-context";
import { isApiError } from "@/lib/api/client";
import type { Appointment, AppointmentStatus } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { addDays, formatGregorianDate, formatHijriDate, formatTime, isIsoDate, todayInZone } from "@/lib/datetime";
import { useNow, useQuery } from "@/lib/hooks/use-query";
import { toLocalMobile } from "@/lib/validation/saudi";
import { AppointmentRowActions, availableTransitions, useAppointmentActions } from "./appointment-actions";
import { BookingDialog } from "./booking-dialog";
import { AppointmentStatusBadge } from "./status";

const STATUSES: AppointmentStatus[] = ["Scheduled", "Confirmed", "CheckedIn", "Completed", "Cancelled", "NoShow"];

export function AppointmentsBoard() {
  return (
    <RequirePermission anyOf={[P.Appointments.View]}>
      <Board />
    </RequirePermission>
  );
}

function Board() {
  const t = useTranslations("appointments");
  const locale = useLocale();
  const toast = useToast();
  const { api, can } = useAuth();
  const { timeZone, allowedBranches, branchName } = useClinic();

  const [date, setDate] = useState(() => todayInZone(timeZone));
  const [doctorId, setDoctorId] = useState("");
  const [branchId, setBranchId] = useState("");
  const [status, setStatus] = useState<AppointmentStatus | "">("");
  const [page, setPage] = useState(1);
  const [booking, setBooking] = useState<"booking" | "walk-in" | null>(null);

  const validDate = isIsoDate(date);
  const list = useQuery(validDate ? `appts:${date}:${doctorId}:${branchId}:${status}:${page}` : null, (signal) =>
    api.appointments.list(
      { date, doctorId: doctorId || undefined, branchId: branchId || undefined, status: status || undefined, page, pageSize: 100 },
      signal,
    ),
  );
  const doctors = useQuery("doctors:active", async (signal) => (await api.doctors.list({ activeOnly: true, pageSize: 100 }, signal)).items);
  const services = useQuery("services:active", () => api.services.list(true));

  const counts = useMemo(() => {
    const map = new Map<AppointmentStatus, number>();
    for (const a of list.data?.items ?? []) map.set(a.status, (map.get(a.status) ?? 0) + 1);
    return map;
  }, [list.data]);

  const actions = useAppointmentActions({
    onUpdated: (updated) =>
      list.setData((current) =>
        current ? { ...current, items: current.items.map((a) => (a.id === updated.id ? updated : a)) } : current,
      ),
    onConflict: list.reload,
  });

  const canCreate = can(P.Appointments.Create);
  const now = useNow();

  const columns: Column<Appointment>[] = [
    {
      id: "time",
      header: t("columns.time"),
      cell: (a) => (
        <span className="ltr-nums font-semibold">
          {a.source === "WalkIn" ? (a.tokenNumber ?? "—") : formatTime(a.startUtc, timeZone, locale)}
        </span>
      ),
    },
    {
      id: "patient",
      header: t("columns.patient"),
      cell: (a) => (
        <div className="min-w-0">
          {can(P.Patients.View) ? (
            <Link href={`/patients/${a.patientId}`} className="font-medium text-primary-strong hover:underline">
              {a.patientName}
            </Link>
          ) : (
            <span className="font-medium">{a.patientName}</span>
          )}
          <div className="text-xs text-fg-muted">
            <span className="ltr-nums">{a.patientMrn}</span> · <span className="ltr-nums">{toLocalMobile(a.patientMobile)}</span>
          </div>
        </div>
      ),
    },
    { id: "doctor", header: t("columns.doctor"), cell: (a) => a.doctorName },
    { id: "service", header: t("columns.service"), cell: (a) => a.serviceName ?? "—", hideable: true },
    {
      id: "source",
      header: t("columns.source"),
      cell: (a) => t(`booking.sources.${a.source}`),
      hideable: true,
      defaultHidden: true,
    },
    {
      id: "branch",
      header: t("columns.branch"),
      cell: (a) => branchName(a.branchId),
      hideable: true,
      defaultHidden: allowedBranches.length <= 1,
    },
    {
      id: "status",
      header: t("columns.status"),
      cell: (a) => (
        <div className="flex flex-col items-end gap-0.5 md:items-start">
          <AppointmentStatusBadge status={a.status} />
          {a.cancellationReason ? <span className="text-xs text-fg-subtle">{a.cancellationReason}</span> : null}
        </div>
      ),
    },
  ];

  const rowActions = (a: Appointment) => (
    <AppointmentRowActions appointment={a} actions={actions} options={availableTransitions(a, can, now)} />
  );

  return (
    <>
      <PageHeader
        title={t("title")}
        description={t("subtitle")}
        actions={
          canCreate ? (
            <>
              <Button variant="outline" icon={<DoorOpen className="size-4" aria-hidden="true" />} onClick={() => setBooking("walk-in")}>
                {t("walkIn")}
              </Button>
              <Button icon={<CalendarPlus className="size-4" aria-hidden="true" />} onClick={() => setBooking("booking")}>
                {t("book")}
              </Button>
            </>
          ) : null
        }
      />

      <Card>
        <div className="flex flex-col gap-4 border-b border-border px-4 py-4 sm:px-6">
          <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
            <div className="flex items-center gap-2">
              <Button variant="outline" size="icon" onClick={() => { setDate((d) => addDays(d, -1)); setPage(1); }} aria-label={t("previousDay")}>
                <ChevronRight className="size-5 ltr:rotate-180" aria-hidden="true" />
              </Button>
              <label htmlFor="board-date" className="sr-only">
                {t("date")}
              </label>
              <input
                id="board-date"
                type="date"
                value={date}
                onChange={(e) => { setDate(e.target.value); setPage(1); }}
                className="h-10 min-w-0 rounded-control border border-border-input bg-surface px-3 text-sm"
              />
              <Button variant="outline" size="icon" onClick={() => { setDate((d) => addDays(d, 1)); setPage(1); }} aria-label={t("nextDay")}>
                <ChevronLeft className="size-5 ltr:rotate-180" aria-hidden="true" />
              </Button>
              <Button variant="ghost" size="sm" onClick={() => { setDate(todayInZone(timeZone)); setPage(1); }}>
                {t("today")}
              </Button>
            </div>
            {validDate ? (
              <div className="text-sm md:text-end" aria-live="polite">
                <p className="font-semibold text-fg">{formatGregorianDate(date, locale)}</p>
                <p className="text-fg-muted">{formatHijriDate(date, locale)}</p>
              </div>
            ) : null}
          </div>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
            <Select
              label={t("filters.doctor")}
              value={doctorId}
              onChange={(e) => { setDoctorId(e.target.value); setPage(1); }}
              placeholder={t("filters.allDoctors")}
              options={(doctors.data ?? []).map((d) => ({ value: d.id, label: d.displayNameAr }))}
            />
            {allowedBranches.length > 1 ? (
              <Select
                label={t("filters.branch")}
                value={branchId}
                onChange={(e) => { setBranchId(e.target.value); setPage(1); }}
                placeholder={t("filters.allBranches")}
                options={allowedBranches.map((b) => ({ value: b.id, label: b.name }))}
              />
            ) : null}
            <Select
              label={t("filters.status")}
              value={status}
              onChange={(e) => { setStatus(e.target.value as AppointmentStatus | ""); setPage(1); }}
              placeholder={t("filters.allStatuses")}
              options={STATUSES.map((s) => ({ value: s, label: t(`status.${s}`) }))}
            />
          </div>

          {list.data && list.data.items.length > 0 ? (
            <ul className="flex flex-wrap gap-2" aria-label={t("summary")}>
              {STATUSES.filter((s) => counts.get(s)).map((s) => (
                <li key={s} className="flex items-center gap-1.5 text-sm text-fg-muted">
                  <AppointmentStatusBadge status={s} />
                  <span className="ltr-nums font-semibold text-fg">{counts.get(s)}</span>
                </li>
              ))}
            </ul>
          ) : null}
        </div>

        <DataTable
          caption={t("tableCaption", { date })}
          columns={columns}
          rows={list.data?.items}
          rowKey={(a) => a.id}
          loading={list.loading}
          error={list.error ? (isApiError(list.error) ? list.error.message : t("loadFailed")) : null}
          onRetry={list.reload}
          meta={list.data && list.data.meta.totalPages > 1 ? list.data.meta : undefined}
          onPageChange={setPage}
          rowActions={rowActions}
          toolbar={
            <Button variant="ghost" size="sm" onClick={list.reload} icon={<RefreshCw className="size-4" aria-hidden="true" />}>
              {t("refresh")}
            </Button>
          }
          empty={
            <div className="px-4 py-12 text-center">
              <p className="font-semibold">{t("emptyTitle")}</p>
              <p className="mt-1 text-sm text-fg-muted">{t("emptyDescription")}</p>
            </div>
          }
        />
      </Card>

      {booking ? (
        <BookingDialog
          open
          mode={booking}
          initialDate={date}
          doctors={doctors.data ?? []}
          services={services.data ?? []}
          onClose={() => setBooking(null)}
          onBooked={(created) => {
            setBooking(null);
            toast({
              tone: "success",
              title: created.tokenNumber ? t("walkInCreated", { token: created.tokenNumber }) : t("booked"),
            });
            list.reload();
          }}
        />
      ) : null}

      {actions.dialogs}
    </>
  );
}
