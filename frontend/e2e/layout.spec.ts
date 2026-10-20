import type { Page } from "@playwright/test";
import { axeViolations, expect, noHorizontalOverflow, setLocale, signIn, test, type Locale } from "./support/fixtures";

const WIDTHS = [375, 768, 1024, 1440] as const;
const LOCALES: Locale[] = ["ar", "en"];

/** RTL/LTR, one h1, no sideways scrolling and no axe WCAG 2.2 AA violation, at every width. */
async function checkPage(page: Page, url: string, locale: Locale) {
  const problems: string[] = [];
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(url);
  await page.waitForLoadState("networkidle");
  await expect(page.locator("h1").first()).toBeVisible();
  const dir = await page.evaluate(() => document.documentElement.dir);
  if (dir !== (locale === "ar" ? "rtl" : "ltr")) problems.push(`dir=${dir}`);
  const h1 = await page.locator("h1").count();
  if (h1 !== 1) problems.push(`${h1} h1`);
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: width < 768 ? 812 : 900 });
    await page.waitForTimeout(150);
    if (!(await noHorizontalOverflow(page))) problems.push(`overflow@${width}`);
    const axe = await axeViolations(page);
    if (axe.length) problems.push(`axe@${width}: ${axe.join(", ")}`);
  }
  return problems;
}

test.describe("Layout, direction and accessibility", () => {
  for (const locale of LOCALES) {
    test(`public pages (${locale})`, async ({ page }) => {
      await setLocale(page.context(), locale);
      const problems: Record<string, string[]> = {};
      for (const url of ["/login", "/register", "/forgot-password"]) {
        const p = await checkPage(page, url, locale);
        if (p.length) problems[url] = p;
      }
      expect(problems).toEqual({});
    });

    test(`clinic pages as owner (${locale})`, async ({ page, world }) => {
      test.setTimeout(240_000);
      await signIn(page, world.owner.email, undefined, locale);
      const doctorId = world.doctorA.doctorId;
      const s = world.sample;
      const problems: Record<string, string[]> = {};
      for (const url of [
        "/today", "/appointments", "/patients", "/patients/new", `/patients/${s.patientId}`, "/doctors", `/doctors/${doctorId}`,
        "/follow-ups", "/staff", "/settings", "/subscription", "/account", "/audit-log", `/visits/${s.visitId}`, `/prescriptions/${s.prescriptionId}`,
      ]) {
        const p = await checkPage(page, url, locale);
        if (p.length) problems[url] = p;
      }
      expect(problems).toEqual({});
    });

    test(`doctor's own visit and Today (${locale})`, async ({ page, world }) => {
      await signIn(page, world.doctorA.email, undefined, locale);
      const problems: Record<string, string[]> = {};
      for (const url of ["/today", `/visits/${world.sample.visitId}`]) {
        const p = await checkPage(page, url, locale);
        if (p.length) problems[url] = p;
      }
      expect(problems).toEqual({});
    });
  }
});
