import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiClient, ApiError, ClientErrorCodes } from "./client";
import type { Session } from "./types";

const session = (token: string): Session => ({
  accessToken: token,
  accessTokenExpiresAtUtc: "2026-10-06T10:15:00Z",
  user: { id: "u1", fullName: "Owner", email: "o@c.sa", emailConfirmed: true, platformRole: "None", preferredLocale: "ar" },
  tenant: { id: "t1", name: "Clinic", slug: "clinic" },
  permissions: ["Patients.View"],
});

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

const unauthorized = () => json(401, { success: false, code: "UNAUTHORIZED", message: "سجّل الدخول", correlationId: "c1" });

type Call = { url: string; init: RequestInit };

/** Scripted fetch: each handler receives the call and returns a Response. */
function fakeFetch(handler: (call: Call, index: number) => Response | Promise<Response>) {
  const calls: Call[] = [];
  const fn = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const call = { url: String(input), init: init ?? {} };
    calls.push(call);
    return handler(call, calls.length - 1);
  });
  return { fn: fn as unknown as typeof fetch, calls };
}

const header = (call: Call, name: string) => (call.init.headers as Record<string, string> | undefined)?.[name];
const isRefresh = (call: Call) => call.url.endsWith("/api/v1/auth/refresh");

afterEach(() => vi.restoreAllMocks());

describe("ApiClient", () => {
  it("unwraps success envelopes and sends language + bearer token", async () => {
    const { fn, calls } = fakeFetch(() => json(200, { success: true, data: { id: "p1" } }));
    const client = new ApiClient({ baseUrl: "http://api.test/", fetchImpl: fn, getLanguage: () => "ar" });
    client.setSession(session("A"));

    await expect(client.get("/patients/p1")).resolves.toEqual({ id: "p1" });
    expect(calls[0].url).toBe("http://api.test/api/v1/patients/p1");
    expect(header(calls[0], "Authorization")).toBe("Bearer A");
    expect(header(calls[0], "Accept-Language")).toBe("ar");
    expect(calls[0].init.credentials).toBe("include");
  });

  it("drops empty query values and maps paged envelopes", async () => {
    const { fn, calls } = fakeFetch(() =>
      json(200, { success: true, data: [{ id: 1 }], meta: { page: 2, pageSize: 1, totalCount: 5, totalPages: 5 } }),
    );
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn });
    const page = await client.getPaged("/patients", { query: { page: 2, search: "", sort: undefined, gender: null } });
    expect(calls[0].url).toBe("http://api.test/api/v1/patients?page=2");
    expect(page).toEqual({ items: [{ id: 1 }], meta: { page: 2, pageSize: 1, totalCount: 5, totalPages: 5 } });
  });

  it("refreshes once on 401 and retries the original request with the new token", async () => {
    const { fn, calls } = fakeFetch((call) => {
      if (isRefresh(call)) return json(200, { success: true, data: session("B") });
      return header(call, "Authorization") === "Bearer B" ? json(200, { success: true, data: "ok" }) : unauthorized();
    });
    const onSessionChange = vi.fn();
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn, onSessionChange });
    client.setSession(session("A"));

    await expect(client.get("/patients")).resolves.toBe("ok");
    expect(calls.map((c) => (isRefresh(c) ? "refresh" : header(c, "Authorization")))).toEqual(["Bearer A", "refresh", "Bearer B"]);
    // The refresh call carries no bearer token and relies on the httpOnly cookie.
    const refreshCall = calls.find(isRefresh)!;
    expect(header(refreshCall, "Authorization")).toBeUndefined();
    expect(refreshCall.init.credentials).toBe("include");
    expect(onSessionChange).toHaveBeenLastCalledWith(expect.objectContaining({ accessToken: "B" }));
  });

  it("shares one refresh between concurrent 401s (refresh tokens rotate; reuse would end the session)", async () => {
    let releaseRefresh!: () => void;
    const refreshGate = new Promise<void>((r) => (releaseRefresh = r));
    const { fn, calls } = fakeFetch(async (call) => {
      if (isRefresh(call)) {
        await refreshGate;
        return json(200, { success: true, data: session("B") });
      }
      return header(call, "Authorization") === "Bearer B" ? json(200, { success: true, data: call.url }) : unauthorized();
    });
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn });
    client.setSession(session("A"));

    const all = Promise.all([client.get("/a"), client.get("/b"), client.get("/c")]);
    await vi.waitFor(() => expect(calls.filter(isRefresh)).toHaveLength(1));
    releaseRefresh();
    await expect(all).resolves.toHaveLength(3);
    expect(calls.filter(isRefresh)).toHaveLength(1);
  });

  it("takes the cross-tab lock around the refresh call so tabs never present the same cookie at once (P3-SEC-25)", async () => {
    const held: string[] = [];
    let inside = false;
    const request = vi.fn(async (name: string, task: () => Promise<unknown>) => {
      held.push(name);
      inside = true;
      try {
        return await task();
      } finally {
        inside = false;
      }
    });
    vi.stubGlobal("navigator", { ...navigator, locks: { request } });
    let refreshedInsideLock = false;
    const { fn } = fakeFetch((call) => {
      if (isRefresh(call)) {
        refreshedInsideLock = inside;
        return json(200, { success: true, data: session("B") });
      }
      return unauthorized();
    });
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn });

    await expect(client.refresh()).resolves.toEqual(expect.objectContaining({ accessToken: "B" }));
    expect(held).toEqual(["clinic-saas.session-refresh"]);
    expect(refreshedInsideLock).toBe(true);
    vi.unstubAllGlobals();
  });

  it("ends the session when refresh fails and surfaces the original 401", async () => {
    const { fn, calls } = fakeFetch((call) =>
      isRefresh(call) ? json(401, { success: false, code: "INVALID_REFRESH_TOKEN", message: "انتهت الجلسة" }) : unauthorized(),
    );
    const onSessionChange = vi.fn();
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn, onSessionChange });
    client.setSession(session("A"));

    const error = await client.get("/patients").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(401);
    expect(calls).toHaveLength(2); // original + refresh, no retry
    expect(onSessionChange).toHaveBeenLastCalledWith(null);
    expect(client.hasAccessToken).toBe(false);
  });

  it("waits for Retry-After and retries a rate-limited refresh once instead of signing out", async () => {
    vi.useFakeTimers();
    let refreshCalls = 0;
    const { fn } = fakeFetch((call) => {
      if (!isRefresh(call)) return json(200, { success: true, data: null });
      refreshCalls++;
      return refreshCalls === 1
        ? new Response(JSON.stringify({ success: false, code: "RATE_LIMITED", message: "طلبات كثيرة" }), {
            status: 429,
            headers: { "content-type": "application/json", "retry-after": "3" },
          })
        : json(200, { success: true, data: session("C") });
    });
    const onSessionChange = vi.fn();
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn, onSessionChange });

    const pending = client.refresh();
    await vi.advanceTimersByTimeAsync(3000);
    await expect(pending).resolves.toMatchObject({ accessToken: "C" });
    expect(refreshCalls).toBe(2);
    expect(onSessionChange).not.toHaveBeenCalledWith(null);
    vi.useRealTimers();
  });

  it("retries only once even if the retried request is still unauthorized", async () => {
    const { fn, calls } = fakeFetch((call) => (isRefresh(call) ? json(200, { success: true, data: session("B") }) : unauthorized()));
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn });
    client.setSession(session("A"));

    await expect(client.get("/patients")).rejects.toMatchObject({ status: 401 });
    expect(calls.filter((c) => !isRefresh(c))).toHaveLength(2);
    expect(calls.filter(isRefresh)).toHaveLength(1);
  });

  it("does not refresh or re-send a rejected password check (one wrong password = one failed attempt)", async () => {
    const { fn, calls } = fakeFetch(() => json(401, { success: false, code: "INVALID_CREDENTIALS", message: "كلمة المرور غير صحيحة" }));
    const onSessionChange = vi.fn();
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn, onSessionChange });
    client.setSession(session("A"));
    onSessionChange.mockClear();

    await expect(client.post("/auth/change-password", { currentPassword: "x", newPassword: "y" })).rejects.toMatchObject({
      status: 401,
      code: "INVALID_CREDENTIALS",
    });
    expect(calls).toHaveLength(1);
    expect(calls.filter(isRefresh)).toHaveLength(0);
    expect(onSessionChange).not.toHaveBeenCalled();
    expect(client.hasAccessToken).toBe(true);
  });

  it("does not refresh or re-send a rejected MFA code (one wrong code = one failed attempt)", async () => {
    const { fn, calls } = fakeFetch(() => json(401, { success: false, code: "INVALID_MFA_CODE", message: "رمز التحقق غير صحيح" }));
    const onSessionChange = vi.fn();
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn, onSessionChange });
    client.setSession(session("A"));
    onSessionChange.mockClear();

    await expect(client.post("/auth/mfa/recovery-codes/regenerate", { totpCode: "123456" })).rejects.toMatchObject({
      status: 401,
      code: "INVALID_MFA_CODE",
    });
    expect(calls).toHaveLength(1);
    expect(calls.filter(isRefresh)).toHaveLength(0);
    expect(onSessionChange).not.toHaveBeenCalled();
    expect(client.hasAccessToken).toBe(true);
  });

  it("never refreshes or sends a token for anonymous endpoints", async () => {
    const { fn, calls } = fakeFetch(() => json(401, { success: false, code: "INVALID_CREDENTIALS", message: "بيانات الدخول غير صحيحة" }));
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn });
    client.setSession(session("A"));

    await expect(client.post("/auth/login", { email: "x", password: "y" }, { anonymous: true })).rejects.toMatchObject({
      code: "INVALID_CREDENTIALS",
      message: "بيانات الدخول غير صحيحة",
    });
    expect(calls).toHaveLength(1);
    expect(header(calls[0], "Authorization")).toBeUndefined();
  });

  it("maps the unified error body (code, localized message, field errors) and flags plan upgrades", async () => {
    const { fn } = fakeFetch(() =>
      json(403, {
        success: false,
        code: "USAGE_LIMIT_REACHED",
        message: "وصلت إلى الحد المسموح في باقتك.",
        correlationId: "cid-9",
        details: { limitKey: "max_patients", max: 100 },
      }),
    );
    const onError = vi.fn();
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn, onError });
    const error = (await client.post("/patients", {}).catch((e: unknown) => e)) as ApiError;

    expect(error.isUpgradeRequired).toBe(true);
    expect(error.message).toBe("وصلت إلى الحد المسموح في باقتك.");
    expect(error.correlationId).toBe("cid-9");
    expect(error.details).toEqual({ limitKey: "max_patients", max: 100 });
    expect(onError).toHaveBeenCalledWith(error);
  });

  it("keeps per-field validation errors", async () => {
    const { fn } = fakeFetch(() =>
      json(400, { success: false, code: "VALIDATION_FAILED", message: "صحح الحقول", errors: { mobileNumber: ["رقم الجوال غير صحيح"] } }),
    );
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn });
    const error = (await client.post("/patients", {}).catch((e: unknown) => e)) as ApiError;
    expect(error.isValidation).toBe(true);
    expect(error.errors).toEqual({ mobileNumber: ["رقم الجوال غير صحيح"] });
  });

  it("reports network failures with a localized fallback message", async () => {
    const fn = vi.fn(async () => {
      throw new TypeError("Failed to fetch");
    }) as unknown as typeof fetch;
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn, fallbackMessage: () => "تعذر الاتصال" });
    await expect(client.get("/x")).rejects.toMatchObject({ code: ClientErrorCodes.Network, message: "تعذر الاتصال", status: 0 });
  });

  it("returns undefined for 204/202 responses", async () => {
    const { fn } = fakeFetch(() => new Response(null, { status: 204 }));
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn });
    await expect(client.delete("/services/1")).resolves.toBeUndefined();
  });

  it("keeps the access token in memory only", async () => {
    const setLocal = vi.spyOn(Storage.prototype, "setItem");
    const { fn } = fakeFetch(() => json(200, { success: true, data: session("B") }));
    const client = new ApiClient({ baseUrl: "http://api.test", fetchImpl: fn });
    await client.refresh();
    expect(client.hasAccessToken).toBe(true);
    expect(setLocal).not.toHaveBeenCalled();
    expect(document.cookie).not.toContain("B");
  });
});
