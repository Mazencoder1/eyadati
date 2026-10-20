import type { Page } from "@playwright/test";
import { call, mobile, nationalId, riyadhDate } from "./support/api";
import { expect, labelled, fmt, messages, signIn, test } from "./support/fixtures";
import { createPatient } from "./support/world";

const ar = messages.ar;
const a = ar.appointments;

async function openBoard(page: Page, date: string) {
  await page.goto("/appointments");
  await page.locator("#board-date").fill(date);
  await page.waitForLoadState("networkidle");
}

const rowOf = (page: Page, patient: string) => page.getByRole("row").filter({ hasText: patient });
const menuOf = (page: Page, patient: string) => page.getByRole("button", { name: fmt(a.actions.more, { patient }) });

test.describe("Receptionist", () => {
  test.beforeEach(async ({ page, world }) => {
    await signIn(page, world.reception.email);
  });

  test("registers a patient", async ({ page }) => {
    const name = "عبدالله القرني";
    await page.goto("/patients/new");
    await page.getByLabel(labelled(ar.patients.fields.fullName)).first().fill(name);
    await page.getByLabel(labelled(ar.patients.fields.mobile)).fill(mobile());
    await page.getByLabel(labelled(ar.patients.fields.gender)).selectOption({ label: ar.patients.gender.Male });
    await page.getByLabel(labelled(ar.patients.fields.identityType)).selectOption({ label: ar.patients.identityTypes.NationalId });
    await page.getByLabel(labelled(ar.patients.fields.identityNumber)).fill(nationalId());
    await page.getByRole("button", { name: ar.patients.create }).click();
    await expect(page.getByText(/^تم تسجيل المريض/)).toBeVisible();
    await expect(page.getByRole("heading", { level: 1, name })).toBeVisible();
  });

  test("warns about a shared mobile and refuses a duplicate ID number", async ({ page, world }) => {
    const phone = mobile();
    const id = nationalId();
    await call("POST", "/patients", world.owner.token, {
      fullName: "وليد الغامدي", mobileNumber: phone, identityType: "NationalId", identityNumber: id, gender: "Male",
    });
    await page.goto("/patients/new");
    await page.getByLabel(labelled(ar.patients.fields.fullName)).fill("وليد الغامدي");
    await page.getByLabel(labelled(ar.patients.fields.mobile)).fill(phone);
    await page.getByLabel(labelled(ar.patients.fields.gender)).selectOption({ label: ar.patients.gender.Male });
    await page.getByLabel(labelled(ar.patients.fields.identityType)).selectOption({ label: ar.patients.identityTypes.NationalId });
    await page.getByLabel(labelled(ar.patients.fields.identityNumber)).fill(id);
    await page.getByLabel(labelled(ar.patients.fields.identityNumber)).blur();
    const warning = page.getByRole("status").filter({ hasText: ar.patients.duplicates.title });
    await expect(warning).toBeVisible();
    await expect(warning.getByRole("link", { name: "وليد الغامدي" })).toBeVisible();

    await page.getByRole("button", { name: ar.patients.create }).click();
    await expect(page.getByRole("alert").filter({ hasText: "يوجد مريض مسجل بنفس رقم الهوية." })).toBeVisible();
    await expect(page).toHaveURL(/\/patients\/new/);
  });

  test("books, reschedules and cancels an appointment", async ({ page, world }) => {
    const patient = await createPatient(world.owner.token, "خالد السبيعي");
    const tomorrow = riyadhDate(1);

    await page.goto("/appointments");
    await page.getByRole("button", { name: a.book }).click();
    const booking = page.getByRole("dialog", { name: a.booking.title });
    await booking.getByRole("group", { name: ar.patients.picker.label }).getByRole("searchbox").fill(patient.fullName);
    await booking.getByText(patient.fullName).click();
    await booking.getByLabel(labelled(a.booking.doctor)).selectOption({ label: `${world.doctorA.displayName} — طب الأسرة` });
    await booking.getByLabel(labelled(a.booking.date)).fill(tomorrow);
    const firstSlot = booking.locator("fieldset label").first();
    const bookedTime = (await firstSlot.textContent())!.trim();
    await firstSlot.click();
    await booking.getByRole("button", { name: a.booking.submit }).click();
    await expect(page.getByText(a.booked)).toBeVisible();

    await openBoard(page, tomorrow);
    await expect(rowOf(page, patient.fullName)).toContainText(bookedTime);

    // Reschedule to another free slot of the same doctor.
    await menuOf(page, patient.fullName).click();
    await page.getByRole("menuitem", { name: a.actions.reschedule }).click();
    const move = page.getByRole("dialog", { name: a.reschedule.title });
    const target = move.locator("fieldset label").nth(3);
    const newTime = (await target.textContent())!.trim();
    await target.click();
    await move.getByRole("button", { name: a.reschedule.submit }).click();
    await expect(page.getByText(new RegExp(`${newTime}`)).first()).toBeVisible();
    await expect(rowOf(page, patient.fullName)).toContainText(newTime);

    // Cancel with a reason.
    await menuOf(page, patient.fullName).click();
    await page.getByRole("menuitem", { name: a.actions.cancel }).click();
    const cancel = page.getByRole("dialog", { name: a.cancelDialog.title });
    await cancel.getByLabel(labelled(a.cancelDialog.reason)).fill("طلب المريض");
    await cancel.getByRole("button", { name: a.cancelDialog.confirm }).click();
    await expect(page.getByText(a.actions.done.cancel)).toBeVisible();
    await expect(rowOf(page, patient.fullName)).toContainText(a.status.Cancelled);

    const list = await call<{ patientId: string }[]>("GET", `/appointments?date=${tomorrow}&patientId=${patient.id}`, world.owner.token);
    expect(list).toHaveLength(1);
  });

  test("registers a walk-in and checks the patient in", async ({ page, world }) => {
    const patient = await createPatient(world.owner.token, "سعاد المطيري");
    await page.goto("/appointments");
    await page.getByRole("button", { name: a.walkIn }).click();
    const dialog = page.getByRole("dialog", { name: a.booking.walkInTitle });
    await dialog.getByRole("group", { name: ar.patients.picker.label }).getByRole("searchbox").fill(patient.fullName);
    await dialog.getByText(patient.fullName).click();
    await dialog.getByLabel(labelled(a.booking.doctor)).selectOption({ label: `${world.doctorB.displayName} — طب الأسرة` });
    await dialog.getByRole("button", { name: a.booking.submitWalkIn }).click();
    await expect(page.getByText(a.walkInCreated.split("{")[0].trim()).first()).toBeVisible();

    await openBoard(page, riyadhDate(0));
    const row = rowOf(page, patient.fullName);
    await expect(row).toContainText(/T-\d+/);
    // A walk-in can't be rescheduled; the menu doesn't offer it.
    await menuOf(page, patient.fullName).click();
    await expect(page.getByRole("menuitem", { name: a.actions.reschedule })).toHaveCount(0);
    await page.keyboard.press("Escape");

    await row.getByRole("button", { name: a.actions["check-in"] }).click();
    await expect(page.getByText(a.actions.done["check-in"])).toBeVisible();
    await expect(row).toContainText(a.status.CheckedIn);
  });
});
