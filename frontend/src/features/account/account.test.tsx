import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { ApiError } from "@/lib/api/client";
import type { SessionInfo } from "@/lib/api/types";
import { describeDevice } from "./account-model";
import { AccountPage } from "./account-page";

const CHROME_WIN = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36";
const SAFARI_IOS = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

const sessions: SessionInfo[] = [
  { sessionId: "s-current", tenantId: "t1", lastActivityAtUtc: "2026-10-08T07:00:00Z", ipAddress: "10.0.0.1", userAgent: CHROME_WIN, isCurrent: true },
  { sessionId: "s-phone", tenantId: "t1", lastActivityAtUtc: "2026-10-07T18:30:00Z", ipAddress: "10.0.0.2", userAgent: SAFARI_IOS, isCurrent: false },
];

const api = {
  auth: {
    sessions: vi.fn(),
    tenants: vi.fn(),
    revokeSession: vi.fn(),
    changePassword: vi.fn(),
  },
};
const toast = vi.fn();
const session = {
  accessToken: "token-1",
  user: { id: "u1", fullName: "سارة أحمد", email: "sara@clinic.sa" },
};

vi.mock("@/lib/auth/session", () => ({ useAuth: () => ({ api, session }), useApi: () => api }));
vi.mock("@/features/clinic/clinic-context", () => ({ useClinic: () => ({ timeZone: "Asia/Riyadh" }) }));
vi.mock("@/components/ui/toast", () => ({ useToast: () => toast }));

beforeAll(() => {
  HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
    this.setAttribute("open", "");
  };
  HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
    this.removeAttribute("open");
  };
});

beforeEach(() => {
  api.auth.sessions.mockReset().mockResolvedValue(sessions);
  api.auth.tenants.mockReset().mockResolvedValue([{ id: "t1", name: "عيادة النخبة", slug: "nokhba" }]);
  api.auth.revokeSession.mockReset().mockResolvedValue(undefined);
  api.auth.changePassword.mockReset();
  toast.mockReset();
});
afterEach(cleanup);

function setup(locale: "ar" | "en" = "ar") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Riyadh">
      <div dir={locale === "ar" ? "rtl" : "ltr"}>
        <AccountPage />
      </div>
    </NextIntlClientProvider>,
  );
}

const a = ar.account;
const fill = (label: string, value: string) => fireEvent.change(screen.getByLabelText(new RegExp(`^${label}`)), { target: { value } });

describe("describeDevice", () => {
  it("names common browsers and systems, and returns nulls for unknown agents", () => {
    expect(describeDevice(CHROME_WIN)).toEqual({ browser: "Chrome", os: "Windows" });
    expect(describeDevice(SAFARI_IOS)).toEqual({ browser: "Safari", os: "iOS" });
    expect(describeDevice("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/141.0 Safari/537.36 Edg/141.0").browser).toBe("Edge");
    expect(describeDevice(null)).toEqual({ browser: null, os: null });
  });
});

describe("Account page — sessions", () => {
  it("lists sessions; the current device is marked and cannot be ended here", async () => {
    setup();
    expect(await screen.findByText("Chrome على Windows")).toBeTruthy();
    expect(screen.getByText(a.sessions.current)).toBeTruthy();
    const endButtons = screen.getAllByRole("button", { name: /إنهاء جلسة/ });
    expect(endButtons).toHaveLength(1);
    expect(endButtons[0].getAttribute("aria-label")).toBe("إنهاء جلسة Safari على iOS");
    expect(screen.getAllByText(/عيادة النخبة/)).toHaveLength(2);
  });

  it("ends another session after confirmation, then reloads the list", async () => {
    setup();
    fireEvent.click(await screen.findByRole("button", { name: "إنهاء جلسة Safari على iOS" }));
    const dialog = await screen.findByRole("dialog", { name: a.sessions.confirmTitle });
    await act(async () => fireEvent.click(within(dialog).getByRole("button", { name: a.sessions.end })));

    expect(api.auth.revokeSession).toHaveBeenCalledWith("s-phone");
    expect(toast).toHaveBeenCalledWith({ tone: "success", title: a.sessions.ended });
    await waitFor(() => expect(api.auth.sessions).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("closing the confirmation ends nothing", async () => {
    setup();
    fireEvent.click(await screen.findByRole("button", { name: "إنهاء جلسة Safari على iOS" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: ar.common.cancel }));
    expect(api.auth.revokeSession).not.toHaveBeenCalled();
  });

  it("shows an error with retry when the sessions can't be loaded", async () => {
    api.auth.sessions.mockRejectedValue(new ApiError({ status: 0, code: "NETWORK_ERROR", message: "تعذر الاتصال بالخادم." }));
    setup();
    expect((await screen.findByRole("alert")).textContent).toContain("تعذر الاتصال بالخادم.");
    api.auth.sessions.mockResolvedValue(sessions);
    fireEvent.click(screen.getByRole("button", { name: a.sessions.retry }));
    expect(await screen.findByText("Chrome على Windows")).toBeTruthy();
  });
});

describe("Account page — password", () => {
  it("validates on the client before calling the API", async () => {
    setup();
    fill(a.password.current, "OldPassword1");
    fill(a.password.new, "short");
    fill(a.password.confirm, "different");
    fireEvent.click(screen.getByRole("button", { name: a.password.submit }));
    expect(await screen.findByText(ar.validation.weak_password)).toBeTruthy();
    expect(screen.getByText(ar.validation.passwords_mismatch)).toBeTruthy();

    fill(a.password.new, "OldPassword1");
    fill(a.password.confirm, "OldPassword1");
    fireEvent.click(screen.getByRole("button", { name: a.password.submit }));
    expect(await screen.findByText(ar.validation.same_password)).toBeTruthy();
    expect(api.auth.changePassword).not.toHaveBeenCalled();
  });

  it("shows a wrong current password on that field", async () => {
    api.auth.changePassword.mockRejectedValue(
      new ApiError({ status: 401, code: "INVALID_CREDENTIALS", message: "البريد الإلكتروني أو كلمة المرور غير صحيحة." }),
    );
    setup();
    fill(a.password.current, "WrongPassword1");
    fill(a.password.new, "NewPassword123");
    fill(a.password.confirm, "NewPassword123");
    await act(async () => fireEvent.click(screen.getByRole("button", { name: a.password.submit })));

    const current = screen.getByLabelText(new RegExp(`^${a.password.current}`));
    expect(current.getAttribute("aria-invalid")).toBe("true");
    expect(document.getElementById(current.getAttribute("aria-describedby")!)?.textContent).toBe(
      "البريد الإلكتروني أو كلمة المرور غير صحيحة.",
    );
    expect(toast).not.toHaveBeenCalled();
  });

  it("changes the password, confirms it and clears the form", async () => {
    api.auth.changePassword.mockResolvedValue({});
    setup();
    fill(a.password.current, "OldPassword1");
    fill(a.password.new, "NewPassword123");
    fill(a.password.confirm, "NewPassword123");
    await act(async () => fireEvent.click(screen.getByRole("button", { name: a.password.submit })));

    expect(api.auth.changePassword).toHaveBeenCalledWith("OldPassword1", "NewPassword123");
    expect(toast).toHaveBeenCalledWith({ tone: "success", title: a.password.done });
    expect((screen.getByLabelText(new RegExp(`^${a.password.new}`)) as HTMLInputElement).value).toBe("");
  });
});

describe("Account page — English", () => {
  it("renders in English, left to right", async () => {
    const { container } = setup("en");
    expect(await screen.findByText("Chrome on Windows")).toBeTruthy();
    expect(screen.getByRole("heading", { level: 1, name: "My account" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Change password" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "End session on Safari on iOS" })).toBeTruthy();
    expect(container.querySelector("[dir]")?.getAttribute("dir")).toBe("ltr");
  });
});
