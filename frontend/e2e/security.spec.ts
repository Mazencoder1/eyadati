import { call, login, mailboxToken, PASSWORD, riyadhDate, statusOf, unique } from "./support/api";
import { expect, messages, signIn, test } from "./support/fixtures";
import { createPatient } from "./support/world";

const ar = messages.ar;

test.describe("Security boundaries", () => {
  test("stale rowVersion, slot conflicts, made-up ids and forged identity fields are all refused or ignored", async ({ page, world }) => {
    const reception = await login(world.reception.email);
    const patient = await createPatient(world.owner.token, "عبير الاختبار");
    const date = riyadhDate(4);
    const slots = await call<{ startUtc: string }[]>("GET", `/doctors/${world.doctorB.doctorId}/availability?date=${date}`, reception);
    const appt = await call<{ id: string; rowVersion: string }>("POST", "/appointments", reception, {
      patientId: patient.id, doctorId: world.doctorB.doctorId, startUtc: slots[0].startUtc, source: "Phone",
    });
    // Same slot again → SLOT_TAKEN; a change with an outdated rowVersion → CONCURRENCY_CONFLICT.
    expect(await statusOf("POST", "/appointments", reception, { patientId: patient.id, doctorId: world.doctorB.doctorId, startUtc: slots[0].startUtc, source: "Phone" }))
      .toEqual({ status: 409, code: "SLOT_TAKEN" });
    await call("POST", `/appointments/${appt.id}/confirm`, reception, { rowVersion: appt.rowVersion });
    expect(await statusOf("PUT", `/appointments/${appt.id}/reschedule`, reception, { startUtc: slots[3].startUtc, rowVersion: appt.rowVersion }))
      .toEqual({ status: 409, code: "CONCURRENCY_CONFLICT" });

    // Made-up ids → 404, never 500.
    const fake = "11111111-2222-3333-4444-555555555555";
    for (const url of [`/patients/${fake}`, `/appointments/${fake}`, `/doctors/${fake}`]) expect((await statusOf("GET", url, reception)).status, url).toBe(404);
    expect((await statusOf("GET", `/visits/${fake}`, world.owner.token)).status).toBe(404);
    expect((await statusOf("GET", `/prescriptions/${fake}`, world.owner.token)).status).toBe(404);

    // Forged identity fields are ignored: the prescription takes patient/doctor/tenant from the visit and the caller.
    const doctorA = await login(world.doctorA.email);
    const rx = await call<{ patientId: string; doctorId: string }>("POST", "/prescriptions", doctorA, {
      visitId: world.sample.visitId, items: [{ medicationName: "Cetirizine" }],
      patientId: patient.id, doctorId: world.doctorB.doctorId, tenantId: fake, branchId: world.northBranchId,
    });
    expect(rx).toMatchObject({ patientId: world.sample.patientId, doctorId: world.doctorA.doctorId });
    // A forged tenantId on a new patient doesn't move it to another clinic.
    const created = await call<{ id: string }>("POST", "/patients", reception, {
      fullName: "مريض بمعرف مزور", mobileNumber: "0551234567", identityType: "None", gender: "Male", tenantId: fake,
    });
    expect((await statusOf("GET", `/patients/${created.id}`, await login(world.other.email))).status).toBe(404);
    expect((await statusOf("GET", `/patients/${created.id}`, reception)).status).toBe(200);

    // UI: a made-up patient id shows "not found", not someone else's record or a crash.
    await signIn(page, world.reception.email);
    await page.goto(`/patients/${fake}`);
    await expect(page.getByText(ar.common.notFoundTitle).or(page.getByText("المريض غير موجود.")).first()).toBeVisible();
  });

  test("another clinic gets 404 for every record of this clinic, and the UI shows nothing", async ({ page, world }) => {
    const other = await login(world.other.email);
    const s = world.sample;
    for (const url of [`/patients/${s.patientId}`, `/visits/${s.visitId}`, `/prescriptions/${s.prescriptionId}`, `/patients/${s.patientId}/visits`]) {
      expect((await statusOf("GET", url, other)).status, url).toBe(404);
    }
    expect((await statusOf("PUT", `/visits/${s.visitId}`, other, { clinical: { diagnosis: "x" }, rowVersion: "AAAAAAAAAAA=" })).status).toBe(404);

    await signIn(page, world.other.email);
    await page.goto(`/patients/${s.patientId}`);
    await page.waitForLoadState("networkidle");
    await expect(page.getByText(s.patientName)).toHaveCount(0);
    await expect(page.getByText(s.diagnosis)).toHaveCount(0);
  });

  test("a receptionist restricted to another branch can't see this branch's appointments", async ({ page, world }) => {
    const patient = await createPatient(world.owner.token, "بدر الدوسري");
    const date = riyadhDate(2);
    const slots = await call<{ startUtc: string }[]>("GET", `/doctors/${world.doctorA.doctorId}/availability?date=${date}`, world.owner.token);
    const appt = await call<{ id: string }>("POST", "/appointments", world.owner.token, {
      patientId: patient.id, doctorId: world.doctorA.doctorId, branchId: world.mainBranchId, startUtc: slots[5].startUtc, source: "Phone",
    });
    const north = await login(world.receptionNorth.email);
    expect((await statusOf("GET", `/appointments/${appt.id}`, north)).status).toBe(404);
    const list = await call<{ id: string }[]>("GET", `/appointments?date=${date}`, north);
    expect(list.map((x) => x.id)).not.toContain(appt.id);

    await signIn(page, world.receptionNorth.email);
    await page.goto("/appointments");
    await page.locator("#board-date").fill(date);
    await page.waitForLoadState("networkidle");
    await expect(page.getByText(patient.fullName)).toHaveCount(0);
  });

  test("a receptionist can't read the audit log", async ({ page, world }) => {
    expect((await statusOf("GET", "/audit-logs", await login(world.reception.email))).status).toBe(403);
    await signIn(page, world.reception.email);
    await expect(page.getByRole("navigation", { name: ar.shell.mainNav }).getByRole("link", { name: ar.nav.auditLog })).toHaveCount(0);
    await page.goto("/audit-log");
    await expect(page.getByText(ar.shell.forbiddenTitle)).toBeVisible();
  });

  test("a receptionist can't reach clinical records, through the API or by URL", async ({ page, world }) => {
    const reception = await login(world.reception.email);
    const s = world.sample;
    expect((await statusOf("GET", `/visits/${s.visitId}`, reception)).status).toBe(403);
    expect((await statusOf("GET", `/patients/${s.patientId}/visits`, reception)).status).toBe(403);
    expect((await statusOf("GET", `/prescriptions/${s.prescriptionId}`, reception)).status).toBe(403);
    expect((await statusOf("POST", "/prescriptions", reception, { visitId: s.visitId, items: [{ medicationName: "x" }] })).status).toBe(403);

    await signIn(page, world.reception.email);
    for (const url of [`/visits/${s.visitId}`, `/prescriptions/${s.prescriptionId}`]) {
      await page.goto(url);
      await expect(page.getByText(ar.shell.forbiddenTitle)).toBeVisible();
      await expect(page.getByText(s.diagnosis)).toHaveCount(0);
      await expect(page.getByText("Paracetamol")).toHaveCount(0);
    }
    await page.goto(`/patients/${s.patientId}`);
    await expect(page.getByRole("heading", { level: 1, name: s.patientName })).toBeVisible();
    await expect(page.getByText("بنسلين")).toHaveCount(0);
  });

  test("only the treating doctor documents, completes and prescribes", async ({ page, world }) => {
    const s = world.sample;
    const visit = await call<{ rowVersion: string }>("GET", `/visits/${s.visitId}`, world.doctorA.token);
    const edit = { clinical: { diagnosis: "تعديل غير مسموح" }, rowVersion: visit.rowVersion };

    // Another doctor (own-doctor scope) doesn't even see the visit.
    const doctorB = await login(world.doctorB.email);
    expect((await statusOf("PUT", `/visits/${s.visitId}`, doctorB, edit)).status).toBe(404);
    expect((await statusOf("POST", `/visits/${s.visitId}/complete`, doctorB, { rowVersion: visit.rowVersion })).status).toBe(404);
    expect([403, 404]).toContain((await statusOf("POST", "/prescriptions", doctorB, { visitId: s.visitId, items: [{ medicationName: "x" }] })).status);

    // The owner reads it but can't write clinical content or prescribe (GD-01, D8).
    const owner = await login(world.owner.email);
    expect(await statusOf("PUT", `/visits/${s.visitId}`, owner, edit)).toEqual({ status: 403, code: "VISIT_NOT_TREATING_DOCTOR" });
    expect(await statusOf("POST", "/prescriptions", owner, { visitId: s.visitId, items: [{ medicationName: "x" }] })).toEqual({
      status: 403, code: "PRESCRIBER_NOT_TREATING_DOCTOR",
    });
    const after = await call<{ clinical: { diagnosis: string }; rowVersion: string }>("GET", `/visits/${s.visitId}`, world.doctorA.token);
    expect(after.clinical.diagnosis).toBe(s.diagnosis);
    expect(after.rowVersion).toBe(visit.rowVersion);

    await signIn(page, world.owner.email);
    await page.goto(`/visits/${s.visitId}`);
    await expect(page.getByText(s.diagnosis)).toBeVisible();
    await expect(page.getByText(ar.visits.readOnlyNotTreating.split("(")[0].trim())).toBeVisible();
    await expect(page.getByRole("button", { name: ar.common.save, exact: true })).toHaveCount(0);
    await expect(page.getByRole("button", { name: ar.visits.complete })).toHaveCount(0);
    await expect(page.getByRole("button", { name: ar.prescriptions.new })).toHaveCount(0);
  });

  test("a disabled member loses access immediately, even with an open session", async ({ page, world }) => {
    const roles = await call<{ id: string; systemKey?: string }[]>("GET", "/roles", world.owner.token);
    const email = `${unique("leaver")}@example.com`;
    const seat = await call<{ membershipId: string }>("POST", "/staff", world.owner.token, {
      fullName: "موظف مغادر", email, roleIds: [roles.find((r) => r.systemKey === "receptionist")!.id], branchIds: [],
    });
    await call("POST", "/auth/accept-invitation", null, { token: await mailboxToken(email, "/accept-invitation"), newPassword: PASSWORD });
    const token = await login(email);

    await signIn(page, email);
    await page.goto("/patients");
    await expect(page.getByRole("heading", { level: 1, name: ar.patients.title })).toBeVisible();

    await call("POST", `/staff/${seat.membershipId}/disable`, await login(world.owner.email));
    expect(await statusOf("GET", "/patients", token)).toEqual({ status: 401, code: "NO_ACTIVE_MEMBERSHIP" });
    await page.goto("/patients");
    await page.waitForURL(/\/login/);
  });
});
