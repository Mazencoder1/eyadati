import { env, PASSWORD } from "./support/api";
import { expect, messages, signIn, test } from "./support/fixtures";
import { ownClinic } from "./support/sessions";

const ar = messages.ar;

test.describe("Refresh token never reaches page script", () => {
  test("a normal reload renews the session through the httpOnly cookie only", async ({ page }) => {
    const { email } = await ownClinic("refresh");
    const refreshes: { header: string | undefined; body: string }[] = [];
    page.on("response", async (res) => {
      if (res.request().method() === "POST" && new URL(res.url()).pathname.endsWith("/auth/refresh") && res.ok()) {
        refreshes.push({ header: (await res.request().headerValue("x-client-type")) ?? undefined, body: await res.text() });
      }
    });
    await signIn(page, email);
    await page.reload();
    await expect(page.getByRole("navigation", { name: ar.shell.mainNav })).toBeVisible();

    expect(refreshes.length).toBeGreaterThan(0);
    for (const r of refreshes) {
      expect(r.header, "the web app never asks for native mode").toBeUndefined();
      expect(r.body.toLowerCase()).not.toContain("refreshtoken");
    }
    const rt = (await page.context().cookies()).find((c) => c.name === "rt");
    expect(rt, "refresh cookie is set").toBeTruthy();
    expect(rt).toMatchObject({ httpOnly: true, sameSite: "Strict", path: "/api/v1/auth" });
    expect(await page.evaluate(() => document.cookie)).not.toContain("rt=");
  });

  test("script running in the page can't get a refresh token, even by asking for native mode", async ({ page, request }) => {
    const { email } = await ownClinic("xss");
    await signIn(page, email);

    // What an XSS payload could try from inside the web app.
    const attempt = await page.evaluate(async (api) => {
      const url = `${api}/api/v1/auth/refresh`;
      const out: { native: string; plain?: { status: number; body: string }; cookie: string; storage: string } = {
        native: "", cookie: document.cookie, storage: JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }),
      };
      try {
        const res = await fetch(url, { method: "POST", credentials: "include", headers: { "Content-Type": "application/json", "X-Client-Type": "mobile" }, body: "{}" });
        out.native = `${res.status} ${await res.text()}`;
      } catch {
        out.native = "blocked";
      }
      const plain = await fetch(url, { method: "POST", credentials: "include", headers: { "Content-Type": "application/json" }, body: "{}" });
      out.plain = { status: plain.status, body: await plain.text() };
      return out;
    }, env.apiUrl);

    expect(attempt.native, "CORS refuses the X-Client-Type header before the request is sent").toBe("blocked");
    expect(attempt.plain?.status).toBe(200);
    expect(attempt.plain?.body.toLowerCase()).not.toContain("refreshtoken");
    expect(attempt.cookie).not.toContain("rt=");
    expect(attempt.storage.toLowerCase()).not.toContain("refresh");

    // The server refuses it too (no reliance on CORS): anything carrying browser headers can't use native mode.
    const fromBrowser = await request.post(`${env.apiUrl}/api/v1/auth/login`, {
      headers: { "X-Client-Type": "mobile", Origin: env.webUrl },
      data: { email, password: PASSWORD },
    });
    expect(fromBrowser.status()).toBe(403);
    expect(((await fromBrowser.json()) as { code: string }).code).toBe("CLIENT_TYPE_NOT_ALLOWED");
    expect(await fromBrowser.text()).not.toContain("refreshToken");

    // A real native client (no browser headers) still gets its token in the body.
    const native = await request.post(`${env.apiUrl}/api/v1/auth/login`, {
      headers: { "X-Client-Type": "mobile" },
      data: { email, password: PASSWORD },
    });
    expect(native.status()).toBe(200);
    expect(((await native.json()) as { data: { refreshToken?: string } }).data.refreshToken).toBeTruthy();
  });

  test("several tabs loading at the same moment keep the session (P3-SEC-25)", async ({ page }) => {
    // Each tab renews its session through the one shared cookie. The server rotates the refresh token and treats a
    // replayed one as theft, so tabs that refresh at the same instant (browser restore, links opened together) must
    // take turns, or the first one's rotation turns the others' requests into "reuse" and ends the session everywhere.
    const { email } = await ownClinic("tabs");
    await signIn(page, email);
    const context = page.context();
    const tabs = [page, ...(await Promise.all([1, 2, 3].map(() => context.newPage())))];

    for (let round = 0; round < 2; round++) {
      await Promise.all(tabs.map((tab) => tab.goto("/today")));
      for (const tab of tabs) await expect(tab.getByRole("navigation", { name: ar.shell.mainNav })).toBeVisible();
    }
    // Still signed in afterwards: a fresh tab renews once more.
    const later = await context.newPage();
    await later.goto("/today");
    await expect(later.getByRole("navigation", { name: ar.shell.mainNav })).toBeVisible();
  });
});
