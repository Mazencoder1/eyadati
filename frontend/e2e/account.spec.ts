import { call, login, mobile, PASSWORD, statusOf, unique, type SessionData } from "./support/api";
import { expect, labelled, messages, signIn, test } from "./support/fixtures";

const ar = messages.ar;
const t = ar.account;

test.describe("Account", () => {
  // A clinic of its own: changing this password must not affect the shared test clinic.
  let email = "";
  test.beforeEach(async () => {
    email = `${unique("account")}@example.com`;
    await call<SessionData>("POST", "/auth/register", null, {
      clinicName: `عيادة الحساب ${unique("a").slice(2)}`, city: "جدة", clinicPhone: mobile(), ownerFullName: "رهف الحساب",
      email, password: PASSWORD, ownerMobile: mobile(), acceptTerms: true,
    });
  });

  test("lists signed-in devices and ends another session", async ({ page }) => {
    const otherDevice = await login(email); // a second session (no browser user agent)
    await signIn(page, email);
    await page.getByRole("link", { name: new RegExp(t.title) }).first().click();
    await page.waitForURL(/\/account/);
    await expect(page.getByRole("heading", { level: 1, name: t.title })).toBeVisible();
    await expect(page.getByText(t.sessions.current)).toBeVisible();

    const end = page.getByRole("button", { name: new RegExp(`^${t.sessions.end}|^إنهاء جلسة`) });
    await expect(end).toHaveCount(2); // registration session + API login; the current one is not offered
    await end.first().click();
    await page.getByRole("dialog", { name: t.sessions.confirmTitle }).getByRole("button", { name: t.sessions.end }).click();
    await expect(page.getByText(t.sessions.ended)).toBeVisible();
    await expect(end).toHaveCount(1);
    // Ending a session also ends the access token already issued for it, at once (GD-03). The list is newest first,
    // so the device ended above is the API sign-in made after registration.
    expect(await statusOf("GET", "/patients", otherDevice)).toEqual({ status: 401, code: "UNAUTHORIZED" });
  });

  test("rejects a wrong current password on the field, then changes the password", async ({ page }) => {
    await signIn(page, email);
    await page.goto("/account");
    const current = page.getByLabel(labelled(t.password.current));
    await current.fill("Wrong-Password-1");
    await page.getByLabel(labelled(t.password.new)).first().fill("New-Password-2026");
    await page.getByLabel(labelled(t.password.confirm)).fill("New-Password-2026");
    await page.getByRole("button", { name: t.password.submit }).click();
    await expect(current).toHaveAttribute("aria-invalid", "true");
    await expect(page).toHaveURL(/\/account/); // still signed in

    await current.fill(PASSWORD);
    await page.getByRole("button", { name: t.password.submit }).click();
    await expect(page.getByText(t.password.done)).toBeVisible();
    await expect(current).toHaveValue("");

    expect((await statusOf("POST", "/auth/login", "", { email, password: PASSWORD })).status).toBe(401);
    expect(await login(email, "New-Password-2026")).toBeTruthy();
    // The page keeps working on the new session.
    await page.reload();
    await expect(page.getByRole("heading", { level: 1, name: t.title })).toBeVisible();
  });
});
