import fs from "node:fs";
import path from "node:path";
import { test as base, expect, type BrowserContext, type Page } from "@playwright/test";
import ar from "../../messages/ar.json";
import en from "../../messages/en.json";
import { env, PASSWORD } from "./api";
import { buildWorld, type World } from "./world";

export { expect };
export const messages = { ar, en } as const;
export type Locale = keyof typeof messages;

/** The world is built once per worker (the suite runs with one worker) and shared by every spec. */
export const test = base.extend<object, { world: World }>({
  world: [
    async ({}, use) => {
      await use(await buildWorld());
    },
    { scope: "worker", timeout: 120_000 },
  ],
});

export async function setLocale(context: BrowserContext, locale: Locale) {
  await context.addCookies([{ name: "NEXT_LOCALE", value: locale, url: env.webUrl }]);
}

/** Signs in through the real login form and waits for the landing page. */
export async function signIn(page: Page, email: string, password = PASSWORD, locale: Locale = "ar") {
  const t = messages[locale].auth.login;
  await setLocale(page.context(), locale);
  await page.goto("/login");
  await page.getByLabel(labelled(t.email)).fill(email);
  await page.getByLabel(labelled(t.password)).fill(password);
  await page.getByRole("button", { name: t.submit, exact: true }).click();
  await page.waitForURL(/\/today/);
}

export async function noHorizontalOverflow(page: Page): Promise<boolean> {
  return page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1);
}

const AXE_SOURCE = fs.readFileSync(path.join(require.resolve("axe-core"), "..", "axe.min.js"), "utf8");

/** axe WCAG 2.2 A/AA violations on the page (or inside `selector`), as "rule-id (n)" strings. */
export async function axeViolations(page: Page, selector?: string): Promise<string[]> {
  await page.addScriptTag({ content: AXE_SOURCE });
  return page.evaluate(async (sel) => {
    const axe = (window as unknown as { axe: { run: (ctx: unknown, opts: unknown) => Promise<{ violations: { id: string; nodes: unknown[] }[] }> } }).axe;
    const res = await axe.run(sel ? document.querySelector(sel) : document, {
      runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"] },
    });
    return res.violations.map((v) => `${v.id} (${v.nodes.length})`);
  }, selector ?? null);
}

/** Matches a form label exactly, with or without the required-field asterisk. */
export const labelled = (text: string) => new RegExp(`^${text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\s*\\*?$`);

/** Fills an ICU message's placeholders (only the simple `{name}` form used in labels). */
export const fmt = (template: string, values: Record<string, string | number>) =>
  template.replace(/\{(\w+)\}/g, (_, k: string) => String(values[k] ?? `{${k}}`));
