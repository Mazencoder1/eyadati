import { call, login, mailboxToken, mobile, PASSWORD, platformLogin, statusOf, unique, type SessionData } from "./support/api";
import { expect, labelled, messages, signIn, test } from "./support/fixtures";

const ar = messages.ar;
const NEW_PASSWORD = "E2e-Reset-Password-1";

test.describe("Account lifecycle", () => {
  test("a clinic registers through the form, signs out, and protected pages need sign-in again", async ({ page }) => {
    const r = ar.auth.register;
    const email = `${unique("signup")}@example.com`;
    const clinic = `عيادة التسجيل ${unique("r").slice(2, 8)}`;
    await page.goto("/register");
    await page.getByLabel(labelled(r.clinicName)).fill(clinic);
    await page.getByLabel(labelled(r.city)).fill("الدمام");
    await page.getByLabel(labelled(r.clinicPhone)).fill(mobile());
    await page.getByLabel(labelled(r.ownerFullName)).fill("نجلاء المؤسسة");
    await page.getByLabel(labelled(r.ownerMobile)).fill(mobile());
    await page.getByLabel(labelled(r.email)).fill(email);
    await page.getByLabel(labelled(r.password)).fill(PASSWORD);
    await page.getByLabel(r.acceptTerms).check();
    await page.getByRole("button", { name: r.submit }).click();
    await page.waitForURL(/\/today/);
    await expect(page.getByText(clinic)).toBeVisible();

    await page.getByRole("button", { name: ar.shell.logout }).click();
    await page.waitForURL(/\/login/);
    await page.goto("/patients");
    await page.waitForURL(/\/login/);
  });

  test("a staff member accepts the invitation from the email link and signs in", async ({ page, world }) => {
    const roles = await call<{ id: string; systemKey?: string }[]>("GET", "/roles", world.owner.token);
    const email = `${unique("invited")}@example.com`;
    await call("POST", "/staff", await login(world.owner.email), {
      fullName: "رنا المدعوة", email, roleIds: [roles.find((x) => x.systemKey === "staff")!.id], branchIds: [],
    });
    const token = await mailboxToken(email, "/accept-invitation");
    await page.goto(`/accept-invitation#token=${encodeURIComponent(token)}`);
    await page.getByLabel(labelled(ar.auth.setPassword.password)).fill(PASSWORD);
    await page.getByLabel(labelled(ar.auth.setPassword.confirm)).fill(PASSWORD);
    await page.getByRole("button", { name: ar.auth.invitation.submit }).click();
    await expect(page.getByText(ar.auth.invitation.doneTitle).first()).toBeVisible();
    // The same link can't be used twice.
    expect((await statusOf("POST", "/auth/accept-invitation", "", { token, newPassword: PASSWORD })).status).toBeGreaterThanOrEqual(400);
    await signIn(page, email);
    await expect(page.getByText(world.clinicName)).toBeVisible();
  });

  test("password reset by email link: the new password works, the old one doesn't", async ({ page }) => {
    const email = `${unique("forgot")}@example.com`;
    await call<SessionData>("POST", "/auth/register", null, {
      clinicName: `عيادة الاستعادة ${unique("f").slice(2, 8)}`, city: "الرياض", clinicPhone: mobile(), ownerFullName: "سلوى",
      email, password: PASSWORD, ownerMobile: mobile(), acceptTerms: true,
    });
    await page.goto("/forgot-password");
    await page.getByLabel(labelled(ar.auth.forgot.email)).fill(email);
    await page.getByRole("button", { name: ar.auth.forgot.submit }).click();
    await expect(page.getByText(ar.auth.forgot.sentTitle).first()).toBeVisible();

    const token = await mailboxToken(email, "/reset-password");
    // An email sent before the link format changed carries the token in the query string: it must still work.
    await page.goto(`/reset-password?token=${encodeURIComponent(token)}`);
    await expect(page.getByLabel(labelled(ar.auth.setPassword.password))).toBeVisible();
    expect(page.url(), "the token is removed from the address bar once read").not.toContain(token);
    await page.getByLabel(labelled(ar.auth.setPassword.password)).fill(NEW_PASSWORD);
    await page.getByLabel(labelled(ar.auth.setPassword.confirm)).fill(NEW_PASSWORD);
    await page.getByRole("button", { name: ar.auth.reset.submit }).click();
    await expect(page.getByText(ar.auth.reset.doneTitle).first()).toBeVisible();

    expect((await statusOf("POST", "/auth/login", "", { email, password: PASSWORD })).status).toBe(401);
    await signIn(page, email, NEW_PASSWORD);
  });

  test("a suspended clinic is refused immediately, with one clear screen", async ({ page }) => {
    const email = `${unique("suspend")}@example.com`;
    const session = await call<SessionData>("POST", "/auth/register", null, {
      clinicName: `عيادة الإيقاف ${unique("s").slice(2, 8)}`, city: "الرياض", clinicPhone: mobile(), ownerFullName: "هند",
      email, password: PASSWORD, ownerMobile: mobile(), acceptTerms: true,
    });
    await signIn(page, email);
    await page.goto("/patients");
    await expect(page.getByRole("heading", { level: 1, name: ar.patients.title })).toBeVisible();

    const admin = await platformLogin();
    await call("POST", `/platform/tenants/${session.tenant!.id}/suspend`, admin);
    expect(await statusOf("GET", "/patients", session.accessToken)).toEqual({ status: 403, code: "TENANT_SUSPENDED" });
    // The open session: the next request is refused and the app shows one clear screen instead of failing pages.
    await page.getByRole("navigation", { name: ar.shell.mainNav }).getByRole("link", { name: ar.nav.appointments }).click();
    await expect(page.getByText(ar.shell.suspendedTitle)).toBeVisible();
    await expect(page.getByRole("navigation", { name: ar.shell.mainNav })).toHaveCount(0);
    // A reload can't renew the session of a suspended clinic: back to sign-in.
    await page.reload();
    await page.waitForURL(/\/login/);
  });
});
