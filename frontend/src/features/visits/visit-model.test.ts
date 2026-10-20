import { describe, expect, it } from "vitest";
import type { Visit } from "@/lib/api/types";
import { formFieldOf, isDirty, mapVisitServerErrors, toFormValues, toUpdateRequest, validateVisit } from "./visit-model";

const visit: Visit = {
  id: "v1",
  branchId: "b1",
  patientId: "p1",
  patientName: "محمد",
  patientMrn: "P-000001",
  doctorId: "d1",
  doctorName: "د. سارة",
  visitedAtUtc: "2026-10-06T08:00:00Z",
  createdAtUtc: "2026-10-06T08:00:00Z",
  rowVersion: "rv1",
  status: "Open",
  followUpDate: null,
  clinical: { diagnosis: "التهاب البلعوم", vitals: { systolicBp: 120, temperatureC: 38.2 } },
};

describe("visit form", () => {
  it("round-trips the server's visit into form values and back", () => {
    const values = toFormValues(visit);
    expect(values.systolicBp).toBe("120");
    expect(values.temperatureC).toBe("38.2");
    expect(values.heartRate).toBe("");
    const body = toUpdateRequest(values, "rv1");
    expect(body.rowVersion).toBe("rv1");
    expect(body.clinical?.diagnosis).toBe("التهاب البلعوم");
    expect(body.clinical?.vitals).toMatchObject({ systolicBp: 120, temperatureC: 38.2, heartRate: null });
    expect(isDirty(values, visit)).toBe(false);
  });

  it("accepts Arabic-Indic digits and a decimal comma, upper-cases the ICD-10 code, and drops empty vitals", () => {
    const values = { ...toFormValues({ ...visit, clinical: null }), heartRate: "٨٨", temperatureC: "37,5", diagnosisCode: " j02.9 " };
    const body = toUpdateRequest(values, "rv");
    expect(body.clinical?.vitals).toMatchObject({ heartRate: 88, temperatureC: 37.5 });
    expect(body.clinical?.diagnosisCode).toBe("J02.9");
    expect(toUpdateRequest(toFormValues({ ...visit, clinical: null }), "rv").clinical?.vitals).toBeNull();
  });

  it("checks vitals against the server's ranges and integer columns", () => {
    const base = toFormValues({ ...visit, clinical: null });
    expect(validateVisit({ ...base, systolicBp: "350" }, "2026-10-06").systolicBp).toBe("out_of_range");
    expect(validateVisit({ ...base, heartRate: "72.5" }, "2026-10-06").heartRate).toBe("invalid_value");
    expect(validateVisit({ ...base, oxygenSaturation: "abc" }, "2026-10-06").oxygenSaturation).toBe("invalid_value");
    expect(validateVisit({ ...base, temperatureC: "37.4" }, "2026-10-06").temperatureC).toBeNull();
  });

  it("checks the ICD-10 format, text lengths and a follow-up date in the past", () => {
    const base = toFormValues({ ...visit, clinical: null });
    expect(validateVisit({ ...base, diagnosisCode: "J02.9" }, "2026-10-06").diagnosisCode).toBeFalsy();
    expect(validateVisit({ ...base, diagnosisCode: "fever" }, "2026-10-06").diagnosisCode).toBe("invalid_value");
    expect(validateVisit({ ...base, diagnosis: "x".repeat(1001) }, "2026-10-06").diagnosis).toBe("too_long");
    expect(validateVisit({ ...base, followUpDate: "2026-10-01" }, "2026-10-06").followUpDate).toBe("invalid_date");
    expect(validateVisit({ ...base, followUpDate: "2026-10-20" }, "2026-10-06").followUpDate).toBeFalsy();
  });

  it("maps nested server error keys onto form fields", () => {
    expect(formFieldOf("clinical.Vitals.SystolicBp")).toBe("systolicBp");
    expect(formFieldOf("followUpDate")).toBe("followUpDate");
    expect(mapVisitServerErrors({ "clinical.DiagnosisCode": ["رمز غير صحيح"], rowVersion: ["x"] })).toEqual({
      diagnosisCode: "رمز غير صحيح",
      rowVersion: "x",
    });
  });

  it("detects unsaved edits", () => {
    expect(isDirty({ ...toFormValues(visit), symptoms: "سعال" }, visit)).toBe(true);
  });
});
