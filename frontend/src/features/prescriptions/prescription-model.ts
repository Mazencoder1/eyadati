/**
 * Pure logic of the prescription form: lines ↔ request, and checks mirroring the server
 * (Application/Prescriptions/PrescriptionService.cs → CreatePrescriptionValidator / PrescriptionItemValidator).
 * Unit tested in prescription-model.test.ts.
 */
import type { CreatePrescriptionRequest, Guid } from "@/lib/api/types";
import { check, rules, toLatinDigits, type ValidationCode } from "@/lib/validation/saudi";

/** Same cap as Prescription.MaxItems on the server. */
export const MAX_LINES = 30;

export interface MedicationLine {
  /** Client-only key for React lists. */
  key: string;
  medicationName: string;
  strength: string;
  dosage: string;
  frequency: string;
  route: string;
  durationDays: string;
  instructions: string;
}

let nextKey = 0;
export function emptyLine(): MedicationLine {
  nextKey++;
  return { key: `line-${nextKey}`, medicationName: "", strength: "", dosage: "", frequency: "", route: "", durationDays: "", instructions: "" };
}

const clean = (v: string) => (v.trim() ? v.trim() : null);

/** A line counts when anything was typed in it; completely empty lines are ignored instead of failing validation. */
function isBlank(line: MedicationLine) {
  return Object.entries(line).every(([k, v]) => k === "key" || !v.trim());
}

export function validateLines(lines: MedicationLine[], notes: string): Record<string, ValidationCode> {
  const errors: Record<string, ValidationCode | null> = {};
  const used = lines.map((l, i) => [l, i] as const).filter(([l]) => !isBlank(l));
  if (used.length === 0) errors.items = "required";
  if (used.length > MAX_LINES) errors.items = "out_of_range";
  for (const [l, i] of used) {
    errors[`items[${i}].medicationName`] = check(l.medicationName, rules.required(), rules.maxLength(200));
    errors[`items[${i}].strength`] = check(l.strength, rules.maxLength(100));
    errors[`items[${i}].dosage`] = check(l.dosage, rules.maxLength(100));
    errors[`items[${i}].frequency`] = check(l.frequency, rules.maxLength(100));
    errors[`items[${i}].route`] = check(l.route, rules.maxLength(50));
    const d = toLatinDigits(l.durationDays).trim();
    errors[`items[${i}].durationDays`] = d && !/^\d+$/.test(d) ? "invalid_value" : check(d, rules.range(1, 365));
    errors[`items[${i}].instructions`] = check(l.instructions, rules.maxLength(500));
  }
  errors.notes = check(notes, rules.maxLength(1000));
  return Object.fromEntries(Object.entries(errors).filter(([, v]) => v)) as Record<string, ValidationCode>;
}

/** Only the visit and the content: never a patient, doctor or branch id (the server derives them). */
export function toCreateRequest(visitId: Guid, lines: MedicationLine[], notes: string): CreatePrescriptionRequest {
  return {
    visitId,
    notes: clean(notes),
    items: lines
      .filter((l) => !isBlank(l))
      .map((l) => ({
        medicationName: l.medicationName.trim(),
        strength: clean(l.strength),
        dosage: clean(l.dosage),
        frequency: clean(l.frequency),
        route: clean(l.route),
        durationDays: clean(l.durationDays) ? Number(toLatinDigits(l.durationDays.trim())) : null,
        instructions: clean(l.instructions),
      })),
  };
}
