import { mailboxLink } from "./support/api";
import { expect, messages, test } from "./support/fixtures";
import { ownClinic } from "./support/sessions";

const ar = messages.ar;

test.describe("One-time links keep their token out of every request", () => {
  test("the emailed link carries the token in the fragment, and opening it sends the token nowhere", async ({ page }) => {
    const { email } = await ownClinic("links");
    const { token, separator } = await mailboxLink(email, "/verify-email");
    expect(separator, "emails use #token=, which a browser never sends to any server").toBe("#");

    const requested: string[] = [];
    page.on("request", (req) => requested.push(req.url()));
    const navigation = await page.goto(`/verify-email#token=${encodeURIComponent(token)}`);

    await expect(page.getByText(ar.auth.verify.success)).toBeVisible();
    // Nothing the browser requested — the page, scripts, the API call — has the token in its URL; the call that uses it
    // carries it in a POST body.
    expect(requested.filter((u) => u.includes(token)), "no request URL contains the token").toEqual([]);
    // The address bar is cleaned, so history and "copy link" don't keep it.
    expect(page.url()).not.toContain(token);
    expect(new URL(page.url()).hash).toBe("");

    const headers = navigation!.headers();
    expect(headers["referrer-policy"]).toBe("no-referrer");
    expect(headers["cache-control"]).toContain("no-store");
  });

  test("a link without a token explains itself instead of failing silently", async ({ page }) => {
    await page.goto("/reset-password");
    await expect(page.getByText(ar.auth.setPassword.missingToken)).toBeVisible();
  });
});
