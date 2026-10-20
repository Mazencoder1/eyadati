import { createTranslator } from "next-intl";
import { describe, expect, it } from "vitest";
import ar from "../../messages/ar.json";
import en from "../../messages/en.json";

function keys(obj: Record<string, unknown>, prefix = ""): string[] {
  return Object.entries(obj).flatMap(([k, v]) =>
    v && typeof v === "object" ? keys(v as Record<string, unknown>, `${prefix}${k}.`) : [`${prefix}${k}`],
  );
}

describe("message catalogs", () => {
  it("ar and en define exactly the same keys", () => {
    expect(keys(en).sort()).toEqual(keys(ar).sort());
  });

  it("has no empty strings", () => {
    const empty = (o: Record<string, unknown>) => keys(o).filter((k) => k.split(".").reduce<unknown>((x, p) => (x as Record<string, unknown>)[p], o) === "");
    expect(empty(ar)).toEqual([]);
    expect(empty(en)).toEqual([]);
  });
});

describe("Gate D copy fixes", () => {
  it("GD-12: a patient who just checked in is not 'waiting 0 minutes'", () => {
    const tAr = createTranslator({ locale: "ar", messages: ar, namespace: "today.queue" });
    const tEn = createTranslator({ locale: "en", messages: en, namespace: "today.queue" });
    expect(tAr("waitingFor", { minutes: 0 })).toBe("وصل الآن");
    expect(tEn("waitingFor", { minutes: 0 })).toBe("just arrived");
    expect(tAr("waitingFor", { minutes: 1 })).toBe("ينتظر منذ دقيقة");
    expect(tEn("waitingFor", { minutes: 24 })).toBe("waiting 24 min");
  });

  it("GD-09 / GD-10: specific empty-prescription message; no plan price wording", () => {
    expect(ar.prescriptions.itemsRequired).toBe("أضف دواءً واحدًا على الأقل.");
    expect(keys(ar)).not.toContain("subscription.perMonthExVat");
    expect(ar.subscription.pricesLater).toBeTruthy();
  });
});

// Tenant permissions served by GET /permissions (backend Domain/Authorization/Permissions.cs, incl. T-012's
// MedicalRecords.ViewAllDoctors). The roles editor falls back to the raw name, so a missing label is easy to miss.
const TENANT_PERMISSIONS = [
  "Patients.View", "Patients.Create", "Patients.Edit", "Patients.Delete", "Patients.Export",
  "MedicalRecords.View", "MedicalRecords.Edit", "MedicalRecords.ViewAllDoctors",
  "Doctors.View", "Doctors.Manage",
  "Appointments.View", "Appointments.Create", "Appointments.Edit", "Appointments.Cancel", "Appointments.CheckIn",
  "Visits.View", "Visits.Create", "Visits.Edit", "Prescriptions.View", "Prescriptions.Create",
  "Invoices.View", "Invoices.Create", "Invoices.Void", "Invoices.Refund", "Payments.Record",
  "Reports.View", "Reports.Financial", "Staff.View", "Staff.Manage", "Roles.Manage", "Branches.Manage",
  "Settings.Manage", "Subscription.View", "Subscription.Manage", "AuditLogs.View", "Files.View", "Files.Upload",
];

describe("permission labels", () => {
  it.each([["ar", ar], ["en", en]] as const)("%s labels every tenant permission", (_, catalog) => {
    const labels = new Set(keys(catalog.permissions as Record<string, unknown>));
    expect(TENANT_PERMISSIONS.filter((p) => !labels.has(p))).toEqual([]);
  });
});
