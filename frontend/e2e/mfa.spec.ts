import { call, env, forgetPlatformSession, freshTotp, platformLogin, platformSecret, rememberPlatformSecret } from "./support/api";
import { axeViolations, expect, labelled, messages, setLocale, test } from "./support/fixtures";
import type { Page } from "@playwright/test";

const ar = messages.ar;
const en = messages.en;

/**
 * Platform-admin two-step verification in a real browser: enrolment (setup key, recovery codes shown once), the
 * sign-in challenge with a TOTP and with a recovery code, replacing the recovery codes, removing MFA. The account is
 * the local bootstrap super admin; the suite starts by taking it back to "no authenticator" and ends enrolled again.
 * Every TOTP use needs a new 30-second step, so these tests wait for the clock now and then.
 */
test.describe.serial("Platform admin two-step verification", () => {
  test.setTimeout(240_000);
  let recoveryCodes: string[] = [];
  let secret = "";

  test.beforeAll(async () => {
    const token = await platformLogin(); // enrols through the API if the local database is new
    await call("POST", "/auth/mfa/remove", token, { currentPassword: env.platformPassword, totpCode: await freshTotp(platformSecret()!) });
    forgetPlatformSession();
  });

  test.afterAll(async () => {
    forgetPlatformSession();
    await platformLogin(); // leave the account enrolled for the other specs
  });

  async function passwordStep(page: Page, locale: "ar" | "en" = "ar") {
    const t = messages[locale].auth.login;
    await setLocale(page.context(), locale);
    await page.goto("/login");
    await page.getByLabel(labelled(t.email)).fill(env.platformEmail);
    await page.getByLabel(labelled(t.password)).fill(env.platformPassword);
    await page.getByRole("button", { name: t.submit, exact: true }).click();
  }

  /** Completes the challenge step with a recovery code (keeps authenticator steps for the tests that need them). */
  async function signInWithRecovery(page: Page, code: string) {
    await passwordStep(page);
    await page.getByRole("button", { name: ar.auth.mfa.challenge.useRecovery }).click();
    await page.getByLabel(labelled(ar.auth.mfa.challenge.recoveryCode)).fill(code);
    await page.getByRole("button", { name: ar.auth.mfa.challenge.submit, exact: true }).click();
    await page.waitForURL(/\/today/);
  }

  test("enrolment: setup key and link, recovery codes shown once, then the landing page offers the security page", async ({ page }) => {
    await passwordStep(page);
    await expect(page.getByRole("heading", { level: 1, name: ar.auth.mfa.enroll.title })).toBeVisible();

    secret = (await page.getByTestId("mfa-secret").innerText()).replace(/\s+/g, "");
    expect(secret).toMatch(/^[A-Z2-7]{32}$/);
    await expect(page.getByRole("link", { name: ar.auth.mfa.enroll.openLink })).toHaveAttribute("href", /^otpauth:\/\/totp\//);
    expect(await axeViolations(page)).toEqual([]);

    // A wrong code keeps the setup on screen and creates nothing.
    await page.getByLabel(labelled(ar.auth.mfa.enroll.code)).fill("000000");
    await page.getByRole("button", { name: ar.auth.mfa.enroll.submit, exact: true }).click();
    // Scoped to <main>: Next.js keeps an empty role="alert" route announcer on every page, which would match at once
    // and let the next fill race the refusal (the form clears the code field when the server answers).
    await expect(page.getByRole("main").getByRole("alert")).toBeVisible();
    await expect(page.getByTestId("mfa-secret")).toBeVisible();

    await page.getByLabel(labelled(ar.auth.mfa.enroll.code)).fill(await freshTotp(secret));
    await page.getByRole("button", { name: ar.auth.mfa.enroll.submit, exact: true }).click();

    // The recovery codes come first; there is no session behind them until "continue".
    await expect(page.getByRole("heading", { level: 1, name: ar.auth.mfa.codes.title })).toBeVisible();
    const items = page.getByRole("list", { name: ar.auth.mfa.codes.listLabel }).getByRole("listitem");
    await expect(items).toHaveCount(10);
    recoveryCodes = (await items.allInnerTexts()).map((c) => c.trim());
    expect(new Set(recoveryCodes).size).toBe(10);
    expect(await axeViolations(page)).toEqual([]);
    const continueButton = page.getByRole("button", { name: ar.auth.mfa.codes.continue, exact: true });
    await expect(continueButton).toBeDisabled();
    await page.getByRole("checkbox", { name: ar.auth.mfa.codes.saved }).check();
    await continueButton.click();

    await page.waitForURL(/\/today/);
    await expect(page.getByText(ar.shell.noClinicTitle)).toBeVisible();
    await page.getByRole("link", { name: ar.shell.twoStepLink }).click();
    await page.waitForURL(/\/security/);
    await expect(page.getByRole("heading", { level: 1, name: ar.security.title })).toBeVisible();
    expect(await axeViolations(page)).toEqual([]);
    rememberPlatformSecret(secret);
    // Nothing secret was left in the browser's storage.
    const stored = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
    expect(stored).not.toContain(secret);
    for (const code of recoveryCodes) expect(stored).not.toContain(code);
  });

  test("signing in asks for the code: a wrong one is refused, the right one opens the session", async ({ page }) => {
    await passwordStep(page);
    await expect(page.getByRole("heading", { level: 1, name: ar.auth.mfa.challenge.title })).toBeVisible();
    expect(await axeViolations(page)).toEqual([]);
    // No session exists yet: the protected page bounces back to sign-in.
    const code = await freshTotp(secret);

    await page.getByLabel(labelled(ar.auth.mfa.challenge.code)).fill("000000");
    await page.getByRole("button", { name: ar.auth.mfa.challenge.submit, exact: true }).click();
    await expect(page.getByRole("main").getByRole("alert")).toBeVisible();
    await expect(page).toHaveURL(/\/login/);

    await page.getByLabel(labelled(ar.auth.mfa.challenge.code)).fill(code);
    await page.getByRole("button", { name: ar.auth.mfa.challenge.submit, exact: true }).click();
    await page.waitForURL(/\/today/);
    await expect(page.getByText(ar.shell.noClinicTitle)).toBeVisible();
  });

  test("a recovery code signs in once; the same code is refused afterwards", async ({ page, browser }) => {
    await signInWithRecovery(page, recoveryCodes[0]);

    const second = await (await browser.newContext()).newPage();
    await passwordStep(second);
    await second.getByRole("button", { name: ar.auth.mfa.challenge.useRecovery }).click();
    await second.getByLabel(labelled(ar.auth.mfa.challenge.recoveryCode)).fill(recoveryCodes[0]);
    await second.getByRole("button", { name: ar.auth.mfa.challenge.submit, exact: true }).click();
    await expect(second.getByRole("main").getByRole("alert")).toBeVisible();
    await expect(second).toHaveURL(/\/login/);
    await second.context().close();
  });

  test("the challenge also works in English (LTR) and stays accessible", async ({ page }) => {
    await passwordStep(page, "en");
    await expect(page.getByRole("heading", { level: 1, name: en.auth.mfa.challenge.title })).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("dir", "ltr");
    expect(await axeViolations(page)).toEqual([]);
  });

  test("replacing the recovery codes needs a current code and shows the ten new ones once", async ({ page, browser }) => {
    await signInWithRecovery(page, recoveryCodes[1]);
    await page.getByRole("link", { name: ar.shell.twoStepLink }).click();
    await page.waitForURL(/\/security/);

    await page.getByRole("button", { name: ar.security.recovery.action }).click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel(labelled(ar.security.recovery.code)).fill("000000");
    await dialog.getByRole("button", { name: ar.security.recovery.confirm }).click();
    await expect(dialog.getByRole("alert")).toBeVisible();

    await dialog.getByLabel(labelled(ar.security.recovery.code)).fill(await freshTotp(secret));
    await dialog.getByRole("button", { name: ar.security.recovery.confirm }).click();
    const items = dialog.getByRole("list", { name: ar.auth.mfa.codes.listLabel }).getByRole("listitem");
    await expect(items).toHaveCount(10);
    const fresh = (await items.allInnerTexts()).map((c) => c.trim());
    expect(fresh.some((c) => recoveryCodes.includes(c))).toBe(false);
    expect(await axeViolations(page)).toEqual([]);
    await dialog.getByRole("button", { name: ar.security.recovery.done }).click();
    await expect(dialog).toHaveCount(0);

    // An old code is dead; a new one works.
    const other = await (await browser.newContext()).newPage();
    await passwordStep(other);
    await other.getByRole("button", { name: ar.auth.mfa.challenge.useRecovery }).click();
    await other.getByLabel(labelled(ar.auth.mfa.challenge.recoveryCode)).fill(recoveryCodes[2]);
    await other.getByRole("button", { name: ar.auth.mfa.challenge.submit, exact: true }).click();
    await expect(other.getByRole("main").getByRole("alert")).toBeVisible();
    await other.context().close();
    recoveryCodes = fresh;
  });

  test("removing MFA needs the password and a code, signs out everywhere and asks to enrol again", async ({ page }) => {
    await signInWithRecovery(page, recoveryCodes[0]);
    await page.getByRole("link", { name: ar.shell.twoStepLink }).click();
    await page.waitForURL(/\/security/);

    await page.getByRole("button", { name: ar.security.remove.action }).click();
    const dialog = page.getByRole("dialog");
    const code = await freshTotp(secret); // a wrong password does not consume the code, so one code serves both tries
    await dialog.getByLabel(labelled(ar.security.remove.password)).fill("Not-The-Password-1");
    await dialog.getByLabel(labelled(ar.security.remove.code)).fill(code);
    await dialog.getByRole("button", { name: ar.security.remove.confirm }).click();
    await expect(dialog.getByRole("alert")).toBeVisible();

    await dialog.getByLabel(labelled(ar.security.remove.password)).fill(env.platformPassword);
    await dialog.getByLabel(labelled(ar.security.remove.code)).fill(code);
    await dialog.getByRole("button", { name: ar.security.remove.confirm }).click();
    await page.waitForURL(/\/login/);

    // The next sign-in is an enrolment, not a session.
    await passwordStep(page);
    await expect(page.getByRole("heading", { level: 1, name: ar.auth.mfa.enroll.title })).toBeVisible();
    await expect(page.getByTestId("mfa-secret")).toBeVisible();
  });
});
