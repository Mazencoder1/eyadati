/**
 * Display helpers for the audit trail. Action codes come from the backend (Domain/Auditing/AuditLog.cs → AuditActions);
 * unknown codes are shown as-is, so a new backend action never breaks the page.
 */

/** Filter categories = action prefixes the API filters on (StartsWith). */
export const AUDIT_CATEGORIES = [
  "auth.",
  "patient.",
  "appointment.",
  "visit.",
  "prescription.",
  "staff.",
  "role.",
  "doctor.",
  "branch.",
  "service.",
  "settings.",
  "subscription.",
  "security.",
] as const;

/** Message key for an action code: next-intl reads "." as nesting, so dots become underscores. */
export const actionMessageKey = (action: string) => action.replace(/\./g, "_");

/** Compact, human-readable form of the small JSON context, e.g. {"fields":["MobileNumber"]} → "fields: MobileNumber". */
export function describeDetails(details: string | null | undefined): string {
  if (!details) return "";
  try {
    const value = JSON.parse(details) as unknown;
    if (value && typeof value === "object" && !Array.isArray(value)) {
      return Object.entries(value as Record<string, unknown>)
        .map(([k, v]) => `${k}: ${Array.isArray(v) ? v.join(", ") : typeof v === "object" && v !== null ? JSON.stringify(v) : String(v)}`)
        .join(" · ");
    }
    return String(value);
  } catch {
    return details;
  }
}
