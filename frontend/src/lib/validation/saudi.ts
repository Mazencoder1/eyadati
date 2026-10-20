/**
 * Client-side mirrors of the server's Saudi format rules (backend: Domain/Common/SaudiFormats.cs and
 * Application/Common/Validation.cs). They give instant feedback only — the server stays authoritative.
 * Each validator returns a validation code (same codes the API uses) or null when valid.
 */

export type ValidationCode =
  | "required"
  | "too_long"
  | "too_short"
  | "invalid_email"
  | "invalid_saudi_mobile"
  | "invalid_national_id"
  | "invalid_vat_number"
  | "invalid_date"
  | "out_of_range"
  | "invalid_value"
  | "weak_password"
  | "invalid_slug"
  | "passwords_mismatch"
  | "same_password";

/** Converts Arabic-Indic (٠-٩) and Eastern Arabic-Indic (۰-۹) digits to ASCII. */
export function toLatinDigits(input: string): string {
  return input
    .replace(/[٠-٩]/g, (c) => String(c.charCodeAt(0) - 0x0660))
    .replace(/[۰-۹]/g, (c) => String(c.charCodeAt(0) - 0x06f0));
}

/**
 * Normalizes a Saudi mobile to E.164 (+9665XXXXXXXX). Accepts 05XXXXXXXX, 5XXXXXXXX, 9665…, +9665…, 009665…,
 * with spaces/dashes and Arabic digits. Returns null when invalid.
 */
export function normalizeSaudiMobile(input: string | null | undefined): string | null {
  if (!input || !input.trim()) return null;
  let digits = toLatinDigits(input).replace(/\D/g, "");
  if (digits.startsWith("00966")) digits = digits.slice(5);
  else if (digits.startsWith("966")) digits = digits.slice(3);
  else if (digits.startsWith("0")) digits = digits.slice(1);
  return /^5\d{8}$/.test(digits) ? `+966${digits}` : null;
}

export function isValidSaudiMobile(input: string | null | undefined): boolean {
  return normalizeSaudiMobile(input) !== null;
}

/** +9665XXXXXXXX → 05XXXXXXXX for display and editing. */
export function toLocalMobile(e164: string | null | undefined): string {
  if (!e164) return "";
  return e164.startsWith("+966") ? `0${e164.slice(4)}` : e164;
}

/** National ID (starts with 1) or Iqama (starts with 2): 10 digits with the Luhn-style check digit. */
export function isValidNationalIdOrIqama(input: string | null | undefined): boolean {
  if (!input) return false;
  const id = toLatinDigits(input).trim();
  if (!/^[12]\d{9}$/.test(id)) return false;
  let sum = 0;
  for (let i = 0; i < 10; i++) {
    let d = id.charCodeAt(i) - 48;
    if (i % 2 === 0) {
      d *= 2;
      if (d > 9) d -= 9;
    }
    sum += d;
  }
  return sum % 10 === 0;
}

/** ZATCA VAT number: 15 digits, first and last are 3. */
export function isValidVatNumber(input: string | null | undefined): boolean {
  return !!input && /^3\d{13}3$/.test(toLatinDigits(input).trim());
}

/** Loose check (one @, text on both sides, a dot in the domain); the server applies the definitive rule. */
export function isValidEmail(input: string | null | undefined): boolean {
  if (!input) return false;
  const v = input.trim();
  return v.length <= 256 && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v);
}

/** 10–128 characters with at least one letter and one digit (server: StrongPassword). */
export function isStrongPassword(input: string | null | undefined): boolean {
  if (!input) return false;
  return input.length >= 10 && input.length <= 128 && /\p{L}/u.test(input) && /\p{Nd}/u.test(input);
}

export function isValidSlug(input: string): boolean {
  return /^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$/.test(input);
}

// ------------------------------------------------------------------ composable field rules

export type Rule = (value: string) => ValidationCode | null;

export const rules = {
  required: (): Rule => (v) => (v.trim() ? null : "required"),
  maxLength: (max: number): Rule => (v) => (v.trim().length > max ? "too_long" : null),
  minLength: (min: number): Rule => (v) => (v.trim() && v.trim().length < min ? "too_short" : null),
  email: (): Rule => (v) => (!v.trim() || isValidEmail(v) ? null : "invalid_email"),
  saudiMobile: (): Rule => (v) => (!v.trim() || isValidSaudiMobile(v) ? null : "invalid_saudi_mobile"),
  nationalId: (): Rule => (v) => (!v.trim() || isValidNationalIdOrIqama(v) ? null : "invalid_national_id"),
  vat: (): Rule => (v) => (!v.trim() || isValidVatNumber(v) ? null : "invalid_vat_number"),
  password: (): Rule => (v) => (isStrongPassword(v) ? null : "weak_password"),
  slug: (): Rule => (v) => (!v.trim() || isValidSlug(v.trim()) ? null : "invalid_slug"),
  range:
    (min: number, max: number): Rule =>
    (v) => {
      if (!v.trim()) return null;
      const n = Number(toLatinDigits(v));
      return Number.isFinite(n) && n >= min && n <= max ? null : "out_of_range";
    },
};

/** Runs rules in order and returns the first failing code. */
export function check(value: string | null | undefined, ...fieldRules: Rule[]): ValidationCode | null {
  const v = value ?? "";
  for (const rule of fieldRules) {
    const code = rule(v);
    if (code) return code;
  }
  return null;
}
