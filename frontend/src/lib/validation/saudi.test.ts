import { describe, expect, it } from "vitest";
import {
  check,
  isStrongPassword,
  isValidEmail,
  isValidNationalIdOrIqama,
  isValidSaudiMobile,
  isValidVatNumber,
  normalizeSaudiMobile,
  rules,
  toLatinDigits,
  toLocalMobile,
} from "./saudi";

describe("Saudi mobile (mirrors SaudiFormats.NormalizeMobile)", () => {
  it.each([
    ["0501234567", "+966501234567"],
    ["501234567", "+966501234567"],
    ["966501234567", "+966501234567"],
    ["+966501234567", "+966501234567"],
    ["00966501234567", "+966501234567"],
    ["050 123 4567", "+966501234567"],
    ["050-123-4567", "+966501234567"],
    ["٠٥٠١٢٣٤٥٦٧", "+966501234567"],
  ])("normalizes %s", (input, expected) => {
    expect(normalizeSaudiMobile(input)).toBe(expected);
  });

  it.each(["", "   ", "0401234567", "05012345", "05012345678", "+971501234567", "abc"])("rejects %s", (input) => {
    expect(isValidSaudiMobile(input)).toBe(false);
  });

  it("formats E.164 back to the local 05 form", () => {
    expect(toLocalMobile("+966501234567")).toBe("0501234567");
    expect(toLocalMobile(null)).toBe("");
  });
});

describe("National ID / Iqama check digit", () => {
  it.each(["1000000008", "2000000006", "١٠٠٠٠٠٠٠٠٨"])("accepts %s", (id) => {
    expect(isValidNationalIdOrIqama(id)).toBe(true);
  });

  it.each(["1000000001", "3000000004", "100000000", "10000000080", "abcdefghij", ""])("rejects %s", (id) => {
    expect(isValidNationalIdOrIqama(id)).toBe(false);
  });
});

describe("other formats", () => {
  it("validates ZATCA VAT numbers", () => {
    expect(isValidVatNumber("300000000000003")).toBe(true);
    expect(isValidVatNumber("310000000000002")).toBe(false);
    expect(isValidVatNumber("30000000000003")).toBe(false);
  });

  it("validates emails loosely", () => {
    expect(isValidEmail("owner@clinic.sa")).toBe(true);
    expect(isValidEmail("owner@clinic")).toBe(false);
    expect(isValidEmail("no spaces@x.sa")).toBe(false);
  });

  it("applies the password policy (10+ chars, letter + digit, Arabic letters count)", () => {
    expect(isStrongPassword("Clinic2026!")).toBe(true);
    expect(isStrongPassword("عيادة123456")).toBe(true);
    expect(isStrongPassword("short1")).toBe(false);
    expect(isStrongPassword("onlyletterslong")).toBe(false);
    expect(isStrongPassword("1234567890")).toBe(false);
  });

  it("converts Arabic-Indic and Persian digits", () => {
    expect(toLatinDigits("٠١٢٣٤٥٦٧٨٩")).toBe("0123456789");
    expect(toLatinDigits("۰۱۲۳۴۵۶۷۸۹")).toBe("0123456789");
  });
});

describe("rule composition", () => {
  it("returns the first failing code and skips optional empty values", () => {
    expect(check("", rules.required(), rules.saudiMobile())).toBe("required");
    expect(check("123", rules.required(), rules.saudiMobile())).toBe("invalid_saudi_mobile");
    expect(check("", rules.saudiMobile())).toBeNull();
    expect(check("0501234567", rules.required(), rules.saudiMobile())).toBeNull();
    expect(check("300", rules.range(5, 240))).toBe("out_of_range");
    expect(check("٣٠", rules.range(5, 240))).toBeNull();
  });
});
