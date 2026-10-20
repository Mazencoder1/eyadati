/**
 * Pure logic of the visit screen: form values ↔ API payload, client-side checks that mirror the server's bounds
 * (Application/Visits/VisitService.cs → VisitClinicalValidator), and mapping of server field errors onto the form.
 * No React — unit tested in visit-model.test.ts. The server stays authoritative.
 */
import type { UpdateVisitRequest, Visit, VisitClinicalData, VisitVitals } from "@/lib/api/types";
import { check, rules, toLatinDigits, type ValidationCode } from "@/lib/validation/saudi";

export const VITAL_KEYS = [
  "systolicBp",
  "diastolicBp",
  "heartRate",
  "respiratoryRate",
  "temperatureC",
  "oxygenSaturation",
  "weightKg",
  "heightCm",
] as const;
export type VitalKey = (typeof VITAL_KEYS)[number];

/** Same ranges as the server validator; decimals only where the column allows them. */
export const VITAL_RANGES: Record<VitalKey, { min: number; max: number; decimal: boolean }> = {
  systolicBp: { min: 40, max: 300, decimal: false },
  diastolicBp: { min: 20, max: 200, decimal: false },
  heartRate: { min: 20, max: 300, decimal: false },
  respiratoryRate: { min: 4, max: 80, decimal: false },
  temperatureC: { min: 30, max: 45, decimal: true },
  oxygenSaturation: { min: 40, max: 100, decimal: false },
  weightKg: { min: 0.2, max: 500, decimal: true },
  heightCm: { min: 20, max: 300, decimal: true },
};

export const TEXT_LIMITS = {
  symptoms: 1000,
  clinicalNotes: 4000,
  diagnosis: 1000,
  diagnosisCode: 20,
  treatmentPlan: 2000,
  followUpNotes: 500,
} as const;

export type VisitFormValues = Record<VitalKey, string> & {
  symptoms: string;
  clinicalNotes: string;
  diagnosis: string;
  diagnosisCode: string;
  treatmentPlan: string;
  followUpDate: string;
  followUpNotes: string;
};

const ICD10 = /^\s*[A-Za-z][0-9][0-9A-Za-z](\.[0-9A-Za-z]{1,4})?\s*$/;

const text = (v: string | null | undefined) => v ?? "";
const num = (v: number | null | undefined) => (v === null || v === undefined ? "" : String(v));

export function toFormValues(visit: Visit): VisitFormValues {
  const c = visit.clinical ?? {};
  const vitals = c.vitals ?? {};
  return {
    systolicBp: num(vitals.systolicBp),
    diastolicBp: num(vitals.diastolicBp),
    heartRate: num(vitals.heartRate),
    respiratoryRate: num(vitals.respiratoryRate),
    temperatureC: num(vitals.temperatureC),
    oxygenSaturation: num(vitals.oxygenSaturation),
    weightKg: num(vitals.weightKg),
    heightCm: num(vitals.heightCm),
    symptoms: text(c.symptoms),
    clinicalNotes: text(c.clinicalNotes),
    diagnosis: text(c.diagnosis),
    diagnosisCode: text(c.diagnosisCode),
    treatmentPlan: text(c.treatmentPlan),
    followUpDate: text(visit.followUpDate),
    followUpNotes: text(visit.followUpNotes),
  };
}

function parseNumber(value: string): number | null {
  const v = toLatinDigits(value).trim().replace(",", ".");
  if (!v) return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

const clean = (v: string) => (v.trim() ? v.trim() : null);

/** Builds the PUT body. The clinical block is always sent whole (the API replaces it), so nothing is lost. */
export function toUpdateRequest(values: VisitFormValues, rowVersion: string): UpdateVisitRequest {
  const vitals = Object.fromEntries(VITAL_KEYS.map((k) => [k, parseNumber(values[k])])) as VisitVitals;
  const clinical: VisitClinicalData = {
    symptoms: clean(values.symptoms),
    clinicalNotes: clean(values.clinicalNotes),
    diagnosis: clean(values.diagnosis),
    diagnosisCode: clean(values.diagnosisCode)?.toUpperCase() ?? null,
    treatmentPlan: clean(values.treatmentPlan),
    vitals: VITAL_KEYS.some((k) => vitals[k] !== null) ? vitals : null,
  };
  return {
    clinical,
    followUpDate: clean(values.followUpDate),
    followUpNotes: clean(values.followUpNotes),
    rowVersion,
  };
}

/** Client-side checks with the server's codes. `today` (YYYY-MM-DD, clinic zone) bounds the follow-up date. */
export function validateVisit(values: VisitFormValues, today: string): Partial<Record<keyof VisitFormValues, ValidationCode | null>> {
  const result: Partial<Record<keyof VisitFormValues, ValidationCode | null>> = {};
  for (const key of VITAL_KEYS) {
    const { min, max, decimal } = VITAL_RANGES[key];
    const raw = values[key].trim();
    if (!raw) continue;
    const n = parseNumber(raw);
    if (n === null) result[key] = "invalid_value";
    else if (!decimal && !Number.isInteger(n)) result[key] = "invalid_value";
    else result[key] = check(String(n), rules.range(min, max));
  }
  for (const key of Object.keys(TEXT_LIMITS) as (keyof typeof TEXT_LIMITS)[]) {
    result[key] ??= check(values[key], rules.maxLength(TEXT_LIMITS[key]));
  }
  if (!result.diagnosisCode && values.diagnosisCode.trim() && !ICD10.test(values.diagnosisCode)) result.diagnosisCode = "invalid_value";
  if (values.followUpDate && values.followUpDate < today) result.followUpDate = "invalid_date";
  return result;
}

/**
 * Server field keys look like "clinical.Vitals.SystolicBp", "clinical.DiagnosisCode" or "followUpDate"; the form
 * uses the last segment in camelCase.
 */
export function formFieldOf(serverKey: string): string {
  const last = serverKey.split(".").pop() ?? serverKey;
  return last.charAt(0).toLowerCase() + last.slice(1);
}

export function mapVisitServerErrors(errors: Record<string, string[]> | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [key, messages] of Object.entries(errors ?? {})) {
    const field = formFieldOf(key);
    if (messages.length > 0 && !(field in out)) out[field] = messages[0];
  }
  return out;
}

/** True when the form differs from what the server last returned. */
export function isDirty(values: VisitFormValues, visit: Visit): boolean {
  const saved = toFormValues(visit);
  return (Object.keys(saved) as (keyof VisitFormValues)[]).some((k) => saved[k].trim() !== values[k].trim());
}
