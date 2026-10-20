import { describe, expect, it } from "vitest";
import { emptyLine, MAX_LINES, toCreateRequest, validateLines } from "./prescription-model";

const line = (over: Partial<ReturnType<typeof emptyLine>> = {}) => ({ ...emptyLine(), ...over });

describe("prescription form", () => {
  it("requires at least one medication and ignores fully blank lines", () => {
    expect(validateLines([line()], "")).toEqual({ items: "required" });
    const ok = [line({ medicationName: "Paracetamol" }), line()];
    expect(validateLines(ok, "")).toEqual({});
    expect(toCreateRequest("v1", ok, "").items).toHaveLength(1);
  });

  it("requires a name on a started line and checks duration and lengths like the server", () => {
    const errors = validateLines([line({ dosage: "1 tablet", durationDays: "400" }), line({ medicationName: "x", durationDays: "2.5" })], "n".repeat(1001));
    expect(errors["items[0].medicationName"]).toBe("required");
    expect(errors["items[0].durationDays"]).toBe("out_of_range");
    expect(errors["items[1].durationDays"]).toBe("invalid_value");
    expect(errors.notes).toBe("too_long");
  });

  it("caps the number of lines", () => {
    const many = Array.from({ length: MAX_LINES + 1 }, () => line({ medicationName: "x" }));
    expect(validateLines(many, "").items).toBe("out_of_range");
  });

  it("sends only the visit and the content — no patient, doctor or branch", () => {
    const body = toCreateRequest("visit-1", [line({ medicationName: " Amoxicillin ", strength: "500 mg", durationDays: "٧" })], " بعد الأكل ");
    expect(Object.keys(body).sort()).toEqual(["items", "notes", "visitId"]);
    expect(body).toEqual({
      visitId: "visit-1",
      notes: "بعد الأكل",
      items: [{ medicationName: "Amoxicillin", strength: "500 mg", dosage: null, frequency: null, route: null, durationDays: 7, instructions: null }],
    });
  });
});
