import { call, login, riyadhDate } from "./support/api";
import { expect, fmt, labelled, messages, signIn, test } from "./support/fixtures";
import { createPatient } from "./support/world";

const ar = messages.ar;
const f = ar.followUps;
const SECRET = "تشخيص سري للمتابعة";

test.describe.configure({ mode: "serial" });

test.describe("Follow-ups for reception", () => {
  let patient: { id: string; fullName: string };

  test.beforeAll(async ({ world }) => {
    patient = await createPatient(world.owner.token, "ياسمين المتابعة");
    // Doctor A records a visit (main branch) with clinical content and a follow-up in 3 days.
    await call("POST", "/visits", await login(world.doctorA.email), {
      patientId: patient.id, doctorId: world.doctorA.doctorId, branchId: world.mainBranchId,
      clinical: { diagnosis: SECRET, treatmentPlan: "خطة علاجية سرية" }, followUpDate: riyadhDate(3), followUpNotes: "ملاحظة سرية",
    });
  });

  test("the API gives reception operational fields only", async ({ world }) => {
    const res = await fetch(`${process.env.E2E_API_URL ?? "http://localhost:5056"}/api/v1/follow-ups`, {
      headers: { Authorization: `Bearer ${await login(world.reception.email)}` },
    });
    expect(res.status).toBe(200);
    const raw = await res.text();
    expect(raw).toContain(patient.fullName);
    for (const secret of [SECRET, "خطة علاجية سرية", "ملاحظة سرية", "بنسلين", "diagnosis", "visitId"]) expect(raw).not.toContain(secret);
  });

  test("a receptionist finds the follow-up and books it from the list", async ({ page, world }) => {
    await signIn(page, world.reception.email);
    await page.getByRole("navigation", { name: ar.shell.mainNav }).getByRole("link", { name: ar.nav.followUps }).click();
    await expect(page.getByRole("heading", { level: 1, name: f.title })).toBeVisible();
    const row = page.getByRole("row").filter({ hasText: patient.fullName });
    await expect(row).toContainText(f.notBooked);
    await expect(page.getByText(SECRET)).toHaveCount(0);

    await row.getByRole("button", { name: fmt(f.bookFor, { patient: patient.fullName }) }).click();
    const dialog = page.getByRole("dialog", { name: ar.appointments.booking.title });
    await expect(dialog.getByText(patient.fullName)).toBeVisible();
    await expect(dialog.getByLabel(labelled(ar.appointments.booking.doctor))).toHaveValue(world.doctorA.doctorId);
    await expect(dialog.getByLabel(labelled(ar.appointments.booking.date))).toHaveValue(riyadhDate(3));
    await dialog.locator("fieldset label").first().click();
    await dialog.getByRole("button", { name: ar.appointments.booking.submit }).click();
    await expect(page.getByText(f.booked)).toBeVisible();
    // Default filter is "not booked": the booked follow-up leaves the list; "booked" shows it with its time.
    await expect(page.getByRole("row").filter({ hasText: patient.fullName })).toHaveCount(0);
    await page.getByLabel(labelled(f.filters.status)).selectOption("true");
    await expect(page.getByRole("row").filter({ hasText: patient.fullName })).toContainText(f.bookedFor);
  });

  test("a receptionist restricted to another branch doesn't see it; another clinic sees nothing", async ({ world }) => {
    const north = await call<{ patientId: string }[]>("GET", "/follow-ups", await login(world.receptionNorth.email));
    expect(north.map((x) => x.patientId)).not.toContain(patient.id);
    const other = await call<unknown[]>("GET", `/follow-ups?doctorId=${world.doctorA.doctorId}`, await login(world.other.email));
    expect(other).toEqual([]);
  });
});
