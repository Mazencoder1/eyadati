import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { readOneTimeToken, useOneTimeToken } from "./one-time-token";

describe("readOneTimeToken", () => {
  it("reads the token from the fragment (the current link format)", () => {
    expect(readOneTimeToken({ hash: "#token=abc-123_DEF", search: "" })).toBe("abc-123_DEF");
  });

  it("decodes percent-escapes the email link applied", () => {
    expect(readOneTimeToken({ hash: "#token=a%2Bb%2Fc%3D", search: "" })).toBe("a+b/c=");
  });

  it("still reads the query form of links emailed before the change", () => {
    expect(readOneTimeToken({ hash: "", search: "?token=legacy-token" })).toBe("legacy-token");
  });

  it("prefers the fragment when both are present", () => {
    expect(readOneTimeToken({ hash: "#token=new", search: "?token=old" })).toBe("new");
  });

  it("returns an empty string when there is no token", () => {
    expect(readOneTimeToken({ hash: "", search: "" })).toBe("");
    expect(readOneTimeToken({ hash: "#other=1", search: "?x=2" })).toBe("");
  });
});

describe("useOneTimeToken", () => {
  afterEach(() => window.history.replaceState(null, "", "/"));

  it("keeps the token in memory and removes it from the address bar", async () => {
    window.history.replaceState(null, "", "/reset-password#token=secret-value");
    const { result } = renderHook(() => useOneTimeToken());

    await waitFor(() => expect(result.current.ready).toBe(true));
    expect(result.current.token).toBe("secret-value");
    expect(window.location.href).not.toContain("secret-value");
    expect(window.location.pathname).toBe("/reset-password");
    expect(window.location.hash).toBe("");
  });

  it("also removes a legacy query-string token", async () => {
    window.history.replaceState(null, "", "/verify-email?token=legacy-secret");
    const { result } = renderHook(() => useOneTimeToken());

    await waitFor(() => expect(result.current.ready).toBe(true));
    expect(result.current.token).toBe("legacy-secret");
    expect(window.location.search).toBe("");
  });

  it("reports ready with an empty token when the link has none", async () => {
    window.history.replaceState(null, "", "/accept-invitation");
    const { result } = renderHook(() => useOneTimeToken());

    await waitFor(() => expect(result.current.ready).toBe(true));
    expect(result.current.token).toBe("");
  });
});
