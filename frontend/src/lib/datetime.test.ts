import { describe, expect, it } from "vitest";
import { addDays, ageInYears, formatDateTime, formatGregorianDate, formatHijriDate, formatShortDate, formatTime, isIsoDate, startOfDayUtc, todayInZone } from "./datetime";

describe("clinic-time helpers", () => {
  it("computes 'today' in the clinic zone, not the browser's", () => {
    // 21:30 UTC on Oct 5 is already 00:30 on Oct 6 in Riyadh (UTC+3).
    expect(todayInZone("Asia/Riyadh", new Date("2026-10-05T21:30:00Z"))).toBe("2026-10-06");
    expect(todayInZone("UTC", new Date("2026-10-05T21:30:00Z"))).toBe("2026-10-05");
  });

  it("formats UTC instants as clinic wall-clock time with Latin digits", () => {
    expect(formatTime("2026-10-06T06:00:00Z", "Asia/Riyadh", "ar")).toBe("09:00");
    expect(formatTime("2026-10-06T06:00:00Z", "Asia/Riyadh", "en")).toBe("09:00");
  });

  it("shows the Gregorian date explicitly (ar-SA defaults to Hijri in browsers)", () => {
    const ar = formatGregorianDate("2026-10-06", "ar");
    expect(ar).toContain("2026");
    expect(ar).toContain("أكتوبر");
  });

  it("shows the Umm al-Qura Hijri date", () => {
    expect(formatHijriDate("2026-10-06", "ar")).toContain("1448");
    expect(formatHijriDate("2026-10-06", "ar")).toContain("ربيع");
    expect(formatHijriDate("2026-10-06", "en")).toContain("1448");
  });

  it("does calendar arithmetic without zone drift", () => {
    expect(addDays("2026-10-31", 1)).toBe("2026-11-01");
    expect(addDays("2026-03-01", -1)).toBe("2026-02-28");
    expect(isIsoDate("2026-10-06")).toBe(true);
    expect(isIsoDate("06/10/2026")).toBe(false);
  });

  it("computes age in whole years", () => {
    expect(ageInYears("1990-10-07", "2026-10-06")).toBe(35);
    expect(ageInYears("1990-10-06", "2026-10-06")).toBe(36);
  });
});

describe("numeric dates inside LTR isolates", () => {
  it("contain no bidi marks, so the parts never reorder (\"0709:16 2026/10/\" defect)", () => {
    const marks = /[\u200e\u200f\u061c]/;
    expect(formatDateTime("2026-10-07T06:16:00Z", "Asia/Riyadh", "ar")).toBe("07/10/2026 09:16");
    expect(formatShortDate("1988-03-14", "ar")).toBe("14/03/1988");
    expect(formatTime("2026-10-07T06:16:00Z", "Asia/Riyadh", "ar")).not.toMatch(marks);
    expect(formatDateTime("2026-10-07T06:16:00Z", "Asia/Riyadh", "en")).toBe("07/10/2026 09:16");
  });
});

describe("startOfDayUtc", () => {
  it("is local midnight in the clinic zone, as a UTC instant", () => {
    expect(startOfDayUtc("2026-10-08", "Asia/Riyadh")).toBe("2026-10-07T21:00:00.000Z");
    expect(startOfDayUtc("2026-10-08", "UTC")).toBe("2026-10-08T00:00:00.000Z");
    expect(startOfDayUtc("2026-01-01", "Asia/Riyadh")).toBe("2025-12-31T21:00:00.000Z");
  });
});
