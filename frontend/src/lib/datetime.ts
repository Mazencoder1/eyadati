/**
 * Date/time helpers. Storage and the API are UTC; display and "which day is today" use the clinic's time zone.
 * Every formatter names its calendar explicitly: the "ar-SA" locale defaults to the Hijri (Umm al-Qura) calendar,
 * so Gregorian output must ask for `ca-gregory` (the same implicit-Hijri trap the backend guards against).
 * Digits are Latin (nu-latn) for times, phone numbers and MRNs, as is common in Saudi clinic software.
 */
import type { Locale } from "@/i18n/config";

export const DEFAULT_TIME_ZONE = "Asia/Riyadh";

const gregorianTag = (locale: Locale) => (locale === "ar" ? "ar-SA-u-ca-gregory-nu-latn" : "en-GB-u-ca-gregory");
const hijriTag = (locale: Locale) =>
  locale === "ar" ? "ar-SA-u-ca-islamic-umalqura-nu-latn" : "en-US-u-ca-islamic-umalqura";

/** Today's calendar date (YYYY-MM-DD) in the given zone. */
export function todayInZone(timeZone: string, now: Date = new Date()): string {
  return isoDateInZone(now, timeZone);
}

/** The calendar date (YYYY-MM-DD) of an instant in the given zone. */
export function isoDateInZone(instant: Date, timeZone: string): string {
  const parts = new Intl.DateTimeFormat("en-CA-u-ca-gregory-nu-latn", {
    timeZone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(instant);
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  return `${get("year")}-${get("month")}-${get("day")}`;
}

/** Parses YYYY-MM-DD as a calendar date (noon UTC, so no zone can shift the day). */
function calendarDate(isoDate: string): Date {
  const [y, m, d] = isoDate.split("-").map(Number);
  return new Date(Date.UTC(y, (m ?? 1) - 1, d ?? 1, 12));
}

export function isIsoDate(value: string): boolean {
  return /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(calendarDate(value).getTime());
}

/** The UTC instant at which a calendar date (YYYY-MM-DD) starts in the given zone, as an ISO string. */
export function startOfDayUtc(isoDate: string, timeZone: string): string {
  const [y, m, d] = isoDate.split("-").map(Number);
  const asUtc = Date.UTC(y, m - 1, d);
  // The zone's offset at that moment: format the instant in the zone and read it back as if it were UTC.
  const parts = new Intl.DateTimeFormat("en-CA-u-ca-gregory-nu-latn", {
    timeZone,
    hourCycle: "h23",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  }).formatToParts(new Date(asUtc));
  const get = (type: string) => Number(parts.find((p) => p.type === type)?.value ?? 0);
  const wall = Date.UTC(get("year"), get("month") - 1, get("day"), get("hour"), get("minute"), get("second"));
  return new Date(asUtc - (wall - asUtc)).toISOString();
}

export function addDays(isoDate: string, days: number): string {
  const d = calendarDate(isoDate);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

/** e.g. "الثلاثاء، 6 أكتوبر 2026" / "Tuesday 6 October 2026" */
export function formatGregorianDate(isoDate: string, locale: Locale): string {
  return new Intl.DateTimeFormat(gregorianTag(locale), {
    weekday: "long",
    day: "numeric",
    month: "long",
    year: "numeric",
    timeZone: "UTC",
  }).format(calendarDate(isoDate));
}

/** e.g. "24 ربيع الآخر 1448 هـ" */
export function formatHijriDate(isoDate: string, locale: Locale): string {
  return new Intl.DateTimeFormat(hijriTag(locale), {
    day: "numeric",
    month: "long",
    year: "numeric",
    timeZone: "UTC",
  }).format(calendarDate(isoDate));
}

/**
 * Numeric dates/times are shown left-to-right (`.ltr-nums`). The ar-SA formatter puts right-to-left marks (U+200F)
 * between the parts and an Arabic comma before the time; inside an LTR isolate those marks reorder the pieces
 * ("0709:16 2026/10/"), so they are removed: "07/10/2026 09:16".
 */
function plainNumeric(formatted: string): string {
  return formatted.replace(/[\u200e\u200f\u061c]/g, "").replace(/،\s*|,\s*/g, " ");
}

/** Short Gregorian date, e.g. 06/10/2026, for tables. */
export function formatShortDate(isoDate: string | null | undefined, locale: Locale): string {
  if (!isoDate) return "";
  return new Intl.DateTimeFormat(gregorianTag(locale), {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    timeZone: "UTC",
  }).format(calendarDate(isoDate.slice(0, 10))).replace(/[\u200e\u200f\u061c]/g, "");
}

/** Wall-clock time of a UTC instant in the clinic zone, 24h (e.g. 09:30). */
export function formatTime(utcIso: string, timeZone: string, locale: Locale): string {
  return new Intl.DateTimeFormat(gregorianTag(locale), {
    hour: "2-digit",
    minute: "2-digit",
    hourCycle: "h23",
    timeZone,
  }).format(new Date(utcIso)).replace(/[\u200e\u200f\u061c]/g, "");
}

/**
 * Date + time in one string. Arabic output contains RLM marks and an Arabic comma that only order correctly in the
 * surrounding RTL text — render it with `tabular-nums`, not `ltr-nums` (which forces LTR and scrambles it).
 */
export function formatDateTime(utcIso: string | null | undefined, timeZone: string, locale: Locale): string {
  if (!utcIso) return "";
  return plainNumeric(new Intl.DateTimeFormat(gregorianTag(locale), {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    hourCycle: "h23",
    timeZone,
  }).format(new Date(utcIso)));
}

/** "HH:mm:ss" (API TimeOnly) → "HH:mm" */
export function trimSeconds(time: string): string {
  return time.length >= 5 ? time.slice(0, 5) : time;
}

/** Age in whole years from a YYYY-MM-DD birth date, relative to `today` (YYYY-MM-DD). */
export function ageInYears(dateOfBirth: string, today: string): number {
  const [by, bm, bd] = dateOfBirth.split("-").map(Number);
  const [ty, tm, td] = today.split("-").map(Number);
  let age = ty - by;
  if (tm < bm || (tm === bm && td < bd)) age--;
  return Math.max(0, age);
}

export function formatMoney(amount: number, currency: string, locale: Locale): string {
  return new Intl.NumberFormat(locale === "ar" ? "ar-SA-u-nu-latn" : "en-SA", {
    style: "currency",
    currency,
    maximumFractionDigits: 2,
  }).format(amount);
}
