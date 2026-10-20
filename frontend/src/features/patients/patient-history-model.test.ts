import { describe, expect, it } from "vitest";
import type { Appointment, Paged, PrescriptionListItem, VisitListItem } from "@/lib/api/types";
import { buildHistory, latestAppointments, nextFollowUp } from "./patient-history-model";

const RIYADH = "Asia/Riyadh";

const appt = (id: string, startUtc: string, status: Appointment["status"] = "Completed") =>
  ({ id, startUtc, status, doctorName: "د. سارة" }) as Appointment;
const visit = (id: string, visitedAtUtc: string, appointmentId?: string, diagnosis?: string) =>
  ({ id, visitedAtUtc, appointmentId, diagnosis, status: "Completed", doctorName: "د. سارة" }) as VisitListItem;
const rx = (id: string, issuedAtUtc: string) => ({ id, number: id, issuedAtUtc, itemCount: 1 }) as PrescriptionListItem;

describe("buildHistory", () => {
  it("merges newest first, shows an appointment that led to a visit only as the visit, and groups by clinic day", () => {
    const days = buildHistory(
      [appt("a1", "2026-10-01T06:00:00Z"), appt("a2", "2026-10-05T07:00:00Z"), appt("a3", "2026-10-20T07:00:00Z", "Scheduled")],
      [visit("v1", "2026-10-05T07:10:00Z", "a2", "التهاب البلعوم")],
      [rx("RX-1", "2026-10-05T07:10:00Z")],
      RIYADH,
    );
    expect(days.map((d) => d.date)).toEqual(["2026-10-20", "2026-10-05", "2026-10-01"]);
    expect(days[1].entries.map((e) => e.kind)).toEqual(["visit", "prescription"]);
    const v = days[1].entries[0];
    expect(v.kind === "visit" && v.appointment?.id).toBe("a2");
    expect(days.flatMap((d) => d.entries).filter((e) => e.kind === "appointment").map((e) => e.kind === "appointment" && e.appointment.id)).toEqual([
      "a3",
      "a1",
    ]);
  });

  it("uses the clinic's calendar day, not UTC (21:30 UTC is already the next day in Riyadh)", () => {
    expect(buildHistory([], [visit("v", "2026-10-05T21:30:00Z")], [], RIYADH)[0].date).toBe("2026-10-06");
  });

  it("works with any source missing (no permission to load it)", () => {
    expect(buildHistory(undefined, undefined, undefined, RIYADH)).toEqual([]);
    expect(buildHistory([appt("a", "2026-10-01T06:00:00Z")], undefined, undefined, RIYADH)[0].entries).toHaveLength(1);
  });
});

describe("latestAppointments", () => {
  const paged = (page: number, totalPages: number): Paged<Appointment> => ({
    items: [appt(`p${page}`, "2026-10-01T06:00:00Z")],
    meta: { page, pageSize: 100, totalCount: totalPages * 100, totalPages },
  });

  it("returns everything when there are at most two pages", async () => {
    const r = await latestAppointments(async (page) => paged(page, 2));
    expect(r).toMatchObject({ truncated: false });
    expect(r.items.map((a) => a.id)).toEqual(["p1", "p2"]);
  });

  it("loads the last two pages (the most recent) when there are more", async () => {
    const asked: number[] = [];
    const r = await latestAppointments(async (page) => (asked.push(page), paged(page, 5)));
    expect(asked).toEqual([1, 4, 5]);
    expect(r.items.map((a) => a.id)).toEqual(["p4", "p5"]);
    expect(r.truncated).toBe(true);
  });
});

describe("nextFollowUp", () => {
  const v = (followUpDate: string | null) => ({ followUpDate }) as unknown as VisitListItem;
  it("is the soonest follow-up from today on, or null", () => {
    expect(nextFollowUp([v("2026-10-20"), v("2026-10-09"), v("2026-10-01"), v(null)], "2026-10-08")).toBe("2026-10-09");
    expect(nextFollowUp([v("2026-10-08")], "2026-10-08")).toBe("2026-10-08");
    expect(nextFollowUp([v("2026-10-01"), v(null)], "2026-10-08")).toBeNull();
    expect(nextFollowUp(undefined, "2026-10-08")).toBeNull();
  });
});
