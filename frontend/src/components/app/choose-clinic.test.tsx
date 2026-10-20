import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import ar from "../../../messages/ar.json";
import type { Session, TenantSummary } from "@/lib/api/types";

// A session without a clinic (platform login without tenantId, T-008) must render a chooser, not crash.
const state: { session: Session; tenants: TenantSummary[]; switchTenant: ReturnType<typeof vi.fn> } = {
  session: null as unknown as Session,
  tenants: [],
  switchTenant: vi.fn(),
};

vi.mock("next/navigation", () => ({ useRouter: () => ({ replace: vi.fn(), push: vi.fn() }), usePathname: () => "/today" }));
vi.mock("@/lib/auth/session", () => ({
  useAuth: () => ({
    session: state.session,
    api: { auth: { tenants: async () => state.tenants, switchTenant: state.switchTenant } },
  }),
}));

const { ChooseClinic } = await import("./app-shell");

afterEach(() => {
  cleanup();
  state.switchTenant.mockReset();
});

const session = (platformRole: string): Session => ({
  accessToken: "t",
  accessTokenExpiresAtUtc: "2026-10-06T00:00:00Z",
  user: { id: "u1", fullName: "مدير المنصة", email: "admin@example.test", emailConfirmed: true, platformRole, preferredLocale: "ar" },
  tenant: null,
  permissions: ["Platform.Tenants.View"],
});

const wrap = (ui: ReactNode) =>
  render(
    <NextIntlClientProvider locale="ar" messages={ar} timeZone="Asia/Riyadh">
      {ui}
    </NextIntlClientProvider>,
  );

describe("ChooseClinic (session without a clinic)", () => {
  it("platform account with memberships: lists the clinics and switches on click", async () => {
    state.session = session("SuperAdmin");
    state.tenants = [{ id: "t1", name: "عيادة النخبة", slug: "a" }, { id: "t2", name: "عيادة الشفاء", slug: "b" }];
    const logout = vi.fn();
    wrap(<ChooseClinic onLogout={logout} />);
    expect(await screen.findByRole("heading", { name: ar.shell.platformTitle })).toBeTruthy();
    expect(screen.getByText(ar.shell.platformChoose)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "عيادة الشفاء" }));
    await waitFor(() => expect(state.switchTenant).toHaveBeenCalledWith("t2"));
    fireEvent.click(screen.getByRole("button", { name: ar.shell.logout }));
    expect(logout).toHaveBeenCalled();
  });

  it("platform account without memberships: explains instead of failing", async () => {
    state.session = session("Support");
    state.tenants = [];
    wrap(<ChooseClinic onLogout={() => {}} />);
    expect(await screen.findByText(ar.shell.platformNoClinics)).toBeTruthy();
  });

  it("regular account without an active clinic keeps the existing message", async () => {
    state.session = session("None");
    state.tenants = [];
    wrap(<ChooseClinic onLogout={() => {}} />);
    expect(await screen.findByText(ar.shell.noClinicDescription)).toBeTruthy();
  });
});
