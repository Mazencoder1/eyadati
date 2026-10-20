/**
 * Pure logic behind the "Today" page: which view a user gets, and the numbers computed from today's appointments.
 * No React, no API — unit tested in today-model.test.ts.
 *
 * View selection is UI gating only. Every list and action is still authorized by the API.
 */
import type { Appointment, AppointmentStatus, Guid, Paged } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";

// ------------------------------------------------------------------ view selection

/** Which question the page answers first. One page per user; sections are reordered, never stacked as dashboards. */
export type TodayFocus = "reception" | "doctor" | "manager" | "viewer";

export interface TodayView {
  /** Holds Appointments.View — without it there is nothing to show. */
  canAccess: boolean;
  focus: TodayFocus;
  /** Book / walk-in buttons (Appointments.Create). */
  canBook: boolean;
  /** Per-doctor load table (managers/owners). */
  showDoctorLoad: boolean;
  /** Initial doctor scope: "own" = the user's own doctor profile (from /auth/me), "all" = every doctor. */
  initialScope: "own" | "all";
}

export function selectTodayView(can: (permission: string) => boolean, ownDoctorId: Guid | null | undefined): TodayView {
  const canAccess = can(P.Appointments.View);
  const frontDesk = can(P.Appointments.Create);
  // Doctor template: documents visits. Nurses (Visits.View only) and reception do not.
  const clinician = can(P.Visits.Create);
  const manager = can(P.Staff.View) || can(P.Settings.Manage) || can(P.Reports.View);

  let focus: TodayFocus;
  if (frontDesk || (can(P.Appointments.CheckIn) && !clinician)) focus = "reception";
  else if (clinician) focus = "doctor";
  else if (manager) focus = "manager";
  else focus = "viewer";

  const initialScope: TodayView["initialScope"] = focus === "doctor" && ownDoctorId ? "own" : "all";

  return { canAccess, focus, canBook: frontDesk, showDoctorLoad: manager, initialScope };
}

// ------------------------------------------------------------------ data loading

export interface DayAppointments {
  items: Appointment[];
  totalCount: number;
  /** More appointments exist than were loaded (page cap reached) — figures are incomplete. */
  truncated: boolean;
}

export const PAGE_SIZE = 100; // API maximum
export const MAX_PAGES = 5;

/** Loads every page of a day (up to `maxPages` × 100 rows) and reports truncation instead of hiding it. */
export async function collectPages(
  fetchPage: (page: number) => Promise<Paged<Appointment>>,
  maxPages = MAX_PAGES,
): Promise<DayAppointments> {
  const first = await fetchPage(1);
  const items = [...first.items];
  const lastPage = Math.min(first.meta.totalPages, maxPages);
  for (let page = 2; page <= lastPage; page++) {
    items.push(...(await fetchPage(page)).items);
  }
  const seen = new Set<string>();
  const unique = items.filter((a) => (seen.has(a.id) ? false : (seen.add(a.id), true)));
  return { items: unique, totalCount: first.meta.totalCount, truncated: first.meta.totalPages > maxPages };
}

// ------------------------------------------------------------------ metrics

const OPEN: AppointmentStatus[] = ["Scheduled", "Confirmed"];
const isOpen = (a: Appointment) => OPEN.includes(a.status);
const byStart = (a: Appointment, b: Appointment) =>
  a.startUtc.localeCompare(b.startUtc) || (a.tokenNumber ?? "").localeCompare(b.tokenNumber ?? "", "en", { numeric: true });

/** When the patient arrived: the check-in time, or for a walk-in not yet checked in its registration (start) time. */
export function arrivedAt(a: Appointment): string {
  return a.checkedInAtUtc ?? a.startUtc;
}

const byArrival = (a: Appointment, b: Appointment) =>
  arrivedAt(a).localeCompare(arrivedAt(b)) || (a.tokenNumber ?? "").localeCompare(b.tokenNumber ?? "", "en", { numeric: true });

/** Whole minutes the patient has been waiting since arrival (never negative). */
export function waitingMinutes(a: Appointment, now: number): number {
  return Math.max(0, Math.floor((now - new Date(arrivedAt(a)).getTime()) / 60_000));
}

/**
 * In the clinic now: checked-in patients plus walk-ins who have a queue token but are not checked in yet.
 * Ordered by arrival: check-in time when known, otherwise the walk-in's registration time.
 */
export function isInClinic(a: Appointment): boolean {
  return a.status === "CheckedIn" || (a.source === "WalkIn" && isOpen(a));
}

export interface UpcomingItem {
  appointment: Appointment;
  /** Start time has passed and the patient has not arrived. */
  late: boolean;
}

export interface TodayMetrics {
  byStatus: Record<AppointmentStatus, number>;
  /** All non-cancelled appointments of the day. */
  booked: number;
  /** Patients in the clinic waiting (see isInClinic). */
  waiting: number;
  /** Booked patients still expected, whose time has not come yet. */
  expected: number;
  /** Booked patients whose time has passed without arrival — call them or mark no-show. */
  late: number;
  completed: number;
  noShow: number;
  cancelled: number;
  queue: Appointment[];
  upcoming: UpcomingItem[];
}

export function computeTodayMetrics(items: Appointment[], now: number): TodayMetrics {
  const byStatus: Record<AppointmentStatus, number> = {
    Scheduled: 0,
    Confirmed: 0,
    CheckedIn: 0,
    Completed: 0,
    Cancelled: 0,
    NoShow: 0,
  };
  for (const a of items) byStatus[a.status]++;

  const queue = items.filter(isInClinic).sort(byArrival);
  const upcoming = items
    .filter((a) => a.source !== "WalkIn" && isOpen(a))
    .sort(byStart)
    .map((appointment) => ({ appointment, late: new Date(appointment.startUtc).getTime() < now }));
  const late = upcoming.filter((u) => u.late).length;

  return {
    byStatus,
    booked: items.length - byStatus.Cancelled,
    waiting: queue.length,
    expected: upcoming.length - late,
    late,
    completed: byStatus.Completed,
    noShow: byStatus.NoShow,
    cancelled: byStatus.Cancelled,
    queue,
    upcoming,
  };
}

export interface DoctorLoad {
  doctorId: Guid;
  doctorName: string;
  /** Non-cancelled appointments today. */
  booked: number;
  completed: number;
  waiting: number;
  /** Still to come (booked, not arrived — including late). */
  remaining: number;
  noShow: number;
}

/** Per-doctor workload for today, busiest outstanding work (waiting + remaining) first. */
export function buildDoctorLoad(items: Appointment[]): DoctorLoad[] {
  const map = new Map<Guid, DoctorLoad>();
  for (const a of items) {
    const row =
      map.get(a.doctorId) ??
      { doctorId: a.doctorId, doctorName: a.doctorName, booked: 0, completed: 0, waiting: 0, remaining: 0, noShow: 0 };
    map.set(a.doctorId, row);
    if (a.status === "Cancelled") continue;
    row.booked++;
    if (a.status === "Completed") row.completed++;
    else if (a.status === "NoShow") row.noShow++;
    else if (isInClinic(a)) row.waiting++;
    else if (isOpen(a)) row.remaining++;
  }
  return [...map.values()].sort(
    (x, y) => y.waiting + y.remaining - (x.waiting + x.remaining) || x.doctorName.localeCompare(y.doctorName, "ar"),
  );
}
