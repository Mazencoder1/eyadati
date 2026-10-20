import type { Page } from "@playwright/test";
import { call, mobile, PASSWORD, unique, type SessionData } from "./api";

/** A clinic of its own, so ending its sessions or changing its password can't affect the shared test clinic. */
export async function ownClinic(label: string) {
  const email = `${unique(label)}@example.com`;
  const session = await call<SessionData>("POST", "/auth/register", null, {
    clinicName: `عيادة الجلسات ${unique("s").slice(2, 8)}`, city: "الرياض", clinicPhone: mobile(), ownerFullName: "منى الجلسات",
    email, password: PASSWORD, ownerMobile: mobile(), acceptTerms: true,
  });
  return { email, tenantId: session.tenant!.id };
}

/** Remembers the access token the app is currently using, from the API's own responses (the app keeps it in memory only). */
export function trackAccessToken(page: Page) {
  const seen = { token: "" };
  page.on("response", async (res) => {
    if (res.request().method() !== "POST" || !res.ok()) return;
    if (!/\/auth\/(login|refresh|change-password|switch-tenant)$/.test(new URL(res.url()).pathname)) return;
    try {
      seen.token = ((await res.json()) as { data: { accessToken: string } }).data.accessToken;
    } catch {
      // not a session response
    }
  });
  return seen;
}
