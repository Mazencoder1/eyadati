import { describe, expect, it } from "vitest";
import type { Appointment, AppointmentSource, AppointmentStatus, Paged } from "@/lib/api/types";
import { P } from "@/lib/auth/permissions";
import {
  buildDoctorLoad,
  collectPages,
  computeTodayMetrics,
  isInClinic,
  selectTodayView,
  waitingMinutes,
} from "./today-model";

// Permission sets of the backend's role templates (Domain/Authorization/SystemRoles.cs), reduced to what matters here.
const roles = {
  owner: [
    P.Appointments.View, P.Appointments.Create, P.Appointments.Edit, P.Appointments.Cancel, P.Appointments.CheckIn,
    P.Visits.Create, P.Staff.View, P.Settings.Manage, P.Reports.View,
  ],
  receptionist: [P.Appointments.View, P.Appointments.Create, P.Appointments.Edit, P.Appointments.Cancel, P.Appointments.CheckIn],
  doctor: [P.Appointments.View, P.Appointments.Edit, P.Appointments.CheckIn, P.Visits.Create, P.MedicalRecords.View],
  nurse: [P.Appointments.View, P.Appointments.CheckIn, P.Visits.View, P.MedicalRecords.View],
  staff: [P.Appointments.View],
  managerNoClinical: [P.Appointments.View, P.Staff.View, P.Reports.View],
  accountant: [P.Reports.View, P.Subscription.View],
};
const canOf = (perms: string[]) => (p: string) => perms.includes(p);

describe("selectTodayView", () => {
  it("receptionist: queue first, can book, no doctor load", () => {
    const v = selectTodayView(canOf(roles.receptionist), null);
    expect(v).toEqual({ canAccess: true, focus: "reception", canBook: true, showDoctorLoad: false, initialScope: "all" });
  });

  it("owner holding every role gets one reception-led page with the doctor load (not three dashboards)", () => {
    const v = selectTodayView(canOf(roles.owner), "doc-1");
    expect(v.focus).toBe("reception");
    expect(v.canBook).toBe(true);
    expect(v.showDoctorLoad).toBe(true);
    expect(v.initialScope).toBe("all");
  });

  it("doctor with a resolved profile starts on their own patients", () => {
    const v = selectTodayView(canOf(roles.doctor), "doc-1");
    expect(v).toMatchObject({ focus: "doctor", canBook: false, showDoctorLoad: false, initialScope: "own" });
  });

  it("a clinician without a doctor profile in this clinic sees all doctors (no client-side guessing)", () => {
    expect(selectTodayView(canOf(roles.doctor), null)).toMatchObject({ focus: "doctor", initialScope: "all" });
  });

  it("nurse (check-in, no visit documentation) works the queue for all doctors", () => {
    expect(selectTodayView(canOf(roles.nurse), null)).toMatchObject({ focus: "reception", canBook: false, initialScope: "all" });
  });

  it("manager without clinical permissions leads with the summary and load", () => {
    expect(selectTodayView(canOf(roles.managerNoClinical), null)).toMatchObject({
      focus: "manager",
      showDoctorLoad: true,
      canBook: false,
    });
  });

  it("read-only staff gets a viewer page; accountant has no access", () => {
    expect(selectTodayView(canOf(roles.staff), null).focus).toBe("viewer");
    expect(selectTodayView(canOf(roles.accountant), null).canAccess).toBe(false);
  });
});

// ------------------------------------------------------------------ fixtures

let seq = 0;
function appt(
  status: AppointmentStatus,
  start: string,
  opts: { source?: AppointmentSource; doctorId?: string; doctorName?: string; token?: string; checkedInAt?: string } = {},
): Appointment {
  seq++;
  return {
    id: `a${seq}`,
    branchId: "b1",
    patientId: `p${seq}`,
    patientName: `Patient ${seq}`,
    patientMrn: `P-${seq}`,
    patientMobile: "966500000000",
    doctorId: opts.doctorId ?? "d1",
    doctorName: opts.doctorName ?? "د. أ",
    startUtc: start,
    endUtc: start,
    status,
    source: opts.source ?? "Reception",
    tokenNumber: opts.token ?? null,
    rowVersion: "rv",
    checkedInAtUtc: opts.checkedInAt ?? null,
  };
}

const NOW = Date.parse("2026-10-06T09:00:00Z");

describe("computeTodayMetrics", () => {
  const items = [
    appt("Completed", "2026-10-06T06:00:00Z"),
    appt("CheckedIn", "2026-10-06T08:30:00Z"),
    appt("Scheduled", "2026-10-06T08:40:00Z", { source: "WalkIn", token: "T-02" }),
    appt("CheckedIn", "2026-10-06T08:10:00Z", { source: "WalkIn", token: "T-01" }),
    appt("Confirmed", "2026-10-06T08:45:00Z"), // late
    appt("Scheduled", "2026-10-06T10:00:00Z"), // expected
    appt("Scheduled", "2026-10-06T09:30:00Z"), // expected
    appt("NoShow", "2026-10-06T07:00:00Z"),
    appt("Cancelled", "2026-10-06T11:00:00Z"),
  ];
  const m = computeTodayMetrics(items, NOW);

  it("counts by status and the decision figures", () => {
    expect(m.byStatus).toEqual({ Scheduled: 3, Confirmed: 1, CheckedIn: 2, Completed: 1, Cancelled: 1, NoShow: 1 });
    expect(m.booked).toBe(8);
    expect(m.waiting).toBe(3);
    expect(m.late).toBe(1);
    expect(m.expected).toBe(2);
    expect(m.completed).toBe(1);
    expect(m.noShow).toBe(1);
    expect(m.cancelled).toBe(1);
  });

  it("queue holds checked-in patients and un-checked walk-ins, ordered by arrival/appointment time", () => {
    expect(m.queue.map((a) => a.tokenNumber ?? a.startUtc)).toEqual(["T-01", "2026-10-06T08:30:00Z", "T-02"]);
  });

  it("upcoming lists booked, not-arrived patients in time order and flags late ones", () => {
    expect(m.upcoming.map((u) => [u.appointment.startUtc, u.late])).toEqual([
      ["2026-10-06T08:45:00Z", true],
      ["2026-10-06T09:30:00Z", false],
      ["2026-10-06T10:00:00Z", false],
    ]);
  });

  it("orders walk-ins with the same start time by numeric token", () => {
    const same = "2026-10-06T08:00:00Z";
    const q = computeTodayMetrics(
      [appt("CheckedIn", same, { source: "WalkIn", token: "T-10" }), appt("CheckedIn", same, { source: "WalkIn", token: "T-9" })],
      NOW,
    ).queue;
    expect(q.map((a) => a.tokenNumber)).toEqual(["T-9", "T-10"]);
  });

  it("orders the queue by actual check-in time when the API provides it", () => {
    // Booked for 08:00 but arrived 08:50; booked for 08:30 and arrived 08:20 — the earlier arrival is seen first.
    const late = appt("CheckedIn", "2026-10-06T08:00:00Z", { checkedInAt: "2026-10-06T08:50:00Z" });
    const early = appt("CheckedIn", "2026-10-06T08:30:00Z", { checkedInAt: "2026-10-06T08:20:00Z" });
    expect(computeTodayMetrics([late, early], NOW).queue.map((a) => a.id)).toEqual([early.id, late.id]);
  });

  it("measures waiting time from check-in, falling back to a walk-in's registration time", () => {
    expect(waitingMinutes(appt("CheckedIn", "2026-10-06T08:00:00Z", { checkedInAt: "2026-10-06T08:35:30Z" }), NOW)).toBe(24);
    expect(waitingMinutes(appt("Scheduled", "2026-10-06T08:45:00Z", { source: "WalkIn", token: "T-03" }), NOW)).toBe(15);
    expect(waitingMinutes(appt("CheckedIn", "2026-10-06T09:30:00Z", { checkedInAt: "2026-10-06T09:05:00Z" }), NOW)).toBe(0);
  });

  it("handles an empty day", () => {
    const e = computeTodayMetrics([], NOW);
    expect([e.booked, e.waiting, e.late, e.expected, e.queue.length, e.upcoming.length]).toEqual([0, 0, 0, 0, 0, 0]);
  });

  it("isInClinic excludes finished, cancelled and not-yet-arrived bookings", () => {
    expect(isInClinic(appt("Completed", "2026-10-06T08:00:00Z", { source: "WalkIn" }))).toBe(false);
    expect(isInClinic(appt("Scheduled", "2026-10-06T08:00:00Z"))).toBe(false);
    expect(isInClinic(appt("Confirmed", "2026-10-06T08:00:00Z", { source: "WalkIn" }))).toBe(true);
  });
});

describe("buildDoctorLoad", () => {
  it("aggregates per doctor, ignores cancelled, sorts by outstanding work", () => {
    const rows = buildDoctorLoad([
      appt("Completed", "2026-10-06T06:00:00Z", { doctorId: "d1", doctorName: "د. أ" }),
      appt("Scheduled", "2026-10-06T10:00:00Z", { doctorId: "d1", doctorName: "د. أ" }),
      appt("Cancelled", "2026-10-06T10:00:00Z", { doctorId: "d1", doctorName: "د. أ" }),
      appt("CheckedIn", "2026-10-06T08:00:00Z", { doctorId: "d2", doctorName: "د. ب" }),
      appt("Scheduled", "2026-10-06T08:05:00Z", { doctorId: "d2", doctorName: "د. ب", source: "WalkIn" }),
      appt("Confirmed", "2026-10-06T11:00:00Z", { doctorId: "d2", doctorName: "د. ب" }),
      appt("NoShow", "2026-10-06T07:00:00Z", { doctorId: "d2", doctorName: "د. ب" }),
      appt("Cancelled", "2026-10-06T07:00:00Z", { doctorId: "d3", doctorName: "د. ج" }),
    ]);
    expect(rows).toEqual([
      { doctorId: "d2", doctorName: "د. ب", booked: 4, completed: 0, waiting: 2, remaining: 1, noShow: 1 },
      { doctorId: "d1", doctorName: "د. أ", booked: 2, completed: 1, waiting: 0, remaining: 1, noShow: 0 },
      { doctorId: "d3", doctorName: "د. ج", booked: 0, completed: 0, waiting: 0, remaining: 0, noShow: 0 },
    ]);
  });
});

describe("collectPages", () => {
  const pager = (total: number, pageSize = 100) => {
    const calls: number[] = [];
    const fetchPage = async (page: number): Promise<Paged<Appointment>> => {
      calls.push(page);
      const from = (page - 1) * pageSize;
      const count = Math.max(0, Math.min(pageSize, total - from));
      return {
        items: Array.from({ length: count }, (_, i) => ({ ...appt("Scheduled", "2026-10-06T10:00:00Z"), id: `x${from + i}` })),
        meta: { page, pageSize, totalCount: total, totalPages: Math.ceil(total / pageSize) },
      };
    };
    return { fetchPage, calls };
  };

  it("loads a single page without extra requests", async () => {
    const { fetchPage, calls } = pager(40);
    const r = await collectPages(fetchPage);
    expect([r.items.length, r.totalCount, r.truncated, calls]).toEqual([40, 40, false, [1]]);
  });

  it("follows every page up to the cap", async () => {
    const { fetchPage, calls } = pager(250);
    const r = await collectPages(fetchPage);
    expect([r.items.length, r.truncated, calls]).toEqual([250, false, [1, 2, 3]]);
  });

  it("flags truncation instead of silently dropping rows", async () => {
    const { fetchPage, calls } = pager(260);
    const r = await collectPages(fetchPage, 2);
    expect([r.items.length, r.totalCount, r.truncated, calls]).toEqual([200, 260, true, [1, 2]]);
  });

  it("handles an empty day", async () => {
    const { fetchPage } = pager(0);
    const r = await collectPages(fetchPage);
    expect([r.items.length, r.truncated]).toEqual([0, false]);
  });
});
