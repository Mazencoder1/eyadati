import { describe, expect, it } from "vitest";
import { HOME_PATH } from "@/lib/routes";
import { safeNextPath } from "./auth-card";

/**
 * T-014 (QA/Security): the post-login `next` parameter must never lead off the app's origin.
 * The browser resolves the path with the WHATWG URL parser, which also removes ASCII tab/LF/CR anywhere in the input,
 * so "/\t/evil.example" becomes the protocol-relative "//evil.example". `new URL()` below applies the same rules.
 */
const APP = "https://app.clinic.example";
const landsOnApp = (next: string) => new URL(safeNextPath(next), APP).origin === APP;

describe("safeNextPath", () => {
  it.each(["/today", "/patients/1?tab=visits", "/doctors/abc#schedule"])("keeps same-app path %s", (path) => {
    expect(safeNextPath(path)).toBe(path);
  });

  it.each([null, undefined, "", "today", "https://evil.example", "//evil.example", "/\\evil.example", "javascript:alert(1)"])(
    "falls back to home for %s",
    (value) => {
      expect(safeNextPath(value)).toBe(HOME_PATH);
    },
  );

  // QA-17 (fixed in T-015): control characters stripped by the URL parser turned a "relative" path into another origin.
  it.each(["/\t/evil.example", "/\n/evil.example", "/\r/evil.example", "/\t\\evil.example"])(
    "KnownDefect QA-17: %j never leaves the app origin",
    (value) => {
      expect(landsOnApp(value)).toBe(true);
    },
  );

  // T-015 hardening: the same tricks hidden anywhere in the value or behind (double) percent-encoding.
  it.each([
    "/\u0000/evil.example",
    "/x\u001f/evil.example",
    "/x/\\evil.example",
    "/%2F/evil.example",
    "/%2f%2fevil.example",
    "/%5Cevil.example",
    "/%09/evil.example",
    "/%0A/evil.example",
    "/%252F/evil.example",
    "/%25252F%25252Fevil.example",
    "/%E0%A4%A",
    " //evil.example",
    "\t//evil.example",
    "https:/evil.example",
    "/\\/evil.example",
  ])("falls back to home for %j", (value) => {
    expect(safeNextPath(value)).toBe(HOME_PATH);
    expect(landsOnApp(value)).toBe(true);
  });

  it.each(["/patients?search=%D8%A3%D8%AD%D9%85%D8%AF", "/appointments?date=2026-10-06&view=list", "/staff/3f2a?status=Disabled"])(
    "keeps encoded same-app path %s",
    (path) => {
      expect(safeNextPath(path)).toBe(path);
    },
  );
});
