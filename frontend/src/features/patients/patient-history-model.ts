/**
 * Pure logic of the patient's clinical timeline: appointments, visits (with diagnosis when the caller may see it)
 * and prescriptions merged into one newest-first list, grouped by clinic-local day. Unit tested in
 * patient-history-model.test.ts. Each source is only present when the caller's permissions allowed loading it.
 */
import type { Appointment, Paged, PrescriptionListItem, VisitListItem } from "@/lib/api/types";
import { isoDateInZone } from "@/lib/datetime";

export type HistoryEntry =
  | { kind: "visit"; at: string; visit: VisitListItem; appointment?: Appointment }
  | { kind: "prescription"; at: string; prescription: PrescriptionListItem }
  | { kind: "appointment"; at: string; appointment: Appointment };

export interface HistoryDay {
  date: string;
  entries: HistoryEntry[];
}

/**
 * An appointment that led to a visit is shown once, as the visit (the visit carries the clinical meaning; repeating
 * the appointment would duplicate the same encounter).
 */
export function buildHistory(
  appointments: Appointment[] | undefined,
  visits: VisitListItem[] | undefined,
  prescriptions: PrescriptionListItem[] | undefined,
  timeZone: string,
): HistoryDay[] {
  const byAppointment = new Map((appointments ?? []).map((a) => [a.id, a]));
  const covered = new Set((visits ?? []).map((v) => v.appointmentId).filter(Boolean));

  const entries: HistoryEntry[] = [
    ...(visits ?? []).map((v): HistoryEntry => ({
      kind: "visit",
      at: v.visitedAtUtc,
      visit: v,
      appointment: v.appointmentId ? byAppointment.get(v.appointmentId) : undefined,
    })),
    ...(prescriptions ?? []).map((p): HistoryEntry => ({ kind: "prescription", at: p.issuedAtUtc, prescription: p })),
    ...(appointments ?? [])
      .filter((a) => !covered.has(a.id))
      .map((a): HistoryEntry => ({ kind: "appointment", at: a.startUtc, appointment: a })),
  ];

  // Newest first; on the same instant a visit comes before the prescriptions written in it.
  const rank = { visit: 0, prescription: 1, appointment: 2 } as const;
  entries.sort((x, y) => y.at.localeCompare(x.at) || rank[x.kind] - rank[y.kind]);

  const days: HistoryDay[] = [];
  for (const entry of entries) {
    const date = isoDateInZone(new Date(entry.at), timeZone);
    const last = days[days.length - 1];
    if (last?.date === date) last.entries.push(entry);
    else days.push({ date, entries: [entry] });
  }
  return days;
}

/**
 * The soonest follow-up date (YYYY-MM-DD) recorded on a visit that is today or later, if any. Follow-up dates are
 * clinic-local calendar dates, so plain string comparison is correct.
 */
export function nextFollowUp(visits: VisitListItem[] | undefined, today: string): string | null {
  const upcoming = (visits ?? [])
    .map((v) => v.followUpDate)
    .filter((d): d is string => !!d && d >= today)
    .sort();
  return upcoming[0] ?? null;
}

/**
 * The API lists a patient's appointments oldest first (no sort option), so the most recent ones are on the last
 * pages. Loads the last two pages (up to 200 appointments) and reports whether older ones were left out.
 */
export async function latestAppointments(
  fetchPage: (page: number, pageSize: number) => Promise<Paged<Appointment>>,
  pageSize = 100,
): Promise<{ items: Appointment[]; truncated: boolean }> {
  const first = await fetchPage(1, pageSize);
  const pages = first.meta.totalPages;
  if (pages <= 2) {
    const second = pages === 2 ? (await fetchPage(2, pageSize)).items : [];
    return { items: [...first.items, ...second], truncated: false };
  }
  const [before, last] = await Promise.all([fetchPage(pages - 1, pageSize), fetchPage(pages, pageSize)]);
  return { items: [...before.items, ...last.items], truncated: true };
}
