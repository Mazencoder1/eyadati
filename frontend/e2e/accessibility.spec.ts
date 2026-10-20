import type { Locator, Page } from "@playwright/test";
import { call, riyadhDate } from "./support/api";
import { axeViolations, expect, fmt, labelled, messages, signIn, test, type Locale } from "./support/fixtures";
import { createPatient } from "./support/world";

/** Opens a dialog with `open`, checks axe inside it, that focus moved in, and that Esc closes it and returns focus. */
async function checkDialog(page: Page, name: string, open: () => Promise<void>, opener: Locator) {
  await open();
  const dialog = page.getByRole("dialog", { name });
  await expect(dialog).toBeVisible();
  const problems: string[] = [];
  const axe = await axeViolations(page, "dialog[open]");
  if (axe.length) problems.push(`axe: ${axe.join(", ")}`);
  if (!(await page.evaluate(() => !!document.activeElement?.closest("dialog[open]")))) problems.push("focus not inside");
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();
  if (!(await opener.evaluate((el) => el === document.activeElement))) problems.push("focus not returned to opener");
  return problems;
}

for (const locale of ["ar", "en"] as Locale[]) {
  const m = messages[locale];
  const a = m.appointments;

  test.describe(`Accessibility (${locale})`, () => {
    test("dialogs: axe-clean, focus moves in, Esc closes and focus returns", async ({ page, world }) => {
      test.setTimeout(120_000);
      const patient = await createPatient(world.owner.token, locale === "ar" ? "لمى العنزي" : "جود العنزي");
      const date = riyadhDate(3);
      const slots = await call<{ startUtc: string }[]>("GET", `/doctors/${world.doctorA.doctorId}/availability?date=${date}`, world.owner.token);
      await call("POST", "/appointments", world.owner.token, {
        patientId: patient.id, doctorId: world.doctorA.doctorId, branchId: world.mainBranchId, startUtc: slots[2].startUtc, source: "Phone",
      });

      await signIn(page, world.owner.email, undefined, locale);
      const problems: Record<string, string[]> = {};
      const record = (key: string, p: string[]) => {
        if (p.length) problems[key] = p;
      };

      await page.goto("/appointments");
      const book = page.getByRole("button", { name: a.book });
      record("booking", await checkDialog(page, a.booking.title, () => book.click(), book));
      const walkIn = page.getByRole("button", { name: a.walkIn });
      record("walk-in", await checkDialog(page, a.booking.walkInTitle, () => walkIn.click(), walkIn));

      await page.locator("#board-date").fill(date);
      await page.waitForLoadState("networkidle");
      const menu = page.getByRole("button", { name: fmt(a.actions.more, { patient: patient.fullName }) });
      const viaMenu = (item: string) => async () => {
        await menu.click();
        await page.getByRole("menuitem", { name: item }).click();
      };
      record("reschedule", await checkDialog(page, a.reschedule.title, viaMenu(a.actions.reschedule), menu));
      record("cancel", await checkDialog(page, a.cancelDialog.title, viaMenu(a.actions.cancel), menu));

      await page.goto("/staff");
      const invite = page.getByRole("button", { name: m.staff.invite });
      record("invite", await checkDialog(page, m.staff.inviteTitle, () => invite.click(), invite));

      await page.goto("/settings");
      await page.getByRole("tab", { name: m.settings.tabs.services }).click();
      const addService = page.getByRole("button", { name: m.settings.services.add });
      record("service", await checkDialog(page, m.settings.services.add, () => addService.click(), addService));

      expect(problems).toEqual({});
    });

    test("doctor's prescription and completion dialogs", async ({ page, world }) => {
      await signIn(page, world.doctorA.email, undefined, locale);
      await page.goto(`/visits/${world.sample.visitId}`);
      const problems: Record<string, string[]> = {};
      const rx = page.getByRole("button", { name: m.prescriptions.new });
      const p1 = await checkDialog(page, m.prescriptions.new, () => rx.click(), rx);
      if (p1.length) problems.prescription = p1;
      const complete = page.getByRole("button", { name: m.visits.complete });
      const p2 = await checkDialog(page, m.visits.completeTitle, () => complete.click(), complete);
      if (p2.length) problems.complete = p2;
      expect(problems).toEqual({});
    });

    test("keyboard: skip link, mobile navigation, first invalid field gets focus", async ({ page, world }) => {
      await signIn(page, world.reception.email, undefined, locale);

      // Skip link is the first tab stop and moves focus to the main content.
      await page.goto("/patients");
      await expect(page.getByRole("heading", { level: 1, name: m.patients.title })).toBeVisible();
      await page.keyboard.press("Tab");
      const skip = page.getByRole("link", { name: m.shell.skipToContent });
      await expect(skip).toBeFocused();
      await page.keyboard.press("Enter");
      await expect(page.locator("#main")).toBeFocused();

      // Mobile navigation: a modal opened from the keyboard; Esc closes it and focus returns to the menu button.
      await page.setViewportSize({ width: 375, height: 812 });
      const openMenu = page.getByRole("button", { name: m.shell.openMenu });
      await openMenu.focus();
      await page.keyboard.press("Enter");
      const nav = page.getByRole("dialog").getByRole("navigation", { name: m.shell.mainNav });
      await expect(nav).toBeVisible();
      expect(await axeViolations(page, "dialog[open]")).toEqual([]);
      await page.keyboard.press("Escape");
      await expect(nav).toBeHidden();
      await expect(openMenu).toBeFocused();
      await page.setViewportSize({ width: 1440, height: 900 });

      // Submitting an empty form puts focus on the first invalid field, which is marked and described.
      await page.goto("/patients/new");
      await page.getByRole("button", { name: m.patients.create }).click();
      const name = page.getByLabel(labelled(m.patients.fields.fullName));
      await expect(name).toBeFocused();
      await expect(name).toHaveAttribute("aria-invalid", "true");
      const describedBy = await name.getAttribute("aria-describedby");
      await expect(page.locator(`[id="${describedBy}"]`)).toHaveText(m.validation.required);
    });

    test("language switch changes direction and lang, keeping the page", async ({ page, world }) => {
      await signIn(page, world.reception.email, undefined, locale);
      await page.goto("/patients");
      const other = locale === "ar" ? "en" : "ar";
      await page.getByRole("button", { name: new RegExp(m.common.switchLanguage) }).click();
      await expect(page.locator("html")).toHaveAttribute("lang", other);
      await expect(page.locator("html")).toHaveAttribute("dir", other === "ar" ? "rtl" : "ltr");
      await expect(page).toHaveURL(/\/patients$/);
      await expect(page.getByRole("heading", { level: 1, name: messages[other].patients.title })).toBeVisible();
    });
  });
}
