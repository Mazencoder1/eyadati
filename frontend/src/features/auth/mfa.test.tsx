import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import { SecurityPage } from "@/features/account/security-page";
import { ApiError } from "@/lib/api/client";
import type { Session } from "@/lib/api/types";
import { LoginForm } from "./login-form";

const SECRET = "JBSWY3DPEHPK3PXP";
const CODES = Array.from({ length: 10 }, (_, i) => `AAAA-BBBB-CCCC-DD${String(i).padStart(2, "2")}`);

const router = { replace: vi.fn() };
vi.mock("next/navigation", () => ({ useRouter: () => router, useSearchParams: () => new URLSearchParams() }));

const mfa = {
  verify: vi.fn(),
  enrollStart: vi.fn(),
  enrollComplete: vi.fn(),
  adoptSession: vi.fn(),
  regenerateRecoveryCodes: vi.fn(),
  remove: vi.fn(),
};
const api = { auth: { login: vi.fn(), mfa } };
const auth = { api, status: "anonymous" as string, session: null as Session | null, signedOut: false };
vi.mock("@/lib/auth/session", () => ({ useAuth: () => auth, useApi: () => api }));

const adminSession = (platformRole = "SuperAdmin"): Session => ({
  accessToken: "access-token-1",
  accessTokenExpiresAtUtc: "2026-10-08T07:15:00Z",
  user: { id: "u1", fullName: "Platform Admin", email: "root@platform.test", emailConfirmed: true, platformRole, preferredLocale: "ar" },
  tenant: null,
  permissions: ["Platform.Tenants.View"],
});

const apiError = (status: number, code: string, message: string, details?: unknown) =>
  new ApiError({ status, code, message, details });

beforeAll(() => {
  HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) {
    this.setAttribute("open", "");
  };
  HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) {
    this.removeAttribute("open");
  };
});

beforeEach(() => {
  router.replace.mockReset();
  for (const fn of [api.auth.login, ...Object.values(mfa)]) fn.mockReset();
  auth.status = "anonymous";
  auth.session = null;
  localStorage.clear();
  sessionStorage.clear();
});
afterEach(cleanup);

function renderWith(ui: React.ReactElement, locale: "ar" | "en" = "ar") {
  return render(
    <NextIntlClientProvider locale={locale} messages={locale === "ar" ? ar : en} timeZone="Asia/Riyadh">
      <div dir={locale === "ar" ? "rtl" : "ltr"}>{ui}</div>
    </NextIntlClientProvider>,
  );
}

const field = (label: string) => screen.getByLabelText(new RegExp(`^${label}`));
const type = (label: string, value: string) => fireEvent.change(field(label), { target: { value } });

async function signInWith(emailAndPassword = { email: "root@platform.test", password: "Platform-Admin-Passw0rd!" }) {
  type(ar.auth.login.email, emailAndPassword.email);
  type(ar.auth.login.password, emailAndPassword.password);
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: ar.auth.login.submit }));
  });
}

describe("Sign-in: the MFA challenge", () => {
  const challenge = () => apiError(409, "MFA_REQUIRED", "MFA", { challengeToken: "challenge-token", expiresAtUtc: "2026-10-08T07:05:00Z" });

  it("moves from the password to the code step, without keeping the password around", async () => {
    api.auth.login.mockRejectedValue(challenge());
    renderWith(<LoginForm />);
    await signInWith();

    expect(await screen.findByRole("heading", { name: ar.auth.mfa.challenge.title })).toBeTruthy();
    expect(screen.queryByLabelText(new RegExp(`^${ar.auth.login.password}`))).toBeNull();
    expect(router.replace).not.toHaveBeenCalled();
  });

  it("sends the TOTP (spaces removed) with the challenge token and then goes to the app", async () => {
    api.auth.login.mockRejectedValue(challenge());
    mfa.verify.mockResolvedValue(adminSession());
    renderWith(<LoginForm />);
    await signInWith();

    type(ar.auth.mfa.challenge.code, "123 456");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: ar.auth.mfa.challenge.submit }));
    });

    expect(mfa.verify).toHaveBeenCalledWith("challenge-token", { totpCode: "123456" });
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/today"));
  });

  it("shows the server's message for a wrong code, clears the box and stays on the step", async () => {
    api.auth.login.mockRejectedValue(challenge());
    mfa.verify.mockRejectedValue(apiError(401, "INVALID_MFA_CODE", "رمز التحقق غير صحيح أو مستخدم من قبل."));
    renderWith(<LoginForm />);
    await signInWith();

    type(ar.auth.mfa.challenge.code, "000000");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: ar.auth.mfa.challenge.submit }));
    });

    expect((await screen.findByRole("alert")).textContent).toContain("رمز التحقق غير صحيح");
    expect((field(ar.auth.mfa.challenge.code) as HTMLInputElement).value).toBe("");
    expect(router.replace).not.toHaveBeenCalled();
  });

  it("offers a recovery code instead, and sends it as a recovery code", async () => {
    api.auth.login.mockRejectedValue(challenge());
    mfa.verify.mockResolvedValue(adminSession());
    renderWith(<LoginForm />);
    await signInWith();

    fireEvent.click(screen.getByRole("button", { name: ar.auth.mfa.challenge.useRecovery }));
    type(ar.auth.mfa.challenge.recoveryCode, "aaaa-bbbb-cccc-dd22");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: ar.auth.mfa.challenge.submit }));
    });

    expect(mfa.verify).toHaveBeenCalledWith("challenge-token", { recoveryCode: "aaaa-bbbb-cccc-dd22" });
  });

  it("can go back to the password step", async () => {
    api.auth.login.mockRejectedValue(challenge());
    renderWith(<LoginForm />);
    await signInWith();
    fireEvent.click(await screen.findByRole("button", { name: ar.auth.mfa.challenge.back }));
    expect(screen.getByRole("heading", { name: ar.auth.login.title })).toBeTruthy();
  });

  it("works the same in English (LTR)", async () => {
    api.auth.login.mockRejectedValue(challenge());
    renderWith(<LoginForm />, "en");
    type(en.auth.login.email, "root@platform.test");
    type(en.auth.login.password, "Platform-Admin-Passw0rd!");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: en.auth.login.submit }));
    });
    expect(await screen.findByRole("heading", { name: en.auth.mfa.challenge.title })).toBeTruthy();
  });
});

describe("Sign-in: MFA enrolment", () => {
  const enrolment = () => apiError(409, "MFA_ENROLLMENT_REQUIRED", "MFA", { enrollmentToken: "enroll-token", expiresAtUtc: "2026-10-08T07:10:00Z" });
  const setup = { secret: SECRET, setupUri: `otpauth://totp/ClinicSaaS:root%40platform.test?secret=${SECRET}`, pendingToken: "pending-token", expiresAtUtc: "2026-10-08T07:10:00Z" };

  it("shows the setup key once (grouped, with a link and a manual key), and nothing is written to storage", async () => {
    api.auth.login.mockRejectedValue(enrolment());
    mfa.enrollStart.mockResolvedValue(setup);
    renderWith(<LoginForm />);
    await signInWith();

    expect(mfa.enrollStart).toHaveBeenCalledWith("enroll-token");
    expect((await screen.findByTestId("mfa-secret")).textContent).toBe("JBSW Y3DP EHPK 3PXP");
    expect(screen.getByRole("link", { name: ar.auth.mfa.enroll.openLink }).getAttribute("href")).toBe(setup.setupUri);
    expect(localStorage.length + sessionStorage.length).toBe(0);
  });

  it("shows the recovery codes first and adopts the session only after they were acknowledged", async () => {
    api.auth.login.mockRejectedValue(enrolment());
    mfa.enrollStart.mockResolvedValue(setup);
    const session = adminSession();
    mfa.enrollComplete.mockResolvedValue({ session, recoveryCodes: CODES });
    renderWith(<LoginForm />);
    await signInWith();

    await screen.findByTestId("mfa-secret");
    type(ar.auth.mfa.enroll.code, "654 321");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: ar.auth.mfa.enroll.submit }));
    });
    expect(mfa.enrollComplete).toHaveBeenCalledWith("pending-token", "654321");

    const list = await screen.findByRole("list", { name: ar.auth.mfa.codes.listLabel });
    expect(within(list).getAllByRole("listitem")).toHaveLength(10);
    expect(mfa.adoptSession).not.toHaveBeenCalled();
    expect(router.replace).not.toHaveBeenCalled();
    expect(screen.queryByTestId("mfa-secret")).toBeNull();

    const next = screen.getByRole("button", { name: ar.auth.mfa.codes.continue }) as HTMLButtonElement;
    expect(next.disabled).toBe(true);
    fireEvent.click(screen.getByRole("checkbox", { name: ar.auth.mfa.codes.saved }));
    expect(next.disabled).toBe(false);
    fireEvent.click(next);

    expect(mfa.adoptSession).toHaveBeenCalledWith(session);
    expect(router.replace).toHaveBeenCalledWith("/today");
    expect(localStorage.length + sessionStorage.length).toBe(0);
  });

  it("keeps the setup on screen when the code is wrong", async () => {
    api.auth.login.mockRejectedValue(enrolment());
    mfa.enrollStart.mockResolvedValue(setup);
    mfa.enrollComplete.mockRejectedValue(apiError(401, "INVALID_MFA_CODE", "رمز التحقق غير صحيح أو مستخدم من قبل."));
    renderWith(<LoginForm />);
    await signInWith();

    await screen.findByTestId("mfa-secret");
    type(ar.auth.mfa.enroll.code, "000000");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: ar.auth.mfa.enroll.submit }));
    });
    expect((await screen.findByRole("alert")).textContent).toContain("رمز التحقق غير صحيح");
    expect(screen.getByTestId("mfa-secret")).toBeTruthy();
    expect(mfa.adoptSession).not.toHaveBeenCalled();
  });

  it("offers to start again when the setup cannot be started", async () => {
    api.auth.login.mockRejectedValue(enrolment());
    mfa.enrollStart.mockRejectedValue(apiError(401, "INVALID_MFA_TOKEN", "انتهت صلاحية خطوة التحقق. سجّل الدخول من جديد."));
    renderWith(<LoginForm />);
    await signInWith();

    expect((await screen.findByRole("alert")).textContent).toContain("انتهت صلاحية");
    fireEvent.click(screen.getByRole("button", { name: ar.auth.mfa.enroll.restart }));
    expect(screen.getByRole("heading", { name: ar.auth.login.title })).toBeTruthy();
  });
});

describe("Security page", () => {
  beforeEach(() => {
    auth.status = "authenticated";
    auth.session = adminSession();
  });

  it("replaces the recovery codes with a current TOTP and shows the new ones once", async () => {
    mfa.regenerateRecoveryCodes.mockResolvedValue({ recoveryCodes: CODES });
    renderWith(<SecurityPage />);

    fireEvent.click(screen.getByRole("button", { name: ar.security.recovery.action }));
    type(ar.security.recovery.code, "111 222");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: ar.security.recovery.confirm }));
    });

    expect(mfa.regenerateRecoveryCodes).toHaveBeenCalledWith("111222");
    expect(within(await screen.findByRole("list", { name: ar.auth.mfa.codes.listLabel })).getAllByRole("listitem")).toHaveLength(10);

    fireEvent.click(screen.getByRole("button", { name: ar.security.recovery.done }));
    expect(screen.queryByRole("list", { name: ar.auth.mfa.codes.listLabel })).toBeNull();
  });

  it("removes MFA with the password and a code, then leaves for the sign-in page", async () => {
    mfa.remove.mockResolvedValue(undefined);
    renderWith(<SecurityPage />);

    fireEvent.click(screen.getByRole("button", { name: ar.security.remove.action }));
    type(ar.security.remove.password, "Platform-Admin-Passw0rd!");
    type(ar.security.remove.code, "333 444");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: ar.security.remove.confirm }));
    });

    expect(mfa.remove).toHaveBeenCalledWith("Platform-Admin-Passw0rd!", { totpCode: "333444" });
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/login"));
  });

  it("can remove MFA with a recovery code instead", async () => {
    mfa.remove.mockResolvedValue(undefined);
    renderWith(<SecurityPage />);

    fireEvent.click(screen.getByRole("button", { name: ar.security.remove.action }));
    fireEvent.click(screen.getByRole("button", { name: ar.security.remove.useRecovery }));
    type(ar.security.remove.password, "Platform-Admin-Passw0rd!");
    type(ar.security.remove.recoveryCode, "AAAA-BBBB-CCCC-DD22");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: ar.security.remove.confirm }));
    });
    expect(mfa.remove).toHaveBeenCalledWith("Platform-Admin-Passw0rd!", { recoveryCode: "AAAA-BBBB-CCCC-DD22" });
  });

  it("stays signed in and shows the server's message when the password or code is wrong", async () => {
    mfa.remove.mockRejectedValue(apiError(401, "INVALID_CREDENTIALS", "البريد الإلكتروني أو كلمة المرور غير صحيحة."));
    renderWith(<SecurityPage />);

    fireEvent.click(screen.getByRole("button", { name: ar.security.remove.action }));
    type(ar.security.remove.password, "wrong");
    type(ar.security.remove.code, "333444");
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: ar.security.remove.confirm }));
    });

    expect((await screen.findByRole("alert")).textContent).toContain("غير صحيحة");
    expect(router.replace).not.toHaveBeenCalledWith("/login");
  });

  it("sends a clinic user (no platform role) home instead of showing platform controls", async () => {
    auth.session = adminSession("None");
    renderWith(<SecurityPage />);
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith("/today"));
    expect(screen.queryByRole("button", { name: ar.security.remove.action })).toBeNull();
  });

  it("sends an anonymous visitor to sign in", async () => {
    auth.status = "anonymous";
    auth.session = null;
    renderWith(<SecurityPage />);
    await waitFor(() => expect(router.replace).toHaveBeenCalledWith(expect.stringContaining("/login")));
  });
});
