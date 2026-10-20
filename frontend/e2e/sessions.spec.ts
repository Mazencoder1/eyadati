import { call, login, PASSWORD, platformLogin, statusOf } from "./support/api";
import { expect, labelled, messages, signIn, test } from "./support/fixtures";
import { ownClinic, trackAccessToken } from "./support/sessions";

const ar = messages.ar;
const ENDED = { status: 401, code: "UNAUTHORIZED" };

test.describe("Sessions end at once (GD-03, GD-15)", () => {
  test("logging out ends the access token immediately, not 15 minutes later", async ({ page }) => {
    const { email } = await ownClinic("logout");
    const seen = trackAccessToken(page);
    await signIn(page, email);
    await expect.poll(() => seen.token).not.toBe("");
    const token = seen.token;
    expect((await statusOf("GET", "/patients", token)).status).toBe(200);

    await page.getByRole("button", { name: ar.shell.logout }).click();
    await page.waitForURL(/\/login/);

    expect(await statusOf("GET", "/patients", token)).toEqual(ENDED);
    expect(await statusOf("GET", "/auth/me", token)).toEqual(ENDED);
  });

  test("changing the password ends every other device and the old token; the page carries on with the new session", async ({ page }) => {
    const { email } = await ownClinic("pwchange");
    const otherDevice = await login(email);
    const seen = trackAccessToken(page);
    await signIn(page, email);
    await expect.poll(() => seen.token).not.toBe("");
    const before = seen.token;

    await page.goto("/account");
    const t = ar.account;
    await page.getByLabel(labelled(t.password.current)).fill(PASSWORD);
    await page.getByLabel(labelled(t.password.new)).first().fill("Sessions-New-Pass-2026");
    await page.getByLabel(labelled(t.password.confirm)).fill("Sessions-New-Pass-2026");
    await page.getByRole("button", { name: t.password.submit }).click();
    await expect(page.getByText(t.password.done)).toBeVisible();

    expect(await statusOf("GET", "/patients", otherDevice)).toEqual(ENDED);
    expect(await statusOf("GET", "/patients", before)).toEqual(ENDED);
    expect((await statusOf("GET", "/patients", seen.token)).status).toBe(200);

    // The browser keeps working without signing in again.
    await page.reload();
    await expect(page.getByRole("heading", { level: 1, name: t.title })).toBeVisible();
  });

  test("a suspended then reactivated clinic: old sessions stay dead, a new sign-in works", async ({ page }) => {
    const { email, tenantId } = await ownClinic("reactivate");
    const apiToken = await login(email);
    const seen = trackAccessToken(page);
    await signIn(page, email);
    await expect.poll(() => seen.token).not.toBe("");
    const browserToken = seen.token;

    const admin = await platformLogin();
    await call("POST", `/platform/tenants/${tenantId}/suspend`, admin);
    expect(await statusOf("GET", "/patients", browserToken)).toEqual({ status: 403, code: "TENANT_SUSPENDED" });
    expect(await statusOf("GET", "/patients", apiToken)).toEqual({ status: 403, code: "TENANT_SUSPENDED" });

    await call("POST", `/platform/tenants/${tenantId}/reactivate`, admin);
    expect(await statusOf("GET", "/patients", browserToken)).toEqual(ENDED);
    expect(await statusOf("GET", "/patients", apiToken)).toEqual(ENDED);

    // The open tab can't renew a session that suspension revoked: it goes back to sign-in …
    await page.reload();
    await page.waitForURL(/\/login/);
    // … and signing in again after reactivation works.
    await signIn(page, email);
    await expect(page.getByRole("navigation", { name: ar.shell.mainNav })).toBeVisible();
  });
});
