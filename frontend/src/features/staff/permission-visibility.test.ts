import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { isSupportedPermission, supportedCatalog, UNSUPPORTED_PERMISSIONS } from "./permission-visibility";

describe("supportedCatalog", () => {
  it("hides unsupported permissions and drops groups left empty", () => {
    const catalog = [
      { group: "Patients", permissions: ["Patients.View", "Patients.Export"] },
      { group: "Invoices", permissions: ["Invoices.View", "Invoices.Create", "Invoices.Void", "Invoices.Refund"] },
      { group: "Reports", permissions: ["Reports.View", "Reports.Financial"] },
    ];
    expect(supportedCatalog(catalog)).toEqual([
      { group: "Patients", permissions: ["Patients.View"] },
      { group: "Reports", permissions: ["Reports.View"] },
    ]);
    expect(isSupportedPermission("Files.View")).toBe(true);
  });
});

/** Walks backend .cs sources (no build output). */
function backendSources(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    if (e.isDirectory()) return e.name === "bin" || e.name === "obj" ? [] : backendSources(path.join(dir, e.name));
    return e.name.endsWith(".cs") ? [fs.readFileSync(path.join(dir, e.name), "utf8")] : [];
  });
}

describe("hidden permissions really guard nothing yet", () => {
  it("no API endpoint or service enforces a permission the role editor hides", () => {
    const root = path.resolve(__dirname, "../../../../backend/src");
    const code = [...backendSources(path.join(root, "ClinicSaaS.Api")), ...backendSources(path.join(root, "ClinicSaaS.Application"))].join("\n");
    const catalog = fs.readFileSync(path.join(root, "ClinicSaaS.Domain/Authorization/Permissions.cs"), "utf8");
    const enforced = [...UNSUPPORTED_PERMISSIONS].filter((permission) => {
      const [group] = permission.split(".");
      const constant = catalog.match(new RegExp(`const string (\\w+) = "${permission.replace(".", "\\.")}"`))?.[1];
      expect(constant, `${permission} is in Permissions.cs`).toBeTruthy();
      return new RegExp(`Permissions\\.${group}\\.${constant}\\b|"${permission.replace(".", "\\.")}"`).test(code);
    });
    // A feature that starts using one of these must also make it visible again in the role editor.
    expect(enforced).toEqual([]);
  });
});
