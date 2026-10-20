import { describe, expect, it } from "vitest";
import { HOME_PATH } from "@/lib/routes";
import { safeNextPath } from "./auth-card";

/**
 * QA re-test of QA-17 after T-015 (docs/security/T-017-retest.md). Property: whatever `next` is, the post-login
 * destination resolves to the app's own origin, as the browser's WHATWG URL parser (`new URL`) would resolve it,
 * and is either the input unchanged or the home path. Inputs are every combination of the separator, control,
 * encoding and scheme tricks below, deterministically.
 */
const APP = "https://app.clinic.example";
const origin = (path: string) => new URL(path, APP).origin;

const prefixes = ["", "/", "//", "/\\", "\\", " ", "\t", "%2F", "/%2F", "/%2f", "/%5C", "/%5c", "/%252F", "/%25252F", "/%09", "/%0a", "/%0D", "/%00"];
const middles = ["", "\t", "\n", "\r", "\u0000", "\u001f", "\u007f", "\\", "/", "%2F", "%5C", "%09", "%252F", "　", "／", "∕"];
const tails = ["/evil.example", "evil.example", "\\evil.example", "/evil.example/today", "@evil.example", ":evil.example", "evil.example:443/x"];
const schemes = ["https://evil.example", "http:evil.example", "javascript:alert(1)", "data:text/html,x", "HTTPS://evil.example", "ftp://evil.example", "/\\/evil.example"];

const inputs = [
  ...schemes,
  ...prefixes.flatMap((p) => middles.flatMap((m) => tails.map((t) => p + m + t))),
];

describe("QA-17 re-test: safeNextPath never leaves the app origin", () => {
  it(`holds for ${inputs.length} generated inputs`, () => {
    const escapes = inputs.filter((value) => {
      const out = safeNextPath(value);
      return origin(out) !== APP || (out !== value && out !== HOME_PATH);
    });
    expect(escapes).toEqual([]);
  });

  it("returns only values that start with a single slash", () => {
    for (const value of inputs) {
      const out = safeNextPath(value);
      expect(out.startsWith("/") && !out.startsWith("//") && !out.startsWith("/\\")).toBe(true);
    }
  });

  it("keeps the app's own deep links (ids, Arabic search, filters, hash)", () => {
    for (const path of ["/today", "/patients/0b5d6f1e-7c1a-4b5e-9d1e-2f6c1a2b3c4d", "/patients?search=%D9%85%D8%AD%D9%85%D8%AF&page=2", "/doctors/abc?tab=schedule#hours"]) {
      expect(safeNextPath(path)).toBe(path);
    }
  });
});
