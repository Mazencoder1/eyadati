"use client";

import { CalendarPlus, DoorOpen, RefreshCw, ShieldOff, Stethoscope } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { PageHeader } from "@/components/app/page";
import { visibleNavItems } from "@/components/app/nav-items";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, ButtonLink } from "@/components/ui/button";
import { Card, CardHeader } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/ui/data-table";
import { EmptyState } from "@/components/ui/empty-state";
import { Select } from "@/components/ui/field";
import { Skeleton } from "@/components/ui/spinner";
import { useToast } from "@/components/ui/toast";
import { AppointmentRowActions, availableTransitions, useAppointmentActions, type AppointmentActions } from "@/features/appointments/appointment-actions";
import { BookingDialog } from "@/features/appointments/booking-dialog";
import { AppointmentStatusBadge } from "@/features/appointments/status";
import { useClinic } from "@/features/clinic/clinic-context";
import { useOpenVisit } from "@/features/visits/use-open-visit";
import { isApiError } from "@/lib/api/client";
import type { Appointment } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import { useAuth } from "@/lib/auth/session";
import { formatGregorianDate, formatHijriDate, formatTime, todayInZone } from "@/lib/datetime";
import { useNow, useQuery } from "@/lib/hooks/use-query";
import { HOME_PATH } from "@/lib/routes";
import { toLocalMobile } from "@/lib/validation/saudi";
import { cn } from "@/lib/utils";
import {
  buildDoctorLoad,
  collectPages,
  computeTodayMetrics,
  PAGE_SIZE,
  selectTodayView,
  waitingMinutes,
  type DoctorLoad,
  type UpcomingItem,
} from "./today-model";

const UPCOMING_LIMIT = 10;
const AUTO_REFRESH_MS = 60_000;

/** Entry point: users without Appointments.View are sent to the first section they can open. */
export function TodayPage() {
  const t = useTranslations("shell");
  const router = useRouter();
  const { can, canAny } = useAuth();
  const allowed = can(P.Appointments.View);
  const fallback = visibleNavItems(canAny).find((item) => item.href !== HOME_PATH)?.href;

  useEffect(() => {
    if (!allowed && fallback) router.replace(fallback);
  }, [allowed, fallback, router]);

  if (!allowed) {
    return fallback ? null : (
      <EmptyState
        icon={<ShieldOff className="size-6" />}
        title={t("forbiddenTitle")}
        description={t("forbiddenDescription")}
        className="py-24"
      />
    );
  }
  return <Today />;
}

function Today() {
  const t = useTranslations("today");
  const tAppt = useTranslations("appointments");
  const locale = useLocale();
  const toast = useToast();
  const { api, can } = useAuth();
  const { timeZone } = useClinic();
  const now = useNow(30_000);
  const date = todayInZone(timeZone, new Date(now));
  // Own doctor profile comes from /auth/me (server-side, from the session). Wait for it so a doctor's first load is
  // already scoped to their own patients.
  const { ownDoctorId } = useClinic();
  const view = selectTodayView(can, ownDoctorId);
  const meLoading = ownDoctorId === undefined;

  const [chosen, setChosen] = useState<string | null>(null);
  const doctorId = chosen ?? (view.initialScope === "own" ? (ownDoctorId ?? "") : "");

  const day = useQuery(meLoading ? null : `today:${date}:${doctorId}`, async (signal) => ({
    ...(await collectPages((page) =>
      api.appointments.list({ date, doctorId: doctorId || undefined, page, pageSize: PAGE_SIZE }, signal),
    )),
    loadedAt: Date.now(),
  }));
  const doctors = useQuery("doctors:active", async (signal) => (await api.doctors.list({ activeOnly: true, pageSize: 100 }, signal)).items);
  const services = useQuery(view.canBook ? "services:active" : null, () => api.services.list(true));

  // Keep the queue current while the page stays open (other desks check patients in too).
  const reload = day.reload;
  useEffect(() => {
    const id = setInterval(() => {
      if (document.visibilityState === "visible") reload();
    }, AUTO_REFRESH_MS);
    return () => clearInterval(id);
  }, [reload]);

  const lastLoaded = day.data?.loadedAt;

  const items = useMemo(() => day.data?.items ?? [], [day.data]);
  const metrics = useMemo(() => computeTodayMetrics(items, now), [items, now]);
  const load = useMemo(() => buildDoctorLoad(items), [items]);

  const actions = useAppointmentActions({
    onUpdated: (updated) =>
      day.setData((current) =>
        current ? { ...current, items: current.items.map((a) => (a.id === updated.id ? updated : a)) } : current,
      ),
    onConflict: day.reload,
  });
  const [booking, setBooking] = useState<"booking" | "walk-in" | null>(null);

  const showLoad = view.showDoctorLoad && !doctorId;
  const showDoctorColumn = !doctorId;
  const firstLoad = day.data === undefined && !day.error;

  const summary = (
    <Summary
      loading={firstLoad}
      waiting={metrics.waiting}
      late={metrics.late}
      expected={metrics.expected}
      completed={metrics.completed}
      noShow={metrics.noShow}
      booked={metrics.booked}
      cancelled={metrics.cancelled}
    />
  );
  const lists = (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-5">
      <QueueCard
        className="lg:col-span-3"
        loading={firstLoad}
        queue={metrics.queue}
        actions={actions}
        now={now}
        showDoctor={showDoctorColumn}
        ownDoctorId={ownDoctorId ?? null}
      />
      <UpcomingCard
        className="lg:col-span-2"
        loading={firstLoad}
        upcoming={metrics.upcoming}
        actions={actions}
        now={now}
        showDoctor={showDoctorColumn}
      />
    </div>
  );
  const loadCard = showLoad && (firstLoad || load.length > 0) ? <DoctorLoadCard rows={load} loading={firstLoad} /> : null;

  return (
    <>
      <PageHeader
        title={t("title")}
        description={
          <>
            <span className="block font-medium text-fg">{formatGregorianDate(date, locale)}</span>
            <span className="block">{formatHijriDate(date, locale)}</span>
          </>
        }
        actions={
          view.canBook ? (
            <>
              <Button variant="outline" icon={<DoorOpen className="size-4" aria-hidden="true" />} onClick={() => setBooking("walk-in")}>
                {tAppt("walkIn")}
              </Button>
              <Button icon={<CalendarPlus className="size-4" aria-hidden="true" />} onClick={() => setBooking("booking")}>
                {tAppt("book")}
              </Button>
            </>
          ) : null
        }
      />

      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
          <Select
            className="w-full sm:max-w-xs"
            label={t("doctorScope")}
            value={doctorId}
            disabled={doctors.data === undefined || meLoading}
            onChange={(e) => setChosen(e.target.value)}
            placeholder={t("allDoctors")}
            options={(doctors.data ?? []).map((d) => ({
              value: d.id,
              label: d.id === ownDoctorId ? t("ownDoctor", { name: d.displayNameAr }) : d.displayNameAr,
            }))}
          />
          <div className="flex items-center gap-3">
            {lastLoaded ? (
              <p className="text-xs text-fg-subtle" aria-live="polite">
                {t("updatedAt", { time: formatTime(new Date(lastLoaded).toISOString(), timeZone, locale) })}
              </p>
            ) : null}
            <Button
              variant="ghost"
              size="sm"
              onClick={day.reload}
              loading={day.loading && day.data !== undefined}
              icon={<RefreshCw className="size-4" aria-hidden="true" />}
            >
              {tAppt("refresh")}
            </Button>
          </div>
        </div>

        {day.error ? (
          <Alert
            tone="danger"
            title={t("loadFailed")}
            action={
              <Button size="sm" variant="outline" onClick={day.reload}>
                {t("retry")}
              </Button>
            }
          >
            {isApiError(day.error) ? day.error.message : t("loadFailedHint")}
          </Alert>
        ) : null}

        {day.data?.truncated ? (
          <Alert tone="warning" title={t("truncatedTitle")}>
            {t("truncatedBody", { shown: day.data.items.length, total: day.data.totalCount })}{" "}
            <Link href="/appointments" className="font-semibold underline">
              {t("openBoard")}
            </Link>
          </Alert>
        ) : null}

        {day.error && !day.data ? null : view.focus === "manager" ? (
          <>
            {summary}
            {loadCard}
            {lists}
          </>
        ) : view.focus === "doctor" ? (
          // GD-12: a doctor's next action is the queue; the day's counters come after it.
          <>
            {lists}
            {summary}
            {loadCard}
          </>
        ) : (
          <>
            {summary}
            {lists}
            {loadCard}
          </>
        )}

        <div className="flex justify-center pt-2">
          <ButtonLink href="/appointments" variant="ghost" size="sm">
            {t("openBoard")}
          </ButtonLink>
        </div>
      </div>

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
              title: created.tokenNumber ? tAppt("walkInCreated", { token: created.tokenNumber }) : tAppt("booked"),
            });
            day.reload();
          }}
        />
      ) : null}
      {actions.dialogs}
    </>
  );
}

// ------------------------------------------------------------------ summary

function Summary(props: {
  loading: boolean;
  waiting: number;
  late: number;
  expected: number;
  completed: number;
  noShow: number;
  booked: number;
  cancelled: number;
}) {
  const t = useTranslations("today.summary");
  const figures: { key: string; value: number; label: string; hint: string; attention?: boolean }[] = [
    { key: "waiting", value: props.waiting, label: t("waiting"), hint: t("waitingHint") },
    { key: "late", value: props.late, label: t("late"), hint: t("lateHint"), attention: props.late > 0 },
    { key: "expected", value: props.expected, label: t("expected"), hint: t("expectedHint") },
    { key: "completed", value: props.completed, label: t("completed"), hint: t("completedHint") },
    { key: "noShow", value: props.noShow, label: t("noShow"), hint: t("noShowHint") },
  ];
  return (
    <Card aria-labelledby="today-summary-title">
      <h2 id="today-summary-title" className="sr-only">
        {t("title")}
      </h2>
      <dl className="grid grid-cols-2 divide-border sm:grid-cols-5 sm:divide-x">
        {figures.map((f) => (
          <div key={f.key} className="flex flex-col gap-0.5 px-4 py-3 sm:px-5 sm:py-4">
            <dt className="order-2 text-sm font-medium text-fg">{f.label}</dt>
            <dd className={cn("order-1 text-2xl font-semibold ltr-nums self-start", f.attention ? "text-warning-strong" : "text-fg")}>
              {props.loading ? <Skeleton className="h-8 w-10" /> : f.value}
            </dd>
            <dd className="order-3 text-xs text-fg-subtle">{f.hint}</dd>
          </div>
        ))}
      </dl>
      <div className="border-t border-border px-4 py-2.5 text-xs text-fg-muted sm:px-5">
        {props.loading ? <Skeleton className="h-4 w-48" /> : t("totals", { booked: props.booked, cancelled: props.cancelled })}
      </div>
    </Card>
  );
}

// ------------------------------------------------------------------ lists

function PatientName({ appointment }: { appointment: Appointment }) {
  const { can } = useAuth();
  return can(P.Patients.View) ? (
    <Link href={`/patients/${appointment.patientId}`} className="font-semibold text-primary-strong hover:underline">
      {appointment.patientName}
    </Link>
  ) : (
    <span className="font-semibold text-fg">{appointment.patientName}</span>
  );
}

function ListSkeleton() {
  return (
    <ul className="divide-y divide-border" aria-hidden="true">
      {Array.from({ length: 3 }, (_, i) => (
        <li key={i} className="px-4 py-4 sm:px-6">
          <Skeleton className="h-5 w-2/3" />
          <Skeleton className="mt-2 h-4 w-1/3" />
        </li>
      ))}
    </ul>
  );
}

function RowShell({
  lead,
  children,
  actions,
}: {
  lead: ReactNode;
  children: ReactNode;
  actions: ReactNode;
}) {
  return (
    <li className="flex flex-col gap-3 px-4 py-3.5 @lg:flex-row @lg:items-center @lg:gap-4 sm:px-6">
      <div className="flex min-w-0 flex-1 items-start gap-4">
        <div className="w-14 shrink-0">{lead}</div>
        <div className="min-w-0 flex-1">{children}</div>
      </div>
      {actions ? <div className="flex flex-wrap items-center justify-end gap-2">{actions}</div> : null}
    </li>
  );
}

function QueueCard({
  queue,
  loading,
  actions,
  now,
  showDoctor,
  ownDoctorId,
  className,
}: {
  queue: Appointment[];
  loading: boolean;
  actions: AppointmentActions;
  now: number;
  showDoctor: boolean;
  /** The signed-in doctor's profile: their own checked-in patients get "open visit" as the main action. */
  ownDoctorId: string | null;
  className?: string;
}) {
  const t = useTranslations("today.queue");
  const locale = useLocale();
  const { can } = useAuth();
  const { timeZone } = useClinic();
  const visit = useOpenVisit();
  const canDocument = can(P.Visits.Create) && can(P.MedicalRecords.Edit);

  return (
    <Card className={className} aria-labelledby="today-queue-title">
      <CardHeader
        title={<span id="today-queue-title">{t("title")}</span>}
        description={t("description")}
      />
      {loading ? (
        <ListSkeleton />
      ) : queue.length === 0 ? (
        <EmptyState title={t("emptyTitle")} description={t("emptyDescription")} className="py-10" />
      ) : (
        <ol className="@container divide-y divide-border">
          {queue.map((a, i) => {
            const time = formatTime(a.startUtc, timeZone, locale);
            return (
              <RowShell
                key={a.id}
                lead={
                  <div className="flex flex-col">
                    <span className="text-xs text-fg-subtle">{t("position", { n: i + 1 })}</span>
                    <span className="ltr-nums text-lg font-bold text-fg">{a.tokenNumber ?? time}</span>
                  </div>
                }
                actions={
                  canDocument && a.status === "CheckedIn" && a.doctorId === ownDoctorId ? (
                    // The doctor's next step is the visit itself; completing the appointment happens with the visit.
                    <Button
                      size="sm"
                      loading={visit.busyId === a.id}
                      icon={<Stethoscope className="size-4" aria-hidden="true" />}
                      onClick={() => void visit.open(a)}
                    >
                      {t("openVisit")}
                    </Button>
                  ) : (
                    <AppointmentRowActions appointment={a} actions={actions} options={availableTransitions(a, can, now)} />
                  )
                }
              >
                <PatientName appointment={a} />
                <p className="mt-0.5 text-sm text-fg-muted">
                  {a.source === "WalkIn" ? t("walkInSince", { time }) : t("bookedFor", { time })}
                  {showDoctor ? <> — <span className="whitespace-nowrap">{a.doctorName}</span></> : null}
                  {a.status === "CheckedIn" ? (
                    <> — <span className="whitespace-nowrap">{t("waitingFor", { minutes: waitingMinutes(a, now) })}</span></>
                  ) : null}
                </p>
                <div className="mt-1.5 flex flex-wrap items-center gap-2">
                  {a.status === "CheckedIn" ? (
                    <AppointmentStatusBadge status={a.status} />
                  ) : (
                    <Badge tone="info">{t("notCheckedIn")}</Badge>
                  )}
                  {a.serviceName ? <span className="text-xs text-fg-subtle">{a.serviceName}</span> : null}
                </div>
              </RowShell>
            );
          })}
        </ol>
      )}
    </Card>
  );
}

function UpcomingCard({
  upcoming,
  loading,
  actions,
  now,
  showDoctor,
  className,
}: {
  upcoming: UpcomingItem[];
  loading: boolean;
  actions: AppointmentActions;
  now: number;
  showDoctor: boolean;
  className?: string;
}) {
  const t = useTranslations("today.upcoming");
  const tRoot = useTranslations("today");
  const locale = useLocale();
  const { can } = useAuth();
  const { timeZone } = useClinic();
  const shown = upcoming.slice(0, UPCOMING_LIMIT);
  const hidden = upcoming.length - shown.length;

  return (
    <Card className={className} aria-labelledby="today-upcoming-title">
      <CardHeader title={<span id="today-upcoming-title">{t("title")}</span>} description={t("description")} />
      {loading ? (
        <ListSkeleton />
      ) : upcoming.length === 0 ? (
        <EmptyState title={t("emptyTitle")} description={t("emptyDescription")} className="py-10" />
      ) : (
        <>
          <ul className="@container divide-y divide-border">
            {shown.map(({ appointment: a, late }) => (
              <RowShell
                key={a.id}
                lead={<span className="ltr-nums text-base font-semibold text-fg">{formatTime(a.startUtc, timeZone, locale)}</span>}
                actions={<AppointmentRowActions appointment={a} actions={actions} options={availableTransitions(a, can, now)} />}
              >
                <PatientName appointment={a} />
                <p className="mt-0.5 text-sm text-fg-muted">
                  <span className="ltr-nums">{toLocalMobile(a.patientMobile)}</span>
                  {showDoctor ? <> — <span className="whitespace-nowrap">{a.doctorName}</span></> : null}
                </p>
                <div className="mt-1.5 flex flex-wrap items-center gap-2">
                  {late ? <Badge tone="warning">{t("late")}</Badge> : <AppointmentStatusBadge status={a.status} />}
                </div>
              </RowShell>
            ))}
          </ul>
          {hidden > 0 ? (
            <p className="border-t border-border px-4 py-3 text-sm text-fg-muted sm:px-6">
              {t("more", { count: hidden })}{" "}
              <Link href="/appointments" className="font-semibold text-primary-strong hover:underline">
                {tRoot("openBoard")}
              </Link>
            </p>
          ) : null}
        </>
      )}
    </Card>
  );
}

// ------------------------------------------------------------------ per-doctor load

function DoctorLoadCard({ rows, loading }: { rows: DoctorLoad[]; loading: boolean }) {
  const t = useTranslations("today.load");
  const num = (n: number) => <span className="ltr-nums">{n}</span>;
  const columns: Column<DoctorLoad>[] = [
    { id: "doctor", header: t("doctor"), cell: (r) => <span className="font-medium">{r.doctorName}</span> },
    { id: "booked", header: t("booked"), cell: (r) => num(r.booked) },
    { id: "completed", header: t("completed"), cell: (r) => num(r.completed) },
    { id: "waiting", header: t("waiting"), cell: (r) => num(r.waiting) },
    { id: "remaining", header: t("remaining"), cell: (r) => num(r.remaining) },
    { id: "noShow", header: t("noShow"), cell: (r) => num(r.noShow) },
  ];
  return (
    <Card aria-labelledby="today-load-title">
      <CardHeader title={<span id="today-load-title">{t("title")}</span>} description={t("description")} />
      <DataTable caption={t("title")} columns={columns} rows={loading ? undefined : rows} rowKey={(r) => r.doctorId} loading={loading} />
    </Card>
  );
}
