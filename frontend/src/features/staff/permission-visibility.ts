import type { PermissionGroup } from "@/lib/api/types";

/**
 * Permissions that exist in the server catalog but guard no feature yet (no endpoint enforces them — GD-11).
 * The role editor hides them so nobody grants or reads them as if they did something. Hiding is display only:
 * stored roles keep them untouched, and saving a role sends them back unchanged.
 * Not hidden: Reports.View (selects the manager layout of Today) and Files.* (the attachments API exists, D3).
 */
export const UNSUPPORTED_PERMISSIONS: ReadonlySet<string> = new Set([
  "Invoices.View",
  "Invoices.Create",
  "Invoices.Void",
  "Invoices.Refund",
  "Payments.Record",
  "Reports.Financial",
  "Patients.Export",
  "Subscription.Manage",
]);

export const isSupportedPermission = (permission: string) => !UNSUPPORTED_PERMISSIONS.has(permission);

/** The catalog without unsupported permissions; groups left empty are dropped. */
export function supportedCatalog(catalog: PermissionGroup[]): PermissionGroup[] {
  return catalog
    .map((g) => ({ ...g, permissions: g.permissions.filter(isSupportedPermission) }))
    .filter((g) => g.permissions.length > 0);
}
