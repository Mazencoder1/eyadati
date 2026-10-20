import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/client";
import { safeNextPath } from "./auth-card";
import { classifyAcceptError, invitationLoginHref, invitationReturnPath } from "./invitation";

const err = (status: number, code: string, errors?: Record<string, string[]>) =>
  new ApiError({ status, code, message: code, errors });

describe("classifyAcceptError", () => {
  it("existing account without the right session → sign in", () => {
    expect(classifyAcceptError(err(409, "INVITATION_SIGN_IN_REQUIRED"))).toBe("sign-in-required");
  });
  it("used / expired / revoked link → invalid", () => {
    expect(classifyAcceptError(err(400, "INVALID_OR_EXPIRED_TOKEN"))).toBe("invalid-link");
  });
  it("new account accepted without a password → password form", () => {
    expect(classifyAcceptError(err(400, "VALIDATION_FAILED", { newPassword: ["required"] }))).toBe("password-required");
  });
  it("anything else is shown as an error", () => {
    expect(classifyAcceptError(err(400, "VALIDATION_FAILED", { newPassword: ["weak"], token: ["x"] }))).toBe("error");
    expect(classifyAcceptError(err(429, "RATE_LIMITED"))).toBe("error");
    expect(classifyAcceptError(err(409, "CONCURRENCY_CONFLICT"))).toBe("error");
    expect(classifyAcceptError(new Error("boom"))).toBe("error");
  });
});

describe("invitation return path", () => {
  const token = "abc+/=.def&x=1#frag";

  it("keeps the token in the URL only, encoded", () => {
    expect(invitationReturnPath(token)).toBe(`/accept-invitation?token=${encodeURIComponent(token)}`);
  });

  it("login → next round-trips through safeNextPath to the same accept URL and token", () => {
    const login = new URL(invitationLoginHref(token), "https://app.example");
    expect(login.pathname).toBe("/login");
    expect(login.searchParams.get("notice")).toBe("invitation");
    const next = safeNextPath(login.searchParams.get("next"));
    const back = new URL(next, "https://app.example");
    expect(back.pathname).toBe("/accept-invitation");
    expect(back.searchParams.get("token")).toBe(token);
  });
});

describe("safeNextPath (open-redirect guard)", () => {
  it.each([
    [null, "/today"],
    ["", "/today"],
    ["https://evil.example/x", "/today"],
    ["//evil.example", "/today"],
    ["/\\evil.example", "/today"],
    ["/\\/evil.example", "/today"],
    ["/%0d%0aSet-Cookie:x", "/today"], // encoded control characters are rejected (QA-17)
    ["/\tevil", "/today"],
    ["javascript:alert(1)", "/today"],
    ["/patients/1?tab=a#b", "/patients/1?tab=a#b"],
  ])("%s → %s", (input, expected) => {
    expect(safeNextPath(input)).toBe(expected);
  });
});
