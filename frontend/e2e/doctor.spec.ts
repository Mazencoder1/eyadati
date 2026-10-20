import { call, login, riyadhDate, statusOf } from "./support/api";
import { expect, labelled, fmt, messages, signIn, test } from "./support/fixtures";
import { createPatient } from "./support/world";

const ar = messages.ar;
const v = ar.visits;

test.describe.configure({ mode: "serial" });

test.describe("Doctor", () => {
  let patient: { id: string; fullName: string };
  let visitUrl = "";
  let prescriptionNumber = "";

  test.beforeAll(async ({ world }) => {
    patient = await createPatient(world.owner.token, "نورة الشهري");
    const p = await call<{ rowVersion: string }>("GET", `/patients/${patient.id}`, world.owner.token);
    await call("PUT", `/patients/${patient.id}/medical`, world.owner.token, { allergies: "سلفا", rowVersion: p.rowVersion });
    const walkIn = await call<{ id: string; rowVersion: string }>("POST", "/appointments", world.reception.token, {
      patientId: patient.id, doctorId: world.doctorA.doctorId, branchId: world.mainBranchId, source: "WalkIn",
    });
    await call("POST", `/appointments/${walkIn.id}/check-in`, world.reception.token, { rowVersion: walkIn.rowVersion });
  });

  test.beforeEach(async ({ page, world }) => {
    await signIn(page, world.doctorA.email);
  });

  test("opens the checked-in patient's visit from Today and sees the medical alerts", async ({ page }) => {
    const queue = page.getByRole("region", { name: ar.today.queue.title }).or(page.locator("section").filter({ hasText: ar.today.queue.title }));
    const item = queue.getByRole("listitem").filter({ hasText: patient.fullName }).first();
    await expect(item).toBeVisible();
    await item.getByRole("button", { name: ar.today.queue.openVisit }).or(item.getByRole("link", { name: ar.today.queue.openVisit })).click();
    await page.waitForURL(/\/visits\//);
    visitUrl = new URL(page.url()).pathname;
    await expect(page.getByText(v.alertsTitle)).toBeVisible();
    await expect(page.getByText("سلفا")).toBeVisible();
  });

  test("records vitals, findings, diagnosis, plan and follow-up", async ({ page }) => {
    await page.goto(visitUrl);
    await page.getByLabel(labelled(v.vitals.systolicBp)).fill("118");
    await page.getByLabel(labelled(v.vitals.diastolicBp)).fill("76");
    await page.getByLabel(labelled(v.fields.symptoms)).fill("صداع منذ يومين");
    await page.getByLabel(labelled(v.fields.diagnosis)).fill("صداع توتري");
    await page.getByLabel(labelled(v.fields.treatmentPlan)).fill("راحة ومسكن عند الحاجة");
    await page.getByLabel(labelled(v.fields.followUpDate)).fill(riyadhDate(14));
    await page.getByLabel(labelled(v.fields.followUpNotes)).fill("مراجعة بعد أسبوعين");
    await page.getByRole("button", { name: ar.common.save, exact: true }).click();
    await expect(page.getByText(ar.common.saved)).toBeVisible();
  });

  test("issues a prescription and prints it", async ({ page }) => {
    await page.goto(visitUrl);
    await page.getByRole("button", { name: ar.prescriptions.new }).click();
    const dialog = page.getByRole("dialog", { name: ar.prescriptions.new });
    await dialog.getByLabel(labelled(ar.prescriptions.fields.medicationName)).first().fill("Ibuprofen");
    await dialog.getByLabel(labelled(ar.prescriptions.fields.strength)).first().fill("400mg");
    await dialog.getByRole("button", { name: ar.prescriptions.issue }).click();
    const toast = page.getByText(/^تم إصدار الوصفة/);
    await expect(toast).toBeVisible();
    prescriptionNumber = (await toast.textContent())!.match(/RX-\d+/)![0];

    await page.getByRole("link", { name: ar.prescriptions.view }).first().click();
    await page.waitForURL(/\/prescriptions\//);
    await expect(page.getByRole("heading", { level: 1, name: ar.prescriptions.doc.title })).toBeVisible();
    await expect(page.getByText("Ibuprofen")).toBeVisible();
    await expect(page.getByRole("button", { name: ar.prescriptions.print })).toBeVisible();
    await page.emulateMedia({ media: "print" });
    await expect(page.getByRole("navigation", { name: ar.shell.mainNav })).toBeHidden();
    await expect(page.getByRole("button", { name: ar.prescriptions.print })).toBeHidden();
    await expect(page.getByText("Ibuprofen")).toBeVisible();
    await page.emulateMedia({ media: "screen" });
  });

  test("completes the visit, which then becomes read-only", async ({ page }) => {
    await page.goto(visitUrl);
    await page.getByRole("button", { name: v.complete }).click();
    const dialog = page.getByRole("dialog", { name: v.completeTitle });
    await dialog.getByRole("button", { name: v.complete }).click();
    await expect(page.getByText(v.completed)).toBeVisible();
    await expect(page.getByText(v.lockedTitle)).toBeVisible();
    await expect(page.locator("main textarea")).toHaveCount(0);
    await expect(page.getByText("صداع توتري")).toBeVisible();
    await expect(page.getByText("مراجعة بعد أسبوعين")).toBeVisible();
  });

  test("a completed visit takes no new prescription; the issued one stays readable and printable", async ({ page, world }) => {
    await page.goto(visitUrl);
    await expect(page.getByText(v.lockedTitle)).toBeVisible();
    await expect(page.getByRole("button", { name: ar.prescriptions.new })).toHaveCount(0);

    const visitId = visitUrl.split("/").pop()!;
    const refused = await statusOf("POST", "/prescriptions", await login(world.doctorA.email), { visitId, items: [{ medicationName: "x" }] });
    expect(refused).toEqual({ status: 422, code: "VISIT_COMPLETED" });

    await page.getByRole("link", { name: ar.prescriptions.view }).first().click();
    await page.waitForURL(/\/prescriptions\//);
    await expect(page.getByRole("heading", { level: 1, name: ar.prescriptions.doc.title })).toBeVisible();
    await expect(page.getByText(prescriptionNumber)).toBeVisible();
    await expect(page.getByRole("button", { name: ar.prescriptions.print })).toBeVisible();
  });

  test("sees the visit and prescription in the patient's history", async ({ page }) => {
    await page.goto(`/patients/${patient.id}`);
    await expect(page.getByText("صداع توتري")).toBeVisible();
    await expect(page.getByText(fmt(ar.history.prescription, { number: prescriptionNumber }))).toBeVisible();
    await expect(page.getByText(/^متابعة قادمة في/)).toBeVisible();
  });
});
